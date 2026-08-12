namespace CyberCloud.Vault;

/// <summary>
///     One OpenBao token and when it stops being usable.
/// </summary>
/// <remarks>
///     ⚠ <b>A class rather than a record struct, and the identity is what makes
///     <see cref="IVaultTokenSource.Invalidate" /> safe.</b> The resolver hands back the exact
///     instance it was given so the source can tell "throw away the token I am holding" from "throw
///     away a token somebody else already replaced". Two concurrent resolves both hitting a
///     <c>403</c> would otherwise re-login twice, and the second would discard the first's fresh
///     token.
/// </remarks>
/// <param name="Value">The <c>X-Vault-Token</c> header value. ⚠ Never logged, never persisted.</param>
/// <param name="ExpiresAt">
///     When this stops being usable, already reduced by <see cref="VaultOptions.TokenExpirySkew" />.
///     A source with no lease information reports <see cref="DateTimeOffset.MaxValue" />.
/// </param>
public sealed record VaultToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
///     Hands out the token the platform reads OpenBao with.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam, and the reason is testability of everything <i>except</i> the seam.</b>
///         <see cref="KubernetesVaultTokenSource" /> is the only implementation a deployment should
///         use, and it cannot be exercised without a Kubernetes API server — OpenBao's Kubernetes
///         auth backend calls the cluster's <c>TokenReview</c> API on <b>every</b> login, and
///         configuring <c>pem_keys</c> for local signature checking does not skip it (its
///         <c>pathLogin</c> calls <c>serviceAccount.lookup</c> unconditionally, after
///         <c>parseAndValidateJWT</c> returns). Verified against OpenBao's source, not inferred from
///         the documentation, and it is the reason this interface exists: with it, every other
///         behaviour in this assembly is testable against a real OpenBao container.
///     </para>
///     <para>
///         ⚠ <b>Registering an implementation of this in a production host is a way to put a static
///         token into the platform, and there is no other.</b> <see cref="VaultOptions" /> has no
///         token member precisely so that the shortcut has to be written in code by somebody who
///         meant it. If a review ever finds one, that is what it found.
///     </para>
/// </remarks>
public interface IVaultTokenSource {
    /// <summary>Returns a usable token, logging in if the one it holds is gone or nearly expired.</summary>
    /// <param name="cancellationToken">Cancels the login.</param>
    /// <returns>The token, or the reason the platform cannot authenticate. ⚠ Never an empty token.</returns>
    ValueTask<Result<VaultToken>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Throws away a token that OpenBao has stopped accepting, so the next
    ///     <see cref="GetAsync" /> logs in again.
    /// </summary>
    /// <param name="stale">
    ///     The token that was refused. Ignored unless it is the one currently cached — see
    ///     <see cref="VaultToken" /> for why identity matters.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>This exists for revocation, which the expiry skew cannot cover.</b> An operator
    ///     responding to a suspected compromise revokes the platform's token; OpenBao then answers
    ///     <c>403</c> to a token whose lease says it is good for another month. Without this, every
    ///     provision on the silo fails until that lease runs out — OpenBao's default token TTL is
    ///     long enough for that to mean "until somebody restarts the silo".
    /// </remarks>
    void Invalidate(VaultToken stale);
}
