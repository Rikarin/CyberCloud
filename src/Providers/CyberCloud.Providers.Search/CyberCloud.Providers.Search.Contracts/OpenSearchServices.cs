using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Search/services</c>: the type, its api-version, its
///     body shape, and the one Kubernetes object it becomes.
/// </summary>
/// <remarks>
///     <para>
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md): <i>"OpenSearch —
///         <c>CyberCloud.Search/services</c> · M3 · 1.0 EM. <b>OpenSearch operator</b> (Apache-2.0,
///         ADR-011 — Elasticsearch is not available to us). Data/master/coordinating node roles, ISM
///         policies, snapshot repository into the tenant's bucket."</i> ⚠ <b>Read that sentence whole:
///         it names <i>four</i> things and this type delivers <i>one</i> of them.</b> The role split is
///         built and is the interesting half; ISM policies and the snapshot repository are named at
///         <c>charts/managed/opensearch/conformance.yaml § owed</c> with what each actually needs, and
///         neither is a matter of effort — both are blocked on piece 5.
///     </para>
///     <para>
///         ⚠ <b>ADR-011 is why this row is OpenSearch, and it is the only row in docs/plan/12 whose
///         operator choice and whose <i>engine</i> choice come from different ADRs.</b> ADR-010
///         clause 1's survey names the <i>"OpenSearch operator"</i>; ADR-011's table is what makes the
///         engine OpenSearch rather than Elasticsearch — <i>"Elasticsearch | SSPL/Elastic | ✗ —
///         <b>OpenSearch</b> (Apache-2.0)"</i>. Valkey, OpenBao and FerretDB are the same shape and this
///         is the fourth. What is different here is that the substitution is not a compatibility
///         <i>claim</i>: OpenSearch is a fork of Elasticsearch 7.10 and has diverged since, so unlike
///         <c>CyberCloud.Cache/redis</c> — where <i>"the connection string works with every Redis
///         client"</i> — a 8.x Elasticsearch client is not promised anything here. Said in the
///         <c>version</c> property's own description rather than in a README.
///     </para>
///     <para>
///         ⚠ <b>ONE OBJECT, AND THE ROLE SPLIT LIVES INSIDE IT.</b> An <c>OpenSearchCluster</c>
///         (<c>opensearch.opster.io/v1</c>) carries <c>spec.nodePools[]</c>, and each pool is a
///         StatefulSet with its own replica count, its own disk and its own <c>roles</c> list. So
///         <i>"data/master/coordinating node roles"</i> is three entries in one array rather than three
///         objects — which is why <see cref="NodePoolsJson" /> is the longest function in this file and
///         <c>SearchProvider</c>'s meters are a sum over two populations.
///     </para>
///     <para>
///         ⚠ <b>THE GROUP IS DEPRECATED UPSTREAM AND IS SHIPPED ANYWAY, WITH THE DATE.</b>
///         <c>api/v1/groupversion_info.go</c> carries <c>GroupVersion = schema.GroupVersion{Group:
///         "opensearch.opster.io", Version: "v1"}</c> and a package comment saying <i>"The
///         opensearch.opster.io API group is deprecated and will be removed in a future release. Please
///         migrate to opensearch.org/v1"</i>. Both groups exist in the tree today —
///         <c>api/opensearch.org/v1/groupversion_info.go</c> registers <c>Group: "opensearch.org"</c>.
///         <see cref="ClusterKind" /> names the <b>deprecated</b> one deliberately: it is the group the
///         operator releases in <c>charts/bundle/</c> would serve today, and a provider that rendered
///         into a group the installed operator does not watch produces an object that is applied,
///         accepted, and reconciled by nothing — the one failure mode with no error anywhere. Moving it
///         is a new api-version plus a bundle bump, in that order, and
///         <c>conformance.yaml § owed</c>, <c>api-group-is-deprecated</c>, says so.
///     </para>
///     <para>
///         ⚠ <b>WHAT THE OPERATOR DOES ABOUT CREDENTIALS IS NOT WHAT THE CATALOGUE'S OTHER SERVICES
///         DO, AND THE ASSUMPTION WORTH CHECKING TURNED OUT TO BE FALSE.</b> OpenSearch ships the
///         security plugin on by default and the obvious worry is
///         <c>CyberCloud.Storage/accounts</c>' one — an engine that treats "no credentials configured"
///         as "authenticate nobody". It does not.
///         <c>opensearch-operator/pkg/helpers/helpers.go</c>'s <c>EnsureAdminCredentialsSecret</c>
///         returns the tenant's secret when <c>spec.security.config.adminCredentialsSecret.Name</c> is
///         set and otherwise <i>generates</i> one — <c>randomPassword := GenerateSecurePassword()</c>
///         into a Secret with <c>username: admin</c> and that password. So this service degrades the
///         way <c>CyberCloud.DBforPostgreSQL/servers</c> does: it comes up <b>authenticated with a
///         credential the platform cannot hand out</b>, not open. <see cref="Schema2026" /> therefore
///         declares no credential reference at all, and <see cref="ListKeysAction" /> has a response
///         shape and no handler.
///         <para>
///             ⚠ <b>The upstream <i>documentation</i> says the opposite of the upstream code, and the
///             disagreement is recorded rather than resolved.</b>
///             <c>docs/userguide/main.md</c> says <i>"By default the operator will use the included
///             demo securityconfig with default users"</i> and names <c>admin / admin</c>. Both can be
///             true at once — the demo <i>securityconfig</i> supplies the roles and role-mappings while
///             the admin <i>password</i> is generated — and which one a given release does is the
///             difference between a cluster nobody can log into and a cluster everybody can.
///             <c>conformance.yaml § owed</c>, <c>demo-securityconfig</c>, is the item that closes it,
///             and it is the one thing in this provider that should be verified against a running
///             operator before this row is called done.
///         </para>
///     </para>
///     <para>
///         ⚠ <b>TLS IS THE HAZARD THIS TYPE OWNS, AND IT IS THE ONE THING THE OPERATOR WILL NOT DO
///         UNASKED.</b> <c>pkg/reconcilers/tls.go</c> begins <c>if r.instance.Spec.Security == nil ||
///         r.instance.Spec.Security.Tls == nil { r.logger.Info("No security specified. Not doing
///         anything"); return ctrl.Result{}, nil }</c> — so an <c>OpenSearchCluster</c> with no
///         <c>spec.security.tls</c> gets <b>no certificates generated, no secrets created and no volume
///         mounts configured</b>, and OpenSearch's security plugin requires transport TLS to form a
///         cluster at all. <see cref="ClusterJson" /> writes <c>generate: true</c> on both the transport
///         and the HTTP listener unconditionally. It is not a property, because "turn off TLS between
///         the nodes of your search cluster" is not a setting a managed service should have.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the five providers before this one give</b>:
///         the manager did not read <c>SoftDeleteDays</c>, and declaring a recovery window the platform
///         does not honour would be a promise made to the users most likely to test it. ⚠ <b>THAT REASON
///         HAS EXPIRED AND THE DECLARATION IS NOW A ONE-LINE DECISION RATHER THAN A BLOCKED ONE.</b>
///         docs/plan/08 § Soft delete is built: a <c>DELETE</c> of a type declaring a window parks the
///         resource at <c>IndexEntryState.SoftDeleted</c> so its old address answers the canonical
///         <c>404</c>, holds its name, keeps its committed quota, moves its ReBAC parent edge to the
///         subscription and drops its direct role assignments; a restore reverses it and a purge — under
///         its own permission — ends it. So the question this type still owes an answer to is the
///         provider's own: <i>does the data this type carries deserve a recovery window, and how long</i>,
///         which is a claim about the data and not about the platform.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/opensearch/values.yaml</c> is the other half — ADR-010 § Which end authors
///         the schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class OpenSearchServices {
    /// <summary>The provider namespace, as docs/plan/12 § The catalogue spells it.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>CyberCloud.OpenSearch</c>.</b> docs/plan/12 gives the namespace to the
    ///     <i>capability</i> and the type to the engine, which is what lets
    ///     <c>CyberCloud.Search/vectorStores</c> sit beside this one: a vector store is search and is
    ///     not OpenSearch. The same reasoning <c>CyberCloud.Messaging</c> uses for Kafka and NATS.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.Search";

    /// <summary>The resource type. docs/plan/12 § The catalogue.</summary>
    public const string TypePath = "services";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/opensearch/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/opensearch";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the endpoint and the admin credential.</summary>
    /// <remarks>
    ///     docs/plan/12 § Cross-cutting decisions makes this <i>"an action with its own permission,
    ///     audited on every call"</i>. ⚠ <c>regenerateKeys</c> is named in the same paragraph and is
    ///     <b>not</b> declared, for the reason the five providers before this one give: it is specified
    ///     with a rolling grace period and nothing in the platform can hold two live credentials for one
    ///     resource.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>The permission <see cref="ListKeysAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. The
    ///     credential this returns is the OpenSearch <b>admin</b> — the operator generates exactly one
    ///     and it is not scoped — so sharing <c>read</c> would make every viewer of a search service a
    ///     cluster administrator of it.
    /// </remarks>
    public const string ListKeysPermission = "listKeys";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The object a service IS ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>OpenSearchCluster</c> custom resource — every node pool, in one object.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The plural is <c>opensearchclusters</c>.</b> It is carried rather than derived for the
    ///     reason <see cref="GroupVersionKind.Plural" /> gives, and it is what the cluster-backed
    ///     harness derives a definition stub from — <c>ClusterConformanceHarness</c> reads it off
    ///     <c>ProviderConformanceCase.Objects</c>.
    ///     <para>
    ///         ⚠ The group is the <b>deprecated</b> <c>opensearch.opster.io</c> — see this type's own
    ///         remarks for why that is a decision rather than an oversight, and
    ///         <c>conformance.yaml § owed</c>, <c>api-group-is-deprecated</c>, for what moves it.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() {
            Group = "opensearch.opster.io",
            Version = "v1",
            Kind = "OpenSearchCluster",
            Plural = "opensearchclusters"
        };

    // ── Ports, names and node-pool vocabulary the operator owns ───────────────────────────────

    /// <summary>The REST port. <c>GeneralConfig.HttpPort</c>, <c>+kubebuilder:default=9200</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Not declared as a property, and it is written anyway.</b> The CRD defaults it, so an
    ///     omitted <c>spec.general.httpPort</c> comes back as <c>9200</c> — which is one of the three
    ///     structural defaults that make <see cref="Matches" /> a containment check. Writing it makes
    ///     the endpoint <see cref="Endpoint" /> hands out a fact of this file rather than of somebody
    ///     else's default.
    /// </remarks>
    public const int HttpPort = 9200;

    /// <summary>The component name of the pool that holds the data. One StatefulSet per component.</summary>
    /// <remarks>
    ///     ⚠ <b>These three strings are object-name segments, not labels.</b> The operator names each
    ///     pool's StatefulSet <c>{cluster}-{component}</c>, so changing one renames a StatefulSet, which
    ///     is a delete and a recreate of every pod in it — and on <see cref="DataComponent" /> that is
    ///     every shard. They are constants here so that the rename is a diff in this file rather than a
    ///     literal somebody edits in a template.
    /// </remarks>
    public const string DataComponent = "data";

    /// <summary>The component name of the pool that holds the cluster state.</summary>
    public const string MasterComponent = "masters";

    /// <summary>The component name of the pool that holds neither. See <see cref="CoordinatingNodes" />.</summary>
    public const string CoordinatingComponent = "coordinators";

    /// <summary>
    ///     The role a dedicated cluster-state node declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>cluster_manager</c> and not <c>master</c>, and the operator accepts both.</b>
    ///     <c>pkg/builders/cluster.go</c>'s <c>availableRoles</c> carries <c>"master"</c> and
    ///     <c>"cluster_manager"</c>, and <c>helpers.ResolveClusterManagerRole(version)</c> picks the
    ///     spelling the declared OpenSearch version wants. OpenSearch renamed the role in 2.0 and every
    ///     version <see cref="Schema2026" /> offers is ≥ 2.19, so the modern spelling is the correct one
    ///     and the compatibility path is not relied on.
    /// </remarks>
    public const string ClusterManagerRole = "cluster_manager";

    /// <summary>The role a data node declares. ⚠ Ingest is on the same pool — see <see cref="NodePoolsJson" />.</summary>
    public const string DataRole = "data";

    /// <summary>The pipeline role. Carried by the data pool, and by the coordinating pool when there is one.</summary>
    public const string IngestRole = "ingest";

    /// <summary>The volume every node that is <i>not</i> a data node gets.</summary>
    /// <remarks>
    ///     ⚠ <b>A cluster-manager node has no shards and still has state that must survive a restart</b>
    ///     — the cluster metadata, which is the only copy of what indices exist and where their shards
    ///     live. ⚠ And <c>NodePool.DiskSize</c> is <c>omitempty</c>, so a pool that declared none would
    ///     get whatever the operator falls back to rather than a stated size. It is written for every
    ///     pool, and it is counted: <c>SearchProvider.StorageDrawn</c> adds it per non-data node, which
    ///     is what makes storage a sum over two populations rather than a product.
    /// </remarks>
    public const string ControlPlaneVolumeSize = "10Gi";

    /// <summary>The CPU a cluster-manager node requests.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a property, and it still costs quota.</b> A cluster-manager node
    ///     holds the cluster state in memory and does not scale with the tenant's index size; it is not
    ///     a knob worth publishing. What it is not is free — a service with three of them runs three
    ///     JVMs before a document is indexed. <c>SearchProvider</c>'s derivations carry the sum.
    ///     <para>
    ///         ⚠ <b>It is larger than <c>CyberCloud.Storage/accounts</c>' <c>250m</c>/<c>512Mi</c> and
    ///         that is not a preference.</b> A SeaweedFS master is a Go binary; this is a JVM, and
    ///         512 MiB is below what OpenSearch's own startup heap check passes. A control-plane share
    ///         copied from that provider would produce a pool that CrashLoopBackOffs before it ever
    ///         joins, which reads as a cluster that will not form rather than as a sizing mistake.
    ///     </para>
    /// </remarks>
    public const string ControlPlaneCpu = "500m";

    /// <summary>The memory a cluster-manager node requests. See <see cref="ControlPlaneCpu" />.</summary>
    public const string ControlPlaneMemory = "2Gi";

    /// <summary>The image repository, without a tag.</summary>
    /// <remarks>
    ///     ⚠ <b>Not written into <c>spec.general.image</c>, unlike every other provider in the
    ///     tree.</b> <c>GeneralConfig</c> embeds an <c>*ImageSpec</c> <i>and</i> carries
    ///     <c>Version string</c>, and the operator composes the reference from the version when the
    ///     image is empty. That is the opposite of <c>charts/managed/seaweedfs</c>' finding — there
    ///     <c>applyVersion</c> returns an empty image unchanged and a <c>Seaweed</c> with no
    ///     <c>spec.image</c> renders pods with no image at all. Here <c>spec.general.version</c> is the
    ///     one spelling and the repository is documented rather than rendered, so the two facts stay
    ///     one fact. Kept as a constant because <see cref="ListKeysResponse" />'s prose and the chart's
    ///     <c>@internal imageName</c> escape hatch both name it.
    /// </remarks>
    public const string ImageRepository = "opensearchproject/opensearch";

    /// <summary>The <c>OpenSearchCluster</c> a service owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = name };

    /// <summary>
    ///     The <c>Service</c> the operator puts in front of the cluster.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Not an object this provider applies, and it is here because <see cref="Endpoint" />
    ///     needs it.</b> <c>GeneralConfig.ServiceName</c> is the one field in that struct with <b>no
    ///     <c>omitempty</c></b> — the operator names the Service after it and every node's
    ///     <c>discovery.seed_hosts</c> resolves through it, so a cluster that left it unset would not
    ///     form. <see cref="ClusterJson" /> writes the resource's own name into it, which makes the
    ///     Service name and the object name the same string on purpose: two names for one cluster is
    ///     the shape a support engineer has to hold in their head.
    /// </remarks>
    public static string ServiceName(string name) => name;

    /// <summary>The in-cluster REST endpoint <c>listKeys</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <c>https</c>, and this is the first service in the catalogue where that is <i>true</i>
    ///     rather than aspirational. <c>CyberCloud.Storage/accounts</c> hands out <c>http</c> because
    ///     nothing terminates TLS on its S3 port; here <see cref="ClusterJson" /> asks the operator to
    ///     generate an HTTP certificate — see this type's remarks on <c>pkg/reconcilers/tls.go</c> — so
    ///     the listener genuinely speaks TLS. ⚠ It is a <b>self-signed</b> certificate from the
    ///     operator's own CA, so a client still has to trust that CA; <c>conformance.yaml § owed</c>,
    ///     <c>ca-bundle-is-not-handed-out</c>.
    /// </remarks>
    public static string Endpoint(string ns, string name) =>
        "https://"
        + ServiceName(name)
        + "."
        + ns
        + ".svc:"
        + HttpPort.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>m1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b><c>m1</c> and not the <c>s1</c> the three most recent providers took.</b> docs/plan/12
    ///     § Sizing vocabulary reads <i>"m1.* · 1:8 · Memory-bound — caches, analytics"</i>, and an
    ///     OpenSearch node is bound by heap and by the filesystem cache underneath it rather than by
    ///     CPU. The table is deliberately the <b>same values</b> as
    ///     <c>CyberCloud.Cache/redis</c>' for every key the two share, because two <c>m1</c> tables that
    ///     disagreed would make the family name mean two things —
    ///     <c>OpenSearchDeclarationTests.TheM1TableIsTheOneTheVocabularyAlreadyDefines</c> pins the
    ///     ratio against the literal <c>8</c> rather than against that provider, which a
    ///     <c>Providers.*</c> assembly may not reference.
    ///     <para>
    ///         ⚠ <b>The two smallest rungs are missing and their absence is the decision.</b>
    ///         <c>m1.nano</c> (100m/1Gi) and <c>m1.micro</c> (250m/2Gi) are in that provider's table and
    ///         are not offered here: OpenSearch sets its JVM heap from the container limit, and a node
    ///         under 4 GiB spends its life in garbage collection and then fails a bootstrap check. A
    ///         cache at 1 GiB is a small cache; a search node at 1 GiB is an outage with a green
    ///         readiness probe. Offering a rung this engine cannot run is worse than having no rung.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This sizes the data <i>and</i> coordinating pools and nothing else.</b> The
    ///         cluster-manager pool is sized by <see cref="ControlPlaneCpu" />, which is what makes this
    ///         type's meters a sum over two populations — see <c>SearchProvider</c>.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
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
                    Description: "The region the service is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The service's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the search service."
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
                    Description: "OpenSearch version. ⚠ OpenSearch is a fork of Elasticsearch 7.10 "
                    + "(ADR-011 — Elasticsearch is SSPL and is not available to us) and the two have "
                    + "diverged since, so an Elasticsearch 8 client is not promised anything here. "
                    + "Upgrades between the values below are online and in the maintenance window; a "
                    + "third value is a new api-version."
                ) {
                    AllowedValues = ["2.19.0", "3.1.0"],
                    DefaultJson = "\"3.1.0\""
                },
                new(
                    "/properties/dataNodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of data nodes. This is the capacity axis: total raw capacity "
                    + "is this count times the disk size below, before replicas. Every data node also "
                    + "carries the ingest role, so an indexing pipeline needs no separate pool."
                ) {
                    Minimum = 1,
                    Maximum = 20,
                    DefaultJson = "3"
                },
                new(
                    "/properties/masterNodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of dedicated cluster-manager nodes. They hold the cluster "
                    + "state in a quorum, so three is the smallest count that survives losing one. One "
                    + "is offered for development and has no quorum at all. An even count is worse "
                    + "than the odd count below it and the API cannot say so — see the service's own "
                    + "documentation."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "3"
                },
                new(
                    "/properties/coordinatingNodes",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "Number of coordinating-only nodes — nodes that hold no data and no "
                    + "cluster state and exist to fan a search out and merge the results. Zero is the "
                    + "default and is right until a query pattern makes one data node the bottleneck "
                    + "for every search. They are sized by the same preset as the data nodes."
                ) {
                    Minimum = 0,
                    Maximum = 10,
                    DefaultJson = "0"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory per data node and per coordinating node, either by "
                    + "preset or explicitly. The cluster-manager nodes are sized by the platform and "
                    + "are not affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Search nodes use the m1 family, "
                    + "which is 1 vCPU to 8 GiB, because OpenSearch is bound by heap and by the "
                    + "filesystem cache rather than by CPU. The m1 rungs below m1.small are "
                    + "deliberately not offered: OpenSearch derives its JVM heap from the container "
                    + "limit and a node under 4 GiB fails a bootstrap check after passing its "
                    + "readiness probe."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"m1.medium\""
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
                    Description: "The data volume, per data node."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Disk size per data node, in Kubernetes quantity form. Grows online; "
                    + "never shrinks. The cluster-manager and coordinating nodes get a fixed 10Gi that "
                    + "is not configurable and is counted against the storage quota anyway."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for every node pool. Empty means the cluster "
                    + "default."
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
                    Description: "Whether the operator is asked for a ServiceMonitor. On by default — "
                    + "docs/plan/12: \"a managed service the tenant cannot see the health of is a black "
                    + "box they will not trust with production\". ⚠ The metrics themselves come from "
                    + "the prometheus-exporter plugin, which the operator installs into every node on "
                    + "the first reconcile after this is turned on — so turning it on restarts the "
                    + "pods and turning it off does not remove the plugin."
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
    ///     <para>
    ///         ⚠ <b>The credential this would return already exists, which is not true of every
    ///         service in the catalogue.</b> The operator generates it — see this file's header on
    ///         <c>EnsureAdminCredentialsSecret</c> — so unlike <c>CyberCloud.Storage/accounts</c>, where
    ///         there is no credential at all until piece 5 lands, here there is one and the platform
    ///         has no path to read it out. That is the <c>CyberCloud.DBforPostgreSQL/servers</c>
    ///         position exactly, and it is the second sighting of it.
    ///     </para>
    /// </remarks>
    public static ResourceSchema ListKeysResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/endpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster REST endpoint, https://host:port. ⚠ No external "
                    + "address is returned, because there is none — see the service's own "
                    + "documentation on exposure."
                ),
                new(
                    "/username",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The administrative user. Always \"admin\": the operator generates "
                    + "exactly one credential and does not name it."
                ),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The administrative password, read from the tenant's Vault for this "
                    + "call only."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The OpenSearch version a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? DefaultVersion
            : DefaultVersion;

    /// <summary>The data-node count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int DataNodes(JsonElement desired) => Number(desired, "dataNodes", DefaultDataNodes);

    /// <summary>The cluster-manager-node count a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MasterNodes(JsonElement desired) =>
        Number(desired, "masterNodes", DefaultMasterNodes);

    /// <summary>The coordinating-node count a body asks for. ⚠ May legitimately be zero.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Zero is the default and it is the reason <see cref="NodePoolsJson" /> renders two pools
    ///     rather than three on an ordinary body.</b> <c>NodePool.Replicas</c> has no
    ///     <c>omitempty</c>, so a pool declared with <c>replicas: 0</c> is a StatefulSet the operator
    ///     creates, scales to nothing, and then waits on in every readiness roll-up it does. The pool is
    ///     omitted instead.
    /// </remarks>
    public static int CoordinatingNodes(JsonElement desired) =>
        Number(desired, "coordinatingNodes", DefaultCoordinatingNodes);

    /// <summary>The disk size per data node a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>Whether the desired body asks for the operator's scrape object.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", true);

    /// <summary>
    ///     The CPU and memory one data or coordinating node asks for: the explicit quantities when both
    ///     are given, otherwise the preset's.
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

    /// <summary>
    ///     The node pools a desired body becomes — two or three, in a fixed order.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS docs/plan/12's <i>"Data/master/coordinating node roles"</i> AND IT IS AN
    ///         ARRAY, WHICH IS THE PART THAT COSTS.</b> Server-side apply merges a list of objects by
    ///         its merge key or replaces it wholesale, and <c>spec.nodePools</c> has no listMapKey in
    ///         the operator's CRD — so the whole array is one atomic field this provider owns. That
    ///         makes the <i>order</i> load-bearing in a way a map is not: masters, data, coordinators,
    ///         always, so that a body which changed nothing renders bytes that changed nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The coordinating pool is OMITTED at zero rather than declared with
    ///         <c>replicas: 0</c>.</b> See <see cref="CoordinatingNodes" /> — a zero-replica pool is a
    ///         real StatefulSet the operator then waits on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The data pool carries <c>ingest</c> as well as <c>data</c>, and the coordinating
    ///         pool carries <c>ingest</c> alone.</b> A pool with an <i>empty</i> roles list is the
    ///         textbook spelling of a coordinating-only node and it is not what is written here:
    ///         <c>pkg/builders/cluster.go</c> filters <c>node.Roles</c> against its own
    ///         <c>availableRoles</c> and then does <c>nodeRolesValue := strings.Join(selectedRoles,
    ///         ",")</c> with <c>if len(selectedRoles) == 0 { nodeRolesValue = "[]" }</c> — it renders
    ///         the two-character <i>string</i> <c>[]</c> into an environment variable that OpenSearch
    ///         parses as a node-roles list. That path exists and nothing in the operator's tests or
    ///         documentation exercises it, and a node whose roles OpenSearch failed to parse joins as
    ///         a <i>default</i> node — data, cluster-manager and all — which is the exact opposite of
    ///         what was asked for and reports itself as healthy. <c>ingest</c> is a role these nodes
    ///         genuinely have, it takes the documented path through the same function, and it holds no
    ///         shards and no cluster state, which is what "coordinating" has to mean here.
    ///         <c>conformance.yaml § owed</c>, <c>coordinating-is-ingest-only</c>.
    ///     </para>
    /// </remarks>
    public static string NodePoolsJson(JsonElement desired) {
        var (cpu, memory) = Resources(desired);
        var storageClass = Text(desired, "storage", "class", string.Empty);

        var pools = new JsonArray {
            Pool(
                MasterComponent,
                MasterNodes(desired),
                [ClusterManagerRole],
                ControlPlaneVolumeSize,
                ControlPlaneCpu,
                ControlPlaneMemory,
                storageClass
            ),
            Pool(
                DataComponent,
                DataNodes(desired),
                [DataRole, IngestRole],
                StorageSize(desired),
                cpu,
                memory,
                storageClass
            )
        };

        if (CoordinatingNodes(desired) > 0) {
            pools.Add(
                Pool(
                    CoordinatingComponent,
                    CoordinatingNodes(desired),
                    [IngestRole],
                    ControlPlaneVolumeSize,
                    cpu,
                    memory,
                    storageClass
                )
            );
        }

        return pools.ToJsonString();
    }

    /// <summary>The <c>OpenSearchCluster</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.security.tls.transport.generate</c> AND <c>…http.generate</c> ARE WRITTEN
    ///         UNCONDITIONALLY, AND THIS IS THE ONE FIELD SET WHOSE ABSENCE BREAKS THE SERVICE
    ///         SILENTLY.</b> <c>pkg/reconcilers/tls.go</c> returns immediately when
    ///         <c>Spec.Security</c> or <c>Spec.Security.Tls</c> is nil — <i>"No security specified. Not
    ///         doing anything"</i> — generating no certificates, creating no secrets and mounting no
    ///         volumes. OpenSearch's security plugin requires transport TLS to form a cluster, so a
    ///         service rendered without this is a set of nodes that never discover each other.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>perNode: true</c> on the transport listener, which is not the cheaper
    ///         option.</b> One certificate shared by every node would also satisfy the plugin. A
    ///         per-node certificate is what makes the transport layer's peer check identify the
    ///         <i>node</i> rather than the cluster, and a shared key means one compromised pod can
    ///         impersonate the cluster manager.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.security.config</c>, and that is the decision this type is most likely
    ///         to be second-guessed on.</b> Leaving <c>adminCredentialsSecret</c> unset makes the
    ///         operator <i>generate</i> a random admin password —
    ///         <c>helpers.EnsureAdminCredentialsSecret</c> — so the service comes up authenticated and
    ///         the platform simply cannot hand the credential out. Rendering a reference to a Secret
    ///         nothing writes, which is what <c>charts/managed/seaweedfs</c> does, would be right there
    ///         and wrong here: SeaweedFS with no identities file grants admin to everyone, so the
    ///         dangling reference is the safer of two bad states. Here the dangling reference would
    ///         stop a cluster that would otherwise be fine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.dashboards</c>, so OpenSearch Dashboards is not deployed.</b> docs/plan/12's
    ///         row does not ask for it and a second web application per service is a second thing to
    ///         expose, authenticate and patch. <c>conformance.yaml § owed</c>, <c>dashboards</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>spec.general.snapshotRepositories</c>, which is one of the four things
    ///         docs/plan/12's row names.</b> A repository into the tenant's bucket needs
    ///         <c>s3.client.default.access_key</c> and <c>…secret_key</c> in the OpenSearch keystore,
    ///         which is piece 5 with an extra step. Declared owed rather than half-rendered: a snapshot
    ///         repository that cannot authenticate is a backup policy that reports success until it is
    ///         needed.
    ///     </para>
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    /// </remarks>
    public static string ClusterJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var general = new JsonObject {
            // ⚠ Required upstream — GeneralConfig.ServiceName is the one field in that struct with no
            // omitempty, and every node's discovery.seed_hosts resolves through the Service it names.
            ["serviceName"] = ServiceName(name),
            ["version"] = Version(desired),
            // ⚠ Written although the CRD defaults it to 9200, so Endpoint() names a port this file
            // decided rather than one somebody else's default did.
            ["httpPort"] = HttpPort
        };

        if (MonitoringEnabled(desired)) {
            general["monitoring"] = new JsonObject { ["enable"] = true };
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["general"] = general,
                ["security"] = new JsonObject {
                    ["tls"] = new JsonObject {
                        ["transport"] = new JsonObject {
                            ["generate"] = true, ["perNode"] = true
                        },
                        ["http"] = new JsonObject { ["generate"] = true }
                    }
                },
                ["nodePools"] = JsonNode.Parse(NodePoolsJson(desired))
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
    ///         ⚠ <b>Containment, not equality, and the evidence is the CRD rather than a README —
    ///         which is the check the three providers before this one each had to make separately.</b>
    ///         <c>api/v1/opensearch_types.go</c> carries <c>+kubebuilder:default=9200</c> on
    ///         <c>GeneralConfig.HttpPort</c>, <c>+kubebuilder:default=true</c> on
    ///         <c>GeneralConfig.SetVMMaxMapCount</c>, and — the one that decides it —
    ///         <c>+kubebuilder:default=true</c> together with
    ///         <c>+kubebuilder:validation:Required</c> on <c>ConfMgmt.SmartScaler</c>. That last pair
    ///         means the API server writes a <c>spec.confMgmt.smartScaler: true</c> that this provider
    ///         never sent into <i>every</i> object, on <i>every</i> apply, whatever the body said. An
    ///         equality comparison would never converge — not intermittently, not on some operator
    ///         release, but on the first create against a correctly installed CRD.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The node pools are compared by <i>component</i> rather than by index.</b>
    ///         <see cref="NodePoolsJson" /> renders them in a fixed order, and comparing positionally
    ///         would make this function agree with the renderer instead of with the cluster — so an
    ///         operator, an admission policy or a merge that reordered the array would read back as
    ///         converged while the data pool's replica count was being compared against the masters'.
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
            || document["kind"]?.GetValue<string>() is not (null or "OpenSearchCluster")
            || document["spec"] is not JsonObject spec
            || spec["general"] is not JsonObject general
            || general["version"]?.GetValue<string>() != Version(desired)
            || general["serviceName"]?.GetValue<string>() is not { Length: > 0 }
            || spec["nodePools"] is not JsonArray pools) {
            return false;
        }

        // ⚠ THE TLS BLOCK IS PART OF THE DESIRED STATE AND IS READ BACK LIKE ONE. A cluster whose
        // spec.security was stripped by an admission policy or a well-meant `kubectl edit` is a
        // cluster the operator's tls reconciler skips entirely — see ClusterJson — and the symptom is
        // nodes that never form a cluster rather than a field that looks wrong.
        if (spec["security"] is not JsonObject security
            || security["tls"] is not JsonObject tls
            || (tls["transport"] as JsonObject)?["generate"]?.GetValue<bool>() != true
            || (tls["http"] as JsonObject)?["generate"]?.GetValue<bool>() != true) {
            return false;
        }

        foreach (var expected in JsonNode.Parse(NodePoolsJson(desired))!.AsArray()) {
            var component = expected!["component"]!.GetValue<string>();

            var found = pools.FirstOrDefault(
                x => (x as JsonObject)?["component"]?.GetValue<string>() == component
            ) as JsonObject;

            if (found is null
                || found["replicas"]?.GetValue<int>() != expected["replicas"]!.GetValue<int>()
                || found["diskSize"]?.GetValue<string>() != expected["diskSize"]!.GetValue<string>()
                || !RolesMatch(found["roles"] as JsonArray, expected["roles"]!.AsArray())) {
                return false;
            }
        }

        // ⚠ AND THE COUNT, WHICH IS THE HALF THE LOOP ABOVE CANNOT SEE. Dropping coordinatingNodes
        // back to zero removes a pool from the desired array, and a containment check over the
        // desired entries alone would report Converged while a coordinating StatefulSet was still
        // running and still being billed.
        return pools.Count == JsonNode.Parse(NodePoolsJson(desired))!.AsArray().Count;
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the service in.</param>
    /// <param name="dataNodes">How many data nodes.</param>
    /// <param name="storageSize">The disk size per data node.</param>
    /// <param name="masterNodes">How many cluster-manager nodes.</param>
    /// <param name="coordinatingNodes">How many coordinating-only nodes.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. A schema's <see cref="SchemaKind.Nested" />
    ///     container is rebuilt from whichever leaf lands first, so a body carrying an empty object
    ///     would not survive the read-back the conformance suite compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int dataNodes = 3,
        string storageSize = "100Gi",
        int masterNodes = 3,
        int coordinatingNodes = 0,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = DefaultVersion,
                ["dataNodes"] = dataNodes,
                ["masterNodes"] = masterNodes,
                ["coordinatingNodes"] = coordinatingNodes,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader below has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    const string DefaultVersion = "3.1.0";
    const string DefaultPreset = "m1.medium";
    const string DefaultStorageSize = "100Gi";
    const int DefaultDataNodes = 3;
    const int DefaultMasterNodes = 3;
    const int DefaultCoordinatingNodes = 0;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>One entry of <c>spec.nodePools</c>.</summary>
    /// <remarks>
    ///     ⚠ <c>resources</c> carries <c>requests</c> and <c>limits</c> at the same values, which is
    ///     Guaranteed QoS. An OpenSearch node whose memory limit exceeds its request is a node the
    ///     kubelet may evict while its JVM heap — sized from the <i>limit</i> — is perfectly happy,
    ///     and a search cluster that loses a data node to eviction re-replicates every shard on it.
    /// </remarks>
    static JsonObject Pool(
        string component,
        int replicas,
        string[] roles,
        string diskSize,
        string cpu,
        string memory,
        string storageClass
    ) {
        var pool = new JsonObject {
            ["component"] = component,
            ["replicas"] = replicas,
            ["diskSize"] = diskSize,
            ["roles"] = new JsonArray([.. roles.Select(x => (JsonNode)JsonValue.Create(x))])
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            pool["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory },
                ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory }
            };
        }

        if (storageClass.Length > 0) {
            // ⚠ `persistence.pvc.storageClass`, not `storageClassName`. PVCSource spells the JSON tag
            // `storageClass` while the Go field is StorageClassName, and PersistenceConfig embeds
            // PersistenceSource with `json:","` — so an entry written at the wrong depth or under the
            // wrong key is silently dropped by the API server's pruning and the pool lands on the
            // cluster's default class.
            pool["persistence"] = new JsonObject {
                ["pvc"] = new JsonObject {
                    ["storageClass"] = storageClass,
                    ["accessModes"] = new JsonArray { "ReadWriteOnce" }
                }
            };
        }

        return pool;
    }

    static bool RolesMatch(JsonArray? found, JsonArray expected) =>
        found is not null
        && found.Count == expected.Count
        && expected.All(
            x => found.Any(y => y?.GetValue<string>() == x!.GetValue<string>())
        );

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
