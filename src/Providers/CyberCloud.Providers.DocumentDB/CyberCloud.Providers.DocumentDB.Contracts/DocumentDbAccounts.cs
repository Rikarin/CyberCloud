using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.DocumentDB/accounts</c>: the type, its api-version,
///     its body shape, and the four Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md):
///         <i>"MongoDB-compatible — <c>CyberCloud.DocumentDB/accounts</c> · M2 · 1.2 EM. <b>FerretDB</b>
///         (Apache-2.0) over a CloudNativePG cluster. ADR-011: real MongoDB is SSPL and cannot be
///         offered as a service."</i>
///     </para>
///     <para>
///         ⚠ <b>THIS IS A COMPATIBILITY LAYER AND EVERY NAME A HUMAN READS SAYS SO.</b> ADR-011's rule
///         for <c>CyberCloud.Cache/redis</c> — <i>"say Valkey on the product page"</i> — applies here
///         with more force, because the gap between the substitute and the original is behavioural
///         rather than only nominal. docs/plan/12's own sentence: <i>"Selling it as 'MongoDB' produces
///         a churn event at the first <c>$lookup</c>. Selling it as 'MongoDB-compatible document
///         database, here is exactly what works' produces a happy customer with a smaller use
///         case."</i> <see cref="UnsupportedCommands" /> is that table, in the type, so that the
///         supported-subset statement is a thing the CLI and the portal can print rather than a
///         paragraph in a document nobody ships.
///     </para>
///     <para>
///         ⚠ <b>THERE IS NO FERRETDB OPERATOR, AND ADR-010 CLAUSE 1's SURVEY NAMES ONE.</b> That
///         clause lists <i>"…, RabbitMQ Cluster Operator, OpenSearch operator, <b>FerretDB</b>,
///         Qdrant, …"</i> in a sentence about <i>"the operator selection per managed service"</i>.
///         Checked on 2026-08-12 against the GitHub API rather than against a README: the
///         <c>FerretDB</c> organisation contains <c>FerretDB</c>, <c>documentdb</c>, <c>dance</c>,
///         <c>deps</c>, language examples and marketplace forks, and <b>no operator, no CRD and no
///         Helm chart</b>. The documented Kubernetes install is a <c>Deployment</c> and a
///         <c>Service</c> applied with <c>kubectl</c>. ⚠ <b>SECOND SIGHTING</b> —
///         <c>charts/managed/nats</c> found the same thing about that row (<c>nats-operator</c>
///         archived 2025-04-10), so ADR-010 clause 1 is a survey of <i>software</i> and only
///         sometimes of <i>operators</i>. Two of eight rows in, that is a property of the clause
///         rather than of either service. <c>charts/managed/ferretdb/SOURCE</c> records the check.
///     </para>
///     <para>
///         ⚠ <b>SO THE ROW IS HALF OPERATOR-BACKED AND HALF NOT, WHICH NOTHING IN THE CATALOGUE HAS
///         BEEN BEFORE.</b> The Postgres half is a CloudNativePG <c>Cluster</c> — an operator answers
///         for HA, failover, backup and the scrape object. The FerretDB half is a plain
///         <c>Deployment</c>, a <c>Service</c> and a hand-written <c>PodMonitor</c>. See
///         <see cref="PodMonitorJson" />: this is the first row where docs/plan/12 § The pattern,
///         once, piece 6 takes <b>both</b> of its branches at once, and the second branch is safe here
///         for the reason <c>charts/managed/nats</c> proved — the pods the selector matches are
///         written by this provider.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/12 SAYS THE POSTGRES HALF IS "ALREADY BUILT FOR THE ROW ABOVE" AND NO LINE
///         OF IT IS REUSABLE.</b> That row is <c>CyberCloud.DBforPostgreSQL/servers</c> and
///         <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly
///         referencing another, deliberately. So <see cref="ClusterJson" /> is a second, independent
///         rendering of the same CRD. ⚠ <b>Writing it found a live defect in the first one</b> —
///         see <see cref="SharedPreloadLibraries" /> — which is the strongest argument available that
///         the duplication is a cost worth paying rather than a rule worth bending: two independent
///         renderings disagreeing is a thing a reviewer can see, and one shared helper being wrong is
///         not.
///     </para>
///     <para>
///         ⚠ <b>NOTHING HERE MINTS, AND WHAT MAKES THAT SAFE IS THE ENGINE RATHER THAN THE
///         PLATFORM.</b> CloudNativePG generates the credential itself, and FerretDB neither stores
///         nor invents one: checked in <c>website/docs/security/authentication.md</c>, <i>"FerretDB
///         does not store authentication information (usernames and passwords) itself. Instead, it
///         relies entirely on PostgreSQL's authentication mechanisms"</i>, and an anonymous client
///         <i>"may still connect to FerretDB without authentication, but they cannot access or
///         perform actions on the database"</i>. So the service works and an unauthenticated caller
///         gets nothing. ⚠ The paragraph that stood here said piece 5 was not built and that
///         <c>listKeys</c> had nowhere to read the password from. Both have been false since
///         <c>ISecretWriter</c> and <c>DocumentDbAccountListKeysHandler</c> landed: the handler reads
///         <see cref="SuperuserSecretName" />'s two keys, and this row declines to mint because the
///         cluster already holds a password minting could only contradict.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/ferretdb/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class DocumentDbAccounts {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>CyberCloud.MongoDB</c> and not <c>CyberCloud.FerretDB</c>.</b> The first is the
    ///     trademark ADR-011 forbids the product from claiming; the second names the implementation in
    ///     an address that outlives it. <c>DocumentDB</c> is what the row is — a document database —
    ///     and it is what Azure's own parity row is called.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.DocumentDB";

    /// <summary>The resource type. docs/plan/12 § The catalogue.</summary>
    public const string TypePath = "accounts";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/ferretdb/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/ferretdb";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the endpoint and a credential.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions makes it <i>"an action with its own permission,
    ///     audited on every call"</i>. ⚠ <c>regenerateKeys</c> is named in the same paragraph and is
    ///     <b>not</b> declared, for the reason the five providers before this one give: it is specified
    ///     with a rolling grace period and nothing in the platform can hold two live credentials for
    ///     one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of an account a holder of a PostgreSQL role's password
    ///     — and on this type that role is the one FerretDB forwards every client's credentials to.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The four objects an account IS ────────────────────────────────────────────────────────

    /// <summary>The CloudNativePG <c>Cluster</c> that holds the data.</summary>
    /// <remarks>
    ///     ⚠ <b>The same group, version and kind <c>CyberCloud.DBforPostgreSQL/servers</c> renders,
    ///     declared again because it must be.</b> <c>src/Providers/README.md § Hard rule</c> forbids
    ///     the reference; <c>module-layering.txt</c> records that a line between two providers <i>"would
    ///     fail rule 2 and be deleted, not honoured"</i>. Two rows sharing one operator is the case that
    ///     rule had not met before.
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() { Group = "postgresql.cnpg.io", Version = "v1", Kind = "Cluster", Plural = "clusters" };

    /// <summary>The FerretDB proxy's <c>Deployment</c>.</summary>
    /// <remarks>
    ///     ⚠ A <c>Deployment</c> and not a <c>StatefulSet</c>, because FerretDB holds no data: it
    ///     translates the MongoDB wire protocol into calls on the DocumentDB extension and every byte
    ///     lives in PostgreSQL. <c>FERRETDB_STATE_DIR</c> is a telemetry marker rather than state —
    ///     <c>build/ferretdb/production.Dockerfile</c> sets it to <c>/state</c> — and losing it costs
    ///     nothing but a re-sent "new instance" beacon.
    /// </remarks>
    public static GroupVersionKind DeploymentKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    /// <summary>The <c>Service</c> a MongoDB driver connects to.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    /// <summary>The <c>PodMonitor</c> that scrapes the FerretDB pods.</summary>
    /// <remarks>
    ///     ⚠ The plural is <c>podmonitors</c>. It is carried rather than derived for the reason
    ///     <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed harness
    ///     derives a definition stub from.
    /// </remarks>
    public static GroupVersionKind PodMonitorKind { get; } =
        new() {
            Group = "monitoring.coreos.com", Version = "v1", Kind = "PodMonitor", Plural = "podmonitors"
        };

    // ── Ports, names and images ───────────────────────────────────────────────────────────────

    /// <summary>The MongoDB wire-protocol port.</summary>
    /// <remarks>
    ///     ⚠ <b>27017 is not negotiable and is not a property.</b> Every MongoDB driver in existence
    ///     defaults to it, and a managed document database on a port the tenant chose is a connection
    ///     string that breaks the first tool somebody points at it.
    /// </remarks>
    public const int MongoPort = 27017;

    /// <summary>The debug listener: Prometheus metrics, liveness and readiness.</summary>
    /// <remarks>
    ///     <c>internal/util/debug/debug.go</c> registers <c>/debug/metrics</c>,
    ///     <c>/debug/livez</c> and <c>/debug/readyz</c> on this one listener. It is never on
    ///     <see cref="ServiceJson" />: <c>/debug/archive</c> on the same port returns a zip of the
    ///     process's internal state, and a routable address for it is an information disclosure with a
    ///     download link.
    /// </remarks>
    public const int DebugPort = 8088;

    /// <summary>Where Prometheus finds the metrics on <see cref="DebugPort" />.</summary>
    /// <remarks>
    ///     ⚠ Not <c>/metrics</c>. The handler is registered at <c>/debug/metrics</c> —
    ///     <c>internal/util/debug/debug.go</c> — and a <c>PodMonitor</c> with the conventional path
    ///     scrapes a 404 forever without failing, which is the quiet-scrape hazard docs/plan/12
    ///     § piece 6 exists to avoid.
    /// </remarks>
    public const string MetricsPath = "/debug/metrics";

    /// <summary>The FerretDB image repository.</summary>
    public const string GatewayImageRepository = "ghcr.io/ferretdb/ferretdb";

    /// <summary>The PostgreSQL-with-DocumentDB image repository.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>ghcr.io/cloudnative-pg/postgresql</c>, which is what the row above renders.</b>
    ///     FerretDB v2 is a proxy over the <c>documentdb</c> PostgreSQL extension and a stock
    ///     PostgreSQL image does not carry it, so <c>CREATE EXTENSION documentdb</c> fails and every
    ///     query afterwards fails with it. ⚠ And not
    ///     <c>ghcr.io/ferretdb/postgres-documentdb-<b>dev</b></c>, which is what
    ///     <c>build/deps/postgres-documentdb.Dockerfile</c> uses and which that file's own comment
    ///     marks as a development image.
    /// </remarks>
    public const string PostgresImageRepository = "ghcr.io/ferretdb/postgres-documentdb";

    /// <summary>
    ///     The libraries the DocumentDB extension needs preloaded, in the order upstream writes them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS RENDERS TO <c>spec.postgresql.shared_preload_libraries</c> — A LIST, A SIBLING
    ///         OF <c>parameters</c> — AND THE ROW ABOVE PUT IT <i>INSIDE</i> <c>parameters</c> UNTIL
    ///         2026-08-12, WHICH CLOUDNATIVEPG REFUSES.</b> Checked in that operator's source rather
    ///         than inferred:
    ///         <c>api/v1/cluster_types.go</c> declares
    ///         <c>AdditionalLibraries []string `json:"shared_preload_libraries,omitempty"`</c> on
    ///         <c>PostgresConfiguration</c>, and <c>pkg/postgres/configuration.go</c> lists
    ///         <c>"shared_preload_libraries"</c> in <c>FixedConfigurationParameters</c>. The validating
    ///         webhook — <c>internal/webhook/v1/cluster_webhook.go</c> — walks
    ///         <c>spec.postgresql.parameters</c> and answers any fixed key with
    ///         <c>field.Invalid(…, "Can't set fixed configuration parameter")</c> unless the value
    ///         equals CloudNativePG's own sanitized one — which is the default settings'
    ///         <c>SharedPreloadLibraries: ""</c>, because <c>IncludingSharedPreloadLibraries</c> gates
    ///         the only code that would add to it and the webhook leaves that field <c>false</c>. So
    ///         no non-empty list can ever equal it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The consequence for the other row was a real defect, and it is fixed since
    ///         2026-08-12.</b>
    ///         <c>src/Providers/CyberCloud.Providers.DBforPostgreSQL/…/PostgresServers.cs</c>'s
    ///         <c>ClusterJson</c> wrote <c>parameters["shared_preload_libraries"]</c> whenever
    ///         <c>/properties/extensions</c> was non-empty, and
    ///         <c>charts/managed/postgres/templates/cluster.yaml</c> did the same — so every server
    ///         created with <c>pgvector</c>, <c>postgis</c>, <c>pg_stat_statements</c> or
    ///         <c>timescaledb</c> was rejected by admission <i>after</i> the caller was told
    ///         <c>202</c>. The default body asks for none, which is why nothing had noticed. Both
    ///         spellings now render a list beside <c>parameters</c>;
    ///         <c>charts/managed/postgres/conformance.yaml § owed</c> carries the finding and
    ///         <c>charts/managed/ferretdb/conformance.yaml § owed</c> records the close.
    ///     </para>
    ///     <para>
    ///         ⚠ The library names carry the <c>pg_</c> prefix and the <i>extension</i> created from
    ///         them does not — see <see cref="ExtensionStatement" />. Two vocabularies, three lines
    ///         apart.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> SharedPreloadLibraries { get; } =
        ["pg_cron", "pg_documentdb_core", "pg_documentdb"];

    /// <summary>The statement that installs the extension, run once at bootstrap.</summary>
    /// <remarks>
    ///     ⚠ <b><c>documentdb</c>, with no <c>pg_</c> prefix, and <c>CASCADE</c> is load-bearing.</b>
    ///     The extension depends on <c>pg_documentdb_core</c>, <c>pg_cron</c>, <c>vector</c> and
    ///     <c>postgis</c>; without <c>CASCADE</c> the statement fails on the first missing dependency
    ///     and the bootstrap job fails with it.
    /// </remarks>
    public const string ExtensionStatement = "CREATE EXTENSION IF NOT EXISTS documentdb CASCADE;";

    /// <summary>The database FerretDB connects to and the extension is installed in.</summary>
    /// <remarks>
    ///     ⚠ <b><c>postgres</c>, which is CloudNativePG's <i>maintenance</i> database rather than the
    ///     application one, and that is upstream's shape rather than a preference.</b>
    ///     <c>spec.bootstrap.initdb.postInitSQL</c> is documented in <c>api/v1/cluster_types.go</c> as
    ///     <i>"executed as a superuser in the <c>postgres</c> database"</i>, and installing an
    ///     extension needs a superuser. Putting it in the application database instead —
    ///     <c>postInitApplicationSQL</c>, which also runs as superuser — would let this type drop
    ///     <see cref="EnableSuperuserAccess" /> entirely and is the better shape; it is not taken
    ///     because nothing verifiable says the DocumentDB extension and <c>pg_cron</c>'s
    ///     <c>cron.database_name</c> follow it there, and shipping an unverified privilege model
    ///     produces a service that never comes up. <c>conformance.yaml § owed</c>,
    ///     <c>superuser-is-the-connection-role</c>.
    /// </remarks>
    public const string Database = "postgres";

    /// <summary>Whether the rendered <c>Cluster</c> asks CloudNativePG for a superuser secret.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>true</c>, and <c>spec.enableSuperuserAccess</c> defaults to <c>false</c>, so
    ///         this is written on every apply rather than inherited.</b> The extension lives in the
    ///         <c>postgres</c> database (<see cref="Database" />) and the application owner has no
    ///         rights there, so the superuser role is the only one that can serve a client.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is part of <see cref="Matches" />, and it is the one field here whose absence is
    ///         invisible from everywhere else.</b> <c>internal/controller/cluster_create.go</c> creates
    ///         the secret only <c>if cluster.GetEnableSuperuserAccess()</c>, and the operator
    ///         <i>deletes</i> it again when the flag is taken away — <c>api/v1/cluster_types.go</c>:
    ///         <i>"the operator will ignore the SuperuserSecret content, delete it"</i>. A
    ///         <c>Cluster</c> whose flag was flipped off by a merge, a policy or a <c>kubectl edit</c>
    ///         therefore leaves a healthy PostgreSQL, a healthy proxy Deployment, and a
    ///         <c>Secret</c> reference that resolves to nothing: the FerretDB pods stop starting and
    ///         every existing client is refused at the next reconnect.
    ///     </para>
    /// </remarks>
    public const bool EnableSuperuserAccess = true;

    /// <summary>The UID and GID the DocumentDB image runs PostgreSQL as.</summary>
    /// <remarks>
    ///     ⚠ <b>999, and CloudNativePG's default is 26.</b> <c>api/v1/cluster_types.go</c>:
    ///     <c>DefaultPostgresUID = 26</c>, <c>DefaultPostgresGID = 26</c>. The
    ///     <c>postgres-documentdb</c> image builds on the Debian PostgreSQL packaging, whose
    ///     <c>postgres</c> user is 999, so a <c>Cluster</c> that left these unset mounts its data
    ///     directory with an owner the running process cannot write and the first instance never
    ///     initialises. ⚠ <b>The failure is a permission error in an init container's log</b> — there
    ///     is nothing on the <c>Cluster</c>'s own status that says "the UID is wrong", which is why
    ///     this is written out with the numbers rather than left to whichever default happens to be
    ///     in force.
    /// </remarks>
    public const int PostgresUid = 999;

    /// <inheritdoc cref="PostgresUid" />
    public const int PostgresGid = 999;

    /// <summary>The CPU one FerretDB pod requests. ⚠ Platform-set, and it still costs quota.</summary>
    /// <remarks>
    ///     FerretDB is a stateless protocol translator: it holds no data, caches nothing durable, and
    ///     scales with connection count rather than with the tenant's dataset. That makes it a bad
    ///     candidate for a sizing property and a good one for a constant — but an account with two
    ///     gateway pods runs two containers before a document is written, and a meter that counted
    ///     only the PostgreSQL instances would under-reserve by exactly that. <c>DocumentDbProvider</c>
    ///     carries the sum.
    /// </remarks>
    public const string GatewayCpu = "250m";

    /// <summary>The memory one FerretDB pod requests. See <see cref="GatewayCpu" />.</summary>
    public const string GatewayMemory = "512Mi";

    // ── Addressing ────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Cluster</c> object's name.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Suffixed, where the row above names its <c>Cluster</c> after the resource.</b> An
    ///     account owns two workloads and the one a driver connects to is the FerretDB
    ///     <c>Deployment</c>; giving the <i>PostgreSQL</i> cluster the bare name would put the
    ///     tenant-facing endpoint on the suffixed object and the internal one on the plain object,
    ///     which is backwards from every direction a human reads it. The suffix also keeps
    ///     CloudNativePG's own generated names — <c>{name}-pg-rw</c>, <c>{name}-pg-app</c>,
    ///     <c>{name}-pg-superuser</c> — visibly the operator's rather than ours.
    /// </remarks>
    public static string ClusterName(string name) => name + "-pg";

    /// <summary>The <c>Cluster</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = ClusterName(name) };

    /// <summary>The FerretDB <c>Deployment</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef DeploymentRef(string ns, string name) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = name };

    /// <summary>The <c>Service</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = name };

    /// <summary>The <c>PodMonitor</c> an account owns while monitoring is on.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PodMonitorRef(string ns, string name) =>
        new() { Kind = PodMonitorKind, Namespace = ns, Name = name };

    /// <summary>The read-write <c>Service</c> CloudNativePG puts in front of the primary.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Not an object this provider applies.</b> <c>GetServiceReadWriteName()</c> is
    ///     <c>{cluster}-rw</c>; the operator also creates <c>-ro</c> and <c>-r</c>. FerretDB reads and
    ///     writes, so it is the <c>-rw</c> one — <c>-r</c> would round-robin writes onto hot standbys
    ///     and every insert would fail with "read-only transaction" on two thirds of the connections.
    /// </remarks>
    public static string PostgresServiceName(string name) => ClusterName(name) + "-rw";

    /// <summary>The <c>Secret</c> CloudNativePG generates the superuser password into.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written by the operator, never by this provider.</b>
    ///         <c>internal/controller/cluster_create.go</c> generates it with
    ///         <c>password.Generate(64, 10, 0, false, true)</c> when
    ///         <see cref="EnableSuperuserAccess" /> is on and no <c>spec.superuserSecret</c> is given.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This row mints nothing into the vault, and the reason is not that piece 5 is
    ///         missing.</b> docs/plan/12 § The pattern, once, piece 5 is built — <c>ISecretWriter</c>
    ///         is the interface and <c>CyberCloud.Vault</c> ships <c>OpenBaoSecretWriter</c>. The
    ///         reason is the paragraph above: CloudNativePG has already put a password in the database
    ///         by the time this reconciler could write one, so a minted credential would be a password
    ///         the cluster never accepted while everything reported success.
    ///         <c>DocumentDbAccountListKeysHandler</c> reads these two keys instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>uri</c> key of THIS secret is unusable and the <c>uri</c> key of the
    ///         application secret is not — a distinction that costs a non-starting pod to learn.</b>
    ///         <c>pkg/specs/secrets.go</c> builds every generated secret's <c>uri</c> from a
    ///         <c>dbname</c>, and <c>cluster_create.go</c> passes <c>"*"</c> for the superuser one. So
    ///         <c>{name}-pg-superuser</c>'s <c>uri</c> is
    ///         <c>postgresql://postgres:…@{name}-pg-rw.{ns}:5432/<b>*</b></c> — a database that does
    ///         not exist. <see cref="DeploymentJson" /> therefore assembles the DSN from the
    ///         <c>username</c> and <c>password</c> keys instead of projecting <c>uri</c>, and the
    ///         string assembly is safe for a checkable reason: the third argument to
    ///         <c>password.Generate</c> is the <i>symbol</i> count and it is <c>0</c>, so a generated
    ///         password is alphanumeric and carries nothing a URL would have to escape.
    ///     </para>
    /// </remarks>
    public static string SuperuserSecretName(string name) => ClusterName(name) + "-superuser";

    /// <summary>The keys inside <see cref="SuperuserSecretName" />.</summary>
    /// <remarks>
    ///     ⚠ The Secret is <c>kubernetes.io/basic-auth</c>, so the two names are the type's rather
    ///     than anyone's choice. <see cref="DeploymentJson" /> already projects both into the
    ///     gateway's environment; <c>listKeys</c> reads the same two, which is what makes the
    ///     credential this action returns the credential the gateway is actually using.
    /// </remarks>
    public const string UsernameKey = "username";

    /// <inheritdoc cref="UsernameKey" />
    public const string PasswordKey = "password";

    /// <summary>The in-cluster MongoDB endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>mongodb://</c> is the scheme every driver expects and it is the one place this type
    ///     says the word. It is a protocol identifier rather than a product claim — see
    ///     <see cref="UnsupportedCommands" /> for what the product page owes alongside it.
    /// </remarks>
    public static string Endpoint(string ns, string name) =>
        "mongodb://"
        + name
        + "."
        + ns
        + ".svc:"
        + MongoPort.ToString(CultureInfo.InvariantCulture)
        + "/";

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied.</b> Four providers kept
    ///     their own copy of this grammar and one of them grew a second <i>parser</i> next to it, in
    ///     <see langword="double" />, which disagreed on value rather than on verdict.
    ///     <c>QuantityParserTests</c> fails if a fresh copy or a second suffix table appears.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>
    ///     An <c>s3://</c> destination, or empty. ⚠ Bare — <c>@pattern</c> is anchored on the way out,
    ///     so anchoring it here would produce <c>^(?:^…$)$</c> in every generated surface.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Character for character the pattern <c>CyberCloud.DBforPostgreSQL/servers</c> declares,
    ///     and it is a SECOND copy rather than a shared constant</b> — the hard rule that forbids the
    ///     assembly reference forbids reaching for the string too. It is spelled out here so that a
    ///     reviewer diffing the two sees one line rather than a call into another provider's
    ///     namespace, which is the trade <c>src/Providers/README.md § Hard rule</c> makes deliberately.
    ///     ⚠ <b>It is NOT the quantity grammar, which is the copy that is forbidden</b> — that one
    ///     lives in <see cref="KubeQuantity" /> in a shared assembly and is referenced above.
    /// </remarks>
    public const string BackupDestinationPattern = @"(s3://[a-z0-9][a-z0-9.\-]*[a-z0-9](/[^\s]*)?)?";

    /// <summary>
    ///     The sizing presets of docs/plan/12 § Sizing vocabulary, <c>s1</c> family — <i>"1:4 ·
    ///     General — most databases"</i>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE PLATFORM HAS TWO <c>s1</c> TABLES ALREADY AND THEY DISAGREE RUNG FOR RUNG.
    ///         THIS IS THE THIRD.</b> docs/plan/12 § Sizing vocabulary opens <i>"One table, defined
    ///         once, used by every service and every VM"</i>, and there is no such table:
    ///         <c>PostgresServers.Presets</c> spells <c>s1.small</c> as <c>(500m, 2Gi)</c> and
    ///         <c>StorageAccounts.Presets</c> spells it <c>(1, 4Gi)</c>. The two are the same ratio one
    ///         rung apart, so a tenant who reads <c>s1.small</c> on two products gets two different
    ///         machines.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And one of the two does not hold the ratio its own family name claims.</b>
    ///         <c>PostgresServers.Presets["s1.nano"]</c> is <c>(100m, 512Mi)</c>, which is 5 GiB per
    ///         core rather than 4, on that rung and no other. This table takes the ratio-correct
    ///         rungs and <c>DocumentDbDeclarationTests.EveryPresetHoldsTheOneToFourRatioTheFamilyNameClaims</c>
    ///         pins every one of them, because reproducing a neighbour's arithmetic to match it would
    ///         be the drift the vocabulary exists to stop. <c>conformance.yaml § owed</c>,
    ///         <c>sizing-table-is-not-shared</c>, says what closes it and why this provider did not:
    ///         a sixth copy in a shared assembly that the five existing tables do not use would be a
    ///         sixth copy.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This sizes the PostgreSQL instances and nothing else.</b> The FerretDB pods are
    ///         <see cref="GatewayCpu" />, which is what makes this type's quota meters a sum over two
    ///         populations rather than <c>replicas × one figure</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["s1.nano"] = ("250m", "1Gi"),
            ["s1.micro"] = ("500m", "2Gi"),
            ["s1.small"] = ("1", "4Gi"),
            ["s1.medium"] = ("2", "8Gi"),
            ["s1.large"] = ("4", "16Gi"),
            ["s1.xlarge"] = ("8", "32Gi"),
            ["s1.2xlarge"] = ("16", "64Gi"),
            ["s1.4xlarge"] = ("32", "128Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     The FerretDB versions this api-version offers, and the two image tags each one is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>ONE PROPERTY, TWO IMAGES, AND THE PAIRING IS THE WHOLE REASON IT IS A TABLE.</b>
    ///         FerretDB and the DocumentDB extension are versioned together — upstream tags the
    ///         PostgreSQL image <c>{pgMajor}-{documentdbVersion}-ferretdb-{ferretdbVersion}</c> — and
    ///         a pair that was never released together is a proxy talking to an extension whose call
    ///         signatures it does not know. Two properties would let a tenant express exactly that
    ///         pair, so there is one, and the table is what turns it back into two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The upgrade ORDER is upstream's and this type cannot yet honour it.</b> The
    ///         documented sequence is: install the matching DocumentDB image first, then move
    ///         FerretDB. One reconcile pass applies both objects, so a version change starts a
    ///         CloudNativePG rolling upgrade and a Deployment rollout at the same moment.
    ///         <c>conformance.yaml § owed</c>, <c>version-upgrade-order-is-not-sequenced</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two members, because FerretDB has no long-term branch.</b> The same shape
    ///         <c>charts/managed/seaweedfs</c> records: <i>"docs/plan/12's 'supported major versions'
    ///         is a shape this project does not have"</i>. These are the two most recent releases as
    ///         of 2026-08-12, and a third is a new api-version rather than an edit to this one.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Gateway, string Postgres)> Versions { get; } =
        new Dictionary<string, (string Gateway, string Postgres)>(StringComparer.Ordinal) {
            ["2.5"] = ("2.5.0", "17-0.106.0-ferretdb-2.5.0"),
            ["2.7"] = ("2.7.0", "17-0.107.0-ferretdb-2.7.0")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     What a MongoDB client cannot do here, as upstream's own compatibility page lists it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS docs/plan/12's SUPPORTED-SUBSET TABLE, AND IT IS IN THE TYPE BECAUSE A
    ///         PRODUCT PAGE IS NOT A PLACE A BUILD CAN FAIL.</b> That document requires the row to
    ///         <i>"say so, with a supported-subset table"</i> and warns that selling it as MongoDB
    ///         <i>"produces a churn event at the first <c>$lookup</c>"</i>. Declared here, the list
    ///         reaches the type's summary, the CLI help and the portal through the same generation the
    ///         schema uses, and
    ///         <c>DocumentDbDeclarationTests.TheCompatibilityStatementNamesTransactionsAndIsInTheSummary</c>
    ///         fails if it is dropped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Transactions are the first entry because they are the one that costs a migration.</b>
    ///         <c>website/docs/migration/compatibility.md</c> lists <c>commitTransaction</c> and
    ///         <c>abortTransaction</c> as not implemented. An application that opens a session and
    ///         commits gets an error at run time, not at connect time, which is the worst moment to
    ///         find out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Change streams are NOT in this list and their absence is honest rather than a
    ///         claim.</b> docs/plan/12's paragraph names them as a gap; upstream's compatibility page
    ///         has no row for them either way, and a search of that documentation tree for
    ///         <c>changeStream</c> returns nothing. Listing them would be repeating a claim this
    ///         provider could not check, and omitting them silently would be worse — so
    ///         <c>conformance.yaml § owed</c>, <c>change-streams-are-unverified</c>, carries the
    ///         question rather than either answer.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> UnsupportedCommands { get; } =
        [
            "commitTransaction",
            "abortTransaction",
            "bulkWrite",
            "convertToCapped",
            "createRole",
            "dropRole",
            "grantRolesToUser",
            "revokeRolesFromUser",
            "setParameter",
            "profile"
        ];

    /// <summary>The one-line compatibility statement every generated surface carries.</summary>
    /// <remarks>
    ///     ⚠ It names the substitute and the largest gap in the same sentence, which is ADR-011's rule
    ///     for <c>CyberCloud.Cache/redis</c> applied to a layer whose difference is behavioural.
    /// </remarks>
    public const string CompatibilityStatement =
        "MongoDB-compatible, not MongoDB: FerretDB over PostgreSQL, covering CRUD, indexes and the "
        + "wire protocol. Multi-document transactions are not supported.";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///     <c>@default</c> directive — charts/README.md § The annotation format — because the chart's
    ///     default <i>is</i> the YAML literal on the annotated line, and <c>ChartAnnotationEmitter</c>
    ///     writes that literal from <see cref="SchemaProperty.DefaultJson" />.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the account is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The account's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the PostgreSQL cluster and the "
                    + "FerretDB gateway."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },

                // ── The chart's API surface, in the chart's own declaration order ───────────────
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Required: true,
                    Description: "FerretDB minor version. ⚠ One property moves two images: the "
                    + "DocumentDB PostgreSQL extension and the FerretDB gateway are released as a "
                    + "matched pair and a mismatched pair is a proxy talking to an extension it does "
                    + "not know. FerretDB maintains no long-term branch, so the two values here are "
                    + "the two most recent releases and a third is a new api-version."
                ) {
                    AllowedValues = [.. Versions.Keys.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"2.7\""
                },
                new(
                    "/properties/postgres",
                    SchemaKind.Nested,
                    Description: "The CloudNativePG cluster the documents actually live in."
                ),
                new(
                    "/properties/postgres/instances",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of PostgreSQL instances, including the primary. One is a "
                    + "single point of failure and is offered for development only. Failover, "
                    + "replication and point-in-time recovery are CloudNativePG's, which is why this "
                    + "row costs 1.2 engineer-months rather than a rebuild of them."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "2"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per PostgreSQL instance, either by preset or "
                    + "explicitly. The FerretDB gateway pods are sized by the platform and are not "
                    + "affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Databases use the s1 family, "
                    + "which is 1 vCPU to 4 GiB."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"s1.small\""
                },
                new(
                    "/properties/sizing/cpu",
                    SchemaKind.Text,
                    Description: "Explicit vCPU quantity in Kubernetes form, for example 500m or 2. "
                    + "Empty means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/sizing/memory",
                    SchemaKind.Text,
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. Empty "
                    + "means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The data volume, per PostgreSQL instance."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Data volume size per PostgreSQL instance, in Kubernetes quantity "
                    + "form. Every instance carries a full copy, so raw consumption is this times the "
                    + "instance count. Grows online; never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"20Gi\"",
                    ExampleJson = "\"20Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for the PostgreSQL volumes. Empty means the "
                    + "cluster default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/gateway",
                    SchemaKind.Nested,
                    Description: "The FerretDB gateway — what speaks the MongoDB wire protocol."
                ),
                new(
                    "/properties/gateway/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of FerretDB pods. The gateway is stateless — it translates "
                    + "and forwards, and every byte is in PostgreSQL — so this is a throughput and "
                    + "availability setting rather than a topology one."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "2"
                },
                new(
                    "/properties/backup",
                    SchemaKind.Nested,
                    Description: "Backup to an object store, using CloudNativePG's barman-cloud. "
                    + "docs/plan/12: because it is PostgreSQL underneath, backup and point-in-time "
                    + "recovery are the operator's."
                ),
                new(
                    "/properties/backup/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether continuous backup and WAL archiving run. ⚠ Off by default, "
                    + "and only because there is no destination to default to: turning it on without "
                    + "a destinationPath below renders no backup configuration at all rather than an "
                    + "empty one, so the two properties have to be set together."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/backup/retentionDays",
                    SchemaKind.WholeNumber,
                    Description: "How long base backups and WAL are kept. The point-in-time-recovery "
                    + "window is this number of days."
                ) {
                    Minimum = 1,
                    Maximum = 365,
                    DefaultJson = "14"
                },
                new(
                    "/properties/backup/destinationPath",
                    SchemaKind.Text,
                    Description: "Object-store URL for base backups and WAL, for example "
                    + "s3://tenant-bucket/documentdb. Empty means no backup configuration is rendered, "
                    + "whatever enabled says."
                ) {
                    Pattern = BackupDestinationPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"s3://tenant-bucket/documentdb\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether both halves of this service are scraped: CloudNativePG is "
                    + "asked for a PodMonitor over the PostgreSQL pods, and the platform writes one "
                    + "over the FerretDB pods because FerretDB has no operator to ask. On by default "
                    + "— docs/plan/12: \"a managed service the tenant cannot see the health of is a "
                    + "black box they will not trust with production\"."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listKeys</c> returns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Declared even though no handler serves it</b>, because an undeclared response is
    ///         the one part of the API surface with no contract. What leaves the platform through a
    ///         <c>secret: true</c> action is exactly the thing that should be written down before it
    ///         leaves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The credential is a PostgreSQL role and the response says so.</b> FerretDB stores
    ///         no users: it forwards a client's credentials to PostgreSQL and returns PostgreSQL's
    ///         verdict. So what a MongoDB driver puts in its connection string is a role that exists in
    ///         the CloudNativePG cluster — today exactly one does, the superuser the operator
    ///         generated.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/endpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster MongoDB endpoint, mongodb://host:port/. ⚠ No external "
                    + "address is returned, because there is none — see the account's own "
                    + "documentation on exposure."
                ),
                new(
                    "/database",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The database the DocumentDB extension is installed in, which is the "
                    + "one a driver authenticates against."
                ),
                new(
                    "/username",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The PostgreSQL role a client authenticates as. Not secret on its "
                    + "own; useless without the password below."
                ),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "That role's password, read from the tenant's Vault for this call "
                    + "only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The FerretDB version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) {
        var declared = Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

        // ⚠ An unrecognised member falls back to the DEFAULT version rather than to the newest or to
        // an empty tag. Unreachable from a validated body — AllowedValues closes it — and it is the
        // fallback that matters: an empty tag renders `ghcr.io/ferretdb/ferretdb:` and fails per pod.
        return Versions.ContainsKey(declared) ? declared : DefaultVersion;
    }

    /// <summary>The FerretDB image a body asks for, repository and tag.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string GatewayImage(JsonElement desired) =>
        GatewayImageRepository + ":" + Versions[Version(desired)].Gateway;

    /// <summary>The PostgreSQL image a body asks for, repository and tag.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PostgresImage(JsonElement desired) =>
        PostgresImageRepository + ":" + Versions[Version(desired)].Postgres;

    /// <summary>The PostgreSQL instance count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Instances(JsonElement desired) =>
        Number(desired, "postgres", "instances", DefaultInstances);

    /// <summary>The FerretDB pod count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int GatewayReplicas(JsonElement desired) =>
        Number(desired, "gateway", "replicas", DefaultGatewayReplicas);

    /// <summary>The data-volume size per PostgreSQL instance a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>Whether the desired body asks for scrape objects.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", DefaultMonitoring);

    /// <summary>
    ///     Whether a backup configuration is rendered at all.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns><c>true</c> only when the flag is on <b>and</b> a destination was given.</returns>
    /// <remarks>
    ///     ⚠ <b>TWO PROPERTIES, ONE ANSWER, AND THE SCHEMA CANNOT SAY SO.</b> An enabled backup with
    ///     an empty <c>destinationPath</c> would render <c>barmanObjectStore.destinationPath: ""</c>,
    ///     which is a backup destination that is not a destination — the cluster would come up and its
    ///     archiver would fail every WAL segment, which is the shape of "backed up" that is worse than
    ///     "not backed up" because it looks like the first. <c>ResourceSchema</c> validates each
    ///     property against constants and has no seam for a relation between two, which is the
    ///     <b>third sighting</b> of that gap after <c>charts/managed/kafka</c>'s
    ///     <c>replication-factor-versus-node-count</c> and <c>charts/managed/seaweedfs</c>'s
    ///     <c>replication-versus-topology</c>. So the renderer answers it, the property descriptions
    ///     say so in both directions, and <c>conformance.yaml § owed</c> carries it.
    /// </remarks>
    public static bool BackupEnabled(JsonElement desired) =>
        Flag(desired, "backup", "enabled", DefaultBackup)
        && Text(desired, "backup", "destinationPath", string.Empty).Length > 0;

    /// <summary>
    ///     The CPU and memory one PostgreSQL instance asks for: the explicit quantities when both are
    ///     given, otherwise the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them —
    ///     which renders no <c>requests</c>/<c>limits</c> pair at all rather than a half-specified one.
    /// </returns>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", DefaultPreset);
        var fallback = Presets.TryGetValue(preset, out var found)
            ? found
            : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The CloudNativePG <c>Cluster</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>shared_preload_libraries</c> is a SIBLING of <c>parameters</c>, not a key in
    ///         it.</b> See <see cref="SharedPreloadLibraries" /> for the CRD field, the webhook that
    ///         refuses the other spelling, and the row above that uses it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>cron.database_name</c> has to name the same database the extension is installed
    ///         in.</b> <c>pg_cron</c> schedules its jobs in exactly one database and defaults to
    ///         <c>postgres</c>; the DocumentDB extension registers background jobs through it. The two
    ///         agree here because <see cref="Database" /> is <c>postgres</c> — written out rather than
    ///         left to the default, so that moving the extension to the application database is one
    ///         edit with the dependency visible next to it.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    /// </remarks>
    public static string ClusterJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);

        var libraries = new JsonArray();
        foreach (var library in SharedPreloadLibraries) {
            libraries.Add(library);
        }

        var storage = new JsonObject { ["size"] = StorageSize(desired) };
        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            storage["storageClass"] = storageClass;
        }

        var spec = new JsonObject {
            ["instances"] = Instances(desired),
            ["imageName"] = PostgresImage(desired),
            // ⚠ 999, not the operator's default of 26 — see PostgresUid.
            ["postgresUID"] = PostgresUid,
            ["postgresGID"] = PostgresGid,
            // ⚠ Written on every apply because the CRD default is false and the extension lives in a
            // database only a superuser can reach — see EnableSuperuserAccess.
            ["enableSuperuserAccess"] = EnableSuperuserAccess,
            ["postgresql"] = new JsonObject {
                ["shared_preload_libraries"] = libraries,
                ["parameters"] = new JsonObject { ["cron.database_name"] = Database }
            },
            ["bootstrap"] = new JsonObject {
                ["initdb"] = new JsonObject {
                    // ⚠ postInitSQL, not postInitApplicationSQL: the first runs in the `postgres`
                    // database and that is where FerretDB connects. See Database.
                    ["postInitSQL"] = new JsonArray { ExtensionStatement }
                }
            },
            ["storage"] = storage,
            ["monitoring"] = new JsonObject { ["enablePodMonitor"] = MonitoringEnabled(desired) }
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            spec["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        if (BackupEnabled(desired)) {
            spec["backup"] = new JsonObject {
                ["retentionPolicy"] =
                    Number(desired, "backup", "retentionDays", DefaultRetentionDays)
                        .ToString(CultureInfo.InvariantCulture)
                    + "d",
                ["barmanObjectStore"] = new JsonObject {
                    ["destinationPath"] = Text(desired, "backup", "destinationPath", string.Empty),
                    ["wal"] = new JsonObject { ["compression"] = "gzip" }
                }
            };
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ClusterName(name) }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>The FerretDB <c>Deployment</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>FERRETDB_LISTEN_ADDR</c> AND <c>FERRETDB_DEBUG_ADDR</c> ARE WRITTEN OUT EVEN
    ///         THOUGH THE IMAGE ALREADY SETS THEM, AND THAT IS THE POINT.</b> The binary's own
    ///         defaults are <c>127.0.0.1:27017</c> and <c>127.0.0.1:8088</c> —
    ///         <c>cmd/ferretdb/main.go</c>, kong tags — and only
    ///         <c>build/ferretdb/production.Dockerfile</c>'s <c>ENV FERRETDB_LISTEN_ADDR=:27017</c> and
    ///         <c>ENV FERRETDB_DEBUG_ADDR=:8088</c> make the process reachable from outside its own
    ///         pod. Two independently defaulted values in two files, and if the image ever stops
    ///         setting them the pod binds to loopback: the Service resolves to a port nothing answers,
    ///         the kubelet's probes fail against the pod IP, and the PodMonitor scrapes nothing. This
    ///         is the same coupling <c>charts/managed/seaweedfs</c> records about
    ///         <c>WORKDIR /data</c> and <c>PersistenceSpec.MountPath</c>, and the consequence here is
    ///         worse: that one was about durability, this one is about the service being reachable at
    ///         all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The DSN is assembled from two <c>secretKeyRef</c>s rather than projecting the
    ///         secret's own <c>uri</c> key.</b> See <see cref="SuperuserSecretName" />: the superuser
    ///         secret's <c>uri</c> names the database <c>*</c>. The assembly uses Kubernetes'
    ///         <c>$(VAR)</c> expansion over two earlier entries in the same <c>env</c> list, and it is
    ///         safe to interpolate without escaping for a checkable reason — CloudNativePG generates
    ///         the password with zero symbols.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The probes ask two different questions and the readiness one is the interesting
    ///         one.</b> <c>/debug/readyz</c> reports that PostgreSQL is reachable <i>and</i> the
    ///         DocumentDB extension is installed; <c>/debug/livez</c> reports only that the process
    ///         accepts connections. Using <c>readyz</c> for liveness would restart every gateway pod
    ///         whenever CloudNativePG failed over, which is the moment they are least useful to
    ///         restart.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The pod template's labels are NOT ADR-013's seven, and they cannot be.</b>
    ///         <c>KubeCommand</c> injects those onto the object's own metadata; a Deployment's
    ///         <c>spec.selector</c> is immutable after create and its pod labels have to match it, so
    ///         a resource whose <c>cybercloud.io/api-version</c> label moved could never be updated
    ///         again. <b>SECOND SIGHTING</b> of <c>charts/managed/nats</c>' <c>pod-labels</c>, on a
    ///         <c>Deployment</c> rather than a <c>StatefulSet</c> — the immutability is the same and
    ///         so is the consequence: a FerretDB pod cannot be attributed to a tenant by label.
    ///     </para>
    /// </remarks>
    public static string DeploymentJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var secret = SuperuserSecretName(name);

        var container = new JsonObject {
            ["name"] = "ferretdb",
            ["image"] = GatewayImage(desired),
            ["ports"] = new JsonArray {
                ContainerPort("mongodb", MongoPort), ContainerPort("debug", DebugPort)
            },
            ["env"] = new JsonArray {
                SecretEnv("FERRETDB_PGUSER", secret, "username"),
                SecretEnv("FERRETDB_PGPASSWORD", secret, "password"),
                new JsonObject {
                    ["name"] = "FERRETDB_POSTGRESQL_URL",
                    ["value"] = "postgres://$(FERRETDB_PGUSER):$(FERRETDB_PGPASSWORD)@"
                    + PostgresServiceName(name)
                    + ":5432/"
                    + Database
                },
                // ⚠ Written out — see the remarks. The image sets both and the binary defaults both
                // to loopback.
                new JsonObject {
                    ["name"] = "FERRETDB_LISTEN_ADDR",
                    ["value"] = ":" + MongoPort.ToString(CultureInfo.InvariantCulture)
                },
                new JsonObject {
                    ["name"] = "FERRETDB_DEBUG_ADDR",
                    ["value"] = ":" + DebugPort.ToString(CultureInfo.InvariantCulture)
                },
                // ⚠ Telemetry is a call home and a managed service does not make one on a tenant's
                // behalf. `disable` is the documented value; leaving it `undecided` sends a beacon an
                // hour after start.
                new JsonObject { ["name"] = "FERRETDB_TELEMETRY", ["value"] = "disable" }
            },
            ["readinessProbe"] = Probe("/debug/readyz", 5, 3),
            ["livenessProbe"] = Probe("/debug/livez", 10, 5),
            ["resources"] = new JsonObject {
                ["requests"] = GatewayQuantities(), ["limits"] = GatewayQuantities()
            }
        };

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["replicas"] = GatewayReplicas(desired),
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject { ["labels"] = Selector(name) },
                    ["spec"] = new JsonObject { ["containers"] = new JsonArray { container } }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>Service</c> document a MongoDB driver connects to.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It takes no body, and that is deliberate.</b> Nothing a tenant can set changes it,
    ///         so a signature that accepted the desired body would invite somebody to make one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>ClusterIP</c>, and there is no external option at any setting.</b>
    ///         docs/plan/12 § Cross-cutting decisions requires an explicit CIDR allow-list on any
    ///         exposure; that is expressible here — a <c>Service</c> carries
    ///         <c>loadBalancerSourceRanges</c>, unlike the SeaweedFS operator's four-field subset — and
    ///         it is not offered, because the credential behind the endpoint is a PostgreSQL role
    ///         whose password this platform cannot yet rotate. Exposure without
    ///         <c>regenerateKeys</c> is a public database whose password can never be changed.
    ///         <c>conformance.yaml § owed</c>, <c>external-exposure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The debug port is never on this Service.</b> <c>/debug/archive</c> returns a zip of
    ///         the process's internal state on the same listener as the metrics, so a routable address
    ///         for the metrics is a routable address for that. The <c>PodMonitor</c> scrapes the pod
    ///         directly and loses nothing.
    ///     </para>
    /// </remarks>
    public static string ServiceJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["type"] = "ClusterIP",
                ["selector"] = Selector(name),
                ["ports"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "mongodb",
                        ["port"] = MongoPort,
                        ["targetPort"] = "mongodb",
                        ["protocol"] = "TCP"
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>PodMonitor</c> document that scrapes the FerretDB pods.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>docs/plan/12 § The pattern, once, piece 6's SECOND branch — and this row takes
    ///         BOTH branches at once, which nothing before it has.</b> The corrected piece 6 reads
    ///         <i>"ask the operator for the scrape object wherever the operator accepts the request,
    ///         and hand-write one into the chart only when there is no operator to ask."</i>
    ///         CloudNativePG accepts the request for the PostgreSQL half —
    ///         <c>spec.monitoring.enablePodMonitor</c>, rendered by <see cref="ClusterJson" /> — and
    ///         there is no FerretDB operator at all, so this half is hand-written. One resource, one
    ///         <c>monitoring.enabled</c> flag, two mechanisms.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the hazard the second branch exists to warn about cannot arise here, for
    ///         exactly the reason <c>charts/managed/nats</c> proved.</b> That warning is that a
    ///         hand-written scrape hard-codes somebody else's pod labels and goes quiet without failing
    ///         when the operator moves one. The labels this selector matches are written by
    ///         <see cref="DeploymentJson" /> onto pods created by <see cref="DeploymentJson" />. Both
    ///         halves of piece 6 are discharged rather than owed, and it is the first row where that
    ///         sentence needed two mechanisms to be true.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>path</c> is <c>/debug/metrics</c> and not <c>/metrics</c> — see
    ///         <see cref="MetricsPath" />. ⚠ And upstream says <i>"the set of metrics is not stable
    ///         yet"</i>, which is a dashboard problem rather than a scrape problem and is recorded at
    ///         <c>conformance.yaml § owed</c>, <c>grafana-dashboard</c>.
    ///     </para>
    /// </remarks>
    public static string PodMonitorJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                ["podMetricsEndpoints"] = new JsonArray {
                    new JsonObject { ["port"] = "debug", ["path"] = MetricsPath }
                }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns><c>true</c> when the fields this provider owns hold the desired values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, not equality, and this type needs it for THREE independent reasons
    ///         because it renders objects from three different worlds.</b> Checked against each one
    ///         rather than assumed from the pattern:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <b>The <c>Cluster</c> is edited by its operator.</b> CloudNativePG writes a large
    ///             <c>.status</c>, and — the half that matters — <c>spec.postgresql</c> comes back
    ///             carrying the operator's own managed libraries merged into
    ///             <c>shared_preload_libraries</c>: <c>api/v1/cluster_types.go</c> calls the field
    ///             <i>"lists of shared preload libraries to add to the <b>default ones</b>"</i>. An
    ///             equality comparison on that list fails on the first read-back of a correct cluster.
    ///         </item>
    ///         <item>
    ///             <b>The <c>Deployment</c> and the <c>Service</c> are the most heavily defaulted
    ///             objects in Kubernetes.</b> <c>strategy</c>, <c>revisionHistoryLimit</c>,
    ///             <c>progressDeadlineSeconds</c>, <c>terminationMessagePath</c>,
    ///             <c>imagePullPolicy</c>, <c>dnsPolicy</c>, <c>clusterIP</c>, <c>ipFamilies</c>,
    ///             <c>sessionAffinity</c> — none of them sent, all of them returned. This is
    ///             <c>NatsClusters.Matches</c>' finding, and it applies to a built-in kind whichever
    ///             provider renders it.
    ///         </item>
    ///         <item>
    ///             <b>The <c>PodMonitor</c> is a custom resource whose CRD may or may not default
    ///             anything, and this provider does not depend on knowing which.</b>
    ///             <c>charts/managed/kafka</c> found Strimzi declares no defaults and
    ///             <c>charts/managed/seaweedfs</c> found the opposite about its operator, so the safe
    ///             reading is the one that is right either way.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ Dispatches on the object's <c>kind</c>, because a conformance case supplies this as
    ///         one function over every object the resource owns and an unrecognised document must be
    ///         <c>false</c> rather than assumed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it deliberately does NOT compare is anything derived from the resource's
    ///         ADDRESS.</b> The signature carries an object and a body and no id, so the object names,
    ///         the selector labels and the <c>Secret</c> reference are checked for <i>presence and
    ///         shape</i> rather than for value. <c>DocumentDbReconcilerTests</c> asserts the values
    ///         against a real address, including two accounts in one resource group.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document || document["spec"] is not JsonObject spec) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "Cluster" => MatchesCluster(spec, desired),
            "Deployment" => MatchesDeployment(spec, desired),
            "Service" => MatchesService(spec),
            "PodMonitor" => MatchesPodMonitor(spec),
            _ => false
        };
    }

    static bool MatchesCluster(JsonObject spec, JsonElement desired) =>
        spec["instances"]?.GetValue<int>() == Instances(desired)
        && spec["imageName"]?.GetValue<string>() == PostgresImage(desired)
        && (spec["storage"] as JsonObject)?["size"]?.GetValue<string>() == StorageSize(desired)
        && (spec["monitoring"] as JsonObject)?["enablePodMonitor"]?.GetValue<bool>()
        == MonitoringEnabled(desired)
        // ⚠ THE FIELD WHOSE ABSENCE TAKES THE SERVICE DOWN AND WHICH NOTHING ELSE WOULD REPORT. When
        // this is false CloudNativePG DELETES the superuser Secret the gateway mounts, and what is
        // left is a healthy PostgreSQL, a healthy Deployment object, and pods that never start. See
        // EnableSuperuserAccess.
        && spec["enableSuperuserAccess"]?.GetValue<bool>() == EnableSuperuserAccess
        // ⚠ CONTAINMENT ON THE LIBRARY LIST, because the operator ADDS its own to it.
        && spec["postgresql"] is JsonObject postgresql
        && SharedPreloadLibraries.All(
            library => postgresql["shared_preload_libraries"] is JsonArray declared
                && declared.Any(x => x?.GetValue<string>() == library)
        );

    static bool MatchesDeployment(JsonObject spec, JsonElement desired) =>
        spec["replicas"]?.GetValue<int>() == GatewayReplicas(desired)
        && (((spec["template"] as JsonObject)?["spec"] as JsonObject)?["containers"] as JsonArray)
            ?.OfType<JsonObject>()
            .Any(x => x["image"]?.GetValue<string>() == GatewayImage(desired))
        == true;

    static bool MatchesService(JsonObject spec) =>
        (spec["ports"] as JsonArray)?.OfType<JsonObject>().Any(x => x["port"]?.GetValue<int>() == MongoPort)
        == true;

    static bool MatchesPodMonitor(JsonObject spec) =>
        (spec["podMetricsEndpoints"] as JsonArray)
        ?.OfType<JsonObject>()
        .Any(x => x["path"]?.GetValue<string>() == MetricsPath)
        == true;

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the account in.</param>
    /// <param name="instances">How many PostgreSQL instances.</param>
    /// <param name="storageSize">The data volume size per instance.</param>
    /// <param name="gatewayReplicas">How many FerretDB pods.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int instances = 2,
        string storageSize = "20Gi",
        int gatewayReplicas = 2,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["postgres"] = new JsonObject { ["instances"] = instances },
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["gateway"] = new JsonObject { ["replicas"] = gatewayReplicas },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "2.7";
    const string DefaultPreset = "s1.small";
    const string DefaultStorageSize = "20Gi";
    const int DefaultInstances = 2;
    const int DefaultGatewayReplicas = 2;
    const int DefaultRetentionDays = 14;
    const bool DefaultMonitoring = true;
    const bool DefaultBackup = false;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The labels the <c>Deployment</c>'s selector, its pod template and the <c>PodMonitor</c> all
    ///     match on.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>None of these is one of ADR-013's seven, and none of them may ever become one.</b> A
    ///     Deployment's <c>spec.selector</c> is immutable after create, so every key here has to be
    ///     stable for the life of the resource — and <c>cybercloud.io/api-version</c> is by
    ///     construction not. See <see cref="DeploymentJson" />.
    /// </remarks>
    static JsonObject Selector(string name) =>
        new() {
            ["app.kubernetes.io/name"] = "ferretdb",
            ["app.kubernetes.io/instance"] = name,
            ["app.kubernetes.io/component"] = "gateway",
            ["app.kubernetes.io/managed-by"] = "cybercloud"
        };

    static JsonObject GatewayQuantities() =>
        new() { ["cpu"] = GatewayCpu, ["memory"] = GatewayMemory };

    static JsonObject ContainerPort(string portName, int port) =>
        new() { ["name"] = portName, ["containerPort"] = port, ["protocol"] = "TCP" };

    static JsonObject SecretEnv(string variable, string secret, string key) =>
        new() {
            ["name"] = variable,
            ["valueFrom"] = new JsonObject {
                ["secretKeyRef"] = new JsonObject { ["name"] = secret, ["key"] = key }
            }
        };

    static JsonObject Probe(string path, int period, int failureThreshold) =>
        new() {
            ["httpGet"] = new JsonObject { ["path"] = path, ["port"] = "debug" },
            ["periodSeconds"] = period,
            ["failureThreshold"] = failureThreshold
        };

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string parent, string name, string fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
