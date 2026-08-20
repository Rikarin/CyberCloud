using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

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
    ///         ⚠ <b>The signing and encryption keys here are ephemeral, and that is a hole with a
    ///         name.</b> docs/plan/11 § Protocol wants "a rotating key set (30-day rotation, both keys
    ///         published for 60)", which needs the keys to live somewhere every silo can read and
    ///         somewhere a rotation job can write — that is <c>CyberCloud.Vault</c> (docs/plan/18),
    ///         which does not exist. Ephemeral keys mean every process restart invalidates every
    ///         issued token, which is survivable in development and is not a production
    ///         configuration. <see cref="AccessTokenPolicy.SigningKeyRotation" /> and
    ///         <see cref="AccessTokenPolicy.SigningKeyOverlap" /> are the numbers whoever wires the
    ///         vault has to honour.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddIdentityHostOpenIddict(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOpenIddict()
            .AddServer(
                options => {
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

                    // ⚠ The ten minutes that ARE the revocation story — docs/plan/11 § Sessions and
                    // revocation. Read from the shared contract rather than written here, so the
                    // gateway and this server cannot drift on the one number both depend on.
                    options.SetAccessTokenLifetime(AccessTokenPolicy.AccessTokenLifetime);
                    options.SetRefreshTokenLifetime(AccessTokenPolicy.RefreshTokenLifetime);

                    // ⚠ Development keys. See the remarks above — the production key set is the
                    // vault's, and it does not exist yet.
                    options.AddEphemeralEncryptionKey();
                    options.AddEphemeralSigningKey();

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
