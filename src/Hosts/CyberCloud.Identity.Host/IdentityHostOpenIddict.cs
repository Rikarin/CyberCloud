using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace CyberCloud.Identity.Host;

/// <summary>
///     The OAuth 2.1 / OIDC authorization server. ADR-015, docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ADR-015: "OpenIddict is a library: it handles the protocol, we own the stores, and the
///         stores are grains." This file is the protocol half — the endpoints, the flows, and the
///         lifetimes. The stores are <c>IApplicationGrain</c>, <c>IServicePrincipalGrain</c> and
///         <c>ISessionGrain</c>.
///     </para>
///     <para>
///         ⚠ <b>Pinned at 7.6.0, not docs/plan/02 § ADR-015's 7.3.0.</b> That pin is justified in the
///         document as "newest stable, never a preview", and 7.4.0, 7.5.0 and 7.6.0 are all published
///         stable while 8.0 is still preview — so the document's own reasoning points at 7.6.0. The
///         correction is recorded in <c>Directory.Packages.props</c>.
///     </para>
/// </remarks>
public static class IdentityHostOpenIddict {
    /// <summary>The token endpoint. docs/plan/11 § Hosts.</summary>
    public const string TokenPath = "/token";

    /// <summary>The authorization endpoint.</summary>
    public const string AuthorizationPath = "/authorize";

    /// <summary>The UserInfo endpoint.</summary>
    public const string UserInfoPath = "/userinfo";

    /// <summary>The device-authorization endpoint — <c>cyc login</c> on a headless box.</summary>
    public const string DeviceAuthorizationPath = "/device";

    /// <summary>
    ///     Where the person on the other machine types the user code.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Its absence was a start-up failure, not a missing feature.</b> OpenIddict's own
    ///     post-configuration refuses the whole server with "The end-user verification endpoint must
    ///     be enabled to use the device authorization flow" when
    ///     <c>AllowDeviceAuthorizationFlow</c> is set and this is not — so resolving
    ///     <c>IOptions&lt;OpenIddictServerOptions&gt;</c> threw, and every OIDC request with it.
    ///     Nothing noticed because nothing called <see cref="AddIdentityHostOpenIddict" />; the test
    ///     project asserted the path constants and the <em>other</em> registration.
    ///     <para>
    ///         This is the <c>verification_uri</c> the device prints, and since #43 its passthrough
    ///         redirects to the identity app's device page (<c>/device-code</c>), carrying the user
    ///         code when the link had one — <c>IdentityEndpoints.MapDeviceVerification</c>. The page
    ///         takes the code, the sign-in and the answer; this path only has to exist and be short.
    ///     </para>
    /// </remarks>
    public const string EndUserVerificationPath = "/device/verify";

    /// <summary>
    ///     The revocation endpoint — RFC 7009, for refresh tokens only. <c>cyc logout</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Enabled by #43 against this file's own "no token revocation endpoint" rule, which was
    ///     about <i>access</i> tokens and still holds: an access token presented here is answered
    ///     <c>unsupported_token_type</c> (RFC 7009 § 2.2.1), never quietly accepted, and a refresh
    ///     token revokes the token session behind it — <c>DegradedModeHandlers.ValidateRevocationRequest</c>.
    /// </remarks>
    public const string RevocationPath = "/revoke";

    /// <summary>The end-session endpoint.</summary>
    public const string EndSessionPath = "/logout";

    /// <summary>
    ///     Registers the authorization server.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What is deliberately NOT enabled, in order of how much damage each would do:</b>
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>No introspection endpoint.</b> <see cref="AccessTokenPolicy.SupportsIntrospection" />
    ///             is <see langword="false" /> and this is where that becomes true of the running
    ///             server. docs/plan/11 § Sessions and revocation: "an introspection call per request
    ///             would put the identity system on the hot path of every request, which is precisely
    ///             what a short token is for." An endpoint that exists gets used, so the correct
    ///             implementation is not to publish one — the gateway validates the JWT locally
    ///             against the published key set.
    ///         </item>
    ///         <item>
    ///             <b>No resource-owner password grant.</b> Removed in OAuth 2.1, and it defeats MFA
    ///             — docs/plan/11 § Protocol. <see cref="GrantType" /> has no member for it either,
    ///             so an application registration cannot ask.
    ///         </item>
    ///         <item>
    ///             <b>No implicit and no hybrid flow.</b> Same section: "the only interactive flow" is
    ///             authorization code with PKCE.
    ///         </item>
    ///         <item>
    ///             <b>No revocation of access tokens.</b>
    ///             <see cref="AccessTokenPolicy.AccessTokensAreRevocable" /> is
    ///             <see langword="false" />; revocation happens by revoking the <i>session</i>, which
    ///             stops the refresh chain. Publishing a revocation endpoint that silently did
    ///             nothing to an already-issued access token would be worse than not having one — so
    ///             <see cref="RevocationPath" /> (#43, <c>cyc logout</c>) takes refresh tokens and
    ///             answers an access token <c>unsupported_token_type</c>, out loud.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>PKCE is required for every client, not only public ones.</b> OAuth 2.1 requires it
    ///         for public clients and recommends it for confidential ones; requiring it universally
    ///         costs a confidential client nothing and closes authorization-code injection for the
    ///         case where a confidential client's secret has leaked but its code has not.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The signing and encryption keys are ephemeral unless a development key directory
    ///             is configured, and either way that is a hole with a name.
    ///         </b> docs/plan/11 § Protocol wants "a rotating key set (30-day rotation, both keys
    ///         published for 60)", which needs the keys to live somewhere every silo can read and
    ///         somewhere a rotation job can write — that is <c>CyberCloud.Vault</c> (docs/plan/18),
    ///         which does not exist. Ephemeral keys mean every process restart invalidates every
    ///         issued token; <see cref="DevelopmentKeyFile" /> keeps a development run's keys on
    ///         disk so a restart does not sign everybody out, and refuses to do so anywhere else.
    ///         <see cref="AccessTokenPolicy.SigningKeyRotation" /> and
    ///         <see cref="AccessTokenPolicy.SigningKeyOverlap" /> are the numbers whoever wires the
    ///         vault has to honour.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Degraded mode, which is ADR-015's "we own the stores" applied to the protocol
    ///             layer.
    ///         </b> OpenIddict's built-in request validation goes through its core managers, and a
    ///         manager needs a store implementation per object type — an application store (the
    ///         <c>client_id</c> index it would read through, <c>IClientIndexGrain</c>, exists now;
    ///         the store over it does not), a token store, an authorization store. Without them the
    ///         server threw
    ///         <i>
    ///             "The core services must be
    ///             registered"
    ///         </i> on the first token request, which is the state this host shipped in
    ///         for as long as nothing called <c>/token</c>. The degraded mode turns those checks off
    ///         and requires a custom validator per endpoint instead; <see cref="DegradedModeHandlers" />
    ///         is the set, and <c>OpenIddictServerOptionsTests.EveryEndpointHasTheValidatorDegradedModeDemands</c>
    ///         keeps it complete, because the failure for a missing one is a <c>500</c> at request
    ///         time and not a refusal at start-up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The signing key is ES256, by name.</b> <see cref="AccessTokenPolicy.SigningAlgorithm" />
    ///         is the one algorithm the gateway is told to accept, and an ephemeral signing key
    ///         minted with no algorithm named is RSA — a server that published an RS256 key beside
    ///         a contract saying ES256 would have every token refused by a validator that honoured
    ///         the contract. <see cref="IdentityHostKeys" /> passes the algorithm from the contract
    ///         on both of its paths so the two cannot disagree.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The CORS policy for <c>/token</c> is registered beside the server and applied
    ///             by the endpoints.
    ///         </b> The portal calls <c>/token</c> and <c>/logout</c> cross-origin
    ///         with credentials — docs/plan/10 § Authentication inputs, and docs/plan/20 § SSR says
    ///         the render process holds no tokens, so nothing may proxy the call — and the allowed
    ///         origins are derived from the browser client's redirect URIs
    ///         (<see cref="FirstPartyClients.AllowedOrigins" />) so there is no second list to
    ///         drift. Every other origin, the gateway's included, gets no CORS headers at all;
    ///         <c>CorsPolicyTests</c> holds both halves.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddIdentityHostOpenIddict(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DeviceFlow>();
        services.TryAddSingleton<TokenApi>();

        // ⚠ The keys, through an IConfigureOptions rather than the builder's AddEphemeral* calls —
        // which keys depends on IdentityHostOptions.DevelopmentKeyDirectory and the environment,
        // and the builder's lambda sees neither. DevelopmentKeyFile carries the environment gate and
        // the reason both keys, not only the signing one, have to persist.
        services.TryAddSingleton<DevelopmentKeyFile>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, IdentityHostKeys>();

        // ⚠ The issuer comes from IdentityHostOptions, and only when it is set. OpenIddict infers
        // one from the request otherwise, which is right on a developer's 127.0.0.1:port and wrong
        // behind a proxy — IdentityHostOptions.Issuer carries the argument. Configured through the
        // options pipeline rather than read here because the section is bound by AddIdentityHostApi,
        // and this method must not care which order the two are called in.
        services.AddOptions<OpenIddictServerOptions>()
            .Configure<IOptions<IdentityHostOptions>>(static (options, host) => {
                    if (!string.IsNullOrEmpty(host.Value.Issuer)) {
                        options.Issuer = new(host.Value.Issuer, UriKind.Absolute);
                    }
                }
            );

        // ⚠ Plain HTTP is accepted in Development and nowhere else. OpenIddict refuses a token
        // request that did not arrive over TLS, which is correct behind Envoy (docs/plan/10 § Shape
        // terminates TLS there) and impossible on a developer's or a test's 127.0.0.1:port. Keyed on
        // the environment rather than on a setting so that no production configuration can turn it
        // off by mistake — a value somebody can set is a value somebody will set.
        services.AddOptions<OpenIddictServerAspNetCoreOptions>()
            .Configure<IHostEnvironment>(static (options, environment) =>
                options.DisableTransportSecurityRequirement = environment.IsDevelopment()
            );

        // The first-party browser origins, with credentials, on the two paths the endpoints mark
        // with RequireCors — and no default policy, so an endpoint that does not ask gets nothing.
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<FirstPartyClients>(static (cors, clients) => cors.AddPolicy(
                    FirstPartyClients.CorsPolicy,
                    policy => policy
                        .WithOrigins([.. clients.AllowedOrigins])
                        .WithMethods("POST", "GET")
                        .WithHeaders("Content-Type")
                        .AllowCredentials()
                )
            );

        services
            .AddOpenIddict()
            .AddServer(static options => {
                    options.EnableDegradedMode();

                    foreach (var handler in DegradedModeHandlers.All) {
                        options.AddEventHandler(handler);
                    }

                    options
                        .SetAuthorizationEndpointUris(AuthorizationPath)
                        .SetTokenEndpointUris(TokenPath)
                        .SetUserInfoEndpointUris(UserInfoPath)
                        .SetDeviceAuthorizationEndpointUris(DeviceAuthorizationPath)
                        .SetEndUserVerificationEndpointUris(EndUserVerificationPath)
                        .SetEndSessionEndpointUris(EndSessionPath)
                        .SetRevocationEndpointUris(RevocationPath);

                    // The device flow's codes (#43): both lifetimes, one number. ⚠ NOT the user-code
                    // charset, length or display format, though the builder offers all three —
                    // OpenIddict's post-configuration clears them whenever token storage is off,
                    // which degraded mode implies ("custom event handlers must … return a user code
                    // that can be used and entered by a human"). They were set here first and the
                    // first code on the wire came back unformatted; DeviceCodes owns all three.
                    options
                        .SetDeviceCodeLifetime(DeviceCodes.Lifetime)
                        .SetUserCodeLifetime(DeviceCodes.Lifetime);

                    // docs/plan/11 § Protocol's flow table, and nothing outside it.
                    options
                        .AllowAuthorizationCodeFlow()
                        .AllowRefreshTokenFlow()
                        .AllowClientCredentialsFlow()
                        .AllowDeviceAuthorizationFlow();

                    options.RequireProofKeyForCodeExchange();

                    // ⚠ S256 only. OpenIddict advertises `plain` beside it by default, and a plain
                    // challenge is the verifier itself — a client that used it would send the one
                    // value PKCE exists to keep off the wire, in the /authorize query that lands in
                    // every access log. The discovery document says S256 and nothing else, so a
                    // client library that reads it picks the right one without being told.
                    options.Configure(static server => {
                            server.CodeChallengeMethods.Clear();
                            server.CodeChallengeMethods.Add(OpenIddictConstants.CodeChallengeMethods.Sha256);
                        }
                    );

                    // ⚠ Registered, not merely declared. OpenIddict validates every requested scope
                    // against this list — in degraded mode too — and answers invalid_scope for one it
                    // has not been told about. Scopes declared the four below for as long as nothing
                    // called /token, and the first client-credentials request for `cyc.api` was
                    // refused with "The specified 'scope' is invalid" (ID2052); TenantOverHttpTests
                    // found it. Read from the nested class so the list has one home.
                    options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, Scopes.Api);

                    // ⚠ The ten minutes that ARE the revocation story — docs/plan/11 § Sessions and
                    // revocation. Read from the shared contract rather than written here, so the
                    // gateway and this server cannot drift on the one number both depend on.
                    options.SetAccessTokenLifetime(AccessTokenPolicy.AccessTokenLifetime);
                    options.SetRefreshTokenLifetime(AccessTokenPolicy.RefreshTokenLifetime);

                    // Five minutes for a code — AccessTokenPolicy.AuthorizationCodeLifetime says why
                    // that number, and IAuthorizationCodeGrain reads the same one so the one-time-use
                    // record cannot expire before the code it guards. ⚠ One-time use is not
                    // OpenIddict's here (no token store in degraded mode): StampAuthorizationCodeId
                    // gives the code an id and TokenApi.MintForCodeAsync burns it in the hot tier.
                    options.SetAuthorizationCodeLifetime(AccessTokenPolicy.AuthorizationCodeLifetime);

                    // ⚠ The claims a code and a refresh token carry that OpenIddict does not know by
                    // name, registered so it keeps them through its own claim mapping. The two
                    // `cyc:` claims never reach an access token — AccessTokenPrincipalFactory.
                    options.RegisterClaims(
                        AccessTokenClaims.Subject,
                        AccessTokenClaims.SubjectType,
                        AccessTokenClaims.TenantId,
                        AccessTokenClaims.SessionId,
                        AccessTokenClaims.AuthenticationTime,
                        AccessTokenClaims.AuthenticationMethods,
                        AccessTokenClaims.Scope,
                        AccessTokenPrincipalFactory.RefreshHandleClaim,
                        AccessTokenPrincipalFactory.InteractiveSessionClaim,
                        OpenIddictConstants.Claims.Email,
                        OpenIddictConstants.Claims.Name
                    );

                    // ⚠ Access tokens are NOT encrypted, deliberately. OpenIddict encrypts by
                    // default, which makes a token opaque to anything but OpenIddict's own validation
                    // handler — and docs/plan/11 § Protocol says "access tokens are JWTs", because
                    // the gateway validates them locally against the published key set rather than
                    // calling back here. An encrypted token would force the introspection call the
                    // same document forbids.
                    options.DisableAccessTokenEncryption();

                    options
                        .UseAspNetCore()
                        .EnableAuthorizationEndpointPassthrough()
                        .EnableTokenEndpointPassthrough()
                        .EnableUserInfoEndpointPassthrough()
                        .EnableEndUserVerificationEndpointPassthrough()
                        .EnableEndSessionEndpointPassthrough();
                }
            );

        return services;
    }

    /// <summary>
    ///     The scopes this server issues. Kept small on purpose.
    /// </summary>
    /// <remarks>
    ///     ⚠ There is no <c>admin</c> or <c>write</c> scope, and their absence is the same decision as
    ///     the missing role claim. A scope in a token is a permission in a token; what a subject may
    ///     do is a ReBAC <c>Check</c> at the point of use. These three say what <i>kind</i> of thing
    ///     the token is for, which is what a scope is actually for.
    /// </remarks>
    public static class Scopes {
        /// <summary>OIDC's own. Required for an id token.</summary>
        public const string OpenId = OpenIddictConstants.Scopes.OpenId;

        /// <summary>The subject's profile, for <c>/userinfo</c>.</summary>
        public const string Profile = OpenIddictConstants.Scopes.Profile;

        /// <summary>Offline access — the condition for a refresh token being issued at all.</summary>
        public const string OfflineAccess = OpenIddictConstants.Scopes.OfflineAccess;

        /// <summary>The Cyber Cloud control-plane API.</summary>
        public const string Api = "cyc.api";
    }
}
