using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Identity.Host;

/// <summary>
///     The identity host's authentication: <b>one cookie scheme, and nothing else</b>.
///     docs/plan/11 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE SECURITY BOUNDARY, AND ITS CONTENT IS MOSTLY WHAT IS ABSENT.</b>
///         docs/plan/11 § Hosts: "a session cookie must never be a credential the resource API
///         accepts. If it is, every CSRF becomes a control-plane write." The two halves are enforced
///         differently and both matter:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A bearer token is not accepted here</b> — no JWT bearer handler and no OpenIddict
///             validation handler is registered, and the package that provides the latter is not even
///             referenced by this project. See the comment block in the <c>.csproj</c>.
///         </item>
///         <item>
///             <b>A cookie is not accepted at the gateway</b> — which is the gateway's own
///             registration to get right, and is why <see cref="AccessTokenPolicy" /> exists as a
///             shared, asserted contract rather than as prose in a document.
///         </item>
///     </list>
///     <para>
///         ⚠ <b><see cref="SchemeName" /> is not
///         <see cref="CookieAuthenticationDefaults.AuthenticationScheme" />.</b> The default is
///         <c>"Cookies"</c>, which several libraries also use as their default — including some that
///         register a second cookie handler on the side. Naming it explicitly means a second
///         registration is a duplicate-scheme exception at start-up rather than a silent override of
///         the one that carries the session.
///     </para>
/// </remarks>
public static class IdentityHostAuthentication {
    /// <summary>The one authentication scheme this host has.</summary>
    public const string SchemeName = "CyberCloud.Identity.Session";

    /// <summary>The cookie's name. Prefixed <c>__Host-</c>, and that prefix is load-bearing.</summary>
    /// <remarks>
    ///     ⚠ <c>__Host-</c> is enforced by the browser, not by us: a cookie with this prefix is
    ///     rejected unless it is <c>Secure</c>, has <c>Path=/</c>, and carries <b>no</b>
    ///     <c>Domain</c> attribute. The last one is the point — no <c>Domain</c> means no subdomain
    ///     can set it, so a compromised or attacker-controlled host under the same registrable domain
    ///     cannot inject a session cookie for the identity origin. That is a real attack against a
    ///     platform that hands tenants subdomains, which this one does.
    /// </remarks>
    public const string CookieName = "__Host-cyc-session";

    /// <summary>
    ///     Registers the session cookie, and nothing else.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <remarks>
    ///     ⚠ Every option below is set explicitly even where it matches the framework default,
    ///     because "the default is fine" stops being true when a framework changes its default and
    ///     nobody notices. <c>SameSite=Lax</c> rather than <c>Strict</c> is the one real trade: the
    ///     OIDC authorization-code flow returns to this origin via a cross-site redirect, and
    ///     <c>Strict</c> would drop the session cookie on that navigation, so the user would arrive
    ///     signed in and be asked to sign in. <c>Lax</c> withholds the cookie from cross-site
    ///     <c>POST</c>s, which is the CSRF case that matters.
    /// </remarks>
    public static IServiceCollection AddIdentityHostAuthentication(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddAuthentication(SchemeName)
            .AddCookie(
                SchemeName,
                options => {
                    options.Cookie.Name = CookieName;
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.Path = "/";
                    options.Cookie.IsEssential = true;

                    // The cookie is the *interactive* session, not the API credential — it exists so
                    // the user does not re-authenticate for every authorization request. Its lifetime
                    // is the browser session's; the API's lifetime is AccessTokenPolicy's ten
                    // minutes, and the two numbers answer different questions.
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;

                    // ⚠ 302-to-a-login-page is wrong for everything but a browser navigation, and
                    // this host serves JSON endpoints too. A redirect on an XHR turns a 401 into a
                    // 200 with a login page in it, which every client then fails to parse.
                    options.Events.OnRedirectToLogin = context => {
                        if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)) {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        }

                        context.Response.Redirect(context.RedirectUri);
                        return Task.CompletedTask;
                    };
                }
            );

        services.AddAuthorization();

        return services;
    }
}
