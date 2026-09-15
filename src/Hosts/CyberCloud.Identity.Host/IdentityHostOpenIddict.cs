using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
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
    ///         ⚠ Like the other four, the page behind it is owed — see
    ///         <see cref="IdentityEndpoints" />, which says where the pages live and which of them
    ///         exist. A path with a passthrough and no page is a 404; a flow allowed with no path at
    ///         all is a host that does not start.
    ///     </para>
    /// </remarks>
    public const string EndUserVerificationPath = "/device/verify";

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
    ///             <b>No token revocation endpoint for access tokens.</b>
    ///             <see cref="AccessTokenPolicy.AccessTokensAreRevocable" /> is
    ///             <see langword="false" />; revocation happens by revoking the <i>session</i>, which
    ///             stops the refresh chain. Publishing a revocation endpoint that silently did
    ///             nothing to an already-issued access token would be worse than not having one.
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
    ///             The signing and encryption keys here are ephemeral, and that is a hole with a
    ///             name.
    ///         </b> docs/plan/11 § Protocol wants "a rotating key set (30-day rotation, both keys
    ///         published for 60)", which needs the keys to live somewhere every silo can read and
    ///         somewhere a rotation job can write — that is <c>CyberCloud.Vault</c> (docs/plan/18),
    ///         which does not exist. Ephemeral keys mean every process restart invalidates every
    ///         issued token, which is survivable in development and is not a production
    ///         configuration. <see cref="AccessTokenPolicy.SigningKeyRotation" /> and
    ///         <see cref="AccessTokenPolicy.SigningKeyOverlap" /> are the numbers whoever wires the
    ///         vault has to honour.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Degraded mode, which is ADR-015's "we own the stores" applied to the protocol
    ///             layer.
    ///         </b> OpenIddict's built-in request validation goes through its core managers, and a
    ///         manager needs a store implementation per object type — an application store with a
    ///         <c>client_id</c> index this tenancy does not have yet, a token store, an
    ///         authorization store. Without them the server threw <i>"The core services must be
    ///         registered"</i> on the first token request, which is the state this host shipped in
    ///         for as long as nothing called <c>/token</c>. The degraded mode turns those checks off
    ///         and requires a custom validator per endpoint instead; <see cref="DegradedModeHandlers" />
    ///         is the set, and <c>OpenIddictServerOptionsTests.EveryEndpointHasTheValidatorDegradedModeDemands</c>
    ///         keeps it complete, because the failure for a missing one is a <c>500</c> at request
    ///         time and not a refusal at start-up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The signing key is ES256, by name.</b> <see cref="AccessTokenPolicy.SigningAlgorithm" />
    ///         is the one algorithm the gateway is told to accept, and <c>AddEphemeralSigningKey()</c>
    ///         with no argument mints RSA — a server that published an RS256 key beside a contract
    ///         saying ES256 would have every token refused by a validator that honoured the contract.
    ///         The algorithm is passed from the contract so the two cannot disagree.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddIdentityHostOpenIddict(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TokenApi>();

        // ⚠ The issuer comes from IdentityHostOptions, and only when it is set. OpenIddict infers
        // one from the request otherwise, which is right on a developer's 127.0.0.1:port and wrong
        // behind a proxy — IdentityHostOptions.Issuer carries the argument. Configured through the
        // options pipeline rather than read here because the section is bound by AddIdentityHostApi,
        // and this method must not care which order the two are called in.
        services.AddOptions<OpenIddictServerOptions>()
            .Configure<IOptions<IdentityHostOptions>>((options, host) => {
                    if (!string.IsNullOrEmpty(host.Value.Issuer)) {
                        options.Issuer = new Uri(host.Value.Issuer, UriKind.Absolute);
                    }
                }
            );

        // ⚠ Plain HTTP is accepted in Development and nowhere else. OpenIddict refuses a token
        // request that did not arrive over TLS, which is correct behind Envoy (docs/plan/10 § Shape
        // terminates TLS there) and impossible on a developer's or a test's 127.0.0.1:port. Keyed on
        // the environment rather than on a setting so that no production configuration can turn it
        // off by mistake — a value somebody can set is a value somebody will set.
        services.AddOptions<OpenIddictServerAspNetCoreOptions>()
            .Configure<IHostEnvironment>((options, environment) =>
                options.DisableTransportSecurityRequirement = environment.IsDevelopment()
            );

        services
            .AddOpenIddict()
            .AddServer(options => {
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
                        .SetEndSessionEndpointUris(EndSessionPath);

                    // docs/plan/11 § Protocol's flow table, and nothing outside it.
                    options
                        .AllowAuthorizationCodeFlow()
                        .AllowRefreshTokenFlow()
                        .AllowClientCredentialsFlow()
                        .AllowDeviceAuthorizationFlow();

                    options.RequireProofKeyForCodeExchange();

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

                    // ⚠ Development keys. See the remarks above — the production key set is the
                    // vault's, and it does not exist yet. The signing algorithm is the contract's,
                    // so the published key set and the gateway's pinned algorithm are one value.
                    options.AddEphemeralEncryptionKey();
                    options.AddEphemeralSigningKey(AccessTokenPolicy.SigningAlgorithm);

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
