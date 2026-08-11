using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>The OAuth grant type URIs and form field names the identity server speaks.</summary>
/// <remarks>
///     docs/plan/11 § Protocol's flow table, one constant per row, plus the two RFC 7523 assertion
///     types. ⚠ Resource Owner Password is absent and stays absent — that table strikes it through:
///     <i>"Removed in OAuth 2.1 and it defeats MFA"</i>.
/// </remarks>
public static class OAuthGrants {
    /// <summary>Authorization Code + PKCE — the only interactive flow docs/plan/11 § Protocol allows.</summary>
    public const string AuthorizationCode = "authorization_code";

    /// <summary>Client Credentials — service principals and CI.</summary>
    public const string ClientCredentials = "client_credentials";

    /// <summary>Device Authorization — <c>cyc login</c> on a headless box.</summary>
    public const string DeviceCode = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Refresh Token — rotating, one-time-use, with reuse detection.</summary>
    public const string RefreshToken = "refresh_token";

    /// <summary>Token Exchange (RFC 8693) — a workload's SA token for a platform token.</summary>
    public const string TokenExchange = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>The client-assertion type for a certificate credential (RFC 7523).</summary>
    public const string JwtBearerAssertion = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The subject-token type a Kubernetes projected service-account token is presented as.</summary>
    public const string JwtTokenType = "urn:ietf:params:oauth:token-type:jwt";

    /// <summary>The token type a workload asks for in exchange.</summary>
    public const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
}

/// <summary>Where the identity server lives.</summary>
/// <remarks>
///     ⚠ docs/plan/11 § Hosts puts <c>/authorize</c>, <c>/token</c> and <c>/.well-known/*</c> on
///     <c>CyberCloud.Identity.Host</c>, on <b>a different origin from the gateway</b> — <i>"Separate
///     hosts on separate origins makes that structural instead of a middleware configuration somebody
///     will change"</i>. No document names the host, so the constant below is a placeholder and the
///     <c>CYC_AUTHORITY_HOST</c> environment variable overrides it. ⚠ Reported as a docs/plan/11 gap.
/// </remarks>
public static class CyberCloudAuthorityHosts {
    /// <summary>The environment variable that overrides the default.</summary>
    public const string EnvironmentVariable = "CYC_AUTHORITY_HOST";

    /// <summary>The public identity host, or whatever <c>CYC_AUTHORITY_HOST</c> names.</summary>
    public static Uri Default { get; } =
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured
        && Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri
            : new Uri("https://login.cybercloud.io/");
}

/// <summary>
///     Speaks OAuth to the identity server: discovery, every grant in docs/plan/11 § Protocol's table,
///     and the JWKS fetch.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here hard-codes a path.</b> Every endpoint comes from the discovery document,
///         which is the contract this SDK is built against while
///         <c>CyberCloud.Identity.Host</c> does not exist. See EmitterContract.cs § "What the SDK
///         needs from the identity server" for the exact list, which is what a reviewer should
///         reconcile against the identity module's own statement.
///     </para>
///     <para>
///         ⚠ <b>The transport here is separate from the resource client's, on purpose.</b> The
///         resource pipeline's outermost handler attaches a bearer token; a token request that went
///         through it would try to authenticate the request that exists to produce the credential.
///     </para>
/// </remarks>
public sealed class IdentityClient : IDisposable {
    readonly HttpClient http;
    readonly SemaphoreSlim discoveryGate = new(1, 1);

    OpenIdConfiguration? configuration;

    /// <summary>Creates a client for one authority.</summary>
    /// <param name="authorityHost">The identity host.</param>
    /// <param name="transport">A transport to use instead of the default — the test seam.</param>
    public IdentityClient(Uri authorityHost, HttpMessageHandler? transport = null) {
        ArgumentNullException.ThrowIfNull(authorityHost);

        AuthorityHost = authorityHost;
        http = transport is null
            ? new HttpClient()
            : new HttpClient(transport, disposeHandler: false);
    }

    /// <summary>The identity host this client talks to.</summary>
    public Uri AuthorityHost { get; }

    /// <summary>
    ///     Reads and caches the discovery document.
    /// </summary>
    /// <remarks>
    ///     Cached for the client's lifetime: endpoints do not move, and re-reading them on every token
    ///     request would double the round trips of a credential that is already on the hot path of a
    ///     ten-minute token. ⚠ The <b>keys</b> are a different matter and are not cached this way —
    ///     see <see cref="SigningKeyCache" />.
    /// </remarks>
    public async ValueTask<OpenIdConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) {
        if (configuration is { } cached)
            return cached;

        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            if (configuration is { } raced)
                return raced;

            var uri = new Uri(AuthorityHost, ".well-known/openid-configuration");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var content = await SendAsync(request, "discovery document", cancellationToken).ConfigureAwait(false);

            var document = SdkJsonContext.Read(content, SdkJsonContext.Default.OpenIdConfiguration, "discovery document");

            if (string.IsNullOrEmpty(document.TokenEndpoint))
                throw new AuthenticationFailedException(
                    $"The discovery document at {uri} names no token_endpoint, so no grant can be redeemed.");

            configuration = document;

            return document;
        } finally {
            discoveryGate.Release();
        }
    }

    /// <summary>Redeems a grant at the token endpoint.</summary>
    /// <param name="form">The form fields. ⚠ Never reproduced in any exception — it carries the secret.</param>
    /// <param name="cancellationToken">The token.</param>
    public async ValueTask<TokenPayload> RequestTokenAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken) {
        var document = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, document.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        var content = await SendAsync(request, "token response", cancellationToken).ConfigureAwait(false);

        return SdkJsonContext.Read(content, SdkJsonContext.Default.TokenPayload, "token response");
    }

    /// <summary>Starts a device authorization — RFC 8628 § 3.1.</summary>
    /// <param name="form">The form fields.</param>
    /// <param name="cancellationToken">The token.</param>
    public async ValueTask<DeviceAuthorizationPayload> RequestDeviceCodeAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken) {
        var document = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(document.DeviceAuthorizationEndpoint))
            throw new CredentialUnavailableException(
                $"{AuthorityHost} advertises no device_authorization_endpoint, so the device code flow is not available here.");

        using var request = new HttpRequestMessage(HttpMethod.Post, document.DeviceAuthorizationEndpoint) {
            Content = new FormUrlEncodedContent(form),
        };

        var content = await SendAsync(request, "device authorization response", cancellationToken).ConfigureAwait(false);

        return SdkJsonContext.Read(content, SdkJsonContext.Default.DeviceAuthorizationPayload, "device authorization response");
    }

    /// <summary>Fetches the signing key set.</summary>
    /// <param name="cancellationToken">The token.</param>
    public async ValueTask<JsonWebKeySet> RequestKeysAsync(CancellationToken cancellationToken) {
        var document = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(document.JwksUri))
            throw new AuthenticationFailedException($"The discovery document at {AuthorityHost} names no jwks_uri.");

        using var request = new HttpRequestMessage(HttpMethod.Get, document.JwksUri);
        var content = await SendAsync(request, "key set", cancellationToken).ConfigureAwait(false);

        return SdkJsonContext.Read(content, SdkJsonContext.Default.JsonWebKeySet, "key set");
    }

    /// <summary>
    ///     Sends a request and returns the body, translating a non-success status into an
    ///     <see cref="AuthenticationFailedException" /> carrying the OAuth error code.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No retry, on any request, deliberately.</b> A token request is not idempotent: a
    ///     refresh grant spends a one-time token (docs/plan/11 § Sessions and revocation) and a device
    ///     poll advances a rate limiter. The retry that helps a <c>GET</c> of a resource is the retry
    ///     that revokes a user's session here, so the resilience lives at the caller —
    ///     <see cref="RefreshTokenExchange" /> — where it can be about the specific grant.
    /// </remarks>
    async ValueTask<ReadOnlyMemory<byte>> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken) {
        HttpResponseMessage message;

        try {
            message = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        } catch (HttpRequestException e) {
            // ⚠ The URL is named; the request body is not. It carries the client secret.
            throw new AuthenticationFailedException($"The identity server at {AuthorityHost} could not be reached.", e);
        }

        using (message) {
            ReadOnlyMemory<byte> body = await message.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (message.IsSuccessStatusCode)
                return body;

            throw CreateFailure(message.StatusCode, body, what);
        }
    }

    internal static AuthenticationFailedException CreateFailure(HttpStatusCode status, ReadOnlyMemory<byte> body, string what) {
        TokenErrorPayload? error = null;

        try {
            error = JsonSerializer.Deserialize(body.Span, SdkJsonContext.Default.TokenErrorPayload);
        } catch (JsonException) {
            // A body that is not the RFC 6749 error shape leaves the status code, which is still
            // actionable. Replacing it with a parse failure would not be.
        }

        var description = error?.ErrorDescription is { Length: > 0 } text ? $" — {text}" : string.Empty;

        return new AuthenticationFailedException(
            $"The identity server rejected the request for a {what}: {(int)status} {error?.Error ?? status.ToString()}{description}") {
            ErrorCode = error?.Error,
        };
    }

    /// <inheritdoc />
    public void Dispose() {
        http.Dispose();
        discoveryGate.Dispose();
    }
}
