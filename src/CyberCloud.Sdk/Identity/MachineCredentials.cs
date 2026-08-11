using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     The shared half of every credential that redeems a grant at the token endpoint: build the
///     form, post it, turn the answer into an <see cref="AccessToken" />.
/// </summary>
public abstract class TokenEndpointCredential : TokenCredential, IDisposable {
    /// <summary>Creates the credential.</summary>
    /// <param name="options">The options, or <see langword="null" /> for the defaults.</param>
    protected TokenEndpointCredential(CyberCloudCredentialOptions? options) {
        Options = options ?? new CyberCloudCredentialOptions();
        Identity = new IdentityClient(Options.AuthorityHost, Options.Transport);
    }

    /// <summary>The options this credential was created with.</summary>
    protected CyberCloudCredentialOptions Options { get; }

    /// <summary>The client that speaks to the identity server.</summary>
    protected IdentityClient Identity { get; }

    /// <summary>Redeems a grant and turns the response into a token.</summary>
    /// <param name="form">The form fields.</param>
    /// <param name="cancellationToken">The token.</param>
    protected async ValueTask<AccessToken> RedeemAsync(List<KeyValuePair<string, string>> form, CancellationToken cancellationToken) {
        var payload = await Identity.RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);

        return ToAccessToken(payload);
    }

    /// <summary>Turns a token response into a token, computing the expiry from <c>expires_in</c>.</summary>
    /// <param name="payload">The response.</param>
    internal static AccessToken ToAccessToken(TokenPayload payload) {
        if (string.IsNullOrEmpty(payload.AccessToken))
            throw new AuthenticationFailedException("The identity server returned a token response with no access_token.");

        // ⚠ Read against the local clock at the moment of the response, not against an `exp` claim.
        // The SDK does not parse its own access token: docs/plan/11 § Protocol makes it an opaque
        // bearer credential to a client, and a client that reads claims out of it starts depending on
        // them. `expires_in` is the answer the RFC gives for exactly this question.
        return new AccessToken(payload.AccessToken, DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn));
    }

    /// <summary>Adds the scope field when there are scopes to ask for.</summary>
    /// <param name="form">The form.</param>
    /// <param name="context">The request.</param>
    protected static void AddScopes(List<KeyValuePair<string, string>> form, TokenRequestContext context) {
        if (context.Scopes is { Length: > 0 })
            form.Add(new KeyValuePair<string, string>("scope", string.Join(' ', context.Scopes)));
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the identity client.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    protected virtual void Dispose(bool disposing) {
        if (disposing)
            Identity.Dispose();
    }
}

/// <summary>
///     A service principal with a client secret — docs/plan/11 § Protocol's Client Credentials row,
///     and docs/plan/10 § Authentication inputs' "Service principal: client credentials".
/// </summary>
/// <remarks>
///     ⚠ <b>The secret is held in a field and reaches exactly one place: the form of the token
///     request.</b> It is not in <see cref="object.ToString" />, not in any exception this type
///     throws, and <see cref="IdentityClient" /> never reproduces a request body in an error.
///     <c>CredentialsNeverLeakTests</c> asserts that by failing the token endpoint every way it can
///     and searching every resulting message for the secret.
/// </remarks>
public sealed class ClientSecretCredential : TokenEndpointCredential {
    readonly string tenantId;
    readonly string clientId;
    readonly string clientSecret;

    /// <summary>Creates the credential.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="clientId">The application's client id.</param>
    /// <param name="clientSecret">The secret.</param>
    /// <param name="options">The options.</param>
    public ClientSecretCredential(string tenantId, string clientId, string clientSecret, CyberCloudCredentialOptions? options = null)
        : base(options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        this.tenantId = tenantId;
        this.clientId = clientId;
        this.clientSecret = clientSecret;
    }

    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.ClientCredentials),
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("tenant_id", context.TenantId ?? tenantId),
        };

        AddScopes(form, context);

        return RedeemAsync(form, cancellationToken);
    }
}

/// <summary>
///     A service principal with a certificate — docs/plan/11 § Credentials' Certificate row, as
///     <c>private_key_jwt</c> (RFC 7523) rather than mTLS.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The private key never leaves the process and never touches the wire.</b> What is sent
///         is a short-lived assertion signed with it — which is the whole reason a certificate
///         credential is better than a secret: there is nothing on the wire to steal and replay, and
///         nothing in a CI log to find.
///     </para>
///     <para>
///         ⚠ <b>The assertion lives 120 seconds and has a unique <c>jti</c>.</b> Both are replay
///         defences: a captured assertion is useless almost immediately, and a server that tracks
///         <c>jti</c> can refuse the second use of one that is not.
///     </para>
///     <para>
///         docs/plan/11 § Credentials schedules the certificate row for M2 as <i>"mTLS for service
///         principals"</i>. ⚠ This is <c>private_key_jwt</c>, not mTLS, and the difference is worth
///         stating: mTLS requires the TLS terminator — Envoy, in docs/plan/10 § Shape — to forward the
///         client certificate to the identity host, which is a deployment change; an assertion is an
///         ordinary form field and works through any proxy. Reported as a docs/plan/11 point to
///         reconcile.
///     </para>
/// </remarks>
public sealed class CertificateCredential : TokenEndpointCredential {
    readonly string tenantId;
    readonly string clientId;
    readonly X509Certificate2 certificate;

    /// <summary>Creates the credential.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="clientId">The application's client id.</param>
    /// <param name="certificate">The certificate. ⚠ Must carry a private key.</param>
    /// <param name="options">The options.</param>
    public CertificateCredential(string tenantId, string clientId, X509Certificate2 certificate, CyberCloudCredentialOptions? options = null)
        : base(options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
            throw new ArgumentException(
                "The certificate has no private key, so no client assertion can be signed with it.",
                nameof(certificate));

        this.tenantId = tenantId;
        this.clientId = clientId;
        this.certificate = certificate;
    }

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        var document = await Identity.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.ClientCredentials),
            new("client_id", clientId),
            new("client_assertion_type", OAuthGrants.JwtBearerAssertion),
            new("client_assertion", CreateAssertion(document.TokenEndpoint)),
            new("tenant_id", context.TenantId ?? tenantId),
        };

        AddScopes(form, context);

        return await RedeemAsync(form, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the signed client assertion.
    /// </summary>
    /// <param name="audience">
    ///     The token endpoint. ⚠ It is the <c>aud</c>, not the authority: RFC 7523 § 3 makes the
    ///     assertion valid only at the endpoint named in it, so an assertion captured by a compromised
    ///     proxy cannot be replayed against a different endpoint of the same server.
    /// </param>
    internal string CreateAssertion(string audience) {
        var now = DateTimeOffset.UtcNow;
        var algorithm = certificate.GetRSAPrivateKey() is not null ? "RS256" : "ES256";

        // x5t#S256 is the SHA-256 thumbprint. It tells the server which registered certificate signed
        // this without the assertion carrying the certificate itself.
        var thumbprint = Base64Url.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256));

        var header = $$"""{"alg":"{{algorithm}}","typ":"JWT","x5t#S256":"{{thumbprint}}"}""";

        var payload = $$"""
            {"iss":"{{clientId}}","sub":"{{clientId}}","aud":{{JsonEncodedAudience(audience)}},"jti":"{{Guid.NewGuid():D}}","nbf":{{now.ToUnixTimeSeconds()}},"exp":{{now.AddSeconds(120).ToUnixTimeSeconds()}}}
            """;

        var signingInput = $"{Base64Url.Encode(Encoding.UTF8.GetBytes(header))}.{Base64Url.Encode(Encoding.UTF8.GetBytes(payload))}";
        var signature = Sign(Encoding.ASCII.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }

    static string JsonEncodedAudience(string audience) => JsonSerializer.Serialize(audience, SdkJsonContext.Default.String);

    byte[] Sign(byte[] input) {
        using var rsa = certificate.GetRSAPrivateKey();

        if (rsa is not null)
            return rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var ecdsa = certificate.GetECDsaPrivateKey();

        if (ecdsa is not null)
            return ecdsa.SignData(input, HashAlgorithmName.SHA256);

        throw new AuthenticationFailedException(
            "The certificate's private key is neither RSA nor ECDSA, and no other signature algorithm is registered.");
    }
}

/// <summary>
///     A workload in a tenant cluster, exchanging its projected service-account token for a platform
///     token — docs/plan/11 § Managed identity, steps 4 and 5, and docs/plan/10 § Authentication
///     inputs' last row.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing is stored, on either side, and that is the point of the feature.</b>
///         docs/plan/11: <i>"No secret is ever stored, on either side. … it removes an entire incident
///         class."</i> So this credential caches nothing across processes: the projected token is
///         re-read from the file on every request, because Kubernetes rotates it in place and a
///         credential that read it once would start failing an hour into a pod's life.
///     </para>
/// </remarks>
public sealed class WorkloadIdentityCredential : TokenEndpointCredential {
    /// <summary>The environment variable naming the projected service-account token file.</summary>
    public const string TokenFileVariable = "CYC_FEDERATED_TOKEN_FILE";

    /// <summary>The environment variable naming the managed identity's client id.</summary>
    public const string ClientIdVariable = "CYC_CLIENT_ID";

    /// <summary>The environment variable naming the tenant.</summary>
    public const string TenantIdVariable = "CYC_TENANT_ID";

    readonly string tokenFilePath;
    readonly string clientId;
    readonly string tenantId;

    /// <summary>Creates the credential from the environment a bound workload runs in.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="CredentialUnavailableException">The environment does not describe a bound workload.</exception>
    public WorkloadIdentityCredential(CyberCloudCredentialOptions? options = null)
        : this(
            Environment.GetEnvironmentVariable(TokenFileVariable),
            Environment.GetEnvironmentVariable(ClientIdVariable),
            Environment.GetEnvironmentVariable(TenantIdVariable) ?? options?.TenantId,
            options) { }

    /// <summary>Creates the credential explicitly.</summary>
    /// <param name="tokenFilePath">The projected token's path.</param>
    /// <param name="clientId">The managed identity's client id.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="options">The options.</param>
    public WorkloadIdentityCredential(string? tokenFilePath, string? clientId, string? tenantId, CyberCloudCredentialOptions? options = null)
        : base(options) {
        // ⚠ Unavailable, not failed: this credential is normally one link of DefaultCyberCloudCredential's
        // chain and "I am not running in a bound pod" must fall through to the next link rather than
        // stopping the chain. See CredentialUnavailableException's remarks.
        if (string.IsNullOrEmpty(tokenFilePath) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId))
            throw new CredentialUnavailableException(
                $"Workload identity needs {TokenFileVariable}, {ClientIdVariable} and {TenantIdVariable}. "
                + "They are projected into a pod bound to a managed identity — docs/plan/11 § Managed identity.");

        this.tokenFilePath = tokenFilePath;
        this.clientId = clientId;
        this.tenantId = tenantId;
    }

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        string subjectToken;

        try {
            subjectToken = (await File.ReadAllTextAsync(tokenFilePath, cancellationToken).ConfigureAwait(false)).Trim();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new CredentialUnavailableException(
                $"The projected service-account token at '{tokenFilePath}' could not be read.", e);
        }

        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.TokenExchange),
            new("client_id", clientId),
            new("tenant_id", context.TenantId ?? tenantId),
            new("subject_token", subjectToken),
            new("subject_token_type", OAuthGrants.JwtTokenType),
            new("requested_token_type", OAuthGrants.AccessTokenType),
        };

        AddScopes(form, context);

        return await RedeemAsync(form, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>base64url, RFC 4648 § 5 — JOSE's encoding, and there is no BCL helper that spells it.</summary>
static class Base64Url {
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text) {
        var padded = text.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
