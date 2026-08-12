using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore.Authentication;
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
///         <b>The pages are built and they live in
///         <c>portal/apps/identity</c></b> — a second Angular app in the portal workspace, served
///         from this origin. <c>src/app/identity-api.ts</c> is the counterparty to every contract in
///         <c>Api/IdentityApiContracts.cs</c>, and the two are kept in step by hand: a name changed
///         on one side and not the other produces a field that is <c>undefined</c> rather than an
///         error, which is why those records pin their JSON names explicitly.
///     </para>
///     <para>
///         <b>What is still owed.</b> The consent page described at the foot of these remarks has no
///         endpoint here, and neither does password reset — <c>SignInService.RequestPasswordResetAsync</c>
///         exists and answers uniformly, but nothing mails the link. ⚠ That sentence used to end
///         "because <c>IOtpDeliverySeam</c> is <c>UnavailableOtpDelivery</c> in every host in this
///         repository", and the real position is emptier: nothing calls
///         <c>IOtpDeliverySeam.DeliverAsync</c> anywhere, and no host registers an implementation of
///         the seam at all — <c>SignInApi.Offered</c>'s remarks set out why for each host. TOTP is
///         mapped and will refuse every code until an <c>ITotpSecretSeam</c> is wired over a vault;
///         recovery codes work today, which is the point of having both.
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

        MapSignIn(app);

        return app;
    }

    /// <summary>
    ///     Maps the interactive sign-in and sign-up endpoints the pages call.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every handler here is three lines, and that is the design.</b> The decisions live
    ///         in <see cref="SignInApi" />, which returns values and can therefore be asserted on
    ///         without a <c>TestServer</c> — see this host's test project, which deliberately has
    ///         none. What is left in a lambda is exactly the part that genuinely needs an
    ///         <c>HttpContext</c>: reading the caller's address and issuing the cookie.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>All of them answer <c>200</c>, including every failure.</b> A <c>401</c> for
    ///         "wrong password" beside a <c>200</c> for "no such account" is the same enumeration
    ///         oracle as a different message, and it is the one a caller reads without even looking
    ///         at the body. <c>UniformFailures.SignIn</c> in a <c>200</c> is what the pages expect
    ///         (<c>portal/apps/identity/src/app/pages/sign-in.ts</c> renders
    ///         <c>result.message</c> from the <c>next</c> branch) and their transport-error branch
    ///         renders the identical string for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Under <c>/api</c>, so the cookie handler answers <c>401</c> rather than
    ///         redirecting.</b> <see cref="IdentityHostAuthentication" />'s <c>OnRedirectToLogin</c>
    ///         keys off that prefix; a script-called endpoint outside it would receive a <c>200</c>
    ///         carrying a sign-in page, which every caller then fails to parse.
    ///     </para>
    /// </remarks>
    static void MapSignIn(IEndpointRouteBuilder app) {
        // ⚠ No grain call, no lookup, no await — see SignInApi.Offered. The address in the body is
        // read and discarded, which is what makes this endpoint safe to expose unauthenticated at
        // volume and impossible to enumerate with.
        app.MapPost("/api/signin/begin", (SignInBeginRequest? request) => Results.Ok(SignInApi.Begin(request)));

        app.MapPost(
            "/api/signin/password",
            async (
                SignInPasswordRequest? request,
                HttpContext context,
                SignInApi api,
                CancellationToken cancellationToken
            ) => await IssueAsync(
                context,
                await api.SignInWithPasswordAsync(request, Describe(context), cancellationToken)
            )
        );

        app.MapPost(
            "/api/signup",
            async (SignUpRequest? request, SignInApi api, CancellationToken cancellationToken) =>
                Results.Ok((await api.SignUpAsync(request, cancellationToken)).Response)
        );

        // ── The passkey pair ───────────────────────────────────────────────────────────────────
        //
        // ⚠ The challenge issued by `begin` goes into a protected cookie and NOT into the response
        // alone. PasskeyChallengeCookie carries the argument: an assertion verified against options
        // the caller supplied is a signature over data the caller chose, which is not an
        // authentication.
        app.MapPost(
            "/api/signin/passkey/begin",
            async (
                PasskeyBeginRequest? request,
                HttpContext context,
                SignInApi api,
                PasskeyChallengeCookie challenges,
                CancellationToken cancellationToken
            ) => {
                var issued = await api.BeginPasskeyAsync(request, cancellationToken);
                if (issued is not { } value) {
                    // The library refused — a misconfigured relying party, not an answer about the
                    // address. The page falls back to its password field.
                    return Results.Ok(new PasskeyBeginResponse(string.Empty));
                }

                challenges.Issue(context, value.Ticket);

                return Results.Ok(value.Response);
            }
        );

        app.MapPost(
            "/api/signin/passkey/complete",
            async (
                PasskeyCompleteRequest? request,
                HttpContext context,
                SignInApi api,
                PasskeyChallengeCookie challenges,
                CancellationToken cancellationToken
            ) => await IssueAsync(
                context,
                await api.CompletePasskeyAsync(
                    request,
                    // ⚠ Taken — read and deleted — before anything can fail, so a challenge is spent
                    // whether or not the assertion verifies. A nonce that survives a failed attempt
                    // is not a nonce.
                    challenges.Take(context),
                    Describe(context),
                    cancellationToken
                )
            )
        );

        // ── The second factor ──────────────────────────────────────────────────────────────────
        //
        // ⚠ Both read WHO is answering from the cookie and never from the body. A user id in a
        // request body would let anybody holding a pending session name somebody else's account.
        // .RequireAuthorization() is what guarantees the principal is there at all — without it the
        // handler would be reasoning about an anonymous identity.
        app.MapPost(
            "/api/signin/totp",
            async (
                SecondFactorRequest? request,
                HttpContext context,
                SignInApi api,
                CancellationToken cancellationToken
            ) => await IssueAsync(context, await api.VerifyTotpAsync(request, context.User, cancellationToken))
        ).RequireAuthorization();

        app.MapPost(
            "/api/signin/recovery-code",
            async (
                SecondFactorRequest? request,
                HttpContext context,
                SignInApi api,
                CancellationToken cancellationToken
            ) => await IssueAsync(
                context,
                await api.RedeemRecoveryCodeAsync(request, context.User, cancellationToken)
            )
        ).RequireAuthorization();
    }

    /// <summary>
    ///     Issues the session cookie when there is one to issue, and returns the body either way.
    /// </summary>
    /// <param name="context">The request being answered.</param>
    /// <param name="result">What <see cref="SignInApi" /> decided.</param>
    /// <remarks>
    ///     ⚠ <c>SignInAsync</c> and not a hand-built <c>Set-Cookie</c>. The cookie handler is what
    ///     applies the options <see cref="IdentityHostAuthentication" /> registered — the
    ///     <c>__Host-</c> name, <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite=Lax</c>, the eight-hour
    ///     sliding expiry — and, more to the point, the data protection that makes the cookie
    ///     unforgeable. A header written here would be a session cookie with none of that.
    /// </remarks>
    static async Task<IResult> IssueAsync(HttpContext context, SignInApiResult result) {
        if (result.Principal is { } principal) {
            await context.SignInAsync(IdentityHostAuthentication.SchemeName, principal);
        }

        return Results.Ok(result.Response);
    }

    /// <summary>
    ///     What the host knows about the request beyond the credential.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <remarks>
    ///     ⚠ The address is passed so <see cref="SignInService" /> can <b>hash</b> it — that type's
    ///     remarks are explicit that it is digested on the way in and never stored or logged. It is
    ///     read here because the host is the only thing that has an <c>HttpContext</c>.
    ///     <para>
    ///         ⚠ <c>RemoteIpAddress</c> and not an <c>X-Forwarded-For</c> header. A caller sets their
    ///         own headers, so trusting one would let an attacker pick which device record their
    ///         session is filed under. Behind a proxy the correct fix is
    ///         <c>UseForwardedHeaders</c> with a configured known-proxy list, which is a deployment
    ///         decision this file must not pre-empt by reading the header directly.
    ///     </para>
    /// </remarks>
    static SignInContext Describe(HttpContext context) =>
        new() {
            ClientId = context.Request.Query["client_id"].ToString(),
            DeviceLabel = context.Request.Headers.UserAgent.ToString(),
            ClientAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty
        };
}
