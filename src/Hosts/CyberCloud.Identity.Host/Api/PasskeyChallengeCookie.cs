using CyberCloud.Core.Time;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     What was issued to the browser when a passkey assertion began.
/// </summary>
/// <param name="OptionsJson">The WebAuthn request options, exactly as the library produced them.</param>
/// <param name="Email">
///     The normalized address the challenge was issued for. ⚠ Carried so completion cannot be
///     answered on behalf of a different account than the one <c>begin</c> was called with.
/// </param>
/// <param name="ExpiresAt">When it stops being accepted.</param>
public sealed record PasskeyChallengeTicket(
    [property: JsonPropertyName("o")] string OptionsJson,
    [property: JsonPropertyName("e")] string Email,
    [property: JsonPropertyName("x")] DateTimeOffset ExpiresAt
);

/// <summary>
///     Where a passkey challenge lives between <c>begin</c> and <c>complete</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE CHALLENGE MUST NOT COME BACK FROM THE CALLER, AND THIS TYPE IS WHY IT DOES
///         NOT.</b> <c>Fido2PasskeyService</c> keeps no server-side state on purpose — its remarks
///         make the case, and the case is sound for <i>registration</i>, where the user is already
///         authenticated and the worst a forged challenge buys is enrolling a passkey onto your own
///         account. It does not carry over to <b>assertion</b>, which is the sign-in itself: an
///         endpoint that accepted <c>OriginalOptions</c> from the request body would let an attacker
///         generate a challenge, sign it with any authenticator they hold, and post both. The library
///         would verify the signature correctly and the sign-in would succeed, because nothing in
///         that exchange was ever chosen by this server.
///     </para>
///     <para>
///         The fix is that the options the assertion is verified against are the ones <i>this
///         process</i> issued. They ride in a cookie rather than in a grain or a cache because a
///         challenge is a nonce belonging to one browser and one attempt:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>No grain activation on an unauthenticated path.</b> docs/plan/11 § Credentials:
///             "an authentication endpoint whose failure path costs a grain activation is a
///             denial-of-service amplifier." A server-side challenge store keyed by an
///             attacker-supplied address is exactly that amplifier, with unbounded state attached.
///         </item>
///         <item>
///             <b>Nothing to expire.</b> The lifetime is inside the protected payload, so an
///             abandoned attempt costs nothing and there is no sweep to get wrong.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Protected, not merely signed, and the distinction is not decorative.</b>
///         <see cref="IDataProtector" /> encrypts and authenticates, so the caller can neither read
///         which credential ids were offered — which would enumerate the account's authenticators —
///         nor edit the expiry.
///     </para>
///     <para>
///         ⚠ <b>The key ring is a deployment requirement.</b> Data-protection keys default to the
///         local file system, so two replicas cannot read each other's challenges and a sign-in that
///         load-balances between <c>begin</c> and <c>complete</c> fails. That is already true of the
///         session cookie itself, so the fix is one shared key ring for the host rather than
///         something this type can arrange — but it fails <i>here</i> first, because this cookie's
///         round trip is seconds rather than hours.
///     </para>
/// </remarks>
public sealed class PasskeyChallengeCookie(IDataProtectionProvider protection, IClock clock) {
    /// <summary>
    ///     The cookie's name. <c>__Host-</c> for the same browser-enforced reasons as the session
    ///     cookie — see <see cref="IdentityHostAuthentication.CookieName" />.
    /// </summary>
    public const string CookieName = "__Host-cyc-passkey";

    /// <summary>The data-protection purpose. ⚠ Distinct, so a payload cannot be used as another.</summary>
    public const string Purpose = "CyberCloud.Identity.Host.PasskeyAssertion.v1";

    readonly IDataProtector protector = protection.CreateProtector(Purpose);

    /// <summary>
    ///     Issues the challenge to the browser.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="ticket">What was issued.</param>
    public void Issue(HttpContext context, PasskeyChallengeTicket ticket) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ticket);

        context.Response.Cookies.Append(
            CookieName,
            protector.Protect(JsonSerializer.Serialize(ticket)),
            new() {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
                // Belt to the braces: the browser drops it, and Take() checks the expiry inside the
                // protected payload regardless, because the browser's copy is the caller's to keep.
                Expires = ticket.ExpiresAt
            }
        );
    }

    /// <summary>
    ///     Reads the challenge and consumes it.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <returns>
    ///     The ticket, or <see langword="null" /> when there is none, it does not unprotect, it does
    ///     not parse, or it has expired.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The cookie is deleted whatever the answer is</b>, including on every failure path. A
    ///     challenge that survived a failed attempt would be a nonce that is not once — an attacker
    ///     who obtained one assertion could retry it, and a user whose authenticator produced a bad
    ///     signature would keep replaying the same challenge until it expired.
    /// </remarks>
    public PasskeyChallengeTicket? Take(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var protectedValue = context.Request.Cookies[CookieName];

        context.Response.Cookies.Delete(
            CookieName,
            new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" }
        );

        if (string.IsNullOrEmpty(protectedValue)) {
            return null;
        }

        PasskeyChallengeTicket? ticket;
        try {
            ticket = JsonSerializer.Deserialize<PasskeyChallengeTicket>(protector.Unprotect(protectedValue));
        } catch (System.Security.Cryptography.CryptographicException) {
            // A tampered, truncated, or foreign-key-ring value. Indistinguishable from "no cookie",
            // deliberately — the caller learns nothing about which it was.
            return null;
        } catch (JsonException) {
            return null;
        }

        return ticket is null || ticket.ExpiresAt <= clock.UtcNow ? null : ticket;
    }
}
