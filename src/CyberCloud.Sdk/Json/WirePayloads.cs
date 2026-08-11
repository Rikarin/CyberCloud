using System.Text.Json.Serialization;

namespace CyberCloud.Sdk;

/// <summary>
///     The OIDC discovery document — <c>/.well-known/openid-configuration</c> on
///     <c>CyberCloud.Identity.Host</c> (docs/plan/11 § Hosts).
/// </summary>
/// <remarks>
///     ⚠ <b>Discovery is the contract, not a convenience.</b> The identity host does not exist yet;
///     building against its discovery document means the SDK never hard-codes a path, so the day it
///     ships the only thing that has to match is a JSON document both sides can read. Everything the
///     SDK needs is a member here, and § "What the SDK needs from the identity server" in
///     EmitterContract.cs states the list.
/// </remarks>
public sealed class OpenIdConfiguration {
    /// <summary>The issuer. Must equal the <c>iss</c> of every token, and the authority it was fetched from.</summary>
    [JsonPropertyName("issuer")]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>The authorization endpoint — authorization code + PKCE.</summary>
    [JsonPropertyName("authorization_endpoint")]
    public string? AuthorizationEndpoint { get; init; }

    /// <summary>The token endpoint — every grant redeems here.</summary>
    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; init; } = string.Empty;

    /// <summary>The device authorization endpoint — <c>cyc login</c> on a headless box (docs/plan/11 § Protocol).</summary>
    [JsonPropertyName("device_authorization_endpoint")]
    public string? DeviceAuthorizationEndpoint { get; init; }

    /// <summary>The JWKS URL. See <see cref="SigningKeyCache" /> for why it is re-read rather than cached forever.</summary>
    [JsonPropertyName("jwks_uri")]
    public string? JwksUri { get; init; }

    /// <summary>The userinfo endpoint.</summary>
    [JsonPropertyName("userinfo_endpoint")]
    public string? UserInfoEndpoint { get; init; }

    /// <summary>The grants the server advertises. Checked before a credential attempts one.</summary>
    [JsonPropertyName("grant_types_supported")]
    public IReadOnlyList<string>? GrantTypesSupported { get; init; }

    /// <summary>
    ///     The PKCE challenge methods. ⚠ The SDK sends <c>S256</c> and nothing else — OAuth 2.1 and
    ///     docs/plan/11 § Protocol allow no implicit and no hybrid, and <c>plain</c> is PKCE with the
    ///     protection removed.
    /// </summary>
    [JsonPropertyName("code_challenge_methods_supported")]
    public IReadOnlyList<string>? CodeChallengeMethodsSupported { get; init; }
}

/// <summary>A successful token response — RFC 6749 § 5.1.</summary>
public sealed class TokenPayload {
    /// <summary>The access token.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>Always <c>Bearer</c> on this platform.</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    /// <summary>Lifetime in seconds. docs/plan/11 § Protocol makes it 600.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    /// <summary>
    ///     The refresh token. ⚠ One-time use with rotation and reuse detection — docs/plan/11
    ///     § Sessions and revocation. See <see cref="RefreshTokenExchange" /> for what that forces.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>The id token, on the interactive grants.</summary>
    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }

    /// <summary>The scopes actually granted, which may be narrower than those asked for.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>An error response from the token or device endpoint — RFC 6749 § 5.2.</summary>
public sealed class TokenErrorPayload {
    /// <summary>The code — <c>invalid_grant</c>, <c>authorization_pending</c>, <c>slow_down</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>A human-readable description. Reproduced in the exception; it contains no secret.</summary>
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    /// <summary>A URL with more detail.</summary>
    [JsonPropertyName("error_uri")]
    public string? ErrorUri { get; init; }
}

/// <summary>A device authorization response — RFC 8628 § 3.2.</summary>
public sealed class DeviceAuthorizationPayload {
    /// <summary>The code the client polls with. ⚠ A secret; never shown to the user.</summary>
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = string.Empty;

    /// <summary>The short code the user types.</summary>
    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = string.Empty;

    /// <summary>Where the user goes to type it.</summary>
    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = string.Empty;

    /// <summary>The same URL with the code already in it, when the server offers one.</summary>
    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; init; }

    /// <summary>How long the user has, in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    /// <summary>The minimum poll interval, in seconds. Increased by five on every <c>slow_down</c>.</summary>
    [JsonPropertyName("interval")]
    public int Interval { get; init; }
}

/// <summary>A JSON Web Key Set — RFC 7517.</summary>
public sealed class JsonWebKeySet {
    /// <summary>The keys.</summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<JsonWebKey> Keys { get; init; } = [];
}

/// <summary>One JSON Web Key. Only the members the SDK verifies id tokens with are modelled.</summary>
public sealed class JsonWebKey {
    /// <summary>Key type — <c>RSA</c> or <c>EC</c>.</summary>
    [JsonPropertyName("kty")]
    public string Kty { get; init; } = string.Empty;

    /// <summary>Key id, matched against a token header's <c>kid</c>.</summary>
    [JsonPropertyName("kid")]
    public string? Kid { get; init; }

    /// <summary>Intended use — <c>sig</c>.</summary>
    [JsonPropertyName("use")]
    public string? Use { get; init; }

    /// <summary>Algorithm — <c>RS256</c>, <c>ES256</c>.</summary>
    [JsonPropertyName("alg")]
    public string? Alg { get; init; }

    /// <summary>RSA modulus, base64url.</summary>
    [JsonPropertyName("n")]
    public string? N { get; init; }

    /// <summary>RSA exponent, base64url.</summary>
    [JsonPropertyName("e")]
    public string? E { get; init; }

    /// <summary>EC curve — <c>P-256</c>.</summary>
    [JsonPropertyName("crv")]
    public string? Crv { get; init; }

    /// <summary>EC x coordinate, base64url.</summary>
    [JsonPropertyName("x")]
    public string? X { get; init; }

    /// <summary>EC y coordinate, base64url.</summary>
    [JsonPropertyName("y")]
    public string? Y { get; init; }
}

/// <summary>
///     What <c>cyc account get-access-token --output json</c> prints. The contract between the CLI
///     and <see cref="CyberCloudCliCredential" />.
/// </summary>
public sealed class CliTokenPayload {
    /// <summary>The access token.</summary>
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>When it expires, ISO 8601.</summary>
    [JsonPropertyName("expiresOn")]
    public DateTimeOffset ExpiresOn { get; init; }
}

/// <summary>
///     One entry of the persistent token cache. ⚠ Written to the OS keychain and nowhere else —
///     docs/plan/21 § `cyc`: <i>"Never a plaintext file — that is how CI credentials leak into
///     container images."</i>
/// </summary>
public sealed record TokenCacheRecord {
    /// <summary>The refresh token, if the grant produced one.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    /// <summary>The access token, if it is still worth keeping.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    /// <summary>When the access token expires.</summary>
    [JsonPropertyName("expiresOn")]
    public DateTimeOffset ExpiresOn { get; init; }

    /// <summary>The authority the tokens came from — a cache entry is not portable between them.</summary>
    [JsonPropertyName("authority")]
    public string? Authority { get; init; }

    /// <summary>The client id the tokens were issued to.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    /// <summary>
    ///     ⚠ Set when a refresh ended ambiguously — the request left but no answer came back. The
    ///     stored refresh token may or may not have been spent, and retrying it is what trips
    ///     docs/plan/11 § Sessions and revocation's reuse detection and revokes the whole chain. A
    ///     poisoned entry is never redeemed; the user signs in again. See
    ///     <see cref="RefreshTokenExchange" />.
    /// </summary>
    [JsonPropertyName("poisoned")]
    public bool Poisoned { get; init; }
}
