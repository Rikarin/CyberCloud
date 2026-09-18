namespace CyberCloud.AppHost;

/// <summary>
///     The names and ports the local topology is built from.
/// </summary>
/// <remarks>
///     <para>
///         These are constants rather than literals in <c>Program.cs</c> because the exit-criterion
///         test in <c>CyberCloud.AppHost.Tests</c> has to reach the same silos through the same
///         gateway ports. A test that re-declared them would pass on a topology that no longer
///         exists.
///     </para>
///     <para>
///         ⚠ <b>The Orleans ports are fixed, and everything else Aspire allocates.</b> Aspire hands
///         each project a free HTTP port and injects it as <c>ASPNETCORE_URLS</c>; it knows nothing
///         about Orleans' silo-to-silo and gateway sockets, which are opened by the Orleans runtime
///         long after the process has started. So those four have to be chosen here, and they must
///         not be Orleans' defaults — <c>CyberCloudClusterOptions.LocalhostGatewayPort</c> explains
///         why 30000 in particular is a bad bet on a developer's machine.
///     </para>
/// </remarks>
public static class CyberCloudResources {
    /// <summary>The Redis container that backs the hot tier — docs/plan/05 § Hot.</summary>
    public const string Redis = "hot";

    /// <summary>The PostgreSQL server that carries every durable shard — docs/plan/05 § Durable.</summary>
    public const string Postgres = "durable";

    /// <summary>The NATS server — ADR-005, docs/plan/04 § Streams.</summary>
    public const string Nats = "nats";

    /// <summary>The k3s container — ADR-014, and the data plane of ADR-001.</summary>
    public const string K3s = "k3s";

    /// <summary>The one-shot job that creates the Orleans grain-storage schema on every shard.</summary>
    public const string DurableSchema = "durable-schema";

    /// <summary>The first silo. It holds the development membership table.</summary>
    public const string SiloOne = "silo-1";

    /// <summary>The second silo. It joins <see cref="SiloOne" />.</summary>
    public const string SiloTwo = "silo-2";

    /// <summary>The first tenant-carrying durable shard.</summary>
    public const string ShardA = "durable00";

    /// <summary>The second tenant-carrying durable shard.</summary>
    public const string ShardB = "durable01";

    /// <summary>
    ///     The shard that carries every null-tenant platform grain — the tenant directory and the
    ///     shard map (docs/plan/04 § Grain taxonomy, the Platform row).
    /// </summary>
    public const string PlatformShard = "platform00";

    /// <summary>
    ///     The region a self-serve sign-up homes its tenant to and places its default resource group
    ///     in. This laptop is one region, and this is its name.
    /// </summary>
    public const string DefaultRegion = "local";

    /// <summary>Silo 1's silo-to-silo port. Also the cluster's primary-silo endpoint.</summary>
    public const int SiloOnePort = 11111;

    /// <summary>Silo 1's client-facing gateway port.</summary>
    public const int SiloOneGatewayPort = 30011;

    /// <summary>Silo 2's silo-to-silo port.</summary>
    public const int SiloTwoPort = 11112;

    /// <summary>Silo 2's client-facing gateway port.</summary>
    public const int SiloTwoGatewayPort = 30012;

    /// <summary>The host port k3s' API server is published on.</summary>
    /// <remarks>
    ///     Fixed rather than allocated because the kubeconfig k3s writes names a port, and a
    ///     kubeconfig whose port changes on every run is a kubeconfig nobody can use.
    /// </remarks>
    public const int K3sApiPort = 6443;

    // ── The hosts a user reaches ──────────────────────────────────────────────────────────────

    /// <summary>The REST + SignalR gateway — an Orleans client, docs/plan/03 § Hosts.</summary>
    public const string Gateway = "gateway";

    /// <summary>The identity host — OIDC, cookies, sign-in and sign-up endpoints.</summary>
    public const string Identity = "identity";

    /// <summary>The NuGet, npm and Maven feeds host of <c>ContainerRegistry/feeds</c>.</summary>
    public const string Feeds = "feeds";

    /// <summary>The portal's Angular dev server.</summary>
    public const string Portal = "portal";

    /// <summary>The identity app's Angular dev server — the sign-in and sign-up pages.</summary>
    public const string IdentityApp = "identity-app";

    /// <summary>
    ///     The ports the three hosts and the two dev servers listen on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Fixed, not allocated, and the reason is the proxy files.</b> The portal calls
    ///         the platform on its own origin — <c>/api</c>, see <c>API_BASE_PATH</c> in the portal —
    ///         because the gateway attaches the bearer token to same-origin requests only, and the
    ///         API shim that will serve that path in production (<c>CyberCloud.Portal.Host</c>) does
    ///         not exist. Locally the Angular dev server is the shim: <c>apps/portal/proxy.conf.json</c>
    ///         forwards <c>/api</c> to the gateway and <c>apps/identity/proxy.conf.json</c> forwards
    ///         the sign-in API to the identity host. A proxy file names a port, and a port that
    ///         changes on every run is one the file cannot name — so the two dev servers and the
    ///         hosts they forward to are pinned here, and <c>AppHostTopologyTests</c> reads the
    ///         proxy files back and refuses a drift.
    ///     </para>
    ///     <para>
    ///         The identity host's port is also its <b>issuer</b>: the gateway and the feeds host
    ///         validate tokens against exactly the origin they were configured with, and the
    ///         identity host is handed the same string as its <c>iss</c> — so <c>http://localhost:5101</c>
    ///         has to be the same string on every side and the port it names has to be the one the
    ///         host listens on. Choosing the port here is what makes it one string rather than three.
    ///     </para>
    /// </remarks>
    public const int GatewayPort = 5100;

    /// <inheritdoc cref="GatewayPort" />
    public const int IdentityPort = 5101;

    /// <inheritdoc cref="GatewayPort" />
    public const int FeedsPort = 5102;

    /// <inheritdoc cref="GatewayPort" />
    public const int PortalPort = 4200;

    /// <inheritdoc cref="GatewayPort" />
    public const int IdentityAppPort = 4201;

    /// <summary>
    ///     The origin the identity host announces and the two token validators pin —
    ///     <c>http://localhost:5101</c>.
    /// </summary>
    public static string IdentityIssuer => $"http://localhost:{IdentityPort}";

    // ── The object store ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     SeaweedFS with its S3 gateway — the platform's object store, which the feeds host and the
    ///     silo's feed reconciler write artefacts to.
    /// </summary>
    /// <remarks>
    ///     The same image and the same <c>s3.json</c> shape <c>SeaweedFsRoundTripTests</c> stands up,
    ///     so what the AppHost talks to is what the store is tested against. ⚠ The S3 client
    ///     deliberately never creates a bucket (<c>IObjectStore</c> writes into one an operator
    ///     made), so <see cref="ObjectStoreBucketInit" /> makes it: a directory under the filer's
    ///     <c>/buckets</c> <i>is</i> a bucket to SeaweedFS, and the filer's HTTP API creates the
    ///     directory on the first upload into it.
    /// </remarks>
    public const string ObjectStore = "seaweedfs";

    /// <summary>The one-shot container that creates <see cref="ObjectStoreBucket" />.</summary>
    public const string ObjectStoreBucketInit = "seaweedfs-bucket";

    /// <summary>The bucket every host is pointed at.</summary>
    public const string ObjectStoreBucket = "cybercloud";

    /// <summary>The S3 gateway's port, published unproxied so the endpoint is the address.</summary>
    public const int ObjectStoreS3Port = 8333;

    /// <summary>The filer's HTTP port, which the bucket init container uploads through.</summary>
    public const int ObjectStoreFilerPort = 8888;

    /// <summary>
    ///     The one identity SeaweedFS's S3 gateway knows. Development-only, in the same sense as
    ///     every password in this file's neighbours: it never leaves the laptop, and a value that
    ///     were secret here would be a value nobody could read to reproduce a colleague's run.
    /// </summary>
    public const string ObjectStoreAccessKeyId = "cybercloud-dev-access-key";

    /// <inheritdoc cref="ObjectStoreAccessKeyId" />
    public const string ObjectStoreSecretAccessKey = "cybercloud-dev-secret-access-key";

    // ── The development mail relay — #93 ──────────────────────────────────────────────────────

    /// <summary>
    ///     Mailpit — an SMTP server that accepts everything and shows it in a web inbox. The
    ///     development carrier behind <c>CyberCloud.Communication</c>'s email channel, so a sign-up
    ///     code and an alert land somewhere a person can open.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Development only, and not because Mailpit is a toy.</b> It is a relay that delivers
    ///     nothing anywhere: every message stays in its inbox. The silos are pointed at it through
    ///     <c>CyberCloud:Communication:Smtp</c> with <c>Security=None</c> and no credential, which
    ///     <c>SmtpRelayOptions.Validate</c> allows only because there is no password to leak; a
    ///     production relay is the same section with TLS and an <c>AUTH</c> credential, and the
    ///     relay itself is what <c>charts/bundle/bundle.yaml § owed</c> still carries.
    /// </remarks>
    public const string Mailpit = "mailpit";

    /// <summary>The image, pinned. ⚠ The same tag <c>SmtpChannelProviderTests</c> runs the carrier against, and <c>AppHostTopologyTests</c> holds the two together.</summary>
    public const string MailpitImage = "axllent/mailpit";

    /// <inheritdoc cref="MailpitImage" />
    public const string MailpitTag = "v1.31.1";

    /// <summary>Mailpit's SMTP port, published unproxied so the silos' <c>localhost:1025</c> is the address.</summary>
    public const int MailpitSmtpPort = 1025;

    /// <summary>Mailpit's web inbox and API — <c>http://localhost:8025</c>.</summary>
    public const int MailpitHttpPort = 8025;

    /// <summary>
    ///     The address the platform's mail is sent from on a development run. A <c>.local</c> domain
    ///     nothing resolves, because nothing is meant to answer it — Mailpit keeps the reply too.
    /// </summary>
    public const string PlatformSender = "no-reply@cybercloud.local";

    /// <summary>The display name beside <see cref="PlatformSender" />.</summary>
    public const string PlatformSenderName = "Cyber Cloud (dev)";

    /// <summary>
    ///     The mailbox <c>List-Unsubscribe</c> points at. On Mailpit an unsubscribe reply lands in the
    ///     same inbox as everything else, which is exactly where a developer testing the header wants
    ///     to see it.
    /// </summary>
    public const string PlatformUnsubscribeMailbox = "unsubscribe@cybercloud.local";

    // ── What the AppHost is asked to leave out ────────────────────────────────────────────────

    /// <summary>
    ///     The configuration key that turns the two Angular dev servers off —
    ///     <c>--CyberCloud:AppHost:Frontends=false</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Set by <c>LocalTopology</c> in <c>CyberCloud.AppHost.Tests</c> and by nothing else. The
    ///     tests measure the silos, the storage tiers and the hosts; <c>ng serve</c> twice over is
    ///     two more minutes and a Node toolchain (<c>portal/.nvmrc</c>) that a .NET test run has no
    ///     business requiring. <c>dotnet run</c> leaves it on, because a platform nobody can open is
    ///     not running.
    /// </remarks>
    public const string FrontendsKey = "CyberCloud:AppHost:Frontends";
}
