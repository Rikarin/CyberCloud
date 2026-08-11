using CyberCloud.Core.Time;
using System.Text.Json;

namespace CyberCloud.Identity.ManagedIdentity;

/// <summary>
///     Reads a tenant cluster's OIDC discovery document and key set over the public internet.
///     docs/plan/11 § Managed identity, step 3.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the component that decides whether a managed identity can exist for a given
///         cluster at all, and it runs at BINDING TIME.</b> docs/plan/11 § Managed identity:
///         <i>"it requires the tenant's cluster to expose a publicly reachable OIDC discovery
///         document, or that we fetch the JWKS through the <c>AgentInitiated</c> tunnel
///         (docs/plan/09). For BYO clusters that is not automatic, and the portal must say so at
///         binding time rather than failing at token exchange."</i>
///     </para>
///     <para>
///         ⚠ <b>The <c>AgentInitiated</c> path is M2 and is not implemented here.</b> A cluster that
///         publishes nothing therefore cannot host a managed identity yet, and the refusal says so in
///         terms of what the tenant has to change — see
///         <see cref="ManagedIdentityFailures.Unreachable" />. When the tunnel lands, the second
///         implementation of <see cref="IClusterOidcDiscovery" /> goes beside this one and the grain
///         does not change.
///     </para>
///     <para>
///         ⚠ <b>HTTPS only, and the reason is not policy.</b> Everything downstream trusts the key
///         set to decide who a workload is. Fetched over plaintext, anybody on the path substitutes
///         their own keys and then mints workload tokens for any service account in the tenant — so
///         an <c>http</c> issuer does not weaken the check, it removes it.
///     </para>
/// </remarks>
public sealed class HttpClusterOidcDiscovery(HttpClient http, IClock clock) : IClusterOidcDiscovery {
    /// <summary>The well-known path OIDC Discovery fixes.</summary>
    public const string DiscoveryPath = "/.well-known/openid-configuration";

    /// <summary>
    ///     The most of a document this will read.
    /// </summary>
    /// <remarks>
    ///     ⚠ A cluster is a party we do not control, and an unbounded read from one is a memory
    ///     exhaustion the tenant can trigger on our silo. A real discovery document is a few hundred
    ///     bytes and a real key set a few kilobytes.
    /// </remarks>
    public const int MaxDocumentBytes = 128 * 1024;

    /// <inheritdoc />
    public async Task<Result<ClusterOidcIssuer>> DiscoverAsync(
        string issuerUrl,
        CancellationToken cancellationToken = default
    ) {
        if (!IsHttpsAbsolute(issuerUrl, out var issuer)) {
            return Unreachable(
                $"'{issuerUrl}' is not an absolute https URL, and a key set fetched over anything "
                + "else could be substituted by anyone on the network path"
            );
        }

        var discovery = new Uri(issuer.GetLeftPart(UriPartial.Path).TrimEnd('/') + DiscoveryPath);

        var document = await ReadJsonAsync(discovery, cancellationToken).ConfigureAwait(false);
        if (document.TryGetError(out var documentError)) {
            return Result<ClusterOidcIssuer>.Failure(documentError);
        }

        using var parsed = document.GetValueOrThrow();

        // ⚠ OIDC Discovery requires the document's own `issuer` to equal the one that was asked
        // about, and the check is load-bearing rather than ceremonial: without it a cluster can
        // publish a document claiming somebody else's issuer, and every token that issuer ever signed
        // would then validate against a key set this cluster chose.
        var claimed = Text(parsed.RootElement, "issuer");
        if (!string.Equals(claimed, issuerUrl, StringComparison.Ordinal)) {
            return Unreachable(
                $"its discovery document claims the issuer '{claimed}', which is not the '{issuerUrl}' "
                + "it was read from — OIDC Discovery requires the two to match"
            );
        }

        var keySetUri = Text(parsed.RootElement, "jwks_uri");
        if (!IsHttpsAbsolute(keySetUri, out var keySet)) {
            return Unreachable("its discovery document names no https 'jwks_uri'");
        }

        // ⚠ Same host as the issuer. OIDC permits a jwks_uri anywhere, and for a Kubernetes API
        // server the two are always the same host — so requiring it costs nothing legitimate and
        // removes "the discovery document can point key fetching at an arbitrary host" from a path
        // that ends in an authentication decision.
        if (!string.Equals(keySet.Host, issuer.Host, StringComparison.OrdinalIgnoreCase)) {
            return Unreachable(
                $"its 'jwks_uri' is served by '{keySet.Host}' rather than by the issuer itself"
            );
        }

        var keys = await ReadJsonAsync(keySet, cancellationToken).ConfigureAwait(false);
        if (keys.TryGetError(out var keysError)) {
            return Result<ClusterOidcIssuer>.Failure(keysError);
        }

        using var keySetJson = keys.GetValueOrThrow();

        if (!keySetJson.RootElement.TryGetProperty("keys", out var entries)
            || entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength() == 0) {
            return Unreachable("its key set contains no keys");
        }

        return Result<ClusterOidcIssuer>.Success(
            new() {
                Issuer = issuerUrl,
                KeySetUri = keySet.ToString(),
                PublicKeySetJson = keySetJson.RootElement.GetRawText(),
                ReadAt = clock.UtcNow
            }
        );
    }

    async Task<Result<JsonDocument>> ReadJsonAsync(Uri uri, CancellationToken cancellationToken) {
        HttpResponseMessage response;

        try {
            response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        } catch (HttpRequestException e) {
            return Unreachable<JsonDocument>($"'{uri}' could not be reached ({e.Message})");
        } catch (TaskCanceledException) {
            // ⚠ Distinguished from a genuine cancellation by the caller's token: an unreachable
            // cluster times out, and a timeout is the single most common shape of "this is a private
            // cluster with no public endpoint" — which is precisely the case docs/plan/11 § Managed
            // identity wants surfaced at binding time.
            cancellationToken.ThrowIfCancellationRequested();
            return Unreachable<JsonDocument>($"'{uri}' did not respond in time");
        }

        using (response) {
            if (!response.IsSuccessStatusCode) {
                return Unreachable<JsonDocument>($"'{uri}' answered {(int)response.StatusCode}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (bytes.Length > MaxDocumentBytes) {
                return Unreachable<JsonDocument>(
                    $"'{uri}' returned more than {MaxDocumentBytes} bytes, which no discovery "
                    + "document or key set legitimately is"
                );
            }

            try {
                return Result<JsonDocument>.Success(JsonDocument.Parse(bytes));
            } catch (JsonException) {
                return Unreachable<JsonDocument>($"'{uri}' did not return JSON");
            }
        }
    }

    static bool IsHttpsAbsolute(string? value, out Uri uri) {
        uri = null!;

        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)) {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) {
            return false;
        }

        uri = parsed;
        return true;
    }

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static Result<ClusterOidcIssuer> Unreachable(string detail) => Unreachable<ClusterOidcIssuer>(detail);

    /// <summary>
    ///     The binding-time refusal, with the specific reason appended.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Verbose on purpose, and the asymmetry with the exchange's refusal is the design.</b>
    ///     The caller here is an authenticated tenant administrator configuring their own cluster —
    ///     the entire reason docs/plan/11 § Managed identity puts the check at binding time is so that
    ///     somebody who can fix the problem is told what it is. The exchange endpoint, whose caller is
    ///     an unauthenticated workload, says <see cref="ManagedIdentityFailures.Exchange" /> and
    ///     nothing else.
    /// </remarks>
    static Result<T> Unreachable<T>(string detail)
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.InvalidRequestBody,
            ManagedIdentityFailures.Unreachable + " Specifically: " + detail + "."
        );
}
