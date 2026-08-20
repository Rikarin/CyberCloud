using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Messaging/kafkaClusters</c>: the type, its
///     api-version, its body shape, and the Strimzi objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"Kafka — <c>CyberCloud.Messaging/kafkaClusters</c>"</i>, on
///         <b>Strimzi in KRaft mode</b> (ADR-010 clause 1 names the operator; ADR-011 clears the
///         licence — Strimzi is Apache-2.0).
///     </para>
///     <para>
///         ⚠ <b>The catalogue rows this type does <i>not</i> declare, and why.</b> That document says
///         <i>"Topics and users as sub-resources — Strimzi's <c>KafkaTopic</c> and <c>KafkaUser</c>
///         CRDs map to resource types almost one to one"</i>. They are not declared here, and the
///         reason is not effort: this platform's resource-id grammar does not carry a parent
///         <i>instance</i>. <c>ResourceId</c> spells a nested type
///         <c>…/kafkaClusters/topics/{name}</c> — one name, at the end, with the type path whole in
///         the middle — and <c>OpenApiEmitter</c>'s own remarks refuse Azure's interleaved
///         <c>…/kafkaClusters/{cluster}/topics/{topic}</c> outright, because emitting it would
///         produce URLs <c>ResourceId.TryParsePath</c> rejects. A <c>topics</c> type on this grammar
///         would be a <i>sibling</i> of its cluster whose parent is a body property, with topic names
///         unique per resource group rather than per cluster, no parent-existence check at create and
///         no permission inheritance from the cluster. That is a different product from the one
///         docs/plan/12 describes, so the gap is reported rather than half-built — see this
///         provider's README section in <c>src/Providers/README.md</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/kafka/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer; <c>/location</c> is root-level and <c>clusterId</c> is placement, and
///         <c>ChartAnnotationEmitter</c> skips both.
///     </para>
///     <para>
///         ⚠ <b>Two facts this type needs and the registry cannot express, recorded here rather than
///         worked around.</b> First, <see cref="NodesPointer" /> wants an <i>odd</i> count — a KRaft
///         quorum of two controllers tolerates no failures at all, so an even value is a cluster that
///         looks highly available and is not. <c>SchemaProperty.AllowedValues</c> is
///         <see cref="SchemaKind.Text" />-only by construction ("comparing 1.0 and 1 for membership
///         has no answer that survives a JSON round trip"), and <c>Minimum</c>/<c>Maximum</c> cannot
///         say "odd", so the constraint is in the description and in nothing that enforces it.
///         Second, the quota meters — see <c>MessagingProvider</c>.
///     </para>
/// </remarks>
public static class KafkaClusters {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.Messaging";

    /// <summary>The one resource type.</summary>
    /// <remarks>
    ///     docs/plan/12 also names <c>natsClusters</c> and <c>rabbitmqClusters</c> in this namespace.
    ///     They are separate types with separate operators and separate reconcilers, and each is its
    ///     own change.
    /// </remarks>
    public const string TypePath = "kafkaClusters";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/kafka/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/kafka";

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.messaging";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The pointer whose value is the node count. Named because its constraint is prose.</summary>
    public const string NodesPointer = "/properties/nodes";

    /// <summary>The action that hands a caller the bootstrap servers and credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials. ⚠ <c>regenerateKeys</c> is named in
    ///     the same paragraph and is <b>not</b> declared, for the reason
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c> gives: it is specified with a rolling grace
    ///     period and nothing in the platform can hold two live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of a broker a holder of its credentials.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>Strimzi's <c>Kafka</c> — the object a cluster <i>is</i>.</summary>
    /// <remarks>
    ///     ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived — <c>kafkas</c> is
    ///     exactly the case an English pluraliser gets wrong, and it must match
    ///     <c>charts/managed/kafka/SOURCE</c>'s <c>upstream-api: kafka.strimzi.io/v1beta2</c>.
    /// </remarks>
    public static GroupVersionKind KafkaKind { get; } =
        new() { Group = "kafka.strimzi.io", Version = "v1beta2", Kind = "Kafka", Plural = "kafkas" };

    /// <summary>Strimzi's <c>KafkaNodePool</c> — the nodes the cluster runs on.</summary>
    /// <remarks>
    ///     ⚠ <b>Not optional, unlike the PostgreSQL provider's <c>Pooler</c>.</b> In KRaft mode with
    ///     <see cref="NodePoolsAnnotation" /> enabled, <c>spec.kafka.replicas</c> and
    ///     <c>spec.kafka.storage</c> are ignored and the pool owns both. A <c>Kafka</c> applied
    ///     without a pool is a cluster with nowhere to run, so the two objects are one unit and the
    ///     conformance case lists both unconditionally.
    /// </remarks>
    public static GroupVersionKind NodePoolKind { get; } =
        new() {
            Group = "kafka.strimzi.io", Version = "v1beta2", Kind = "KafkaNodePool", Plural = "kafkanodepools"
        };

    /// <summary>The annotation that puts a <c>Kafka</c> into KRaft mode — no ZooKeeper.</summary>
    /// <remarks>
    ///     docs/plan/12: <i>"Strimzi, KRaft mode (no ZooKeeper)"</i>. ⚠ An annotation rather than a
    ///     spec field, which is Strimzi's own choice and not ours: the CRD still carries a
    ///     <c>spec.zookeeper</c> block, and the annotation is what makes it dead rather than default.
    /// </remarks>
    public const string KraftAnnotation = "strimzi.io/kraft";

    /// <summary>The annotation that makes <see cref="NodePoolKind" /> the source of node topology.</summary>
    public const string NodePoolsAnnotation = "strimzi.io/node-pools";

    /// <summary>The value both annotations take.</summary>
    public const string Enabled = "enabled";

    /// <summary>The label that binds a <c>KafkaNodePool</c> to its <c>Kafka</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Load-bearing, and it is a label rather than a spec field.</b> A pool whose
    ///     <c>strimzi.io/cluster</c> does not name an existing <c>Kafka</c> is silently ignored by the
    ///     operator — no event, no status, no pods. It is applied through
    ///     <c>IKubeCommandBuilder.WithLabels</c> rather than written into the rendered document, so
    ///     that it goes through the same key and value syntax check ADR-013's seven do.
    /// </remarks>
    public const string ClusterLabel = "strimzi.io/cluster";

    // ── The constraint vocabularies, now shared by code rather than by shape ──────────────────────
    //
    // ⚠ The note that stood here said a platform-wide quantity pattern belonged in
    // CyberCloud.ResourceManager.Contracts "when a third provider needs it; two copies is not yet
    // evidence of a missing seam, three is". Three arrived, and so did the evidence: an author who
    // needed to turn one of these strings into a number found a provider's copy of the grammar and
    // wrote a second quantity parser beside it, in `double`, for something the platform already
    // parsed exactly in `decimal`.
    //
    // The seam is KubeQuantity, and it is beside the parser rather than merely somewhere shared —
    // that adjacency is the part that stops the next author repeating it. Rule 2 of
    // docs/plan/03 § Assembly graph rules is untouched: KubeQuantity is in
    // CyberCloud.ResourceManager.Contracts, which this project already references and every provider
    // may, so no Providers.* edge is created.

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>An IPv4 CIDR block. ⚠ Written down, tested, and enforced by nothing — see below.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This should be <c>Pattern</c> on <see cref="Schema2026" />'s <c>allowedCidrs</c>
    ///         and is not.</b> The registry would take it and enforce it per element;
    ///         <c>./build.sh Charts</c> refuses <c>@pattern</c> on a <c>{array}</c>, and that gate may
    ///         not be edited from a provider change. The full argument, and what the omission costs a
    ///         caller, is at the property itself. <c>KafkaDeclarationTests</c> asserts this string is
    ///         the right one, so that when the vocabulary grows the fix is one line rather than one
    ///         line and a rewrite.
    ///     </para>
    ///     <para>
    ///         The prefix length is bounded by the alternation rather than by a range, because a range
    ///         cannot be spelled inside a regex and a <c>/33</c> allow-list entry matches nothing while
    ///         looking like a rule.
    ///     </para>
    /// </remarks>
    public const string CidrPattern =
        @"((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)/(3[0-2]|[12]?\d)";

    /// <summary>The name Strimzi gives the in-cluster listener. ⚠ At most 11 lower-case characters.</summary>
    /// <remarks>
    ///     The CRD pins <c>^[a-z0-9]{1,11}$</c> on <c>spec.kafka.listeners[].name</c>, and the name
    ///     is used to build Kubernetes object names, so a twelfth character is a rejected manifest
    ///     after the caller was told <c>202</c>.
    /// </remarks>
    public const string InternalListener = "internal";

    /// <summary>The name Strimzi gives the externally exposed listener.</summary>
    public const string ExternalListener = "external";

    /// <summary>The port the in-cluster listener serves.</summary>
    public const int InternalPort = 9092;

    /// <summary>The port the external listener serves.</summary>
    public const int ExternalPort = 9094;

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
                    Description: "The cluster whose namespace holds the Strimzi objects."
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
                    Description: "Apache Kafka version. Minor upgrades are applied automatically in "
                    + "the maintenance window; a major upgrade is an explicit update to this field."
                ) {
                    AllowedValues = ["3.8", "3.9"],
                    DefaultJson = "\"3.9\""
                },
                new(
                    "/properties/nodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of Kafka nodes, each acting as both a KRaft controller and a "
                    + "broker. Use an odd number: a quorum of two tolerates no failures, and one is a "
                    + "single point of failure offered for development only."
                ) {
                    Minimum = 1,
                    Maximum = 9,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per node, either by preset or explicitly."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Brokers use the c1 family, which "
                    + "is 1 vCPU to 2 GiB and guaranteed rather than burstable."
                ) {
                    AllowedValues = [
                        "c1.nano",
                        "c1.micro",
                        "c1.small",
                        "c1.medium",
                        "c1.large",
                        "c1.xlarge",
                        "c1.2xlarge",
                        "c1.4xlarge"
                    ],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"c1.small\""
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
                new("/properties/storage", SchemaKind.Nested, Description: "The log volume, per node."),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Log volume size per node, in Kubernetes quantity form. Grows online; "
                    + "never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name. Empty means the cluster default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage/deleteClaim",
                    SchemaKind.Boolean,
                    Description: "Whether deleting the resource also deletes the log volumes. Off by "
                    + "default, so a mistaken delete leaves the data recoverable by hand."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/topics",
                    SchemaKind.Nested,
                    Description: "Defaults applied to topics the cluster creates automatically. A "
                    + "topic created explicitly overrides them."
                ),
                new(
                    "/properties/topics/partitions",
                    SchemaKind.WholeNumber,
                    Description: "Default partition count for a new topic."
                ) {
                    Minimum = 1,
                    Maximum = 200,
                    DefaultJson = "3"
                },
                new(
                    "/properties/topics/replicationFactor",
                    SchemaKind.WholeNumber,
                    Description: "Default replication factor for a new topic. Must not exceed the node "
                    + "count, or every produce to a new topic fails."
                ) {
                    Minimum = 1,
                    Maximum = 9,
                    DefaultJson = "3"
                },
                new(
                    "/properties/topics/minInSyncReplicas",
                    SchemaKind.WholeNumber,
                    Description: "How many replicas must acknowledge a write before it is committed. "
                    + "Must be below the replication factor, or the cluster cannot tolerate one "
                    + "broker restart."
                ) {
                    Minimum = 1,
                    Maximum = 9,
                    DefaultJson = "2"
                },
                new(
                    "/properties/retention",
                    SchemaKind.Nested,
                    Description: "How long the cluster keeps messages by default. docs/plan/12 calls "
                    + "retention sizing an ongoing operational concern rather than a one-time setting."
                ),
                new(
                    "/properties/retention/hours",
                    SchemaKind.WholeNumber,
                    Description: "Default retention in hours. A week by default."
                ) {
                    Minimum = 1,
                    Maximum = 8760,
                    DefaultJson = "168"
                },
                new(
                    "/properties/retention/size",
                    SchemaKind.Text,
                    Description: "Default retention by partition size, in Kubernetes quantity form. "
                    + "Empty means retention is by time alone."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"50Gi\""
                },
                new(
                    "/properties/listener",
                    SchemaKind.Nested,
                    Description: "The in-cluster listener every client reaches."
                ),
                new(
                    "/properties/listener/tls",
                    SchemaKind.Boolean,
                    Description: "Whether the in-cluster listener requires TLS. On by default; turning "
                    + "it off is a plaintext broker on the pod network."
                ) {
                    DefaultJson = "true"
                },
                new(
                    "/properties/external",
                    SchemaKind.Nested,
                    Description: "Exposure outside the cluster, via a load balancer and a firewall "
                    + "allow-list."
                ),
                new(
                    "/properties/external/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the cluster is reachable from outside the Kubernetes "
                    + "cluster. Off by default — docs/plan/12 § Cross-cutting decisions makes external "
                    + "exposure never the default, because a managed broker on a public IP with a weak "
                    + "password is the most common cloud breach there is."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/external/allowedCidrs",
                    SchemaKind.Array,
                    Description: "Source ranges permitted to reach the external listener. Required in "
                    + "substance rather than in schema: an empty list with external exposure on "
                    + "renders a load balancer that accepts nothing, which is the safe reading of an "
                    + "unfinished configuration."
                ) {
                    // ⚠ `Pattern = CidrPattern` BELONGS HERE AND IS NOT DECLARED. IT IS THE THIRD
                    // REGISTRY-VERSUS-SURFACE GAP THIS PROVIDER FOUND AND THE ONLY ONE THAT COSTS A
                    // REAL CONSTRAINT, SO IT IS RECORDED RATHER THAN WORKED AROUND.
                    //
                    // `SchemaProperty.ElementKind`'s own remarks say Pattern "applies to EACH ELEMENT
                    // when the property is an array. That is the only reading that makes it useful
                    // there and it is what ResourceSchema.Validate implements." So the registry
                    // accepts it, enforces it per element, and the OpenAPI emitter carries it.
                    //
                    // ADR-012's fifth surface refuses it. `./build.sh Charts` fails with "`@pattern`
                    // on a `{array}`. It refines a string, and JSON Schema ignores it on any other
                    // type — so it would read as a constraint and validate nothing." That reasoning
                    // is contradicted by the same emitter's own behaviour one directive over:
                    // `@enum` on an array is emitted as `items: {type: string, enum: […]}`, which is
                    // exactly the per-element shape `@pattern` would need — `items.pattern`.
                    // charts/README.md § What a chart cannot say says so: "A text element kind is the
                    // one array shape the vocabulary reaches, because `@enum` on an array becomes
                    // `items: {type: string, enum: […]}`". The vocabulary reaches the shape and
                    // refuses the directive.
                    //
                    // Closing it is one case in ChartAnnotationEmitter and one in Build.Charts'
                    // Validate, in files this change may not touch. Declaring the Pattern anyway
                    // makes ./build.sh Charts red for every future run, which is worse than an
                    // under-constrained array with a loud note; declaring a weaker constraint that
                    // the chart WOULD carry would be a constraint nobody chose. So the pattern is
                    // written down as `CidrPattern`, asserted correct by KafkaDeclarationTests, and
                    // enforced by nothing — and conformance.yaml § owed carries it as
                    // `cidr-shape-is-unenforced`.
                    //
                    // WHAT IT COSTS: a body may send "999.0.0.1/99" and be accepted. It reaches
                    // `loadBalancerSourceRanges`, where the API server refuses the Service, and the
                    // resource fails to converge AFTER the caller was told 202 — the exact failure
                    // class every other pattern on this type exists to prevent.
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = "[\"203.0.113.0/24\"]"
                },
                new(
                    "/properties/cruiseControl",
                    SchemaKind.Nested,
                    Description: "Cruise Control — partition rebalancing and broker-load reporting."
                ),
                new(
                    "/properties/cruiseControl/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether Cruise Control runs. On by default: docs/plan/12 puts it in "
                    + "the chart rather than in a follow-up, because rebalancing a Kafka cluster by "
                    + "hand is the operational cost that makes this the most demanding service in the "
                    + "catalogue."
                ) {
                    DefaultJson = "true"
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the operator runs a Kafka Exporter alongside the cluster, "
                    + "exposing consumer-lag and topic metrics to the platform's metrics stack."
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
                    "/bootstrapServers",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster bootstrap address, host:port. ⚠ The external "
                    + "listener's address is not returned here — an address a client cannot reach "
                    + "from where it is running is worse than an absent one."
                ),
                new(
                    "/securityProtocol",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What a client must configure: SASL_SSL when the listener requires "
                    + "TLS, SASL_PLAINTEXT when it does not."
                ) {
                    AllowedValues = ["SASL_SSL", "SASL_PLAINTEXT"]
                },
                new("/username", SchemaKind.Text, Required: true, Description: "The SCRAM user."),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The SCRAM user's password, read from the tenant's Vault for this "
                    + "call only."
                )
            ]
        );

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, c1 family.</summary>
    /// <remarks>
    ///     ⚠ <b>A second copy of <c>charts/managed/kafka/templates/_helpers.tpl</c>'s
    ///     <c>kafka.resources</c>, and the duplication is the cost of having no chart renderer</b> —
    ///     <c>CyberCloud.Kubernetes.Charts</c> does not exist (docs/plan/03 § src), so the objects are
    ///     built here. <c>KafkaDeclarationTests.TheSizingTableAgreesWithTheChartsValueForValue</c>
    ///     asserts the two agree value for value.
    ///     <para>
    ///         The ratio is exactly 1:2 at every rung, which is what <c>c1</c> means. A table that
    ///         drifted off the ratio would make the family name a lie on one row and nowhere else.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["c1.nano"] = ("250m", "512Mi"),
            ["c1.micro"] = ("500m", "1Gi"),
            ["c1.small"] = ("1", "2Gi"),
            ["c1.medium"] = ("2", "4Gi"),
            ["c1.large"] = ("4", "8Gi"),
            ["c1.xlarge"] = ("8", "16Gi"),
            ["c1.2xlarge"] = ("16", "32Gi"),
            ["c1.4xlarge"] = ("32", "64Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── Addressing ────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Kafka</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef KafkaRef(string ns, string name) =>
        new() { Kind = KafkaKind, Namespace = ns, Name = name };

    /// <summary>The <c>KafkaNodePool</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ The suffix matches <c>templates/nodepool.yaml</c>'s
    ///     <c>{{ include "kafka.name" . }}-nodes</c>. Two spellings of one object's name is an object
    ///     the reconciler creates and never finds again.
    /// </remarks>
    public static ObjectRef NodePoolRef(string ns, string name) =>
        new() { Kind = NodePoolKind, Namespace = ns, Name = NodePoolName(name) };

    /// <summary>The node pool object's name.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string NodePoolName(string name) => name + "-nodes";

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>Whether the desired body asks for external exposure.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The declared value, or the schema default (<c>false</c>) when absent. ⚠ The default is
    ///     applied <i>here</i> rather than by the write path, which stores the body as sent —
    ///     <see cref="SchemaProperty.DefaultJson" />'s own remarks say the validator does not
    ///     substitute.
    /// </returns>
    public static bool ExternalEnabled(JsonElement desired) => Flag(desired, "external", "enabled", false);

    /// <summary>Whether the desired body asks for Cruise Control.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool CruiseControlEnabled(JsonElement desired) =>
        Flag(desired, "cruiseControl", "enabled", true);

    /// <summary>Whether the desired body asks for a metrics exporter.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) => Flag(desired, "monitoring", "enabled", true);

    /// <summary>Whether the in-cluster listener requires TLS.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool ListenerTls(JsonElement desired) => Flag(desired, "listener", "tls", true);

    /// <summary>The Kafka version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "3.9"
            : "3.9";

    /// <summary>The node count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Nodes(JsonElement desired) => Number(desired, "nodes", 3);

    /// <summary>
    ///     The <c>Kafka</c> document a desired body becomes, ready for server-side apply.
    /// </summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The JSON <c>templates/kafka.yaml</c> renders, for the same values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably, and the two Strimzi
    ///         annotations this type needs go through <c>WithAnnotations</c> for the same reason —
    ///         the builder is the one place a key and a value are syntax-checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.kafka.replicas</c> and no <c>spec.kafka.storage</c>, on purpose.</b>
    ///         With <see cref="NodePoolsAnnotation" /> enabled the operator reads both off the
    ///         <c>KafkaNodePool</c> and ignores whatever is here. Writing them anyway would make this
    ///         field manager the owner of two fields that do nothing, which under server-side apply is
    ///         two fields the tenant's own controller could then never set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No credentials in this document, and none in grain state.</b> Strimzi's entity
    ///         operator provisions SCRAM users through its own <c>KafkaUser</c> flow, reading and
    ///         writing <c>Secret</c>s in the namespace, so the only component that ever holds a
    ///         plaintext password is the operator. ⚠ <c>listKeys</c> has a declared shape and no
    ///         handler, and the reason is <b>not</b> a missing vault path — piece 5 is
    ///         <c>ISecretWriter</c> and it has shipped. It is that no <c>KafkaUser</c> is rendered and
    ///         the listeners declare no SASL mechanism, so no SCRAM user exists for a handler to read
    ///         — <c>actions-without-handlers.txt</c> carries the line. A visible gap rather than a
    ///         password in a resource body.
    ///     </para>
    /// </remarks>
    public static string KafkaJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var replication = Number(desired, "topics", "replicationFactor", 3);
        var minIsr = Number(desired, "topics", "minInSyncReplicas", 2);

        // ⚠ Every value is a STRING. Kafka's own broker configuration is a properties file, and
        // Strimzi passes `spec.kafka.config` through to it verbatim; the CRD marks the block
        // x-kubernetes-preserve-unknown-fields, so a JSON number survives the round trip as a number
        // and then reaches the broker as one. Quoting here is what makes the read-back comparison in
        // `Matches` a comparison of the same type on both sides.
        var config = new JsonObject {
            ["num.partitions"] = Text(Number(desired, "topics", "partitions", 3)),
            ["default.replication.factor"] = Text(replication),
            ["min.insync.replicas"] = Text(minIsr),
            ["offsets.topic.replication.factor"] = Text(replication),
            ["transaction.state.log.replication.factor"] = Text(replication),
            ["transaction.state.log.min.isr"] = Text(minIsr),
            ["log.retention.hours"] = Text(Number(desired, "retention", "hours", 168))
        };

        var retentionSize = Text(desired, "retention", "size", string.Empty);
        if (retentionSize.Length > 0) {
            config["log.retention.bytes"] = retentionSize;
        }

        var listeners = new JsonArray {
            new JsonObject {
                ["name"] = InternalListener,
                ["port"] = InternalPort,
                ["type"] = "internal",
                ["tls"] = ListenerTls(desired)
            }
        };

        if (ExternalEnabled(desired)) {
            var external = new JsonObject {
                ["name"] = ExternalListener,
                ["port"] = ExternalPort,
                ["type"] = "loadbalancer",
                // ⚠ Always TLS on the external listener, whatever `listener.tls` says. That property
                // describes the pod network, where a tenant may reasonably choose plaintext; this one
                // is a public address, and docs/plan/12's "most common cloud breach" sentence is
                // about exactly this listener.
                ["tls"] = true
            };

            var cidrs = AllowedCidrs(desired);
            // ⚠ Written even when EMPTY, and that is the safe reading rather than an omission: an
            // absent `loadBalancerSourceRanges` means "from anywhere", and an unfinished allow-list
            // must not be the configuration that opens the broker to the internet.
            var ranges = new JsonArray();
            foreach (var cidr in cidrs) {
                ranges.Add(cidr);
            }

            external["configuration"] = new JsonObject { ["loadBalancerSourceRanges"] = ranges };
            listeners.Add(external);
        }

        var spec = new JsonObject {
            ["kafka"] = new JsonObject {
                ["version"] = Version(desired),
                ["listeners"] = listeners,
                ["config"] = config
            },
            // The topic and user operators are what make a KafkaTopic or a KafkaUser mean anything in
            // this namespace. They are on unconditionally: a cluster without them is one where the
            // sub-resources docs/plan/12 describes could never be added.
            ["entityOperator"] = new JsonObject {
                ["topicOperator"] = new JsonObject(), ["userOperator"] = new JsonObject()
            }
        };

        if (CruiseControlEnabled(desired)) {
            spec["cruiseControl"] = new JsonObject();
        }

        if (MonitoringEnabled(desired)) {
            spec["kafkaExporter"] = new JsonObject();
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec }
            .ToJsonString();
    }

    /// <summary>The <c>KafkaNodePool</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name — the pool is named after it.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Both roles on one pool.</b> Strimzi's production shape is a controller pool and a
    ///     broker pool, and that is the honest thing to grow into; combined-role nodes are supported
    ///     and are what makes a three-node cluster affordable. Splitting them is a change to this
    ///     function and to <c>Targets</c>, and it is written down as owed in
    ///     <c>charts/managed/kafka/conformance.yaml</c> rather than implied by its absence.
    /// </remarks>
    public static string NodePoolJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var volume = new JsonObject {
            ["id"] = 0,
            ["type"] = "persistent-claim",
            ["size"] = Text(desired, "storage", "size", "100Gi"),
            ["deleteClaim"] = Flag(desired, "storage", "deleteClaim", false)
        };

        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            volume["class"] = storageClass;
        }

        var spec = new JsonObject {
            ["replicas"] = Nodes(desired),
            ["roles"] = new JsonArray { "controller", "broker" },
            // ⚠ `jbod` with one volume rather than a bare `persistent-claim`. A single-volume jbod is
            // what a second log directory can be added to later; a `persistent-claim` storage cannot
            // be widened to jbod without recreating every node, which on a broker means moving every
            // partition.
            ["storage"] = new JsonObject { ["type"] = "jbod", ["volumes"] = new JsonArray { volume } }
        };

        var (cpu, memory) = Resources(desired);
        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            // Requests equal limits — the `c1` family is "guaranteed" in docs/plan/12 § Sizing
            // vocabulary, and guaranteed is a Kubernetes QoS class you get by setting them equal.
            spec["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = NodePoolName(name) }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns>
    ///     <c>true</c> when the fields this provider owns hold the desired values.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, not equality, and the usual reason for that is <i>not</i> the reason
    ///         here — which is worth stating because the wrong reason would decay into the wrong
    ///         rule.</b> The obvious argument is "the operator edits the spec it is given, so equality
    ///         reports drift forever". Checked against the operator rather than assumed: Strimzi's
    ///         <c>Kafka</c> CRD at v1beta2 declares <b>no <c>default:</c> anywhere</b>, so the API
    ///         server's structural defaulting adds nothing on write, and the cluster operator writes
    ///         <c>.status</c> — a subresource — rather than <c>.spec</c>. So the premise is false for
    ///         this operator.
    ///     </para>
    ///     <para>
    ///         The reasons that <i>do</i> hold are the ones
    ///         <c>CyberCloud.DBforPostgreSQL/servers</c> gives and one this type adds. Server-side
    ///         apply leaves other managers' fields in place, so a read-back carries whatever the
    ///         tenant's own controllers wrote. The document carries <c>metadata</c>,
    ///         <c>managedFields</c> and a large operator-written <c>status</c> that this provider
    ///         never sent. And <c>KafkaNodePool</c> declares a <c>scale</c> subresource over
    ///         <c>.spec.replicas</c>, so <c>kubectl scale</c> and any autoscaler can write a field
    ///         inside <c>spec</c> itself — which containment reports as drift on the one field that
    ///         moved, exactly as it should, instead of as a whole object mismatch.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on the object's <c>kind</c> rather than taking it as a parameter, because
    ///         a conformance case supplies this as one function over every object the resource owns.
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
            "KafkaNodePool" => MatchesNodePool(spec, desired),
            _ => MatchesKafka(spec, desired)
        };
    }

    static bool MatchesKafka(JsonObject spec, JsonElement desired) {
        if (spec["kafka"] is not JsonObject kafka) {
            return false;
        }

        if (kafka["version"]?.GetValue<string>() != Version(desired)) {
            return false;
        }

        if ((kafka["config"] as JsonObject)?["log.retention.hours"]?.GetValue<string>()
            != Text(Number(desired, "retention", "hours", 168))) {
            return false;
        }

        // ⚠ The listener COUNT, not the whole array. External exposure is the setting whose change
        // the world can see, and a listener list that has not grown is a load balancer that was never
        // created — which is the difference between "the update reached the cluster" and "the grain
        // stored it".
        var listeners = kafka["listeners"] as JsonArray;
        return listeners?.Count == (ExternalEnabled(desired) ? 2 : 1)
            && (spec["cruiseControl"] is not null) == CruiseControlEnabled(desired);
    }

    static bool MatchesNodePool(JsonObject spec, JsonElement desired) {
        if (spec["replicas"]?.GetValue<int>() != Nodes(desired)) {
            return false;
        }

        var volumes = (spec["storage"] as JsonObject)?["volumes"] as JsonArray;

        return volumes is { Count: > 0 }
            && (volumes[0] as JsonObject)?["size"]?.GetValue<string>()
            == Text(desired, "storage", "size", "100Gi");
    }

    /// <summary>
    ///     The CPU and memory a body asks for: the explicit quantities when both are given, otherwise
    ///     the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them —
    ///     which renders no <c>resources</c> block at all rather than a half-specified one.
    /// </returns>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", "c1.small");
        var fallback = Presets.TryGetValue(preset, out var found) ? found : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    /// <summary>The source ranges a body permits, in the order it listed them.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> AllowedCidrs(JsonElement desired) {
        if (Member(desired, "external", "allowedCidrs") is not { ValueKind: JsonValueKind.Array } array) {
            return [];
        }

        var found = ImmutableArray.CreateBuilder<string>();
        foreach (var element in array.EnumerateArray()) {
            if (element.ValueKind is JsonValueKind.String && element.GetString() is { Length: > 0 } text) {
                found.Add(text);
            }
        }

        return found.ToImmutable();
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the broker cluster in.</param>
    /// <param name="nodes">How many combined controller-broker nodes.</param>
    /// <param name="storageSize">The log volume size per node.</param>
    /// <param name="cruiseControl">Whether Cruise Control runs.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int nodes = 3,
        string storageSize = "100Gi",
        bool cruiseControl = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = "3.9",
                ["nodes"] = nodes,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["topics"] = new JsonObject { ["partitions"] = 3, ["replicationFactor"] = 3 },
                ["retention"] = new JsonObject { ["hours"] = 168 },
                ["cruiseControl"] = new JsonObject { ["enabled"] = cruiseControl }
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

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
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var found)
            ? found
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
