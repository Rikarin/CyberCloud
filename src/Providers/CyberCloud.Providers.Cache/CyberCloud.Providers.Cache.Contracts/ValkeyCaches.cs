using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Cache/redis</c>: the type, its api-version, its body
///     shape, and the spotahome <c>RedisFailover</c> it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue, <i>"Valkey — <c>CyberCloud.Cache/redis</c> · M1 · 1.0 EM"</i>,
///         on <c>spotahome/redis-operator</c> (ADR-010 clause 1, which names the operator per managed
///         service, and ADR-011, which rejects Redis ≥ 7.4 as RSALv2/SSPL and takes Valkey instead).
///     </para>
///     <para>
///         ⚠ <b>The type path is <c>redis</c> and the product is <b>Valkey</b>, and that is deliberate
///         rather than a leftover.</b> docs/plan/01 § Azure parity maps this row onto
///         <c>Microsoft.Cache/redis</c>, and a resource path is the one part of the surface a tenant's
///         existing scripts and ARM-shaped tooling address by string. ADR-011's rule is about the
///         <i>product</i>: <i>"API-compatible; say Valkey on the product page"</i>. So every display
///         name, description, chart and image below says Valkey, and the path does not — because
///         renaming the path would buy a truer noun at the price of the parity the row exists for.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair and
///         <c>charts/managed/valkey/values.yaml</c> is the generated one.</b> ADR-010 § Which end
///         authors the schema, DECIDED 2026-08-11: the C# <c>ResourceSchema</c> is authored, the
///         chart's <c>@param</c> block is generated from it by <c>ChartAnnotationEmitter</c> and
///         byte-diffed by <c>./build.sh Charts</c>. Every property whose pointer begins
///         <c>/properties/</c> and is not <see cref="ClusterIdPointer" /> has a row in that file;
///         <c>/location</c> is root-level and <see cref="ClusterIdPointer" /> is placement, and both
///         are excluded by the emitter rather than by anyone remembering.
///     </para>
///     <para>
///         ⚠ <b>Three things docs/plan/12 names for this row are NOT declared, because the operator
///         ADR-010 clause 1 selects cannot do them.</b> Each was checked against
///         <c>spotahome/redis-operator</c>'s own source rather than against its README, and each is
///         recorded on the property it would have been:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>Cluster</c> mode.</b> There is no sharded mode in that operator at all — its one
///             custom resource is a <c>RedisFailover</c>, which is a primary, its replicas and a
///             Sentinel quorum. See <see cref="ModeValues" />.
///         </item>
///         <item>
///             <b><c>Standalone</c> mode.</b> A <c>RedisFailover</c> always deploys Sentinel:
///             <c>api/redisfailover/v1/validate.go</c> replaces a sentinel replica count of zero or
///             less with <c>defaultSentinelNumber</c>, which is 3, so "no Sentinel" is not a state the
///             CRD can express. See <see cref="ModeValues" />.
///         </item>
///         <item>
///             <b>TLS.</b> docs/plan/12 says <i>"TLS on by default"</i>. Neither
///             <c>RedisSettings</c> nor <c>SentinelSettings</c> carries a TLS field, so a
///             <c>tls.enabled</c> property would be a boolean the reconciler could accept and not
///             honour — the failure mode this platform's own registry notes call "a constraint the
///             author believes is enforced and that validates nothing at all".
///             <c>charts/managed/valkey/conformance.yaml</c> carries it as an owed assertion.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>No property here is <see cref="SchemaProperty.Secret" />, and the <c>requirepass</c>
///         reaches the data plane the same way Postgres's does — as a reference by name.</b> Nothing on
///         the write path swaps a secret value for a <c>SecretRef</c> before the grain writes
///         desired state (<see cref="SchemaProperty" />'s own remarks say so), so a declared secret
///         would be a plaintext password in durable state. <c>spec.auth.secretPath</c> is the seam:
///         the operator reads the password out of a <c>Secret</c> in the namespace, so the CR carries a
///         name and never a value. See <see cref="RedisFailoverJson" /> for what that costs today.
///     </para>
/// </remarks>
public static class ValkeyCaches {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.Cache";

    /// <summary>The one resource type. ⚠ The Azure-parity path — see the type's remarks.</summary>
    public const string TypePath = "redis";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/valkey/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>
    ///     The chart this type is the configuration surface of — docs/plan/12 § The pattern, once,
    ///     piece 1.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A registry declaration, not a render path.</b> <see cref="IResourceTypeBuilder.Chart" />
    ///     records which chart describes this type; <c>IKubeCommandBuilder.Chart</c> would ask
    ///     <c>CyberCloud.Kubernetes.Charts</c> to render one, and that assembly does not exist. So the
    ///     reconciler renders in C# the object the chart's template renders, and this constant is what
    ///     ties the two halves together until a renderer lands.
    /// </remarks>
    public const string ChartName = "managed/valkey";

    /// <summary>The field manager the apply runs under — ADR-013's stable per-provider name.</summary>
    public const string FieldManager = "cybercloud/cybercloud.cache";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the connection credentials.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions, Credentials. ⚠ <c>regenerateKeys</c> is named in the
    ///     same paragraph and is <b>not</b> declared, for the reason
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c> gives: it is specified with a rolling grace period
    ///     and nothing in the platform can hold two live credentials for one resource. Rotating a
    ///     <c>requirepass</c> is in fact the harder of the two — every connected client is
    ///     authenticated once at connect and a rotation drops them all — so honouring the contract here
    ///     needs more than the platform gap, not less.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     A key export is not a read: docs/plan/07 § Consistency puts it in the fully-consistent row by
    ///     name, and <c>ResourceManagerService</c> passes an action's <c>secret</c> flag into the
    ///     authorization call for exactly that reason. Sharing <c>read</c> would make every viewer of a
    ///     cache a holder of its password.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>spotahome's <c>RedisFailover</c> — the object a cache <i>is</i>.</summary>
    /// <remarks>
    ///     ⚠ <see cref="GroupVersionKind.Plural" /> is carried rather than derived, for the reason that
    ///     type's own remarks give — and the plural here is one the naive rule would get right and the
    ///     kind's own name would not: it is <c>redisfailovers</c>, taken from the CRD's
    ///     <c>spec.names.plural</c>. It must match <c>charts/managed/valkey/SOURCE</c>'s
    ///     <c>upstream-api: databases.spotahome.com/v1</c>.
    /// </remarks>
    public static GroupVersionKind FailoverKind { get; } =
        new() {
            Group = "databases.spotahome.com",
            Version = "v1",
            Kind = "RedisFailover",
            Plural = "redisfailovers"
        };

    // ── The two constraint vocabularies, now shared by code rather than by shape ──
    //
    // ⚠ THESE WERE A LOCAL COPY OF THE SAME TWO CONSTANTS CyberCloud.Providers.DBforPostgreSQL.Contracts
    // DECLARES, AND THE COPY COST THE PLATFORM A SECOND QUANTITY PARSER. The note that stood here said
    // the duplication was "recorded as the finding rather than resolved as a convenience" and that the
    // grammar eventually belonged next to LabelSyntax in CyberCloud.Kubernetes.Contracts. What happened
    // in the meantime is the argument against leaving it: an author who needed to turn one of these
    // strings into a number found the pattern here, in a provider assembly, and wrote QuantityBytes
    // beside it — a `double` parser for a grammar the platform already parsed exactly in `decimal`.
    //
    // So the grammar moved to KubeQuantity.Pattern instead, beside the parser rather than beside the
    // other syntax rules. Rule 2 is untouched: KubeQuantity is in CyberCloud.ResourceManager.Contracts,
    // which this project already references and every provider may. LabelSyntax's neighbourhood was the
    // tidier home and this one is the one that makes the next author land on the parser.
    //
    // ⚠ CORRECTED 2026-08-12. This said "PostgresServers and KafkaClusters still declare their own
    // copies … the duplication that produced the second parser survives in two places." They followed
    // the same day, along with TestProvider: all four declarations now alias this constant, and the
    // four strings were verified byte-identical first, so nothing had drifted and no emitted `pattern`
    // moved in openapi/, generated/ or values.schema.json. QuantityParserTests fails if a fifth copy
    // or a second suffix table appears — both halves sabotage-tested against a planted probe.

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>
    ///     The replication topologies this type offers. ⚠ One member, on purpose.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/12 names three — <c>Standalone</c>, <c>Sentinel</c> and <c>Cluster</c> — and
    ///         warns that <i>"these are not interchangeable and the API must not pretend they are"</i>.
    ///         Neither of the other two is expressible on the operator ADR-010 clause 1 selects, and
    ///         both facts come from that operator's source:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <c>Cluster</c> — <c>spotahome/redis-operator</c> ships one CRD,
    ///             <c>RedisFailover</c>, and it has no sharding. There is nothing to render.
    ///         </item>
    ///         <item>
    ///             <c>Standalone</c> — <c>validate.go</c> replaces a sentinel replica count of
    ///             <c>&lt;= 0</c> with <c>defaultSentinelNumber</c> (3), so every <c>RedisFailover</c>
    ///             runs a Sentinel quorum whatever the spec says. A <c>Standalone</c> member would be
    ///             a value the API accepts and the cluster silently ignores.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>The property exists anyway, and that is the decision worth reading.</b> Omitting it
    ///         would be tidier today and would cost a new api-version later: a mode is
    ///         <see cref="SchemaProperty.Immutable" /> identity, docs/plan/12 makes it API-visible on
    ///         purpose, and an api-version is immutable once published — so adding <c>mode</c> when a
    ///         second topology arrives is a new date for every caller, whereas adding a member to an
    ///         existing <see cref="SchemaProperty.AllowedValues" /> is <i>also</i> a new date
    ///         (<c>OpenApiCompatibility</c> refuses it) but leaves the property's meaning, its
    ///         immutability and its place in every generated client alone. One member is the API saying
    ///         "this axis exists and today it has one value", which is the truth; three members would
    ///         be the pretence that paragraph forbids.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> ModeValues { get; } = ["Sentinel"];

    /// <summary>
    ///     The eviction policies Valkey implements, as its <c>maxmemory-policy</c> spells them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>noeviction</c> is the default and the platform makes the same choice for its own hot
    ///     tier — <c>CyberCloud.Cluster.Conformance</c>'s Redis container sets it explicitly, with the
    ///     reason: <i>"a default is how one environment ends up with allkeys-lru and another with
    ///     noeviction"</i>. A cache that silently drops a key somebody was relying on is a correctness
    ///     bug in the caller's code that nothing in either system reports.
    /// </remarks>
    public static ImmutableArray<string> EvictionPolicies { get; } = [
        "noeviction",
        "allkeys-lru",
        "allkeys-lfu",
        "allkeys-random",
        "volatile-lru",
        "volatile-lfu",
        "volatile-random",
        "volatile-ttl"
    ];

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Seventeen properties: the fifteen API-facing <c>@param</c> rows of
    ///         <c>charts/managed/valkey/values.yaml</c>, plus <c>/properties</c> itself and the two
    ///         body-only properties — <c>/location</c> and <see cref="ClusterIdPointer" /> — that the
    ///         chart-annotation emitter excludes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON.</b> There is no
    ///         <c>@default</c> directive because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line — charts/README.md § The annotation format — and
    ///         <c>ChartAnnotationEmitter</c> writes that literal from
    ///         <see cref="SchemaProperty.DefaultJson" />.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the cache is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The cache's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the RedisFailover."
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
                    Description: "Major Valkey version. Minor upgrades are applied automatically in "
                    + "the maintenance window."
                ) {
                    AllowedValues = ["7", "8"],
                    DefaultJson = "\"8\""
                },
                new(
                    "/properties/mode",
                    SchemaKind.Text,
                    Description: "Replication topology. Sentinel is the only one this operator "
                    + "implements, and a client written against it is not portable to a sharded "
                    + "deployment, so it may not change after create."
                ) {
                    AllowedValues = ModeValues,
                    Immutable = true,
                    DefaultJson = "\"Sentinel\""
                },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of Valkey instances, including the primary. One is a single "
                    + "point of failure and is offered for development only."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory, either by preset or explicitly."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Caches use the m1 family, which "
                    + "is 1 vCPU to 8 GiB."
                ) {
                    AllowedValues = [
                        "m1.nano",
                        "m1.micro",
                        "m1.small",
                        "m1.medium",
                        "m1.large",
                        "m1.xlarge",
                        "m1.2xlarge",
                        "m1.4xlarge"
                    ],
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
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. Empty "
                    + "means take it from the preset. It also sets maxmemory, so a cache evicts or "
                    + "refuses before the kernel kills the pod."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/persistence",
                    SchemaKind.Nested,
                    Description: "What survives a restart. None of the three settings makes a cache a "
                    + "database."
                ),
                new(
                    "/properties/persistence/mode",
                    SchemaKind.Text,
                    Description: "None keeps nothing, RDB snapshots periodically, AOF appends every "
                    + "write."
                ) {
                    AllowedValues = ["None", "RDB", "AOF"],
                    DefaultJson = "\"AOF\""
                },
                new(
                    "/properties/persistence/fsync",
                    SchemaKind.Text,
                    Description: "How often the append-only file reaches the disk. Read only when the "
                    + "mode is AOF; everysec can lose the last second of writes."
                ) {
                    AllowedValues = ["always", "everysec", "no"],
                    DefaultJson = "\"everysec\""
                },
                new(
                    "/properties/persistence/size",
                    SchemaKind.Text,
                    Description: "Persistent volume size in Kubernetes quantity form. Unused when the "
                    + "mode is None, which keeps the data directory in memory."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"8Gi\"",
                    ExampleJson = "\"8Gi\""
                },
                new(
                    "/properties/persistence/class",
                    SchemaKind.Text,
                    Description: "StorageClass name. Empty means the cluster default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/maxmemoryPolicy",
                    SchemaKind.Text,
                    Description: "What happens when the cache reaches its memory limit. noeviction "
                    + "returns an error to the writer rather than dropping a key somebody is relying "
                    + "on."
                ) {
                    AllowedValues = EvictionPolicies,
                    DefaultJson = "\"noeviction\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the operator runs a metrics exporter beside every Valkey and "
                    + "every Sentinel pod."
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
    ///         ⚠ <b>Declared even though no handler serves it</b>, for the reason
    ///         <c>CyberCloud.Providers.Sample</c> gives about <c>ping</c>: an undeclared response is the
    ///         one part of an API surface with no contract, and what leaves the platform through a
    ///         <c>secret: true</c> action is exactly the thing to write down before it leaves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A Sentinel endpoint and a master group name, not a host and a port to a server.</b>
    ///         docs/plan/12's warning that the modes are not interchangeable is a warning about
    ///         <i>this</i> shape: a client connects to the Sentinel service, asks it which member is
    ///         the primary, and reconnects. Advertising the primary's own address would work until the
    ///         first failover and would then be a connection string pointing at a replica.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/host",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster DNS name of the Sentinel service. ⚠ Not the primary's "
                    + "own address — a client asks Sentinel which member is the primary, because that "
                    + "answer changes at every failover."
                ),
                new(
                    "/port",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "The Sentinel TCP port."
                ) {
                    Minimum = 1,
                    Maximum = 65535
                },
                new(
                    "/masterName",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The Sentinel master group to ask for. Every client library takes this "
                    + "as a separate argument from the host."
                ),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The requirepass value, read from the tenant's Vault for this call "
                    + "only."
                )
            ]
        );

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, m1 family.</summary>
    /// <remarks>
    ///     ⚠ <b>This table is a second copy of <c>charts/managed/valkey/templates/_helpers.tpl</c>'s
    ///     <c>valkey.resources</c>, and the duplication is the cost of having no chart renderer.</b>
    ///     <c>CyberCloud.Kubernetes.Charts</c> does not exist (docs/plan/03 § src), so the object is
    ///     built here; the moment it does, this table and the reconciler's use of it should go and the
    ///     chart's should stay, because the chart is the file a support engineer reads. Until then both
    ///     exist and <c>ChartRegistryPairTests</c> asserts they agree value for value.
    ///     <para>
    ///         ⚠ <c>m1</c> is 1 vCPU to 8 GiB — docs/plan/12 § Sizing vocabulary, <i>"m1.* · 1:8 ·
    ///         Memory-bound — caches, analytics"</i> — where <c>s1</c> is 1:4. <c>m1.nano</c> is the one
    ///         row off the ratio, at 1:10, exactly as <c>s1.nano</c> is: the smallest box needs a floor
    ///         rather than a ratio, because 800 MiB of Valkey plus an exporter does not fit in what
    ///         100m of CPU implies.
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

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── Addressing ────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>RedisFailover</c> a cache owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef FailoverRef(string ns, string name) =>
        new() { Kind = FailoverKind, Namespace = ns, Name = name };

    /// <summary>
    ///     The name of the <c>Secret</c> the operator reads <c>requirepass</c> from.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Matches <c>templates/redisfailover.yaml</c>'s <c>{{ include "valkey.name" . }}-auth</c>.
    ///     Two spellings of one object's name is a credential the operator looks for and never finds.
    /// </remarks>
    public static string CredentialSecretName(string name) => name + "-auth";

    /// <summary>
    ///     The Sentinel <c>Service</c> a client connects to — the operator's own naming.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>rfs-</c> is spotahome's, from <c>operator/redisfailover/service/constants.go</c>
    ///     (<c>baseName = "rf"</c>, <c>sentinelName = "s"</c>). It is not an object this provider
    ///     applies and it is not one this provider may rename; it is here because
    ///     <see cref="ListKeysResponse" /> advertises it and a connection string built from a guess is
    ///     a connection string that resolves to nothing.
    /// </remarks>
    public static string SentinelServiceName(string name) => "rfs-" + name;

    /// <summary>The Sentinel port. The protocol's own, not a choice.</summary>
    public const int SentinelPort = 26379;

    /// <summary>
    ///     The Sentinel master group. ⚠ Hard-coded by the operator, so it is a fact rather than a
    ///     setting: <c>sentinel monitor mymaster …</c> in its config template.
    /// </summary>
    public const string MasterGroup = "mymaster";

    /// <summary>The Sentinel quorum size the operator is given.</summary>
    /// <remarks>
    ///     ⚠ Constant rather than a chart row, and it is the smallest odd number that can hold a
    ///     majority opinion. It is not a tenant setting because a tenant choosing 2 would get a quorum
    ///     that cannot fail over and a tenant choosing 1 would get one that fails over on a network
    ///     blip. The operator's own default is the same 3 —
    ///     <c>defaultSentinelNumber</c> — and it is written explicitly rather than inherited, so that
    ///     <see cref="Matches" /> reads back a number this provider chose.
    /// </remarks>
    public const int SentinelReplicas = 3;

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The major version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "8"
            : "8";

    /// <summary>The container image a body implies.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>valkey/valkey</c> and not <c>redis</c>.</b> ADR-011 rejects Redis ≥ 7.4 as RSALv2 /
    ///     SSPL and the operator's own <c>defaultImage</c> is <c>redis:6.2.6-alpine</c>, so leaving the
    ///     image unset would ship the licence this platform decided against — from a default, silently.
    ///     The image is therefore written on every apply rather than omitted.
    /// </remarks>
    public static string Image(JsonElement desired) => "valkey/valkey:" + Version(desired) + "-alpine";

    /// <summary>The eviction policy a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string EvictionPolicy(JsonElement desired) =>
        Root(desired, "maxmemoryPolicy") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? "noeviction"
            : "noeviction";

    /// <summary>The persistence mode a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PersistenceMode(JsonElement desired) =>
        Text(desired, "persistence", "mode", "AOF");

    /// <summary>Whether the cache needs a persistent volume rather than an in-memory data directory.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool IsPersistent(JsonElement desired) =>
        !string.Equals(PersistenceMode(desired), "None", StringComparison.Ordinal);

    /// <summary>
    ///     The CPU and memory a body asks for: the explicit quantities when both are given, otherwise
    ///     the preset's.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     Both quantities, or both empty when neither the preset nor an override supplies them —
    ///     which renders no <c>resources</c> block at all rather than a half-specified one, matching
    ///     <c>_helpers.tpl</c>'s <c>if and $cpu $memory</c>.
    /// </returns>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", "m1.small");
        var fallback = Presets.TryGetValue(preset, out var found) ? found : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    /// <summary>
    ///     A Kubernetes memory quantity as whole bytes, or <see langword="null" /> when it is not one
    ///     or does not fit.
    /// </summary>
    /// <param name="quantity">A quantity matching <see cref="QuantityPattern" />, for example <c>4Gi</c>.</param>
    /// <remarks>
    ///     ⚠ <b>This is a rounding rule, not a parser.</b> <see cref="KubeQuantity.TryParse" /> is the
    ///     platform's one answer to "what number is this string", and it answers in
    ///     <see langword="decimal" /> because quota reserves fractional cores. Valkey needs something
    ///     else on top: <c>maxmemory</c> is a whole-byte ceiling, so the exact answer is floored here
    ///     and nowhere else. Keeping the two apart is the point — a provider that needs different
    ///     rounding writes a rule like this one, never a second parse.
    ///     <para>
    ///         ⚠ <b>This used to be that second parse.</b> It walked the same digits and the same
    ///         suffix table in <see langword="double" />, and the damage was not the accept set — the
    ///         two agreed on every string, including <c>""</c>, <c>-1</c>, <c>1e3</c> and <c>4gi</c> —
    ///         but the values. <c>8.7T</c> came back one byte short at 8699999999999, and <c>8Ei</c>
    ///         came back as <see cref="long.MaxValue" /> rather than refused, because
    ///         <c>(double)long.MaxValue</c> rounds up to one past it and the range check compared equal.
    ///     </para>
    ///     <para>
    ///         ⚠ The <c>m</c> suffix is milli — <c>500m</c> is half a byte — and it is parsed rather
    ///         than refused, because <see cref="QuantityPattern" /> permits it and a caller who writes
    ///         <c>512m</c> meaning mebibytes must not get a ceiling a thousand times too large.
    ///         <see cref="KubeQuantity" /> accepts <c>m</c> on a memory value for the same reason, so
    ///         consolidating changed nothing here; the floor turns it into 0, and 0 becomes "no
    ///         ceiling" through <see cref="MaxMemoryBytes" />.
    ///     </para>
    /// </remarks>
    public static long? QuantityBytes(string quantity) =>
        // No lower bound. The grammar has no sign and every multiplier is positive, so a parse that
        // succeeds cannot be negative — a `>= 0` guard here would read as if one could.
        KubeQuantity.TryParse(quantity, out var bytes) && bytes <= long.MaxValue
            ? (long)bytes
            : null;

    /// <summary>
    ///     The fraction of the container's memory the cache is allowed to hold.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not 1.0, and the missing quarter is not caution.</b> Valkey's own accounting covers the
    ///     dataset; a fork for an RDB snapshot or an AOF rewrite copies pages on write, the replication
    ///     backlog and client output buffers are outside <c>maxmemory</c>, and the exporter sidecar
    ///     shares the pod's limit. A cache set to exactly its container limit is a cache that is OOM
    ///     killed during its first background save, which looks to a tenant like an unexplained restart
    ///     under load.
    ///     <para>
    ///         ⚠ <see langword="decimal" /> rather than <see langword="double" />, so the byte count
    ///         this multiplies stays exact. Three quarters is exact in binary too, but the moment the
    ///         fraction changes to something that is not — 0.8, say — a <see langword="double" /> here
    ///         would put the ceiling a byte or two off a number the operator can read back off the pod,
    ///         and re-introduce the imprecision consolidating <see cref="QuantityBytes" /> removed.
    ///     </para>
    /// </remarks>
    public const decimal MaxMemoryFraction = 0.75m;

    /// <summary>
    ///     The <c>maxmemory</c> value for a body, in bytes, or <see langword="null" /> for no ceiling.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The zero test is on the ceiling, not on the byte count, and it used to be on the byte
    ///     count.</b> <c>maxmemory 0</c> means UNLIMITED to Valkey, which is the opposite of what a
    ///     caller with no usable memory figure should get, so the line is omitted instead. Testing the
    ///     byte count let one window through: a limit of one byte — <c>1</c>, <c>1500m</c>, <c>1.9</c>
    ///     — passed the guard, and three quarters of it floored to 0, so the provider wrote the exact
    ///     line the guard existed to prevent. No preset reaches that window, which is why it survived.
    /// </remarks>
    public static long? MaxMemoryBytes(JsonElement desired) {
        var (_, memory) = Resources(desired);

        if (QuantityBytes(memory) is not { } bytes) {
            return null;
        }

        var ceiling = (long)(bytes * MaxMemoryFraction);

        return ceiling > 0 ? ceiling : null;
    }

    /// <summary>
    ///     The <c>redis.conf</c> lines a body implies, in the order the operator writes them.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>These are configuration <i>lines</i>, not a map, because that is the CRD's shape</b> —
    ///     <c>spec.redis.customConfig</c> is a <c>[]string</c> the operator concatenates into a
    ///     <c>redis.conf</c>. It is also why <see cref="Matches" /> tests this field for containment
    ///     rather than equality: the operator's <c>Validate()</c> prepends its own
    ///     <c>replica-priority 100</c> and de-duplicates, so a spec read back is a superset of the one
    ///     applied and an equality test would report drift on every pass forever.
    /// </remarks>
    public static ImmutableArray<string> CustomConfig(JsonElement desired) {
        var lines = ImmutableArray.CreateBuilder<string>();

        if (MaxMemoryBytes(desired) is { } ceiling) {
            lines.Add("maxmemory " + ceiling.ToString(CultureInfo.InvariantCulture));
        }

        lines.Add("maxmemory-policy " + EvictionPolicy(desired));

        switch (PersistenceMode(desired)) {
            case "None":
                // ⚠ Both halves. `appendonly no` alone leaves Valkey's default RDB save points in
                // place, so "None" would still snapshot to a disk the pod does not have.
                lines.Add("appendonly no");
                lines.Add("save \"\"");
                break;

            case "RDB":
                lines.Add("appendonly no");
                lines.Add("save 900 1 300 10 60 10000");
                break;

            default:
                lines.Add("appendonly yes");
                lines.Add("appendfsync " + Text(desired, "persistence", "fsync", "everysec"));
                break;
        }

        return lines.ToImmutable();
    }

    /// <summary>
    ///     The <c>RedisFailover</c> document a desired body becomes, ready for server-side apply.
    /// </summary>
    /// <param name="name">The object's <c>metadata.name</c> — the resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The JSON <c>templates/redisfailover.yaml</c> renders, for the same values.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. Those are ADR-013's seven mandatory
    ///         labels and two annotations, and <c>KubeCommand</c> injects them non-overridably —
    ///         docs/plan/09 § The command builder. A provider that set them itself would be a provider
    ///         that could get them wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>requirepass</c> appears here as a <i>reference by name</i> and never as a
    ///         value, and the <c>Secret</c> it names is not written by this reconciler.</b>
    ///         <c>spec.auth.secretPath</c> is the seam: the operator reads the password out of a
    ///         <c>Secret</c> in the namespace, so the only component that ever holds the plaintext is
    ///         whatever writes that <c>Secret</c> — docs/plan/12 § The pattern, once, piece 5, which
    ///         needs the OpenBao integration that does not exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What that costs here is worse than what it cost Postgres, and the difference is
    ///         deliberate.</b> CloudNativePG generates its own password when the referenced
    ///         <c>Secret</c> is absent, so that provider's gap is a working database whose credentials
    ///         <c>listKeys</c> cannot hand out. spotahome generates nothing: with
    ///         <c>auth.secretPath</c> set and no <c>Secret</c>, the cache does not come up, and the
    ///         resource sits in <see cref="ReconcileOutcome.InProgress" /> until piece 5 lands.
    ///         <b>Omitting the block instead would produce a cache with no <c>requirepass</c> at
    ///         all</b> — a running, unauthenticated Valkey reachable by anything in the tenant's
    ///         namespace — which is a worse answer than a resource that visibly has not finished.
    ///         docs/plan/12 § Cross-cutting decisions makes the same trade for external exposure and
    ///         gives the same reason.
    ///     </para>
    /// </remarks>
    public static string RedisFailoverJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);

        var config = new JsonArray();
        foreach (var line in CustomConfig(desired)) {
            config.Add(line);
        }

        var redis = new JsonObject {
            ["image"] = Image(desired),
            ["replicas"] = Number(desired, "replicas", 3),
            ["customConfig"] = config,
            ["storage"] = Storage(name, desired),
            ["exporter"] = new JsonObject { ["enabled"] = Flag(desired, "monitoring", "enabled", true) }
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            redis["resources"] = Quantities(cpu, memory);
        }

        var sentinel = new JsonObject {
            ["replicas"] = SentinelReplicas,
            ["exporter"] = new JsonObject { ["enabled"] = Flag(desired, "monitoring", "enabled", true) }
        };

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["redis"] = redis,
                ["sentinel"] = sentinel,
                ["auth"] = new JsonObject { ["secretPath"] = CredentialSecretName(name) }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     The data directory: a volume claim, or an <c>emptyDir</c> when nothing is persisted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An <c>emptyDir</c> rather than no <c>storage</c> block at all.</b> The two are not the
    ///     same: with neither, the operator mounts nothing and Valkey writes its working files into the
    ///     container's writable layer, which on a read-only root filesystem fails at start and
    ///     otherwise counts against the node's image store. <c>None</c> means "this data does not
    ///     survive the pod", not "there is nowhere to write".
    /// </remarks>
    static JsonObject Storage(string name, JsonElement desired) {
        if (!IsPersistent(desired)) {
            return new JsonObject { ["emptyDir"] = new JsonObject() };
        }

        var claim = new JsonObject {
            ["accessModes"] = new JsonArray("ReadWriteOnce"),
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject {
                    ["storage"] = Text(desired, "persistence", "size", "8Gi")
                }
            }
        };

        var storageClass = Text(desired, "persistence", "class", string.Empty);

        if (storageClass.Length > 0) {
            claim["storageClassName"] = storageClass;
        }

        return new JsonObject {
            // ⚠ Kept, and the operator's own default is to delete. A cache is not durable and its
            // volume still holds the last AOF: deleting the claim with the pod turns every rolling
            // restart into a cold start, which on a large cache is a latency incident for whatever is
            // in front of it. The resource's own teardown removes the RedisFailover, and the operator
            // removes the claim with it.
            ["keepAfterDeletion"] = true,
            ["persistentVolumeClaim"] = new JsonObject {
                ["metadata"] = new JsonObject { ["name"] = name + "-data" },
                ["spec"] = claim
            }
        };
    }

    static JsonObject Quantities(string cpu, string memory) {
        var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };

        return new JsonObject { ["requests"] = quantities.DeepClone(), ["limits"] = quantities };
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <returns>
    ///     <c>true</c> when the fields this provider owns hold the desired values. ⚠ Subset, not
    ///     equality: server-side apply leaves other managers' fields in place, the operator writes a
    ///     <c>status</c> of its own, and — uniquely on this type — it also <b>edits the spec</b>. Its
    ///     <c>Validate()</c> fills in images, ports, exporter images and a sentinel config, and prepends
    ///     <c>replica-priority 100</c> to <c>customConfig</c>. Demanding an exact match would make the
    ///     operator's own defaulting look like drift and re-apply on every pass.
    /// </returns>
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

        if (spec["redis"] is not JsonObject redis) {
            return false;
        }

        if (redis["replicas"]?.GetValue<int>() != Number(desired, "replicas", 3)
            || redis["image"]?.GetValue<string>() != Image(desired)
            || (spec["sentinel"] as JsonObject)?["replicas"]?.GetValue<int>() != SentinelReplicas
            || (spec["auth"] as JsonObject)?["secretPath"]?.GetValue<string>() is not { Length: > 0 }) {
            return false;
        }

        // ⚠ Containment, per the summary. Every line this provider asked for is present; lines the
        // operator added are not drift.
        var config = redis["customConfig"] as JsonArray;

        foreach (var line in CustomConfig(desired)) {
            if (config?.Any(x => x?.GetValue<string>() == line) != true) {
                return false;
            }
        }

        return true;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the cache in.</param>
    /// <param name="replicas">How many Valkey instances.</param>
    /// <param name="persistence">The persistence mode.</param>
    /// <param name="size">The volume size.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. The read-back the conformance suite compares
    ///     rebuilds a <see cref="SchemaKind.Nested" /> container from whichever leaf lands first, so a
    ///     body carrying an empty object would not survive it.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int replicas = 3,
        string persistence = "AOF",
        string size = "8Gi",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = "8",
                ["mode"] = "Sentinel",
                ["replicas"] = replicas,
                ["persistence"] = new JsonObject { ["mode"] = persistence, ["size"] = size },
                ["maxmemoryPolicy"] = "noeviction",
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

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
