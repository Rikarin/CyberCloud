using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The two clients this platform ships — the portal and the CLI — as static registrations that
///     exist in every tenant without being stored in any. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Static, not seeded, and the reason is the shape of the client index.</b>
///         <c>IClientIndexGrain</c> is keyed by (tenant, <c>client_id</c>), so a first-party client
///         stored as a grain would need a registration in every tenant: a seed at host start for
///         the platform tenant plus a seed per sign-up, two code paths, and a "what if the seed is
///         missing" state that every <c>/authorize</c> would have to answer for. Azure's own portal
///         is a first-party application with a well-known id rather than a per-tenant registration,
///         and that is the shape here. The records are still <see cref="ApplicationRegistration" />,
///         so the consent page's "render <see cref="ApplicationRegistration.DisplayName" /> from the
///         registration" rule and the exact-match redirect rule apply to them unchanged.
///     </para>
///     <para>
///         ⚠ <b>Consulted before the tenant's index, never after.</b> <c>ClientResolver</c> asks this
///         list first, so no tenant can register an application called <c>cyc-portal</c> and have
///         the platform's own client id resolve to it — a shadowing that would let a tenant's
///         redirect URI receive codes minted for the portal.
///         <c>AuthorizeHandlerTests.ATenantRegisteredClientCannotShadowAFirstPartyId</c> is the
///         assertion.
///     </para>
///     <para>
///         The redirect URIs are the one thing a deployment decides
///         (<see cref="IdentityHostOptions.Clients" />); the development run's values are filled in
///         here when nothing is configured and the environment is Development, so a fresh
///         <c>dotnet run</c> needs no configuration and a production host with none has a portal
///         that cannot sign in rather than one that signs into <c>localhost</c>.
///     </para>
/// </remarks>
public sealed class FirstPartyClients {
    /// <summary>The portal's <c>client_id</c>. A browser client: its refresh token lives in a cookie.</summary>
    public const string Portal = "cyc-portal";

    /// <summary>The CLI's <c>client_id</c>. A native client: its refresh token stays in the body.</summary>
    public const string Cli = "cyc-cli";

    /// <summary>What the portal's redirect URI is on the development run, when nothing configures it.</summary>
    public const string DevelopmentPortalRedirectUri = "http://localhost:4200/auth/callback";

    /// <summary>Where the portal lands after sign-out on the development run.</summary>
    public const string DevelopmentPortalPostLogoutRedirectUri = "http://localhost:4200/";

    /// <summary>
    ///     The CLI's loopback redirect — RFC 8252 § 7.3, where the port is whatever the CLI could
    ///     bind and is therefore not part of the registration.
    /// </summary>
    public const string LoopbackRedirectUri = "http://127.0.0.1/callback";

    /// <summary>The policy name <c>/token</c> and <c>/logout</c> carry, and nothing else does.</summary>
    public const string CorsPolicy = "FirstPartyBrowsers";

    static readonly Guid PortalApplicationId = new("c1c0c1c0-0000-4000-8000-000000000001");
    static readonly Guid CliApplicationId = new("c1c0c1c0-0000-4000-8000-000000000002");

    /// <summary>Builds the two registrations from the options and the environment.</summary>
    /// <param name="options">Where the redirect URIs come from.</param>
    /// <param name="environment">Decides whether the development defaults apply.</param>
    public FirstPartyClients(IOptions<IdentityHostOptions> options, IHostEnvironment environment) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var clients = options.Value.Clients;
        var development = environment.IsDevelopment();

        var portal = new ApplicationRegistration {
            ApplicationId = PortalApplicationId,
            TenantId = Guid.Empty,
            ClientId = Portal,
            DisplayName = "Cyber Cloud portal",
            IsPublicClient = true,
            RedirectUris = Configured(clients.Portal.RedirectUris, development, DevelopmentPortalRedirectUri),
            PostLogoutRedirectUris = Configured(
                clients.Portal.PostLogoutRedirectUris,
                development,
                DevelopmentPortalPostLogoutRedirectUri
            ),
            AllowedGrants = [GrantType.AuthorizationCode, GrantType.RefreshToken],
            AllowedScopes = [.. AllScopes]
        };

        var cli = new ApplicationRegistration {
            ApplicationId = CliApplicationId,
            TenantId = Guid.Empty,
            ClientId = Cli,
            DisplayName = "cyc",
            IsPublicClient = true,
            RedirectUris = Configured(clients.Cli.RedirectUris, development, LoopbackRedirectUri),
            AllowedGrants = [GrantType.AuthorizationCode, GrantType.RefreshToken, GrantType.DeviceAuthorization],
            AllowedScopes = [.. AllScopes]
        };

        All = [portal, cli];

        // ⚠ Derived from the browser client's redirect URIs and from nothing else, so there is no
        // second list to drift: an origin that may receive an authorization code is exactly an
        // origin that may present the refresh cookie.
        AllowedOrigins = [
            .. portal.RedirectUris
                .Select(static x => Uri.TryCreate(x, UriKind.Absolute, out var uri)
                        ? uri.GetLeftPart(UriPartial.Authority)
                        : null
                )
                .Where(static x => x is not null)
                .Select(static x => x!)
                .Distinct(StringComparer.Ordinal)
        ];
    }

    /// <summary>The scopes every first-party client may ask for — all four this server issues.</summary>
    public static IReadOnlyList<string> AllScopes { get; } = [
        IdentityHostOpenIddict.Scopes.OpenId,
        IdentityHostOpenIddict.Scopes.Profile,
        IdentityHostOpenIddict.Scopes.OfflineAccess,
        IdentityHostOpenIddict.Scopes.Api
    ];

    /// <summary>Both registrations, portal first.</summary>
    public IReadOnlyList<ApplicationRegistration> All { get; }

    /// <summary>
    ///     The origins whose scripts may call <c>/token</c> with credentials — the browser clients'
    ///     redirect-URI origins, exactly.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; }

    /// <summary>The registration behind a first-party <c>client_id</c>, or <see langword="null" />.</summary>
    /// <param name="clientId">The <c>client_id</c>, compared ordinally.</param>
    public ApplicationRegistration? Find(string? clientId) =>
        All.FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="clientId" /> names one of the two, whatever tenant is asking.</summary>
    /// <param name="clientId">The <c>client_id</c>.</param>
    public static bool IsFirstParty(string? clientId) =>
        string.Equals(clientId, Portal, StringComparison.Ordinal)
        || string.Equals(clientId, Cli, StringComparison.Ordinal);

    /// <summary>
    ///     Whether <paramref name="clientId" /> is a first-party client that runs in a browser — the
    ///     one whose refresh token is moved into <c>__Host-cyc-refresh</c> and read back from it.
    /// </summary>
    /// <param name="clientId">The <c>client_id</c>.</param>
    /// <remarks>
    ///     ⚠ Only the portal. The CLI is a native app that holds its own token store (docs/plan/10
    ///     § Authentication inputs: the OS keychain), has no cookie jar the identity host could
    ///     write into, and calls <c>/token</c> from a process rather than a page — so it keeps the
    ///     refresh token in the body, and it gets no CORS origin.
    /// </remarks>
    public static bool IsBrowserClient(string? clientId) => string.Equals(clientId, Portal, StringComparison.Ordinal);

    /// <summary>
    ///     Whether <paramref name="redirectUri" /> is one of <paramref name="client" />'s registered
    ///     redirect URIs.
    /// </summary>
    /// <param name="client">The registration.</param>
    /// <param name="redirectUri">The <c>redirect_uri</c> the request asked to be sent back to.</param>
    /// <returns>
    ///     <see langword="true" /> on a whole-string ordinal match, or on the one exception RFC 8252
    ///     § 7.3 makes — see <see cref="RedirectUriMatches" />.
    /// </returns>
    public static bool IsRegisteredRedirectUri(ApplicationRegistration client, string? redirectUri) {
        ArgumentNullException.ThrowIfNull(client);

        return !string.IsNullOrEmpty(redirectUri)
            && client.RedirectUris.Exists(registered => RedirectUriMatches(registered, redirectUri));
    }

    /// <summary>
    ///     Whether a presented redirect URI matches a registered one.
    /// </summary>
    /// <param name="registered">The URI from the registration.</param>
    /// <param name="presented">The URI from the request.</param>
    /// <remarks>
    ///     ⚠ <b>Exact string equality, with exactly one relaxation.</b> A prefix match is the open
    ///     redirect that hands an authorization code to whoever asked —
    ///     <see cref="ApplicationRegistration.RedirectUris" /> says so, and <c>IApplicationGrain</c>
    ///     applies the same rule to tenant clients. The relaxation is RFC 8252 § 7.3: a native app
    ///     listening on the loopback interface cannot know its port until it binds one, so a
    ///     registered <c>http://127.0.0.1/callback</c> (or <c>[::1]</c>) matches the same URI on any
    ///     port. Nothing else about the URI may differ — scheme, host, path and query are compared
    ///     as they are — and <c>localhost</c> is deliberately not loopback here, because the RFC
    ///     says it may resolve elsewhere.
    /// </remarks>
    public static bool RedirectUriMatches(string registered, string presented) {
        if (string.Equals(registered, presented, StringComparison.Ordinal)) {
            return true;
        }

        if (!Uri.TryCreate(registered, UriKind.Absolute, out var expected)
            || !Uri.TryCreate(presented, UriKind.Absolute, out var actual)) {
            return false;
        }

        if (!string.Equals(expected.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(actual.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !IsLoopback(expected)
            || !IsLoopback(actual)
            || !string.Equals(expected.Host, actual.Host, StringComparison.Ordinal)) {
            return false;
        }

        // Everything but the port, compared as strings so no component is normalised on the way.
        return string.Equals(expected.PathAndQuery, actual.PathAndQuery, StringComparison.Ordinal)
            && string.Equals(expected.Fragment, actual.Fragment, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="redirectUri" /> is one of the client's post-logout URIs. Exact.</summary>
    /// <param name="client">The registration.</param>
    /// <param name="redirectUri">The <c>post_logout_redirect_uri</c>.</param>
    public static bool IsRegisteredPostLogoutRedirectUri(ApplicationRegistration client, string? redirectUri) {
        ArgumentNullException.ThrowIfNull(client);

        return !string.IsNullOrEmpty(redirectUri)
            && client.PostLogoutRedirectUris.Contains(redirectUri, StringComparer.Ordinal);
    }

    static bool IsLoopback(Uri uri) =>
        uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
        && System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
        && System.Net.IPAddress.IsLoopback(address);

    static List<string> Configured(IList<string> configured, bool development, string developmentDefault) =>
        configured.Count > 0 ? [.. configured] : development ? [developmentDefault] : [];
}
