using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Compute/virtualMachines</c> — a KubeVirt
///     <c>VirtualMachine</c> whose root disk is a CDI clone of a <see cref="Images" /> resource, in a
///     tenant's own resource group.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THE TREE WITH A POWER STATE, AND IT IS NOT IN THE BODY.</b> docs/plan/13
///         § Virtual Machines lists <c>start</c>, <c>stop</c> and <c>restart</c> as actions, and Azure's
///         own model puts a VM's power state in its instance view rather than in the PUT body. Here it
///         is <c>spec.runStrategy</c> on the <c>VirtualMachine</c> object — <c>Always</c> or
///         <c>Halted</c> — moved by the power handler and <b>preserved</b> by the reconciler, which
///         reads the object before it renders and writes back whatever run strategy it found.
///         <see cref="RunStrategyOf" /> and <see cref="VirtualMachineJson" /> are the two halves;
///         <c>VirtualMachineReconciler</c> is where they meet. The alternative — a <c>powerState</c>
///         property — was rejected because an action cannot change a desired body: the operation grain
///         drives the type's reconciler and hands it the stored body, never the action's name, so a
///         <c>stop</c> that had to reach the body could not.
///     </para>
///     <para>
///         ⚠ <b>What that costs, stated rather than glossed.</b> A reconcile pass reads the run strategy
///         and applies it back a moment later under one field manager; a power action that lands
///         between the two is overwritten by the pass. The window is one apply wide and needs a
///         concurrent PUT on the same VM to open at all, and closing it wants a <c>resourceVersion</c>
///         precondition on <c>KubeCommand</c>, which is a platform change rather than a provider one.
///         <c>charts/managed/virtual-machine/conformance.yaml § owed</c>, <c>power-state-can-lose-a-race</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE ROOT DISK IS A CLONE OF AN IMAGE IN THE SAME RESOURCE GROUP, AND THE IMAGE IS NAMED
///             BY ITS RESOURCE NAME.
///         </b> <c>dataVolumeTemplates[0].spec.source.pvc</c> names the claim
///         <see cref="Images.ObjectNameOf" /> produces, in the VM's own namespace — a CDI clone across
///         namespaces is authorised against the <i>creator</i> of the <c>DataVolume</c>, which for a
///         template inside a VM is KubeVirt's own controller, and that is a permission story this row
///         does not tell. A VM boots from an image in its resource group or not at all;
///         <c>conformance.yaml § owed</c>, <c>images-are-per-resource-group</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             CPU AND MEMORY ARE QUANTITIES, NOT AN INSTANCETYPE NAME, AND THAT IS THE OPPOSITE OF
///             WHAT THE NODE POOL DOES.
///         </b> <c>AgentPools.InstancetypeName</c> renders a
///         <c>VirtualMachineClusterInstancetype</c> the bundle does not install, and its own manifest
///         records that the sizing table is therefore a belief — <c>instancetypes-are-the-bundles</c>.
///         This type renders <c>domain.cpu.cores</c> and <c>domain.memory.guest</c> from
///         <see cref="Sizes" /> directly, so what quota reserves is what the guest gets, and a VM is
///         admitted on a cluster with no catalogue object at all. The day the bundle ships one, the
///         two rows converge and the instancetype is the honest render; until then a name nothing
///         resolves would be a VM that never starts.
///     </para>
///     <para>
///         ⚠ <b><c>Converged</c> FOLLOWS KUBEVIRT'S OWN VERDICT.</b> <see cref="Readiness" /> reads
///         <c>status.printableStatus</c>: <c>Running</c> is ready, <c>Stopped</c> is ready when the run
///         strategy is <c>Halted</c>, no status at all converges the way <c>ManagedClusters.Readiness</c>
///         does (a harness with no controller behind the CRD — <c>converged-is-not-ready</c>), and
///         everything else is in progress with KubeVirt's own words. ⚠ The first machine this
///         platform rendered reached <c>Running</c> on the k3s-in-Docker lane — 46 seconds after the
///         apply, under KVM, on 2026-09-17 — against a record that said the lane had no
///         <c>/dev/kvm</c>; <c>KubeVirtOnAnEmptyCluster</c> is the measurement and
///         <c>charts/bundle/bundle.yaml § owed</c>, <c>virtual-machines-need-a-node-with-kvm</c>,
///         the correction.
///     </para>
/// </remarks>
public static class VirtualMachines {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = Images.ProviderNamespace;

    /// <summary>The type path.</summary>
    public const string TypePath = "virtualMachines";

    /// <summary>The one api-version.</summary>
    public const string V2026 = Images.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/virtual-machine";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The three power actions ───────────────────────────────────────────────────────────────

    /// <summary>Boots a stopped machine: <c>spec.runStrategy</c> becomes <see cref="RunAlways" />.</summary>
    public const string StartAction = "start";

    /// <summary>
    ///     Shuts a machine down and releases its compute: <c>spec.runStrategy</c> becomes
    ///     <see cref="RunHalted" />, and KubeVirt sends the guest ACPI power-off before it takes the
    ///     instance away.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is docs/plan/13's <c>deallocate</c> as well as its <c>stop</c>.</b> A halted
    ///     KubeVirt VM has no launcher pod, so it holds no vCPU and no memory and keeps every disk —
    ///     which is exactly the state that document calls <i>"release compute, keep disks"</i>. Azure
    ///     distinguishes a stopped-but-allocated VM because its hypervisor does; KubeVirt has no such
    ///     state to offer, so a second action that meant the same thing is not declared.
    ///     <c>conformance.yaml § owed</c>, <c>deallocate-is-stop</c>.
    /// </remarks>
    public const string StopAction = "stop";

    /// <summary>Restarts a running machine by deleting its instance, which <see cref="RunAlways" /> recreates.</summary>
    /// <remarks>
    ///     ⚠ Exactly what <c>virtctl restart</c> does behind its subresource: the VM controller deletes
    ///     the <c>VirtualMachineInstance</c> and the run strategy brings a new one up. On a stopped
    ///     machine the handler refuses rather than starts it, because a caller who meant <c>start</c>
    ///     would have said so.
    /// </remarks>
    public const string RestartAction = "restart";

    /// <summary>The permission every power action checks.</summary>
    /// <remarks>
    ///     <c>write</c>: turning a machine off is the authority a PUT on it already carries, and nothing
    ///     leaves the platform through any of the three.
    /// </remarks>
    public const string PowerPermission = "write";

    /// <summary>What every power action answers with: which action ran, and the run strategy before and after.</summary>
    /// <remarks>
    ///     Declared so the dispatcher checks the handler's body against it and the generated clients
    ///     have a shape to bind. ⚠ Nothing in it is secret, and the action is not <c>secret: true</c>: a
    ///     run strategy is a word a tenant can read off the machine anyway.
    /// </remarks>
    public static ResourceSchema PowerResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/action",
                    SchemaKind.Text,
                    true,
                    Description: "start, stop or restart — which one ran."
                ) { AllowedValues = [StartAction, StopAction, RestartAction] },
                new(
                    "/runStrategyBefore",
                    SchemaKind.Text,
                    true,
                    Description: "The machine's KubeVirt run strategy before the action: Always for a "
                    + "machine that should be on, Halted for one that should be off."
                ) { AllowedValues = [RunAlways, RunHalted] },
                new(
                    "/runStrategy",
                    SchemaKind.Text,
                    true,
                    Description: "The run strategy after the action. A restart leaves it as it was."
                ) { AllowedValues = [RunAlways, RunHalted] }
            ]
        );

    /// <summary>KubeVirt's run strategy for a machine that should be on.</summary>
    public const string RunAlways = "Always";

    /// <summary>KubeVirt's run strategy for a machine that should be off.</summary>
    public const string RunHalted = "Halted";

    // ── The objects a machine IS ──────────────────────────────────────────────────────────────

    /// <summary>The <c>VirtualMachine</c> — the desired machine.</summary>
    public static GroupVersionKind VirtualMachineKind { get; } =
        new() { Group = "kubevirt.io", Version = "v1", Kind = "VirtualMachine", Plural = "virtualmachines" };

    /// <summary>The <c>VirtualMachineInstance</c> — the running one, which KubeVirt creates and this platform only reads.</summary>
    public static GroupVersionKind InstanceKind { get; } =
        new() {
            Group = "kubevirt.io", Version = "v1", Kind = "VirtualMachineInstance", Plural = "virtualmachineinstances"
        };

    /// <summary>The object name — the resource's own, which KubeVirt also gives the instance.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ObjectNameOf(string name) => name;

    /// <summary>The <c>DataVolume</c> template holding the root disk, which KubeVirt creates from the VM.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Suffixed, because <see cref="Images.ObjectNameOf" /> and <see cref="Disks.ObjectNameOf" />
    ///     are both the bare resource name in the same namespace: a root disk called after its VM would
    ///     collide with a managed disk called after the same VM.
    /// </remarks>
    public static string RootDataVolumeName(string name) => name + "-root";

    /// <summary>The Secret that carries cloud-init user data, which the VM mounts by name.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string CloudInitSecretName(string name) => name + "-cloud-init";

    /// <summary>The key KubeVirt's <c>cloudInitNoCloud.secretRef</c> reads user data from.</summary>
    public const string CloudInitKey = "userdata";

    /// <summary>The <c>VirtualMachine</c> a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef VirtualMachineRef(string ns, string name) =>
        new() { Kind = VirtualMachineKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>The <c>VirtualMachineInstance</c> KubeVirt runs for it, under the same name.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef InstanceRef(string ns, string name) =>
        new() { Kind = InstanceKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>The cloud-init Secret a resource owns when its body names user data.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef CloudInitSecretRef(string ns, string name) => KubeSecret.Ref(ns, CloudInitSecretName(name));

    /// <summary>The volume and disk name of the root disk inside the VM spec.</summary>
    public const string RootVolume = "os";

    /// <summary>The volume and disk name of the cloud-init disk inside the VM spec.</summary>
    public const string CloudInitVolume = "cloudinit";

    /// <summary>
    ///     The annotation that puts the machine's interface on a Kube-OVN subnet.
    /// </summary>
    /// <remarks>
    ///     The same key <c>LoadBalancers.LogicalSwitchAnnotation</c> uses for a proxy pod, on the VM's
    ///     pod template: KubeVirt copies template annotations onto the launcher pod, and Kube-OVN reads
    ///     the pod's. With <c>bridge</c> binding the address the fabric hands the pod is the address
    ///     the guest sees.
    /// </remarks>
    public const string LogicalSwitchAnnotation = "ovn.kubernetes.io/logical_switch";

    // ── Sizes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The closed set of sizes a machine may have, each a vCPU count and a guest memory quantity.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The <c>s1</c> rungs of docs/plan/12 § Sizing vocabulary that are whole cores, and
    ///             nothing under a core.
    ///         </b> KubeVirt's <c>domain.cpu.cores</c> is an integer, so the
    ///         <c>nano</c> and <c>micro</c> presets other families offer as <c>250m</c> and <c>500m</c>
    ///         requests have no guest to map onto here without a request/limit split this row does not
    ///         make. Four rungs, 1:4, from one core to eight.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The same numbers reach quota and reach the guest</b>, which is what
    ///         <c>AgentPools.Resources</c> could not say of its table. The chart's <c>_helpers.tpl</c>
    ///         carries the same four rows and <c>ComputeChartDriftTests</c> compares them.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (int Cores, string Memory)> Sizes { get; } =
        new Dictionary<string, (int Cores, string Memory)>(StringComparer.Ordinal) {
            ["s1.small"] = (1, "4Gi"),
            ["s1.medium"] = (2, "8Gi"),
            ["s1.large"] = (4, "16Gi"),
            ["s1.xlarge"] = (8, "32Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The size names, ordered, as the schema offers them.</summary>
    public static ImmutableArray<string> SizeNames { get; } = [.. Sizes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The size a body gets when it names none.</summary>
    public const string DefaultSize = "s1.small";

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A vault handle as one string: <c>path#field</c>, optionally <c>@version</c>. Empty allowed.
    /// </summary>
    /// <remarks>
    ///     The same spelling <c>SecretRef.ToString</c> produces and <c>CommunicationChannels</c> accepts.
    ///     ⚠ A handle and never a value: docs/plan/13 § Virtual Machines,
    ///     <i>
    ///         "SSH keys and passwords are
    ///         SecretRefs resolved at render and never stored in grain state or in the CR's plaintext"
    ///     </i>.
    ///     The value is resolved once per pass, written into a Secret in the tenant's namespace, and
    ///     the <c>VirtualMachine</c> object names the Secret.
    /// </remarks>
    public const string OptionalSecretRefPattern = @"([^#@\s]+#[^#@\s]+(@[^#@\s]+)?)?";

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No <c>powerState</c>, no <c>deallocate</c>, no <c>snapshot</c>, no live resize.</b>
    ///         The first is the class remarks' argument; the second is <see cref="StopAction" />'s; a
    ///         snapshot is a <c>VirtualMachineSnapshot</c> against a <c>VolumeSnapshotClass</c> the
    ///         bundle's node-local storage does not have; and docs/plan/13 rules live resize out by
    ///         name. <c>size</c> is mutable and takes effect at the next start, which is the resize
    ///         that document does offer.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The network is two names in the same resource group and the join is an annotation
    ///             the fabric reads
    ///         </b>, so on a cluster with no Kube-OVN the annotation is inert and the
    ///         machine sits on the pod network. Nothing here checks that the subnet exists — that is
    ///         another resource's body — and a name that is not a subnet is a pod that never schedules,
    ///         which is <c>LoadBalancers</c>' behaviour too.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the machine is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The machine's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster the machine runs in. Must be the one its image and its "
                    + "disks are in — nothing checks that, and a machine placed elsewhere clones a "
                    + "claim that is not there."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/size",
                    SchemaKind.Text,
                    true,
                    Description: "The machine's size, from the platform's sizing catalogue: s1.small is 1 "
                    + "vCPU and 4 GiB, and each rung doubles both. Changing it takes effect the next "
                    + "time the machine starts — KubeVirt reports RestartRequired until then."
                ) {
                    AllowedValues = SizeNames, Widget = WidgetHint.CozyPreset, DefaultJson = "\"" + DefaultSize + "\""
                },
                new(
                    "/properties/image",
                    SchemaKind.Text,
                    true,
                    Description: "The CyberCloud.Compute/images resource the root disk is cloned from, "
                    + "by name, in this resource group. ⚠ The image must have finished importing: "
                    + "the machine waits for it and says so."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultImage + "\"",
                    ExampleJson = "\"" + DefaultImage + "\""
                },
                new(
                    "/properties/osDiskSize",
                    SchemaKind.Text,
                    true,
                    Description: "The root disk, in Kubernetes quantity form. At least the image's own "
                    + "size; a clone into a smaller claim is refused by CDI, not by this API. "
                    + "⚠ Immutable, for the reason a managed disk's size is."
                ) {
                    Pattern = KubeQuantity.Pattern,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultOsDiskSize + "\"",
                    ExampleJson = "\"20Gi\""
                },
                new(
                    "/properties/dataDisks",
                    SchemaKind.Array,
                    Description: "CyberCloud.Compute/disks resources attached to the machine, by name, "
                    + "in this resource group. A change attaches or detaches at the machine's next "
                    + "start. ⚠ A disk named os or cloudinit collides with the machine's own volumes "
                    + "and is refused."
                ) {
                    // ⚠ NO Pattern AND NO MaxLength, AND NOT BECAUSE THE ELEMENTS ARE FREE. The registry
                    // would apply both per element (SchemaProperty.ElementKind's remarks) and Validate
                    // would enforce them at PUT — and ./build.sh Charts refuses `@pattern` and `@length`
                    // on a `{array}` @param, the standing gap charts/managed/kafka/conformance.yaml
                    // records as `cidr-shape-is-unenforced`. So each element is checked by
                    // DataDiskProblem on the first reconcile pass instead, before a claim name is
                    // rendered; conformance.yaml § owed, `data-disk-names-are-checked-at-reconcile`.
                    ElementKind = SchemaKind.Text, DefaultJson = "[]", ExampleJson = """["data"]"""
                },
                new(
                    "/properties/network",
                    SchemaKind.Nested,
                    Description: "The tenant network the machine's interface joins. Both empty means "
                    + "the cluster's pod network."
                ),
                new(
                    "/properties/network/virtualNetwork",
                    SchemaKind.Text,
                    Description: "The CyberCloud.Network/virtualNetworks resource in this resource group, "
                    + "by name, or empty."
                ) {
                    Pattern = OptionalNamePattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/network/subnet",
                    SchemaKind.Text,
                    Description: "The subnet of that network the interface takes its address from, by "
                    + "name, or empty. ⚠ A name that is not a subnet of the network is refused by the "
                    + "fabric rather than by this API, and the machine never starts."
                ) {
                    Pattern = OptionalNamePattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Widget = WidgetHint.Subnet,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/cloudInit",
                    SchemaKind.Nested,
                    Description: "First-boot configuration, as cloud-init reads it."
                ),
                new(
                    "/properties/cloudInit/userData",
                    SchemaKind.Text,
                    Description: "A vault handle — path#field, optionally @version — whose value is the "
                    + "cloud-init user data: the #cloud-config with your users, SSH keys and packages. "
                    + "Resolved when the machine is rendered and written into a Secret the machine "
                    + "mounts; the value never enters this body. ⚠ The path must be under your own "
                    + "tenant's vault prefix, tenants/<tenantId>/; any other path is refused. Empty "
                    + "means no cloud-init at all."
                ) {
                    Pattern = OptionalSecretRefPattern,
                    MaxLength = 512,
                    Widget = WidgetHint.SecretRef,
                    DefaultJson = "\"\""
                }
            ]
        );

    /// <summary>A resource name or nothing.</summary>
    const string OptionalNamePattern = "(" + ResourceNaming.Pattern + ")?";

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The size a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Size(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "size"), DefaultSize);

    /// <summary>What one machine's CPU and memory are — declared, not believed.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static (int Cores, string Memory) Resources(JsonElement desired) =>
        Sizes.TryGetValue(Size(desired), out var size) ? size : (Cores: 0, Memory: string.Empty);

    /// <summary>The image resource the root disk is cloned from.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Image(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "image"), string.Empty);

    /// <summary>The root disk size a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string OsDiskSize(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "osDiskSize"), DefaultOsDiskSize);

    /// <summary>The disks a body attaches, in body order, empty names dropped.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> DataDisks(JsonElement desired) =>
        [.. ComputeBodies.Strings(ComputeBodies.Property(desired, "dataDisks"))];

    /// <summary>
    ///     Why the body's <c>dataDisks</c> cannot be rendered, or empty when every entry can.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         Two checks the schema does not make. Each entry must be a resource name — the schema
    ///         declares no per-element pattern, because the chart surface cannot carry one (the
    ///         remark on the property says why), so without this any string a body carried reached
    ///         <c>volumes[].persistentVolumeClaim.claimName</c> unvalidated and only a real API
    ///         server's own name rules stood in the way. And no entry may be <c>os</c> or
    ///         <c>cloudinit</c>, which are legal names the machine's own volumes already use: a body
    ///         naming one renders two disks with one name, which KubeVirt's webhook refuses and a
    ///         derived stub admits.
    ///     </para>
    ///     <para>
    ///         The reconciler refuses on the first pass with the property named, rather than letting
    ///         both conformance suites converge on a machine a real cluster would never take. Not at
    ///         PUT, for the reason <see cref="ParseCloudInitRef" /> gives: the write path has no
    ///         per-type validator.
    ///     </para>
    /// </remarks>
    public static string DataDiskProblem(JsonElement desired) {
        foreach (var disk in DataDisks(desired)) {
            if (disk is RootVolume or CloudInitVolume) {
                return $"dataDisks names '{disk}', which is the name of the machine's own "
                    + $"{(disk == RootVolume ? "root" : "cloud-init")} volume. Rename the disk.";
            }

            if (ResourceNaming.Validate(disk, "disk name", "/properties/dataDisks").TryGetError(out var problem)) {
                return problem.Message;
            }
        }

        return string.Empty;
    }

    /// <summary>The virtual network a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string VirtualNetwork(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "network", "virtualNetwork"), string.Empty);

    /// <summary>The subnet a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Subnet(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "network", "subnet"), string.Empty);

    /// <summary>The cloud-init vault handle a body spells, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string CloudInitRef(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "cloudInit", "userData"), string.Empty);

    /// <summary>Whether a body asks for a cloud-init disk at all.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool HasCloudInit(JsonElement desired) => CloudInitRef(desired).Length > 0;

    /// <summary>The vault prefix every path a tenant's body names must start with.</summary>
    /// <param name="tenantId">The tenant whose resource carries the handle.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE ONLY PLACE IN THE TREE WHERE A TENANT-SPELLED VAULT PATH IS RESOLVED, SO THE
    ///             ONLY PLACE THAT HAS TO SAY WHOSE PATHS A TENANT MAY SPELL.
    ///         </b> Every other consumer of
    ///         <c>ISecretResolver</c> resolves a path the platform built itself —
    ///         <c>ContainerRegistries.SecretPath</c> and its four siblings all spell
    ///         <c>tenants/{tenantId}/{provider}/{type}/{id}</c> — or keeps the value server-side. A
    ///         cloud-init handle is written by the tenant, resolved by the platform's one broad vault
    ///         token (<c>OpenBaoSecretResolver</c>'s remarks: a namespace per <i>platform</i>, not per
    ///         tenant, so the path is the whole discriminator), and its VALUE is written into a Secret
    ///         the tenant's own guest mounts. Without this prefix a body naming
    ///         <c>tenants/&lt;other&gt;/CyberCloud.ContainerRegistry/registries/&lt;id&gt;#password</c>
    ///         would hand another tenant's credential to a guest through cloud-init — found by the
    ///         adversarial review of #28, before any production resolver had served this type.
    ///     </para>
    ///     <para>
    ///         The same spelling the five platform-built paths use, so a tenant's own credentials —
    ///         the ones <c>listKeys</c> and <c>listCredentials</c> already hand them — are inside the
    ///         prefix and every other tenant's are outside it. <see cref="ParseCloudInitRef" />
    ///         refuses before anything is resolved, and
    ///         <c>VirtualMachineReconcilerTests.AHandleOutsideTheTenantsOwnVaultPrefixIsRefusedBeforeItIsResolved</c>
    ///         holds a seeded vault to that.
    ///     </para>
    /// </remarks>
    public static string TenantVaultPrefix(Guid tenantId) =>
        string.Create(CultureInfo.InvariantCulture, $"tenants/{tenantId:D}/");

    /// <summary>
    ///     Parses <c>path#field[@version]</c>, refusing a path outside the tenant's own vault prefix,
    ///     or returns the empty handle for an empty string.
    /// </summary>
    /// <param name="spelled">The handle as a body spells it.</param>
    /// <param name="tenantId">
    ///     The tenant whose resource carries the handle — the only tenant whose paths it may name.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The same parse <c>CommunicationChannels.ParseSecretRef</c> does, spelled again here
    ///         because rule 2 keeps one family's <c>.Contracts</c> out of another's, and the schema's
    ///         pattern already refuses most of what this refuses. What it adds is the refusal's target
    ///         and the tenancy check <see cref="TenantVaultPrefix" /> explains.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Refused with <see cref="ErrorCode.AuthorizationFailed" /> on the first reconcile
    ///             pass and not at PUT
    ///         </b>, because the write path validates a body against its schema and
    ///         nothing else — <c>IProviderBuilder</c> has no per-type validator — and a schema pattern
    ///         cannot carry the caller's tenant id. The refusal names the tenant's own prefix and the
    ///         path as spelled, never whether that path exists, so a probe learns nothing about what
    ///         another tenant's vault holds.
    ///     </para>
    /// </remarks>
    public static Result<SecretRef> ParseCloudInitRef(string spelled, Guid tenantId) {
        if (string.IsNullOrWhiteSpace(spelled)) {
            return Result<SecretRef>.Success(new());
        }

        var hash = spelled.IndexOf('#', StringComparison.Ordinal);

        if (hash <= 0 || hash == spelled.Length - 1) {
            return Result<SecretRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a vault handle. Write path#field, optionally @version — the "
                + "spelling SecretRef prints.",
                "/properties/cloudInit/userData"
            );
        }

        var path = spelled[..hash];
        var prefix = TenantVaultPrefix(tenantId);

        // ⚠ SecretRef.IsConfinedTo and not StartsWith alone: `tenants/{mine}/../{theirs}/x` starts
        // with this tenant's prefix, and the resolver's HTTP client collapses the dot segments into
        // the other tenant's path. Found by the #34 review in the mailbox's copy of this check, which
        // was copied from here.
        if (!SecretRef.IsConfinedTo(path, prefix)) {
            return Result<SecretRef>.Failure(
                ErrorCode.AuthorizationFailed,
                $"cloudInit.userData names '{path}', which is not under your tenant's vault prefix "
                + $"'{prefix}'. A machine can only be given a value your own tenant holds, and the path "
                + "may not contain an empty, '.' or '..' segment.",
                "/properties/cloudInit/userData"
            );
        }

        var rest = spelled[(hash + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);

        return Result<SecretRef>.Success(
            new() {
                Path = path, Field = at < 0 ? rest : rest[..at], Version = at < 0 ? string.Empty : rest[(at + 1)..]
            }
        );
    }

    /// <summary>
    ///     The Kube-OVN <c>Subnet</c> object the machine's interface joins, or empty for the pod
    ///     network.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         <c>{namespace}-{network}-{subnet}</c>, which is <c>NetworkSubnets.ObjectNameOf</c>'s
    ///         rule spelled a second time
    ///     </b> — rule 2 of docs/plan/03 § Assembly graph rules forbids the
    ///     reference that would spell it once, and <c>charts/managed/kube-ovn-vpc/conformance.yaml</c>'s
    ///     <c>nothing-can-join-a-network-yet</c> predicted exactly this consumer. A test project is
    ///     outside the rule, and <c>ComputeNetworkJoinTests</c> holds the two spellings together.
    ///     Either name empty means no annotation: half a join is the pod network, not a guess.
    /// </remarks>
    public static string LogicalSwitchOf(string ns, JsonElement desired) {
        var network = VirtualNetwork(desired);
        var subnet = Subnet(desired);

        return network.Length == 0 || subnet.Length == 0 ? string.Empty : ns + "-" + network + "-" + subnet;
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>VirtualMachine</c> a desired body becomes, at a given run strategy.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="runStrategy">
    ///     <see cref="RunAlways" /> or <see cref="RunHalted" /> — what the object already says, or
    ///     <see cref="RunAlways" /> for a machine that does not exist yet. See <see cref="RunStrategyOf" />.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>runStrategy</c> is a parameter and not read off the body, on purpose</b> — the
    ///         class remarks say why. KubeVirt's validating webhook refuses a VM with neither
    ///         <c>running</c> nor <c>runStrategy</c> (<c>vms-admitter.go</c>, "RunStrategy must be
    ///         specified"), so it cannot be left out and owned by somebody else.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>bridge</c> binding on the pod network, as the node pool renders and as Kube-OVN's
    ///             own KubeVirt guide does
    ///         </b>: with a logical-switch annotation the guest gets the fabric's
    ///         address on its own interface, and without one it gets the pod's. <c>masquerade</c> would
    ///         work on a plain cluster and break the tenant network.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The root disk boots first, by <c>bootOrder</c>, and the cloud-init disk is last</b> —
    ///         a data disk with a stray boot sector must not win, and cloud-init's NoCloud source is
    ///         found by label rather than by position.
    ///     </para>
    /// </remarks>
    public static string VirtualMachineJson(string ns, string name, JsonElement desired, string runStrategy) {
        var (cores, memory) = Resources(desired);
        var objectName = ObjectNameOf(name);

        var disks = new JsonArray(
            new JsonObject {
                ["name"] = RootVolume, ["disk"] = new JsonObject { ["bus"] = "virtio" }, ["bootOrder"] = 1
            }
        );

        var volumes = new JsonArray(
            new JsonObject {
                ["name"] = RootVolume, ["dataVolume"] = new JsonObject { ["name"] = RootDataVolumeName(objectName) }
            }
        );

        foreach (var disk in DataDisks(desired)) {
            disks.Add(new JsonObject { ["name"] = disk, ["disk"] = new JsonObject { ["bus"] = "virtio" } });

            volumes.Add(
                new JsonObject {
                    ["name"] = disk,
                    ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = Disks.ObjectNameOf(disk) }
                }
            );
        }

        if (HasCloudInit(desired)) {
            disks.Add(new JsonObject { ["name"] = CloudInitVolume, ["disk"] = new JsonObject { ["bus"] = "virtio" } });

            volumes.Add(
                new JsonObject {
                    ["name"] = CloudInitVolume,
                    ["cloudInitNoCloud"] = new JsonObject {
                        ["secretRef"] = new JsonObject { ["name"] = CloudInitSecretName(objectName) }
                    }
                }
            );
        }

        var templateMetadata = new JsonObject();
        var logicalSwitch = LogicalSwitchOf(ns, desired);

        if (logicalSwitch.Length > 0) {
            templateMetadata["annotations"] = new JsonObject { [LogicalSwitchAnnotation] = logicalSwitch };
        }

        return new JsonObject {
            ["kind"] = VirtualMachineKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = objectName },
            ["spec"] = new JsonObject {
                ["runStrategy"] = runStrategy,
                ["dataVolumeTemplates"] = new JsonArray(
                    new JsonObject {
                        ["metadata"] = new JsonObject { ["name"] = RootDataVolumeName(objectName) },
                        ["spec"] = RootDataVolumeSpec(ns, desired)
                    }
                ),
                ["template"] = new JsonObject {
                    ["metadata"] = templateMetadata,
                    ["spec"] = new JsonObject {
                        ["domain"] = new JsonObject {
                            ["cpu"] = new JsonObject { ["cores"] = cores },
                            ["memory"] = new JsonObject { ["guest"] = memory },
                            ["devices"] = new JsonObject {
                                ["disks"] = disks,
                                ["interfaces"] = new JsonArray(
                                    new JsonObject { ["name"] = "default", ["bridge"] = new JsonObject() }
                                )
                            }
                        },
                        ["networks"] = new JsonArray(
                            new JsonObject { ["name"] = "default", ["pod"] = new JsonObject() }
                        ),
                        ["volumes"] = volumes,
                        // ⚠ Long enough for a guest to flush on ACPI power-off, short enough that a
                        // hung guest does not hold a stop for minutes. KubeVirt's own default is 30.
                        ["terminationGracePeriodSeconds"] = TerminationGracePeriodSeconds
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The seconds KubeVirt waits between ACPI power-off and killing the guest.</summary>
    public const int TerminationGracePeriodSeconds = 60;

    /// <summary>The root disk's <c>DataVolume</c> spec: a clone of the image's claim, at the body's OS disk size.</summary>
    /// <param name="ns">The resource's namespace, which is the image's too.</param>
    /// <param name="desired">The validated desired body.</param>
    static JsonObject RootDataVolumeSpec(string ns, JsonElement desired) =>
        new() {
            ["storage"] = Cdi.Storage(OsDiskSize(desired), string.Empty),
            ["source"] = new JsonObject {
                ["pvc"] = new JsonObject { ["name"] = Images.ObjectNameOf(Image(desired)), ["namespace"] = ns }
            }
        };

    /// <summary>
    ///     The root disk's <c>DataVolume</c> as KubeVirt's controller creates it from
    ///     <c>dataVolumeTemplates[0]</c> once the machine is admitted: the template's spec, owned by
    ///     the machine, at the phase given.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="phase">CDI's phase for it — <see cref="Cdi.Succeeded" /> for a clone that finished.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing on the reconcile path calls this.</b> The reconciler never applies the
    ///         root disk — <c>virt-controller</c> does, from the template — and never reads it back;
    ///         the <c>DataVolume</c> it reads is the <em>image's</em>. This is what the conformance
    ///         case declares in <c>OperatorWritten</c>, the way the vault declares the <c>Backup</c>
    ///         its operator makes: the object as the operator would write it, with an owner reference
    ///         naming the machine by kind and name and an empty uid the harness fills in.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Declaring it is what makes the k3s lane serve the kind, and the need arrived with
    ///             #91.
    ///         </b> The cluster-backed harness installs a definition for every kind the case's
    ///         objects and operator-written objects name and nothing else. The machine's objects name
    ///         <c>VirtualMachine</c> alone, so a k3s with no CDI served no <c>DataVolume</c> path — and
    ///         the image read, which used to come back <c>NotFound</c> and let the reconciler proceed
    ///         on an absent image, comes back since #91 as "the kind is missing from the cluster, not
    ///         the object", a named failure rather than an absence. Every lifecycle assertion for the
    ///         machine failed on it the day #28 met that harness (2026-09-18). With the root disk
    ///         declared, CDI's definition is served, the image read is a real <c>NotFound</c>, and the
    ///         reconciler's own rule for an absent image is what the lane measures again.
    ///     </para>
    /// </remarks>
    public static string RootDataVolumeJson(string ns, string name, JsonElement desired, string phase) {
        var objectName = ObjectNameOf(name);

        return new JsonObject {
            ["apiVersion"] = Cdi.DataVolumeKind.ApiVersion,
            ["kind"] = Cdi.DataVolumeKind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = RootDataVolumeName(objectName),
                ["namespace"] = ns,
                ["ownerReferences"] = new JsonArray(
                    KubeJson.OwnerReference(
                        new() {
                            ApiVersion = VirtualMachineKind.ApiVersion,
                            Kind = VirtualMachineKind.Kind,
                            Name = objectName,
                            Uid = string.Empty
                        }
                    )
                )
            },
            ["spec"] = RootDataVolumeSpec(ns, desired),
            ["status"] = new JsonObject { ["phase"] = phase }
        }.ToJsonString();
    }

    /// <summary>The cloud-init Secret a resolved handle becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="userData">The resolved user data — the value, which goes here and nowhere else.</param>
    /// <remarks>
    ///     <c>data</c> with the base64 written out rather than <c>stringData</c>, for the reason
    ///     <c>ContainerRegistries.CredentialsSecretJson</c> gives: <c>stringData</c> is write-only and a
    ///     read-back would come in a shape no real cluster produces.
    /// </remarks>
    public static string CloudInitSecretJson(string name, string userData) {
        ArgumentNullException.ThrowIfNull(userData);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = CloudInitSecretName(ObjectNameOf(name)) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject { [CloudInitKey] = Convert.ToBase64String(Encoding.UTF8.GetBytes(userData)) }
        }.ToJsonString();
    }

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The run strategy a <c>VirtualMachine</c> read back carries, or empty when the object has
    ///     none — which a real API server never returns.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         This is where the power state lives, and the reconciler reads it before every
    ///         render.
    ///     </b> <c>Always</c> or <c>Halted</c> is what a power action last wrote, or what an
    ///     operator's <c>virtctl</c> wrote — and the reconciler preserves either, because a pass that
    ///     turned a machine back on while correcting an unrelated field would be the drift correction
    ///     tenants complain about.
    /// </remarks>
    public static string RunStrategyOf(string objectJson) =>
        ComputeBodies.TextOf(ComputeBodies.Spec(objectJson)?["runStrategy"]);

    /// <summary>
    ///     Whether a <c>VirtualMachine</c> read back carries what the desired body asks for, whatever
    ///     its run strategy.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Containment, and <c>runStrategy</c> is deliberately not compared</b> — it is the
    ///         power state, and a stopped machine still carries its desired spec. KubeVirt's mutating
    ///         webhook writes <c>architecture</c>, a machine type and a disk bus default into the
    ///         template, so an equality comparison fails against every real cluster and passes in both
    ///         conformance suites.
    ///     </para>
    ///     <para>
    ///         What is compared is what a tenant chose: the cores, the guest memory, the image the root
    ///         clones and its size, every attached disk by claim name, whether a cloud-init volume is
    ///         mounted, and the logical switch when the body names one.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, string ns, JsonElement desired) {
        if (ComputeBodies.Kind(objectJson) != VirtualMachineKind.Kind
            || ComputeBodies.Spec(objectJson) is not { } spec) {
            return false;
        }

        var (cores, memory) = Resources(desired);
        var template = spec["template"]?["spec"] as JsonObject;
        var domain = template?["domain"] as JsonObject;

        if (ComputeBodies.WholeOf(domain?["cpu"]?["cores"]) != cores
            || ComputeBodies.TextOf(domain?["memory"]?["guest"]) != memory) {
            return false;
        }

        var root = (spec["dataVolumeTemplates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();

        if (root is null
            || ComputeBodies.TextOf(root["spec"]?["source"]?["pvc"]?["name"]) != Images.ObjectNameOf(Image(desired))
            || Cdi.RequestedSize(root["spec"] as JsonObject) != OsDiskSize(desired)) {
            return false;
        }

        var volumes = (template?["volumes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        var claims = volumes
            .Select(static x => ComputeBodies.TextOf(x["persistentVolumeClaim"]?["claimName"]))
            .Where(static x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var wanted = DataDisks(desired).Select(Disks.ObjectNameOf).ToHashSet(StringComparer.Ordinal);

        if (!claims.SetEquals(wanted)) {
            return false;
        }

        var hasCloudInit = volumes.Exists(static x => x["cloudInitNoCloud"] is JsonObject);

        if (hasCloudInit != HasCloudInit(desired)) {
            return false;
        }

        var logicalSwitch = LogicalSwitchOf(ns, desired);

        return logicalSwitch.Length == 0
            || ComputeBodies.TextOf(spec["template"]?["metadata"]?["annotations"]?[LogicalSwitchAnnotation])
            == logicalSwitch;
    }

    /// <summary>What KubeVirt says about a machine, read off its <c>status</c>.</summary>
    /// <param name="objectJson">The <c>VirtualMachine</c>'s JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     <para>
    ///         Three outcomes, as <c>ManagedClusters.Readiness</c> has: <see cref="ReadinessKind.Ready" />
    ///         when <c>printableStatus</c> is <c>Running</c>, or <c>Stopped</c> on a machine whose run
    ///         strategy is <see cref="RunHalted" />; <see cref="ReadinessKind.NotReported" /> when the
    ///         object carries no <c>printableStatus</c> at all, which is a cluster with no KubeVirt
    ///         controller behind the definition; and <see cref="ReadinessKind.NotReady" /> otherwise,
    ///         carrying KubeVirt's own status word and, when the instance could not be placed, the
    ///         scheduler's own sentence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The scheduler's sentence is the one a tenant on a node without KVM needs.</b>
    ///         <c>ErrorUnschedulable</c> alone says a machine is stuck;
    ///         <i>
    ///             "Insufficient
    ///             devices.kubevirt.io/kvm"
    ///         </i> says the node has no KVM device to give — the state a
    ///         real node without nested virtualization leaves a machine in, and the one
    ///         <c>charts/bundle/bundle.yaml § owed</c>, <c>virtual-machines-need-a-node-with-kvm</c>,
    ///         predicted for the k3s-in-Docker lane before the lane was measured. KubeVirt mirrors the
    ///         launcher pod's <c>PodScheduled</c> condition onto the VM's <c>status.conditions</c>,
    ///         message included, so it is read from there.
    ///     </para>
    /// </remarks>
    public static Readiness ReadinessOf(string objectJson) {
        var status = ComputeBodies.Status(objectJson);
        var printable = ComputeBodies.TextOf(status?["printableStatus"]);

        if (printable.Length == 0) {
            return new(ReadinessKind.NotReported, "KubeVirt has not reported on the machine yet");
        }

        if (printable == "Running") {
            return new(ReadinessKind.Ready, "the machine is running");
        }

        if (printable == "Stopped" && RunStrategyOf(objectJson) == RunHalted) {
            return new(ReadinessKind.Ready, "the machine is stopped, as asked");
        }

        // ⚠ The scheduler's sentence first and KubeVirt's own Failure second: the first is why a
        // machine cannot be placed, the second is why KubeVirt could not create something for it —
        // a clone refused by CDI's admission, most often — and a status carries at most one of them.
        var message = ComputeBodies.TextOf(ComputeBodies.Condition(status, "PodScheduled")?["message"]);

        if (message.Length == 0) {
            message = ComputeBodies.TextOf(ComputeBodies.Condition(status, "Failure")?["message"]);
        }

        return new(
            ReadinessKind.NotReady,
            message.Length > 0 ? $"KubeVirt reports {printable}: {message}" : $"KubeVirt reports {printable}"
        );
    }

    /// <summary>The <c>status.phase</c> of a <c>VirtualMachineInstance</c>, or empty.</summary>
    /// <param name="instanceJson">The instance's JSON, exactly as the API server returned it.</param>
    public static string InstancePhase(string instanceJson) =>
        ComputeBodies.TextOf(ComputeBodies.Status(instanceJson)?["phase"]);

    /// <summary>The <c>status.printableStatus</c> of a <c>VirtualMachine</c>, or empty.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    public static string PrintableStatus(string objectJson) =>
        ComputeBodies.TextOf(ComputeBodies.Status(objectJson)?["printableStatus"]);

    /// <summary>KubeVirt's verdict on a machine, reduced to the three answers a reconciler acts on.</summary>
    /// <param name="Kind">Which of the three.</param>
    /// <param name="Detail">The words to report, KubeVirt's own where it has any.</param>
    public readonly record struct Readiness(ReadinessKind Kind, string Detail);

    /// <summary>The three answers <see cref="ReadinessOf" /> gives.</summary>
    public enum ReadinessKind {
        /// <summary>The object carries no status; nothing has reported on it.</summary>
        NotReported,

        /// <summary>KubeVirt reports the machine is not where its run strategy wants it.</summary>
        NotReady,

        /// <summary>Running when asked to run, stopped when asked to stop.</summary>
        Ready
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the machine runs in.</param>
    /// <param name="image">The image resource the root disk clones.</param>
    /// <param name="size">The sizing preset.</param>
    /// <param name="osDiskSize">The root disk size.</param>
    /// <param name="dataDisks">Disk resources to attach.</param>
    /// <param name="virtualNetwork">The virtual network, or empty.</param>
    /// <param name="subnet">The subnet, or empty.</param>
    /// <param name="cloudInit">The cloud-init vault handle, or empty.</param>
    /// <param name="location">The region.</param>
    /// <remarks>⚠ Every property it writes is a <b>leaf</b>, for the reason <c>StorageAccounts.Body</c> gives.</remarks>
    public static string Body(
        Guid clusterId,
        string image = DefaultImage,
        string size = DefaultSize,
        string osDiskSize = DefaultOsDiskSize,
        IEnumerable<string>? dataDisks = null,
        string virtualNetwork = "",
        string subnet = "",
        string cloudInit = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["size"] = size,
                ["image"] = image,
                ["osDiskSize"] = osDiskSize,
                ["dataDisks"] = new JsonArray([.. (dataDisks ?? []).Select(static x => (JsonNode)JsonValue.Create(x))]),
                ["network"] = new JsonObject { ["virtualNetwork"] = virtualNetwork, ["subnet"] = subnet },
                ["cloudInit"] = new JsonObject { ["userData"] = cloudInit }
            }
        }.ToJsonString();

    const string DefaultOsDiskSize = "20Gi";

    /// <summary>
    ///     The image name the chart's own defaults carry. ⚠ A chart default and not a platform one:
    ///     <c>image</c> is required, so every body names its own, and this is what <c>helm lint</c>
    ///     validates the chart's <c>values.yaml</c> against — the pattern refuses an empty string.
    /// </summary>
    public const string DefaultImage = "ubuntu";
}
