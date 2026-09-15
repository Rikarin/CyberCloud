using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.Http;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     Where the portal's refresh token lives: an <c>HttpOnly</c> cookie on this origin, never the
///     response body. docs/plan/10 § Authentication inputs' Portal row, docs/plan/11 § Sessions
///     and revocation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The cookie is the transport and the grain is the credential.</b> What rides in here
///         is OpenIddict's encrypted refresh token, whose principal carries the
///         <c>ISessionGrain</c> handle (<c>cyc:rh</c>); one-time use, rotation and reuse detection
///         are <c>ISessionGrain.RefreshAsync</c>'s, and revocation is the session's. A body-borne
///         token would put a fourteen-day credential in page memory and cost a full redirect on
///         every reload; <c>AccessTokenStore</c> in the portal is written on the assumption that it
///         cannot read this cookie, which is the property this type keeps.
///     </para>
///     <para>
///         ⚠ <b>Every attribute is load-bearing.</b> <c>__Host-</c> makes the browser refuse the
///         cookie unless it is <c>Secure</c>, has <c>Path=/</c> and carries no <c>Domain</c>, which
///         closes injection from a tenant subdomain — the same argument as
///         <c>IdentityHostAuthentication.CookieName</c>. <c>SameSite=Lax</c> is what lets the
///         portal's cross-origin, same-site <c>fetch</c> to <c>/token</c> carry it
///         (<c>localhost:4200</c> → <c>localhost:5101</c>, <c>portal.</c> → <c>id.cybercloud.io</c>)
///         and what withholds it from a cross-site <c>POST</c>; the same-site subdomain
///         <c>POST</c> that Lax does not stop is closed by the <c>Origin</c> check in
///         <c>DegradedModeHandlers.ExtractRefreshTokenFromCookie</c>. <c>Max-Age</c> is the refresh
///         lifetime, so the browser and the grain retire it together.
///         <c>RefreshCookieTests</c> holds the string verbatim.
///     </para>
///     <para>
///         ⚠ Dev caveat: cookies ignore ports, so on <c>localhost</c> this is also sent to the
///         gateway on 5100 — which ignores cookies by construction — and Chromium and Firefox accept
///         a <c>Secure</c> cookie on <c>http://localhost</c>.
///     </para>
/// </remarks>
public static class RefreshCookie {
    /// <summary>The cookie's name.</summary>
    public const string Name = "__Host-cyc-refresh";

    /// <summary>How long the browser keeps it — the refresh token's own lifetime.</summary>
    public static TimeSpan MaxAge => AccessTokenPolicy.RefreshTokenLifetime;

    /// <summary>Writes the refresh token to the browser, replacing any earlier one.</summary>
    /// <param name="response">The response being written.</param>
    /// <param name="refreshToken">The encrypted refresh token, verbatim.</param>
    public static void Issue(HttpResponse response, string refreshToken) {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        response.Cookies.Append(Name, refreshToken, Options(MaxAge));
    }

    /// <summary>
    ///     Tells the browser to forget the cookie — after a failed refresh, and on sign-out.
    /// </summary>
    /// <param name="response">The response being written.</param>
    /// <remarks>
    ///     ⚠ <c>Max-Age=0</c> with the same attributes, not a bare delete: a browser matches a
    ///     deletion to the cookie it holds by name, path and the <c>Secure</c> flag, and a
    ///     <c>Set-Cookie</c> that differs in any of them creates a second cookie beside the one it
    ///     meant to remove.
    /// </remarks>
    public static void Clear(HttpResponse response) {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, string.Empty, Options(TimeSpan.Zero));
    }

    /// <summary>The refresh token the request carried in the cookie, or <see langword="null" />.</summary>
    /// <param name="request">The request.</param>
    public static string? Read(HttpRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    static CookieOptions Options(TimeSpan maxAge) =>
        new() {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            MaxAge = maxAge
        };
}
