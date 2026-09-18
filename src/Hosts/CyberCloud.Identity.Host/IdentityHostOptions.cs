namespace CyberCloud.Identity.Host;

/// <summary>
///     What the interactive endpoints and the OIDC surface need that is not in the request.
///     docs/plan/11 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The tenant is a request parameter now, and <see cref="TenantId" /> is only the
///             fallback.
///         </b> docs/plan/11 § Sign-up and tenant creation refuses a global email index, so
///         an address alone resolves to nothing and something has to name the tenant. This type used
///         to answer that with "one host signs into one tenant, named in configuration", and listed
///         the two reasons a caller-chosen tenant was worse: it needs a per-tenant client index
///         (which now exists — <c>IClientIndexGrain</c>), and it "would let an unauthenticated caller
///         choose which tenant's lockout counters and email index it probes". <c>TenantHint</c> meets
///         the second objection by resolving every hint through the platform tenant directory before
///         any per-tenant grain is touched: a made-up GUID or slug activates nothing, and every real
///         tenant is reachable by anyone by construction once sign-up is self-serve. A tenant per
///         path or per issuer was rejected because OpenIddict serves one issuer and the gateway pins
///         exactly one issuer string.
///     </para>
///     <para>
///         So <see cref="TenantId" /> is what the host falls back to when a request names no tenant:
///         set explicitly it wins, unset it is the platform tenant in Development and "no tenant"
///         anywhere else. The full rule is on <c>TenantHint</c>.
///     </para>
/// </remarks>
public sealed class IdentityHostOptions {
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Identity";

    /// <summary>
    ///     The tenant a request that names none signs into, when a deployment wants one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Nullable so "explicitly configured" and "unset" can be told apart, which the rule
    ///         needs: unset means the platform tenant (<see cref="Guid.Empty" />,
    ///         <c>PlatformCrossTenantAuthorizer.PlatformTenantId</c>)
    ///         <b>
    ///             only when the environment is
    ///             Development
    ///         </b>, and "no tenant" — <c>invalid_request</c> on <c>/authorize</c>, the
    ///         uniform failure on <c>/api/signin/*</c> — everywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ A production deployment that sets this to the platform tenant is signing every
    ///         request that names no tenant into the one tenant whose members can reach across all
    ///         the others. Leave it unset outside Development; the portal sends the tenant it
    ///         remembers, and the sign-in page asks for one when nothing does.
    ///     </para>
    /// </remarks>
    public Guid? TenantId { get; set; }

    /// <summary>
    ///     The <c>iss</c> every token carries and the discovery document announces — this host's
    ///     public origin, for example <c>https://id.cybercloud.io</c>. Empty means "whatever origin
    ///     the request arrived on".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Set it in production, and set the gateway's <c>CyberCloud:Gateway:Identity:Issuer</c>
    ///             to the same string.
    ///         </b> The gateway pins the issuer it validates against and refuses
    ///         a discovery document whose <c>issuer</c> differs from the one it was configured with —
    ///         so a host that inferred its issuer from the request would mint <c>iss</c> from
    ///         whatever <c>Host</c> header Envoy passed through, and a token minted behind one
    ///         hostname would be refused by a gateway configured with another. Inference is fine on a
    ///         developer's <c>127.0.0.1:port</c>, where the port is not known until Kestrel binds;
    ///         it is not a production configuration.
    ///     </para>
    ///     <para>
    ///         Absolute, no query, no fragment — OpenIddict refuses anything else at start-up.
    ///     </para>
    /// </remarks>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    ///     Where an unauthenticated <c>/authorize</c> sends a person: the origin the sign-in pages
    ///     are served from, with no trailing slash. Empty means this host's own origin.
    /// </summary>
    /// <remarks>
    ///     ⚠ Only the <i>origin</i> differs between the two cases; the redirect is always to
    ///     <c>{SignInPageBaseUri}/signin?returnUrl=…</c> and the return URL is always a same-origin
    ///     path, because <c>ReturnUrl.Sanitize</c> accepts nothing else. In production the pages are
    ///     built into this host and this stays empty. On the development run they are served by
    ///     <c>ng serve</c> on another port, whose proxy file forwards <c>/authorize</c> back here —
    ///     which is what lets the sanitized relative return URL resume the OIDC request through the
    ///     page's origin while carrying the cookie. The AppHost sets <c>http://localhost:4201</c>.
    /// </remarks>
    public string SignInPageBaseUri { get; set; } = string.Empty;

    /// <summary>
    ///     A directory the signing key, the encryption key and the data-protection key ring persist
    ///     to across restarts. Development only; empty means ephemeral keys.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Set outside Development, the host refuses to start.</b> docs/plan/11 § Protocol's
    ///         rotating key set is <c>CyberCloud.Vault</c>'s (docs/plan/18), and a key file on a disk
    ///         is not a substitute for it anywhere a second replica or a backup exists. The
    ///         environment gate is the same one <c>IdentityHostOpenIddict</c> uses for plain HTTP:
    ///         keyed on the environment rather than on a setting, because a value somebody can set is
    ///         a value somebody will set. <c>DevelopmentKeyFile</c> carries the refusal.
    ///     </para>
    ///     <para>
    ///         Both keys, not only the signing key: authorization codes and refresh tokens are
    ///         encrypted with the encryption key, so an ephemeral one would still end every portal
    ///         session on restart. The AppHost points this at <c>.identity/</c> beside its
    ///         <c>.k3s/</c> and <c>.seaweedfs/</c>.
    ///     </para>
    /// </remarks>
    public string DevelopmentKeyDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     The first-party clients' redirect URIs. <c>FirstPartyClients</c> builds the static
    ///     registrations from them.
    /// </summary>
    public ClientOptions Clients { get; } = new();

    /// <summary>
    ///     The WebAuthn relying-party id — the registrable domain a passkey is bound to.
    /// </summary>
    /// <remarks>
    ///     ⚠ It must equal the origin's registrable domain or a parent of it. A page served from
    ///     <c>id.cybercloud.io</c> may use <c>cybercloud.io</c> or <c>id.cybercloud.io</c> and never
    ///     anything else; getting it wrong fails inside the authenticator with a <c>SecurityError</c>
    ///     that names nothing useful. ⚠ Widening it later <b>orphans every enrolled passkey</b> — the
    ///     credential is bound to the value in force when it was created — so it is a one-way choice
    ///     rather than a setting.
    /// </remarks>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>What the authenticator's own prompt calls this deployment.</summary>
    public string RelyingPartyName { get; set; } = "Cyber Cloud";

    /// <summary>
    ///     The origins a WebAuthn response may claim to come from.
    /// </summary>
    /// <remarks>
    ///     ⚠ Checked by the library against the origin the authenticator signed over, which is what
    ///     makes a passkey phishing-resistant. An empty set means the library refuses every
    ///     assertion, which is the correct behaviour for a host nobody has configured.
    /// </remarks>
    public IList<string> Origins { get; } = ["https://localhost:5001"];

    /// <summary>
    ///     Whether <c>/api/signup/*</c> is open. <c>false</c> answers every call with
    ///     <c>SignUpApi.ClosedMessage</c> and touches nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three processes read this key and they have to agree.</b> The silos'
    ///     <c>PlatformBootstrapTask</c> writes the <c>platform:root#operator</c> grant sign-up
    ///     creates tenants under only when it is set; this host opens the endpoints only when it is
    ///     set. A host with it on beside silos with it off refuses every completion with "something
    ///     went wrong" — the operator check inside <c>IScopeManager.CreateTenantAsync</c> fails and
    ///     the seam says no more. The AppHost sets it on all three from one constant.
    /// </remarks>
    public bool SelfServeSignUp { get; set; }

    /// <summary>
    ///     The region a self-serve sign-up homes its tenant to and places its default resource group
    ///     in. <c>local</c> on the AppHost.
    /// </summary>
    /// <remarks>
    ///     ⚠ Required when <see cref="SelfServeSignUp" /> is on: a tenant is homed to exactly one
    ///     region at creation and a resource group's region has no platform-wide default —
    ///     <c>ScopeManagerService</c> refuses both without one. Empty leaves every completion failing
    ///     at the first create with a sentence naming this setting.
    /// </remarks>
    public string DefaultRegion { get; set; } = string.Empty;

    /// <summary>
    ///     The proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> this host believes —
    ///     addresses (<c>10.42.0.17</c>) or CIDR blocks (<c>10.42.0.0/16</c>). Empty means the
    ///     connection's own address is the caller's and the headers are ignored.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Set it behind the ingress, or the per-IP limits are one bucket for everybody.</b>
    ///         docs/plan/10 § Shape puts Envoy in front of every host, so what
    ///         <c>HttpContext.Connection.RemoteIpAddress</c> holds on a deployed pod is the ingress's
    ///         address — the same one for every person. <c>IdentityRateLimits</c> keys its two
    ///         buckets by that address, so without this list ten sign-ups in ten minutes and sixty
    ///         code answers a minute become platform-wide caps, and ten requests from one hostile
    ///         caller close sign-up for everyone. With the ingress named here,
    ///         <c>IdentityComposition.MapIdentityHost</c> runs the forwarded-headers middleware first
    ///         and the address the buckets — and <c>SignInContext.ClientAddress</c> — see is the one
    ///         Envoy appended to <c>X-Forwarded-For</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Name the ingress and nothing wider.</b> The middleware believes a forwarded
    ///         header only when the connection it arrived on is from an address in this list, and
    ///         that is the whole defence: a caller sets their own headers, so a list that includes
    ///         the caller — the whole pod network, say, when tenant workloads share it — is a
    ///         rate-limit key the caller picks. One hop is read (<c>ForwardLimit</c> stays 1), the
    ///         rightmost entry, which is the address Envoy itself appends; anything the caller put
    ///         in the header before it is never reached. A value that parses as neither an address
    ///         nor a block refuses start-up with a sentence naming this setting.
    ///     </para>
    ///     <para>
    ///         Leave it empty on the development run: the browser reaches the host directly on
    ///         <c>127.0.0.1</c>, and <c>IdentityHostFixture</c> starts the host the same way.
    ///         <c>GrantsOverHttpTests.TheForwardedAddressCountsOnlyWhenTheDeploymentNamesItsProxy</c>
    ///         pins both halves.
    ///     </para>
    /// </remarks>
    public IList<string> TrustedProxies { get; } = [];

    /// <summary>
    ///     The two first-party clients' redirect URIs — the only thing about them a deployment
    ///     decides. Everything else on the registration is fixed in <c>FirstPartyClients</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Empty lists mean "not usable from a browser or a CLI here", and the lists are empty by
    ///     default so a production deployment has to say where its portal is. <c>FirstPartyClients</c>
    ///     fills in the development run's values — <c>http://localhost:4200/auth/callback</c> and the
    ///     RFC 8252 loopback — only when the environment is Development and nothing is configured.
    /// </remarks>
    public sealed class ClientOptions {
        /// <summary>The browser client, <c>cyc-portal</c>.</summary>
        public BrowserClientOptions Portal { get; } = new();

        /// <summary>The native client, <c>cyc-cli</c>.</summary>
        public NativeClientOptions Cli { get; } = new();
    }

    /// <summary>A browser client's two URI lists.</summary>
    public sealed class BrowserClientOptions {
        /// <summary>
        ///     Where the authorization code may be sent. ⚠ Compared whole and ordinally — the
        ///     origins of these are also the CORS allow-list for <c>/token</c>.
        /// </summary>
        public IList<string> RedirectUris { get; } = [];

        /// <summary>Where the browser may be sent after <c>/logout</c>.</summary>
        public IList<string> PostLogoutRedirectUris { get; } = [];
    }

    /// <summary>A native client's redirect URIs.</summary>
    public sealed class NativeClientOptions {
        /// <summary>
        ///     Where the authorization code may be sent. A loopback URI here matches any port, per
        ///     RFC 8252 § 7.3 — see <c>FirstPartyClients.RedirectUriMatches</c>.
        /// </summary>
        public IList<string> RedirectUris { get; } = [];
    }
}
