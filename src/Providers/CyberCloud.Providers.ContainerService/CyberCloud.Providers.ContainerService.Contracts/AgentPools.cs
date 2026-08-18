using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.ContainerService/managedClusters/agentPools</c> — the
///     VMs a managed cluster's workloads actually run on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CHILD TYPE IN THIS TREE THAT CHANGES ITS PARENT.</b>
///         <c>CyberCloud.Storage/accounts/buckets</c> is a name inside capacity its account already
///         provisioned — <c>charts/managed/seaweedfs-bucket/conformance.yaml</c> says so in as many
///         words, <i>"quota is a ceiling and not a reservation"</i>. An agent pool is the opposite: a
///         managed cluster with no pools has <b>no worker nodes at all</b>, so this type is not a
///         subdivision of the parent's capacity, it <i>is</i> the parent's capacity. Three
///         consequences:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>It draws real quota</b> — vCPU, memory and storage, unlike a bucket, which draws only
///             <c>QuotaMeter.Resources</c>.
///         </item>
///         <item>
///             ⚠ <b>And it draws quota that MOVES AFTER THE CREATE.</b> Every other type in the
///             catalogue is sized once and resized by a PUT the manager re-reserves against. This one
///             can be given an autoscaler, and then the thing that changes its size is a controller
///             inside a cluster the platform cannot see. <see cref="EffectiveCount" /> is where that is
///             answered.
///         </item>
///         <item>
///             <b>A cluster with no pool is a legal, converged, useless resource</b>, and nothing
///             refuses it. docs/plan/08 has no "a parent must have at least one child" anywhere, and
///             inventing one here would make a cluster's create depend on a second create the caller
///             has not made yet.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>NOTHING IN THE BODY NAMES THE CLUSTER, WHICH IS THE WHOLE OF A CHILD TYPE.</b> The
///         <i>address</i> names it — <c>…/managedClusters/{cluster}/agentPools/{pool}</c> — and
///         <see cref="ResourceId.Parent" /> is a pure function of that address. A <c>clusterName</c>
///         property would be a second spelling of the same fact and the two would disagree the first
///         time a body was sent under the wrong path. The only places the parent's name is read are
///         <see cref="ObjectNameOf" /> and <see cref="ClusterNameOf" />, both of which take the id.
///     </para>
///     <para>
///         ⚠ <b>IT RENDERS THREE OBJECTS IN THREE API GROUPS, WHICH IS EXACTLY WHAT ITS PARENT
///         RENDERS, AND NO CHILD HAS EVER DONE THAT.</b> A bucket renders one object against its
///         account's one. Here both halves are Cluster API compositions — a machine template, a
///         bootstrap template, and a <c>MachineDeployment</c> that names both — so "a child is a
///         smaller thing than its parent" turns out to be a fact about object storage rather than
///         about child types.
///     </para>
///     <para>
///         ⚠ <b>THE VERSION-SKEW RULE LIVES BETWEEN THIS TYPE AND ITS PARENT AND IS ENFORCED BY
///         NEITHER.</b> See <see cref="ManagedClusters.MaxKubeletMinorsBehind" />: docs/plan/13 promises
///         the API enforces it, <see cref="ResourceSchema" /> validates one body against constants, and
///         a parent's version is in another resource. <c>ManagedClusters.SkewIsLegal</c> exists, is
///         tested, and is called by nothing on the write path.
///     </para>
/// </remarks>
public static class AgentPools {
    /// <summary>The provider namespace — the cluster's, because a child shares its parent's.</summary>
    public const string ProviderNamespace = ManagedClusters.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b><c>managedClusters/agentPools</c>, interleaved — not a flattened
    ///     <c>agentPools</c>.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/12 § Child resources chose <c>…/managedClusters/{c}/agentPools/{p}</c> over the
    ///     flattened form for the reason <c>test/CyberCloud.Isolation/ParentEdgeTests</c> states: a
    ///     flattened address has nowhere to put the parent's name, so the ReBAC <c>parent</c> edge
    ///     would have to name the resource <i>group</i> — and granting somebody a cluster would then
    ///     grant nothing on its node pools, which on this type means granting somebody a cluster and
    ///     not the machines it runs on. ⚠ It is also what keeps
    ///     <c>ProviderConformanceTests.CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c>
    ///     from self-skipping, and here that assertion is load-bearing rather than decorative: a pool
    ///     created under a cluster that does not exist would render a <c>MachineDeployment</c> whose
    ///     <c>clusterName</c> matches nothing, which Cluster API accepts and never reconciles.
    /// </remarks>
    public const string TypePath = "managedClusters/agentPools";

    /// <summary>The one api-version. ⚠ Equal to the cluster's, and it must be.</summary>
    public const string V2026 = ManagedClusters.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    /// <remarks>
    ///     ⚠ <b>A chart of its own rather than a second block in <c>managed/kubernetes</c>.</b>
    ///     <c>Build.Charts</c> pairs a chart with a resource type through one
    ///     <c>cybercloud.io/resource-type</c> annotation per <c>Chart.yaml</c>, so one directory cannot
    ///     be the surface of two types. It is also the honest shape: a node pool has a different
    ///     lifetime from its cluster, and is the only one of the two a tenant deletes casually.
    /// </remarks>
    public const string ChartName = "managed/kubernetes-agentpool";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    /// <remarks>
    ///     ⚠ <b>A pool carries its own <c>clusterId</c> and NOTHING CHECKS IT AGAINST ITS CLUSTER'S —
    ///     second sighting, and worse than the first.</b>
    ///     <c>charts/managed/seaweedfs-bucket/conformance.yaml</c> recorded this as
    ///     <c>bucket-cluster-may-differ-from-its-accounts</c>: a body naming a different management
    ///     cluster from the parent's produces objects applied into a namespace whose parent objects are
    ///     elsewhere. There the symptom is a <c>Bucket</c> nobody reconciles; here it is a
    ///     <c>MachineDeployment</c> in a management cluster that has never heard of the
    ///     <c>Cluster</c> it names, which Cluster API accepts and leaves alone forever, and the
    ///     tenant sees a node pool that is <c>Succeeded</c> and produced no nodes. Closing it is the
    ///     same fix that item names: a <c>RequiresCluster</c> variant that inherits placement from the
    ///     parent rather than declaring it.
    /// </remarks>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that rolls the pool onto the current node image.</summary>
    /// <remarks>
    ///     ⚠ <b>Declared with no handler — <see cref="IResourceTypeBuilder" /> takes none, on any
    ///     type.</b> It exists because docs/plan/13 § Upgrades separates two things a PUT cannot: a
    ///     <i>version</i> change, which is a body change and therefore a PUT, and a <i>node image</i>
    ///     refresh at the same version, which changes no property a tenant can see and so has no body
    ///     to send. Azure spells the second one the same way. Its mechanism when it exists is a rolling
    ///     <c>MachineDeployment</c> update — a new <c>KubevirtMachineTemplate</c> name, which is what
    ///     makes Cluster API replace machines rather than mutate them.
    /// </remarks>
    public const string UpgradeNodeImageAction = "upgradeNodeImage";

    /// <summary>The permission <see cref="UpgradeNodeImageAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <b><c>write</c>, and unlike the cluster's <c>listCredentials</c> it needs no permission of
    ///     its own.</b> Nothing leaves the platform through it — it is a request to replace machines,
    ///     which is exactly the authority a PUT on this resource already carries.
    /// </remarks>
    public const string UpgradeNodeImagePermission = "write";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The three objects a pool IS ───────────────────────────────────────────────────────────

    /// <summary>The <c>MachineDeployment</c> — the object that stitches the other two together.</summary>
    /// <remarks>
    ///     ⚠ <b><c>v1beta2</c>, and its shape moved rather than merely being renamed.</b> A v1beta1
    ///     <c>MachineDeploymentSpec</c> carries <c>spec.strategy.rollingUpdate</c>; v1beta2 carries
    ///     <c>spec.rollout.strategy.rollingUpdate</c>, moves <c>strategy.deletePolicy</c> to
    ///     <c>spec.deletion.order</c>, and replaces both refs with the version-less
    ///     <c>ContractVersionedObjectReference</c>. Copying a v1beta1 template would apply cleanly, be
    ///     admitted, and drop the rollout policy on the floor — the API server prunes unknown fields.
    /// </remarks>
    public static GroupVersionKind MachineDeploymentKind { get; } =
        new() {
            Group = "cluster.x-k8s.io",
            Version = "v1beta2",
            Kind = "MachineDeployment",
            Plural = "machinedeployments"
        };

    /// <summary>The <c>KubevirtMachineTemplate</c> — what one worker VM looks like.</summary>
    public static GroupVersionKind MachineTemplateKind { get; } =
        new() {
            Group = "infrastructure.cluster.x-k8s.io",
            Version = "v1alpha1",
            Kind = "KubevirtMachineTemplate",
            Plural = "kubevirtmachinetemplates"
        };

    /// <summary>The <c>KubeadmConfigTemplate</c> — how a VM joins the cluster.</summary>
    /// <remarks>
    ///     ⚠ <b><c>v1beta2</c>, for the reason <see cref="MachineDeploymentKind" /> gives</b>: the
    ///     <c>v1beta1</c> version of this CRD is marked <c>deprecated: true</c> and is not the storage
    ///     version.
    /// </remarks>
    public static GroupVersionKind BootstrapKind { get; } =
        new() {
            Group = "bootstrap.cluster.x-k8s.io",
            Version = "v1beta2",
            Kind = "KubeadmConfigTemplate",
            Plural = "kubeadmconfigtemplates"
        };

    /// <summary>
    ///     The name of every object a pool renders: its cluster's name and its own, joined.
    /// </summary>
    /// <param name="id">The pool's address.</param>
    /// <remarks>
    ///     ⚠ <b>THE PARENT'S NAME IS IN THE OBJECT NAME BECAUSE THE NAMESPACE DOES NOT DISTINGUISH
    ///     THEM.</b> <c>ReconcileDriver.NamespaceFor</c> is <c>{subscriptionId:N}-{resourceGroup}</c> —
    ///     a parent resource is <i>inside</i> one namespace, not a namespace of its own — so two
    ///     clusters in one resource group may each hold a pool called <c>workers</c>, and a renderer
    ///     that ignored <see cref="ResourceId.ParentNames" /> would have them fighting over one
    ///     <c>MachineDeployment</c>. ⚠ On this type the damage is worse than a bucket's: the two
    ///     <c>MachineDeployment</c>s carry different <c>clusterName</c>s, so each pass would move every
    ///     worker VM in the resource group from one tenant's cluster to the other's and back.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the MachineDeployment it renders would collide "
                + "with every other cluster's pool of the same name in the same resource group. An "
                + "agent pool is a child type and its address always interleaves its cluster — see "
                + "AgentPools.TypePath.",
                nameof(id)
            )
            : id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>The <c>Cluster</c> a pool's machines join, which is its parent's own name.</summary>
    /// <param name="id">The pool's address.</param>
    /// <remarks>
    ///     ⚠ <b>Read off the ADDRESS and never off the body</b> — see the remarks on this class.
    ///     <see cref="ResourceId.Parent" /> is a pure function of the address, so this cannot disagree
    ///     with what the write path resolved the parent's index binding against.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    public static string ClusterNameOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no Cluster for its machines to join.",
            nameof(id)
        );

    /// <summary>The <c>MachineDeployment</c> a pool owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The pool's address.</param>
    public static ObjectRef MachineDeploymentRef(string ns, ResourceId id) =>
        new() { Kind = MachineDeploymentKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>KubevirtMachineTemplate</c> a pool owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The pool's address.</param>
    public static ObjectRef MachineTemplateRef(string ns, ResourceId id) =>
        new() { Kind = MachineTemplateKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>KubeadmConfigTemplate</c> a pool owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The pool's address.</param>
    public static ObjectRef BootstrapRef(string ns, ResourceId id) =>
        new() { Kind = BootstrapKind, Namespace = ns, Name = ObjectNameOf(id) };

    // ── The labels three objects have to agree about ──────────────────────────────────────────

    /// <summary>The label a <c>MachineDeployment</c> selects its machines with.</summary>
    /// <remarks>
    ///     ⚠ <b>ADR-013's SEVEN LABELS DO NOT COVER THIS AND CANNOT.</b> <c>KubeCommandBuilder</c>
    ///     injects the seven into the <i>object's</i> <c>metadata.labels</c>, non-overridably — which
    ///     is why <c>EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations</c> stays green
    ///     for a provider that renders them wrong, as <c>CyberCloud.Providers.DocumentDB</c> measured.
    ///     A <c>MachineDeployment</c>'s <c>spec.selector</c> and its <c>spec.template.metadata.labels</c>
    ///     are a different pair of places, are injected by nothing, and Cluster API's own validating
    ///     webhook <b>refuses the object</b> when they disagree. Second sighting of that shape, and the
    ///     first where upstream catches it for us rather than silently producing a
    ///     workload nothing selects.
    /// </remarks>
    public const string PoolLabel = "cybercloud.io/agent-pool";

    // ── What the platform owns rather than the tenant ─────────────────────────────────────────

    /// <summary>The container-disk image a worker VM boots.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>READ OFF THE REGISTRY ON 2026-08-18, AND THE REPOSITORY IS RIGHT WHILE THE TAGS
    ///         THIS PLATFORM RENDERED WERE NOT.</b> <c>quay.io/capk/ubuntu-2404-container-disk</c>
    ///         exists and carries exactly four tags: <c>v1.31.5</c>, <c>v1.32.1</c>, <c>v1.33.5</c>
    ///         and <c>v1.34.1</c>. <c>ManagedClusters.PinnedPatch</c> named <c>v1.32.9</c> and
    ///         <c>v1.33.4</c>, so every worker VM in every pool pulled a tag that does not exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE ASSUMPTION UNDER THE OLD PIN WAS THE DEFECT, NOT EITHER STRING.</b> This
    ///         remark used to say the provider publishes an image "tagged with the Kubernetes version
    ///         they carry", making the tag "a function of <c>ManagedClusters.PinnedPatch</c>". It
    ///         publishes ONE TAG PER MINOR, at whichever patch it happened to build — so the
    ///         dependency runs the other way, and the pin is now chosen from what this repository has
    ///         published. <c>ManagedClusters.PinnedPatch</c> carries the reasoning and what it costs.
    ///     </para>
    ///     <para>
    ///         ⚠ Still true, and it is why the tag is spelled in one place: a tag that does not exist
    ///         is a VM that never boots, with the reason in a <c>DataVolume</c>'s events — three
    ///         objects below anything this platform reads, and invisible to a tenant who has no
    ///         credential for the cluster.
    ///     </para>
    /// </remarks>
    public const string NodeImageRepository = "quay.io/capk/ubuntu-2404-container-disk";

    /// <summary>The <c>VirtualMachineClusterInstancetype</c> a preset names.</summary>
    /// <param name="preset">A key of <see cref="ManagedClusters.Presets" />.</param>
    /// <remarks>
    ///     ⚠ <b>THE PRESET NAME IS THE OBJECT NAME, AND A DOT IS LEGAL THERE.</b> docs/plan/09 wants
    ///     <i>"an instancetype from a platform catalogue, which is where the <c>t1.micro</c>/<c>c1.large</c>
    ///     vocabulary from ADR-010 is defined once and reused by every provider"</i>. A Kubernetes
    ///     object name is a DNS-1123 <i>subdomain</i>, in which <c>.</c> is legal — unlike a label
    ///     <i>value</i>, where it is not, which is the rule <c>KubeLabels.ResourceTypeValue</c> exists
    ///     for and the reason this looks like it should need mangling and does not.
    ///     <para>
    ///         ⚠ <b>It is CLUSTER-SCOPED and this provider does not create it.</b>
    ///         <c>VirtualMachineClusterInstancetype</c> is cluster-scoped;
    ///         <c>VirtualMachineInstancetype</c> is namespaced. Rendering the namespaced one per pool
    ///         would put the sizing vocabulary in the tenant's own namespace where a tenant could edit
    ///         it, and rendering the cluster-scoped one would be this platform creating an object with
    ///         no namespace to be isolated by — the same refusal
    ///         <c>ManagedClusters.DataStoreName</c> makes. So the catalogue is the bundle's, and a
    ///         missing instancetype is a VM that never starts. <c>conformance.yaml § owed</c>,
    ///         <c>instancetypes-are-the-bundles</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>kind</c> is written out rather than omitted.</b> KubeVirt resolves an
    ///         <c>InstancetypeMatcher</c> with an empty <c>kind</c> to the <i>cluster-scoped</i> type,
    ///         which is the one this wants — so omitting it would be correct today and would silently
    ///         change meaning if that default ever moved.
    ///     </para>
    /// </remarks>
    public static string InstancetypeName(string preset) => preset;

    /// <summary>The kind an <c>InstancetypeMatcher</c> names.</summary>
    public const string InstancetypeKind = "VirtualMachineClusterInstancetype";

    /// <summary>The annotations Cluster API's autoscaler contract reads.</summary>
    /// <remarks>
    ///     ⚠ <b>ANNOTATIONS ON THE <c>MachineDeployment</c>, WHICH IS CLUSTER API'S OWN MECHANISM AND
    ///     NOT A CONVENTION INVENTED HERE.</b> Cluster API's <c>MachineDeployment</c> defaulting
    ///     webhook reads exactly these two when it decides what <c>spec.replicas</c> should be, and the
    ///     cluster-autoscaler's Cluster API provider reads them to find its bounds. ⚠ <b>Nothing in
    ///     this platform installs a cluster-autoscaler</b>, so on a bundle without one these are inert
    ///     and the pool sits at its declared count — which is a safe failure and is not a silent one
    ///     only because it is written down. <c>conformance.yaml § owed</c>,
    ///     <c>autoscaling-needs-an-autoscaler-in-the-bundle</c>.
    /// </remarks>
    public const string AutoscaleMinAnnotation = "cluster.x-k8s.io/cluster-api-autoscaler-node-group-min-size";

    /// <inheritdoc cref="AutoscaleMinAnnotation" />
    public const string AutoscaleMaxAnnotation = "cluster.x-k8s.io/cluster-api-autoscaler-node-group-max-size";

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO <c>taints</c> AND NO <c>labels</c>, AND BOTH ARE THINGS AN AKS NODE POOL
    ///         HAS.</b> They are node properties written by <c>kubeadm</c> at join time, so they are
    ///         renderable — <c>joinConfiguration.nodeRegistration</c> takes both. They are left out
    ///         because a taint is the one property whose misuse is unrecoverable from the API: a pool
    ///         tainted so that nothing schedules on it looks identical to a pool whose VMs never
    ///         booted, and the tenant's own diagnostic — <c>kubectl describe node</c> — needs the
    ///         credential this platform cannot yet hand out. They belong in the api-version that ships
    ///         alongside a working <c>listCredentials</c>.
    ///         <c>conformance.yaml § owed</c>, <c>taints-and-labels-wait-for-credentials</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>spot</c>, NO <c>availabilityZones</c>, NO <c>gpu</c>.</b> docs/plan/13 puts
    ///         GPU in M3 by name and calls it <i>"a hardware programme with a software component"</i>;
    ///         zones need a management cluster whose nodes are labelled by zone, which is a fact about
    ///         somebody's data centre; and spot is a billing concept this platform has no market for.
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
                    Description: "The region the pool is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The pool's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The management cluster the machine objects are applied to. Must be "
                    + "the one the cluster is in — nothing checks that, and a pool placed elsewhere "
                    + "produces a MachineDeployment naming a Cluster that is not there, which Cluster "
                    + "API accepts and never reconciles."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/count",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many worker VMs the pool runs. ⚠ When autoscaling is on this is "
                    + "the starting size and the autoscaler moves it; quota is reserved against the "
                    + "maximum in that case, not against this."
                ) {
                    Minimum = 1,
                    Maximum = 100,
                    DefaultJson = "3"
                },
                new(
                    "/properties/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The VM size, from the platform's sizing catalogue. Kubernetes nodes "
                    + "use the s1 family, which is 1 vCPU to 4 GiB. ⚠ Immutable: a Cluster API machine "
                    + "template cannot be resized in place, so changing this would mean replacing every "
                    + "VM in the pool, which is a different operation from the one a PUT looks like."
                ) {
                    AllowedValues = [.. ManagedClusters.Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    Immutable = true,
                    DefaultJson = "\"s1.small\""
                },
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The Kubernetes minor version the nodes run. ⚠ It may be up to three "
                    + "minors behind the cluster's control plane and may never be ahead of it. This API "
                    + "does not check that — the cluster's version is a different resource — so an "
                    + "illegal pair is accepted here and produces nodes that join and then misbehave."
                ) {
                    AllowedValues = ManagedClusters.Versions,
                    DefaultJson = "\"" + ManagedClusters.DefaultVersion + "\""
                },
                new(
                    "/properties/osDiskSize",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The root volume of each VM, in Kubernetes quantity form. It holds the "
                    + "operating system, the container images and every writable layer, so a pool "
                    + "running large images needs more of it than a pool running small ones."
                ) {
                    Pattern = ManagedClusters.QuantityPattern,
                    Immutable = true,
                    DefaultJson = "\"60Gi\"",
                    ExampleJson = "\"60Gi\""
                },
                new(
                    "/properties/autoscale",
                    SchemaKind.Nested,
                    Description: "Whether a cluster-autoscaler may resize the pool."
                ),
                new(
                    "/properties/autoscale/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the pool carries autoscaler bounds. Off by default. ⚠ Turning "
                    + "it on changes what the pool costs: quota is then reserved against maxCount, "
                    + "because a pool that may grow to twenty machines has to have twenty machines' "
                    + "worth of headroom to grow into."
                ) {
                    DefaultJson = "false"
                },
                new(
                    "/properties/autoscale/minCount",
                    SchemaKind.WholeNumber,
                    Description: "The smallest the autoscaler may shrink the pool to. Ignored when "
                    + "autoscaling is off."
                ) {
                    Minimum = 1,
                    Maximum = 100,
                    DefaultJson = "1"
                },
                new(
                    "/properties/autoscale/maxCount",
                    SchemaKind.WholeNumber,
                    Description: "The largest the autoscaler may grow the pool to, and what quota is "
                    + "reserved against while autoscaling is on. ⚠ Nothing checks that it is at least "
                    + "minCount — that is a relation between two properties of one body, which the "
                    + "schema validates nothing about."
                ) {
                    Minimum = 1,
                    Maximum = 100,
                    DefaultJson = "3"
                },
                new(
                    "/properties/upgrade",
                    SchemaKind.Nested,
                    Description: "How machines are replaced when the pool changes."
                ),
                new(
                    "/properties/upgrade/maxSurge",
                    SchemaKind.WholeNumber,
                    Description: "How many extra machines may exist during a rolling replacement. One "
                    + "means a new VM boots and joins before an old one is removed, which is why it is "
                    + "the default: it costs one machine's capacity and loses none."
                ) {
                    Minimum = 0,
                    Maximum = 10,
                    DefaultJson = "1"
                },
                new(
                    "/properties/upgrade/maxUnavailable",
                    SchemaKind.WholeNumber,
                    Description: "How many machines may be missing during a rolling replacement. Zero "
                    + "with a surge of one is the safe pair; raising it is faster and reduces the "
                    + "pool's capacity while it runs. ⚠ Both being zero would make an upgrade unable to "
                    + "start, and nothing refuses that pair."
                ) {
                    Minimum = 0,
                    Maximum = 10,
                    DefaultJson = "0"
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>How many machines a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Count(JsonElement desired) => Number(desired, "count", DefaultCount);

    /// <summary>The sizing preset a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Size(JsonElement desired) => Text(desired, "size", DefaultSize);

    /// <summary>The Kubernetes minor a body asks its nodes for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Text(desired, "version", ManagedClusters.DefaultVersion);

    /// <summary>The full version <see cref="Version" /> is rendered as.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>The same pin the control plane uses</b> — <c>ManagedClusters.PinnedPatch</c> — so a
    ///     cluster and a pool at the same minor are at the same patch. Two tables would drift, and the
    ///     drift would be a skew nobody declared.
    /// </remarks>
    public static string RenderedVersion(JsonElement desired) =>
        ManagedClusters.PinnedPatch.TryGetValue(Version(desired), out var pinned)
            ? pinned
            : "v" + Version(desired);

    /// <summary>The root volume a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string OsDiskSize(JsonElement desired) => Text(desired, "osDiskSize", DefaultOsDiskSize);

    /// <summary>Whether a body asks for an autoscaler.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool AutoscaleEnabled(JsonElement desired) =>
        Member(desired, "autoscale", "enabled") is { ValueKind: JsonValueKind.True };

    /// <summary>The autoscaler's floor.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MinCount(JsonElement desired) =>
        NestedNumber(desired, "autoscale", "minCount", DefaultMinCount);

    /// <summary>The autoscaler's ceiling.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxCount(JsonElement desired) =>
        NestedNumber(desired, "autoscale", "maxCount", DefaultMaxCount);

    /// <summary>How many machines the platform must have room for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>THE CEILING WHEN AUTOSCALING IS ON, AND THIS IS THE FINDING THIS TYPE EXISTS TO
    ///     MAKE.</b> Every quota meter in this platform is a pure function of a body, reserved at write
    ///     time and re-derived from the stored body at delete time — <c>ResourceManagerService</c>
    ///     requires exactly that, and nine providers have satisfied it by being sized once. A node pool
    ///     with an autoscaler is the first resource whose real consumption is moved by something the
    ///     platform does not observe, so "reserve what the body says" would reserve three machines for
    ///     a resource that may run twenty. Reserving the ceiling is the only answer that keeps the
    ///     reservation a function of the body <i>and</i> keeps it true.
    ///     <para>
    ///         ⚠ <b>What it costs, stated rather than glossed:</b> a tenant who enables autoscaling
    ///         pays quota for capacity they are not using. That is the correct trade for a
    ///         <i>reservation</i> — docs/plan/06 § Quota is a reservation and not a counter — and it is
    ///         the wrong one for a <i>bill</i>, which is docs/plan/22's usage pipeline sampling what
    ///         actually ran. The two disagreeing here is not a bug in either.
    ///     </para>
    /// </remarks>
    public static int EffectiveCount(JsonElement desired) =>
        AutoscaleEnabled(desired) ? Math.Max(MaxCount(desired), Count(desired)) : Count(desired);

    /// <summary>How many extra machines a rolling replacement may create.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxSurge(JsonElement desired) =>
        NestedNumber(desired, "upgrade", "maxSurge", DefaultMaxSurge);

    /// <summary>How many machines a rolling replacement may take away.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxUnavailable(JsonElement desired) =>
        NestedNumber(desired, "upgrade", "maxUnavailable", DefaultMaxUnavailable);

    /// <summary>What one machine's CPU and memory are believed to be.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>BELIEVED, NOT DECLARED — see <see cref="InstancetypeName" />.</b> The rendered object
    ///     names an instancetype and never states a quantity, so these numbers reach quota and reach
    ///     nothing else. An instancetype in the bundle that does not match this table would be billed
    ///     at one size and run at another, and nothing anywhere would notice.
    ///     <c>conformance.yaml § owed</c>, <c>instancetypes-are-the-bundles</c>.
    /// </remarks>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        ManagedClusters.Presets.TryGetValue(Size(desired), out var preset)
            ? preset
            : (Cpu: string.Empty, Memory: string.Empty);

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>KubevirtMachineTemplate</c> a desired body becomes.</summary>
    /// <param name="id">The pool's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE SPEC IS SEVEN LEVELS DEEP AND EVERY LEVEL IS SOMEBODY ELSE'S TYPE.</b>
    ///         <c>spec.template.spec</c> is the KubeVirt provider's machine spec;
    ///         <c>.virtualMachineTemplate.spec</c> is KubeVirt's own <c>VirtualMachineSpec</c>; the
    ///         <c>.template.spec</c> inside <i>that</i> is a <c>VirtualMachineInstanceSpec</c>. Nothing
    ///         validates the shape until it reaches a real CRD, because the conformance harness's
    ///         derived stub has an open schema — so this is the render most exposed to
    ///         <c>charts/managed/kubernetes/conformance.yaml § owed</c>'s
    ///         <c>a-green-cluster-suite-proves-the-apply-path-only</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO CLOUD-INIT VOLUME AND NO SSH KEY, AND LEAVING THEM OUT IS REQUIRED RATHER THAN
    ///         TIDY.</b> The KubeVirt provider's machine controller <i>appends</i> a
    ///         <c>CloudInitConfigDrive</c> volume and a matching disk to whatever this renders, writing
    ///         the bootstrap data and its own <c>capk</c> user into it. A template that supplied one
    ///         would end up with two, which is a VM that boots from the wrong config drive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>virtualMachineBootstrapCheck</c>, so the CRD's own default of <c>ssh</c>
    ///         stands</b> — the same "render nothing and take the default" position
    ///         <c>StorageBuckets.BucketJson</c> takes on <c>reclaimPolicy</c>, and it matches the
    ///         Kamaji provider's own published KubeVirt template. ⚠ It is the riskiest default on this
    ///         type: an SSH bootstrap check needs a key that the KubeVirt controller generates while
    ///         reconciling the <c>KubevirtCluster</c>, and
    ///         <c>ManagedClusters.ExternallyManagedAnnotation</c> makes it skip that object entirely.
    ///         <c>conformance.yaml § owed</c>, <c>bootstrap-check-may-never-complete</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ The root volume's name is <c>root</c> and the provider prefixes it with the machine's
    ///         name when it creates the real <c>DataVolume</c>, so the name here is a template-local
    ///         handle rather than an object name.
    ///     </para>
    /// </remarks>
    public static string MachineTemplateJson(ResourceId id, JsonElement desired) {
        var disk = new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = RootVolumeName },
            ["spec"] = new JsonObject {
                ["storage"] = new JsonObject {
                    ["resources"] = new JsonObject {
                        ["requests"] = new JsonObject { ["storage"] = OsDiskSize(desired) }
                    }
                },
                ["source"] = new JsonObject {
                    ["registry"] = new JsonObject {
                        ["url"] = "docker://" + NodeImageRepository + ":" + RenderedVersion(desired)
                    }
                }
            }
        };

        var virtualMachine = new JsonObject {
            ["runStrategy"] = "Always",
            ["instancetype"] = new JsonObject {
                ["kind"] = InstancetypeKind, ["name"] = InstancetypeName(Size(desired))
            },
            ["dataVolumeTemplates"] = new JsonArray(disk),
            ["template"] = new JsonObject {
                ["spec"] = new JsonObject {
                    ["domain"] = new JsonObject {
                        ["devices"] = new JsonObject {
                            ["disks"] = new JsonArray(
                                new JsonObject {
                                    ["name"] = RootVolumeName,
                                    ["disk"] = new JsonObject { ["bus"] = "virtio" }
                                }
                            ),
                            ["interfaces"] = new JsonArray(
                                new JsonObject { ["name"] = "default", ["bridge"] = new JsonObject() }
                            )
                        }
                    },
                    ["networks"] = new JsonArray(
                        new JsonObject { ["name"] = "default", ["pod"] = new JsonObject() }
                    ),
                    ["volumes"] = new JsonArray(
                        new JsonObject {
                            ["name"] = RootVolumeName,
                            ["dataVolume"] = new JsonObject { ["name"] = RootVolumeName }
                        }
                    ),
                    // ⚠ `External`, because a KubeVirt VM backing a Kubernetes node must not be
                    // live-migrated or evicted by the management cluster's own drain: the node inside
                    // it has its own workloads and its own opinion about when it may go away.
                    ["evictionStrategy"] = "External"
                }
            }
        };

        return new JsonObject {
            ["kind"] = MachineTemplateKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["spec"] = new JsonObject {
                ["template"] = new JsonObject {
                    ["spec"] = new JsonObject {
                        ["virtualMachineTemplate"] = new JsonObject { ["spec"] = virtualMachine }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>KubeadmConfigTemplate</c> a desired body becomes.</summary>
    /// <param name="id">The pool's address.</param>
    /// <remarks>
    ///     ⚠ <b>IT IS ALMOST EMPTY AND THAT IS CORRECT.</b> Everything a worker needs to join —
    ///     the API endpoint, the bootstrap token, the CA hash — is written by Cluster API's kubeadm
    ///     bootstrap controller into the per-machine <c>KubeadmConfig</c> it derives from this
    ///     template. What a template is <i>for</i> is the parts that are the same for every machine in
    ///     the pool, and this platform declares one of them: the cloud provider is external, because a
    ///     kubelet that believes it is on a cloud with no cloud-controller-manager stays
    ///     <c>NotReady</c> with an uninitialised-taint that nothing removes.
    ///     <para>
    ///         ⚠ It takes no body at all and is still a pure function of the address, so
    ///         <see cref="Matches" /> has something to compare and clause 1 is unaffected.
    ///     </para>
    /// </remarks>
    public static string BootstrapJson(ResourceId id) =>
        new JsonObject {
            ["kind"] = BootstrapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["spec"] = new JsonObject {
                ["template"] = new JsonObject {
                    ["spec"] = new JsonObject {
                        ["joinConfiguration"] = new JsonObject {
                            ["nodeRegistration"] = new JsonObject {
                                ["kubeletExtraArgs"] = new JsonArray(
                                    new JsonObject { ["name"] = "cloud-provider", ["value"] = "external" }
                                )
                            }
                        }
                    }
                }
            }
        }.ToJsonString();

    /// <summary>The <c>MachineDeployment</c> a desired body becomes.</summary>
    /// <param name="id">The pool's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>spec.selector</c> AND <c>spec.template.metadata.labels</c> ARE THE SAME MAP
    ///         WRITTEN TWICE, AND CLUSTER API REFUSES THE OBJECT WHEN THEY DISAGREE.</b> See
    ///         <see cref="PoolLabel" />. They are built from one expression here for that reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>clusterName</c> APPEARS TWICE — ON THE SPEC AND ON THE MACHINE TEMPLATE — AND
    ///         BOTH COME FROM THE ADDRESS.</b> Cluster API requires both and does not derive one from
    ///         the other; a pool whose two disagreed would be adopted by one cluster and counted by
    ///         another.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both refs are version-less <c>ContractVersionedObjectReference</c>s</b>, exactly as
    ///         <c>ManagedClusters.ClusterJson</c>'s are — <c>{apiGroup, kind, name}</c>, with Cluster
    ///         API resolving the version from a label on the provider's CRD.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>spec.rollout.strategy</c>, not <c>spec.strategy</c>.</b> The field moved in
    ///         v1beta2; the old spelling is pruned by the API server, so a rollout policy written at
    ///         the old path is accepted and silently absent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>replicas</c> is always written, even under an autoscaler.</b> Cluster API's
    ///         defaulting webhook computes a value when the field is missing; leaving it out would make
    ///         a read-back differ from the render on the first create for a reason
    ///         <see cref="Matches" /> would have to be taught about, and would hand the pool's initial
    ///         size to a webhook rather than to the tenant.
    ///     </para>
    /// </remarks>
    public static string MachineDeploymentJson(ResourceId id, JsonElement desired) {
        var name = ObjectNameOf(id);
        var cluster = ClusterNameOf(id);

        var metadata = new JsonObject { ["name"] = name };

        if (AutoscaleEnabled(desired)) {
            metadata["annotations"] = new JsonObject {
                [AutoscaleMinAnnotation] = MinCount(desired).ToString(CultureInfo.InvariantCulture),
                [AutoscaleMaxAnnotation] = MaxCount(desired).ToString(CultureInfo.InvariantCulture)
            };
        }

        return new JsonObject {
            ["kind"] = MachineDeploymentKind.Kind,
            ["metadata"] = metadata,
            ["spec"] = new JsonObject {
                ["clusterName"] = cluster,
                ["replicas"] = Count(desired),
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                ["rollout"] = new JsonObject {
                    ["strategy"] = new JsonObject {
                        ["type"] = "RollingUpdate",
                        ["rollingUpdate"] = new JsonObject {
                            ["maxSurge"] = MaxSurge(desired),
                            ["maxUnavailable"] = MaxUnavailable(desired)
                        }
                    }
                },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject { ["labels"] = Selector(name) },
                    ["spec"] = new JsonObject {
                        ["clusterName"] = cluster,
                        ["version"] = RenderedVersion(desired),
                        ["bootstrap"] = new JsonObject {
                            ["configRef"] = new JsonObject {
                                ["apiGroup"] = BootstrapKind.Group,
                                ["kind"] = BootstrapKind.Kind,
                                ["name"] = name
                            }
                        },
                        ["infrastructureRef"] = new JsonObject {
                            ["apiGroup"] = MachineTemplateKind.Group,
                            ["kind"] = MachineTemplateKind.Kind,
                            ["name"] = name
                        }
                    }
                }
            }
        }.ToJsonString();
    }

    static JsonObject Selector(string name) => new() { [PoolLabel] = name };

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body and the address ask
    ///     for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="id">The pool's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>clusterName</c> IS COMPARED AND IT IS THE FIELD THIS TYPE MOST NEEDS COMPARED.</b> A
    ///     <c>MachineDeployment</c> whose <c>clusterName</c> was rewritten — by a merge, by an
    ///     admission policy, by a <c>kubectl edit</c> — moves every VM in the pool into a different
    ///     tenant's cluster, and every other field would still match. It is derived from the address
    ///     rather than from the body precisely so that the comparison cannot be satisfied by a body
    ///     that agrees with itself.
    /// </remarks>
    public static bool Matches(string objectJson, ResourceId id, JsonElement desired) =>
        MatchesBody(objectJson, desired)
        && (Kind(objectJson) != MachineDeploymentKind.Kind
            || (Spec(objectJson) is { } spec
                && spec["clusterName"]?.GetValue<string>() == ClusterNameOf(id)
                && spec["template"]?["spec"]?["clusterName"]?.GetValue<string>() == ClusterNameOf(id)
                && spec["selector"]?["matchLabels"]?[PoolLabel]?.GetValue<string>() == ObjectNameOf(id)));

    /// <summary>
    ///     The half of <see cref="Matches" /> that a desired <b>body</b> alone decides.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE SPLIT EXISTS BECAUSE OF A LIMIT IN THE SHARED CONFORMANCE HARNESS THAT ONLY A
    ///         CHILD TYPE MEETS — SECOND SIGHTING.</b>
    ///         <c>ProviderConformanceCase.ObjectMatchesDesired</c> is
    ///         <c>(objectJson, desiredJson) =&gt; bool</c> and carries <b>no address</b>, so the
    ///         predicate the shared suite can evaluate for a pool is strictly smaller than the one the
    ///         reconciler evaluates. <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>
    ///         records it as <c>object-matches-desired-cannot-see-an-address</c> and this type inherits
    ///         it unchanged. What stands in is <c>AgentPoolReconcilerTests</c>, which asserts
    ///         <c>clusterName</c> and the selector against real addresses — including the case the
    ///         harness could never build, two clusters in ONE resource group each holding a pool called
    ///         <c>workers</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, and here the defaulting is a WEBHOOK rather than a CRD marker</b>,
    ///         which is a distinction three earlier providers did not have to make. Neither
    ///         <c>MachineDeploymentSpec</c> nor <c>MachineSpec</c> carries a single
    ///         <c>+kubebuilder:default</c>; Cluster API's <b>mutating webhook</b> writes
    ///         <c>replicas</c>, the whole <c>rollout.strategy</c>, two <c>cluster.x-k8s.io/*</c> labels
    ///         into the selector and the template, and a <c>v</c> prefix onto the version. ⚠ <b>So an
    ///         equality comparison here fails against a real API server and passes in BOTH conformance
    ///         suites</b> — a derived CRD stub has no webhook behind it any more than it has defaults.
    ///     </para>
    /// </remarks>
    public static bool MatchesBody(string objectJson, JsonElement desired) {
        if (Spec(objectJson) is not { } spec) {
            return false;
        }

        return Kind(objectJson) switch {
            "MachineDeployment" => spec["replicas"]?.GetValue<int>() == Count(desired)
                && spec["template"]?["spec"]?["version"]?.GetValue<string>() == RenderedVersion(desired)
                && Rolling(spec, "maxSurge") == MaxSurge(desired)
                && Rolling(spec, "maxUnavailable") == MaxUnavailable(desired),
            "KubevirtMachineTemplate" => VirtualMachine(spec) is { } virtualMachine
                && virtualMachine["instancetype"]?["name"]?.GetValue<string>()
                == InstancetypeName(Size(desired))
                && virtualMachine["instancetype"]?["kind"]?.GetValue<string>() == InstancetypeKind
                && RootDiskSize(virtualMachine) == OsDiskSize(desired),
            // ⚠ The bootstrap template is a function of nothing in the body, so the only thing a
            // read-back can disagree about is whether it is still there — which `Spec` already
            // answered. Returning true is not a weakening: it is the honest amount of comparison an
            // object with no tenant-facing content admits.
            "KubeadmConfigTemplate" => true,
            _ => false
        };
    }

    static int Rolling(JsonObject spec, string field) =>
        spec["rollout"]?["strategy"]?["rollingUpdate"]?[field] is JsonValue value
        && value.TryGetValue<int>(out var number)
            ? number
            : -1;

    static JsonObject? VirtualMachine(JsonObject spec) =>
        spec["template"]?["spec"]?["virtualMachineTemplate"]?["spec"] as JsonObject;

    static string RootDiskSize(JsonObject virtualMachine) =>
        virtualMachine["dataVolumeTemplates"] is JsonArray volumes
        && volumes.Count > 0
        && volumes[0]?["spec"]?["storage"]?["resources"]?["requests"]?["storage"] is JsonValue value
        && value.TryGetValue<string>(out var size)
            ? size
            : string.Empty;

    static string Kind(string objectJson) => Document(objectJson)?["kind"]?.GetValue<string>() ?? string.Empty;

    static JsonObject? Spec(string objectJson) => Document(objectJson)?["spec"] as JsonObject;

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The management cluster the machine objects are applied to.</param>
    /// <param name="count">How many machines.</param>
    /// <param name="size">The sizing preset.</param>
    /// <param name="version">The nodes' Kubernetes minor.</param>
    /// <param name="osDiskSize">The root volume of each machine.</param>
    /// <param name="autoscale">Whether to carry autoscaler bounds.</param>
    /// <param name="minCount">The autoscaler's floor.</param>
    /// <param name="maxCount">The autoscaler's ceiling.</param>
    /// <param name="location">The region.</param>
    /// <remarks>⚠ Every property it writes is a <b>leaf</b>, for the reason <c>StorageAccounts.Body</c> gives.</remarks>
    public static string Body(
        Guid clusterId,
        int count = DefaultCount,
        string size = DefaultSize,
        string? version = null,
        string osDiskSize = DefaultOsDiskSize,
        bool autoscale = false,
        int minCount = DefaultMinCount,
        int maxCount = DefaultMaxCount,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["count"] = count,
                ["size"] = size,
                ["version"] = version ?? ManagedClusters.DefaultVersion,
                ["osDiskSize"] = osDiskSize,
                ["autoscale"] = new JsonObject {
                    ["enabled"] = autoscale, ["minCount"] = minCount, ["maxCount"] = maxCount
                },
                ["upgrade"] = new JsonObject {
                    ["maxSurge"] = DefaultMaxSurge, ["maxUnavailable"] = DefaultMaxUnavailable
                }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────

    const int DefaultCount = 3;
    const string DefaultSize = "s1.small";
    const string DefaultOsDiskSize = "60Gi";
    const int DefaultMinCount = 1;
    const int DefaultMaxCount = 3;
    const int DefaultMaxSurge = 1;
    const int DefaultMaxUnavailable = 0;

    /// <summary>The template-local handle of a worker's root volume.</summary>
    const string RootVolumeName = "root";

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

    static string Text(JsonElement desired, string name, string fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string name, int fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : fallback;

    static int NestedNumber(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var number)
            ? number
            : fallback;
}
