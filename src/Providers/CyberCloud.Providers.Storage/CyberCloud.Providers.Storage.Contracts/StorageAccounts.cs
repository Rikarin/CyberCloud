using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Storage/accounts</c>: the type, its api-version, its
///     body shape, and the one Kubernetes object it becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS ROW IS NOT IN docs/plan/12, AND THAT IS THE FIRST THING TO KNOW ABOUT IT.</b> The
///         four providers before this one are all rows of
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md), whose subject is
///         <i>"databases, caches, brokers, search"</i>. Object storage is
///         [15 § The three kinds](../../../../docs/plan/15-storage-blob-file.md):
///         <i>"Object · <c>CyberCloud.Storage/accounts</c> + <c>/buckets</c> · SeaweedFS + S3 gateway ·
///         HTTPS, S3 API"</i>, and <i>"Object storage — M1 · 2.0 EM"</i>. docs/plan/12 mentions the
///         service only as something it <i>consumes</i> — CloudNativePG's <i>"declarative backup to S3
///         (which we have — [15])"</i> — which is the gap this provider closes:
///         <c>charts/managed/postgres</c> renders a backup destination of
///         <c>s3://tenant-bucket/postgres</c> against a provider that did not exist.
///     </para>
///     <para>
///         ⚠ <b>What docs/plan/12 <i>does</i> own here is the operator</b>, through ADR-010 clause 1,
///         whose survey names <c>SeaweedFS</c>. docs/plan/15 § Object storage gives the reasoning —
///         MinIO's licence moved to AGPL, Ceph RGW <i>"is a full-time role"</i>, SeaweedFS is
///         Apache-2.0 with an O(1)-lookup design. So the eight pieces of docs/plan/12 § The pattern,
///         once apply to this row even though the catalogue table does not list it, and this type is
///         built to them.
///     </para>
///     <para>
///         ⚠ <b>ONE OBJECT, AND IT IS THE WHOLE CLUSTER.</b> A <c>Seaweed</c>
///         (<c>seaweed.seaweedfs.com/v1</c>) expands into masters, volume servers, a filer and a
///         standalone S3 gateway — four StatefulSets/Deployments, their Services, and (when asked) four
///         <c>ServiceMonitor</c>s. Nothing else is applied, which makes this the <i>smallest</i>
///         rendered object set in the catalogue and the <i>largest</i> resulting workload. See
///         <see cref="SeaweedKind" />.
///     </para>
///     <para>
///         ⚠ <b>THE CREDENTIAL IS THE INTERESTING PART OF THIS SERVICE AND IT IS NOT BUILT.</b>
///         <see cref="ConfigSecretName" /> is rendered into <c>spec.s3.configSecret</c> on every apply
///         and nothing writes the <c>Secret</c> it names, so the gateway pod cannot mount its volume
///         and the account visibly does not finish. That is the
///         <c>CyberCloud.Cache/redis</c> decision applied to a service where it matters more, and the
///         argument is at <see cref="ConfigSecretName" />: SeaweedFS with no identities configured does
///         not merely skip authentication, it grants <b>admin</b> to every request. Checked against
///         <c>weed/s3api/auth_credentials.go</c> rather than against a README —
///         <c>charts/managed/seaweedfs/SOURCE</c> records the line.
///     </para>
///     <para>
///         ⚠ <b><c>buckets</c> is docs/plan/15's other half and is not declared.</b> The operator
///         <i>does</i> ship a <c>Bucket</c> CRD, so unlike NATS accounts there is a real object to
///         render — the blocker is the conformance harness being single-type, which is somebody else's
///         to close. <c>charts/managed/seaweedfs/conformance.yaml § owed</c>, <c>bucket-child-type</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/seaweedfs/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class StorageAccounts {
    /// <summary>The provider namespace, as docs/plan/15 § The three kinds spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>CyberCloud.ObjectStorage</c>, and the distinction is docs/plan/15's own.</b> That
    ///     document puts <i>three</i> kinds under one namespace — <c>accounts</c>/<c>buckets</c> for
    ///     object, <c>fileShares</c> for file — and puts block storage under
    ///     <c>CyberCloud.Compute/disks</c> instead, <i>"because merging them into 'storage' produces an
    ///     API where two thirds of the properties are inapplicable"</i>. The namespace is shared; the
    ///     types are not.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.Storage";

    /// <summary>The resource type. docs/plan/15 § The resource model.</summary>
    public const string TypePath = "accounts";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/seaweedfs/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/seaweedfs";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the S3 endpoint and an access-key pair.</summary>
    /// <remarks>
    ///     docs/plan/15 § The resource model puts <c>accessKeys/{name}</c> under the account with
    ///     <i>"secret into Vault, <c>listKeys</c> action"</i>, and docs/plan/12 § Cross-cutting
    ///     decisions makes it <i>"an action with its own permission, audited on every call"</i>.
    ///     ⚠ <c>regenerateKeys</c> is named in the same paragraph and is <b>not</b> declared, for the
    ///     reason the four providers before this one give: it is specified with a rolling grace period
    ///     and nothing in the platform can hold two live credentials for one resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of an account a holder of its S3 credentials — and on
    ///     this type those credentials are the <i>only</i> access control the data plane has, because
    ///     docs/plan/15 § The resource model deliberately keeps ReBAC off the object GET path.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The object an account IS ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>Seaweed</c> custom resource — the whole cluster, in one object.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>seaweeds</c>.</b> It is carried rather than derived for the reason
    ///     <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed harness
    ///     derives a definition stub from — <c>ClusterConformanceHarness</c> reads it off
    ///     <c>ProviderConformanceCase.Objects</c>.
    /// </remarks>
    public static GroupVersionKind SeaweedKind { get; } =
        new() {
            Group = "seaweed.seaweedfs.com", Version = "v1", Kind = "Seaweed", Plural = "seaweeds"
        };

    // ── Ports and names the operator owns ─────────────────────────────────────────────────────

    /// <summary>The S3 port. <c>seaweedv1.FilerS3Port</c>, and the operator's own fallback.</summary>
    /// <remarks>
    ///     ⚠ <b>Not declared as a property, because the operator's <c>s3EffectivePort</c> already
    ///     defaults to it and a port a tenant can move is a connection string that changes shape.</b>
    ///     The IAM API is served on the same port when <c>spec.s3.iam</c> is on, which it is by default
    ///     — <c>api/v1/seaweed_types.go</c>, <c>+kubebuilder:default:=true</c> on
    ///     <c>S3GatewaySpec.IAM</c>.
    /// </remarks>
    public const int S3Port = 8333;

    /// <summary>The port every component serves Prometheus metrics on when monitoring is asked for.</summary>
    /// <remarks>
    ///     ⚠ <b>Setting this is the whole of docs/plan/12 § The pattern, once, piece 6's FIRST
    ///     branch on this service, and it is that branch's second sighting.</b> The corrected piece 6
    ///     reads <i>"ask the operator for the scrape object wherever the operator accepts the
    ///     request"</i>; CloudNativePG answered with <c>spec.monitoring.enablePodMonitor</c> and this
    ///     operator answers with <c>metricsPort</c>. <c>internal/controller/controller_s3.go</c>:
    ///     <c>if m.Spec.S3.MetricsPort != nil { ensureS3ServiceMonitor(m) }</c>, with an <c>else</c>
    ///     branch that <i>deletes</i> the <c>ServiceMonitor</c> when the port is taken away. So the
    ///     scrape object is the operator's, its selector is the operator's, and this provider renders
    ///     no monitoring object at all.
    ///     <para>
    ///         ⚠ It is <b>not</b> <c>spec.metricsAddress</c>, which is the other thing in that CRD with
    ///         "metrics" in its name and is a <i>push</i> gateway address the master forwards to the
    ///         rest of the cluster. Setting that one would produce metrics nothing scrapes and no
    ///         <c>ServiceMonitor</c> anywhere.
    ///     </para>
    /// </remarks>
    public const int MetricsPort = 9327;

    /// <summary>The filer's metadata volume, per docs/plan/05's durability rules.</summary>
    /// <remarks>
    ///     ⚠ <b>The filer's embedded store is the object namespace, and without this volume it lives in
    ///     the pod's writable layer.</b> With no <c>spec.filer.config</c> the operator mounts no
    ///     <c>filer.toml</c> at all — deliberately, see
    ///     <c>internal/controller/controller_filer_configmap.go</c>'s <c>hasFilerConfig</c> — and
    ///     SeaweedFS falls back to its embedded <c>leveldb2</c> store at
    ///     <c>{-defaultStoreDir}/filerldb2</c>. <c>weed/command/filer.go</c> defaults
    ///     <c>-defaultStoreDir</c> to <c>"."</c> and the operator never sets it, so the store lands in
    ///     the container's working directory. ⚠ <b>That working directory is <c>/data</c> only because
    ///     the upstream image says <c>WORKDIR /data</c>, and the operator's
    ///     <c>PersistenceSpec.MountPath</c> defaults to <c>"/data"</c> — two independently defaulted
    ///     values in two repositories that happen to agree, with nothing in either stating the
    ///     dependency.</b> <see cref="SeaweedJson" /> writes the mount path explicitly rather than
    ///     inheriting it, so the coupling is a line in this file instead of a coincidence.
    /// </remarks>
    public const string FilerVolumeSize = "10Gi";

    /// <summary>Where the filer's metadata volume is mounted. ⚠ Written out — see <see cref="FilerVolumeSize" />.</summary>
    public const string FilerMountPath = "/data";

    /// <summary>The image, without a tag. ⚠ The operator has no default and an empty image is legal.</summary>
    /// <remarks>
    ///     ⚠ <b>Written on every apply because the operator supplies nothing.</b>
    ///     <c>api/v1/image.go</c>'s <c>applyVersion(image, version)</c> returns <c>image</c> unchanged
    ///     when <c>image</c> is empty, and every component's <c>Image()</c> goes through it — so a
    ///     <c>Seaweed</c> with no <c>spec.image</c> renders pods with an empty image and fails at the
    ///     API server, per pod, after the caller was told <c>202</c>. This is the same class of
    ///     finding <c>charts/managed/valkey</c> records about spotahome's <c>defaultImage</c>, reached
    ///     from the opposite side: there the operator's default was the wrong <i>licence</i>, here
    ///     there is no default at all.
    ///     <para>
    ///         ⚠ <see cref="SeaweedJson" /> writes the tag into <c>spec.image</c> and leaves
    ///         <c>spec.version</c> unset, rather than setting both. <c>applyVersion</c> would rewrite
    ///         the tag from <c>spec.version</c> if both were present, so two spellings of one fact
    ///         would be one fact and one silent override.
    ///     </para>
    /// </remarks>
    public const string ImageRepository = "chrislusf/seaweedfs";

    /// <summary>The <c>Seaweed</c> an account owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef SeaweedRef(string ns, string name) =>
        new() { Kind = SeaweedKind, Namespace = ns, Name = name };

    /// <summary>The <c>Service</c> the operator puts in front of the S3 gateway.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Not an object this provider applies, and it is here because <see cref="Endpoint" />
    ///     needs it.</b> <c>internal/controller/controller_s3.go</c>'s <c>buildS3Service</c> names it
    ///     <c>{name}-s3</c>. Writing a <c>Service</c> of our own would be this chart competing with the
    ///     controller that owns it — the same rule <c>charts/managed/valkey</c> states.
    /// </remarks>
    public static string S3ServiceName(string name) => name + "-s3";

    /// <summary>The <c>Secret</c> holding the S3 identities file. ⚠ Nothing writes it — see the remarks.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE REFERENCE IS RENDERED AND THE SECRET IS NOT WRITTEN, WHICH IS DELIBERATE AND IS
    ///         THE SINGLE MOST IMPORTANT DECISION ON THIS TYPE.</b> docs/plan/12 § The pattern, once,
    ///         piece 5 — credential provisioning into the tenant's Vault — needs an OpenBao integration
    ///         that does not exist; <c>ISecretResolver</c> has one implementation and it refuses. So the
    ///         gateway pod mounts a <c>Secret</c> that is absent, stays in <c>ContainerCreating</c>, and
    ///         the account never reports <c>Succeeded</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The alternative is worse here than on any service before it, and the reason was
    ///         checked in SeaweedFS' own source rather than inferred.</b>
    ///         <c>weed/s3api/auth_credentials.go</c> sets <c>iam.isAuthEnabled = len(iam.identities) &gt;
    ///         0</c>, and <c>AuthenticateRequest</c> begins <c>if !iam.isAuthEnabled { return
    ///         &amp;Identity{Name: "admin", Account: &amp;AccountAdmin, Actions: []Action{ACTION_ADMIN}}
    ///         }</c>. A gateway with no identity file therefore does not merely skip authentication: it
    ///         answers every unauthenticated request <b>as an administrator</b>. docs/plan/12's own
    ///         <i>"a managed database on a public IP with a weak password is the single most common
    ///         cloud breach"</i> applies with no password at all, over HTTP, to a protocol every tool in
    ///         the industry already speaks.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the answer to "is the service usable without piece 5" is different from the one
    ///         <c>CyberCloud.DBforPostgreSQL/servers</c> gives.</b> CloudNativePG <i>generates</i> a
    ///         password when the <c>Secret</c> its CR references is absent, so that service has a
    ///         working database whose credentials <c>listKeys</c> cannot hand out. There is no
    ///         equivalent here: an S3 endpoint is reachable only with an access-key pair, and the pair
    ///         is exactly what does not exist. This service is <b>not usable at all</b> until piece 5
    ///         lands — which is the <c>CyberCloud.Cache/redis</c> answer, one notch worse.
    ///         <c>charts/managed/seaweedfs/conformance.yaml § owed</c>, <c>listkeys-has-no-handler</c>,
    ///         says what closes it.
    ///     </para>
    /// </remarks>
    public static string ConfigSecretName(string name) => name + "-s3-config";

    /// <summary>The key <see cref="ConfigSecretName" /> files the identities under.</summary>
    /// <remarks>
    ///     ⚠ Projected as <c>-config=/etc/sw/{key}</c> —
    ///     <c>internal/controller/controller_s3.go</c>'s <c>buildS3GatewayStartupScript</c>. The
    ///     extension is <c>.json</c> because SeaweedFS reads the identities file as the JSON form of
    ///     <c>iam_pb.S3ApiConfiguration</c>.
    /// </remarks>
    public const string ConfigSecretKey = "s3.json";

    /// <summary>The in-cluster S3 endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>http</c> and not <c>https</c>, and that is a statement rather than an oversight. The
    ///     operator's <c>TLSSpec</c> issues certificates through cert-manager for the cluster's
    ///     <i>internal</i> gRPC, and nothing terminates TLS on the S3 port; docs/plan/15's <i>"Consumed
    ///     as HTTPS"</i> is an ingress-side promise this type cannot yet keep. See
    ///     <c>conformance.yaml § owed</c>, <c>external-exposure</c>.
    /// </remarks>
    public static string Endpoint(string ns, string name) =>
        "http://"
        + S3ServiceName(name)
        + "."
        + ns
        + ".svc:"
        + S3Port.ToString(CultureInfo.InvariantCulture);

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied.</b> Four providers kept their
    ///     own copy of this grammar and one of them grew a second <i>parser</i> next to it, in
    ///     <see langword="double" />, which disagreed on value rather than on verdict.
    ///     <c>QuantityParserTests</c> fails if a fifth copy or a second suffix table appears. There is
    ///     no rule-2 problem in reaching for it: <see cref="KubeQuantity" /> lives in
    ///     <c>CyberCloud.ResourceManager.Contracts</c>, which every provider may reference.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>s1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>docs/plan/12 § Sizing vocabulary has no row for a storage service, and the table was
    ///     followed anyway rather than extended.</b> Its five families are burstable, CPU-bound,
    ///     general, memory-bound and latency-sensitive; a volume server is bound by disk and by network,
    ///     which is a sixth thing. <c>s1</c> — <i>"1:4 · General — most databases"</i> — is the closest,
    ///     and taking it is the point of having a vocabulary: a service that invents a family because
    ///     its author reasoned about its workload is the drift the table exists to stop. If object
    ///     storage wants an <c>i1</c>, that is a change to docs/plan/12 and then to this row, in that
    ///     order.
    ///     <para>
    ///         ⚠ <b>This sizes the <i>volume servers</i> and nothing else.</b> The masters, the filer
    ///         and the S3 gateway are sized by <see cref="ControlPlaneCpu" />, which is what makes this
    ///         the first type in the catalogue whose quota meters are a sum over <i>heterogeneous</i>
    ///         components rather than <c>replicas × one figure</c>. See <c>StorageProvider</c>.
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

    /// <summary>The CPU every non-volume component requests.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a property, and it still costs quota.</b> A master keeps the
    ///     volume topology in memory, a filer serves metadata and the S3 gateway is a stateless proxy;
    ///     none of them scales with the tenant's data and none of them is a knob worth publishing. What
    ///     they are not is free — an account with three masters, a filer and two gateways runs six pods
    ///     before a byte is stored, and a meter that counted only the volume servers would under-reserve
    ///     by exactly that. <c>StorageProvider</c>'s derivations carry the sum.
    /// </remarks>
    public const string ControlPlaneCpu = "250m";

    /// <summary>The memory every non-volume component requests. See <see cref="ControlPlaneCpu" />.</summary>
    public const string ControlPlaneMemory = "512Mi";

    /// <summary>
    ///     The replication codes docs/plan/15's <i>"replication"</i> account default maps onto.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>SeaweedFS spells replication as three digits and the digits are not a count.</b>
    ///     <c>xyz</c> is <i>x</i> extra copies in other data centres, <i>y</i> in other racks of the
    ///     same data centre, <i>z</i> on other servers in the same rack — so <c>001</c> is two copies
    ///     total and <c>000</c> is one. Publishing the codes as an API would be publishing somebody
    ///     else's encoding; publishing a count would lose the placement, which is the whole of what the
    ///     code says. The member names are the placement and the code is the rendering.
    ///     <para>
    ///         ⚠ <b>A code the topology cannot satisfy is accepted by the API and never satisfied by
    ///         the cluster.</b> <c>DifferentDataCenter</c> on a single-zone cluster leaves every write
    ///         under-replicated, and SeaweedFS reports that as a volume that will not go writable rather
    ///         than as an error on the create. That is a relation between this property and the
    ///         cluster's own topology, which <c>ResourceSchema</c> validates nothing about — the same
    ///         shape as <c>charts/managed/kafka</c>'s <c>replication-factor-versus-node-count</c>, and
    ///         the second sighting of it. <c>conformance.yaml § owed</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, string> ReplicationCodes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["None"] = "000",
            ["SameRack"] = "001",
            ["DifferentRack"] = "010",
            ["DifferentDataCenter"] = "100"
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
                    Description: "The cluster whose namespace holds the object store."
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
                    Description: "SeaweedFS version. ⚠ SeaweedFS ships a release roughly weekly and "
                    + "maintains no long-term branch, so docs/plan/12's \"supported major versions\" is "
                    + "a shape this project does not have; the two values here are the two most recent "
                    + "releases and a new api-version is what adds a third."
                ) {
                    AllowedValues = ["4.40", "4.41"],
                    DefaultJson = "\"4.41\""
                },
                new(
                    "/properties/replication",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where the second copy of every object is placed. None keeps one copy "
                    + "and is for scratch data only. A placement the cluster's topology cannot satisfy "
                    + "leaves volumes read-only rather than failing the create. Immutable, because "
                    + "SeaweedFS applies this to volumes as they are created and never rewrites the "
                    + "ones that already exist — a change would split the account into two durability "
                    + "promises with no way to tell which object got which."
                ) {
                    AllowedValues = [.. ReplicationCodes.Keys.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"SameRack\"",
                    Immutable = true
                },
                new(
                    "/properties/masters",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of master servers. The masters hold the volume topology in a "
                    + "Raft group, so three is the smallest count that survives losing one. One is "
                    + "offered for development and has no quorum at all."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/volumeServers",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of volume servers. This is the capacity axis: total raw "
                    + "capacity is this count times the volume size below, before replication."
                ) {
                    Minimum = 1,
                    Maximum = 16,
                    DefaultJson = "3"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per volume server, either by preset or explicitly. The "
                    + "masters, the filer and the S3 gateway are sized by the platform and are not "
                    + "affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Volume servers use the s1 family, "
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
                    Description: "The data volume, per volume server."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Data volume size per volume server, in Kubernetes quantity form. Grows "
                    + "online; never shrinks."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for the volume servers. Empty means the cluster "
                    + "default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/gateway",
                    SchemaKind.Nested,
                    Description: "The S3 gateway — docs/plan/15's ADR-008: \"the API is S3\"."
                ),
                new(
                    "/properties/gateway/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of S3 gateway pods. The gateway is stateless, so this is a "
                    + "throughput and availability setting rather than a topology one."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "2"
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the operator is asked for a ServiceMonitor per component. On "
                    + "by default — docs/plan/12: \"a managed service the tenant cannot see the health "
                    + "of is a black box they will not trust with production\". Turning it off removes "
                    + "the metrics port as well as the scrape, which is the operator's own behaviour "
                    + "rather than this provider's."
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
                    "/endpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster S3 endpoint, http://host:port. ⚠ No external address is "
                    + "returned, because there is none — see the account's own documentation on "
                    + "exposure."
                ),
                new(
                    "/region",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region name an S3 client signs with. SeaweedFS does not route on "
                    + "it, and SigV4 refuses to sign without one, so it is returned rather than left "
                    + "for the caller to guess."
                ),
                new(
                    "/accessKeyId",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The access key id. Not secret on its own; useless without the pair "
                    + "below."
                ),
                new(
                    "/secretAccessKey",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The secret access key, read from the tenant's Vault for this call "
                    + "only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The SeaweedFS version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>The master count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Masters(JsonElement desired) => Number(desired, "masters", DefaultMasters);

    /// <summary>The volume-server count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int VolumeServers(JsonElement desired) =>
        Number(desired, "volumeServers", DefaultVolumeServers);

    /// <summary>The S3 gateway replica count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int GatewayReplicas(JsonElement desired) =>
        Number(desired, "gateway", "replicas", DefaultGatewayReplicas);

    /// <summary>The data-volume size per volume server a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>The SeaweedFS replication code a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     The three-digit code. ⚠ Falls back to the default's code rather than to <c>000</c>: an
    ///     unrecognised member name must not silently become "one copy".
    /// </returns>
    public static string ReplicationCode(JsonElement desired) {
        var declared = Root(desired, "replication") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultReplication
            : DefaultReplication;

        return ReplicationCodes.TryGetValue(declared, out var code)
            ? code
            : ReplicationCodes[DefaultReplication];
    }

    /// <summary>Whether the desired body asks for the operator's scrape objects.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", true);

    /// <summary>
    ///     The CPU and memory one volume server asks for: the explicit quantities when both are given,
    ///     otherwise the preset's.
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

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>Seaweed</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.s3</c> and not <c>spec.filer.s3</c>, and the CRD refuses both.</b>
    ///         <c>internal/controller/controller_s3.go</c>'s header: the standalone gateway <i>"is the
    ///         preferred way to expose S3. The older <c>FilerSpec.S3</c> path (embedded S3 inside every
    ///         filer pod) is retained for backward compatibility but is deprecated. When both are set
    ///         the webhook rejects the CR."</i> The embedded path would also tie gateway throughput to
    ///         filer replicas, which on this type is pinned at one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spec.filer.replicas</c> is 1 and is not a property, and the reason is a store
    ///         rather than a preference.</b> With no <c>spec.filer.config</c> the filer runs its
    ///         embedded <c>leveldb2</c> — see <see cref="FilerVolumeSize" /> — which is per-pod. Two
    ///         filers would be two divergent object namespaces behind one Service, and neither would
    ///         report an error. An HA filer needs a shared metadata store, which is a Postgres this
    ///         platform can already run and cannot yet let one resource depend on;
    ///         <c>conformance.yaml § owed</c>, <c>filer-is-a-single-point-of-failure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.s3.service.type</c>, so the gateway is <c>ClusterIP</c>.</b> The
    ///         operator's <c>ServiceSpec</c> is a four-field subset — <c>type</c>, <c>annotations</c>,
    ///         <c>loadBalancerIP</c>, <c>clusterIP</c> — with <b>no <c>loadBalancerSourceRanges</c></b>,
    ///         so docs/plan/12 § Cross-cutting decisions' mandatory firewall allow-list has nothing to
    ///         render into. Offering exposure without it would be the one thing that paragraph forbids
    ///         in as many words. <c>conformance.yaml § owed</c>, <c>external-exposure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    /// </remarks>
    public static string SeaweedJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var monitoring = MonitoringEnabled(desired);
        var (cpu, memory) = Resources(desired);

        var master = new JsonObject {
            ["replicas"] = Masters(desired),
            ["defaultReplication"] = ReplicationCode(desired),
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities()
        };

        // ⚠ The storage request sits beside cpu/memory rather than under a `storage:` block, because
        // VolumeSpec inlines corev1.ResourceRequirements: `spec.volume.requests.storage` is what
        // controller_volume_statefulset.go lifts into the claim template, and everything else in the
        // same map is filtered down to the container. One map, two destinations, decided by key.
        var volumeRequests = new JsonObject { ["storage"] = StorageSize(desired) };
        var volume = new JsonObject {
            ["replicas"] = VolumeServers(desired), ["requests"] = volumeRequests
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            volumeRequests["cpu"] = cpu;
            volumeRequests["memory"] = memory;
            volume["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
        }

        var storageClass = Text(desired, "storage", "class", string.Empty);
        if (storageClass.Length > 0) {
            volume["storageClassName"] = storageClass;
        }

        var filer = new JsonObject {
            ["replicas"] = 1,
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities(),
            ["persistence"] = new JsonObject {
                ["enabled"] = true,
                // ⚠ Written out rather than inherited — see FilerVolumeSize for the two defaults in
                // two repositories this line stops depending on.
                ["mountPath"] = FilerMountPath,
                ["accessModes"] = new JsonArray { "ReadWriteOnce" },
                ["resources"] = new JsonObject {
                    ["requests"] = new JsonObject { ["storage"] = FilerVolumeSize }
                }
            }
        };

        var s3 = new JsonObject {
            ["replicas"] = GatewayReplicas(desired),
            ["requests"] = ControlPlaneQuantities(),
            ["limits"] = ControlPlaneQuantities(),
            // ⚠ The reference is unconditional. See ConfigSecretName: a gateway with no identities
            // authenticates nobody and authorises everybody.
            ["configSecret"] = new JsonObject {
                ["name"] = ConfigSecretName(name), ["key"] = ConfigSecretKey
            }
        };

        if (monitoring) {
            master["metricsPort"] = MetricsPort;
            volume["metricsPort"] = MetricsPort;
            filer["metricsPort"] = MetricsPort;
            s3["metricsPort"] = MetricsPort;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                // ⚠ The tag goes here and `spec.version` stays unset — see ImageRepository.
                ["image"] = ImageRepository + ":" + Version(desired),
                ["master"] = master,
                ["volume"] = volume,
                ["filer"] = filer,
                ["s3"] = s3
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
    ///         ⚠ <b>Containment, not equality, and this type has TWO reasons where the three before it
    ///         had one apiece — which is worth stating as a contrast rather than as a restatement.</b>
    ///         <c>KafkaClusters.Matches</c> found that Strimzi's CRD declares <b>no <c>default:</c>
    ///         anywhere</b>, so the structural-defaulting argument was false for it;
    ///         <c>NatsClusters.Matches</c> found the opposite, because built-in kinds are the most
    ///         heavily defaulted objects in Kubernetes. Here the object is a <i>custom</i> resource and
    ///         the CRD declares defaults anyway — <c>api/v1/seaweed_types.go</c> carries
    ///         <c>+kubebuilder:default:=1</c> on <c>S3GatewaySpec.Replicas</c>,
    ///         <c>+kubebuilder:default:=true</c> on its <c>IAM</c>, <c>:=StatefulSet</c> on
    ///         <c>VolumeSpec.Kind</c> and four more — so an applied <c>spec.s3</c> comes back carrying
    ///         <c>iam: true</c> this provider never sent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second reason is the one no earlier provider met: this operator installs a
    ///         MUTATING webhook, and its defaulter is a scaffold.</b>
    ///         <c>api/v1/seaweed_webhook.go</c> registers
    ///         <c>/mutate-seaweed-seaweedfs-com-v1-seaweed</c> on <c>create;update</c>, and the body of
    ///         <c>SeaweedCustomDefaulter.Default</c> is a log line and <c>// TODO(user): fill in your
    ///         defaulting logic.</c> — it mutates nothing <i>today</i>. An equality comparison would
    ///         therefore pass on this version of the operator and break silently on whichever release
    ///         fills that TODO in, with the symptom being every account stuck in <c>InProgress</c>
    ///         while its cluster is perfectly correct. That is a different hazard from
    ///         <c>spotahome</c>'s <c>validate.go</c>, which mutates now, and it is the one an author
    ///         who read the file and saw an empty function would have talked themselves out of.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on the object's <c>kind</c> even though this type owns one kind, because a
    ///         conformance case supplies this as one function over every object the resource owns and
    ///         an unrecognised document must be <c>false</c> rather than assumed.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document
            || document["kind"]?.GetValue<string>() is not (null or "Seaweed")
            || document["spec"] is not JsonObject spec) {
            return false;
        }

        return spec["master"] is JsonObject master
            && master["replicas"]?.GetValue<int>() == Masters(desired)
            && master["defaultReplication"]?.GetValue<string>() == ReplicationCode(desired)
            && spec["volume"] is JsonObject volume
            && volume["replicas"]?.GetValue<int>() == VolumeServers(desired)
            && (volume["requests"] as JsonObject)?["storage"]?.GetValue<string>() == StorageSize(desired)
            && spec["s3"] is JsonObject s3
            && s3["replicas"]?.GetValue<int>() == GatewayReplicas(desired)
            // ⚠ THE CREDENTIAL REFERENCE IS PART OF THE DESIRED STATE AND IS READ BACK LIKE ONE. A
            // Seaweed whose configSecret was stripped by an admission policy, a merge, or a well-meant
            // `kubectl edit` is a gateway that comes up and serves every request as an administrator —
            // see ConfigSecretName. It is the one field here whose absence makes the resource WORK,
            // which is why nothing else would ever report it.
            && (s3["configSecret"] as JsonObject)?["name"]?.GetValue<string>() is { Length: > 0 };
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the account in.</param>
    /// <param name="volumeServers">How many volume servers.</param>
    /// <param name="storageSize">The data volume size per volume server.</param>
    /// <param name="masters">How many masters.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int volumeServers = 3,
        string storageSize = "100Gi",
        int masters = 3,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["replication"] = DefaultReplication,
                ["masters"] = masters,
                ["volumeServers"] = volumeServers,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["gateway"] = new JsonObject { ["replicas"] = DefaultGatewayReplicas },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "4.41";
    const string DefaultReplication = "SameRack";
    const string DefaultPreset = "s1.small";
    const string DefaultStorageSize = "100Gi";
    const int DefaultMasters = 3;
    const int DefaultVolumeServers = 3;
    const int DefaultGatewayReplicas = 2;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    static JsonObject ControlPlaneQuantities() =>
        new() { ["cpu"] = ControlPlaneCpu, ["memory"] = ControlPlaneMemory };

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
