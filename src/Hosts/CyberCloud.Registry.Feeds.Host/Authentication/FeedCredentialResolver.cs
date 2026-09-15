using CyberCloud.Identity.Validation;
using System.Text;

namespace CyberCloud.Registry.Feeds.Host.Authentication;

/// <summary>
///     Finds the platform token in a package client's request — three places, one validator.
///     docs/plan/13 § Artifact feeds: "three protocols with three auth schemes".
/// </summary>
/// <remarks>
///     <para>
///         <b>Where each client puts the token, and why this host reads all three:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <c>npm</c> sends <c>Authorization: Bearer {token}</c> from an <c>_authToken</c> line
///             in <c>.npmrc</c>. The gateway's shape exactly.
///         </item>
///         <item>
///             <c>mvn deploy</c> and <c>dotnet restore</c> send <c>Authorization: Basic</c> from a
///             <c>settings.xml</c> server or a <c>nuget.config</c> credential — a username and a
///             password. The password is the platform token; the username is whatever the tenant
///             wrote and is ignored, because the token names its subject and a username that
///             disagreed with it would be a second, unverified claim about who is calling.
///         </item>
///         <item>
///             <c>dotnet nuget push</c> sends <c>X-NuGet-ApiKey: {token}</c> and nothing else — the
///             NuGet client has no other way to carry a push credential.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Three ways in, and the same validation after every one.</b> Nothing about which
///         header carried the bytes changes what the bytes have to be: a JWT the identity host
///         signed, with the audience, the issuer, the <c>tid</c> and the <c>sub_typ</c> the shared
///         validator requires. The gateway's stage 2 reads one header and says why reading more would
///         widen its surface; this host reads three because its clients leave it no choice, and
///         narrows the surface back to one by handing all three to <see cref="IBearerTokenValidator" />.
///     </para>
///     <para>
///         ⚠ <b>Precedence, when more than one is present: <c>Authorization</c> wins.</b> A request
///         carrying both is a misconfigured client, and the standard header is the one to believe;
///         the API-key header is read only when there is no <c>Authorization</c> at all.
///     </para>
/// </remarks>
/// <param name="validator">The shared validator — the JWKS one in production.</param>
public sealed class FeedCredentialResolver(IBearerTokenValidator validator) {
    /// <summary>The header <c>dotnet nuget push</c> carries its credential in.</summary>
    public const string NuGetApiKeyHeader = "X-NuGet-ApiKey";

    /// <summary>What a <c>401</c> from this host tells the client to send next.</summary>
    /// <remarks>
    ///     Both schemes, because the clients differ: NuGet and Maven answer a <c>Basic</c> challenge
    ///     by retrying with their configured credential, and <c>npm</c> sends <c>Bearer</c> without
    ///     being asked.
    /// </remarks>
    public const string Challenge = "Basic realm=\"cybercloud-feeds\", Bearer realm=\"cybercloud-feeds\"";

    /// <summary>Reads the credential out of the request and validates it.</summary>
    /// <param name="http">The request.</param>
    /// <param name="cancellationToken">Cancels the validation.</param>
    /// <returns>The claims, or <see cref="ErrorCode.AuthorizationFailed" /> phrased for the client.</returns>
    public Task<Result<TokenClaims>> ResolveAsync(HttpContext http, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(http);

        var authorization = http.Request.Headers.Authorization.ToString();

        if (authorization.Length > 0) {
            if (authorization.StartsWith("Bearer ", StringComparison.Ordinal)) {
                return validator.ValidateAsync(authorization["Bearer ".Length..].Trim(), http, cancellationToken);
            }

            if (authorization.StartsWith("Basic ", StringComparison.Ordinal)) {
                return BasicPassword(authorization["Basic ".Length..].Trim()) is { } password
                    ? validator.ValidateAsync(password, http, cancellationToken)
                    : Task.FromResult(
                        Result<TokenClaims>.Failure(
                            BearerTokenErrors.Unauthenticated("the Basic credential is not base64 user:password")
                        )
                    );
            }

            return Task.FromResult(
                Result<TokenClaims>.Failure(
                    BearerTokenErrors.Unauthenticated("the Authorization header is neither Bearer nor Basic")
                )
            );
        }

        var apiKey = http.Request.Headers[NuGetApiKeyHeader].ToString();

        if (apiKey.Length > 0) {
            return validator.ValidateAsync(apiKey.Trim(), http, cancellationToken);
        }

        return Task.FromResult(
            Result<TokenClaims>.Failure(
                BearerTokenErrors.Unauthenticated("no Authorization header and no X-NuGet-ApiKey header")
            )
        );
    }

    /// <summary>The password half of a <c>Basic</c> credential, or <see langword="null" /> when it is not one.</summary>
    /// <param name="encoded">The base64 after the scheme.</param>
    internal static string? BasicPassword(string encoded) {
        byte[] bytes;

        try {
            bytes = Convert.FromBase64String(encoded);
        } catch (FormatException) {
            return null;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var colon = decoded.IndexOf(':', StringComparison.Ordinal);

        // A token carries no colon, so the first one is the separator whatever the username was.
        return colon < 0 ? null : decoded[(colon + 1)..];
    }
}
