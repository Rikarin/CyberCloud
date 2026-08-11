using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CyberCloud.Identity.Host;

/// <summary>
///     The interactive endpoints this host serves, and what the sign-in and sign-up pages need from
///     them. docs/plan/11 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THERE ARE NO PAGES HERE, AND THAT IS THE SCOPE LINE.</b> docs/plan/11 § Effort puts
///         "sign-up/in/reset/consent pages (Angular + xUI, SSR, on the identity host)" at 0.8 EM as a
///         separate piece, and ADR-017 makes every component xUI's. Building server-rendered pages
///         here would either duplicate the design system or invent a second one. What this file is,
///         instead, is the endpoint surface those pages call plus — in the remarks on each — what the
///         page has to do, so whoever builds them is not reverse-engineering the protocol.
///     </para>
///     <para>
///         <b>What the sign-in page needs, end to end:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Ask for the address first, and only then offer credentials.</b>
///             <c>POST /api/signin/begin</c> returns the offered credential kinds <i>in
///             <see cref="CredentialKind" /> order</i>, which puts a passkey first — docs/plan/11
///             § Credentials makes it the default rather than an upsell. ⚠ It returns the same list
///             shape for an address with no account, because otherwise the page enumerates on the
///             platform's behalf.
///         </item>
///         <item>
///             <b>For a passkey</b>, post the address, take
///             <see cref="PasskeyAssertionChallenge.OptionsJson" /> straight to
///             <c>navigator.credentials.get()</c> without touching it, and post the result back
///             verbatim. ⚠ The page must not parse or rebuild the options; the challenge binding is
///             the library's and reserializing it breaks it.
///         </item>
///         <item>
///             <b>For a password</b>, post it to <c>/api/signin/password</c>. The response is
///             <see cref="UniformFailures.SignIn" /> for every failure, and the page must render that
///             string as it arrives — a page that says "no account with that address" undoes the
///             hardening the endpoint pays for.
///         </item>
///         <item>
///             <b>When <see cref="SignInOutcome.SecondFactorRequired" /> is set</b>, collect a TOTP
///             code or a recovery code and post it before treating the session as usable.
///         </item>
///         <item>
///             <b>Then resume the OIDC request</b> by re-issuing the original
///             <c>GET /authorize</c> with its query string intact. The cookie set by the sign-in is
///             what makes the second attempt succeed.
///         </item>
///     </list>
///     <para>
///         <b>What the sign-up page needs:</b> offer a passkey first and a password second, on the
///         same screen, with the passkey as the primary action. The response to
///         <c>POST /api/signup</c> is <see cref="UniformFailures.SignUp" /> whether or not the
///         address was free — the mail that follows is what differs, and it goes to the address
///         either way.
///     </para>
///     <para>
///         <b>What the consent page needs:</b> the client's display name, the scopes requested, and
///         nothing else. ⚠ It must render the registered display name from
///         <see cref="ApplicationRegistration.DisplayName" /> and never a value from the
///         authorization request's query string, which is attacker-controlled.
///     </para>
/// </remarks>
public static class IdentityEndpoints {
    /// <summary>
    ///     Maps the endpoints.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <remarks>
    ///     ⚠ The OIDC endpoints themselves — <c>/authorize</c>, <c>/token</c>, <c>/userinfo</c>,
    ///     <c>/device</c>, <c>/logout</c> and <c>/.well-known/*</c> — are OpenIddict's and are not
    ///     mapped here; <see cref="IdentityHostOpenIddict" /> configures them, and the passthrough
    ///     options let the handlers below take over where a decision needs our grains.
    ///     <para>
    ///         The <c>/api</c> prefix is what
    ///         <see cref="IdentityHostAuthentication" />'s <c>OnRedirectToLogin</c> keys off to answer
    ///         <c>401</c> instead of a redirect, so an endpoint that is called by script belongs under
    ///         it and one that is navigated to does not.
    ///     </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app) {
        ArgumentNullException.ThrowIfNull(app);

        // ⚠ A liveness probe and nothing else. docs/plan/11 § Hosts gives this host the OIDC surface;
        // everything that is not OIDC belongs at the gateway, on the bearer origin. An endpoint here
        // that read tenant data would be a control-plane read authenticated by a cookie, which is the
        // exact thing the two-host split exists to prevent.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        // The token-validation contract, published so the gateway does not hard-code it and so a
        // change to AccessTokenPolicy is visible to an operator without reading source.
        app.MapGet(
            "/.well-known/cybercloud-token-policy",
            () => Results.Ok(
                new {
                    accessTokenLifetimeSeconds = (int)AccessTokenPolicy.AccessTokenLifetime.TotalSeconds,
                    signingAlgorithm = AccessTokenPolicy.SigningAlgorithm,
                    jwksPath = AccessTokenPolicy.JsonWebKeySetPath,
                    discoveryPath = AccessTokenPolicy.DiscoveryPath,
                    supportsIntrospection = AccessTokenPolicy.SupportsIntrospection,
                    accessTokensAreRevocable = AccessTokenPolicy.AccessTokensAreRevocable,
                    // ⚠ Published so a reviewer can see it from outside: these claim names are the
                    // ones a Cyber Cloud token must never carry, and the gateway should treat a token
                    // carrying one as suspect rather than as a token with extra claims.
                    forbiddenClaims = AccessTokenPolicy.SupportsIntrospection
                        ? Array.Empty<string>()
                        : [.. AccessTokenClaims.ForbiddenClaims]
                }
            )
        );

        return app;
    }
}
