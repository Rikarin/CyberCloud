namespace CyberCloud.Sdk;

/// <summary>
///     An access token and when it stops being usable.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="RefreshAfter" /> is not <see cref="ExpiresOn" /> and the difference matters at
///     ten minutes.</b> docs/plan/11 § Protocol makes access tokens ten minutes long. Waiting until
///     expiry to refresh means every request that lands on the boundary blocks on a token request;
///     refreshing early means none of them ever does. A credential that knows a better moment — one
///     holding a refresh token, say — says so here; otherwise
///     <see cref="AccessTokenCache.RefreshWindow" /> picks one.
/// </remarks>
public readonly struct AccessToken : IEquatable<AccessToken> {
    /// <summary>Creates a token.</summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="expiresOn">When it stops being accepted.</param>
    /// <param name="refreshAfter">When to start trying to replace it, or <see langword="null" />.</param>
    public AccessToken(string token, DateTimeOffset expiresOn, DateTimeOffset? refreshAfter = null) {
        Token = token;
        ExpiresOn = expiresOn;
        RefreshAfter = refreshAfter;
    }

    /// <summary>The bearer token. ⚠ Never log this, never put it in an exception message.</summary>
    public string Token { get; }

    /// <summary>When the token stops being accepted.</summary>
    public DateTimeOffset ExpiresOn { get; }

    /// <summary>When to start replacing it, if the credential has an opinion.</summary>
    public DateTimeOffset? RefreshAfter { get; }

    /// <inheritdoc />
    public bool Equals(AccessToken other)
        => string.Equals(Token, other.Token, StringComparison.Ordinal) && ExpiresOn == other.ExpiresOn;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AccessToken other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Token, ExpiresOn);

    /// <summary>Compares two tokens.</summary>
    public static bool operator ==(AccessToken left, AccessToken right) => left.Equals(right);

    /// <summary>Compares two tokens.</summary>
    public static bool operator !=(AccessToken left, AccessToken right) => !left.Equals(right);

    /// <summary>
    ///     ⚠ Deliberately does <b>not</b> include the token. <see cref="object.ToString" /> is what
    ///     ends up in a log line somebody added in a hurry, and this is the cheapest place to make
    ///     that harmless.
    /// </summary>
    public override string ToString() => $"AccessToken(expires {ExpiresOn:O})";
}

/// <summary>What a caller is asking a credential for.</summary>
public readonly struct TokenRequestContext {
    /// <summary>Creates a request context.</summary>
    /// <param name="scopes">The scopes.</param>
    /// <param name="tenantId">
    ///     The tenant to authenticate against, overriding the credential's own. docs/plan/11
    ///     § Sign-up and tenant creation makes a user belong to exactly one tenant, so this is how the
    ///     portal's account switcher asks for the other one's token.
    /// </param>
    /// <param name="claims">Additional claims the service demanded — a step-up challenge (docs/plan/11 § Protocol's <c>amr</c>).</param>
    public TokenRequestContext(string[] scopes, string? tenantId = null, string? claims = null) {
        Scopes = scopes;
        TenantId = tenantId;
        Claims = claims;
    }

    /// <summary>The scopes.</summary>
    public string[] Scopes { get; }

    /// <summary>The tenant, or <see langword="null" /> for the credential's own.</summary>
    public string? TenantId { get; }

    /// <summary>A claims challenge, or <see langword="null" />.</summary>
    public string? Claims { get; }
}

/// <summary>
///     The credential abstraction — docs/plan/10 § Authentication inputs, the SDK row:
///     <i>"<c>TokenCredential</c> — the Azure SDK shape, so the mental model transfers"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The shape transfers; the implementations do not, and docs/plan/21 § The .NET SDK is
///         now explicit about the difference.</b> <c>DefaultAzureCredential</c> and its family
///         authenticate against Entra. They will never produce a token this platform accepts. What a
///         developer carries over is the pattern — one abstract async call returning a token and an
///         expiry, chainable, injectable — and that is worth having on its own.
///     </para>
///     <para>
///         ⚠ <b>Async only, and there is no synchronous twin.</b> Every credential here performs I/O:
///         an HTTP round trip, a keychain read, a subprocess. A synchronous entry point over that is
///         a deadlock waiting for a caller with a synchronization context, which is why CC1001 exists
///         (docs/plan/00 § Non-negotiables) — and owning this abstraction rather than inheriting
///         Azure's means we are not obliged to declare one. Callers that must block do it at their own
///         boundary, where they can see the context they are blocking on.
///     </para>
/// </remarks>
public abstract class TokenCredential {
    /// <summary>Gets a token.</summary>
    /// <param name="context">What is being asked for.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <exception cref="CredentialUnavailableException">
    ///     This credential cannot be used in this environment at all — no CLI installed, no federated
    ///     token file, no environment variables. ⚠ Distinguished from a failure because
    ///     <see cref="ChainedTokenCredential" /> moves to the next credential on this and stops on
    ///     anything else.
    /// </exception>
    /// <exception cref="AuthenticationFailedException">The credential is applicable and authentication failed.</exception>
    public abstract ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default);
}
