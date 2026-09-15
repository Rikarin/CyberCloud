using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Orleans.Multitenant;

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
///         <b>
///             The pages are built and they live in
///             <c>portal/apps/identity</c>
///         </b> — a second Angular app in the portal workspace, served
///         from this origin. <c>src/app/identity-api.ts</c> is the counterparty to every contract in
///         <c>Api/IdentityApiContracts.cs</c>, and the two are kept in step by hand: a name changed
///         on one side and not the other produces a field that is <c>undefined</c> rather than an
///         error, which is why those records pin their JSON names explicitly.
///     </para>
///     <para>
///         <b>What is still owed.</b> The consent page described at the foot of these remarks has no
///         endpoint here (a tenant-registered client is answered <c>consent_required</c> at
///         <c>/authorize</c> until it does), and neither does password reset —
///         <c>SignInService.RequestPasswordResetAsync</c> exists and answers uniformly, but nothing
///         mails the link. TOTP is mapped and will refuse every code until an
///         <c>ITotpSecretSeam</c> is wired over a vault; recovery codes and the delivered email code
///         at <c>/api/signin/otp</c> both work today. <c>/userinfo</c> is not mapped — the portal
///         reads <c>tid</c> and <c>sub</c> off the access token and <c>email</c> and <c>name</c> off
///         the id_token. One-time use of an authorization code is not enforced: it needs a hot-tier
///         code store, and PKCE binds a replayed code to the verifier only the legitimate tab holds.
///         The device flow and token exchange keep their <c>temporarily_unavailable</c> answers.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Password reset is owed for a specific reason rather than for want of a caller, and
///             the reason is a timing oracle.
///         </b> Everything it needs now exists —
///         <c>IUserGrain.IssueOtpAsync</c> mints and delivers a code, and
///         <see cref="OtpPurpose.PasswordReset" /> is a purpose it takes. What does not exist is a
///         way to send one without <i>waiting</i> for the carrier: docs/plan/11 § Credentials
///         requires the reset endpoint to "take the same time whether or not the account exists",
///         and the found branch would await an <c>IMessageGrain</c> dispatch that the not-found
///         branch has no equivalent of. <c>SignInService</c>'s 250 ms floor absorbs a grain
///         activation and does not absorb a mail provider. The fix is an asynchronous dispatch the
///         endpoint does not await — an outbox in <c>CyberCloud.Communication</c>, docs/plan/17 —
///         and wiring reset to the synchronous path in the meantime would trade an unbuilt feature
///         for a measurable enumeration oracle on the one endpoint whose entire design is not having
///         one.
///     </para>
///     <para>
///         <b>What the sign-in page needs, end to end:</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Name the tenant.</b> The <c>returnUrl</c> the page was sent with is the
///             <c>/authorize</c> request, and its <c>tenant</c> query parameter — a tenant id or a
///             slug — is what the page posts as <c>tenant</c> on <c>begin</c>, <c>password</c> and
///             <c>passkey/begin</c>. When the request named none, the page asks for an organisation
///             and posts that instead; when neither says, the host's fallback applies
///             (<c>TenantHint</c>).
///         </item>
///         <item>
///             <b>Ask for the address first, and only then offer credentials.</b>
///             <c>POST /api/signin/begin</c> returns the offered credential kinds
///             <i>
///                 in
///                 <see cref="CredentialKind" /> order
///             </i>, which puts a passkey first — docs/plan/11
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
///             <b>When <see cref="SignInOutcome.SecondFactorRequired" /> is set</b>, collect a
///             second factor and post it before treating the session as usable. Three are offered:
///             a TOTP code, a recovery code, or — after <c>POST /api/signin/otp/send</c> — a code
///             mailed to the account's address. ⚠ The <c>send</c> call takes no address; the page
///             must not collect one, because the whole point is that the platform chooses where a
///             second factor goes.
///         </item>
///         <item>
///             <b>Then resume the OIDC request</b> by navigating — a full page load, not a route
///             change — to the sanitized <c>returnUrl</c>, which is the original
///             <c>GET /authorize</c> with its query string intact. The cookie set by the sign-in is
///             what makes the second attempt succeed; <c>MapAuthorize</c> says what it checks.
///         </item>
///     </list>
///     <para>
///         <b>What the sign-up page needs</b> is the <c>/api/signup/*</c> surface docs/plan/11
///         § Sign-up and tenant creation describes, which lands beside this file; the stub this
///         file used to map at <c>/api/signup</c> is gone with it.
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
    ///     ⚠ The OIDC endpoints themselves — <c>/authorize</c>, <c>/token</c>, <c>/logout</c> and
    ///     <c>/.well-known/*</c> — are OpenIddict's; <see cref="IdentityHostOpenIddict" /> configures
    ///     them, and the passthrough options let a handler here take over where a decision needs our
    ///     grains: <see cref="MapAuthorize" />, <see cref="MapToken" /> and <see cref="MapLogout" />.
    ///     ⚠ A passthrough with no handler behind it is a <c>404</c>, which is what <c>/token</c>
    ///     answered for as long as nothing mapped it and the reason
    ///     https://github.com/Rikarin/CyberCloud/issues/68's gateway had no token to validate.
    ///     <c>/userinfo</c>, <c>/device</c> and <c>/device/verify</c> are enabled and unmapped, and
    ///     their validators refuse before the passthrough is reached.
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
        MapAuthorize(app);
        MapToken(app);
        MapLogout(app);

        return app;
    }

    // ── /authorize ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Maps the authorization endpoint's passthrough — the half of <c>/authorize</c> that reads
    ///     the cookie.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ By the time this runs,
    ///         <c>DegradedModeHandlers.ValidateAuthorizationRequest</c> has resolved the tenant and
    ///         the client and validated the redirect URI, the grant, the scopes and PKCE, and left
    ///         the first two in the transaction. <see cref="AuthorizeApi" /> decides what the cookie
    ///         is worth; this lambda turns the decision into one of three responses:
    ///         <c>Results.SignIn</c> with OpenIddict's scheme, which mints the code and redirects to
    ///         the client; <c>Results.Forbid</c> with the same scheme, which OpenIddict turns into an
    ///         error redirect to the client (the request was validated, so that is safe); or a plain
    ///         redirect to the sign-in page with this request as the return URL.
    ///     </para>
    ///     <para>
    ///         ⚠ The return URL is this request's <b>path and query</b>, never its absolute URL.
    ///         <see cref="ReturnUrl.Sanitize" /> accepts only a same-origin path, on both ends of the
    ///         redirect, and the sign-in page resumes by navigating to it — on the development run
    ///         through its dev server's proxy, which is what makes the page's origin look like this
    ///         one and carry the cookie.
    ///     </para>
    /// </remarks>
    static void MapAuthorize(IEndpointRouteBuilder app) {
        app.MapGet(
            IdentityHostOpenIddict.AuthorizationPath,
            async (HttpContext context, AuthorizeApi api, CancellationToken cancellationToken) => {
                var request = context.GetOpenIddictServerRequest()
                    ?? throw new InvalidOperationException(
                        "The authorization endpoint was reached outside OpenIddict's pipeline. "
                        + "EnableAuthorizationEndpointPassthrough is what routes a validated request here."
                    );

                var transaction = context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;

                if (transaction?.Properties.TryGetValue(DegradedModeHandlers.TenantProperty, out var tenantValue) != true
                    || tenantValue is not Guid tenantId
                    || !transaction.Properties.TryGetValue(DegradedModeHandlers.ClientProperty, out var clientValue)
                    || clientValue is not ApplicationRegistration client) {
                    return OpenIddictError(OpenIddictConstants.Errors.InvalidRequest, "The request was not validated.");
                }

                var decision = await api.DecideAsync(
                    request,
                    tenantId,
                    client,
                    context.User,
                    context.Request.Path + context.Request.QueryString,
                    cancellationToken
                );

                return decision switch {
                    AuthorizeDecision.IssueCode code => Results.SignIn(
                        code.Principal,
                        authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
                    ),
                    AuthorizeDecision.Refuse refused => OpenIddictError(refused.Error, refused.Description),
                    AuthorizeDecision.SignIn signIn => Results.Redirect(signIn.Location),
                    _ => throw new InvalidOperationException($"Unhandled decision {decision.GetType().Name}.")
                };
            }
        );
    }

    // ── /token ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Maps the token endpoint's passthrough — the half of <c>/token</c> that mints.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             By the time this runs, OpenIddict has parsed the request and
    ///             <c>DegradedModeHandlers.ValidateTokenRequest</c> has resolved the client.
    ///         </b> The passthrough is the tail of OpenIddict's own pipeline, not a route beside it:
    ///         a request that failed validation was answered with an OAuth error before this lambda
    ///         existed, so what is left is to mint. For client credentials the authenticated principal
    ///         travels in the transaction under <c>DegradedModeHandlers.ServicePrincipalProperty</c>;
    ///         for a code or a refresh the token's principal comes back from
    ///         <c>AuthenticateAsync</c> with OpenIddict's scheme, which is the library's way of
    ///         handing a passthrough the token it decrypted. A request that arrives without either is
    ///         refused rather than re-authenticated, because a second path that authenticates is a
    ///         second path to get wrong.
    ///     </para>
    ///     <para>
    ///         <c>Results.SignIn</c> with OpenIddict's scheme is what turns a principal into signed
    ///         tokens: the server's sign-in handler serializes it, signs it with the key set the JWKS
    ///         endpoint publishes, and writes the token response — and
    ///         <c>DegradedModeHandlers.MoveRefreshTokenToCookie</c> moves the browser client's refresh
    ///         token into <see cref="RefreshCookie" /> on the way out. Nothing here touches a key.
    ///     </para>
    ///     <para>
    ///         ⚠ Marked with the first-party CORS policy: the portal calls this cross-origin with
    ///         credentials, and no other origin gets a CORS header.
    ///     </para>
    /// </remarks>
    static void MapToken(IEndpointRouteBuilder app) {
        app.MapPost(
                IdentityHostOpenIddict.TokenPath,
                async (HttpContext context, TokenApi api, CancellationToken cancellationToken) => {
                    var request = context.GetOpenIddictServerRequest()
                        ?? throw new InvalidOperationException(
                            "The token endpoint was reached outside OpenIddict's pipeline. "
                            + "EnableTokenEndpointPassthrough is what routes a validated request here; "
                            + "a request that did not come through it has not been validated."
                        );

                    var transaction = context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;

                    if (request.IsClientCredentialsGrantType()) {
                        if (transaction?.Properties.TryGetValue(DegradedModeHandlers.ServicePrincipalProperty, out var value) != true
                            || value is not ServicePrincipalDescriptor principal) {
                            return OpenIddictError(OpenIddictConstants.Errors.InvalidClient, TokenApi.InvalidClientDescription);
                        }

                        return Results.SignIn(
                            api.Mint(principal, request.ClientId ?? string.Empty, request.GetScopes()),
                            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
                        );
                    }

                    if (transaction?.Properties.TryGetValue(DegradedModeHandlers.ClientProperty, out var clientValue) != true
                        || clientValue is not ApplicationRegistration client) {
                        return OpenIddictError(OpenIddictConstants.Errors.InvalidClient, "The client was not validated.");
                    }

                    var token = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

                    if (token.Principal is not { } presented) {
                        return OpenIddictError(OpenIddictConstants.Errors.InvalidGrant, "The token could not be read.");
                    }

                    var minted = request.IsRefreshTokenGrantType()
                        ? await api.MintForRefreshAsync(presented, client, cancellationToken)
                        : await api.MintForCodeAsync(presented, client, Describe(context), cancellationToken);

                    if (minted.TryGetError(out var refused)) {
                        return OpenIddictError(OpenIddictConstants.Errors.InvalidGrant, refused.Message);
                    }

                    return Results.SignIn(
                        minted.GetValueOrThrow(),
                        authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
                    );
                }
            )
            .RequireCors(FirstPartyClients.CorsPolicy);
    }

    // ── /logout ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Maps the end-session endpoint's passthrough: sign the cookie out, revoke its session,
    ///     forget the refresh cookie, and send the browser back to the client.
    /// </summary>
    /// <param name="app">The host's route builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Three things end here, and only one of them is the cookie. The cookie is signed
    ///         out through its own scheme so the handler clears it the way it set it; the
    ///         interactive <c>ISessionGrain</c> is revoked, which is what makes every token session
    ///         bound to it fail its next refresh (<see cref="TokenApi.MintForRefreshAsync" />) — they
    ///         are not enumerated here; and <see cref="RefreshCookie" /> is cleared so the portal's
    ///         next load starts a sign-in rather than presenting a token that will be refused.
    ///     </para>
    ///     <para>
    ///         The redirect is OpenIddict's: <c>Results.SignOut</c> with its scheme lands on the
    ///         <c>post_logout_redirect_uri</c> that <c>DegradedModeHandlers.ValidateEndSessionRequest</c>
    ///         matched against the registration, with <c>state</c> echoed. A navigation, not a
    ///         <c>fetch</c>, so the cookie's <c>SameSite=Lax</c> lets it through.
    ///     </para>
    /// </remarks>
    static void MapLogout(IEndpointRouteBuilder app) {
        app.MapGet(
                IdentityHostOpenIddict.EndSessionPath,
                async (HttpContext context, IGrainFactory grains, ILoggerFactory loggers) => {
                    var request = context.GetOpenIddictServerRequest()
                        ?? throw new InvalidOperationException(
                            "The end-session endpoint was reached outside OpenIddict's pipeline. "
                            + "EnableEndSessionEndpointPassthrough is what routes a validated request here."
                        );

                    if (IdentitySessionPrincipal.TenantId(context.User) is { } tenantId
                        && IdentitySessionPrincipal.SessionId(context.User) is { } sessionId) {
                        await grains.ForTenant(TenantHint.Qualifier(tenantId))
                            .GetGrain<ISessionGrain>(GrainKeys.Session(sessionId))
                            .RevokeAsync(RevocationReason.SignOut);

                        GrantLog.SignedOut(loggers.CreateLogger(typeof(IdentityEndpoints)), tenantId, sessionId);
                    }

                    RefreshCookie.Clear(context.Response);

                    return Results.SignOut(
                        new AuthenticationProperties { RedirectUri = request.PostLogoutRedirectUri },
                        [IdentityHostAuthentication.SchemeName, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
                    );
                }
            )
            .RequireCors(FirstPartyClients.CorsPolicy);
    }

    /// <summary>
    ///     An OAuth error answered through OpenIddict, so it takes the shape the endpoint's protocol
    ///     gives errors — a JSON body at <c>/token</c>, an error redirect or page at <c>/authorize</c>.
    /// </summary>
    static IResult OpenIddictError(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(
                new Dictionary<string, string?>(StringComparer.Ordinal) {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
                }
            ),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );

    // ── The sign-in API ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Maps the interactive sign-in endpoints the pages call.
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
    ///         ⚠
    ///         <b>
    ///             Under <c>/api</c>, so the cookie handler answers <c>401</c> rather than
    ///             redirecting.
    ///         </b> <see cref="IdentityHostAuthentication" />'s <c>OnRedirectToLogin</c>
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
        )
            .RequireAuthorization();

        // ── The delivered second factor — docs/plan/11 § Credentials' email OTP row ────────────
        //
        // ⚠ TWO ENDPOINTS AND NEITHER OF THEM HOLDS A CODE. `send` asks IUserGrain to mint, record
        // and deliver one; `otp` asks the same grain to compare and burn it. OtpPolicy carries the
        // four properties that put both inside a grain rather than here. Both read WHO from the
        // cookie, and `send` reads WHERE from grain state — an address on either request would let a
        // half-authenticated caller post the second factor to itself.
        app.MapPost(
            "/api/signin/otp/send",
            async (
                OtpSendRequest? request,
                HttpContext context,
                SignInApi api,
                CancellationToken cancellationToken
            ) => Results.Ok((await api.SendEmailOtpAsync(request, context.User, cancellationToken)).Response)
        )
            .RequireAuthorization();

        app.MapPost(
            "/api/signin/otp",
            async (
                SecondFactorRequest? request,
                HttpContext context,
                SignInApi api,
                CancellationToken cancellationToken
            ) => await IssueAsync(context, await api.VerifyEmailOtpAsync(request, context.User, cancellationToken))
        )
            .RequireAuthorization();

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
        )
            .RequireAuthorization();
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
