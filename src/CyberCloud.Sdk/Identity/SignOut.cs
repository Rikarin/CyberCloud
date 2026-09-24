namespace CyberCloud.Sdk;

/// <summary>What <see cref="CyberCloudSignOut.SignOutAsync" /> did.</summary>
/// <param name="HadSignIn">Whether the cache held a sign-in for this authority and client.</param>
/// <param name="Revoked">Whether the identity server accepted the revocation of its refresh token.</param>
/// <param name="Detail">Why the revocation did not happen, when it did not. Never contains token material.</param>
public sealed record SignOutResult(bool HadSignIn, bool Revoked, string Detail);

/// <summary>
///     Signs a cached interactive sign-in out: revokes its refresh token at the identity server, then
///     forgets it. What <c>cyc logout</c> runs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Revoke first, forget second, and forget whatever the server said.</b> Forgetting alone
///         — which is all <c>cyc logout</c> did before #43 — leaves a refresh token that works for
///         fourteen days in whatever copy of it exists elsewhere: a backup of the keychain, a file
///         copied off the box. Revoking at <c>/revoke</c> ends the session behind it on the server,
///         so every copy dies at once. And the entry is removed even when the server could not be
///         reached, because a person who ran <c>logout</c> on a laptop about to lose its network
///         must not find themselves still signed in on it; <see cref="SignOutResult.Detail" /> says
///         the revocation is owed, so the caller can say so.
///     </para>
///     <para>
///         ⚠ <b>No retry, for <see cref="IdentityClient" />'s reason.</b> A revocation that timed out
///         may have happened; the token is forgotten either way, and trying again later is
///         harmless only because revocation is idempotent — which is the one grant-shaped call on
///         this client that is.
///     </para>
/// </remarks>
public static class CyberCloudSignOut {
    /// <summary>Revokes and forgets the cached sign-in.</summary>
    /// <param name="options">The authority, the transport and the cache the sign-in was made with.</param>
    /// <param name="clientId">The client it was made as — <see cref="CyberCloudCliCredential.CliClientId" /> for <c>cyc</c>.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async ValueTask<SignOutResult> SignOutAsync(
        CyberCloudCredentialOptions options,
        string clientId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        var key = TokenCache.KeyFor(options.AuthorityHost, clientId, options.TenantId);
        var cached = await options.TokenCache.GetAsync(key, cancellationToken).ConfigureAwait(false);

        if (cached is not { RefreshToken.Length: > 0 } record) {
            await options.TokenCache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

            return new(false, false, "No sign-in was cached.");
        }

        var revoked = false;
        var detail = string.Empty;

        // ⚠ A poisoned entry is still revoked: the token may or may not have been spent, and a
        // revocation of a spent one is answered as done — RFC 7009 § 2.2 — so there is nothing to lose.
        try {
            using var identity = new IdentityClient(options.AuthorityHost, options.Transport);
            await identity.RevokeAsync(record.RefreshToken!, clientId, cancellationToken).ConfigureAwait(false);
            revoked = true;
        } catch (AuthenticationFailedException e) {
            // CredentialUnavailableException among them — a server that advertises no endpoint.
            detail = e.Message;
        } finally {
            await options.TokenCache.RemoveAsync(key, CancellationToken.None).ConfigureAwait(false);
        }

        return new(true, revoked, detail);
    }
}
