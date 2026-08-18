using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.ContainerService/managedClusters</c> — the first
///     resource type in this platform whose product is a Kubernetes API server.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE RESOURCE'S PRODUCT IS A KUBERNETES API SERVER, AND EVERY OTHER SENTENCE ON THIS
///         TYPE FOLLOWS FROM THAT.</b> The nine provider families before this one render objects into
///         a cluster the platform owns and the tenant never sees; what they sell is a workload. This
///         one renders objects into the <i>management</i> cluster whose effect is a second cluster,
///         and what it sells is that cluster's API. Three consequences, each of which is a decision
///         somewhere below rather than an observation:
///     </para>
///     <list type="number">
///         <item>
///             <b><see cref="Matches" /> is a claim about the REQUEST, not about the product.</b> It
///             compares the three objects this provider applied against the body it rendered them
///             from. Every one of those can be perfect while the tenant has no cluster at all,
///             because the thing that turns them into a cluster is three controllers this platform
///             does not run. <see cref="Readiness" /> is the other half and it reads <c>status</c>.
///         </item>
///         <item>
///             <b>Drift means two different things and only one of them is detectable here.</b> A
///             hand edit to the <c>Cluster</c> object is drift this provider corrects. A tenant
///             wrecking their own cluster from inside — deleting a CNI, cordoning every node — is not
///             drift at all: the resource is exactly as asked for. docs/plan/09 § Cluster connections
///             puts the health of the produced cluster on the <i>connection</i>, which is a different
///             object with a different owner.
///         </item>
///         <item>
///             <b><see cref="ReconcileOutcome.Converged" /> is the weakest of the three claims and is
///             the one the platform reports.</b> See <c>ManagedClusterReconciler</c> and
///             <c>charts/managed/kubernetes/conformance.yaml § owed</c>, <c>converged-is-not-ready</c>.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>docs/plan/13 AND docs/plan/09 DISAGREE ABOUT WHETHER A BYO CLUSTER IS THIS TYPE, AND
///         THIS TAKES docs/plan/09's SIDE.</b> docs/plan/13 § Managed Kubernetes puts two flavours
///         behind one resource type — <c>kind: Managed</c> and <c>kind: Connected</c> — and says a
///         <c>Connected</c> cluster <i>"returns <c>null</c> for node-pool operations rather than
///         pretending"</i>. docs/plan/09 § Cluster connections spells them as two paths in one
///         sentence: <i>"<c>CyberCloud.ContainerService/managedClusters</c> for ours,
///         <c>/connectedClusters</c> for theirs"</i>. Three platform facts decide it, and none of them
///         is a preference:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A discriminated body is not expressible.</b> <see cref="ResourceSchema" /> validates
///             each property against constants — <c>Required</c>, <c>AllowedValues</c>,
///             <c>Minimum</c> — and has no conditional. A <c>kind</c> field would make
///             <c>network.podCidr</c> required for one flavour and meaningless for the other, and the
///             API would accept a <c>Connected</c> body carrying a pod CIDR and silently ignore it.
///         </item>
///         <item>
///             ⚠ <b>The quota meters would be conditional, and a conditional meter is undeclarable —
///             fourth sighting.</b> <c>charts/managed/nats/conformance.yaml</c> established that
///             <c>QuotaGrain.TryReserveAsync</c> refuses a non-positive amount (<i>"A reservation must
///             be positive; 0 is not"</i>), so a meter that derives zero on an ordinary body refuses
///             every such create. A <c>Connected</c> cluster consumes no vCPU and no memory in this
///             platform, so every derived meter here would be zero for it.
///         </item>
///         <item>
///             <b>"Returns <c>null</c> for node-pool operations" has nowhere to live.</b> Node pools
///             are a child resource <i>type</i>, not an operation: a PUT to
///             <c>…/managedClusters/{name}/agentPools/{pool}</c> either creates a resource or answers
///             <c>404</c>. There is no third answer, and a type that accepted the create and did
///             nothing would be the pretending that sentence forbids.
///         </item>
///     </list>
///     <para>
///         So <c>connectedClusters</c> is a resource type this provider does <b>not</b> declare, and
///         its absence is scope rather than a blocker — <c>conformance.yaml § owed</c>,
///         <c>connected-clusters-is-a-second-type</c>.
///     </para>
///     <para>
///         ⚠ <b>THE CLUSTER THIS RESOURCE IS PLACED INTO IS NOT THE CLUSTER THIS RESOURCE IS.</b>
///         <see cref="ClusterIdPointer" /> names the <i>management</i> cluster — the one whose API
///         server accepts the three objects below. What comes out is a second cluster with a
///         kubeconfig of its own, and ⚠ <b>nothing in this platform turns that kubeconfig into a
///         connection</b>: <c>IClusterConnectionGrain.AttachAsync</c> exists and is called by no
///         shipping code anywhere in the tree. So a tenant can create a cluster and cannot then place
///         a Postgres server in it, which is the M1 exit story's fourth step. <c>conformance.yaml
///         § owed</c>, <c>the-cluster-this-creates-is-not-connectable</c>.
///     </para>
/// </remarks>
public static class ManagedClusters {
    /// <summary>The provider namespace — docs/plan/01 § The catalogue and docs/plan/13 both spell it.</summary>
    public const string ProviderNamespace = "CyberCloud.ContainerService";

    /// <summary>The type path.</summary>
    public const string TypePath = "managedClusters";

    /// <summary>The one api-version.</summary>
    /// <remarks>
    ///     ⚠ It has to equal the <c>cybercloud.io/api-version</c> annotation in
    ///     <c>charts/managed/kubernetes/Chart.yaml</c>, and — because <see cref="AgentPools" /> is a
    ///     child — the child's as well. A parent and a child served at different dates would make
    ///     <c>…/managedClusters/{c}/agentPools/{p}?api-version=…</c> a request whose single version
    ///     parameter has to mean two things.
    /// </remarks>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    /// <remarks>
    ///     ⚠ <b><c>charts/managed/</c> rather than the <c>charts/tenant-cluster/</c> that
    ///     charts/README.md's own directory sketch names, and that sketch is corrected rather than
    ///     honoured.</b> <c>Build.Charts</c> requires <c>SOURCE</c>, <c>conformance.yaml</c> and both
    ///     <c>cybercloud.io/*</c> annotations only for a chart under <c>charts/managed/</c>, so a chart
    ///     outside it is a managed service that quietly owes no conformance manifest — which is
    ///     docs/plan/12 § The pattern, once's eighth piece, dropped by a directory name. A managed
    ///     Kubernetes cluster is a catalogue row like every other.
    /// </remarks>
    public const string ChartName = "managed/kubernetes";

    /// <summary>The pointer <c>RequiresCluster</c> names — the MANAGEMENT cluster.</summary>
    /// <remarks>See this class's remarks: the cluster it is placed into is not the cluster it is.</remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands back a kubeconfig.</summary>
    /// <remarks>
    ///     ⚠ <b>Declared with no handler, exactly as the nine <c>listKeys</c>-shaped actions before it
    ///     are, and <see cref="IResourceTypeBuilder" /> is why.</b> <c>Action</c> takes a name, a kind,
    ///     a permission, a request shape and a response shape, and <b>no handler delegate</b> — so no
    ///     action in this platform can run, on any type. Building a private mechanism for this one
    ///     would be a tenth type's worth of surface that the dispatcher, when it lands, would have to
    ///     un-build. <c>conformance.yaml § owed</c>, <c>listcredentials-has-no-handler</c>.
    ///     <para>
    ///         ⚠ <b>docs/plan/13 asks for a short-lived, scoped credential and NOT the admin one</b> —
    ///         <i>"a <c>listCredentials</c> action returning a short-lived, scoped kubeconfig — never
    ///         the admin one"</i>. What Cluster API writes is a <c>Secret</c> named
    ///         <see cref="KubeconfigSecretName" /> holding exactly the admin kubeconfig, signed by the
    ///         cluster CA with no expiry a client can see. Turning that into what docs/plan/13 asks for
    ///         is a certificate request against the produced cluster, which needs a connection to it —
    ///         the same missing write this class's remarks name. Both halves are owed and they are the
    ///         same item.
    ///     </para>
    /// </remarks>
    public const string ListCredentialsAction = "listCredentials";

    /// <summary>The permission <see cref="ListCredentialsAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own permission rather than <c>read</c>, and this is the strongest case in the
    ///     catalogue for the distinction.</b> docs/plan/07 § Consistency puts a key export in the
    ///     fully-consistent row by name. What leaves through this action is not a password to one
    ///     database — it is <c>cluster-admin</c> on a whole Kubernetes cluster, which is every workload
    ///     in it, every Secret in it and the ability to schedule a privileged pod on any node. A caller
    ///     who may list clusters is not, by that fact, a caller who may own one.
    /// </remarks>
    public const string ListCredentialsPermission = "listCredentials";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The three objects a cluster IS ────────────────────────────────────────────────────────
    //
    // ⚠ THREE OBJECTS IN THREE API GROUPS, FROM THREE SEPARATE UPSTREAM PROJECTS, AND THE LAST ONE
    // NAMES THE OTHER TWO. CyberCloud.Analytics/clickhouseClusters was the first type to render two
    // custom resources in two groups from ONE operator binary; this renders three, from Cluster API
    // core, the Kamaji control-plane provider and the KubeVirt infrastructure provider. What binds
    // them is Cluster API's PROVIDER CONTRACT: a `Cluster` names a control plane and an
    // infrastructure by API GROUP, kind and name, and CAPI's own controller resolves them.
    //
    // ⚠ SO A TYPO IN A REF IS NOT AN APPLY ERROR. All three applies succeed, the Cluster is admitted,
    // and CAPI reports a condition on an object nothing in this platform reads. That is the same
    // hazard ClickHouse's zookeeper.nodes pointer has, one level up, and it is why
    // ManagedClusterReconcilerTests pins both refs against the objects' own names.
    //
    // ⚠ AND THE VERSION IN A REF IS NOT WRITTEN DOWN AT ALL, WHICH IS NEW AS OF CLUSTER API v1beta2.
    // A v1beta1 `Cluster` used a corev1.ObjectReference and named `apiVersion`; v1beta2's
    // ContractVersionedObjectReference is `{apiGroup, kind, name}` and CAPI infers the version from a
    // LABEL ON THE PROVIDER'S OWN CRD (`cluster.x-k8s.io/<contract>: <version>`). See
    // charts/managed/kubernetes/conformance.yaml § owed, `capk-crd-carries-the-v1beta1-contract-label`
    // — that label is the one upstream fact most likely to break this row, and it is not ours.

    /// <summary>The Cluster API <c>Cluster</c> — the object that stitches the other two together.</summary>
    /// <remarks>
    ///     ⚠ <b><c>v1beta2</c>, and taking <c>v1beta1</c> instead would have been the wrong kind of
    ///     safe.</b> Checked against the CRD rather than against a template:
    ///     <c>config/crd/bases/cluster.x-k8s.io_clusters.yaml</c> marks <c>v1beta1</c>
    ///     <c>deprecated: true</c>, <c>storage: false</c>, and Cluster API v1.14.0's release notes say
    ///     it <i>"is on track to be unserved in CAPI v1.16"</i>. ⚠ The Kamaji control-plane provider's
    ///     own reference template still emits <c>v1beta1</c> while its controller imports the v1beta2
    ///     Go package, so copying that template would have pinned this platform to a version that
    ///     stops being served — <c>charts/managed/kubernetes/SOURCE</c> records the discrepancy.
    /// </remarks>
    public static GroupVersionKind ClusterKind { get; } =
        new() { Group = "cluster.x-k8s.io", Version = "v1beta2", Kind = "Cluster", Plural = "clusters" };

    /// <summary>The Kamaji control plane — the tenant's API server, as pods in the management cluster.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the object the tenant is buying.</b> ADR-009: <i>"control plane components as
    ///     pods in the management cluster"</i>. It is owned by
    ///     <c>clastix/cluster-api-control-plane-provider-kamaji</c> rather than by <c>clastix/kamaji</c>
    ///     itself — the second project serves <c>TenantControlPlane</c>, which this provider does not
    ///     render, because the control-plane provider creates one from a <c>KamajiControlPlane</c> and
    ///     a resource type that rendered both would be fighting its own controller.
    ///     <para>
    ///         ⚠ <b><c>v1alpha2</c>, not <c>v1alpha1</c>.</b> The provider's CRD at v0.20.0 serves
    ///         exactly one version and it is <c>v1alpha2</c>; there is no <c>v1alpha1</c> to fall back
    ///         to. That release is also the first whose <c>metadata.yaml</c> claims the
    ///         <c>v1beta2</c> Cluster API contract, which is what makes <see cref="ClusterKind" />'s
    ///         choice consistent rather than merely newer.
    ///     </para>
    /// </remarks>
    public static GroupVersionKind ControlPlaneKind { get; } =
        new() {
            Group = "controlplane.cluster.x-k8s.io",
            Version = "v1alpha2",
            Kind = "KamajiControlPlane",
            Plural = "kamajicontrolplanes"
        };

    /// <summary>The KubeVirt infrastructure — where the worker VMs will be created.</summary>
    /// <remarks>
    ///     ⚠ <b>It creates no machine, and — because of
    ///     <see cref="ExternallyManagedAnnotation" /> — it creates nothing at all.</b> A
    ///     <c>KubevirtCluster</c> is the infrastructure <i>side</i> of the Cluster API contract; the
    ///     VMs are <see cref="AgentPools" />', which is the structural reason node pools are a child
    ///     resource here rather than an array property.
    /// </remarks>
    public static GroupVersionKind InfrastructureKind { get; } =
        new() {
            Group = "infrastructure.cluster.x-k8s.io",
            Version = "v1alpha1",
            Kind = "KubevirtCluster",
            Plural = "kubevirtclusters"
        };

    /// <summary>The annotation that tells the KubeVirt provider to keep its hands off.</summary>
    /// <remarks>
    ///     ⚠ <b>ONE ANNOTATION DECIDES WHICH OF TWO CONTROLLERS OWNS THE CONTROL-PLANE ENDPOINT, AND
    ///     WITHOUT IT BOTH DO.</b> Read in the KubeVirt provider's own controller rather than in its
    ///     README: <c>controllers/kubevirtcluster_controller.go</c> returns immediately for an
    ///     externally-managed cluster, so it creates no load-balancer <c>Service</c>, generates no SSH
    ///     key <c>Secret</c> and never sets <c>status.ready</c>. The Kamaji control-plane provider then
    ///     patches <c>spec.controlPlaneEndpoint</c> and <c>status.ready</c> onto this object itself —
    ///     and its patcher is a <b>hardcoded switch over infrastructure kinds</b> in which
    ///     <c>KubevirtCluster</c> is a listed case. Drop this annotation and two controllers both
    ///     believe they own the endpoint; the symptom is a control-plane address that flips.
    /// </remarks>
    public const string ExternallyManagedAnnotation = "cluster.x-k8s.io/managed-by";

    /// <summary>Who manages a <c>KubevirtCluster</c> this provider renders.</summary>
    public const string ExternallyManagedBy = "kamaji";

    /// <summary>The name of the <c>KamajiControlPlane</c> a cluster renders.</summary>
    /// <remarks>
    ///     ⚠ <b>Suffixed, which the other two are not, and it follows Cluster API's own quickstart
    ///     rather than this platform's taste.</b> Every CAPI template in the wild names the control
    ///     plane <c>{cluster}-control-plane</c>, and an operator reading
    ///     <c>kubectl get kamajicontrolplanes</c> against a cluster full of ours should see the shape
    ///     they expect.
    /// </remarks>
    /// <param name="name">The resource's own name.</param>
    public static string ControlPlaneName(string name) => name + "-control-plane";

    /// <summary>The <c>Secret</c> Cluster API writes the admin kubeconfig into.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing this provider applies creates it, and nothing this provider runs reads it.</b>
    ///     The name is Cluster API's convention — <c>{cluster}-kubeconfig</c>, in the cluster's own
    ///     namespace — and it is spelled here because <see cref="ListCredentialsAction" />'s handler
    ///     will need it and because a name nobody wrote down is a name the handler's author will guess.
    /// </remarks>
    /// <param name="name">The resource's own name.</param>
    public static string KubeconfigSecretName(string name) => name + "-kubeconfig";

    /// <summary>
    ///     The scheme a cluster's credential reference is written in — a <c>Secret</c> in the
    ///     management cluster rather than a path in a vault.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A second scheme, because the credential is in a place a vault path cannot name.</b>
    ///     Every other <c>ClusterConnectionDescriptor.CredentialRef</c> in this platform points into
    ///     OpenBao. Cluster API writes this one itself, into a <c>Secret</c> in the management
    ///     cluster's own namespace, and nothing copies it out — so a ref that pretended to be a vault
    ///     path would name a path with nothing at it.
    /// </remarks>
    public const string KubeconfigRefScheme = "kube-secret";

    /// <summary>
    ///     Where a created cluster's kubeconfig lives, as a <c>ClusterConnectionDescriptor</c>
    ///     credential reference.
    /// </summary>
    /// <param name="ns">The management-cluster namespace the resource's objects are in.</param>
    /// <param name="name">The resource's own name.</param>
    /// <returns><c>kube-secret://{namespace}/{cluster}-kubeconfig#value</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NOTHING RESOLVES THIS YET, AND THAT IS RECORDED RATHER THAN IMPLIED.</b>
    ///         <c>IKubeApiClientFactory</c> has a case for
    ///         <c>ClusterConnectionKind.InHouse</c> and resolves its credential through
    ///         <c>ISecretResolver</c>, which reads a vault. A resolver that reads a <c>Secret</c> out
    ///         of the <i>management</i> cluster is the missing half — see
    ///         <c>charts/managed/kubernetes/conformance.yaml § owed</c>,
    ///         <c>the-cluster-this-creates-is-not-connectable</c>, item (b). Attaching the connection
    ///         is item (a) and is built: the descriptor is registered, the cluster is addressable by
    ///         <c>clusterId</c>, and the first call through it fails on the credential with a message
    ///         naming this scheme rather than failing on "no connection" with nothing to act on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The key is <c>value</c>, which is Cluster API's, not ours.</b> The
    ///         <c>Secret</c> it writes carries the whole kubeconfig under a single <c>value</c> key.
    ///     </para>
    /// </remarks>
    public static string KubeconfigCredentialRef(string ns, string name) =>
        $"{KubeconfigRefScheme}://{ns}/{KubeconfigSecretName(name)}#value";

    /// <summary>
    ///     The API server URL a ready cluster reports, or an empty string when no controller has
    ///     written one.
    /// </summary>
    /// <param name="objectJson">The <c>Cluster</c> object's JSON, as the API server returned it.</param>
    /// <remarks>
    ///     ⚠ <b>Read from <c>spec.controlPlaneEndpoint</c>, which the Kamaji control-plane provider
    ///     patches onto the <c>Cluster</c> itself</b> — see
    ///     <see cref="ExternallyManagedAnnotation" /> for why exactly one controller owns that field
    ///     and what happens when two do. An empty answer means the endpoint has not been assigned, and
    ///     a connection registered against an empty endpoint would be one every later call fails on,
    ///     so the reconciler does not report a cluster it cannot name an address for.
    /// </remarks>
    public static string ApiServerEndpoint(string objectJson) {
        if (Document(objectJson)?["spec"] is not JsonObject spec) {
            return string.Empty;
        }

        if (spec["controlPlaneEndpoint"] is not JsonObject endpoint) {
            return string.Empty;
        }

        var host = endpoint["host"]?.GetValue<string>() ?? string.Empty;
        var port = endpoint["port"]?.GetValue<int>() ?? 0;

        return string.IsNullOrWhiteSpace(host) || port <= 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"https://{host}:{port}");
    }

    /// <summary>The <c>Cluster</c> a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ClusterRef(string ns, string name) =>
        new() { Kind = ClusterKind, Namespace = ns, Name = name };

    /// <summary>The <c>KamajiControlPlane</c> a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ControlPlaneRef(string ns, string name) =>
        new() { Kind = ControlPlaneKind, Namespace = ns, Name = ControlPlaneName(name) };

    /// <summary>The <c>KubevirtCluster</c> a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef InfrastructureRef(string ns, string name) =>
        new() { Kind = InfrastructureKind, Namespace = ns, Name = name };

    // ── What the platform owns rather than the tenant ─────────────────────────────────────────

    /// <summary>The Kamaji <c>DataStore</c> every tenant control plane is backed by.</summary>
    /// <remarks>
    ///     ⚠ <b>ONE, SHARED, PLATFORM-OWNED — AND ADR-009 SAYS "a dedicated etcd per tenant from
    ///     etcd-operator". THIS DOES NOT DO THAT, AND THE REASON IS STRUCTURAL RATHER THAN
    ///     ECONOMIC.</b> Checked in the CRD: <c>kamaji.clastix.io_datastores.yaml</c> is
    ///     <c>scope: Cluster</c>. Every object this platform applies is namespaced, carries ADR-013's
    ///     seven labels and lives inside <c>{subscriptionId:N}-{resourceGroup}</c>; a cluster-scoped
    ///     object has no namespace to be isolated by, so two tenants' clusters would compete for one
    ///     name in one flat space and the platform's whole tenancy story would rest on a naming
    ///     convention. This provider therefore <b>names</b> a DataStore the platform installed and
    ///     <b>creates none</b>. The name is Kamaji's own chart default — <c>defaultDatastoreName:
    ///     default</c>, with a <c>kamaji-etcd</c> subchart that creates a <c>DataStore</c> called
    ///     <c>default</c> when <c>kamaji-etcd.deploy</c> is on.
    ///     <para>
    ///         ⚠ <b>What that costs, stated rather than left to be discovered:</b> tenant control planes
    ///         share an etcd. Kamaji separates them inside it — each tenant control plane gets its own
    ///         schema or prefix in the datastore — so this is not a data-visibility boundary being
    ///         crossed; it is a <i>blast radius</i> and a <i>noisy neighbour</i> boundary being given
    ///         up. One tenant's write storm is every tenant's control-plane latency, and one etcd's
    ///         corruption is every tenant's cluster. ADR-009's dedicated etcd is the right answer and it
    ///         needs a per-tenant cluster-scoped <c>DataStore</c>, which is the platform's job rather
    ///         than a reconciler's. <c>conformance.yaml § owed</c>,
    ///         <c>datastore-is-shared-and-adr-009-says-it-should-not-be</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is optional in the CRD and mandatory in practice.</b> <c>dataStoreName</c> has
    ///         no <c>+kubebuilder:default</c> and no required marker — but Kamaji's own validating
    ///         webhook <c>Get</c>s the named DataStore and refuses the tenant control plane with
    ///         <i>"&lt;name&gt; DataStore does not exist"</i>. So an install with
    ///         <c>kamaji-etcd.deploy=false</c> fails <b>every</b> cluster create at admission, on the
    ///         object this provider does not apply, with nothing on the <c>Cluster</c> to say so.
    ///     </para>
    /// </remarks>
    public const string DataStoreName = "default";

    /// <summary>The DNS domain of every cluster this type creates.</summary>
    /// <remarks>
    ///     ⚠ <b>RENDERED EXPLICITLY, AND THE REASON IS THAT IT CANNOT BE ADDED LATER.</b> Cluster API
    ///     does <b>not</b> default <c>clusterNetwork.serviceDomain</c> — there is no
    ///     <c>+kubebuilder:default</c> on it and its mutating webhook never touches it — and the Kamaji
    ///     control-plane provider copies whatever is there into the tenant control plane's
    ///     <c>clusterDomain</c>, which carries a CEL rule of <c>self == oldSelf</c> and the message
    ///     <i>"changing the cluster domain is not supported"</i>. So an empty value takes Kamaji's own
    ///     default and then <b>freezes</b>, and a later api-version that wanted to write
    ///     <c>cluster.local</c> explicitly would be refused on every existing cluster. Writing it at
    ///     creation costs one line; not writing it is unrecoverable.
    ///     <para>
    ///         It is not a tenant property because it is not a decision a tenant can make well: every
    ///         chart, operator and sidecar in the ecosystem assumes <c>cluster.local</c>, and a cluster
    ///         that does not use it breaks software the platform did not write.
    ///     </para>
    /// </remarks>
    public const string ServiceDomain = "cluster.local";

    /// <summary>What one control-plane container of a tenant control plane costs.</summary>
    /// <remarks>
    ///     ⚠ <b>Platform-chosen, not tenant-chosen, and that is what makes this type's quota a SUM over
    ///     two populations the tenant sizes differently.</b> A tenant picks how many control-plane
    ///     replicas they want and cannot pick how big one is: a Kubernetes API server's working set is
    ///     a function of the objects in it rather than of anything on this form, and offering a
    ///     "control plane size" would be offering a number nobody can choose correctly. It has to agree
    ///     with <c>charts/managed/kubernetes/templates/_helpers.tpl</c>, and
    ///     <c>ManagedClusterSizingTests</c> is what says so.
    /// </remarks>
    public const string ControlPlaneCpu = "500m";

    /// <inheritdoc cref="ControlPlaneCpu" />
    public const string ControlPlaneMemory = "1Gi";

    /// <summary>How many containers one control-plane replica really is.</summary>
    /// <remarks>
    ///     ⚠ <b>THREE, AND METERING ONE WOULD UNDER-RESERVE BY TWO THIRDS.</b> A Kamaji control-plane
    ///     replica is <c>kube-apiserver</c>, <c>kube-controller-manager</c> and <c>kube-scheduler</c> —
    ///     ADR-009 in as many words, <i>"a control-plane pod set"</i> — and the CRD takes a separate
    ///     component block for each. <see cref="ControlPlaneCpu" /> is per <i>container</i>, so the
    ///     meter multiplies by this. A derivation that read <c>replicas</c> alone would be right about
    ///     the object and wrong about the cluster, and it would provision, read back and converge.
    /// </remarks>
    public const int ControlPlaneContainersPerReplica = 3;

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ <b>Pointed at <see cref="KubeQuantity" /> rather than copied</b>, for the reason
    ///     <c>QuantityParserTests</c> enforces: four providers kept their own copy of this grammar and
    ///     one of them grew a second parser next to it.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <summary>An IPv4 CIDR block, whole-value.</summary>
    /// <remarks>
    ///     ⚠ <b>It checks SHAPE and not RANGE, and the difference is where the real mistakes are.</b>
    ///     <c>10.244.0.0/16</c> and <c>10.244.0.0/33</c> both look like CIDRs and this pattern refuses
    ///     the second; what it cannot refuse is a pod CIDR that <i>overlaps the management cluster's
    ///     own</i>, which is the failure that produces a cluster whose nodes route the platform's
    ///     addresses to themselves. That is a fact about the cluster rather than about the body —
    ///     <c>charts/managed/seaweedfs/conformance.yaml</c>'s <c>replication-versus-topology</c> is the
    ///     same shape — and it is owed as <c>cidrs-are-not-checked-against-the-management-cluster</c>.
    /// </remarks>
    public const string CidrPattern =
        @"((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)/([0-9]|[12]\d|3[0-2])";

    /// <summary>The Kubernetes minor versions this api-version offers a control plane at.</summary>
    /// <remarks>
    ///     ⚠ <b>Two, and adding a third is a new api-version rather than an edit.</b> docs/plan/08
    ///     § The provider registry makes a published version immutable, and a tenant whose
    ///     <c>AllowedValues</c> grew under them is a tenant whose stored body stopped being the body
    ///     they sent.
    ///     <para>
    ///         ⚠ <b>THE API TAKES A MINOR AND THE OBJECT NEEDS A FULL SEMVER, WHICH IS WHY
    ///         <see cref="PinnedPatch" /> EXISTS.</b> Kamaji validates a tenant control plane's version
    ///         with a semantic-version comparison against its own bundled kubeadm, and a
    ///         two-component <c>v1.33</c> is not a semantic version. So the platform pins the patch. A
    ///         tenant-visible patch would be worse than the pin in both directions: a tenant pinned to
    ///         <c>1.33.2</c> cannot be moved to <c>1.33.3</c> when it fixes a CVE, and a tenant choosing
    ///         freely can pick a patch this platform has never run.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> Versions { get; } = ["1.32", "1.33"];

    /// <summary>The newest offered control-plane version — what a body that says nothing gets.</summary>
    public const string DefaultVersion = "1.33";

    /// <summary>The full version each offered minor is rendered as.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>REVIEWED 2026-08-18, AND BOTH STRINGS WERE WRONG — NOT AS KUBERNETES VERSIONS BUT
    ///         AS IMAGE TAGS.</b> The previous pins were <c>v1.32.9</c> and <c>v1.33.4</c>. Both are
    ///         real Kubernetes releases: 1.32 ended at 1.32.13 and 1.33 is at 1.33.13. Neither is a tag
    ///         of <see cref="AgentPools.NodeImageRepository" />, which publishes exactly four —
    ///         <c>v1.31.5</c>, <c>v1.32.1</c>, <c>v1.33.5</c>, <c>v1.34.1</c>, read off
    ///         <c>quay.io</c> on 2026-08-18. So every worker VM in every pool pulled a tag that does
    ///         not exist, and the reason arrived in a <c>DataVolume</c>'s events, three objects below
    ///         anything this platform reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE NODE-IMAGE REGISTRY IS THE BINDING CONSTRAINT, NOT THE KUBERNETES RELEASE
    ///         PAGE, AND THAT INVERTS WHAT <see cref="AgentPools.NodeImageRepository" /> ASSUMED.</b>
    ///         That constant's remarks said the KubeVirt provider publishes an image "tagged with the
    ///         Kubernetes version they carry", so the tag was "a function of <c>PinnedPatch</c>". It
    ///         publishes one tag per MINOR, at whichever patch it happened to build. A pin is
    ///         therefore only renderable if it is a patch that repository has published, which leaves
    ///         one candidate per offered minor and these are they.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>WHAT THAT COSTS, SAID RATHER THAN HIDDEN: <c>v1.32.1</c> WAS BUILT IN JANUARY
    ///         2025</b> and carries every fix that landed in 1.32.2 through 1.32.13. The alternative
    ///         is a newer control plane with no bootable node image, which is a cluster with no nodes
    ///         — a worse failure and a silent one. ⚠ And both offered minors are out of upstream
    ///         support: 1.32 since 2026-02-28, 1.33 since 2026-06-28. Fixing THAT is a new
    ///         api-version rather than a pin move, because <see cref="Versions" /> is an
    ///         <c>AllowedValues</c> a stored body carries — <c>conformance.yaml § owed</c>,
    ///         <c>offered-minors-are-out-of-support</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ Still unchecked, and it needs a running management cluster rather than a registry
    ///         read: whether a pin is newer than the Kamaji install's bundled kubeadm, which its
    ///         version webhook refuses with a message about a version nobody typed.
    ///         <c>conformance.yaml § owed</c>, <c>pinned-patches-are-unreviewed</c>. ⚠ Moving a pin is
    ///         a chart version bump rather than a new api-version, because a patch is not an API
    ///         change and no stored body carries one.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, string> PinnedPatch { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["1.32"] = "v1.32.1", ["1.33"] = "v1.33.5"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     How many minor versions a node may lag its control plane, per Kubernetes' own version-skew
    ///     policy.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THREE, AND docs/plan/13 SAYS "at most one minor" ABOUT A DIFFERENT THING.</b> That
    ///     document's sentence is <i>"the control plane must be upgraded first and by at most one
    ///     minor"</i>, which is the rule for the control plane's <b>own step size</b> — and it is real:
    ///     both Cluster API's topology webhook and Kamaji's own version webhook refuse a two-minor jump
    ///     and refuse a downgrade. The <i>kubelet</i> rule is separate and is <b>three</b> minors below
    ///     the API server, per the Kubernetes version-skew policy, which moved from two to three in
    ///     Kubernetes 1.28. <see cref="SkewIsLegal" /> implements the kubelet rule and
    ///     <see cref="UpgradeIsLegal" /> the step-size one, because they answer different questions and
    ///     a single function would have to be asked which.
    ///     <para>
    ///         ⚠ <b>AND NEITHER IS ENFORCED BY THIS PLATFORM, WHICH IS A REFUTATION OF docs/plan/13
    ///         RATHER THAN AN OMISSION.</b> That document says <i>"the API enforces it with a clear
    ///         error, rather than letting a tenant break their cluster and open a ticket"</i>. It
    ///         cannot: a node pool's version lives in a <b>different resource</b> from its cluster's,
    ///         and <see cref="ResourceSchema" /> validates one body against constants — it never sees
    ///         another resource. This is the seam
    ///         <c>charts/managed/seaweedfs-bucket/conformance.yaml</c> records as
    ///         <c>bucket-cluster-may-differ-from-its-accounts</c>; this is its second sighting and the
    ///         first where the consequence is a broken cluster rather than an unreconciled object. Both
    ///         functions exist, are tested, and are <b>called by nothing on the write path</b> —
    ///         deliberately, so that closing the seam is wiring an existing function rather than
    ///         writing one. <c>conformance.yaml § owed</c>, <c>version-skew-is-not-enforced</c>.
    ///     </para>
    /// </remarks>
    public const int MaxKubeletMinorsBehind = 3;

    /// <summary>Whether a node version is legal against a control-plane version.</summary>
    /// <param name="controlPlane">The control plane's minor, <c>1.33</c>.</param>
    /// <param name="node">The node pool's minor, <c>1.30</c>.</param>
    /// <returns><see langword="true" /> when Kubernetes' skew policy permits the pair.</returns>
    /// <remarks>
    ///     ⚠ <b>A node NEWER than its control plane is illegal and is the mistake this catches.</b> The
    ///     skew policy is one-directional: a kubelet may lag by three minors and may never lead by one.
    ///     A tenant upgrading a node pool first — which is the natural order if nobody says otherwise —
    ///     gets nodes that register and then fail in ways that read as a platform fault.
    /// </remarks>
    public static bool SkewIsLegal(string controlPlane, string node) {
        if (!TryMinor(controlPlane, out var major, out var controlPlaneMinor)
            || !TryMinor(node, out var nodeMajor, out var nodeMinor)
            || major != nodeMajor) {
            return false;
        }

        var behind = controlPlaneMinor - nodeMinor;
        return behind >= 0 && behind <= MaxKubeletMinorsBehind;
    }

    /// <summary>Whether a control-plane version change is a legal step.</summary>
    /// <param name="from">The version now.</param>
    /// <param name="to">The version asked for.</param>
    /// <returns><see langword="true" /> for no change or exactly one minor forward.</returns>
    /// <remarks>
    ///     ⚠ <b>Downgrades are refused, and that is upstream's rule rather than a policy invented
    ///     here</b> — Kamaji's version webhook rejects a lower version outright and rejects a jump of
    ///     more than one minor as <i>"a minor version in a non-sequential mode"</i>. Enforcing it at
    ///     the API would turn an admission failure on an object the tenant never sees into a refusal on
    ///     the request they made.
    /// </remarks>
    public static bool UpgradeIsLegal(string from, string to) {
        if (!TryMinor(from, out var major, out var fromMinor)
            || !TryMinor(to, out var toMajor, out var toMinor)
            || major != toMajor) {
            return false;
        }

        var step = toMinor - fromMinor;
        return step is 0 or 1;
    }

    static bool TryMinor(string version, out int major, out int minor) {
        major = 0;
        minor = 0;

        var parts = version.Split('.');

        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>The sizing presets a control plane is NOT sized by, and a node pool is.</summary>
    /// <remarks>
    ///     ⚠ <b>It lives here rather than on <see cref="AgentPools" /> because both types describe it</b>
    ///     — and because a second copy in the same assembly is the drift
    ///     <c>ClickHouseClusters.Presets</c>' remarks argue against across assemblies.
    ///     <para>
    ///         ⚠ <b><c>s1</c>, and the values are <c>StorageAccounts.Presets</c>' rather than
    ///         <c>PostgresServers.Presets</c>'.</b> docs/plan/12 § Sizing vocabulary calls <c>s1.*</c>
    ///         <i>"1:4 · General — most databases"</i>, which is also the ratio every cloud's default
    ///         Kubernetes node SKU has. The two shipped spellings of <c>s1</c> differ by one rung and
    ///         <c>PostgresServers.Presets["s1.nano"]</c> is <c>(100m, 512Mi)</c> — 5 GiB per core, on
    ///         that rung and no other. This takes the ratio-correct one, which is the third type to do
    ///         so, and <c>ManagedClusterDeclarationTests</c> pins the ratio rather than the copy.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AND HERE A PRESET IS A VM RATHER THAN A CONTAINER, WHICH NOTHING BEFORE THIS HAS
    ///         BEEN.</b> Every earlier provider turns a preset into <c>resources.requests</c> on a pod.
    ///         A node pool turns it into the <b>name of a KubeVirt instancetype</b> — see
    ///         <see cref="AgentPools.InstancetypeName" /> — so the numbers below are what the platform
    ///         BELIEVES that instancetype to be, and nothing checks the belief. A
    ///         <c>VirtualMachineClusterInstancetype</c> is cluster-scoped, is created by the bundle, and
    ///         is not this provider's to render.
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

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO EXPOSURE PROPERTY AT ALL, AND THE API SERVER IS THEREFORE REACHABLE ONLY FROM
    ///         INSIDE THE MANAGEMENT CLUSTER. THIS IS THE LARGEST DECISION ON THE TYPE.</b>
    ///         docs/plan/12 § Cross-cutting decisions requires an explicit CIDR allow-list on any
    ///         exposure, and there is nowhere upstream to render one: the KubeVirt provider's
    ///         <c>ServiceSpecTemplate</c> has exactly one field, <c>type</c> — no ports, no selector and
    ///         no <c>loadBalancerSourceRanges</c> — and Kamaji's <c>NetworkComponent</c> offers
    ///         <c>serviceAnnotations</c> and <c>serviceLabels</c> but no source ranges either. So a
    ///         <c>LoadBalancer</c> here would be an unrestricted public Kubernetes API server, which is
    ///         the single worst object this platform could hand out. <c>serviceType</c> is rendered as
    ///         <c>ClusterIP</c> instead, and the resulting cluster is <b>secure and unreachable</b> —
    ///         the outcome <c>charts/managed/clickhouse</c> already produced for a different reason,
    ///         and the second sighting of an upstream <c>ServiceSpec</c> with nowhere to put a
    ///         firewall (the first was <c>charts/managed/seaweedfs</c>). ⚠ A property that rendered
    ///         nothing would be worse than the absence: <c>conformance.yaml § owed</c>,
    ///         <c>api-server-has-no-restrictable-exposure</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>addons</c>, AND docs/plan/13 ASKS FOR THEM BY NAME.</b> That document's
    ///         sub-resources are <i>"<c>agentPools</c>, <c>credentials</c>, <c>addons</c> (ingress,
    ///         cert-manager, monitoring agents, GPU operator — each a bundle chart the tenant opts
    ///         into)"</i>. An addon is a chart installed <b>into the produced cluster</b>, and the
    ///         platform has no connection to the produced cluster — see this class's remarks. So
    ///         <c>addons</c> is not a property that was left out; it is a property whose mechanism does
    ///         not exist, and shipping a switch that turns nothing on would be worse than the absence.
    ///         <c>conformance.yaml § owed</c>, <c>addons-need-a-connection-to-the-new-cluster</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>subnetId</c>, AND docs/plan/09's OWN STEP TABLE OPENS WITH "Allocate VPC,
    ///         subnet, API VIP".</b> That is <c>CyberCloud.Network/virtualNetworks</c>, which is a
    ///         different provider; a resource id in this body would be the sanctioned cross-provider
    ///         route (rule 2), and it would be a required property pointing at a type that may not be
    ///         in the registry the silo built. <c>conformance.yaml § owed</c>, <c>no-vpc-placement</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>ONE CIDR PER FAMILY, NOT A LIST, AND UPSTREAM IS WHY.</b> Cluster API's
    ///         <c>NetworkRanges.CIDRBlocks</c> takes up to a hundred; the Kamaji control-plane provider
    ///         reads <c>CIDRBlocks[0]</c> and nothing else. Declaring an array here would let a tenant
    ///         ask for dual-stack and get single-stack silently, so the schema takes one string and the
    ///         render wraps it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every default here is the chart's default, spelled as JSON</b> — there is no
    ///         <c>@default</c> directive, because the chart's default <i>is</i> the YAML literal on the
    ///         annotated line.
    ///     </para>
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
                    Description: "The management cluster the control plane runs in. ⚠ This is not the "
                    + "cluster being created: it is the cluster whose API server accepts the Cluster "
                    + "API objects that create one."
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
                    Description: "The Kubernetes minor version of the control plane. The patch level is "
                    + "the platform's. ⚠ Upgrade the control plane before the node pools and by at most "
                    + "one minor at a time; a node pool may run up to three minors behind and may never "
                    + "run ahead. Neither rule is enforced by this API today — an illegal pair is "
                    + "refused by the cluster's own admission, after the create was accepted."
                ) {
                    AllowedValues = Versions,
                    DefaultJson = "\"" + DefaultVersion + "\""
                },
                new(
                    "/properties/controlPlane",
                    SchemaKind.Nested,
                    Description: "The control plane, which runs as pods in the management cluster."
                ),
                new(
                    "/properties/controlPlane/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many copies of the API server, controller manager and scheduler "
                    + "to run. Two survives a node failure; one is offered for development. ⚠ Unlike an "
                    + "etcd quorum this is a plain replica count — the datastore is separate and is "
                    + "shared — so an even number is not the mistake it would be on a Raft member set."
                ) {
                    Minimum = 1,
                    Maximum = 5,
                    DefaultJson = "2"
                },
                new(
                    "/properties/network",
                    SchemaKind.Nested,
                    Description: "The address space of the cluster being created."
                ),
                new(
                    "/properties/network/podCidr",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The CIDR block pods are addressed from. ⚠ It must not overlap the "
                    + "management cluster's own pod or service range, and nothing checks that — an "
                    + "overlap produces a cluster whose nodes route the platform's addresses to "
                    + "themselves."
                ) {
                    Pattern = CidrPattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"10.244.0.0/16\"",
                    ExampleJson = "\"10.244.0.0/16\""
                },
                new(
                    "/properties/network/serviceCidr",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The CIDR block Service cluster IPs are allocated from. The same "
                    + "overlap warning applies."
                ) {
                    Pattern = CidrPattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"10.96.0.0/12\"",
                    ExampleJson = "\"10.96.0.0/12\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the control plane's own metrics endpoints are scraped. On by "
                    + "default — docs/plan/12: \"a managed service the tenant cannot see the health of "
                    + "is a black box they will not trust with production\". ⚠ It covers the control "
                    + "plane, which runs in the management cluster. Nothing scrapes inside the cluster "
                    + "being created; that needs an agent in the bundle."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listCredentials</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Declared even though no handler serves it, because an undeclared response is the one
    ///     part of the API surface with no contract</b> — and on this type the contract is the most
    ///     valuable thing in the file, because what leaves is <c>cluster-admin</c> on a whole cluster.
    /// </remarks>
    public static ResourceSchema ListCredentialsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/kubeconfig",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "A complete kubeconfig for the cluster, YAML. ⚠ docs/plan/13 requires "
                    + "this to be short-lived and scoped rather than the cluster's admin credential; "
                    + "what Cluster API generates is the admin one, and narrowing it needs a "
                    + "certificate request against the cluster this platform cannot yet reach."
                ),
                new(
                    "/apiServerEndpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The URL the kubeconfig points at, https://host:port. ⚠ It is an "
                    + "address inside the management cluster and is not routable from anywhere else — "
                    + "the control plane is deliberately not exposed, because there is no upstream "
                    + "field to attach a CIDR allow-list to. Returned separately so that a caller does "
                    + "not have to parse YAML to discover that."
                ) {
                    Format = SchemaFormat.Uri
                },
                new(
                    "/expiresAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the credential stops working, RFC 3339. ⚠ Returned because a "
                    + "credential with no stated expiry is one every caller will paste into CI and "
                    + "never rotate — docs/plan/13 makes \"a kubectl credential that expires\" part of "
                    + "what a tenant is buying."
                ) {
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The Kubernetes minor a body asks its control plane for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) => Text(desired, "version", DefaultVersion);

    /// <summary>The full version <see cref="Version" /> is rendered as.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>See <see cref="PinnedPatch" />: a minor is not a semantic version and Kamaji parses one.</remarks>
    public static string RenderedVersion(JsonElement desired) =>
        PinnedPatch.TryGetValue(Version(desired), out var pinned) ? pinned : "v" + Version(desired);

    /// <summary>How many control-plane replicas a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int ControlPlaneReplicas(JsonElement desired) =>
        Number(desired, "controlPlane", "replicas", DefaultControlPlaneReplicas);

    /// <summary>The pod CIDR a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PodCidr(JsonElement desired) => Member(desired, "network", "podCidr", DefaultPodCidr);

    /// <summary>The service CIDR a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string ServiceCidr(JsonElement desired) =>
        Member(desired, "network", "serviceCidr", DefaultServiceCidr);

    /// <summary>Whether a body asks for the control plane's metrics to be scraped.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Root(desired, "monitoring") is not { ValueKind: JsonValueKind.Object } section
        || !section.TryGetProperty("enabled", out var value)
        || value.ValueKind is JsonValueKind.True;

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>KubevirtCluster</c> a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>IT HAS NO SPEC WORTH WRITING, AND THAT IS THE FINDING RATHER THAN AN OVERSIGHT.</b>
    ///     <c>KubevirtClusterSpec</c> is four fields — <c>controlPlaneEndpoint</c>,
    ///     <c>controlPlaneServiceTemplate</c>, <c>sshKeys</c>, <c>infraClusterSecretRef</c> — and
    ///     <see cref="ExternallyManagedAnnotation" /> makes the KubeVirt controller skip the object
    ///     entirely, so the endpoint is Kamaji's to write, the service template would render a Service
    ///     nobody creates, the SSH keys are generated by the machine controller per pool, and the
    ///     secret ref is for running VMs in a <i>different</i> cluster than the management one, which
    ///     this platform does not do. What is left is a named object in the right group so that
    ///     <c>Cluster.spec.infrastructureRef</c> resolves — which is the whole of the Cluster API
    ///     infrastructure contract for a hosted control plane.
    ///     <para>
    ///         ⚠ It takes no body at all, and it is still a pure function of the name, so
    ///         <c>Matches</c> has something to compare and the four-clause contract is unaffected.
    ///     </para>
    /// </remarks>
    public static string InfrastructureJson(string name) =>
        new JsonObject {
            ["kind"] = InfrastructureKind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = name,
                ["annotations"] = new JsonObject { [ExternallyManagedAnnotation] = ExternallyManagedBy }
            },
            ["spec"] = new JsonObject()
        }.ToJsonString();

    /// <summary>The <c>KamajiControlPlane</c> a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>dataStoreName</c> IS THE ONE FIELD WITHOUT WHICH NOTHING HAPPENS, AND IT NAMES
    ///         AN OBJECT THIS PROVIDER DOES NOT CREATE.</b> See <see cref="DataStoreName" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>serviceType: ClusterIP</c> OVERRIDES A CRD DEFAULT OF <c>LoadBalancer</c>, AND
    ///         THAT IS THE MOST CONSEQUENTIAL LINE IN THIS FILE.</b> Kamaji defaults
    ///         <c>network</c> to <c>{serviceType: LoadBalancer}</c>, so rendering nothing here would
    ///         publish every tenant's Kubernetes API server on a public address with no allow-list.
    ///         <see cref="Schema2026" />'s remarks carry the whole argument; the short form is that
    ///         there is no upstream field to render an allow-list into and docs/plan/12 § Cross-cutting
    ///         decisions requires one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>addons</c> block.</b> Kamaji offers CoreDNS, kube-proxy and konnectivity as
    ///         control-plane addons, and each of them is software installed <i>inside the produced
    ///         cluster</i>. Leaving the block out takes the provider's own defaults, which is the
    ///         honest position for a platform that cannot see inside that cluster to check the result —
    ///         and it is the position <c>StorageBuckets.BucketJson</c> takes on <c>reclaimPolicy</c>:
    ///         render nothing and let the CRD's default stand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three component blocks carry the same figures and are written three times.</b>
    ///         <c>apiServer</c>, <c>controllerManager</c> and <c>scheduler</c> are separate fields on
    ///         the spec and there is no "all of them" — which is also why
    ///         <see cref="ControlPlaneContainersPerReplica" /> is 3 rather than 1.
    ///     </para>
    /// </remarks>
    public static string ControlPlaneJson(string name, JsonElement desired) =>
        new JsonObject {
            ["kind"] = ControlPlaneKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ControlPlaneName(name) },
            ["spec"] = new JsonObject {
                ["dataStoreName"] = DataStoreName,
                ["replicas"] = ControlPlaneReplicas(desired),
                ["version"] = RenderedVersion(desired),
                ["network"] = new JsonObject { ["serviceType"] = "ClusterIP" },
                ["apiServer"] = Component(),
                ["controllerManager"] = Component(),
                ["scheduler"] = Component()
            }
        }.ToJsonString();

    /// <summary>The Cluster API <c>Cluster</c> a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>THE TWO REFS ARE THE WHOLE OBJECT, THEY CARRY NO <c>apiVersion</c>, AND NEITHER IS
    ///     CHECKED BY ANYTHING ON THE WRITE PATH.</b> Cluster API v1beta2's
    ///     <c>ContractVersionedObjectReference</c> is <c>{apiGroup, kind, name}</c> — the version is
    ///     resolved from a label on the provider's CRD rather than written here — so a ref naming an
    ///     object that does not exist is admitted, stored, and reported as a condition on an object
    ///     nothing in this platform reads. The names are therefore derived from the same functions the
    ///     other two renders use rather than spelled a second time, and
    ///     <c>ManagedClusterReconcilerTests</c> asserts the three objects' names against the two refs.
    ///     <para>
    ///         ⚠ <b><c>serviceDomain</c> is written and must be</b> — see <see cref="ServiceDomain" />.
    ///     </para>
    /// </remarks>
    public static string ClusterJson(string name, JsonElement desired) =>
        new JsonObject {
            ["kind"] = ClusterKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["clusterNetwork"] = new JsonObject {
                    ["pods"] = new JsonObject { ["cidrBlocks"] = new JsonArray(PodCidr(desired)) },
                    ["services"] = new JsonObject { ["cidrBlocks"] = new JsonArray(ServiceCidr(desired)) },
                    ["serviceDomain"] = ServiceDomain
                },
                ["controlPlaneRef"] = new JsonObject {
                    ["apiGroup"] = ControlPlaneKind.Group,
                    ["kind"] = ControlPlaneKind.Kind,
                    ["name"] = ControlPlaneName(name)
                },
                ["infrastructureRef"] = new JsonObject {
                    ["apiGroup"] = InfrastructureKind.Group,
                    ["kind"] = InfrastructureKind.Kind,
                    ["name"] = name
                }
            }
        }.ToJsonString();

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, AND THE REASON IS READ IN THE CRDs RATHER THAN IN A README — because
    ///         two agents have found a README and its own code disagreeing.</b> The
    ///         <c>KamajiControlPlane</c> CRD carries five <c>+kubebuilder:default</c> markers on the
    ///         top-level spec — <c>replicas=2</c>, <c>registry="registry.k8s.io"</c>, a whole
    ///         <c>kubelet</c> object, a whole <c>network</c> object and <c>network.serviceType</c> — so
    ///         a real API server writes back four keys this provider never sent, on the first create.
    ///         Cluster API's own <c>ClusterSpec</c> has no defaults and its mutating webhook is a no-op
    ///         for a non-topology cluster, which is worth knowing rather than assuming; the KubeVirt
    ///         provider defaults only status fields. ⚠ <b>An equality comparison would pass in the
    ///         Docker-free suite AND in the k3s suite and fail only against a real management
    ///         cluster</b>, because the harness derives its CRD stubs from
    ///         <c>ProviderConformanceCase.Objects</c> and a derived stub has an <i>open</i> schema with
    ///         no defaults in it — <c>CyberCloud.Providers.Search</c> measured exactly that, 27 of 27
    ///         green over an equality bug. <c>ManagedClusterMatchesTests</c> is the hand-written test
    ///         that catches it, and it is the only thing that can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It says nothing at all about whether the tenant has a cluster.</b> See
    ///         <see cref="Readiness" />, and this class's remarks.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on <c>kind</c>, because a conformance case supplies one comparison over
    ///         every object the resource owns and this resource owns three. An unrecognised document is
    ///         <see langword="false" /> rather than assumed.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (Document(objectJson) is not { } document || document["spec"] is not JsonObject spec) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "Cluster" => MatchesCluster(spec, desired),
            "KamajiControlPlane" => MatchesControlPlane(spec, desired),
            "KubevirtCluster" => MatchesInfrastructure(document),
            _ => false
        };
    }

    static bool MatchesCluster(JsonObject spec, JsonElement desired) =>
        FirstCidr(spec, "pods") == PodCidr(desired)
        && FirstCidr(spec, "services") == ServiceCidr(desired)
        && spec["clusterNetwork"]?["serviceDomain"]?.GetValue<string>() == ServiceDomain
        && spec["controlPlaneRef"]?["kind"]?.GetValue<string>() == ControlPlaneKind.Kind
        && spec["controlPlaneRef"]?["apiGroup"]?.GetValue<string>() == ControlPlaneKind.Group
        && spec["infrastructureRef"]?["kind"]?.GetValue<string>() == InfrastructureKind.Kind
        && spec["infrastructureRef"]?["apiGroup"]?.GetValue<string>() == InfrastructureKind.Group;

    static bool MatchesControlPlane(JsonObject spec, JsonElement desired) =>
        spec["dataStoreName"]?.GetValue<string>() == DataStoreName
        && spec["replicas"]?.GetValue<int>() == ControlPlaneReplicas(desired)
        && spec["version"]?.GetValue<string>() == RenderedVersion(desired)
        // ⚠ THE ONE FIELD ON THIS OBJECT WHOSE DRIFT IS A SECURITY EVENT. Kamaji's CRD defaults
        // serviceType to LoadBalancer, so an object whose ClusterIP was removed by a hand edit, a
        // merge or a mutating policy comes back as a published API server — and every other field
        // would still match. It is compared for that reason and no other.
        && spec["network"]?["serviceType"]?.GetValue<string>() == "ClusterIP";

    /// <summary>
    ///     ⚠ The infrastructure object is judged on its <b>annotation</b> rather than on its spec,
    ///     because it has no spec — see <see cref="InfrastructureJson" />.
    /// </summary>
    static bool MatchesInfrastructure(JsonObject document) =>
        document["metadata"]?["annotations"]?[ExternallyManagedAnnotation]?.GetValue<string>()
        == ExternallyManagedBy;

    static string FirstCidr(JsonObject spec, string family) =>
        spec["clusterNetwork"]?[family]?["cidrBlocks"] is JsonArray blocks && blocks.Count > 0
            ? blocks[0]?.GetValue<string>() ?? string.Empty
            : string.Empty;

    /// <summary>What a <c>Cluster</c>'s own status says about whether there is a cluster yet.</summary>
    /// <param name="objectJson">The <c>Cluster</c> object's JSON, as the API server returned it.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS THE FUNCTION THAT MAKES THIS TYPE DIFFERENT FROM EVERY OTHER PROVIDER, AND
    ///         ITS THIRD ANSWER IS THE INTERESTING ONE.</b> Nine families decide convergence from
    ///         <see cref="Matches" /> alone, which is right for them: what they applied <i>is</i> the
    ///         product, modulo an operator that will get there. Here what was applied is a
    ///         <i>request</i> for a cluster, so this reads Cluster API's own conditions and the
    ///         reconciler reports them as docs/plan/09 § Kubernetes in Kubernetes' step list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ClusterReadinessKind.NotReported" /> IS A HOLE AND IT IS NAMED RATHER
    ///         THAN HIDDEN.</b> An object with no <c>status</c> at all has never been seen by a
    ///         controller. In a management cluster with Cluster API installed that lasts seconds; in a
    ///         cluster where the CRDs exist and the controller is dead it lasts forever, and this
    ///         platform cannot tell the two apart from here. <c>ManagedClusterReconciler</c> treats it
    ///         as converged, because the alternative is a type that can never converge in either
    ///         conformance suite — the Docker-free harness echoes the apply back and the k3s harness
    ///         installs a schema-less CRD stub with no controller behind it. ⚠ <b>The case where the
    ///         CRDs are absent entirely is caught one layer earlier</b>, by the apply, which is what
    ///         keeps the hole to "installed but not running" rather than "not installed".
    ///         <c>conformance.yaml § owed</c>, <c>converged-is-not-ready</c>.
    ///     </para>
    /// </remarks>
    public static ClusterReadiness Readiness(string objectJson) {
        if (Document(objectJson) is not { } document) {
            return new(ClusterReadinessKind.NotReported, "the object is not readable as JSON");
        }

        if (document["status"] is not JsonObject status) {
            return new(ClusterReadinessKind.NotReported, "no controller has written a status yet");
        }

        if (status["conditions"] is JsonArray conditions) {
            foreach (var condition in conditions.OfType<JsonObject>()) {
                if (condition["type"]?.GetValue<string>() != "Ready") {
                    continue;
                }

                return condition["status"]?.GetValue<string>() == "True"
                    ? new(ClusterReadinessKind.Ready, "the control plane is ready")
                    : new(
                        ClusterReadinessKind.NotReady,
                        condition["message"]?.GetValue<string>()
                        ?? condition["reason"]?.GetValue<string>()
                        ?? "the cluster is not ready yet"
                    );
            }
        }

        // ⚠ A status with no `Ready` condition is NOT the same as no status. Cluster API writes the
        // infrastructure and control-plane halves before it summarises them, so this branch is the
        // ordinary early state of a real provision and is what docs/plan/09's six-to-nine-minute step
        // table looks like from here.
        return new(
            ClusterReadinessKind.NotReady,
            (Flag(status, "infrastructureReady")
                ? "the infrastructure is ready and "
                : "waiting for the infrastructure, and ")
            + (Flag(status, "controlPlaneReady")
                ? "the control plane is ready"
                : "waiting for the control plane")
        );
    }

    static bool Flag(JsonObject status, string name) =>
        status[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The management cluster to place the control plane in.</param>
    /// <param name="version">The control plane's Kubernetes minor.</param>
    /// <param name="controlPlaneReplicas">How many control-plane replicas.</param>
    /// <param name="podCidr">The pod address space.</param>
    /// <param name="serviceCidr">The Service address space.</param>
    /// <param name="monitoring">Whether the control plane's metrics are scraped.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>, for the reason <c>StorageAccounts.Body</c>
    ///     gives: <c>ResourceSchema.Project</c> skips a <see cref="SchemaKind.Nested" /> container and
    ///     rebuilds it from whichever leaf lands first.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string version = DefaultVersion,
        int controlPlaneReplicas = DefaultControlPlaneReplicas,
        string podCidr = DefaultPodCidr,
        string serviceCidr = DefaultServiceCidr,
        bool monitoring = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = version,
                ["controlPlane"] = new JsonObject { ["replicas"] = controlPlaneReplicas },
                ["network"] = new JsonObject { ["podCidr"] = podCidr, ["serviceCidr"] = serviceCidr },
                ["monitoring"] = new JsonObject { ["enabled"] = monitoring }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────

    const int DefaultControlPlaneReplicas = 2;
    const string DefaultPodCidr = "10.244.0.0/16";
    const string DefaultServiceCidr = "10.96.0.0/12";

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    static JsonObject Component() =>
        new() {
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject {
                    ["cpu"] = ControlPlaneCpu, ["memory"] = ControlPlaneMemory
                }
            }
        };

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string name, string fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static string Member(JsonElement desired, string parent, string name, string fallback) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;
}

/// <summary>Which of the three things a <c>Cluster</c>'s status can be saying.</summary>
/// <remarks>
///     ⚠ <b>Three rather than two, and the third is the whole point.</b> "Ready" and "not ready" is
///     what a boolean would give; what a reconciler needs to know as well is <i>nobody has said</i>,
///     because that is indistinguishable from "not ready" at the object and completely different at
///     the platform. See <see cref="ManagedClusters.Readiness" />.
/// </remarks>
public enum ClusterReadinessKind {
    /// <summary>No controller has written a status. ⚠ Not the same as not ready.</summary>
    NotReported = 0,

    /// <summary>A controller has looked and the cluster is not usable yet.</summary>
    NotReady = 1,

    /// <summary>Cluster API reports the cluster ready.</summary>
    Ready = 2
}

/// <summary>What a <c>Cluster</c>'s status says, and in words a tenant can read.</summary>
/// <param name="Kind">Which of the three.</param>
/// <param name="Detail">
///     What to put in front of a human. ⚠ It is the upstream <c>message</c> where there is one —
///     docs/plan/09 § Kubernetes in Kubernetes names image pull, DHCP and cloud-init as the flakiest
///     step, and none of those is a phrase this platform could have invented.
/// </param>
public readonly record struct ClusterReadiness(ClusterReadinessKind Kind, string Detail);
