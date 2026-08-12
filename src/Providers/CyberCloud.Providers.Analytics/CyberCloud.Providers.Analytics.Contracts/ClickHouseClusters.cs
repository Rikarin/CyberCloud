using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Analytics/clickhouseClusters</c>: the type, its
///     api-version, its body shape, and the <b>two</b> Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE PLATFORM RUNS CLICKHOUSE AND THE PLATFORM'S CLICKHOUSE IS NOT ONE OF THESE, AND
///         GETTING THAT BACKWARDS IS THE EXPENSIVE MISTAKE ON THIS ROW.</b>
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md) says <i>"We run
///         ClickHouse for telemetry and metering"</i> and cites
///         [16](../../../../docs/plan/16-observability.md) and
///         [22](../../../../docs/plan/22-billing-metering-and-quota.md). Both of those describe a
///         <b>platform-owned, per-region, database-per-tenant</b> store —
///         [05 § Every store](../../../../docs/plan/05-state-and-storage.md) spells it out in one row,
///         <i>"ClickHouse · Logs, traces, metering rollups, resource graph · Per region;
///         database-per-tenant; shard+replica"</i> — and this type is a <b>single-tenant cluster in a
///         tenant's own namespace whose schema the tenant owns</b>. Four reasons they cannot be the
///         same thing, in the order that decides it:
///     </para>
///     <list type="number">
///         <item>
///             <b>The tenancy shape is opposite.</b> docs/plan/16 § Ingest routes to <i>"ClickHouse
///             (per-tenant database)"</i> on one cluster; this type gives one tenant one cluster. A
///             resource that held every tenant's logs would be a resource one tenant could delete.
///         </item>
///         <item>
///             <b>The schema owner is opposite, and it is this row's own scope boundary.</b>
///             docs/plan/12: <i>"Schema is the tenant's problem and the resource does not manage
///             tables."</i> The platform's store has platform-authored schema — docs/plan/22's
///             <c>usage_raw</c> is a <c>ReplacingMergeTree</c> keyed on the idempotency key, and
///             docs/plan/08 § the resource-graph projection is another. A resource type that does not
///             manage tables cannot be the thing whose tables are the product.
///         </item>
///         <item>
///             <b>It would be a dependency cycle.</b> <c>CyberCloud.Ingest.Host</c> is deliberately
///             <i>not</i> an Orleans client (docs/plan/03, docs/plan/16 § Ingest) so that telemetry
///             never runs through a grain call. If the store it writes to were a resource of this
///             type, reconciling it would emit telemetry that needed the store the reconcile was
///             creating.
///         </item>
///         <item>
///             <b>The tenant-facing resource for observability already exists and is a different
///             one.</b> docs/plan/16 § <c>CyberCloud.Monitor/workspaces</c> is what a tenant buys to
///             get logs and traces, and <i>"Platform telemetry uses the same machinery under a
///             platform workspace. No separate stack."</i> A tenant who wants ClickHouse-the-log-store
///             buys a workspace; a tenant who wants ClickHouse-the-database buys this.
///         </item>
///     </list>
///     <para>
///         ⚠ What docs/plan/12's sentence actually claims is narrower and is true:
///         <i>"the operational knowledge is not incremental"</i> — we already run the engine, so the
///         operator survey, the runbook and the version discipline are shared.
///         [01 § the parity catalogue](../../../../docs/plan/01-azure-parity-catalogue.md)'s
///         <i>"Also our own telemetry store, so it gets built either way"</i> is the same claim about
///         the same thing: the <b>engine</b> gets built either way, not the <b>resource type</b>.
///     </para>
///     <para>
///         ⚠ <b>TWO OBJECTS, AND THE SECOND ONE IS THE FINDING.</b> docs/plan/12 says
///         <i>"ZooKeeper/ClickHouse Keeper managed by the operator"</i>, which is half true in a way
///         that costs an object. The Altinity operator does serve a Keeper CRD —
///         <see cref="KeeperKind" />, <c>clickhouse-keeper.altinity.com/v1</c> — but a
///         <see cref="ClickHouseKind" /> <b>does not create one for itself</b>: it names one, by
///         Service, in <c>spec.configuration.zookeeper.nodes</c>. Upstream's own
///         <c>docs/chk-examples/01-chi-simple-with-keeper.yaml</c> is two documents and the comment on
///         the host line reads <i>"This is a service name of chk/simple-1"</i>. So the platform applies
///         both and wires the first to the second — see <see cref="KeeperServiceName" />.
///     </para>
///     <para>
///         ⚠ <b>The Keeper is unconditional, and that is a decision rather than a default.</b> A
///         one-shard one-replica cluster does not need coordination to <i>run</i>, and a tenant who
///         creates a <c>ReplicatedMergeTree</c> on it — which is the ordinary thing to create, and
///         which the tenant is entitled to because the schema is theirs — gets
///         <c>Coordination::Exception</c> at DDL time rather than at create time. Making the Keeper
///         appear when <c>replicas</c> crosses one would be worse still: the tables that already exist
///         would have been created without it. The Kafka manifest's argument, in a different service:
///         a cluster without its coordination is not a smaller cluster, it is one with nowhere to put
///         its replication log.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS TYPE DELIBERATELY DOES NOT DO.</b> docs/plan/12: <i>"Schema is the tenant's
///         problem and the resource does not manage tables. A managed ClickHouse that tries to own DDL
///         is a migration tool nobody asked for."</i> So: no <c>databases</c> or <c>tables</c> child
///         types, no <c>CREATE</c> on any path, and — the one that is not obvious —
///         <c>spec.configuration.clusters[].schemaPolicy</c> is <b>left unset</b>. That field tells the
///         operator how much of an existing replica's schema to copy onto a new one when a cluster is
///         scaled out; it is the operator's own answer to its own problem, and the platform taking a
///         position on it would be the platform having an opinion about the tenant's tables.
///     </para>
///     <para>
///         ⚠ <b>S3-BACKED COLD STORAGE IS THE ONE BULLET OF docs/plan/12's ROW THAT IS NOT BUILT</b>,
///         and the reason is a seam rather than effort — <c>charts/managed/clickhouse/conformance.yaml
///         § owed</c>, <c>s3-cold-tier</c>. It needs a bucket endpoint and an access-key pair, which is
///         <c>CyberCloud.Storage/accounts</c>; a provider may not reference another provider (rule 2)
///         and the sanctioned route — a resource id through <c>CyberCloud.ResourceManager</c> — has no
///         reader a reconciler can call. The credential half is piece 5, which does not exist either.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/clickhouse/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class ClickHouseClusters {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>A new family, and the sixth in the tree.</b> docs/plan/03 § Providers plans a
    ///     <c>Data</c> namespace holding <i>"postgres, valkey, mongo, clickhouse, opensearch,
    ///     qdrant"</i>; docs/plan/12 and
    ///     [01 § the parity catalogue](../../../../docs/plan/01-azure-parity-catalogue.md) both spell
    ///     this row <c>CyberCloud.Analytics/clickhouseClusters</c>, which is the Azure-parity shape
    ///     (Azure Data Explorer is <c>Microsoft.Kusto</c>, not <c>Microsoft.DBforPostgreSQL</c>). The
    ///     catalogue documents win: every shipped provider so far took its namespace from the row
    ///     rather than from docs/plan/03's grouping, and <c>CyberCloud.DBforPostgreSQL</c> is the
    ///     precedent.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.Analytics";

    /// <summary>The resource type. docs/plan/12 § The catalogue.</summary>
    public const string TypePath = "clickhouseClusters";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/clickhouse/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/clickhouse";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the endpoints and a credential.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions: <i>"<c>listKeys</c> is an action with its own
    ///     permission, audited on every call"</i>. ⚠ <c>regenerateKeys</c> is named in the same
    ///     paragraph and is <b>not</b> declared, for the reason the five providers before this one
    ///     give: it is specified with a rolling grace period and nothing in the platform can hold two
    ///     live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of a cluster a holder of its database credentials.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The two objects a cluster IS ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>ClickHouseInstallation</c> — the servers, their storage and their coordination
    ///     pointer.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>clickhouseinstallations</c>.</b> It is carried rather than derived for
    ///     the reason <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed
    ///     harness derives a definition stub from — <c>ClusterConformanceHarness</c> reads it off
    ///     <c>ProviderConformanceCase.Objects</c>.
    /// </remarks>
    public static GroupVersionKind ClickHouseKind { get; } =
        new() {
            Group = "clickhouse.altinity.com",
            Version = "v1",
            Kind = "ClickHouseInstallation",
            Plural = "clickhouseinstallations"
        };

    /// <summary>
    ///     The <c>ClickHouseKeeperInstallation</c> — the Raft quorum the servers replicate through.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A DIFFERENT API GROUP FROM THE CLUSTER, WHICH IS WHY IT IS A SECOND STUB IN THE
    ///     CLUSTER-BACKED SUITE RATHER THAN A SECOND KIND IN ONE.</b> It is
    ///     <c>clickhouse-keeper.altinity.com</c>, hyphen and all, and it is served by the <i>same</i>
    ///     operator binary. A reader who assumed one group would install one definition and get the
    ///     status-code-less <c>HttpOperationException</c> src/Providers/README.md § What the third
    ///     provider measured describes.
    /// </remarks>
    public static GroupVersionKind KeeperKind { get; } =
        new() {
            Group = "clickhouse-keeper.altinity.com",
            Version = "v1",
            Kind = "ClickHouseKeeperInstallation",
            Plural = "clickhousekeeperinstallations"
        };

    /// <summary>The <c>ClickHouseInstallation</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClickHouseRef(string ns, string name) =>
        new() { Kind = ClickHouseKind, Namespace = ns, Name = name };

    /// <summary>The <c>ClickHouseKeeperInstallation</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>The same name as the cluster, deliberately.</b> The two are different kinds in
    ///     different API groups, so the names cannot collide, and giving the Keeper a suffix would put
    ///     that suffix inside the operator's own Service prefix — <c>keeper-{name}-keeper</c> — which
    ///     is a connection string nobody would guess right.
    /// </remarks>
    public static ObjectRef KeeperRef(string ns, string name) =>
        new() { Kind = KeeperKind, Namespace = ns, Name = name };

    // ── Names and ports the operator owns ─────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>Service</c> the operator puts in front of a Keeper installation.
    /// </summary>
    /// <param name="name">The Keeper installation's name, which is the resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b><c>keeper-</c> is the operator's prefix and is the single most load-bearing string in
    ///     this file, because getting it wrong produces a cluster that comes up, converges, serves
    ///     <c>SELECT 1</c>, and cannot create a replicated table.</b> Taken from upstream's own
    ///     <c>docs/chk-examples/01-chi-simple-with-keeper.yaml</c>, whose CHI names
    ///     <c>host: keeper-simple-1</c> against a CHK called <c>simple-1</c> with the comment
    ///     <i>"This is a service name of chk/simple-1"</i>. Nothing this provider applies creates that
    ///     Service, so nothing this provider applies would fail if the prefix were wrong.
    /// </remarks>
    public static string KeeperServiceName(string name) => "keeper-" + name;

    /// <summary>
    ///     The <c>Service</c> the operator puts in front of a ClickHouse installation.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>clickhouse-</c>, and confirmed from the <i>other</i> side rather than from a naming
    ///     convention: the operator's own hardening guide prints the <c>users.xml</c> it generates, and
    ///     the <c>default</c> user's <c>host_regexp</c> contains
    ///     <c>clickhouse\-my-cluster\.test\.svc\.cluster\.local</c> for a CHI called
    ///     <c>my-cluster</c> in namespace <c>test</c>.
    /// </remarks>
    public static string ClientServiceName(string name) => "clickhouse-" + name;

    /// <summary>The Keeper client port. ⚠ ZooKeeper's, because Keeper speaks ZooKeeper's protocol.</summary>
    public const int KeeperClientPort = 2181;

    /// <summary>The HTTP interface port — what a JDBC/HTTP client and the portal use.</summary>
    public const int HttpPort = 8123;

    /// <summary>The native TCP interface port — what <c>clickhouse-client</c> uses.</summary>
    public const int NativePort = 9000;

    /// <summary>
    ///     The port ClickHouse serves its own Prometheus endpoint on when monitoring is asked for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is docs/plan/12 § The pattern, once, piece 6 reaching an answer neither of its two
    ///     branches describes, and it is worth stating rather than filing under the closest one.</b>
    ///     Piece 6 says <i>"ask the operator for the scrape object wherever the operator accepts the
    ///     request, and hand-write one into the chart only when there is no operator to ask."</i> There
    ///     <i>is</i> an operator here and it does not accept the request: the Altinity operator exports
    ///     metrics for every installation it manages through one cluster-wide exporter on its own pod,
    ///     and there is no per-installation <c>ServiceMonitor</c> switch on the CHI at all. What the
    ///     CRD does accept is <c>spec.configuration.settings</c>, which turns on <b>ClickHouse's own</b>
    ///     Prometheus endpoint — upstream's <c>docs/chi-examples/17-monitoring-02-prometheus-local-endpoint.yaml</c>
    ///     sets exactly the six keys <see cref="ClickHouseJson" /> writes, and pairs them with a
    ///     hand-written <c>PodMonitor</c> selecting <c>clickhouse.altinity.com/app: chop</c>.
    ///     <para>
    ///         ⚠ So the metrics are made to <i>exist</i> here and the object that scrapes them is
    ///         <b>owed</b>, which is the Kafka outcome reached for the opposite reason — and the hazard
    ///         piece 6's correction names is live: that selector is the <i>operator's</i> pod label, so
    ///         a hand-written scrape here would go quiet without failing if Altinity renamed it. See
    ///         <c>conformance.yaml § owed</c>, <c>scrape-object</c>.
    ///     </para>
    /// </remarks>
    public const int MetricsPort = 9363;

    /// <summary>The ClickHouse server image, without a tag.</summary>
    public const string ServerImageRepository = "clickhouse/clickhouse-server";

    /// <summary>The ClickHouse Keeper image, without a tag.</summary>
    /// <remarks>
    ///     ⚠ <b>Tagged with the same version as the server, and that is a statement rather than a
    ///     convenience.</b> Keeper and server share a release train and a wire protocol; a cluster
    ///     whose coordination is two majors ahead of its servers is a combination nobody tests. One
    ///     property, two images — which is also why <c>/properties/version</c> is described as the
    ///     cluster's version rather than the server's.
    /// </remarks>
    public const string KeeperImageRepository = "clickhouse/clickhouse-keeper";

    /// <summary>The Keeper's own volume, per docs/plan/05's durability rules.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a property, and it still costs quota.</b> A Keeper's log and snapshots are small
    ///     and bounded by the number of parts in the cluster rather than by the tenant's data, so there
    ///     is no knob worth publishing. What it is not is free: a three-node quorum is three of these,
    ///     and <c>AnalyticsProvider.StorageDrawn</c> counts them for the reason
    ///     <c>CyberCloud.Storage/accounts</c> counts its filer's.
    /// </remarks>
    public const string KeeperVolumeSize = "10Gi";

    /// <summary>The CPU every Keeper pod requests.</summary>
    /// <remarks>
    ///     ⚠ A constant sized by the platform rather than by the tenant, exactly as
    ///     <c>StorageAccounts.ControlPlaneCpu</c> is. It is the second half of what makes this type's
    ///     meters a sum over heterogeneous components — see <c>AnalyticsProvider</c>.
    /// </remarks>
    public const string KeeperCpu = "250m";

    /// <summary>The memory every Keeper pod requests. See <see cref="KeeperCpu" />.</summary>
    public const string KeeperMemory = "512Mi";

    /// <summary>The name of the single ClickHouse cluster inside the installation.</summary>
    /// <remarks>
    ///     ⚠ <b>It reaches SQL, which is why it is a constant with a reason and not an incidental
    ///     string.</b> A tenant writes <c>ON CLUSTER '{cluster}'</c> and <c>Distributed(...)</c> against
    ///     this name, so changing it later would break every DDL statement a tenant had written — and
    ///     the schema is theirs, so the platform would be breaking something it cannot see.
    ///     <c>default</c> is what upstream's own examples use and what a reader expects.
    /// </remarks>
    public const string ClusterName = "default";

    /// <summary>The in-cluster HTTP endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>http</c> and not <c>https</c>, and that is a statement rather than an oversight. The
    ///     operator will serve TLS when given certificates, and nothing in this platform issues them to
    ///     a tenant's data plane yet — see <c>conformance.yaml § owed</c>, <c>tls-and-exposure</c>.
    /// </remarks>
    public static string HttpEndpoint(string ns, string name) =>
        "http://"
        + ClientServiceName(name)
        + "."
        + ns
        + ".svc:"
        + HttpPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The in-cluster native-protocol endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string NativeEndpoint(string ns, string name) =>
        ClientServiceName(name)
        + "."
        + ns
        + ".svc:"
        + NativePort.ToString(CultureInfo.InvariantCulture);

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied.</b> Four providers kept
    ///     their own copy of this grammar and one of them grew a second <i>parser</i> next to it, in
    ///     <see langword="double" />, which disagreed on value rather than on verdict.
    ///     <c>QuantityParserTests</c> fails if a fifth copy or a second suffix table appears.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>m1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>The one row of that table that names this service by name.</b> <c>m1.*</c> is
    ///     <i>"1:8 · Memory-bound — caches, <b>analytics</b>"</i>, so unlike
    ///     <c>CyberCloud.Storage/accounts</c> — which had to take the closest family and say so — this
    ///     type is the family's stated case.
    ///     <para>
    ///         ⚠ <b>The values are the same eight rows <c>ValkeyCaches.Presets</c> carries, character
    ///         for character, and that is correct rather than duplication to remove.</b> One vocabulary
    ///         means one table of numbers; two providers in the same family agreeing is the property,
    ///         and a provider referencing another provider's table is what rule 2 forbids. What may not
    ///         be copied is the quantity <i>grammar</i> and its parser, which is why
    ///         <see cref="QuantityPattern" /> points at <see cref="KubeQuantity" /> instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This sizes the ClickHouse servers and nothing else.</b> The Keeper pods are sized by
    ///         <see cref="KeeperCpu" />, which is what makes this type's quota meters a
    ///         <i>product</i> and a <i>sum</i> at once — see <c>AnalyticsProvider</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["m1.nano"] = ("100m", "1Gi"),
            ["m1.micro"] = ("250m", "2Gi"),
            ["m1.small"] = ("500m", "4Gi"),
            ["m1.medium"] = ("1", "8Gi"),
            ["m1.large"] = ("2", "16Gi"),
            ["m1.xlarge"] = ("4", "32Gi"),
            ["m1.2xlarge"] = ("8", "64Gi"),
            ["m1.4xlarge"] = ("16", "128Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

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
                    Description: "The region the cluster is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The cluster's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the ClickHouse cluster."
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
                    Description: "ClickHouse version, which is also the ClickHouse Keeper version — the "
                    + "two share a release train and a wire protocol. Only long-term-support lines are "
                    + "offered; a new api-version is what adds a third."
                ) {
                    AllowedValues = ["24.8", "25.3"],
                    DefaultJson = "\"25.3\""
                },
                new(
                    "/properties/shards",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of shards. This is the capacity and parallelism axis: a table's "
                    + "data is split across shards and a query fans out to all of them. ⚠ Resharding an "
                    + "existing table is not something the operator or this resource does, so growing "
                    + "this moves new data only."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "1"
                },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of replicas per shard. This is the availability axis, and it "
                    + "only applies to tables the tenant creates as Replicated — the resource does not "
                    + "manage tables. Total server count is shards times replicas."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "2"
                },
                new(
                    "/properties/keeperNodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of ClickHouse Keeper nodes. Keeper is a Raft quorum, so three "
                    + "is the smallest count that survives losing one and an even count tolerates no "
                    + "more failures than the odd count below it. One is offered for development and "
                    + "has no quorum at all."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per ClickHouse server, either by preset or explicitly. "
                    + "The Keeper nodes are sized by the platform and are not affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Analytics uses the m1 family, which "
                    + "is 1 vCPU to 8 GiB."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"m1.small\""
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
                    Description: "Explicit memory quantity in Kubernetes form, for example 8Gi. Empty "
                    + "means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The data volume, per ClickHouse server."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Data volume size per ClickHouse server, in Kubernetes quantity form. "
                    + "Grows online; never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for the ClickHouse servers and the Keeper nodes. "
                    + "Empty means the cluster default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether ClickHouse's own Prometheus endpoint is served on port 9363. "
                    + "On by default — docs/plan/12: \"a managed service the tenant cannot see the "
                    + "health of is a black box they will not trust with production\". ⚠ It makes the "
                    + "metrics exist; the object that scrapes them is not built — see "
                    + "conformance.yaml § owed."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listKeys</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Declared even though no handler serves it, because an undeclared response is the one
    ///     part of the API surface with no contract.</b> What leaves the platform through a
    ///     <c>secret: true</c> action is exactly the thing that should be written down before it
    ///     leaves. There is no request shape, for the reason <c>ActionRegistration</c> gives.
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/httpEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster HTTP endpoint, http://host:port. ⚠ No external address "
                    + "is returned, because there is none — see the cluster's own documentation on "
                    + "exposure."
                ),
                new(
                    "/nativeEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster native-protocol endpoint, host:port, for "
                    + "clickhouse-client and every driver that speaks the binary protocol."
                ),
                new(
                    "/clusterName",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The name to write in ON CLUSTER and in a Distributed table's first "
                    + "argument. Returned rather than left for the caller to guess, because it is the "
                    + "one platform-chosen string that reaches the tenant's SQL."
                ),
                new(
                    "/username",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The user the credential below belongs to."
                ),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The password, read from the tenant's Vault for this call only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The ClickHouse version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>The shard count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Shards(JsonElement desired) => Number(desired, "shards", DefaultShards);

    /// <summary>The per-shard replica count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Replicas(JsonElement desired) => Number(desired, "replicas", DefaultReplicas);

    /// <summary>The Keeper node count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int KeeperNodes(JsonElement desired) =>
        Number(desired, "keeperNodes", DefaultKeeperNodes);

    /// <summary>
    ///     How many ClickHouse server pods a body asks for.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>A PRODUCT, AND THE ONE FACT ABOUT THIS TYPE THAT A DERIVATION COPIED FROM AN EARLIER
    ///     PROVIDER GETS WRONG.</b> <c>spec.configuration.clusters[].layout</c> carries
    ///     <c>shardsCount</c> and <c>replicasCount</c> as two numbers, and the operator creates one
    ///     StatefulSet per <i>host</i> — which is one per (shard, replica) pair. A four-shard
    ///     three-replica cluster is twelve servers, not four and not seven. It is spelled once, here,
    ///     so that the meters, the object and any future reader are reading the same multiplication.
    /// </remarks>
    public static int Servers(JsonElement desired) => Shards(desired) * Replicas(desired);

    /// <summary>The data-volume size per server a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>The StorageClass a body asks for, or the empty string for the cluster default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageClass(JsonElement desired) =>
        Text(desired, "storage", "class", string.Empty);

    /// <summary>Whether the desired body asks for ClickHouse's own Prometheus endpoint.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", true);

    /// <summary>
    ///     The CPU and memory one ClickHouse server asks for: the explicit quantities when both are
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

    /// <summary>The <c>ClickHouseKeeperInstallation</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Applied FIRST, and the ordering is in the reconciler rather than here.</b> A CHI whose
    ///     ZooKeeper nodes name a Service that does not exist yet is not an error — ClickHouse retries
    ///     the connection — but it is a cluster that logs coordination failures for as long as the gap
    ///     lasts, and there is no reason to open one.
    /// </remarks>
    public static string KeeperJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var container = new JsonObject {
            ["name"] = "clickhouse-keeper",
            ["image"] = KeeperImageRepository + ":" + Version(desired),
            ["resources"] = new JsonObject {
                ["requests"] = KeeperQuantities(), ["limits"] = KeeperQuantities()
            }
        };

        var claim = new JsonObject {
            ["accessModes"] = new JsonArray { "ReadWriteOnce" },
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["storage"] = KeeperVolumeSize }
            }
        };

        var storageClass = StorageClass(desired);
        if (storageClass.Length > 0) {
            claim["storageClassName"] = storageClass;
        }

        return new JsonObject {
            // ⚠ THE RENDER NAMES ITS OWN KIND, AND THIS IS THE FIRST TYPE IN THE TREE THAT HAS TO.
            // KubeCommandBuilder injects `apiVersion` and `kind` from the GroupVersionKind the
            // reconciler applies with, so this line is redundant ON THE APPLY PATH — the five
            // providers before this one write nothing here and their `Matches` accepts a null kind.
            // That is only safe for a type that owns ONE kind. This one owns two, is compared by ONE
            // function, and a document with no kind would have to be guessed at from its shape.
            //
            // ⚠ It is not two spellings of one fact: the value comes from the SAME
            // GroupVersionKind constant the builder is handed, so the two writes cannot disagree.
            // ClickHouseReconcilerTests asserts that the applied body's kind is the target's.
            ["kind"] = KeeperKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["configuration"] = new JsonObject {
                    ["clusters"] = new JsonArray {
                        new JsonObject {
                            ["name"] = ClusterName,
                            // ⚠ A Keeper cluster has replicas and no shards. Raft replicates one log
                            // to every member; there is nothing to split.
                            ["layout"] = new JsonObject { ["replicasCount"] = KeeperNodes(desired) }
                        }
                    }
                },
                ["defaults"] = new JsonObject {
                    ["templates"] = new JsonObject {
                        ["podTemplate"] = PodTemplateName, ["dataVolumeClaimTemplate"] = VolumeTemplateName
                    }
                },
                ["templates"] = new JsonObject {
                    ["podTemplates"] = new JsonArray {
                        new JsonObject {
                            ["name"] = PodTemplateName,
                            ["spec"] = new JsonObject { ["containers"] = new JsonArray { container } }
                        }
                    },
                    ["volumeClaimTemplates"] = new JsonArray {
                        new JsonObject { ["name"] = VolumeTemplateName, ["spec"] = claim }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>ClickHouseInstallation</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.configuration.zookeeper.nodes</c> IS THE WHOLE REASON THIS TYPE RENDERS TWO
    ///         OBJECTS</b>, and the host it names is a <c>Service</c> that <i>neither</i> object
    ///         creates — the operator does, off the Keeper installation, with the <c>keeper-</c> prefix
    ///         <see cref="KeeperServiceName" /> records. Nothing in an apply, a read-back or an
    ///         admission check would notice that string being wrong. What notices is a tenant creating
    ///         their first <c>ReplicatedMergeTree</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.configuration.users</c>, and the consequence is the opposite of the one
    ///         <c>CyberCloud.Storage/accounts</c> records.</b> Piece 5 does not exist, so this provider
    ///         writes no credential — and a CHI with no <c>users</c> section is not open. The operator's
    ///         own hardening guide says it deploys <c>default</c> with an empty password behind a
    ///         <c>host_regexp</c> and an explicit pod-IP allow-list covering <i>this cluster's pods and
    ///         nothing else</i>, and <c>clickhouse_operator</c> behind the operator pod's IP. So the
    ///         cluster comes up <b>authenticated and unreachable</b> rather than <b>unauthenticated and
    ///         administrable</b>, which is the strictly better half of the same missing piece.
    ///         <c>conformance.yaml § owed</c>, <c>listkeys-has-no-handler</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>schemaPolicy</c>, no <c>files</c> and no <c>settings</c> beyond the six
    ///         Prometheus keys.</b> docs/plan/12's scope boundary — <i>"the resource does not manage
    ///         tables"</i> — is a boundary about DDL, and <c>schemaPolicy</c> is the field where the
    ///         operator asks the platform how much of a tenant's schema to copy when a replica is
    ///         added. Leaving it unset leaves that answer with the operator, where it is the operator's
    ///         business rather than the platform's opinion about somebody else's tables.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked. ⚠ That injection reaches the <i>CHI</i> and
    ///         not the pods the operator creates from it, which is the <c>pod-labels</c> gap
    ///         <c>charts/managed/nats/conformance.yaml</c> records, met here for the second time and
    ///         from the harder side: these pods are templated by somebody else's controller.
    ///     </para>
    /// </remarks>
    public static string ClickHouseJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var monitoring = MonitoringEnabled(desired);
        var (cpu, memory) = Resources(desired);

        var container = new JsonObject {
            ["name"] = "clickhouse", ["image"] = ServerImageRepository + ":" + Version(desired)
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            // ⚠ Two objects rather than one node used twice. A JsonNode has ONE parent, so assigning
            // the same instance to `requests` and `limits` reparents it and the first key comes back
            // holding null — a rendered container with a limit and no request, which schedules
            // differently and reads as a typo nobody made.
            container["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory },
                ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory }
            };
        }

        if (monitoring) {
            // ⚠ A NAMED container port, because a PodMonitor selects an endpoint BY PORT NAME. The
            // scrape object is owed rather than rendered — see MetricsPort — and the port it will
            // need is cheap to declare now and awkward to add later, because adding a port to a
            // container rolls every server pod.
            container["ports"] = new JsonArray {
                new JsonObject { ["name"] = MetricsPortName, ["containerPort"] = MetricsPort }
            };
        }

        var claim = new JsonObject {
            ["accessModes"] = new JsonArray { "ReadWriteOnce" },
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["storage"] = StorageSize(desired) }
            }
        };

        var storageClass = StorageClass(desired);
        if (storageClass.Length > 0) {
            claim["storageClassName"] = storageClass;
        }

        var configuration = new JsonObject {
            ["zookeeper"] = new JsonObject {
                ["nodes"] = new JsonArray {
                    new JsonObject {
                        ["host"] = KeeperServiceName(name), ["port"] = KeeperClientPort
                    }
                }
            },
            ["clusters"] = new JsonArray {
                new JsonObject {
                    ["name"] = ClusterName,
                    ["layout"] = new JsonObject {
                        ["shardsCount"] = Shards(desired), ["replicasCount"] = Replicas(desired)
                    }
                }
            }
        };

        if (monitoring) {
            // ⚠ FLAT, SLASH-SEPARATED KEYS, WHICH IS THE OPERATOR'S OWN SPELLING OF A NESTED XML
            // PATH rather than a JSON pointer that happens to look like one.
            // spec.configuration.settings is `x-kubernetes-preserve-unknown-fields: true` in the CRD,
            // so nothing would refuse a nested object here — it would simply produce a
            // config.d fragment ClickHouse does not read, and the metrics would be absent with the
            // resource converged.
            configuration["settings"] = new JsonObject {
                ["prometheus/endpoint"] = "/metrics",
                ["prometheus/port"] = MetricsPort,
                ["prometheus/metrics"] = true,
                ["prometheus/events"] = true,
                ["prometheus/asynchronous_metrics"] = true,
                ["prometheus/status_info"] = true
            };
        }

        return new JsonObject {
            // ⚠ See KeeperJson: the render names its own kind because ONE Matches serves TWO of them.
            ["kind"] = ClickHouseKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["defaults"] = new JsonObject {
                    ["templates"] = new JsonObject {
                        ["podTemplate"] = PodTemplateName, ["dataVolumeClaimTemplate"] = VolumeTemplateName
                    }
                },
                ["configuration"] = configuration,
                ["templates"] = new JsonObject {
                    ["podTemplates"] = new JsonArray {
                        new JsonObject {
                            ["name"] = PodTemplateName,
                            ["spec"] = new JsonObject { ["containers"] = new JsonArray { container } }
                        }
                    },
                    ["volumeClaimTemplates"] = new JsonArray {
                        new JsonObject { ["name"] = VolumeTemplateName, ["spec"] = claim }
                    }
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
    ///         ⚠ <b>Containment, not equality — and the reason is NOT the one three of the five
    ///         providers before this give, which is why it was checked in the CRD rather than assumed
    ///         from the README.</b> <c>NatsClusters.Matches</c> is containment because built-in kinds
    ///         are the most heavily defaulted objects in Kubernetes; <c>StorageAccounts.Matches</c> is
    ///         containment because the seaweedfs CRD carries <c>+kubebuilder:default</c> markers.
    ///         <b>Neither Altinity CRD declares a single <c>default:</c> anywhere</b> — checked over
    ///         <c>clickhouseinstallations.clickhouse.altinity.com.crd.yaml</c> and
    ///         <c>clickhousekeeperinstallations.clickhouse-keeper.altinity.com.crd.yaml</c>, which is
    ///         the third sighting of <c>KafkaClusters.Matches</c>' finding — and there is no admission
    ///         webhook either. Structural defaulting is simply not the hazard here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The hazard that IS here is that the operator merges other people's documents into
    ///         this spec, by design and on request of somebody who is not this platform.</b>
    ///         <c>spec.templating.policy: auto</c> makes the operator apply every
    ///         <c>ClickHouseInstallationTemplate</c> in the namespace whose <c>chiSelector</c> matches,
    ///         and the CHI's own <c>status.usedTemplates</c> exists to record that it happened. A
    ///         cluster operator who installs one — which is the supported way to set a
    ///         cluster-wide <c>podTemplate</c>, an image pull secret or a node affinity — would make
    ///         every equality comparison in this provider false forever, with the symptom being every
    ///         ClickHouse cluster stuck in <c>InProgress</c> while its workload is perfectly correct.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a third reason that is specific to this CRD: half of what this provider writes
    ///         lands under <c>x-kubernetes-preserve-unknown-fields: true</c></b> —
    ///         <c>configuration.settings</c>, and the <c>spec</c> of every entry in
    ///         <c>templates.podTemplates</c> and <c>templates.volumeClaimTemplates</c>. An equality
    ///         comparison over a subtree the API server does not prune is a comparison against JSON
    ///         this platform did not fully author.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Dispatches on the object's <c>kind</c> because this type owns TWO, and a conformance
    ///         case supplies this as one function over every object the resource owns.</b> An
    ///         unrecognised document is <c>false</c> rather than assumed — a <c>Matches</c> that
    ///         defaulted to <c>true</c> for a kind it did not know would report a Keeper that was never
    ///         applied as converged.
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

        // ⚠ A DOCUMENT WITH NO `kind` IS FALSE, AND THE FIVE PROVIDERS BEFORE THIS ONE ACCEPT ONE.
        // Their `Matches` reads `null or "Seaweed"`, which is right for a type that owns one kind and
        // is a guess for a type that owns two. Every path that reaches this function carries a kind:
        // the API server always returns one, and both renders above write their own.
        return document["kind"]?.GetValue<string>() switch {
            "ClickHouseInstallation" => MatchesClickHouse(spec, desired),
            "ClickHouseKeeperInstallation" => MatchesKeeper(spec, desired),
            _ => false
        };
    }

    /// <summary>Whether a <c>ClickHouseInstallation</c>'s spec carries what the body asks for.</summary>
    /// <remarks>
    ///     ⚠ <b>The ZooKeeper host is compared, and it is the field with the weakest natural defences.</b>
    ///     Every other value here is one the operator turns into a workload that visibly fails when it
    ///     is wrong. A coordination pointer at a Service that does not exist produces a cluster that
    ///     starts, answers, and cannot replicate — so this comparison is the only thing between a typo
    ///     in <see cref="KeeperServiceName" /> and a tenant discovering it in SQL.
    /// </remarks>
    static bool MatchesClickHouse(JsonObject spec, JsonElement desired) {
        if (spec["configuration"] is not JsonObject configuration
            || Layout(configuration) is not { } layout
            || layout["shardsCount"]?.GetValue<int>() != Shards(desired)
            || layout["replicasCount"]?.GetValue<int>() != Replicas(desired)) {
            return false;
        }

        if (First((configuration["zookeeper"] as JsonObject)?["nodes"]) is not { } node
            || node["host"]?.GetValue<string>() != KeeperServiceName(NameOf(spec))
            || node["port"]?.GetValue<int>() != KeeperClientPort) {
            return false;
        }

        return ClaimStorage(spec) == StorageSize(desired)
            && ContainerImage(spec) == ServerImageRepository + ":" + Version(desired);
    }

    /// <summary>Whether a <c>ClickHouseKeeperInstallation</c>'s spec carries what the body asks for.</summary>
    static bool MatchesKeeper(JsonObject spec, JsonElement desired) =>
        spec["configuration"] is JsonObject configuration
        && Layout(configuration) is { } layout
        && layout["replicasCount"]?.GetValue<int>() == KeeperNodes(desired)
        && ClaimStorage(spec) == KeeperVolumeSize
        && ContainerImage(spec) == KeeperImageRepository + ":" + Version(desired);

    /// <summary>
    ///     The name a read-back <c>ClickHouseInstallation</c> carries, for the ZooKeeper comparison.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read off the object rather than passed in, and that is a limitation stated rather than
    ///     hidden.</b> <c>ProviderConformanceCase.ObjectMatchesDesired</c> is
    ///     <c>(objectJson, desiredJson) =&gt; bool</c> and carries no address — the finding
    ///     <c>StorageBuckets</c> records — so the only name available here is the one in the document.
    ///     That makes the comparison "the zookeeper host is derived from THIS object's own name", which
    ///     is the invariant that matters and is not quite "…from the RESOURCE's name". The stronger
    ///     assertion, against a real address, is in <c>ClickHouseReconcilerTests</c>.
    /// </remarks>
    static string NameOf(JsonObject spec) =>
        (spec.Parent as JsonObject)?["metadata"] is JsonObject metadata
        && metadata["name"]?.GetValue<string>() is { Length: > 0 } name
            ? name
            : string.Empty;

    /// <summary>The single cluster entry's layout, or <see langword="null" />.</summary>
    static JsonObject? Layout(JsonObject configuration) =>
        First(configuration["clusters"])?["layout"] as JsonObject;

    /// <summary>The first volume-claim template's requested size, or <c>""</c>.</summary>
    static string ClaimStorage(JsonObject spec) =>
        First((spec["templates"] as JsonObject)?["volumeClaimTemplates"]) is { } claim
        && (claim["spec"] as JsonObject)?["resources"] is JsonObject resources
        && (resources["requests"] as JsonObject)?["storage"]?.GetValue<string>() is { } size
            ? size
            : string.Empty;

    /// <summary>The first pod template's first container image, or <c>""</c>.</summary>
    static string ContainerImage(JsonObject spec) =>
        First((spec["templates"] as JsonObject)?["podTemplates"]) is { } template
        && First((template["spec"] as JsonObject)?["containers"]) is { } container
        && container["image"]?.GetValue<string>() is { } image
            ? image
            : string.Empty;

    /// <summary>
    ///     The first element of a node that is an array of objects, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every one of these lists is declared with exactly one entry by
    ///     <see cref="ClickHouseJson" /> and <see cref="KeeperJson" />, and reading only the first is
    ///     therefore reading all of it. It is written as "the first" rather than "the only" because a
    ///     read-back is somebody else's document: a merged
    ///     <c>ClickHouseInstallationTemplate</c> can append a second pod template, and the one this
    ///     provider wrote is still the one it named in <c>spec.defaults.templates</c> — which is at
    ///     index 0 because the merge appends.
    /// </remarks>
    static JsonObject? First(JsonNode? node) =>
        node is JsonArray { Count: > 0 } array ? array[0] as JsonObject : null;

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the ClickHouse cluster in.</param>
    /// <param name="shards">How many shards.</param>
    /// <param name="replicas">How many replicas per shard.</param>
    /// <param name="storageSize">The data volume size per server.</param>
    /// <param name="keeperNodes">How many Keeper nodes.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. The projection a read runs skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int shards = 1,
        int replicas = 2,
        string storageSize = "100Gi",
        int keeperNodes = 3,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["shards"] = shards,
                ["replicas"] = replicas,
                ["keeperNodes"] = keeperNodes,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader above has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "25.3";
    const string DefaultPreset = "m1.small";
    const string DefaultStorageSize = "100Gi";
    const int DefaultShards = 1;
    const int DefaultReplicas = 2;
    const int DefaultKeeperNodes = 3;

    // ── Names inside the rendered objects ─────────────────────────────────────────────────────
    //
    // ⚠ These are template names INSIDE somebody else's CR, referenced from spec.defaults.templates
    // by string. They are not an API and they are not a Kubernetes object name — a typo here produces
    // a CHI whose `defaults.templates.podTemplate` names a template that is not in its own
    // `templates.podTemplates`, which the operator answers by running the container it would have run
    // anyway with no resources and no image. Written once for that reason.

    const string PodTemplateName = "cybercloud";
    const string VolumeTemplateName = "data";

    /// <summary>The container-port name a future <c>PodMonitor</c> selects. See <see cref="MetricsPort" />.</summary>
    const string MetricsPortName = "metrics";

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    static JsonObject KeeperQuantities() =>
        new() { ["cpu"] = KeeperCpu, ["memory"] = KeeperMemory };

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

    static int Number(JsonElement desired, string name, int fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
