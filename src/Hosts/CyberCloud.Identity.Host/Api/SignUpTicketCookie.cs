using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

/// <summary>What the browser holds between <c>/api/signup/begin</c> and <c>/api/signup/complete</c>.</summary>
/// <param name="SignupId">The <c>ISignUpGrain</c>'s key — random, minted at <c>begin</c>.</param>
/// <param name="ExpiresAt">When the ticket stops being accepted; <c>SignUpPolicy.Lifetime</c> after <c>begin</c>.</param>
public sealed record SignUpTicket(
    [property: JsonPropertyName("s")]
    Guid SignupId,
    [property: JsonPropertyName("x")]
    DateTimeOffset ExpiresAt
);

/// <summary>
///     The <c>__Host-cyc-signup</c> cookie — how a browser names its own sign-up and nothing else's.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The sign-up id is the whole of the browser's authority over a sign-up, so it is
///             protected and never in a body or a URL.
///         </b> Whoever can name the id can answer its code,
///         enrol a credential on its user and complete it as the owner of its tenant. The id is
///         random (a GUID the host minted, never derived from the address), it travels in a
///         data-protected <c>HttpOnly</c> cookie the page cannot read, and the same
///         <see cref="IDataProtector" /> that makes it unforgeable makes it unreadable — the same
///         arrangement as <see cref="PasskeyChallengeCookie" />, whose remarks carry the argument
///         for a cookie over a server-side store on an unauthenticated path.
///     </para>
///     <para>
///         ⚠ <b>Not consumed on read, unlike the passkey cookie.</b> A passkey challenge is a nonce
///         and is taken once; a sign-up ticket is a session for the four calls of one sign-up and is
///         read by each. It is cleared by <c>complete</c> on success and by expiry, and
///         <see cref="Take" /> refuses an expired one whatever the browser sent.
///     </para>
///     <para>
///         ⚠ <b>The lifetime is <see cref="SignUpPolicy.Lifetime" /> on both sides.</b> The grain
///         forgets the sign-up then; a cookie that outlived it would send a person to a
///         <c>complete</c> that cannot say why it failed.
///     </para>
/// </remarks>
public sealed class SignUpTicketCookie(IDataProtectionProvider protection, IClock clock) {
    /// <summary>
    ///     The cookie's name. <c>__Host-</c> for the same browser-enforced reasons as the session
    ///     cookie — see <see cref="IdentityHostAuthentication.CookieName" />.
    /// </summary>
    public const string CookieName = "__Host-cyc-signup";

    /// <summary>The data-protection purpose. ⚠ Distinct, so a payload cannot be used as another.</summary>
    public const string Purpose = "CyberCloud.Identity.Host.SignUpTicket.v1";

    readonly IDataProtector protector = protection.CreateProtector(Purpose);

    /// <summary>Issues the ticket to the browser.</summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="ticket">What was issued.</param>
    public void Issue(HttpContext context, SignUpTicket ticket) {
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
                MaxAge = SignUpPolicy.Lifetime
            }
        );
    }

    /// <summary>Reads the ticket, leaving it in place.</summary>
    /// <param name="context">The request being answered.</param>
    /// <returns>
    ///     The ticket, or <see langword="null" /> when there is none, it does not unprotect, it does
    ///     not parse, or it has expired — indistinguishably, on purpose.
    /// </returns>
    public SignUpTicket? Take(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var protectedValue = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(protectedValue)) {
            return null;
        }

        SignUpTicket? ticket;
        try {
            ticket = JsonSerializer.Deserialize<SignUpTicket>(protector.Unprotect(protectedValue));
        } catch (System.Security.Cryptography.CryptographicException) {
            return null;
        } catch (JsonException) {
            return null;
        }

        return ticket is null || ticket.ExpiresAt <= clock.UtcNow ? null : ticket;
    }

    /// <summary>Clears the ticket. What <c>complete</c> does on success.</summary>
    /// <param name="context">The request being answered.</param>
    public static void Clear(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(
            CookieName,
            new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" }
        );
    }
}
