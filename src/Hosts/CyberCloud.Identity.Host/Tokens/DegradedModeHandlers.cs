using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using System.Security.Claims;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The request validation OpenIddict hands back to us in degraded mode, and where each answer
///     comes from. ADR-015, docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Degraded mode is what "we own the stores, and the stores are grains" costs at the
///             protocol layer, and this file is the bill.
///         </b> OpenIddict's own request validation resolves clients, redirect URIs and codes through
///         its core managers, which want a store implementation per object; without one the server
///         throws <i>"The core services must be registered"</i> on the first token request — which
///         is what this host did for as long as it mapped no token endpoint. Enabling the degraded
///         mode turns those built-in checks off and makes the server refuse any request for which
///         no custom validator is registered: <i>"No custom token request validation handler was
///         found. When enabling the degraded mode, a custom
///         'IOpenIddictServerHandler&lt;ValidateTokenRequestContext&gt;' must be implemented"</i>.
///         Every handler here exists to answer one of those sentences.
///     </para>
///     <para>
///         ⚠ <b>What degraded mode switches off is wider than the store lookups, and two of the
///         checks below exist to put it back.</b> OpenIddict's <c>ValidateClientRedirectUri</c>,
///         its grant and scope permission checks and — less obviously — its
///         <c>ValidateProofKeyForCodeExchangeRequirement</c> all carry the
///         <c>RequireDegradedModeDisabled</c> filter, so <c>RequireProofKeyForCodeExchange()</c> is
///         a setting the server no longer enforces on its own at <c>/authorize</c>.
///         <see cref="ValidateAuthorizationRequest" /> refuses a request without a
///         <c>code_challenge</c> for that reason; the verifier check at <c>/token</c>
///         (<c>ValidateCodeVerifier</c>) has no such filter and stays OpenIddict's.
///     </para>
///     <para>
///         The device flow's two handlers still answer <c>temporarily_unavailable</c>, naming what
///         the flow is waiting on — a verification page and a code store. ⚠ Rejecting in the
///         <i>validation</i> stage is what keeps <c>/authorize</c> from being an open redirect:
///         OpenIddict sends an error to the client's redirect URI only for a request whose
///         validation succeeded, and renders an error page for one whose validation did not.
///     </para>
/// </remarks>
public static class DegradedModeHandlers {
    /// <summary>
    ///     Where <see cref="ValidateTokenRequest" /> leaves the authenticated service principal for
    ///     the endpoint that mints — <see cref="OpenIddictServerTransaction.Properties" />.
    /// </summary>
    /// <remarks>
    ///     A transaction property rather than a second grain call: the endpoint runs later in the
    ///     same request, and re-authenticating there would be a second vault read for one grant.
    /// </remarks>
    public const string ServicePrincipalProperty = "cybercloud.token.service-principal";

    /// <summary>
    ///     Where the validators leave the resolved <see cref="ApplicationRegistration" /> for the
    ///     passthrough — <c>/authorize</c>'s and <c>/token</c>'s.
    /// </summary>
    public const string ClientProperty = "cybercloud.token.client";

    /// <summary>Where <see cref="ValidateAuthorizationRequest" /> leaves the resolved tenant id.</summary>
    public const string TenantProperty = "cybercloud.token.tenant";

    /// <summary>
    ///     Where <see cref="ValidateAuthorizationRequest" /> leaves a <c>tenant</c> hint that named
    ///     no tenant the directory knows — so the passthrough sends the person to the sign-in page
    ///     to name one, instead of answering an error page. Absent when the hint resolved.
    /// </summary>
    public const string UnknownTenantProperty = "cybercloud.token.unknown-tenant";

    /// <summary>Every handler this server registers, in one list so a missing one is a visible gap.</summary>
    public static IReadOnlyList<OpenIddictServerHandlerDescriptor> All { get; } = [
        ValidateAuthorizationRequest.Descriptor,
        ExtractRefreshTokenFromCookie.Descriptor,
        ValidateTokenRequest.Descriptor,
        MoveRefreshTokenToCookie.Descriptor,
        ValidateEndSessionRequest.Descriptor,
        KeepAccessTokenToTheClosedSet.Descriptor,
        RefuseDeviceAuthorizationRequests.Descriptor,
        RefuseEndUserVerificationRequests.Descriptor,
        RefuseDeviceCodeStorage.GenerateDescriptor,
        RefuseDeviceCodeStorage.ValidateDescriptor
    ];

    // ── /authorize ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Validates an authorization request: the tenant through the directory, the client
    ///     through the resolver, the redirect URI against the registration, the grant and the
    ///     scopes against it too, and PKCE.
    /// </summary>
    /// <param name="tenants">The tenant hint's resolver.</param>
    /// <param name="clients">The client resolver — static first, then the tenant's index.</param>
    /// <param name="logger">Where the reasons go.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Everything here rejects in the validation stage, so nothing here redirects.</b>
    ///         A refusal during validation is rendered by OpenIddict as an error response on this
    ///         origin, never as a redirect to the <c>redirect_uri</c> — which is the only safe
    ///         answer before that URI has been matched against the registration, and is kept for
    ///         the refusals after it (grant, scope, PKCE) so a broken client sees one behaviour.
    ///         <c>AuthorizeHandlerTests.AnUnregisteredRedirectUriIsRefusedWithoutARedirect</c>.
    ///     </para>
    ///     <para>
    ///         Ordered after OpenIddict's own parameter checks, so a malformed <c>redirect_uri</c> or
    ///         a missing <c>response_type</c> gets the library's sentence naming the parameter, and
    ///         this handler sees only well-formed requests.
    ///     </para>
    /// </remarks>
    public sealed class ValidateAuthorizationRequest(
        TenantHint tenants,
        IClientResolver clients,
        ILogger<ValidateAuthorizationRequest> logger
    ) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
                .UseSingletonHandler<ValidateAuthorizationRequest>()
                .SetOrder(OpenIddictServerHandlers.Authentication.ValidateAuthorizedParty.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            var hint = (string?)context.Request[TenantHint.ParameterName];
            var tenantId = await tenants.ResolveAsync(hint, context.CancellationToken);

            // ⚠ A HINT THAT NAMES NOBODY IS NOT AN ERROR PAGE — for a first-party client. The portal
            // remembers the last tenant in a cookie and sends it on every /authorize; a developer's
            // second `dotnet run` starts with an empty durable tier (ADR-014), so the remembered
            // tenant is gone, and the first thing the portal showed was OpenIddict's
            // `invalid_request: A tenant is required` with nowhere to go. A retired tenant and a
            // mistyped slug are the same shape. There is a page for exactly this — the sign-in
            // page's organisation field — so the request is validated against the client's static
            // registration, marked, and the passthrough sends the person there with the hint
            // removed; the request resumes with the tenant they name. A request that names nothing
            // and has no fallback, or a tenant client (whose registration lives IN a tenant), still
            // gets the error: there is no registration to validate the redirect_uri against.
            if (tenantId is null && !string.IsNullOrWhiteSpace(hint) && FirstPartyClients.IsFirstParty(context.ClientId)) {
                context.Transaction.Properties[UnknownTenantProperty] = hint;
                tenantId = Guid.Empty;
            }

            if (tenantId is null) {
                Refuse(context, Guid.Empty, OpenIddictConstants.Errors.InvalidRequest, "A tenant is required: name one with the 'tenant' parameter, as a tenant id or a slug.", "no-tenant");

                return;
            }

            var client = await clients.ResolveAsync(tenantId.Value, context.ClientId, context.CancellationToken);

            if (client is null) {
                Refuse(context, tenantId.Value, OpenIddictConstants.Errors.InvalidClient, "The client is not registered.", "unknown-client");

                return;
            }

            if (!FirstPartyClients.IsRegisteredRedirectUri(client, context.RedirectUri)) {
                Refuse(context, tenantId.Value, OpenIddictConstants.Errors.InvalidRequest, "The 'redirect_uri' parameter is not registered for this client.", "unregistered-redirect-uri");

                return;
            }

            if (!client.AllowedGrants.Contains(GrantType.AuthorizationCode)) {
                Refuse(context, tenantId.Value, OpenIddictConstants.Errors.UnauthorizedClient, "This client may not use the authorization-code flow.", "grant-not-allowed");

                return;
            }

            if (context.Request.GetScopes().Any(x => !client.AllowedScopes.Contains(x, StringComparer.Ordinal))) {
                Refuse(context, tenantId.Value, OpenIddictConstants.Errors.InvalidScope, "A requested scope is not allowed for this client.", "scope-not-allowed");

                return;
            }

            // ⚠ OpenIddict's own requirement check is filtered out in degraded mode — see the type's
            // remarks — so the one setting docs/plan/11 § Protocol makes non-negotiable is enforced
            // here. Only the presence: the method's spelling is OpenIddict's parameter check.
            if (string.IsNullOrEmpty(context.Request.CodeChallenge)) {
                Refuse(context, tenantId.Value, OpenIddictConstants.Errors.InvalidRequest, "The 'code_challenge' parameter is required: this server accepts the authorization-code flow only with PKCE.", "pkce-missing");

                return;
            }

            context.Transaction.Properties[TenantProperty] = tenantId.Value;
            context.Transaction.Properties[ClientProperty] = client;
        }

        void Refuse(ValidateAuthorizationRequestContext context, Guid tenantId, string error, string description, string reason) {
            GrantLog.AuthorizationRequestRefused(logger, tenantId, error, reason);
            context.Reject(error, description);
        }
    }

    // ── /token ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Copies <c>__Host-cyc-refresh</c> into a browser client's refresh request that carries
    ///     no <c>refresh_token</c> of its own — after checking who is asking.
    /// </summary>
    /// <param name="clients">The first-party registrations, for the origin allow-list.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>Origin</c> check runs before any grain call and before the cookie is even
    ///         read.</b> <c>SameSite=Lax</c> lets the browser send the cookie on a same-site
    ///         <c>POST</c>, and a tenant's subdomain is same-site with the identity host
    ///         (docs/plan/11 § Hosts). A page on such a subdomain could therefore <c>POST</c>
    ///         <c>grant_type=refresh_token&amp;client_id=cyc-portal</c> and have the browser attach
    ///         the person's cookie — and would get an access token for them, if the only check were
    ///         the cookie. The browser sets <c>Origin</c> on every cross-origin <c>POST</c> and a
    ///         page cannot forge it, so an origin outside the portal's redirect-URI origins is
    ///         refused here with <c>invalid_request</c> and nothing else happens.
    ///         <c>TokenApiTests.ACookieBorneRefreshFromAForeignOriginNeverReachesAGrain</c>. This
    ///         is the read side; <see cref="ValidateTokenRequest" /> guards the write side, for the
    ///         requests that carry their token in the body or exchange a code.
    ///     </para>
    ///     <para>
    ///         A request that carries <c>refresh_token</c> in its body is not this handler's — the
    ///         CLI does that, from a process with no <c>Origin</c> — and a browser request with the
    ///         right origin and no cookie is left alone too: OpenIddict then answers that the
    ///         parameter is missing, which is what the portal's first load expects.
    ///     </para>
    /// </remarks>
    public sealed class ExtractRefreshTokenFromCookie(FirstPartyClients clients) : IOpenIddictServerHandler<ExtractTokenRequestContext> {
        /// <summary>The refusal, verbatim, for a request whose origin is not a first-party browser's.</summary>
        public const string OriginNotAllowed = "The request's origin is not allowed to present the refresh cookie.";

        /// <summary>The registration — after the form has been read, before anything validates it.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractTokenRequestContext>()
                .UseSingletonHandler<ExtractRefreshTokenFromCookie>()
                .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<ExtractTokenRequestContext>.Descriptor.Order + 10_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ExtractTokenRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            var request = context.Request;

            if (request is null
                || !request.IsRefreshTokenGrantType()
                || !string.IsNullOrEmpty(request.RefreshToken)
                || !FirstPartyClients.IsBrowserClient(request.ClientId)) {
                return default;
            }

            var http = context.Transaction.GetHttpRequest();

            if (http is null) {
                return default;
            }

            var origin = http.Headers.Origin.ToString();

            if (!clients.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, OriginNotAllowed);

                return default;
            }

            if (RefreshCookie.Read(http) is { } token) {
                request.RefreshToken = token;
            }

            return default;
        }
    }

    /// <summary>
    ///     Validates a token request, by grant: client credentials through
    ///     <see cref="TokenApi.AuthenticateClientAsync" />; a code or a refresh token by resolving
    ///     the client the token was minted for and checking it is the one asking.
    /// </summary>
    /// <param name="api">The client-credentials decision.</param>
    /// <param name="clients">The client resolver.</param>
    /// <param name="firstParty">The first-party registrations, for the origin allow-list.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A browser client's token request is refused unless its <c>Origin</c> is one of
    ///         that client's redirect-URI origins — the code exchange and the body-borne refresh
    ///         both, not only the cookie-borne refresh.</b> The cookie is written by
    ///         <see cref="MoveRefreshTokenToCookie" /> into whichever browser made the request, and
    ///         <c>Set-Cookie</c> on a top-level cross-site form <c>POST</c> is honoured whatever
    ///         <c>SameSite</c> says. Without this rule an attacker holding a code and its verifier
    ///         — or a refresh token — for <i>their own</i> account could form-post it to
    ///         <c>/token</c> from a page in the victim's browser, plant their refresh cookie there,
    ///         and the victim's next silent refresh would sign the victim into the attacker's
    ///         tenant: the login-CSRF that makes everything typed afterwards land where the
    ///         attacker can read it. The portal's <c>state</c> check on <c>/auth/callback</c> never
    ///         sees that path. The browser sets <c>Origin</c> on every cross-origin <c>POST</c> and
    ///         a page cannot forge it, so the check refuses before a token session is opened or a
    ///         chain rotated. <c>GrantsOverHttpTests.ACodeExchangeFromAForeignOriginPlantsNoCookie</c>
    ///         and <c>TokenApiTests.ABrowserClientsTokenRequestFromAForeignOriginIsRefused</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ Ordered after OpenIddict's <c>ValidateAuthentication</c>, which is what decrypts the
    ///         code or the refresh token and puts its principal on the context — so a token this
    ///         server did not mint, or one past its lifetime, is refused by the library before this
    ///         runs, and this handler reads the tenant off a principal it can trust. It runs before
    ///         OpenIddict's <c>ValidateAuthorizedParty</c>, <c>ValidateRedirectUri</c> and
    ///         <c>ValidateCodeVerifier</c>, which remain the library's: the presenter, the redirect
    ///         URI the code was issued for, and PKCE.
    ///     </para>
    ///     <para>
    ///         ⚠ The tenant for a code or a refresh comes from the token's own <c>tid</c> and never
    ///         from a parameter: the token names the tenant the sign-in happened in, and letting the
    ///         exchange say otherwise would be letting it move a code between tenants.
    ///     </para>
    ///     <para>
    ///         ⚠ The grain calls — is the sign-in still live, rotate the chain — are not here but in
    ///         the passthrough (<c>IdentityEndpoints.MapToken</c> → <see cref="TokenApi" />), after
    ///         every check that could still refuse the request. A rotation followed by a refusal
    ///         would leave the client holding a retired generation, and its next honest refresh
    ///         would read as a replay and revoke the chain.
    ///     </para>
    /// </remarks>
    public sealed class ValidateTokenRequest(TokenApi api, IClientResolver clients, FirstPartyClients firstParty) : IOpenIddictServerHandler<ValidateTokenRequestContext> {
        /// <summary>The refusal, verbatim, for a browser client's request from an origin that is not its own.</summary>
        public const string OriginNotAllowed = "The request's origin is not allowed to use this client.";

        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
                .UseSingletonHandler<ValidateTokenRequest>()
                .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthentication.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateTokenRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Request.IsClientCredentialsGrantType()) {
                await ValidateClientCredentialsAsync(context);

                return;
            }

            if (context.Request.IsAuthorizationCodeGrantType() || context.Request.IsRefreshTokenGrantType()) {
                await ValidateTokenBearingGrantAsync(context);

                return;
            }

            context.Reject(
                OpenIddictConstants.Errors.UnsupportedGrantType,
                "The device and token-exchange grants are owed — docs/plan/11 § Protocol, and TokenApi's "
                + "remarks say what each is waiting on."
            );
        }

        async Task ValidateClientCredentialsAsync(ValidateTokenRequestContext context) {
            var authenticated = await api.AuthenticateClientAsync(
                context.Request.ClientId,
                context.Request.ClientSecret,
                context.CancellationToken
            );

            if (authenticated.TryGetError(out var refused)) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, refused.Message);

                return;
            }

            context.Transaction.Properties[ServicePrincipalProperty] = authenticated.GetValueOrThrow();
        }

        async Task ValidateTokenBearingGrantAsync(ValidateTokenRequestContext context) {
            if ((context.AuthorizationCodePrincipal ?? context.RefreshTokenPrincipal) is not { } principal
                || !Guid.TryParseExact(principal.GetClaim(AccessTokenClaims.TenantId), "N", out var tenantId)) {
                context.Reject(OpenIddictConstants.Errors.InvalidGrant, "The token names no tenant.");

                return;
            }

            var client = await clients.ResolveAsync(tenantId, context.ClientId, context.CancellationToken);

            if (client is null) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");

                return;
            }

            // ⚠ A public client presents no secret, and one that does is misconfigured in a way worth
            // refusing: a "secret" a SPA or a CLI holds is one every copy of it holds.
            if (client.IsPublicClient && !string.IsNullOrEmpty(context.Request.ClientSecret)) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "A public client must not send a client_secret.");

                return;
            }

            // ⚠ A confidential client's secret is NOT verified here — owed with the consent page.
            // Unreachable until then: every tenant-registered client is answered consent_required
            // at /authorize (AuthorizeApi), so no code or refresh token is ever minted for one. The
            // consent page must not land without the secret check landing beside it, or a
            // confidential client's code would be exchangeable by anyone holding it.

            // ⚠ The browser client, from any origin but its own: refused before a grain is touched.
            // See the type's remarks — this is the write side of the cookie rule, and
            // ExtractRefreshTokenFromCookie is the read side.
            if (FirstPartyClients.IsBrowserClient(client.ClientId) && !IsFirstPartyOrigin(context)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, OriginNotAllowed);

                return;
            }

            var grant = context.Request.IsRefreshTokenGrantType() ? GrantType.RefreshToken : GrantType.AuthorizationCode;

            if (!client.AllowedGrants.Contains(grant)) {
                context.Reject(OpenIddictConstants.Errors.UnauthorizedClient, "This client may not use this grant.");

                return;
            }

            context.Transaction.Properties[ClientProperty] = client;
        }

        bool IsFirstPartyOrigin(ValidateTokenRequestContext context) =>
            context.Transaction.GetHttpRequest() is { } http
            && firstParty.AllowedOrigins.Contains(http.Headers.Origin.ToString(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Moves a browser client's refresh token out of the JSON body and into
    ///     <c>__Host-cyc-refresh</c>; clears that cookie when a browser client's refresh failed.
    /// </summary>
    /// <param name="clients">The first-party registrations, for the origin allow-list.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Runs before OpenIddict's <c>ProcessJsonResponse</c> writes the body, and edits the
    ///         response it is about to write: <c>refresh_token</c> is removed so the same credential
    ///         is not handed out twice, once in a cookie the page cannot read and once in a body it
    ///         can. For every other client the body is untouched —
    ///         <c>RefreshCookieTests.OnlyBrowserClientsGetTheCookie</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cookie is written only for a request whose <c>Origin</c> is the browser
    ///         client's own — the second lock on the login-CSRF <see cref="ValidateTokenRequest" />
    ///         refuses first.</b> That validator is what answers a foreign origin, so a token
    ///         response for one should never reach this handler; if one does — a handler reordered,
    ///         a validator dropped — the refresh token is handed to nobody: not to the cookie, which
    ///         would plant it in the victim's browser, and not to the body, which the portal's
    ///         token is never in. An attacker's own tokens in an attacker's own page cost nothing;
    ///         the cookie in someone else's browser is the whole attack.
    ///         <c>RefreshCookieTests.AForeignOriginGetsNoCookieAndNoToken</c>.
    ///     </para>
    ///     <para>
    ///         The clearing half: a refresh that answered an error for a browser client is a cookie
    ///         the browser should stop presenting — a revoked chain will refuse it every time, and a
    ///         portal that keeps sending it keeps getting <c>invalid_grant</c> instead of a sign-in.
    ///     </para>
    /// </remarks>
    public sealed class MoveRefreshTokenToCookie(FirstPartyClients clients) : IOpenIddictServerHandler<ApplyTokenResponseContext> {
        /// <summary>The registration — before the JSON body is written.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyTokenResponseContext>()
                .UseSingletonHandler<MoveRefreshTokenToCookie>()
                .SetOrder(OpenIddictServerAspNetCoreHandlers.ProcessJsonResponse<ApplyTokenResponseContext>.Descriptor.Order - 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ApplyTokenResponseContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Transaction.GetHttpRequest() is not { HttpContext.Response: { } response } http
                || !FirstPartyClients.IsBrowserClient(context.Request?.ClientId)) {
                return default;
            }

            if (!string.IsNullOrEmpty(context.Response.RefreshToken)) {
                // ⚠ Never in the body for the browser client, and in the cookie only for its own
                // origin — see the type's remarks.
                if (clients.AllowedOrigins.Contains(http.Headers.Origin.ToString(), StringComparer.Ordinal)) {
                    RefreshCookie.Issue(response, context.Response.RefreshToken);
                }

                context.Response.RefreshToken = null;

                return default;
            }

            // ⚠ Only invalid_grant clears the cookie — the chain refused it, so the browser should
            // stop presenting it. An invalid_request (a foreign Origin, a missing parameter) says
            // nothing about the cookie, and clearing it then would let a page on another origin
            // sign the person out of the portal with one POST.
            if (string.Equals(context.Response.Error, OpenIddictConstants.Errors.InvalidGrant, StringComparison.Ordinal)
                && context.Request?.IsRefreshTokenGrantType() == true) {
                RefreshCookie.Clear(response);
            }

            return default;
        }
    }

    // ── /logout ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Validates an end-session request: the client, and its <c>post_logout_redirect_uri</c>
    ///     against the registration.
    /// </summary>
    /// <param name="tenants">The tenant hint's resolver, for the fallback tenant.</param>
    /// <param name="clients">The client resolver.</param>
    /// <remarks>
    ///     ⚠ The tenant a tenant-registered client is looked up in is the cookie's, when there is
    ///     one, and the fallback otherwise — a sign-out carries no <c>tenant</c> parameter, and the
    ///     cookie is the only thing that says where the person signed in. First-party clients are
    ///     tenant-independent and need neither.
    /// </remarks>
    public sealed class ValidateEndSessionRequest(TenantHint tenants, IClientResolver clients) : IOpenIddictServerHandler<ValidateEndSessionRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateEndSessionRequestContext>()
                .UseSingletonHandler<ValidateEndSessionRequest>()
                .SetOrder(OpenIddictServerHandlers.Session.ValidateAuthorizedParty.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateEndSessionRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrEmpty(context.ClientId)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, "The 'client_id' parameter is required.");

                return;
            }

            var tenantId = await TenantOfAsync(context);

            var client = tenantId is null ? null : await clients.ResolveAsync(tenantId.Value, context.ClientId, context.CancellationToken);

            if (client is null) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");

                return;
            }

            if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri)
                && !FirstPartyClients.IsRegisteredPostLogoutRedirectUri(client, context.PostLogoutRedirectUri)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, "The 'post_logout_redirect_uri' parameter is not registered for this client.");

                return;
            }

            context.Transaction.Properties[ClientProperty] = client;
        }

        async Task<Guid?> TenantOfAsync(ValidateEndSessionRequestContext context) {
            if (context.Transaction.GetHttpRequest()?.HttpContext is { } http) {
                var cookie = await http.AuthenticateAsync(IdentityHostAuthentication.SchemeName);

                if (IdentitySessionPrincipal.TenantId(cookie.Principal) is { } fromCookie) {
                    return fromCookie;
                }
            }

            return tenants.Default;
        }
    }

    // ── The access token on the wire ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Keeps a serialized access token to exactly <see cref="AccessTokenClaims.Permitted" />, by
    ///     removing what OpenIddict adds of its own accord.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Found on the wire, not on the principal, and that is why this handler exists.</b>
    ///         <see cref="AccessTokenPrincipalFactory" /> checks its output against the closed set,
    ///         and every assertion on the principal passed — while the first token
    ///         <c>GrantsOverHttpTests</c> decoded carried <c>scope</c>, <c>client_id</c> and
    ///         <c>oi_prst</c> beside it. OpenIddict writes the principal's scopes into a JWT access
    ///         token as <c>scope</c> (RFC 9068 § 2.2.3), its presenter as <c>oi_prst</c>, and the
    ///         client as <c>client_id</c> (RFC 9068 § 2.2) — and the scopes and the presenter
    ///         <i>have</i> to be on the sign-in principal, because OpenIddict issues a refresh token
    ///         only to a principal that holds <c>offline_access</c> and checks the presenter at the
    ///         next exchange. So they are stripped here, from the access token's own copy of the
    ///         principal, just before it is signed; the refresh token and the id_token are generated
    ///         from their own copies and keep everything they need.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>scope</c> is not merely surplus: it is in
    ///         <see cref="AccessTokenClaims.ForbiddenClaims" /> — "carrying both would be two sources
    ///         of truth" beside <c>scp</c> — and <c>JwksBearerTokenValidator</c> refuses a token that
    ///         carries it. Without this handler every token a person took from <c>/token</c> would
    ///         be a <c>401</c> at the gateway with a message about a claim nobody added.
    ///     </para>
    ///     <para>
    ///         Only the access token, and only claims outside the set: OpenIddict's own private
    ///         claims (<c>oi_*</c>) that become <c>iss</c>, <c>exp</c>, <c>iat</c>, <c>jti</c> and
    ///         <c>aud</c> are left alone, because those spellings are in the set.
    ///     </para>
    /// </remarks>
    public sealed class KeepAccessTokenToTheClosedSet : IOpenIddictServerHandler<GenerateTokenContext> {
        /// <summary>The registration — before the subject is cloned into the token descriptor.</summary>
        /// <remarks>
        ///     ⚠ Before <c>AttachTokenSubject</c>, not merely before the token is signed. OpenIddict's
        ///     <c>AttachTokenSubject</c> clones the principal into the <c>SecurityTokenDescriptor</c>
        ///     and <c>AttachTokenMetadata</c> computes <c>scope</c> from the principal's scopes
        ///     right after it; a handler ordered between them and the signing step edits a principal
        ///     the descriptor no longer reads, and every claim it removed is signed anyway. That
        ///     ordering was found by <c>GrantsOverHttpTests</c> on the wire and confirmed against the
        ///     decompiled handlers.
        /// </remarks>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
                .UseSingletonHandler<KeepAccessTokenToTheClosedSet>()
                .SetOrder(OpenIddictServerHandlers.Protection.AttachTokenSubject.Descriptor.Order - 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(GenerateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.TokenType is not OpenIddictConstants.TokenTypeIdentifiers.AccessToken
                || context.Principal?.Identity is not ClaimsIdentity identity) {
                return default;
            }

            foreach (var claim in identity.Claims.Where(IsOutsideTheSet).ToList()) {
                identity.RemoveClaim(claim);
            }

            return default;
        }

        static bool IsOutsideTheSet(Claim claim) =>
            string.Equals(claim.Type, OpenIddictConstants.Claims.Private.Scope, StringComparison.Ordinal)
            || string.Equals(claim.Type, OpenIddictConstants.Claims.Private.Presenter, StringComparison.Ordinal)
            || (!claim.Type.StartsWith(OpenIddictConstants.Claims.Prefixes.Private, StringComparison.Ordinal)
                && !AccessTokenClaims.Permitted.Contains(claim.Type));
    }

    // ── The device flow, still owed ────────────────────────────────────────────────────────────

    /// <summary>The device flow, until there is a verification page and a code store.</summary>
    public sealed class RefuseDeviceAuthorizationRequests
        : IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateDeviceAuthorizationRequestContext>()
                .UseSingletonHandler<RefuseDeviceAuthorizationRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The device-authorization flow is not served yet: it needs the verification page and "
                + "a store for device and user codes. docs/plan/11 § Protocol."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The device flow's other half, refused for the same reason.</summary>
    public sealed class RefuseEndUserVerificationRequests
        : IOpenIddictServerHandler<ValidateEndUserVerificationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateEndUserVerificationRequestContext>()
                .UseSingletonHandler<RefuseEndUserVerificationRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateEndUserVerificationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The device-authorization flow is not served yet, so there is no user code to verify. "
                + "docs/plan/11 § Protocol."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     The device flow's codes, which in degraded mode the server cannot store or look up
    ///     without help — refused at generation and at validation.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Required at start-up, not at request time, and that is the one place the pattern on
    ///         this file breaks.
    ///     </b> A device code and a user code are the two tokens OpenIddict cannot make
    ///     self-contained — a user types the user code into a page, so something has to map it back
    ///     — and its post-configuration refuses to build the server options at all when the device
    ///     flow is allowed and no custom <c>ValidateTokenContext</c> and <c>GenerateTokenContext</c>
    ///     handler exists: <i>"No custom token validation handler was found. When enabling the
    ///     degraded mode, a custom 'IOpenIddictServerHandler&lt;ValidateTokenContext&gt;' must be
    ///     implemented to handle device and user codes"</i>.
    ///     <c>OpenIddictServerOptionsTests.TheOptionsCanBeMaterialisedAtAll</c> found it. ⚠ Both
    ///     handlers act on those two token types and no other — an access token, a code and a
    ///     refresh token pass through untouched, which is what makes them safe to register beside
    ///     the grants that are served.
    /// </remarks>
    public sealed class RefuseDeviceCodeStorage
        : IOpenIddictServerHandler<GenerateTokenContext>, IOpenIddictServerHandler<ValidateTokenContext> {
        const string Reason =
            "The device-authorization flow is not served yet: its device and user codes need a store "
            + "this host does not have. docs/plan/11 § Protocol.";

        /// <summary>The generation half.</summary>
        public static OpenIddictServerHandlerDescriptor GenerateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
                .UseSingletonHandler<RefuseDeviceCodeStorage>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <summary>The validation half.</summary>
        public static OpenIddictServerHandlerDescriptor ValidateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<RefuseDeviceCodeStorage>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(GenerateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.TokenType is OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode
                or OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode) {
                context.Reject(OpenIddictConstants.Errors.TemporarilyUnavailable, Reason);
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            // Only when the caller could ONLY be presenting a device or user code. A validation that
            // would also accept an access token or a refresh token is somebody else's to answer.
            if (context.ValidTokenTypes.Count > 0
                && context.ValidTokenTypes.All(x =>
                    x is OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode
                        or OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode
                )) {
                context.Reject(OpenIddictConstants.Errors.TemporarilyUnavailable, Reason);
            }

            return ValueTask.CompletedTask;
        }
    }
}
