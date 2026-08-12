using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Messaging/natsClusters</c>: the type, its
///     api-version, its body shape, and the five Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"NATS — <c>CyberCloud.Messaging/natsClusters</c> · M1 ·
///         0.8 EM"</i> — <i>"the cheapest provider in the catalogue, because we run NATS for ourselves
///         (ADR-005) and therefore already know how"</i>. It is the third and last M1 row, and the one
///         that closes that milestone's data-services gap.
///     </para>
///     <para>
///         ⚠ <b>IT IS THE FIRST SERVICE IN THE CATALOGUE WITH NO OPERATOR, AND THAT IS A FACT ABOUT
///         NATS RATHER THAN A CHOICE MADE HERE.</b> docs/plan/12's pattern assumes an operator: the
///         first three services render a <c>Cluster</c>, a <c>RedisFailover</c> and a <c>Kafka</c>,
///         and a controller turns each into pods. <c>nats-io/nats-operator</c> — the only project
///         that ever offered a <c>NatsCluster</c> CRD (<c>nats.io/v1alpha2</c>) — was
///         <b>archived on 2025-04-10</b> and its own README says <i>"the recommended way of running
///         NATS on Kubernetes is by using the Helm charts"</i> and that it <i>"is not recommended to
///         be used for new deployments"</i>. Checked against the repository rather than against a
///         summary of it; <c>charts/managed/nats/SOURCE</c> records the date.
///     </para>
///     <para>
///         ⚠ <b>So this provider renders the workload itself</b> — a <c>ConfigMap</c>, two
///         <c>Service</c>s, a <c>StatefulSet</c> and a <c>PodMonitor</c> — instead of one custom
///         resource an operator expands. Three consequences worth having written down before the
///         fifth service is costed:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>The object count is five rather than one or two</b>, and four of them are core or
///             <c>apps</c> kinds a bare cluster already serves. That makes this the one provider whose
///             cluster-backed suite needs almost no CRD stub — the reverse of what
///             <c>CyberCloud.Providers.Messaging.Cluster.Conformance</c>'s header found for Kafka.
///         </item>
///         <item>
///             <b>Every default the operator would have supplied is ours.</b> There is no controller
///             to fill in a probe, a security context or a termination grace period, so each is
///             written here and is a decision rather than an inheritance.
///         </item>
///         <item>
///             <b>docs/plan/12 § The pattern, once, piece 6's <i>second</i> branch is the only one
///             available</b> — <i>"hand-write one into the chart only when there is no operator to
///             ask"</i> — and unlike Kafka, where the branch was reached and the object was still
///             owed, it is <b>discharged</b> here: the reconciler owns the pod labels, so it can emit
///             a <c>PodMonitor</c> that selects them exactly. See <see cref="PodMonitorKind" />.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The catalogue's sub-resources are not declared, and the reason has changed since the
///         Kafka row was written.</b> docs/plan/12 says <i>"Accounts and users as sub-resources, with
///         NKey/JWT credentials into Vault"</i>. The grammar is no longer the obstacle — docs/plan/12
///         § Child resources landed on 2026-08-12 and <see cref="ResourceId.Parent" /> is a pure
///         function of the address. Two other things are, and they are named at
///         <c>charts/managed/nats/conformance.yaml § owed</c> rather than implied by an absence:
///         the conformance harness cannot construct the address of a depth-2 type at all, and NATS
///         accounts are not a CRD anybody offers. See <c>MessagingProvider</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/nats/values.yaml</c> is the other half — ADR-010 § Which end authors the
///         schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class NatsClusters {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string as <see cref="KafkaClusters.ProviderNamespace" />, and that is the
    ///     point rather than a copy.</b> This is the platform's first provider namespace with two
    ///     resource types in it — see <c>MessagingProvider</c> for what that exercised.
    /// </remarks>
    public const string ProviderNamespace = KafkaClusters.ProviderNamespace;

    /// <summary>The resource type. docs/plan/12 § The catalogue.</summary>
    public const string TypePath = "natsClusters";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/nats/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/nats";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the client URL and credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials. ⚠ <c>regenerateKeys</c> is named in
    ///     the same paragraph and is <b>not</b> declared, for the reason the two providers before this
    ///     one give: it is specified with a rolling grace period and nothing in the platform can hold
    ///     two live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of a cluster a holder of its credentials.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a cluster IS ──────────────────────────────────────────────────────────────

    /// <summary>The <c>ConfigMap</c> holding <c>nats.conf</c>.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>A <c>Service</c> — the headless one and the client one are the same kind.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    /// <summary>The <c>StatefulSet</c> that runs the servers.</summary>
    /// <remarks>
    ///     ⚠ A <c>StatefulSet</c> and not a <c>Deployment</c>, and the reason is the route list rather
    ///     than the volume. <see cref="ConfigJson" /> names every peer by its <i>ordinal</i> DNS
    ///     record — <c>nats-0.nats-headless</c> — which only a <c>StatefulSet</c> produces. A
    ///     <c>Deployment</c>'s pods have generated names, so the routes could not be written at all
    ///     without asking the cluster what they came out as, which is state a reconciler must not
    ///     hold.
    /// </remarks>
    public static GroupVersionKind StatefulSetKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "StatefulSet", Plural = "statefulsets" };

    /// <summary>
    ///     Prometheus Operator's <c>PodMonitor</c> — docs/plan/12 § The pattern, once, piece 6.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the first time piece 6's corrected second branch is actually
    ///         discharged.</b> That correction reads: <i>"ask the operator for the scrape object
    ///         wherever the operator accepts the request, and hand-write one into the chart only when
    ///         there is no operator to ask."</i> CloudNativePG answered the first branch with
    ///         <c>spec.monitoring.enablePodMonitor</c>; Strimzi reached the second branch and left the
    ///         object owed, because a hand-written scrape has to hard-code somebody else's pod labels
    ///         and this document warns that the operator changes one in a minor release and the scrape
    ///         goes quiet without failing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That hazard does not exist here, and the reason is the finding.</b> The labels the
    ///         selector matches are <see cref="PodLabels" /> — written by this file, onto pods created
    ///         by a <c>StatefulSet</c> written by this file. There is no upstream release that can
    ///         move them. So "hand-write it" is safe exactly when there is no operator, which is the
    ///         same condition that forces the branch: the two halves of the correction agree, and
    ///         nobody had a service to check that on until now.
    ///     </para>
    ///     <para>
    ///         ⚠ The plural is <c>podmonitors</c>. It is carried rather than derived for the reason
    ///         <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed
    ///         harness derives a definition stub from.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind PodMonitorKind { get; } =
        new() {
            Group = "monitoring.coreos.com", Version = "v1", Kind = "PodMonitor", Plural = "podmonitors"
        };

    // ── Ports ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The client port. What a <c>nats://</c> URL connects to.</summary>
    public const int ClientPort = 4222;

    /// <summary>The route port the servers gossip on. ⚠ Never exposed outside the cluster.</summary>
    public const int ClusterPort = 6222;

    /// <summary>The HTTP monitoring port — <c>/varz</c>, <c>/jsz</c>. Scraped, never routed publicly.</summary>
    public const int MonitorPort = 8222;

    /// <summary>The leaf-node port. docs/plan/12: <i>"so a tenant's edge can attach"</i>.</summary>
    public const int LeafNodePort = 7422;

    /// <summary>The port the metrics sidecar serves <c>/metrics</c> on.</summary>
    /// <remarks>
    ///     ⚠ <b>A sidecar exists because <c>nats-server</c> serves no Prometheus endpoint, which was
    ///     checked in <c>server/monitor.go</c> rather than assumed.</b> The monitoring port answers
    ///     <c>/varz</c>, <c>/connz</c>, <c>/routez</c>, <c>/subsz</c>, <c>/leafz</c>, <c>/jsz</c>,
    ///     <c>/raftz</c> and <c>/healthz</c> — all JSON, none of them Prometheus text. A
    ///     <see cref="PodMonitorKind" /> pointed at <c>8222/metrics</c> would apply cleanly, select
    ///     the right pods, and scrape a <c>404</c> forever: the object exists, the dashboard is empty,
    ///     and nothing anywhere is red. <c>prometheus-nats-exporter</c> is what turns the JSON into
    ///     metrics, and 7777 is its default.
    /// </remarks>
    public const int ExporterPort = 7777;

    /// <summary>Where the JetStream file store is mounted, and the name of the claim template.</summary>
    public const string StoreVolume = "store";

    /// <summary>The path <see cref="StoreVolume" /> is mounted at, and JetStream's <c>store_dir</c>.</summary>
    public const string StoreDir = "/data";

    /// <summary>The key <c>nats.conf</c> is filed under in the <c>ConfigMap</c>.</summary>
    public const string ConfigKey = "nats.conf";

    // ── Naming ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>StatefulSet</c>, the client <c>Service</c> and the <c>PodMonitor</c> take the resource's own name.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ConfigMapName(string name) => name + "-config";

    /// <summary>The headless <c>Service</c>'s name — the <c>StatefulSet</c>'s <c>serviceName</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>This string appears in three places and they must be one spelling.</b> It is the
    ///     <c>Service</c>'s own name, the <c>StatefulSet</c>'s <c>spec.serviceName</c>, and the middle
    ///     segment of every route in <see cref="ConfigJson" />. Two spellings is a cluster whose
    ///     servers never find each other, and NATS reports that as three healthy standalone servers
    ///     rather than as an error.
    /// </remarks>
    public static string HeadlessServiceName(string name) => name + "-headless";

    /// <summary>The <c>ConfigMap</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ConfigMapRef(string ns, string name) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ConfigMapName(name) };

    /// <summary>The headless <c>Service</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef HeadlessServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = HeadlessServiceName(name) };

    /// <summary>The client <c>Service</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClientServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = name };

    /// <summary>The <c>StatefulSet</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef StatefulSetRef(string ns, string name) =>
        new() { Kind = StatefulSetKind, Namespace = ns, Name = name };

    /// <summary>The <c>PodMonitor</c> a cluster owns when monitoring is on.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PodMonitorRef(string ns, string name) =>
        new() { Kind = PodMonitorKind, Namespace = ns, Name = name };

    /// <summary>The in-cluster client URL <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string ClientUrl(string ns, string name) =>
        "nats://"
        + name
        + "."
        + ns
        + ".svc:"
        + ClientPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     The labels on the pods, on the <c>StatefulSet</c>'s selector, on both <c>Service</c>
    ///     selectors and in the <c>PodMonitor</c>'s <c>selector.matchLabels</c>.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>These are NOT ADR-013's seven, and the distinction matters.</b> The seven are injected
    ///     by <c>KubeCommand</c> onto each object's own <c>metadata.labels</c>, non-overridably. These
    ///     four sit inside <c>spec.template.metadata.labels</c>, which is a field of the
    ///     <c>StatefulSet</c>'s <i>body</i> and which no builder reaches — so a pod created by this
    ///     resource carries these and does not carry the seven. That is a real gap and it is written
    ///     down in <c>charts/managed/nats/conformance.yaml § owed</c> as <c>pod-labels</c>, because
    ///     the seven exist so that a pod can be attributed to a tenant and these pods cannot be.
    ///     <para>
    ///         ⚠ A <c>StatefulSet</c>'s <c>spec.selector</c> is immutable after create, so this
    ///         function's output is part of the resource's identity: changing it is a resource that
    ///         can never be updated again, which the API server reports as an invalid update rather
    ///         than as drift.
    ///     </para>
    /// </remarks>
    public static (string Key, string Value)[] PodLabels(string name) => [
        ("app.kubernetes.io/name", "nats"),
        ("app.kubernetes.io/instance", name),
        ("app.kubernetes.io/component", "server"),
        ("app.kubernetes.io/managed-by", "cybercloud")
    ];

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied, which is the whole of what
    ///     that type's remarks ask for.</b> <c>KafkaClusters.QuantityPattern</c> — in this very
    ///     assembly — still carries a byte-identical literal, and <c>QuantityParserTests</c> exists
    ///     because the last time a provider kept its own copy of the grammar, somebody wrote a second
    ///     parser next to it. A fourth copy is what that test now fails on. There is no rule-2 problem
    ///     in reaching for it: <see cref="KubeQuantity" /> lives in
    ///     <c>CyberCloud.ResourceManager.Contracts</c>, which every provider may reference.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>An IPv4 CIDR block. ⚠ Written down, tested, and enforced by nothing — see below.</summary>
    /// <remarks>
    ///     ⚠ <b>The second sighting of the gap <c>KafkaClusters.CidrPattern</c> reported, and a
    ///     second sighting is what turns it into a measurement.</b> The registry would take this as
    ///     <c>Pattern</c> on <see cref="Schema2026" />'s <c>allowedCidrs</c> and enforce it per
    ///     element; <c>./build.sh Charts</c> refuses <c>@pattern</c> on a <c>{array}</c> while
    ///     emitting <c>@enum</c> there as <c>items.enum</c>, which is the same per-element shape. The
    ///     full argument is at the Kafka constant and is not repeated; what this adds is that the gap
    ///     is not specific to one type's allow-list — <b>every service in docs/plan/12 § Cross-cutting
    ///     decisions gets the same firewall list</b>, so it will be found once per remaining service
    ///     until it is closed.
    /// </remarks>
    public const string CidrPattern = KafkaClusters.CidrPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>c1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>c1</c> family, which docs/plan/12 § Sizing vocabulary assigns to
    ///     <i>"CPU-bound — brokers, gateways"</i>.</b> A NATS server with JetStream on file storage is
    ///     arguably closer to <c>s1</c> (1:4, <i>"general"</i>), and the table was followed rather than
    ///     second-guessed: the vocabulary exists so that "large" means one thing across the catalogue,
    ///     and a service that picks a different family because its author reasoned about its workload
    ///     is the drift the table was written to stop. If NATS belongs in <c>s1</c>, that is a change
    ///     to docs/plan/12 and then to this row, in that order.
    ///     <para>
    ///         ⚠ <b>It is deliberately not <c>KafkaClusters.Presets</c>, even though the values are
    ///         identical and the two types are in one assembly.</b> Sharing them would make a change
    ///         to Kafka's sizing a silent change to this one, and the two rows have different reasons
    ///         to move — Kafka's is a broker's CPU, this one's is whatever docs/plan/12 decides a NATS
    ///         server is. <c>NatsSizingTests</c> asserts this table against the chart's
    ///         <c>_helpers.tpl</c>, and <c>NatsDeclarationTests</c> asserts the ratio is 1:2 at every
    ///         rung, which is what <c>c1</c> means — so a row that drifts off the family is caught
    ///         without coupling the two services.
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
                    Description: "The cluster whose namespace holds the NATS servers."
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
                    Description: "NATS server version. Minor upgrades are applied automatically in the "
                    + "maintenance window; a major upgrade is an explicit update to this field."
                ) {
                    AllowedValues = ["2.10", "2.11"],
                    DefaultJson = "\"2.11\""
                },
                new(
                    "/properties/servers",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of NATS servers. docs/plan/12 says three or five: JetStream "
                    + "replicates through a Raft group, so an even count buys no extra fault tolerance "
                    + "over the odd count below it. One is offered for development only and has no "
                    + "quorum at all."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per server, either by preset or explicitly."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Brokers use the c1 family, which "
                    + "is 1 vCPU to 2 GiB and guaranteed rather than burstable."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
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
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The JetStream file store, per server."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "JetStream file-store volume size per server, in Kubernetes quantity "
                    + "form. Grows online; never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"10Gi\"",
                    ExampleJson = "\"10Gi\""
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
                    "/properties/jetstream",
                    SchemaKind.Nested,
                    Description: "JetStream — the persistence layer streams and consumers live in. "
                    + "docs/plan/12 has it on unconditionally, so there is no switch to turn it off."
                ),
                new(
                    "/properties/jetstream/maxMemoryStore",
                    SchemaKind.Text,
                    Description: "Ceiling for memory-backed streams, in Kubernetes quantity form. "
                    + "Empty means no memory store at all, so every stream is file-backed — which is "
                    + "what docs/plan/12 asks for and what the volume above is sized for. A value here "
                    + "must leave room for the server itself inside the container's memory limit."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"256Mi\""
                },
                new(
                    "/properties/limits",
                    SchemaKind.Nested,
                    Description: "Server limits a client can run into."
                ),
                new(
                    "/properties/limits/maxPayload",
                    SchemaKind.WholeNumber,
                    Description: "Largest message a client may publish, in bytes. Raising it costs "
                    + "memory on every server, because a server buffers a whole message before routing "
                    + "it."
                ) {
                    Minimum = 1024,
                    Maximum = 67108864,
                    DefaultJson = "1048576"
                },
                new(
                    "/properties/limits/maxConnections",
                    SchemaKind.WholeNumber,
                    Description: "Largest number of client connections one server accepts."
                ) {
                    Minimum = 16,
                    Maximum = 65536,
                    DefaultJson = "65536"
                },
                new(
                    "/properties/leafNodes",
                    SchemaKind.Nested,
                    Description: "Leaf-node connectivity — docs/plan/12: \"so a tenant's edge can "
                    + "attach\"."
                ),
                new(
                    "/properties/leafNodes/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the servers accept leaf-node connections. Off by default: a "
                    + "leaf node joins the cluster's subject space, so it is a topology change rather "
                    + "than a client."
                ) {
                    DefaultJson = "false"
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
                    // ⚠ `Pattern = CidrPattern` belongs here and is not declared. It is the same
                    // registry-versus-surface gap KafkaClusters reports at the same property, for the
                    // same reason, and this is its second sighting. Declaring it would make
                    // `./build.sh Charts` red for every future run.
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = "[\"203.0.113.0/24\"]"
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether a PodMonitor selects these servers' monitoring endpoint. On "
                    + "by default — docs/plan/12: \"a managed service the tenant cannot see the health "
                    + "of is a black box they will not trust with production\". The endpoint itself is "
                    + "always served; this decides whether anything scrapes it."
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
                    "/url",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster client URL, nats://host:port. ⚠ The external "
                    + "listener's address is not returned here — an address a client cannot reach from "
                    + "where it is running is worse than an absent one."
                ),
                new(
                    "/monitoringUrl",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster HTTP monitoring endpoint, http://host:port — /varz "
                    + "and /jsz. Not secret, and returned here because a caller holding the client URL "
                    + "is the caller who needs it."
                ),
                new("/user", SchemaKind.Text, Required: true, Description: "The client user."),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The client user's password, read from the tenant's Vault for this "
                    + "call only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The NATS version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>The server count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Servers(JsonElement desired) => Number(desired, "servers", DefaultServers);

    /// <summary>The file-store volume size per server a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>Whether the desired body asks for a leaf-node listener.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool LeafNodesEnabled(JsonElement desired) => Flag(desired, "leafNodes", "enabled", false);

    /// <summary>Whether the desired body asks for external exposure.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The declared value, or the schema default (<c>false</c>) when absent. ⚠ The default is
    ///     applied <i>here</i> rather than by the write path, which stores the body as sent —
    ///     <see cref="SchemaProperty.DefaultJson" />'s own remarks say the validator does not
    ///     substitute.
    /// </returns>
    public static bool ExternalEnabled(JsonElement desired) => Flag(desired, "external", "enabled", false);

    /// <summary>Whether the desired body asks for a scrape object.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) => Flag(desired, "monitoring", "enabled", true);

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
        var preset = Text(desired, "sizing", "preset", DefaultPreset);
        var fallback = Presets.TryGetValue(preset, out var found)
            ? found
            : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>nats.conf</c> a desired body becomes.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The configuration file, LF-terminated lines, deterministic for a given body.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>server_name: $POD_NAME</c> rather than a literal.</b> Every server in a cluster
    ///         needs a distinct name — NATS refuses a route from a server whose name it already knows
    ///         — and one <c>ConfigMap</c> is shared by every pod. NATS expands <c>$VAR</c> from the
    ///         process environment when it reads its configuration, and the <c>StatefulSet</c> sets
    ///         <c>POD_NAME</c> from a <c>fieldRef</c>. The alternative is one <c>ConfigMap</c> per
    ///         replica, which turns a replica count change into an object-set change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The routes are written out one per ordinal rather than as the headless service's
    ///         own name.</b> A single <c>nats://x-headless:6222</c> resolves to every ready pod, which
    ///         is exactly the problem: at first start no pod is ready, so the list is empty and no
    ///         cluster forms. Naming <c>x-0.x-headless</c> … addresses a record that exists as soon as
    ///         the <c>StatefulSet</c> does, and the headless <c>Service</c> sets
    ///         <c>publishNotReadyAddresses</c> so it stays addressable while the server is starting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Byte sizes are written as integers, through <see cref="KubeQuantity.TryParse" />
    ///         and no other parser — and the reason is one suffix out of fourteen.</b> The obvious
    ///         alternative is to pass the quantity through, and it very nearly works: NATS'
    ///         <c>conf/parse.go</c> accepts <c>k kb ki kib m mb mi mib g gb gi gib</c> and the
    ///         <c>t</c>/<c>p</c>/<c>e</c> families, <b>lower-cased before comparison</b>, with
    ///         <c>ki</c> = 1024 and <c>gi</c> = 2<sup>30</sup> — the same numbers Kubernetes means.
    ///         <b>The exception is <c>m</c>.</b> Kubernetes reads it as a <i>milli</i> and NATS reads
    ///         it as a <i>mega</i>, so <c>500m</c> is half a byte to the volume and five hundred
    ///         million to JetStream, and <b>neither side complains</b> — the file-store ceiling would
    ///         simply be a billion times the disk. <see cref="Schema2026" />'s pattern permits
    ///         <c>m</c> on a storage size, because it is the same quantity grammar every property on
    ///         every provider uses. Converting once, in the platform's single parser, is what removes
    ///         the question.
    ///     </para>
    /// </remarks>
    public static string ConfigJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var built = new StringBuilder(512);
        var headless = HeadlessServiceName(name);

        built.Append("server_name: $POD_NAME\n");
        built.Append("listen: 0.0.0.0:").Append(Text(ClientPort)).Append('\n');
        built.Append("http: 0.0.0.0:").Append(Text(MonitorPort)).Append('\n');
        built.Append("max_payload: ")
            .Append(Text(Number(desired, "limits", "maxPayload", DefaultMaxPayload)))
            .Append('\n');
        built.Append("max_connections: ")
            .Append(Text(Number(desired, "limits", "maxConnections", DefaultMaxConnections)))
            .Append('\n');

        built.Append("jetstream {\n");
        built.Append("  store_dir: ").Append(StoreDir).Append('\n');
        built.Append("  max_file_store: ").Append(Bytes(StorageSize(desired), DefaultStorageSize)).Append('\n');
        // ⚠ An absent ceiling is written as an explicit 0 rather than omitted. NATS' own default for
        // max_memory_store is a fraction of system memory, so omitting the key would give a
        // memory-backed stream a budget nobody chose — inside a container whose limit the server does
        // not read. Zero is "file storage only", which is what docs/plan/12 asks this service for.
        built.Append("  max_memory_store: ")
            .Append(Bytes(Text(desired, "jetstream", "maxMemoryStore", string.Empty), "0"))
            .Append('\n');
        built.Append("}\n");

        built.Append("cluster {\n");
        built.Append("  name: ").Append(name).Append('\n');
        built.Append("  listen: 0.0.0.0:").Append(Text(ClusterPort)).Append('\n');
        built.Append("  routes = [\n");
        for (var ordinal = 0; ordinal < Servers(desired); ordinal++) {
            built.Append("    nats://")
                .Append(name)
                .Append('-')
                .Append(Text(ordinal))
                .Append('.')
                .Append(headless)
                .Append(':')
                .Append(Text(ClusterPort))
                .Append('\n');
        }

        built.Append("  ]\n");
        built.Append("}\n");

        if (LeafNodesEnabled(desired)) {
            built.Append("leafnodes {\n");
            built.Append("  listen: 0.0.0.0:").Append(Text(LeafNodePort)).Append('\n');
            built.Append("}\n");
        }

        return built.ToString();
    }

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///     annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///     place a key and a value are syntax-checked.
    /// </remarks>
    public static string ConfigMapJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ConfigMapName(name) },
            ["data"] = new JsonObject { [ConfigKey] = ConfigJson(name, desired) }
        }.ToJsonString();
    }

    /// <summary>The headless <c>Service</c> document — stable per-pod DNS for the route list.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>It takes no body, and that is deliberate.</b> Nothing a tenant can set changes it, so
    ///     a signature that accepted the desired body would invite somebody to make one. It exists to
    ///     give <c>{name}-{ordinal}.{name}-headless</c> a DNS record, and
    ///     <c>publishNotReadyAddresses</c> is what makes that record exist before the server behind it
    ///     is ready — without which no cluster ever forms, because every peer is unready at first
    ///     start.
    /// </remarks>
    public static string HeadlessServiceJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = HeadlessServiceName(name) },
            ["spec"] = new JsonObject {
                ["clusterIP"] = "None",
                ["publishNotReadyAddresses"] = true,
                ["selector"] = Selector(name),
                ["ports"] = new JsonArray {
                    Port("client", ClientPort), Port("cluster", ClusterPort), Port("monitor", MonitorPort)
                }
            }
        }.ToJsonString();
    }

    /// <summary>The client <c>Service</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The cluster route port is not on this Service at any exposure level.</b> A
    ///         <c>LoadBalancer</c> carrying 6222 would put the servers' own gossip on a public address,
    ///         where an unauthenticated route is a second server joining the cluster. Only the client
    ///         port and — when asked for — the leaf-node port are ever reachable from outside.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>loadBalancerSourceRanges</c> is written even when the list is EMPTY.</b> An
    ///         absent field means "from anywhere", so an unfinished allow-list would be the
    ///         configuration that opens the cluster to the internet — the reading docs/plan/12
    ///         § Cross-cutting decisions forbids.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The monitoring port is never on this Service.</b> <c>/varz</c> answers with the
    ///         server's configuration and <c>/connz</c> with its clients; the <c>PodMonitor</c>
    ///         scrapes the pod directly, so nothing is lost by keeping it off a routable address.
    ///     </para>
    /// </remarks>
    public static string ClientServiceJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var ports = new JsonArray { Port("client", ClientPort) };
        if (LeafNodesEnabled(desired)) {
            ports.Add(Port("leafnodes", LeafNodePort));
        }

        var spec = new JsonObject {
            ["type"] = ExternalEnabled(desired) ? "LoadBalancer" : "ClusterIP",
            ["selector"] = Selector(name),
            ["ports"] = ports
        };

        if (ExternalEnabled(desired)) {
            var ranges = new JsonArray();
            foreach (var cidr in AllowedCidrs(desired)) {
                ranges.Add(cidr);
            }

            spec["loadBalancerSourceRanges"] = ranges;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>The <c>StatefulSet</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>podManagementPolicy: Parallel</c>, and the default would deadlock the first
    ///         start.</b> The default is <c>OrderedReady</c>: pod 1 is not created until pod 0 is
    ///         ready. Pod 0's readiness probe hits <c>/healthz</c>, which a clustered server does not
    ///         pass until it has a Raft quorum, which needs pod 1. <c>Parallel</c> is what makes a
    ///         three-server cluster reach quorum at all, and it is the kind of default an operator
    ///         would have supplied and there is no operator.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The annotation carrying the config's hash is deliberately NOT written.</b> The
    ///         usual Helm idiom restarts pods when the <c>ConfigMap</c> changes; here it would make
    ///         every render depend on a digest, and a digest of a document this file also writes is
    ///         two spellings of one fact. NATS re-reads its configuration on <c>SIGHUP</c> and the
    ///         reload is written down as owed rather than half-built.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Requests equal limits.</b> The <c>c1</c> family is "guaranteed" in docs/plan/12
    ///         § Sizing vocabulary, and guaranteed is a Kubernetes QoS class you get by setting them
    ///         equal.
    ///     </para>
    /// </remarks>
    public static string StatefulSetJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var ports = new JsonArray {
            ContainerPort("client", ClientPort),
            ContainerPort("cluster", ClusterPort),
            ContainerPort("monitor", MonitorPort)
        };

        if (LeafNodesEnabled(desired)) {
            ports.Add(ContainerPort("leafnodes", LeafNodePort));
        }

        var container = new JsonObject {
            ["name"] = "nats",
            ["image"] = "nats:" + Version(desired) + "-alpine",
            ["args"] = new JsonArray { "--config", "/etc/nats/" + ConfigKey },
            ["ports"] = ports,
            ["env"] = new JsonArray {
                new JsonObject {
                    ["name"] = "POD_NAME",
                    ["valueFrom"] = new JsonObject {
                        ["fieldRef"] = new JsonObject { ["fieldPath"] = "metadata.name" }
                    }
                }
            },
            ["volumeMounts"] = new JsonArray {
                new JsonObject { ["name"] = "config", ["mountPath"] = "/etc/nats" },
                new JsonObject { ["name"] = StoreVolume, ["mountPath"] = StoreDir }
            },
            // ⚠ /healthz?js-enabled-only=true rather than bare /healthz on the READINESS probe. The
            // bare endpoint reports a stream as unhealthy while it is catching up, so a rolling
            // update would take every pod out of the client Service at once on a cluster that was
            // working. The liveness probe below asks the weaker question — is the process serving
            // HTTP at all — because a liveness probe that fails on a slow catch-up restarts the pod
            // that was catching up.
            ["readinessProbe"] = Probe("/healthz?js-enabled-only=true", 5, 3),
            ["livenessProbe"] = Probe("/healthz?js-server-only=true", 10, 5)
        };

        var (cpu, memory) = Resources(desired);
        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            container["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        var claim = new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = StoreVolume },
            ["spec"] = new JsonObject {
                ["accessModes"] = new JsonArray { "ReadWriteOnce" },
                ["resources"] = new JsonObject {
                    ["requests"] = new JsonObject { ["storage"] = StorageSize(desired) }
                }
            }
        };

        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            ((JsonObject)claim["spec"]!)["storageClassName"] = storageClass;
        }

        var containers = new JsonArray { container };

        // ⚠ The exporter runs whenever monitoring does, and it is a SIDECAR rather than a Deployment
        // of its own because it scrapes `localhost:8222` — a per-server endpoint. One exporter for
        // the cluster would report whichever server the client Service happened to route it to, which
        // is a metric that changes meaning between scrapes. It declares no `resources`, so it
        // requests nothing from the scheduler and there is no figure for a quota meter to reserve;
        // that is the same real under-count `PostgresProvider` records about its PgBouncer, and it
        // closes the same way — the block goes in the chart first, and the meter gains a term rather
        // than a guess.
        if (MonitoringEnabled(desired)) {
            containers.Add(
                new JsonObject {
                    ["name"] = "metrics",
                    ["image"] = "natsio/prometheus-nats-exporter:latest",
                    ["args"] = new JsonArray {
                        "-connz",
                        "-routez",
                        "-subz",
                        "-varz",
                        "-jsz=all",
                        "-port",
                        Text(ExporterPort),
                        "http://localhost:" + Text(MonitorPort)
                    },
                    ["ports"] = new JsonArray { ContainerPort("metrics", ExporterPort) }
                }
            );
        }

        var spec = new JsonObject {
            ["replicas"] = Servers(desired),
            ["serviceName"] = HeadlessServiceName(name),
            ["podManagementPolicy"] = "Parallel",
            ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
            ["template"] = new JsonObject {
                ["metadata"] = new JsonObject { ["labels"] = Selector(name) },
                ["spec"] = new JsonObject {
                    // ⚠ 60 seconds, not the default 30. A NATS server given SIGTERM performs a lame
                    // duck shutdown — it stops accepting connections, lets clients migrate, and only
                    // then exits. Killing it at 30 seconds turns a rolling update into dropped
                    // connections, which is the failure a managed broker exists to not have.
                    ["terminationGracePeriodSeconds"] = 60,
                    ["containers"] = containers,
                    ["volumes"] = new JsonArray {
                        new JsonObject {
                            ["name"] = "config",
                            ["configMap"] = new JsonObject { ["name"] = ConfigMapName(name) }
                        }
                    }
                }
            },
            ["volumeClaimTemplates"] = new JsonArray { claim }
        };

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>The <c>PodMonitor</c> document — piece 6's second branch, discharged.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ It takes no body. The only thing a body decides about this object is whether it exists
    ///     at all, which is the reconciler's question rather than this function's.
    /// </remarks>
    public static string PodMonitorJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                // ⚠ The SIDECAR's port, not the server's. See ExporterPort — nats-server serves no
                // Prometheus endpoint at all, so `monitor` here would scrape a 404 forever behind an
                // object that applied cleanly and selected the right pods.
                ["podMetricsEndpoints"] = new JsonArray {
                    new JsonObject { ["port"] = "metrics", ["path"] = "/metrics" }
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
    ///         ⚠ <b>Containment, not equality, and here the ordinary reason is the true one — which is
    ///         the opposite of what <c>KafkaClusters.Matches</c> found and is worth stating as a
    ///         contrast rather than as a restatement.</b> That type checked Strimzi's CRD and found
    ///         <b>no <c>default:</c> anywhere</b>, so the "the API server defaults fields on write"
    ///         argument was false for it and the rule was kept for other reasons. Every object here is
    ///         a <b>built-in</b> kind, and built-in kinds are the most heavily defaulted objects in
    ///         Kubernetes: a <c>Service</c> comes back with <c>clusterIP</c>, <c>ipFamilies</c>,
    ///         <c>sessionAffinity</c> and <c>internalTrafficPolicy</c>; a <c>StatefulSet</c> with
    ///         <c>updateStrategy</c>, <c>revisionHistoryLimit</c>, a <c>terminationMessagePath</c> and
    ///         an <c>imagePullPolicy</c> on every container. Equality would report drift on the first
    ///         read of every resource, forever.
    ///     </para>
    ///     <para>
    ///         The reasons the earlier providers give also hold: server-side apply leaves other
    ///         managers' fields in place, the document carries <c>metadata</c>, <c>managedFields</c>
    ///         and a controller-written <c>status</c>, and <c>spec.replicas</c> on a
    ///         <c>StatefulSet</c> has a <c>scale</c> subresource any autoscaler can write — which
    ///         containment reports as drift on the one field that moved, exactly as it should.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on the object's <c>kind</c> rather than taking it as a parameter, because
    ///         a conformance case supplies this as one function over every object the resource owns.
    ///         ⚠ Two <c>Service</c>s share a kind, so the headless one is told apart by its
    ///         <c>clusterIP</c> rather than by its name — a name comparison would need the resource's
    ///         name, which is not in this function's arguments.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => MatchesConfigMap(document, desired),
            "Service" => document["spec"] is JsonObject service && MatchesService(service, desired),
            "PodMonitor" => document["spec"] is JsonObject monitor
                && monitor["podMetricsEndpoints"] is JsonArray { Count: > 0 },
            _ => document["spec"] is JsonObject set && MatchesStatefulSet(set, desired)
        };
    }

    static bool MatchesConfigMap(JsonObject document, JsonElement desired) =>
        (document["data"] as JsonObject)?[ConfigKey]?.GetValue<string>() is { } configured
        && configured.Contains(
            "max_file_store: " + Bytes(StorageSize(desired), DefaultStorageSize),
            StringComparison.Ordinal
        )
        && CountRoutes(configured) == Servers(desired);

    static bool MatchesService(JsonObject spec, JsonElement desired) {
        // The headless one carries no exposure setting at all — it is `clusterIP: None` in every body
        // this type can produce, so the only thing to check is that it stayed headless.
        if (spec["clusterIP"]?.GetValue<string>() == "None") {
            return true;
        }

        var external = ExternalEnabled(desired);

        return spec["type"]?.GetValue<string>() == (external ? "LoadBalancer" : "ClusterIP")
            // ⚠ Present-and-a-list when exposed, rather than element-by-element. An allow-list the
            // API server dropped is the difference between a firewalled address and an open one, and
            // that is the fact worth reading back; which ranges are in it is what the body says.
            && (!external || spec["loadBalancerSourceRanges"] is JsonArray);
    }

    static bool MatchesStatefulSet(JsonObject spec, JsonElement desired) {
        if (spec["replicas"]?.GetValue<int>() != Servers(desired)) {
            return false;
        }

        var claims = spec["volumeClaimTemplates"] as JsonArray;
        if (claims is not { Count: > 0 }) {
            return false;
        }

        var requests = ((claims[0] as JsonObject)?["spec"] as JsonObject)?["resources"] as JsonObject;

        return (requests?["requests"] as JsonObject)?["storage"]?.GetValue<string>() == StorageSize(desired);
    }

    static int CountRoutes(string config) {
        var found = 0;
        var at = 0;
        while ((at = config.IndexOf("    nats://", at, StringComparison.Ordinal)) >= 0) {
            found++;
            at += 1;
        }

        return found;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the NATS cluster in.</param>
    /// <param name="servers">How many servers.</param>
    /// <param name="storageSize">The JetStream file-store volume size per server.</param>
    /// <param name="leafNodes">Whether the leaf-node listener is on.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int servers = 3,
        string storageSize = "10Gi",
        bool leafNodes = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["servers"] = servers,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["limits"] = new JsonObject { ["maxPayload"] = DefaultMaxPayload },
                ["leafNodes"] = new JsonObject { ["enabled"] = leafNodes },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "2.11";
    const string DefaultPreset = "c1.small";
    const string DefaultStorageSize = "10Gi";
    const int DefaultServers = 3;
    const int DefaultMaxPayload = 1048576;
    const int DefaultMaxConnections = 65536;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    static JsonObject Selector(string name) {
        var selector = new JsonObject();
        foreach (var (key, value) in PodLabels(name)) {
            selector[key] = value;
        }

        return selector;
    }

    static JsonObject Port(string name, int port) =>
        new() { ["name"] = name, ["port"] = port, ["targetPort"] = name, ["protocol"] = "TCP" };

    static JsonObject ContainerPort(string name, int port) =>
        new() { ["name"] = name, ["containerPort"] = port, ["protocol"] = "TCP" };

    static JsonObject Probe(string path, int period, int failureThreshold) =>
        new() {
            ["httpGet"] = new JsonObject { ["path"] = path, ["port"] = "monitor" },
            ["periodSeconds"] = period,
            ["failureThreshold"] = failureThreshold
        };

    /// <summary>A Kubernetes quantity as a byte count, for a configuration file that takes numbers.</summary>
    /// <param name="quantity">The quantity, or the empty string.</param>
    /// <param name="fallback">What an empty or unparseable value means. ⚠ A quantity, not a number.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="KubeQuantity.TryParse" /> and nothing else.</b> This platform has had two
    ///     quantity parsers once already — <c>KubeQuantity</c>'s remarks record what the second one
    ///     got wrong and that <c>QuantityParserTests</c> now fails if a third appears. What is here is
    ///     a <i>formatting</i> rule (bytes, as an integer, invariant) rather than a grammar.
    /// </remarks>
    static string Bytes(string quantity, string fallback) {
        if (!KubeQuantity.TryParse(quantity, out var value) && !KubeQuantity.TryParse(fallback, out value)) {
            // Unreachable from a validated body: `fallback` is a literal in this file and every
            // caller passes one this platform's own parser accepts.
            return "0";
        }

        return decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);
    }

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
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var found)
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
