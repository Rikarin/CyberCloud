using System.Security.Cryptography;

namespace CyberCloud.Sdk;

/// <summary>
///     Redeems a refresh token and stores the one that comes back, under the rules a rotating,
///     one-time-use refresh token with server-side reuse detection forces.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the most dangerous code in the SDK, and the danger is not obvious.</b>
///         docs/plan/11 § Protocol: refresh tokens are <i>"Rotating, one-time-use, with reuse
///         detection → revoke the whole chain"</i>, and § Sessions and revocation confirms it —
///         refresh-reuse detection <i>"invalidates the refresh chain immediately"</i>. So the ordinary
///         reflex of a networked client, "the request failed, try it again", takes a user who suffered
///         one dropped packet and signs them out of every device they own.
///     </para>
///     <para>Three rules follow, and each one is here because the naive version is harmful:</para>
///     <list type="number">
///         <item>
///             <b>A refresh is never retried.</b> Not by the HTTP layer —
///             <see cref="IdentityClient" /> installs no retry handler at all — and not here.
///         </item>
///         <item>
///             <b>An ambiguous failure poisons the entry.</b> If the request left and no answer came
///             back, the server may have rotated the token and we did not hear. Redeeming the old one
///             later is precisely the reuse the server is watching for, so
///             <see cref="TokenCacheRecord.Poisoned" /> is set and the entry is never redeemed again.
///             The user re-authenticates once; the alternative is that they are signed out everywhere
///             and cannot tell why.
///         </item>
///         <item>
///             <b>An <c>invalid_grant</c> clears the entry.</b> The chain is already gone — either
///             expired, revoked, or detected as reused. Keeping it would mean asking again on every
///             call and being refused every time.
///         </item>
///     </list>
///     <para>
///         The single-flight guarantee that stops two threads spending the same one-time token lives
///         one level up, in <see cref="AccessTokenCache" />.
///     </para>
/// </remarks>
static class RefreshTokenExchange {
    public static async ValueTask<AccessToken> RedeemAsync(
        IdentityClient identity,
        ITokenCache cache,
        string cacheKey,
        TokenCacheRecord record,
        string clientId,
        TokenRequestContext context,
        CancellationToken cancellationToken) {
        if (record.Poisoned || string.IsNullOrEmpty(record.RefreshToken))
            throw new CredentialUnavailableException(
                "The cached sign-in cannot be refreshed and a new sign-in is required.");

        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.RefreshToken),
            new("refresh_token", record.RefreshToken),
            new("client_id", clientId),
        };

        if (context.Scopes is { Length: > 0 })
            form.Add(new KeyValuePair<string, string>("scope", string.Join(' ', context.Scopes)));

        TokenPayload payload;

        try {
            payload = await identity.RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
        } catch (AuthenticationFailedException e) when (string.Equals(e.ErrorCode, "invalid_grant", StringComparison.Ordinal)) {
            // Rule 3. The server answered, and the answer is that this chain is finished.
            await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);

            throw new CredentialUnavailableException("The cached sign-in has expired or been revoked. Sign in again.", e);
        } catch (AuthenticationFailedException e) when (e.ErrorCode is null) {
            // Rule 2. No OAuth error code means the server never answered — a transport failure, a
            // timeout, a body we could not parse. We do not know whether the token was spent.
            await cache.SetAsync(cacheKey, record with { Poisoned = true }, cancellationToken).ConfigureAwait(false);

            throw new CredentialUnavailableException(
                "A token refresh ended without an answer, so the cached sign-in can no longer be used safely. "
                + "Sign in again — retrying it would look like refresh-token reuse and would revoke the whole session.",
                e);
        }

        // Write the new refresh token before returning the access token. A caller that gets a usable
        // token and a cache that still holds the spent one is the same ambiguity as rule 2, arrived at
        // through carelessness instead of through a network.
        await cache.SetAsync(
                cacheKey,
                new TokenCacheRecord {
                    RefreshToken = payload.RefreshToken ?? record.RefreshToken,
                    AccessToken = payload.AccessToken,
                    ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
                    Authority = record.Authority,
                    ClientId = clientId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TokenEndpointCredential.ToAccessToken(payload);
    }
}

/// <summary>
///     PKCE — RFC 7636. A fresh verifier per authorization, and only <c>S256</c>.
/// </summary>
/// <remarks>
///     ⚠ <c>plain</c> is not implemented and will not be. It is PKCE with the protection removed: the
///     challenge and the verifier are the same string, so an attacker who intercepted the redirect has
///     both. docs/plan/11 § Protocol allows one interactive flow and OAuth 2.1 requires PKCE on it.
/// </remarks>
readonly struct Pkce {
    Pkce(string verifier, string challenge) {
        Verifier = verifier;
        Challenge = challenge;
    }

    /// <summary>The verifier, sent only when the code is redeemed.</summary>
    public string Verifier { get; }

    /// <summary>The challenge, sent in the authorization request.</summary>
    public string Challenge { get; }

    /// <summary>The method — always <c>S256</c>.</summary>
    public static string Method => "S256";

    public static Pkce Create() {
        // 32 bytes → 43 base64url characters, the length RFC 7636 § 4.1 recommends.
        var verifier = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new Pkce(verifier, challenge);
    }
}
