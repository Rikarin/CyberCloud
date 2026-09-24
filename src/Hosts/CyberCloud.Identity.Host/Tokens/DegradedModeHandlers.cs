using CyberCloud.Identity.Contracts;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.RateLimiting;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Orleans.Multitenant;
using System.Globalization;
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
///         no custom validator is registered:
///         <i>
///             "No custom token request validation handler was
///             found. When enabling the degraded mode, a custom
///             'IOpenIddictServerHandler&lt;ValidateTokenRequestContext&gt;' must be implemented"
///         </i>.
///         Every handler here exists to answer one of those sentences.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What degraded mode switches off is wider than the store lookups, and two of the
///             checks below exist to put it back.
///         </b> OpenIddict's <c>ValidateClientRedirectUri</c>,
///         its grant and scope permission checks and — less obviously — its
///         <c>ValidateProofKeyForCodeExchangeRequirement</c> all carry the
///         <c>RequireDegradedModeDisabled</c> filter, so <c>RequireProofKeyForCodeExchange()</c> is
///         a setting the server no longer enforces on its own at <c>/authorize</c>.
///         <see cref="ValidateAuthorizationRequest" /> refuses a request without a
///         <c>code_challenge</c> for that reason; the verifier check at <c>/token</c>
///         (<c>ValidateCodeVerifier</c>) has no such filter and stays OpenIddict's.
///     </para>
///     <para>
///         ⚠ Rejecting in the <i>validation</i> stage is what keeps <c>/authorize</c> from being an
///         open redirect: OpenIddict sends an error to the client's redirect URI only for a request
///         whose validation succeeded, and renders an error page for one whose validation did not.
///     </para>
///     <para>
///         ⚠ <b>The device flow (#43) is the second store degraded mode takes away, and
///         <see cref="StoreDeviceCodes" /> is the store.</b> OpenIddict cannot make a user code
///         self-contained — a person types it — and without a token store it neither remembers the
///         codes nor knows whether anybody has answered. The four device handlers here put
///         <c>IDeviceAuthorizationGrain</c> in that place: the codes are minted and recorded at
///         generation, a poll is answered from the grain at validation with exactly the error RFC
///         8628 § 3.5 names, and the answer itself is taken by the verification page's own API,
///         not by OpenIddict's verification endpoint, which only redirects to the page.
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
        StampAuthorizationCodeId.Descriptor,
        ValidateDeviceAuthorizationRequest.Descriptor,
        AttachPollingInterval.Descriptor,
        ValidateEndUserVerificationRequest.Descriptor,
        StoreDeviceCodes.GenerateDescriptor,
        StoreDeviceCodes.ValidateDescriptor,
        ValidateRevocationRequest.Descriptor,
        RevokeTokenSession.Descriptor
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
            if (tenantId is null
                && !string.IsNullOrWhiteSpace(hint)
                && FirstPartyClients.IsFirstParty(context.ClientId)) {
                context.Transaction.Properties[UnknownTenantProperty] = hint;
                tenantId = Guid.Empty;
            }

            if (tenantId is null) {
                Refuse(
                    context,
                    Guid.Empty,
                    OpenIddictConstants.Errors.InvalidRequest,
                    "A tenant is required: name one with the 'tenant' parameter, as a tenant id or a slug.",
                    "no-tenant"
                );

                return;
            }

            var client = await clients.ResolveAsync(tenantId.Value, context.ClientId, context.CancellationToken);

            if (client is null) {
                Refuse(
                    context,
                    tenantId.Value,
                    OpenIddictConstants.Errors.InvalidClient,
                    "The client is not registered.",
                    "unknown-client"
                );

                return;
            }

            if (!FirstPartyClients.IsRegisteredRedirectUri(client, context.RedirectUri)) {
                Refuse(
                    context,
                    tenantId.Value,
                    OpenIddictConstants.Errors.InvalidRequest,
                    "The 'redirect_uri' parameter is not registered for this client.",
                    "unregistered-redirect-uri"
                );

                return;
            }

            if (!client.AllowedGrants.Contains(GrantType.AuthorizationCode)) {
                Refuse(
                    context,
                    tenantId.Value,
                    OpenIddictConstants.Errors.UnauthorizedClient,
                    "This client may not use the authorization-code flow.",
                    "grant-not-allowed"
                );

                return;
            }

            if (context.Request.GetScopes().Any(x => !client.AllowedScopes.Contains(x, StringComparer.Ordinal))) {
                Refuse(
                    context,
                    tenantId.Value,
                    OpenIddictConstants.Errors.InvalidScope,
                    "A requested scope is not allowed for this client.",
                    "scope-not-allowed"
                );

                return;
            }

            // ⚠ OpenIddict's own requirement check is filtered out in degraded mode — see the type's
            // remarks — so the one setting docs/plan/11 § Protocol makes non-negotiable is enforced
            // here. Only the presence: the method's spelling is OpenIddict's parameter check.
            if (string.IsNullOrEmpty(context.Request.CodeChallenge)) {
                Refuse(
                    context,
                    tenantId.Value,
                    OpenIddictConstants.Errors.InvalidRequest,
                    "The 'code_challenge' parameter is required: this server accepts the authorization-code flow only with PKCE.",
                    "pkce-missing"
                );

                return;
            }

            context.Transaction.Properties[TenantProperty] = tenantId.Value;
            context.Transaction.Properties[ClientProperty] = client;
        }

        void Refuse(
            ValidateAuthorizationRequestContext context,
            Guid tenantId,
            string error,
            string description,
            string reason
        ) {
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
    ///         ⚠
    ///         <b>
    ///             The <c>Origin</c> check runs before any grain call and before the cookie is even
    ///             read.
    ///         </b> <c>SameSite=Lax</c> lets the browser send the cookie on a same-site
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
    public sealed class ExtractRefreshTokenFromCookie(FirstPartyClients clients) :
        IOpenIddictServerHandler<ExtractTokenRequestContext> {
        /// <summary>The refusal, verbatim, for a request whose origin is not a first-party browser's.</summary>
        public const string OriginNotAllowed = "The request's origin is not allowed to present the refresh cookie.";

        /// <summary>The registration — after the form has been read, before anything validates it.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ExtractTokenRequestContext>()
                .UseSingletonHandler<ExtractRefreshTokenFromCookie>()
                .SetOrder(
                    OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<ExtractTokenRequestContext>.Descriptor.Order
                    + 10_000
                )
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
    ///     <see cref="TokenApi.AuthenticateClientAsync" />; a code, a refresh token or an approved
    ///     device code by resolving the client the token was minted for and checking it is the one
    ///     asking.
    /// </summary>
    /// <param name="api">The client-credentials decision.</param>
    /// <param name="clients">The client resolver.</param>
    /// <param name="firstParty">The first-party registrations, for the origin allow-list.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A browser client's token request is refused unless its <c>Origin</c> is one of
    ///             that client's redirect-URI origins — the code exchange and the body-borne refresh
    ///             both, not only the cookie-borne refresh.
    ///         </b> The cookie is written by
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
    ///         ⚠
    ///         <b>
    ///             A confidential client authenticates on the code and refresh grants, and the
    ///             check is here rather than in the passthrough.
    ///         </b> RFC 6749 § 4.1.3 and § 6: a client
    ///         that was issued credentials MUST authenticate at the token endpoint, and the reason
    ///         is the code — a confidential client's redirect URI may be a server nobody but the
    ///         client can read, so anyone who lifts a code from a log or a <c>Referer</c> should
    ///         still be unable to exchange it without the secret. <see cref="ApplicationRegistration.ClientSecretRef" />
    ///         is a vault handle and never the secret (docs/plan/11 § The object model), so the
    ///         check goes through <see cref="IClientSecretSeam" /> — the same seam the
    ///         client-credentials grant verifies a service principal through — and refuses with one
    ///         <c>invalid_client</c> sentence whatever went wrong, so an unauthenticated caller does
    ///         not learn whether the secret was wrong, missing or unreadable.
    ///         <c>GrantsOverHttpTests.AConfidentialClientMustPresentItsSecretOnTheCodeAndRefreshGrants</c>.
    ///         Landed with the consent page (#94), which is what made a tenant-registered client's
    ///         code mintable at all.
    ///     </para>
    ///     <para>
    ///         ⚠ The grain calls — is the sign-in still live, rotate the chain — are not here but in
    ///         the passthrough (<c>IdentityEndpoints.MapToken</c> → <see cref="TokenApi" />), after
    ///         every check that could still refuse the request. A rotation followed by a refusal
    ///         would leave the client holding a retired generation, and its next honest refresh
    ///         would read as a replay and revoke the chain. The secret check is the one exception
    ///         that reads the vault: it is a read, not a rotation, and it has to happen before either.
    ///     </para>
    /// </remarks>
    /// <param name="secrets">
    ///     Where a confidential client's secret is checked — the grain's digest for one the platform
    ///     issued (#41), the vault seam for one a registration names.
    /// </param>
    /// <param name="logger">Where the reason a confidential client was refused goes — the caller never sees it.</param>
    public sealed class ValidateTokenRequest(
        TokenApi api,
        IClientResolver clients,
        FirstPartyClients firstParty,
        ClientSecretVerifier secrets,
        ILogger<ValidateTokenRequest> logger
    ) : IOpenIddictServerHandler<ValidateTokenRequestContext> {
        /// <summary>The refusal, verbatim, for a browser client's request from an origin that is not its own.</summary>
        public const string OriginNotAllowed = "The request's origin is not allowed to use this client.";

        /// <summary>
        ///     The one refusal a confidential client gets when its secret is missing, wrong or
        ///     unreadable — RFC 6749 § 5.2's <c>invalid_client</c>, and nothing more specific.
        /// </summary>
        public const string ClientNotAuthenticated = "The client could not be authenticated.";

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

            if (context.Request.IsAuthorizationCodeGrantType()
                || context.Request.IsRefreshTokenGrantType()
                || context.Request.IsDeviceCodeGrantType()) {
                await ValidateTokenBearingGrantAsync(context);

                return;
            }

            context.Reject(
                OpenIddictConstants.Errors.UnsupportedGrantType,
                "The token-exchange grant is owed — docs/plan/11 § Protocol, and TokenApi's remarks say "
                + "what it is waiting on."
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
            if ((context.AuthorizationCodePrincipal ?? context.RefreshTokenPrincipal ?? context.DeviceCodePrincipal) is not
                { } principal
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
                context.Reject(
                    OpenIddictConstants.Errors.InvalidClient,
                    "A public client must not send a client_secret."
                );

                return;
            }

            // ⚠ A confidential client authenticates, or nothing else happens — see the type's remarks.
            // Before the origin check and the grant check, because those two say what the request
            // may do, and who is asking comes first. One sentence for every failure: the caller is
            // unauthenticated, and "wrong secret" beside "no such secret" is an oracle.
            if (!client.IsPublicClient) {
                var grantName = context.Request.GrantType ?? string.Empty;

                if (string.IsNullOrEmpty(context.Request.ClientSecret)) {
                    RefuseClient(tenantId, grantName, "client-secret-missing");
                    context.Reject(OpenIddictConstants.Errors.InvalidClient, ClientNotAuthenticated);

                    return;
                }

                if (!ClientSecretVerifier.HasCredential(client)) {
                    RefuseClient(tenantId, grantName, ClientSecretVerifier.NoCredential);
                    context.Reject(OpenIddictConstants.Errors.InvalidClient, ClientNotAuthenticated);

                    return;
                }

                var verified = await secrets.VerifyAsync(
                    client,
                    context.Request.ClientSecret,
                    context.CancellationToken
                );

                if (verified.TryGetError(out var unavailable)) {
                    // ⚠ Verbatim: this is the sentence naming the missing IClientSecretSeam
                    // registration, and the log is the only place an operator will read it.
                    RefuseClient(tenantId, grantName, unavailable.Message);
                    context.Reject(OpenIddictConstants.Errors.InvalidClient, ClientNotAuthenticated);

                    return;
                }

                if (!verified.GetValueOrThrow()) {
                    RefuseClient(tenantId, grantName, "client-secret-rejected");
                    context.Reject(OpenIddictConstants.Errors.InvalidClient, ClientNotAuthenticated);

                    return;
                }
            }

            // ⚠ The browser client, from any origin but its own: refused before a grain is touched.
            // See the type's remarks — this is the write side of the cookie rule, and
            // ExtractRefreshTokenFromCookie is the read side.
            if (FirstPartyClients.IsBrowserClient(client.ClientId) && !IsFirstPartyOrigin(context)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, OriginNotAllowed);

                return;
            }

            var grant = context.Request.IsRefreshTokenGrantType()
                ? GrantType.RefreshToken
                : context.Request.IsDeviceCodeGrantType()
                    ? GrantType.DeviceAuthorization
                    : GrantType.AuthorizationCode;

            if (!client.AllowedGrants.Contains(grant)) {
                context.Reject(OpenIddictConstants.Errors.UnauthorizedClient, "This client may not use this grant.");

                return;
            }

            context.Transaction.Properties[ClientProperty] = client;
        }

        bool IsFirstPartyOrigin(ValidateTokenRequestContext context) =>
            context.Transaction.GetHttpRequest() is { } http
            && firstParty.AllowedOrigins.Contains(http.Headers.Origin.ToString(), StringComparer.Ordinal);

        void RefuseClient(Guid tenantId, string grantType, string reason) =>
            IdentityLog.TokenRequestRefused(logger, tenantId, grantType, reason);
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
    ///         ⚠
    ///         <b>
    ///             The cookie is written only for a request whose <c>Origin</c> is the browser
    ///             client's own — the second lock on the login-CSRF <see cref="ValidateTokenRequest" />
    ///             refuses first.
    ///         </b> That validator is what answers a foreign origin, so a token
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
    public sealed class MoveRefreshTokenToCookie(FirstPartyClients clients) :
        IOpenIddictServerHandler<ApplyTokenResponseContext> {
        /// <summary>The registration — before the JSON body is written.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyTokenResponseContext>()
                .UseSingletonHandler<MoveRefreshTokenToCookie>()
                .SetOrder(
                    OpenIddictServerAspNetCoreHandlers.ProcessJsonResponse<ApplyTokenResponseContext>.Descriptor.Order
                    - 1_000
                )
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
    public sealed class ValidateEndSessionRequest(TenantHint tenants, IClientResolver clients) :
        IOpenIddictServerHandler<ValidateEndSessionRequestContext> {
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

            var client = tenantId is null
                ? null
                : await clients.ResolveAsync(tenantId.Value, context.ClientId, context.CancellationToken);

            if (client is null) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");

                return;
            }

            if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri)
                && !FirstPartyClients.IsRegisteredPostLogoutRedirectUri(client, context.PostLogoutRedirectUri)) {
                context.Reject(
                    OpenIddictConstants.Errors.InvalidRequest,
                    "The 'post_logout_redirect_uri' parameter is not registered for this client."
                );

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

    // ── The authorization code's id ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Gives every authorization code a token id before it is signed, so the exchange can burn
    ///     it in <c>IAuthorizationCodeGrain</c>. RFC 6749 § 4.1.2, docs/plan/11 § Protocol.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             In degraded mode a code has no id at all, and that is the whole reason this
    ///             handler exists.
    ///         </b> OpenIddict's <c>CreateTokenEntry</c> is what sets the token id,
    ///         and it carries <c>RequireDegradedModeDisabled</c> — it writes the token store, which
    ///         this host does not have. Its <c>AttachTokenMetadata</c> stamps a fresh <c>jti</c> on
    ///         an <i>access</i> token and on nothing else. So a code minted by this server was a
    ///         self-contained envelope with no name, and "has this code been exchanged" had nothing
    ///         to key on. This handler sets <c>oi_tkn_id</c> on the code's principal; the JWT carries
    ///         it, <c>ValidateIdentityModelToken</c> puts it back on the principal at the exchange,
    ///         and <see cref="TokenApi.MintForCodeAsync" /> reads it with <c>GetTokenId()</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ Before <c>AttachTokenSubject</c>, for the reason <see cref="KeepAccessTokenToTheClosedSet" />
    ///         gives: that handler clones the principal into the security token descriptor, and a
    ///         claim added afterwards is never signed. Only the code: the refresh token and the
    ///         access token minted at the exchange are built from principals OpenIddict clones with
    ///         <c>oi_tkn_id</c> excluded, so the id names one code and nothing downstream of it.
    ///     </para>
    /// </remarks>
    public sealed class StampAuthorizationCodeId : IOpenIddictServerHandler<GenerateTokenContext> {
        /// <summary>The registration — before the subject is cloned into the token descriptor.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
                .UseSingletonHandler<StampAuthorizationCodeId>()
                .SetOrder(OpenIddictServerHandlers.Protection.AttachTokenSubject.Descriptor.Order - 600)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(GenerateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            // ⚠ The code and nothing else. The tree-wide reformat (e21006d) rewrote this test to
            // `TokenType is not null`, which stamped every token — and an access token carrying
            // `oi_tkn_id` is outside AccessTokenClaims.Permitted, since KeepAccessTokenToTheClosedSet
            // keeps OpenIddict's private claims. GrantsOverHttpTests' closed-set assertion went red
            // on the merged tree; #43's device grant found it on the way past.
            if (context.TokenType is OpenIddictConstants.TokenTypeIdentifiers.Private.AuthorizationCode
                && context.Principal is { } principal
                && string.IsNullOrEmpty(principal.GetTokenId())) {
                principal.SetTokenId(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            }

            return default;
        }
    }

    // ── The device flow — RFC 8628, #43 ────────────────────────────────────────────────────────

    /// <summary>
    ///     Validates a device authorization request: a first-party client registered for the grant,
    ///     public and presenting no secret, asking for scopes it may have — and inside the per-IP
    ///     budget for starting device sign-ins.
    /// </summary>
    /// <param name="clients">The first-party registrations — the only clients the grant is open to.</param>
    /// <param name="limiter">The per-IP counters <c>IdentityRateLimits</c> shares with the pages.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>First-party clients only, because there is no tenant to resolve anything else in.</b>
    ///         A tenant-registered client lives in its tenant's <c>IClientIndexGrain</c>, and a device
    ///         authorization request names no tenant — the person picks one on the sign-in page after
    ///         the codes exist. <c>cyc-cli</c> is the one client registered for
    ///         <see cref="GrantType.DeviceAuthorization" />; a tenant's own device client is a
    ///         registration that would need a <c>tenant</c> parameter here, and it is owed rather than
    ///         guessed at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Counted per IP before a code is drawn.</b> Every request writes a hot-tier grain
    ///         that lives ten minutes, so a caller looping on <c>/device</c> is a caller filling the
    ///         tier; <see cref="IdentityRateLimits.DeviceAuthorization" /> caps that per address. The
    ///         refusal is a <c>429</c> with <c>Retry-After</c> in the pages' own shape, written here
    ///         and marked handled, because RFC 8628 defines no error for this endpoint being busy and
    ///         <c>slow_down</c> belongs to the token endpoint.
    ///     </para>
    /// </remarks>
    public sealed class ValidateDeviceAuthorizationRequest(FirstPartyClients clients, IdentityRateLimiter limiter)
        : IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext> {
        /// <summary>The registration — after OpenIddict's own parameter and scope checks.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateDeviceAuthorizationRequestContext>()
                .UseSingletonHandler<ValidateDeviceAuthorizationRequest>()
                .SetOrder(OpenIddictServerHandlers.Device.ValidateDeviceAuthentication.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Transaction.GetHttpRequest()?.HttpContext is { } http) {
                var decision = await limiter.EvaluateAsync(
                    IdentityRateLimits.DeviceAuthorization,
                    http,
                    context.CancellationToken
                );

                if (!decision.Allowed) {
                    await IdentityRateLimits.WriteRefusalAsync(http, decision);
                    context.HandleRequest();

                    return;
                }
            }

            if (clients.Find(context.ClientId) is not { } client) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");

                return;
            }

            if (!client.AllowedGrants.Contains(GrantType.DeviceAuthorization)) {
                context.Reject(
                    OpenIddictConstants.Errors.UnauthorizedClient,
                    "This client may not use the device authorization grant."
                );

                return;
            }

            if (client.IsPublicClient && !string.IsNullOrEmpty(context.Request.ClientSecret)) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "A public client must not send a client_secret.");

                return;
            }

            if (context.Request.GetScopes().Any(x => !client.AllowedScopes.Contains(x, StringComparer.Ordinal))) {
                context.Reject(
                    OpenIddictConstants.Errors.InvalidScope,
                    "A requested scope is not allowed for this client."
                );
            }
        }
    }

    /// <summary>
    ///     Adds <c>interval</c> to a device authorization response — RFC 8628 § 3.2, which OpenIddict
    ///     does not write on its own.
    /// </summary>
    /// <remarks>
    ///     Optional in the RFC, with five seconds as the client's default when absent; said out loud
    ///     anyway, because the grain holds the same number and a client that read it knows what
    ///     <c>slow_down</c> is measured against.
    /// </remarks>
    public sealed class AttachPollingInterval : IOpenIddictServerHandler<ApplyDeviceAuthorizationResponseContext> {
        /// <summary>The registration — before the JSON body is written.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyDeviceAuthorizationResponseContext>()
                .UseSingletonHandler<AttachPollingInterval>()
                .SetOrder(
                    OpenIddictServerAspNetCoreHandlers.ProcessJsonResponse<ApplyDeviceAuthorizationResponseContext>
                        .Descriptor.Order
                    - 1_000
                )
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ApplyDeviceAuthorizationResponseContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrEmpty(context.Response.Error) && !string.IsNullOrEmpty(context.Response.DeviceCode)) {
                context.Response[OpenIddictConstants.Parameters.Interval] =
                    (long)DeviceCodes.PollingInterval.TotalSeconds;
            }

            return default;
        }
    }

    /// <summary>
    ///     Accepts every end-user verification request, because the endpoint only redirects to the
    ///     page — <c>IdentityEndpoints.MapDeviceVerification</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Nothing is checked here and nothing needs to be: the passthrough answers a redirect to
    ///     the identity app's device page and touches no grain, and the page's own API is where a
    ///     code is looked up — behind the per-IP bucket. A lookup here would be an unmetered code
    ///     guess on a <c>GET</c>.
    /// </remarks>
    public sealed class ValidateEndUserVerificationRequest
        : IOpenIddictServerHandler<ValidateEndUserVerificationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateEndUserVerificationRequestContext>()
                .UseSingletonHandler<ValidateEndUserVerificationRequest>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateEndUserVerificationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            return default;
        }
    }

    /// <summary>
    ///     The device flow's store: mints and records the two codes at generation, and answers a
    ///     device code's poll at validation — RFC 8628 § 3.5, from <c>IDeviceAuthorizationGrain</c>.
    /// </summary>
    /// <param name="devices">The grains a code names.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Required at start-up, and now serving rather than refusing.</b> OpenIddict's
    ///         post-configuration refuses the whole server when the device flow is allowed in
    ///         degraded mode and no custom <c>GenerateTokenContext</c> and <c>ValidateTokenContext</c>
    ///         handler exists — <c>OpenIddictServerOptionsTests.TheOptionsCanBeMaterialisedAtAll</c>.
    ///         Both halves act on device and user codes and nothing else; an access token, a code and a
    ///         refresh token pass through untouched.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Generation: the device code first, and the user code is the one it drew.</b>
    ///         OpenIddict generates the device code before the user code in one sign-in, so the device
    ///         code's generation draws the user code, records the authorization under it, and leaves
    ///         it in the transaction for the user code's generation to hand out. Setting the token
    ///         here is what keeps OpenIddict's own <c>GenerateIdentityModelToken</c> from minting a
    ///         JWT in its place — it leaves an attached token alone. ⚠ The user code is handed out in
    ///         its display form (<see cref="DeviceCodes.Display" />), because OpenIddict's own
    ///         formatting is switched off with token storage.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Validation: one error per RFC 8628 § 3.5 response, exactly.</b> A poll is answered
    ///         by the grain, and every outcome but approval is a rejection with the RFC's own error
    ///         code — <c>authorization_pending</c>, <c>slow_down</c>, <c>access_denied</c>,
    ///         <c>expired_token</c> — which OpenIddict's token endpoint passes through
    ///         (<c>NormalizeErrorResponse</c> rewrites only <c>invalid_token</c>). An approval becomes
    ///         the device code's principal — tenant, person and presenter — so
    ///         <see cref="ValidateTokenRequest" /> resolves the client in the tenant the person chose,
    ///         and the grain is redeemed only in the passthrough, after every check that could still
    ///         refuse, for the reason that validator gives about rotations.
    ///     </para>
    ///     <para>
    ///         A user code presented to OpenIddict's own verification endpoint is refused quietly —
    ///         the endpoint does not require one, so the refusal only means OpenIddict attaches no
    ///         principal — because the lookup is the page API's, behind the per-IP bucket.
    ///     </para>
    /// </remarks>
    public sealed class StoreDeviceCodes(DeviceFlow devices)
        : IOpenIddictServerHandler<GenerateTokenContext>, IOpenIddictServerHandler<ValidateTokenContext> {
        /// <summary>Where the device code's generation leaves the user code it drew.</summary>
        public const string UserCodeProperty = "cybercloud.device.user-code";

        /// <summary>What a device code this server cannot find answers — RFC 6749 § 5.2's <c>invalid_grant</c>.</summary>
        public const string UnknownDeviceCode = "The device code is not valid.";

        /// <summary>The generation half.</summary>
        public static OpenIddictServerHandlerDescriptor GenerateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
                .UseSingletonHandler<StoreDeviceCodes>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <summary>The validation half.</summary>
        public static OpenIddictServerHandlerDescriptor ValidateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<StoreDeviceCodes>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(GenerateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            switch (context.TokenType) {
                case OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode: {
                    var principal = context.Principal;
                    var begun = await devices.BeginAsync(
                        new() {
                            ClientId = context.ClientId ?? string.Empty,
                            Scopes = [.. principal?.GetScopes() ?? []],
                            // ⚠ The configured lifetime, not the principal's expiry: OpenIddict
                            // stamps that from its own TimeProvider, and the grain measures the
                            // ten minutes from the silo's clock (DeviceAuthorizationRequest.Lifetime).
                            Lifetime = DeviceCodes.Lifetime,
                            Interval = DeviceCodes.PollingInterval
                        },
                        context.CancellationToken
                    );

                    if (begun.TryGetError(out var error)) {
                        context.Reject(
                            OpenIddictConstants.Errors.ServerError,
                            "The device sign-in could not be started. Try again. " + error.Code
                        );

                        return;
                    }

                    var (deviceCode, userCode) = begun.GetValueOrThrow();
                    context.Transaction.Properties[UserCodeProperty] = userCode;
                    context.Token = deviceCode;

                    return;
                }

                case OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode:
                    if (context.Transaction.Properties.TryGetValue(UserCodeProperty, out var drawn)
                        && drawn is string code) {
                        // ⚠ Formatted here: OpenIddict's own formatting is off in degraded mode —
                        // IdentityHostOpenIddict says why — so the dash is ours to add.
                        context.Token = DeviceCodes.Display(code);
                    } else {
                        context.Reject(
                            OpenIddictConstants.Errors.ServerError,
                            "A user code was asked for with no device code drawn before it."
                        );
                    }

                    return;
            }
        }

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.ValidTokenTypes.Count == 0) {
                return;
            }

            if (context.ValidTokenTypes.All(static x => x is OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode)) {
                context.Reject(
                    OpenIddictConstants.Errors.InvalidToken,
                    "User codes are looked up by the verification page, not by this endpoint."
                );

                return;
            }

            if (!context.ValidTokenTypes.All(static x => x is OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode)) {
                return;
            }

            var poll = await devices.PollAsync(context.Token);

            switch (poll.Outcome) {
                case DevicePollOutcome.Pending:
                    context.Reject(
                        OpenIddictConstants.Errors.AuthorizationPending,
                        "The person has not answered yet. Poll again after the interval."
                    );

                    return;
                case DevicePollOutcome.SlowDown:
                    context.Reject(
                        OpenIddictConstants.Errors.SlowDown,
                        "Polled inside the interval. The interval is now "
                        + ((long)poll.Interval.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                        + " seconds."
                    );

                    return;
                case DevicePollOutcome.Denied:
                    context.Reject(OpenIddictConstants.Errors.AccessDenied, "The person declined the sign-in.");

                    return;
                case DevicePollOutcome.Expired:
                    context.Reject(
                        OpenIddictConstants.Errors.ExpiredToken,
                        "The device code expired. Start the sign-in again."
                    );

                    return;
                // ⚠ A spent code is carried through as well — DevicePollOutcome.Redeemed says why:
                // the passthrough's redemption is what refuses it, and what revokes the session the
                // first redemption opened.
                case DevicePollOutcome.Approved or DevicePollOutcome.Redeemed when poll.Approval is { } approval:
                    context.Principal = Principal(approval, poll);

                    return;
                default:
                    context.Reject(OpenIddictConstants.Errors.InvalidGrant, UnknownDeviceCode);

                    return;
            }
        }

        /// <summary>
        ///     The device code's principal once the person approved: who, in which tenant, for which
        ///     client and scopes. Nothing here reaches a token — <c>TokenApi.MintForDeviceCodeAsync</c>
        ///     builds the access token from the grain's approval, as the code exchange builds it from
        ///     the code's facts.
        /// </summary>
        static ClaimsPrincipal Principal(DeviceApproval approval, DevicePoll poll) {
            var identity = new ClaimsIdentity(
                "CyberCloud.DeviceCode",
                AccessTokenClaims.Subject,
                "urn:cybercloud:roles-are-not-in-the-token"
            );

            identity.AddClaim(new Claim(AccessTokenClaims.Subject, approval.UserId.ToString("N", CultureInfo.InvariantCulture)));
            identity.AddClaim(new Claim(AccessTokenClaims.SubjectType, SubjectTypes.User));
            identity.AddClaim(
                new Claim(AccessTokenClaims.TenantId, approval.TenantId.ToString("N", CultureInfo.InvariantCulture))
            );

            var principal = new ClaimsPrincipal(identity);

            principal.SetTokenType(OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode);
            principal.SetPresenters(poll.ClientId);
            principal.SetScopes(poll.Scopes);

            return principal;
        }
    }

    // ── /revoke — RFC 7009, #43 ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Validates a revocation request: a client that names itself, a refresh token, and — for a
    ///     confidential client — its secret.
    /// </summary>
    /// <param name="clients">The client resolver.</param>
    /// <param name="secrets">Where a confidential client's secret is checked, issued or vaulted.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Refresh tokens only, and an access token is told so rather than quietly
    ///         accepted.</b> <see cref="AccessTokenPolicy.AccessTokensAreRevocable" /> is
    ///         <see langword="false" />: the gateway validates a JWT locally for its ten minutes and
    ///         nothing here can reach into that. RFC 7009 § 2.2.1 gives the answer for a server that
    ///         cannot revoke a type — <c>unsupported_token_type</c> — and giving it is what keeps this
    ///         endpoint from being the "revocation endpoint that silently did nothing to an
    ///         already-issued access token" <c>IdentityHostOpenIddict</c> refused to publish.
    ///     </para>
    ///     <para>
    ///         Ordered after OpenIddict's <c>ValidateAuthentication</c>, which has decrypted the token
    ///         and put it on the context; OpenIddict's own <c>ValidateAuthorizedParty</c> after this
    ///         holds the presenter to the <c>client_id</c>, so a client cannot revoke another
    ///         client's chain. <c>client_id</c> is required here because that check is skipped for a
    ///         request that names none.
    ///     </para>
    /// </remarks>
    public sealed class ValidateRevocationRequest(IClientResolver clients, ClientSecretVerifier secrets)
        : IOpenIddictServerHandler<ValidateRevocationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateRevocationRequestContext>()
                .UseSingletonHandler<ValidateRevocationRequest>()
                .SetOrder(OpenIddictServerHandlers.Revocation.ValidateAuthentication.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateRevocationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrEmpty(context.ClientId)) {
                context.Reject(OpenIddictConstants.Errors.InvalidRequest, "The 'client_id' parameter is required.");

                return;
            }

            if (context.GenericTokenPrincipal is not { } principal) {
                context.Reject(OpenIddictConstants.Errors.InvalidToken, "The token could not be read.");

                return;
            }

            if (!principal.HasTokenType(OpenIddictConstants.TokenTypeIdentifiers.RefreshToken)) {
                context.Reject(
                    OpenIddictConstants.Errors.UnsupportedTokenType,
                    "Only refresh tokens are revocable here. An access token lives ten minutes and is "
                    + "not revocable by design — revoke the refresh token, which ends the session."
                );

                return;
            }

            if (!Guid.TryParseExact(principal.GetClaim(AccessTokenClaims.TenantId), "N", out var tenantId)
                || await clients.ResolveAsync(tenantId, context.ClientId, context.CancellationToken) is not { } client) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, "The client is not registered.");

                return;
            }

            if (client.IsPublicClient) {
                if (!string.IsNullOrEmpty(context.Request.ClientSecret)) {
                    context.Reject(OpenIddictConstants.Errors.InvalidClient, "A public client must not send a client_secret.");
                }

                return;
            }

            var verified = await secrets.VerifyAsync(client, context.Request.ClientSecret, context.CancellationToken);

            if (!verified.IsSuccess || !verified.GetValueOrThrow()) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, ValidateTokenRequest.ClientNotAuthenticated);
            }
        }
    }

    /// <summary>
    ///     Revokes the token session a refresh token belongs to — which is what ends the chain, since
    ///     the refresh token is only the session grain's handle in OpenIddict's envelope.
    /// </summary>
    /// <param name="grains">The cluster. ⚠ Every reference goes through <c>ForTenant</c>.</param>
    /// <param name="logger">Where the revocation is recorded.</param>
    /// <remarks>
    ///     ⚠ OpenIddict's own <c>RevokeToken</c> carries <c>RequireDegradedModeDisabled</c> — it marks
    ///     a row in a token store this host does not have — so without this handler the endpoint
    ///     would answer <c>200</c> and revoke nothing. The token's <c>sid</c> is the token session
    ///     (<c>TokenApi</c>'s shape), and revoking it with <see cref="RevocationReason.RevokedByClient" />
    ///     makes the next refresh of any generation of the chain <c>invalid_grant</c>.
    /// </remarks>
    public sealed class RevokeTokenSession(IGrainFactory grains, ILogger<RevokeTokenSession> logger)
        : IOpenIddictServerHandler<HandleRevocationRequestContext> {
        /// <summary>The registration — after OpenIddict attaches the principal.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<HandleRevocationRequestContext>()
                .UseSingletonHandler<RevokeTokenSession>()
                .SetOrder(OpenIddictServerHandlers.Revocation.AttachPrincipal.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(HandleRevocationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.GenericTokenPrincipal is not { } principal
                || !Guid.TryParseExact(principal.GetClaim(AccessTokenClaims.TenantId), "N", out var tenantId)
                || !Guid.TryParseExact(principal.GetClaim(AccessTokenClaims.SessionId), "N", out var sessionId)) {
                // RFC 7009 § 2.2: a token the server cannot act on is answered as revoked.
                return;
            }

            await grains.ForTenant(TenantHint.Qualifier(tenantId))
                .GetGrain<ISessionGrain>(GrainKeys.Session(sessionId))
                .RevokeAsync(RevocationReason.RevokedByClient);

            GrantLog.RevokedByClient(logger, tenantId, sessionId);
        }
    }
}
