using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Messaging/rabbitmqClusters</c>: the type, its
///     api-version, its body shape, and the one <c>RabbitmqCluster</c> it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"RabbitMQ — <c>CyberCloud.Messaging/rabbitmqClusters</c> ·
///         M2 · 0.8 EM"</i>, on the <b>RabbitMQ Cluster Operator</b> (ADR-010 clause 1 names it in the
///         operator survey; ADR-011 clears the licence — the operator and the broker are both
///         Apache-2.0 / MPL-2.0 and neither is SSPL). It is the <b>third</b> type in this provider
///         namespace, after <see cref="KafkaClusters" /> and <see cref="NatsClusters" />.
///     </para>
///     <para>
///         ⚠ <b>WHAT "QUORUM QUEUES BY DEFAULT" ACTUALLY COSTS, BECAUSE IT IS NOT A FIELD ON THE
///         CUSTOM RESOURCE AND THE CATALOGUE SENTENCE READS AS IF IT WERE.</b> docs/plan/12 says
///         <i>"Quorum queues by default — classic mirrored queues are deprecated upstream and
///         default-to-deprecated is a trap"</i>. Checked against RabbitMQ and against the operator's
///         CRD rather than against that sentence, three things are true and the third is the one that
///         shapes this type:
///     </para>
///     <list type="number">
///         <item>
///             <b>Classic queue <i>mirroring</i> was REMOVED in RabbitMQ 4.0</b>, not merely
///             deprecated — it was deprecated in 3.9 and deleted in 4.0. <see cref="Schema2026" />
///             offers <c>4.0</c> and <c>4.1</c> and nothing older, so on every version this type can
///             run there is no mirrored queue to fall back to.
///         </item>
///         <item>
///             <b>That makes the trap WORSE rather than moot, and this is the correction worth
///             having.</b> The catalogue sentence reads as "the deprecated thing is the default".
///             On 4.x the default queue type is <c>classic</c> and classic is now
///             <i>unreplicated</i> — a queue that lives on exactly one node and is lost with it. So
///             an unset default on a three-node cluster is not "replicated the deprecated way", it
///             is <b>not replicated at all</b>, on a cluster the tenant paid three nodes for.
///         </item>
///         <item>
///             <b>The switch is a <c>rabbitmq.conf</c> line and the operator offers no spec field for
///             it.</b> The key is <c>default_queue_type</c> — node-wide in <c>rabbitmq.conf</c>, and a
///             per-vhost setting overrides it. The only way to set it through the CRD is
///             <c>spec.rabbitmq.additionalConfig</c>, a free-text INI string
///             (<c>maxLength: 100000</c>). So <see cref="DefaultQueueTypePointer" /> — the property
///             this whole row exists for — reaches the broker as a <b>line inside a string</b> rather
///             than as a typed field, which is why <see cref="AdditionalConfig" /> is a rendered
///             document with its own ordering rule and its own test.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>And the per-vhost override is the half nothing here can close.</b>
///         <c>default_queue_type</c> in <c>rabbitmq.conf</c> is a <i>node-wide fallback</i>; a vhost
///         created with its own default queue type wins over it. This type manages no vhosts — see
///         <c>charts/managed/rabbitmq/conformance.yaml § owed</c>, <c>child-types</c> — so the
///         guarantee it makes is "the default vhost's queues are quorum", not "every queue in this
///         cluster is quorum". Stating the stronger one would be the same class of mistake as selling
///         FerretDB as MongoDB.
///     </para>
///     <para>
///         ⚠ <b>Quorum queues are Raft groups, so the node count wants to be ODD and at least
///         three — and the registry still cannot say "odd". THIRD SIGHTING.</b>
///         <c>KafkaClusters</c> reports it for a KRaft quorum and <c>NatsClusters</c> for a JetStream
///         one; this is a third consensus protocol asking the same unexpressible thing.
///         <c>SchemaProperty.AllowedValues</c> is <see cref="SchemaKind.Text" />-only by construction
///         and <c>Minimum</c>/<c>Maximum</c> cannot say "odd", so the constraint is in the
///         description and in nothing that enforces it. Two sightings made it a measurement; three
///         make it a property of the <i>catalogue</i> rather than of any one consensus protocol.
///     </para>
///     <para>
///         ⚠ <b>THE OPERATOR'S CRD DECLARES <c>default:</c> VALUES, AND A MUTATING WEBHOOK WRITES
///         <c>spec</c> — WHICH IS THE OPPOSITE OF WHAT STRIMZI DOES.</b> <see cref="Matches" /> is
///         containment for the <i>ordinary</i> reason here, and <c>KafkaClusters.Matches</c> is at
///         pains to say the ordinary reason is false for it. Verified against
///         <c>config/crd/bases/rabbitmq.com_rabbitmqclusters.yaml</c> rather than against the
///         operator's README — see <c>charts/managed/rabbitmq/SOURCE</c>, which lists every default.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/rabbitmq/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class RabbitmqClusters {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string as <see cref="KafkaClusters.ProviderNamespace" /> and
    ///     <see cref="NatsClusters.ProviderNamespace" />, and this is the platform's first namespace
    ///     with THREE resource types in it.</b> <c>MessagingSdkTests</c> predicted this type by name
    ///     — <i>"that is the claim a third type in this namespace — <c>rabbitmqClusters</c> is next
    ///     in docs/plan/12 — can break"</i> — and it is extended rather than restated.
    /// </remarks>
    public const string ProviderNamespace = KafkaClusters.ProviderNamespace;

    /// <summary>The resource type. docs/plan/12 § The catalogue.</summary>
    /// <remarks>
    ///     ⚠ <b>Lower-case <c>rabbitmq</c>, not <c>rabbitMq</c> and not <c>rabbitMQ</c>.</b> The
    ///     catalogue spells it <c>rabbitmqClusters</c>, and <c>CliEmitter.CommandOf</c> kebab-cases
    ///     the type path on case transitions — <c>rabbitMqClusters</c> would give
    ///     <c>rabbit-mq-clusters</c>, which is not a verb anybody would type and not the one
    ///     docs/plan/12 implies. <c>RabbitmqOpenApiCasingTests</c> pins the whole string as a literal.
    /// </remarks>
    public const string TypePath = "rabbitmqClusters";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/rabbitmq/Chart.yaml</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It is the same date as the other two types in this namespace, and that is a
    ///     constraint rather than a coincidence.</b> <c>OpenApiEmitter.ApiVersionsOf</c> returns the
    ///     set of dates a registry declares and <c>MessagingSdkTests</c> calls <c>Single()</c> on it;
    ///     more than that, docs/plan/10 emits one document per api-version, so a third date here
    ///     would split this namespace's three types across two documents for no gain.
    /// </remarks>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/rabbitmq";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The pointer this row exists for. Named because its value is a config line.</summary>
    /// <remarks>
    ///     See this type's remarks: <c>default_queue_type</c> is not a CRD field, so this property
    ///     reaches the broker through <see cref="AdditionalConfig" /> rather than through a typed
    ///     spec member. Naming the pointer is what lets a test assert the reach without spelling the
    ///     path twice.
    /// </remarks>
    public const string DefaultQueueTypePointer = "/properties/queues/defaultType";

    /// <summary>The action that hands a caller the AMQP URL and credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials. ⚠ <c>regenerateKeys</c> is named in
    ///     the same paragraph and is <b>not</b> declared, for the reason the three providers before
    ///     this one give: it is specified with a rolling grace period and nothing in the platform can
    ///     hold two live credentials for one resource.
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

    // ── The object a cluster IS ───────────────────────────────────────────────────────────────

    /// <summary>The operator's <c>RabbitmqCluster</c> — the one object a cluster <i>is</i>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>RabbitmqCluster</c> — capital <c>R</c>, lower-case <c>mq</c>.</b> Not
    ///         <c>RabbitMQCluster</c>, which is how the product is written everywhere else including
    ///         this type's own <c>Display</c> name. A <c>kind</c> the API server does not know is a
    ///         <c>404</c> at apply time, per object, and the two spellings differ by two characters
    ///         nobody reads. Verified against the CRD's <c>spec.names.kind</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived, and
    ///         <c>rabbitmqclusters</c> is what the CRD's <c>spec.names.plural</c> says. There is
    ///         exactly one version — <c>v1beta1</c>, served and stored, with no <c>deprecated</c>
    ///         flag — so <c>charts/managed/rabbitmq/SOURCE</c>'s <c>upstream-api</c> row has one
    ///         value and no migration to plan for.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() {
            Group = "rabbitmq.com", Version = "v1beta1", Kind = "RabbitmqCluster", Plural = "rabbitmqclusters"
        };

    // ── Ports and names the operator owns ─────────────────────────────────────────────────────

    /// <summary>The AMQP 0-9-1 / 1.0 port. What an <c>amqp://</c> URL connects to.</summary>
    public const int AmqpPort = 5672;

    /// <summary>The management UI and HTTP API port.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/12 puts this behind <i>"the portal's authenticated proxy rather than a
    ///     public route"</i>, and the operator's own Service carries it next to AMQP
    ///     unconditionally.</b> That single fact is why this type declares no external listener at
    ///     all — see <c>charts/managed/rabbitmq/conformance.yaml § owed</c>,
    ///     <c>external-exposure-moves-three-ports</c>.
    /// </remarks>
    public const int ManagementPort = 15672;

    /// <summary>The Prometheus endpoint <c>rabbitmq_prometheus</c> serves <c>/metrics</c> on.</summary>
    /// <remarks>
    ///     ⚠ <b>There is no switch for it, which is why this type declares no <c>monitoring</c>
    ///     block.</b> The operator's <c>requiredPlugins</c> list is
    ///     <c>rabbitmq_peer_discovery_k8s</c>, <c>rabbitmq_prometheus</c> and
    ///     <c>rabbitmq_management</c>, enabled on every cluster and not disableable through the CRD.
    ///     Declaring <c>monitoring.enabled</c> would be a property whose <see langword="false" />
    ///     branch cannot be honoured — see <see cref="Schema2026" />.
    /// </remarks>
    public const int PrometheusPort = 15692;

    /// <summary>The port the nodes' Erlang distribution and <c>epmd</c> traffic uses.</summary>
    /// <remarks>⚠ Headless-Service only. Never on a routable address, for the reason the NATS route port is not.</remarks>
    public const int ClusterRpcPort = 25672;

    /// <summary>The client <c>Service</c> the operator creates. ⚠ It takes the resource's own name, with no suffix.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Read off the operator rather than guessed.</b> Its <c>ServiceSuffix</c> is the empty
    ///     string and <c>ChildResourceName</c> trims the trailing hyphen, so the client Service is
    ///     named exactly like the custom resource. A suffix invented here would make
    ///     <see cref="ClientUrl" /> hand out a hostname that resolves to nothing.
    /// </remarks>
    public static string ClientServiceName(string name) => name;

    /// <summary>The headless <c>Service</c> the operator creates for peer discovery.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string HeadlessServiceName(string name) => name + "-nodes";

    /// <summary>The <c>Secret</c> the operator writes the generated credentials into.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>This is the answer to "what does the operator do about the <c>guest</c> user", and it
    ///     is the reason this row's <c>listKeys</c> gap costs LESS than the NATS one.</b> RabbitMQ
    ///     ships with <c>guest</c>/<c>guest</c>, and a cluster this operator creates <b>never has a
    ///     <c>guest</c> user at all</b>: it generates a random username
    ///     (<c>default_user_</c> + 24 random bytes, base64) and a random 24-byte password, writes both
    ///     into this <c>Secret</c>, and mounts them as <c>/etc/rabbitmq/conf.d/11-default_user.conf</c>
    ///     — which the broker reads on <i>first boot</i> to seed its one user. Because that user is
    ///     not named <c>guest</c>, RabbitMQ's own <c>loopback_users.guest = true</c> never applies to
    ///     it and <c>guest</c> is never created to be restricted.
    ///     <para>
    ///         ⚠ <b>So the gap is Kafka-shaped, not NATS-shaped, and the distinction is worth having
    ///         in one place.</b> <c>charts/managed/nats/conformance.yaml § owed</c> records that
    ///         <c>nats-server</c> with no <c>authorization</c> block <i>accepts every connection in
    ///         the namespace</i>, so that service comes up open. This one comes up
    ///         <b>authenticated</b>, with a 24-byte password nobody chose, sitting in a namespaced
    ///         <c>Secret</c>. What the platform cannot do is <i>hand it out</i>: <c>listKeys</c> has a
    ///         declared response and no handler, and <c>ISecretResolver</c> has only a refusing
    ///         implementation. The tenant's cluster is safe; the tenant cannot use it through this
    ///         API. That is a visible gap rather than an open broker.
    ///     </para>
    ///     <para>
    ///         ⚠ The operator also offers <c>spec.secretBackend.vault</c> and
    ///         <c>spec.secretBackend.externalSecret</c>. Neither is rendered, because
    ///         <c>CyberCloud.Vault</c> does not exist and pointing <c>secretBackend.vault.role</c> at
    ///         nothing is the spotahome failure — a resource that never comes up — where doing
    ///         nothing here is a resource that comes up with a credential the platform simply cannot
    ///         read yet.
    ///     </para>
    /// </remarks>
    public static string DefaultUserSecretName(string name) => name + "-default-user";

    /// <summary>The <c>RabbitmqCluster</c> a cluster owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = name };

    /// <summary>The in-cluster AMQP URL <c>listKeys</c> would hand out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string ClientUrl(string ns, string name) =>
        "amqp://"
        + ClientServiceName(name)
        + "."
        + ns
        + ".svc:"
        + AmqpPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The in-cluster management URL the portal's authenticated proxy would front.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string ManagementUrl(string ns, string name) =>
        "http://"
        + ClientServiceName(name)
        + "."
        + ns
        + ".svc:"
        + ManagementPort.ToString(CultureInfo.InvariantCulture);

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied.</b> <c>QuantityParserTests</c>
    ///     fails if a fresh copy of the grammar or a second suffix table appears — the last provider
    ///     that kept its own copy got a second <c>double</c>-based <i>parser</i> written beside it,
    ///     and consolidating the four found a live defect where a one-byte limit floored to
    ///     <c>maxmemory 0</c>, which means unlimited. There is no rule-2 problem in reaching for it:
    ///     <see cref="KubeQuantity" /> lives in <c>CyberCloud.ResourceManager.Contracts</c>, which
    ///     every provider may reference. This is the fifth declaration and the fifth reference.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>c1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own table, not <see cref="KafkaClusters.Presets" /> and not
    ///     <see cref="NatsClusters.Presets" />, for the reason the NATS one gives</b> — sharing them
    ///     would make a change to one service's sizing a silent change to two others, and the three
    ///     rows have different reasons to move. <c>RabbitmqSizingTests</c> asserts this table against
    ///     the chart's <c>_helpers.tpl</c> and <c>RabbitmqDeclarationTests</c> asserts the ratio is
    ///     1:2 at every rung, which is what <c>c1</c> means.
    ///     <para>
    ///         ⚠ <b>The <c>c1</c> family, and here the table is doing more work than it does on the
    ///         other two rows.</b> The CRD's own <c>default:</c> for <c>spec.resources</c> is
    ///         <c>{limits: {cpu: 2000m, memory: 2Gi}, requests: {cpu: 1000m, memory: 2Gi}}</c> —
    ///         requests below limits, which is Kubernetes' <b>Burstable</b> QoS class. docs/plan/12
    ///         § Sizing vocabulary calls <c>c1</c> <i>guaranteed</i>. So a body that rendered no
    ///         <c>resources</c> block would not get "the scheduler's defaults", it would get a
    ///         <i>different QoS class from the one the preset name promises</i>, silently, from the
    ///         CRD. See <see cref="ClusterJson" />.
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

    /// <summary>The plugins a tenant may switch on, beyond the three the operator always enables.</summary>
    /// <remarks>
    ///     ⚠ <b>A closed list rather than free text, and the reason is the Service.</b>
    ///     <c>spec.rabbitmq.additionalPlugins</c> takes any string, and the operator adds a port to
    ///     the client <c>Service</c> for each plugin it recognises. A plugin name with a typo is
    ///     enabled-and-absent: the broker logs it, the port is not added, and the tenant sees a
    ///     cluster that is healthy and does not speak the protocol they asked for. An <c>@enum</c> is
    ///     the one array constraint the chart vocabulary <i>can</i> carry — it becomes
    ///     <c>items.enum</c> — which is the same shape <c>@pattern</c> is refused in, and the
    ///     contrast is recorded at <c>NatsClusters.CidrPattern</c>.
    /// </remarks>
    public static ImmutableArray<string> AdditionalPlugins { get; } = [
        "rabbitmq_federation",
        "rabbitmq_mqtt",
        "rabbitmq_shovel",
        "rabbitmq_stomp",
        "rabbitmq_stream",
        "rabbitmq_web_mqtt",
        "rabbitmq_web_stomp"
    ];

    /// <summary>The queue types <c>default_queue_type</c> accepts.</summary>
    /// <remarks>
    ///     ⚠ <c>classic</c> is offered and is <b>not</b> the default. On 4.x a classic queue is
    ///     unreplicated, so choosing it is choosing a single-node queue on a multi-node cluster; that
    ///     is a legitimate choice for a transient work queue and a catastrophic default. See this
    ///     type's remarks.
    /// </remarks>
    public static ImmutableArray<string> QueueTypes { get; } = ["classic", "quorum", "stream"];

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///     <c>@default</c> directive — charts/README.md § The annotation format — because the chart's
    ///     default <i>is</i> the YAML literal on the annotated line, and <c>ChartAnnotationEmitter</c>
    ///     writes that literal from <see cref="SchemaProperty.DefaultJson" />.
    ///     <para>
    ///         ⚠ <b>THREE BLOCKS THE OTHER TWO TYPES IN THIS NAMESPACE HAVE AND THIS ONE DOES NOT,
    ///         each declined with a reason rather than forgotten.</b>
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>No <c>monitoring</c>.</b> <c>rabbitmq_prometheus</c> is in the operator's
    ///             <c>requiredPlugins</c> and cannot be turned off through the CRD, so
    ///             <c>monitoring.enabled: false</c> would be a property whose value the platform
    ///             cannot honour. What <i>is</i> missing is the scrape object, and that is owed
    ///             rather than renamed into a switch — see <see cref="PrometheusPort" />.
    ///         </item>
    ///         <item>
    ///             <b>No <c>external</c>.</b> ⚠ THE ONE PLACE THIS ROW CONTRADICTS docs/plan/12
    ///             § Cross-cutting decisions, and it is that document contradicting itself rather
    ///             than this type declining a requirement. That section gives every service
    ///             <i>"optional external exposure via a Kube-OVN floating IP plus a firewall
    ///             allow-list"</i>; the RabbitMQ row four pages earlier says the management UI is
    ///             <i>"exposed through the portal's authenticated proxy rather than a public
    ///             route"</i>. The operator's client <c>Service</c> carries AMQP <b>5672</b>,
    ///             management <b>15672</b> and Prometheus <b>15692</b> together, and
    ///             <c>spec.service.type</c> is one enum over the whole Service — there is no
    ///             per-port switch. So <c>external.enabled: true</c> would put a RabbitMQ management
    ///             UI and a metrics endpoint on a public IP as a side effect of asking for AMQP.
    ///             The full argument and the two ways out are at
    ///             <c>charts/managed/rabbitmq/conformance.yaml § owed</c>.
    ///         </item>
    ///         <item>
    ///             <b>No <c>tls</c>.</b> <c>spec.tls.secretName</c> names a <c>Secret</c> holding a
    ///             certificate, and nothing in this platform writes one. Rendering the reference
    ///             anyway is the spotahome shape — a cluster that never starts — and here there is a
    ///             working alternative, because the pod network is not the public internet and
    ///             external exposure is declined above.
    ///         </item>
    ///     </list>
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
                    Description: "The cluster whose namespace holds the RabbitmqCluster."
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
                    Description: "RabbitMQ version. Minor upgrades are applied automatically in the "
                    + "maintenance window; a major upgrade is an explicit update to this field. Only "
                    + "4.x is offered: classic queue mirroring was removed in 4.0, so on every "
                    + "version here the replicated queue type is the quorum queue."
                ) {
                    AllowedValues = ["4.0", "4.1"],
                    DefaultJson = "\"4.1\""
                },
                new(
                    "/properties/nodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of RabbitMQ nodes. Use an odd number of at least three: a "
                    + "quorum queue is a Raft group, so a group of two tolerates no failures and an "
                    + "even count buys nothing over the odd count below it. One is offered for "
                    + "development only and replicates nothing."
                ) {
                    Minimum = 1,
                    Maximum = 7,
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
                    Description: "The message store, per node."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Message-store volume size per node, in Kubernetes quantity form. "
                    + "Grows online; never shrinks. A quorum queue keeps its whole Raft log on every "
                    + "member, so this is the same figure on every node rather than a share of one."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"20Gi\"",
                    ExampleJson = "\"20Gi\""
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
                    "/properties/queues",
                    SchemaKind.Nested,
                    Description: "How queues behave when a client does not say."
                ),
                new(
                    DefaultQueueTypePointer,
                    SchemaKind.Text,
                    Description: "The queue type a client gets when it declares a queue without "
                    + "asking for one. Quorum by default — docs/plan/12: a quorum queue is "
                    + "replicated through Raft across the nodes above, and on 4.x a classic queue is "
                    + "not replicated at all, so leaving this unset would put a single-node queue on "
                    + "a cluster the tenant paid three nodes for. This is a node-wide fallback: a "
                    + "vhost created with its own default queue type overrides it."
                ) {
                    AllowedValues = [.. QueueTypes],
                    DefaultJson = "\"quorum\""
                },
                new(
                    "/properties/limits",
                    SchemaKind.Nested,
                    Description: "Broker limits a client can run into."
                ),
                new(
                    "/properties/limits/maxMessageSize",
                    SchemaKind.WholeNumber,
                    Description: "Largest message a client may publish, in bytes. Raising it costs "
                    + "memory on every node that holds a copy, which for a quorum queue is all of "
                    + "them."
                ) {
                    Minimum = 65536,
                    Maximum = 536870912,
                    DefaultJson = "134217728"
                },
                new(
                    "/properties/plugins",
                    SchemaKind.Nested,
                    Description: "Protocols and features beyond AMQP."
                ),
                new(
                    "/properties/plugins/additional",
                    SchemaKind.Array,
                    Description: "Plugins to enable on top of the three the operator always enables "
                    + "— peer discovery, the management UI and the Prometheus endpoint. Each "
                    + "recognised plugin adds its own port to the cluster's in-cluster Service."
                ) {
                    ElementKind = SchemaKind.Text,
                    AllowedValues = [.. AdditionalPlugins],
                    DefaultJson = "[]",
                    ExampleJson = "[\"rabbitmq_stream\"]"
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
    ///     <para>
    ///         ⚠ <b>Every field here already exists in the cluster, which is not true of the NATS
    ///         row.</b> The operator has written all four into
    ///         <see cref="DefaultUserSecretName" /> before the resource reaches Succeeded — it even
    ///         publishes a <c>connection_string</c> key of its own. So this response is a read the
    ///         platform cannot perform rather than a credential nobody has issued, and the handler
    ///         that closes it is a <c>Secret</c> read plus <c>ISecretResolver</c> rather than a
    ///         provisioning flow.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/url",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster AMQP URL, amqp://host:port. ⚠ There is no external "
                    + "address to return — this type declares no external listener at all, for the "
                    + "reason its schema gives."
                ),
                new(
                    "/managementUrl",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster management UI and HTTP API, http://host:port. "
                    + "docs/plan/12 fronts this with the portal's authenticated proxy; it is never a "
                    + "public route."
                ),
                new("/user", SchemaKind.Text, Required: true, Description: "The generated broker user."),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The generated user's password, read from the cluster's default-user "
                    + "Secret for this call only. ⚠ Neither this value nor the user name is chosen by "
                    + "the platform: the operator generates both at first boot and RabbitMQ's own "
                    + "guest/guest account is never created."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The RabbitMQ version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>The node count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Nodes(JsonElement desired) => Number(desired, "nodes", DefaultNodes);

    /// <summary>The message-store volume size per node a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>The default queue type a body asks for. ⚠ The reason this row exists.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string DefaultQueueType(JsonElement desired) =>
        Text(desired, "queues", "defaultType", DefaultQueueTypeValue);

    /// <summary>The plugins a body asks for, in the order the schema declares them.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The requested plugins, de-duplicated and sorted. ⚠ <b>Sorted rather than as written</b>,
    ///     which the CIDR allow-lists on the sibling types deliberately are not. A source range list
    ///     is a firewall rule whose order a reader reasons about; a plugin list is a set, and the
    ///     rendered document has to be a pure function of the body for clause 1 — two bodies that ask
    ///     for the same set in different orders must apply to the same object, or every alternating
    ///     pass is a write.
    /// </returns>
    public static ImmutableArray<string> Plugins(JsonElement desired) {
        if (Member(desired, "plugins", "additional") is not { ValueKind: JsonValueKind.Array } array) {
            return [];
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray()) {
            if (element.ValueKind is JsonValueKind.String && element.GetString() is { Length: > 0 } text) {
                found.Add(text);
            }
        }

        return [.. found];
    }

    /// <summary>
    ///     The CPU and memory a body asks for: the explicit quantities when both are given, otherwise
    ///     the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them.
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
    ///     The <c>spec.rabbitmq.additionalConfig</c> a desired body becomes — the <c>rabbitmq.conf</c>
    ///     fragment the operator files as <c>90-userDefinedConfiguration.conf</c>.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>INI lines, LF-terminated, deterministic for a given body.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS STRING IS WHERE docs/plan/12's HEADLINE SENTENCE ACTUALLY LANDS.</b>
    ///         <c>default_queue_type</c> has no field on the CRD, so "quorum queues by default" is a
    ///         line of free text inside a spec property. That has three consequences worth writing
    ///         down before somebody treats this like a typed field.
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <b>Nothing validates it.</b> The CRD's only constraint on
    ///             <c>additionalConfig</c> is <c>maxLength: 100000</c>. A misspelled key is not
    ///             rejected — the broker logs it and starts. <see cref="Schema2026" />'s
    ///             <c>AllowedValues</c> on <see cref="DefaultQueueTypePointer" /> is the only thing
    ///             standing between a body and an unreplicated cluster, and it constrains the value
    ///             rather than the key.
    ///         </item>
    ///         <item>
    ///             <b>The operator parses it, so it must be valid INI.</b> It reads this block
    ///             looking for <c>default_user</c>, <c>default_pass</c> and <c>auth_mechanisms</c>;
    ///             a block it cannot parse fails the reconcile rather than being ignored. Hence
    ///             <c>key = value</c> with spaces around the equals, which is what upstream's own
    ///             <c>rabbitmq.conf.example</c> writes.
    ///         </item>
    ///         <item>
    ///             <b>It layers ON TOP of the operator's own defaults rather than replacing them,
    ///             and it does not do so by string concatenation.</b> ⚠ Checked against the
    ///             operator, because this is exactly the <c>spotahome</c>-prepends-to-<c>customConfig</c>
    ///             hazard <c>ValkeyCaches</c> found and it is <b>absent here</b>: the operator writes
    ///             <c>10-operatorDefaults.conf</c> and this block as
    ///             <c>90-userDefinedConfiguration.conf</c>, two files in <c>conf.d</c> that RabbitMQ
    ///             merges by filename order. So this string round-trips through <c>spec</c>
    ///             <b>verbatim</b> — which is what makes <see cref="Matches" /> able to compare it —
    ///             and it wins over the operator's defaults at the broker because 90 sorts after 10.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b><c>default_user</c> and <c>default_pass</c> are deliberately NOT written.</b>
    ///         Writing them here would put a plaintext password in the resource's desired body and in
    ///         grain state, which docs/plan/05 forbids outright, and it would take the credential out
    ///         of the operator's hands for no gain. See <see cref="DefaultUserSecretName" />.
    ///     </para>
    /// </remarks>
    public static string AdditionalConfig(JsonElement desired) {
        var built = new StringBuilder(128);

        // ⚠ Alphabetical by key, and it is a rule rather than a habit: clause 1 wants the same body
        // to render the same string on every pass, and a reader diffing two clusters wants the same
        // key on the same line. A block assembled in "whatever order the properties were added"
        // makes both harder the first time a property is inserted in the middle.
        built.Append("default_queue_type = ").Append(DefaultQueueType(desired)).Append('\n');
        built.Append("max_message_size = ")
            .Append(Text(Number(desired, "limits", "maxMessageSize", DefaultMaxMessageSize)))
            .Append('\n');

        return built.ToString();
    }

    /// <summary>The <c>RabbitmqCluster</c> document a desired body becomes, ready for server-side apply.</summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The JSON <c>templates/rabbitmqcluster.yaml</c> renders, for the same values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spec.image</c> IS WRITTEN, and on the other two types in this namespace the
    ///         equivalent is left to the operator.</b> The reason is a mutating admission webhook:
    ///         this operator ships one, it is on unless <c>ENABLE_WEBHOOKS=false</c>, and it fills
    ///         <c>spec.image</c> from the operator's own build-time default when the field is unset.
    ///         So "leave it out and let the operator choose" is not "the tenant's version wins" — it
    ///         is <b>whatever RabbitMQ the operator was compiled against</b>, which is a version
    ///         <c>/properties/version</c> claims to control and does not. Writing it is what makes
    ///         the property mean what it says, and it is what lets <see cref="Matches" /> read the
    ///         version back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spec.resources</c> IS WRITTEN WHENEVER IT CAN BE, AND OMITTING IT IS NOT
    ///         NEUTRAL HERE.</b> On the Kafka and NATS rows an unresolvable preset renders no
    ///         <c>resources</c> block and the workload gets no requests or limits at all — visible,
    ///         and BestEffort. This CRD <i>defaults</i> the field to
    ///         <c>{limits: {cpu: 2000m, memory: 2Gi}, requests: {cpu: 1000m, memory: 2Gi}}</c>, so
    ///         the same omission here produces a <b>Burstable</b> pod at quantities nobody chose
    ///         while the preset name still says <c>c1</c>, which docs/plan/12 defines as guaranteed.
    ///         The quota meters refuse a body whose preset does not resolve before a reconcile ever
    ///         runs — see <c>MessagingProvider</c> — so the branch is unreachable through the write
    ///         path; it is recorded because the CRD default is the kind of fact that turns a harmless
    ///         omission into a silent substitution.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Requests equal limits.</b> The <c>c1</c> family is "guaranteed" in docs/plan/12
    ///         § Sizing vocabulary, and guaranteed is a Kubernetes QoS class you get by setting them
    ///         equal — not a word in a table.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.service</c>, no <c>spec.override</c> and no <c>spec.tls</c>.</b> The
    ///         CRD defaults <c>spec.service</c> to <c>{type: ClusterIP}</c>, which is the value this
    ///         type wants and would have written; declaring it anyway would put this field manager in
    ///         permanent ownership of a field under server-side apply, and the one thing a tenant
    ///         might legitimately want there is a load-balancer type this row declines to offer for
    ///         the reason <see cref="Schema2026" /> gives.
    ///     </para>
    /// </remarks>
    public static string ClusterJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var rabbitmq = new JsonObject { ["additionalConfig"] = AdditionalConfig(desired) };

        var plugins = Plugins(desired);
        if (plugins.Length > 0) {
            var listed = new JsonArray();
            foreach (var plugin in plugins) {
                listed.Add(plugin);
            }

            rabbitmq["additionalPlugins"] = listed;
        }

        var spec = new JsonObject {
            ["replicas"] = Nodes(desired),
            ["image"] = Image(desired),
            // ⚠ A string, not a number. The CRD marks `spec.persistence.storage` int-or-string, so
            // `20` and `"20Gi"` are both accepted and mean wildly different things — and a number
            // does not round-trip as the string `Matches` compares. The schema's Pattern makes the
            // body's value a quantity string; this keeps it one.
            ["persistence"] = new JsonObject { ["storage"] = StorageSize(desired) },
            ["rabbitmq"] = rabbitmq
        };

        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            ((JsonObject)spec["persistence"]!)["storageClassName"] = storageClass;
        }

        var (cpu, memory) = Resources(desired);
        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            spec["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["spec"] = spec }
            .ToJsonString();
    }

    /// <summary>The container image a body's version asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ The <c>-management</c> tag rather than the bare one. The operator enables
    ///     <c>rabbitmq_management</c> on every cluster as a required plugin, and the plain
    ///     <c>rabbitmq:{version}</c> image ships the plugin but not enabled; using it would make the
    ///     first boot enable a plugin on every node instead of starting.
    /// </remarks>
    public static string Image(JsonElement desired) => "rabbitmq:" + Version(desired) + "-management";

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns><c>true</c> when the fields this provider owns hold the desired values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, not equality — and unlike <c>KafkaClusters.Matches</c>, the ORDINARY
    ///         reason is the true one here. Checked against the CRD rather than against the
    ///         operator's README, which is where the two answers differ.</b> That type's remarks
    ///         record that Strimzi's <c>Kafka</c> at <c>v1beta2</c> declares <b>no <c>default:</c>
    ///         anywhere</b>, so "the API server defaults fields on write" was false for it and
    ///         containment was kept for other reasons. <c>rabbitmq.com_rabbitmqclusters.yaml</c> at
    ///         <c>v1beta1</c> declares defaults on <c>spec.replicas</c> (<c>1</c>),
    ///         <c>spec.persistence</c> (<c>{storage: 10Gi}</c>), <c>spec.service</c>
    ///         (<c>{type: ClusterIP}</c>), <c>spec.resources</c>,
    ///         <c>spec.terminationGracePeriodSeconds</c> (<c>604800</c>) and
    ///         <c>spec.delayStartSeconds</c> (<c>30</c>). ⚠ Two of those are <b>object-level</b>
    ///         defaults: omitting <c>spec.persistence</c> entirely still materialises
    ///         <c>{storage: 10Gi}</c>. Equality would report drift on the first read of every
    ///         resource, forever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a second mechanism the other two types do not meet at all: a MUTATING
    ///         WEBHOOK.</b> The operator registers one at
    ///         <c>/mutate-rabbitmq-com-v1beta1-rabbitmqcluster</c> with <c>failurePolicy: Fail</c>,
    ///         and it writes <c>spec.image</c>, <c>spec.imagePullSecrets</c> and — under Vault —
    ///         <c>spec.secretBackend.vault.defaultUserUpdaterImage</c>. So <c>spec</c> gains fields
    ///         between the apply and the read that no <c>default:</c> explains. The controller also
    ///         writes three annotations back onto <c>metadata</c>
    ///         (<c>rabbitmq.com/version</c>, <c>rabbitmq.com/erlang-version</c>,
    ///         <c>rabbitmq.com/queueRebalanceNeededAt</c>) and adds a finalizer.
    ///     </para>
    ///     <para>
    ///         The reasons the earlier providers give also hold: server-side apply leaves other
    ///         managers' fields in place, and the document carries <c>metadata</c>,
    ///         <c>managedFields</c> and an operator-written <c>status</c> this provider never sent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is compared, and one thing that deliberately is not.</b> The four fields
    ///         below are the ones a body decides and the operator does not touch. ⚠
    ///         <c>spec.replicas</c> is compared and <b>reading it back proves less than it does on
    ///         the sibling types</b>: this operator accepts a scale-DOWN at the API server, then
    ///         refuses to perform it, recording the refusal only in
    ///         <c>status.conditions[ReconcileSuccess]</c> with an <c>UnsupportedOperation</c> event.
    ///         So a shrink reads back as converged while the StatefulSet keeps its old node count.
    ///         That is a real gap and it is written down at
    ///         <c>charts/managed/rabbitmq/conformance.yaml § owed</c> as
    ///         <c>scale-down-is-accepted-and-ignored</c> rather than papered over with a status read
    ///         no test in this repository could produce.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on nothing, because this type owns exactly one kind — but it still takes
    ///         the object's whole JSON rather than its spec, because a conformance case supplies this
    ///         as one function over every object the resource owns and a second kind would be added
    ///         here rather than at the call site.
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

        if (spec["replicas"]?.GetValue<int>() != Nodes(desired)) {
            return false;
        }

        if (spec["image"]?.GetValue<string>() != Image(desired)) {
            return false;
        }

        if ((spec["persistence"] as JsonObject)?["storage"]?.GetValue<string>() != StorageSize(desired)) {
            return false;
        }

        // ⚠ CONTAINS, not equals, and this one line is the whole difference between reading the
        // config back and reading OUR config back. The operator does not edit this string — it files
        // it as its own conf.d fragment — but a tenant's own controller under server-side apply, or
        // a future property of this type, can legitimately add a line. What must be true is that the
        // line this row exists for is IN there.
        return (spec["rabbitmq"] as JsonObject)?["additionalConfig"]?.GetValue<string>() is { } config
            && config.Contains(QueueTypeLine(desired), StringComparison.Ordinal);
    }

    /// <summary>The one configuration line docs/plan/12's RabbitMQ row is about.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string QueueTypeLine(JsonElement desired) =>
        "default_queue_type = " + DefaultQueueType(desired);

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the broker cluster in.</param>
    /// <param name="nodes">How many RabbitMQ nodes.</param>
    /// <param name="storageSize">The message-store volume size per node.</param>
    /// <param name="defaultQueueType">The default queue type.</param>
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
        string storageSize = "20Gi",
        string defaultQueueType = "quorum",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["nodes"] = nodes,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["queues"] = new JsonObject { ["defaultType"] = defaultQueueType },
                ["limits"] = new JsonObject { ["maxMessageSize"] = DefaultMaxMessageSize }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.
    //
    // ⚠ AND ON THIS TYPE THE STAKES ARE HIGHER THAN ON THE OTHER TWO. `DefaultQueueTypeValue` is the
    // fallback for a property whose absence means "not replicated" at the broker. If this constant
    // and `/properties/queues/defaultType`'s `DefaultJson` ever disagreed, a body that omitted the
    // property would render a cluster that contradicts the value the API told the caller it had —
    // and RabbitmqDeclarationTests walks every declared default back into a body to catch exactly
    // that class of disagreement.

    const string DefaultVersion = "4.1";
    const string DefaultPreset = "c1.small";
    const string DefaultStorageSize = "20Gi";
    const string DefaultQueueTypeValue = "quorum";
    const int DefaultNodes = 3;
    const int DefaultMaxMessageSize = 134217728;

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
}
