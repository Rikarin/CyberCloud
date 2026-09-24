using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Compute/virtualMachineScaleSets</c> — a replica count
///     over one machine template, rendered as KubeVirt's <c>VirtualMachinePool</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/13 § Scale sets, as written: "a replica count over one template", and not an
///         autoscaler.</b> The pool KubeVirt ships is exactly that shape — <c>spec.replicas</c> machines
///         named <c>{pool}-{index}</c>, each a <c>VirtualMachine</c> the pool controller creates from
///         <c>spec.virtualMachineTemplate</c> and owns — and the pinned release (v1.9.0) serves it at
///         <c>pool.kubevirt.io/v1beta1</c>, measured off a cluster <c>charts/bundle/install.sh</c>
///         installed it on (2026-09-23; <c>v1alpha1</c> is still served and is not the storage
///         version). So the question <c>charts/managed/virtual-machine/conformance.yaml</c> left open —
///         render the pool, or fan a count out into machines this provider owns and lists — is
///         answered by the operator: the pool's own controller does the fan-out, the index, the
///         per-instance root disk and the rolling update, and a second implementation of each here
///         would be the one that is wrong.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE BODY IS A MACHINE'S BODY WITHOUT <c>dataDisks</c>, PLUS A CAPACITY AND AN UPGRADE
///             POLICY — AND THE SCHEMA IS DERIVED FROM THE MACHINE'S RATHER THAN WRITTEN AGAIN.
///         </b> <see cref="Schema2026" /> is <see cref="VirtualMachines.Schema2026" />'s properties with
///         one removed and three added, so <see cref="VirtualMachines.Size" />,
///         <see cref="VirtualMachines.Image" />, <see cref="VirtualMachines.ParseCloudInitRef" /> and
///         <see cref="VirtualMachines.VirtualMachineJson" /> read a scale set's body exactly as they read
///         a machine's, and the machine every instance is cannot drift from the machine a tenant creates
///         one at a time. ⚠ No <c>dataDisks</c>, because a managed disk is one <c>ReadWriteOnce</c>
///         claim and a template is N machines: every instance would name the same claim, and the second
///         one to start is refused by the scheduler. Per-instance data disks are a
///         <c>dataVolumeTemplates</c> entry the pool indexes — <c>conformance.yaml § owed</c>,
///         <c>no-per-instance-data-disks</c>.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE REPLICA COUNT IS ON THE OBJECT, LIKE A MACHINE'S POWER STATE, AND <c>capacity</c>
///             IS ITS CEILING.
///         </b> An action cannot change a desired body — <see cref="VirtualMachines" />'
///         class remarks carry the argument — so <see cref="ScaleAction" /> writes
///         <c>spec.replicas</c> on the pool and the reconciler reads it back before every render, as
///         it reads a machine's run strategy. What the body holds is <c>capacity</c>: the count a new
///         set starts at, the most the action may scale to, and what quota reserves — capacity times
///         one machine. A PUT that raises it reserves more first, which is docs/plan/06 § Quota's
///         "reserved before create" kept for scale-out; one that lowers it below the live count
///         brings the pool down to it on the next pass. The honest cost: a set scaled to two of a
///         capacity of five is reserved at five — <c>conformance.yaml § owed</c>,
///         <c>quota-is-reserved-at-capacity</c>, the scale set's half of <c>deallocate-is-stop</c>.
///     </para>
///     <para>
///         ⚠ <b>The upgrade policy is the pool's own <c>updateStrategy</c>, rendered and not merely
///         recorded.</b> <see cref="ManualUpgrade" /> is <c>unmanaged</c> — a template change reaches
///         instances created afterwards and nothing else; <see cref="OnRestartUpgrade" /> is
///         <c>opportunistic</c> — each machine's spec is updated and its guest picks it up at its next
///         restart; <see cref="RollingUpgrade" /> is <c>proactive</c> with <c>maxUnavailable</c> —
///         the pool controller restarts machines onto the new template, at most that many at a time.
///         Read off the served definition's own descriptions (v1.9.0, 2026-09-23).
///     </para>
/// </remarks>
public static class VirtualMachineScaleSets {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = VirtualMachines.ProviderNamespace;

    /// <summary>The type path.</summary>
    public const string TypePath = "virtualMachineScaleSets";

    /// <summary>The one api-version.</summary>
    public const string V2026 = VirtualMachines.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/virtual-machine-scale-set";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The <c>VirtualMachinePool</c> — the set.</summary>
    public static GroupVersionKind PoolKind { get; } =
        new() { Group = "pool.kubevirt.io", Version = "v1beta1", Kind = "VirtualMachinePool", Plural = "virtualmachinepools" };

    /// <summary>The object name — the resource's own, which the pool prefixes every instance with.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ObjectNameOf(string name) => name;

    /// <summary>The pool a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PoolRef(string ns, string name) =>
        new() { Kind = PoolKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>The name the pool controller gives the instance at <paramref name="index" />.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="index">The instance's index, from zero.</param>
    /// <remarks>
    ///     ⚠ <c>{pool}-{index}</c>, which is the namespace a standalone machine's name lives in too: a
    ///     <c>virtualMachines</c> resource called <c>web-0</c> and a scale set called <c>web</c> in one
    ///     resource group both want the object <c>web-0</c>. <c>conformance.yaml § owed</c>,
    ///     <c>instance-names-share-the-machines-namespace</c>.
    /// </remarks>
    public static string InstanceName(string name, int index) =>
        ObjectNameOf(name) + "-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Secret a set's resolved cloud-init becomes — one for every machine: <c>{set}-cloud-init-set</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         NOT <see cref="VirtualMachines.CloudInitSecretName" />, WHICH THIS TYPE FIRST SHARED, AND
    ///         THE REASON IS A MACHINE OF THE SAME NAME.
    ///     </b> #28's review: a <c>virtualMachines</c> resource <c>web</c> and a set <c>web</c> in one
    ///     resource group both wrote <c>web-cloud-init</c>, under one field manager
    ///     (<c>cybercloud/CyberCloud.Compute</c>), so neither apply conflicted — each overwrote the other's
    ///     user data and labels on every pass, and the set's delete, which removes the Secret whether or
    ///     not its body names cloud-init, removed the machine's. A machine's Secret always ends in
    ///     <c>-cloud-init</c> and this one never does, so no two names of the two types can meet.
    /// </remarks>
    public static string CloudInitSecretName(string name) => ObjectNameOf(name) + "-cloud-init-set";

    /// <summary>The cloud-init Secret a set owns when its body names user data.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef CloudInitSecretRef(string ns, string name) => KubeSecret.Ref(ns, CloudInitSecretName(name));

    /// <summary>The cloud-init Secret a set's resolved handle becomes — the machine's document under the set's name.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="userData">The resolved user data.</param>
    public static string CloudInitSecretJson(string name, string userData) {
        var secret = JsonNode.Parse(VirtualMachines.CloudInitSecretJson(name, userData))!;
        secret["metadata"]!["name"] = CloudInitSecretName(name);
        return secret.ToJsonString();
    }

    /// <summary>The longest resource name a set may have.</summary>
    /// <remarks>
    ///     ⚠ Sixty, not <see cref="ResourceNaming.MaxLength" />. An instance is <c>{name}-{index}</c>
    ///     and KubeVirt writes a machine's name into label values, which stop at 63; with
    ///     <see cref="MaxCapacity" /> under a hundred the index is two digits and the dash one more. The
    ///     reconciler refuses a longer name on the first pass, because the name pattern is the
    ///     platform's and cannot carry a per-type bound.
    /// </remarks>
    public const int MaxNameLength = 60;

    /// <summary>The most machines one set may hold.</summary>
    public const int MaxCapacity = 20;

    /// <summary>The label the pool selects its machines by, beside the platform's own.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The value of <see cref="NameLabel" /> on every instance.</summary>
    public const string NameLabelValue = "virtual-machine-scale-set";

    /// <summary>The label that says which set an instance belongs to.</summary>
    public const string InstanceLabel = "app.kubernetes.io/instance";

    /// <summary>The selector that finds a set's instances, in the form a list takes.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string InstanceSelector(string name) =>
        NameLabel + "=" + NameLabelValue + "," + InstanceLabel + "=" + ObjectNameOf(name);

    // ── The two actions ───────────────────────────────────────────────────────────────────────

    /// <summary>Sets how many machines the set runs, between zero and its capacity.</summary>
    public const string ScaleAction = "scale";

    /// <summary>Lists the machines the set runs and KubeVirt's word for each.</summary>
    public const string ListInstancesAction = "listInstances";

    /// <summary>The permission <see cref="ScaleAction" /> checks: turning machines on and off, as a power action does.</summary>
    public const string ScalePermission = "write";

    /// <summary>The permission <see cref="ListInstancesAction" /> checks.</summary>
    public const string ListInstancesPermission = "read";

    /// <summary>What <see cref="ScaleAction" /> takes.</summary>
    public static ResourceSchema ScaleRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/replicas",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "How many machines to run, from 0 to the set's capacity. ⚠ Above the "
                    + "capacity is refused: capacity is what quota reserved, and a PUT that raises it is "
                    + "how more is reserved."
                ) { Minimum = 0, Maximum = MaxCapacity }
            ]
        );

    /// <summary>What <see cref="ScaleAction" /> answers.</summary>
    public static ResourceSchema ScaleResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/replicasBefore",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "The pool's replica count before the action."
                ),
                new("/replicas", SchemaKind.WholeNumber, true, Description: "The replica count now."),
                new(
                    "/capacity",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "The ceiling the body sets and quota reserves."
                )
            ]
        );

    /// <summary>What <see cref="ListInstancesAction" /> answers.</summary>
    /// <remarks>
    ///     ⚠ <b>Two arrays in one order rather than an array of objects</b>, because an array of
    ///     objects is not expressible in a <see cref="ResourceSchema" /> (<see cref="SchemaKind.Array" />'s
    ///     remarks) — the shape <c>LoadBalancers.BackendsResponse</c> took for the same reason.
    /// </remarks>
    public static ResourceSchema InstancesResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/instances",
                    SchemaKind.Array,
                    true,
                    Description: "The machines the pool runs, by name, in index order."
                ) { ElementKind = SchemaKind.Text },
                new(
                    "/states",
                    SchemaKind.Array,
                    true,
                    Description: "KubeVirt's word for each machine, in the same order — Running, "
                    + "Provisioning, Starting, ErrorUnschedulable and so on; empty for one it has not "
                    + "reported on."
                ) { ElementKind = SchemaKind.Text },
                new(
                    "/replicas",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "The replica count the pool is asked for."
                ),
                new(
                    "/readyReplicas",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "How many machines KubeVirt reports ready. ⚠ 0 with instances listed is a "
                    + "set whose machines exist and have not booted."
                ),
                new(
                    "/capacity",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "The ceiling the body sets and quota reserves."
                )
            ]
        );

    // ── Upgrade policy ────────────────────────────────────────────────────────────────────────

    /// <summary>A template change reaches only instances created after it: KubeVirt's <c>unmanaged</c>.</summary>
    public const string ManualUpgrade = "Manual";

    /// <summary>Each machine's spec is updated and its guest takes it at its next restart: <c>opportunistic</c>.</summary>
    public const string OnRestartUpgrade = "OnRestart";

    /// <summary>The pool restarts machines onto the new template, a bounded number at a time: <c>proactive</c>.</summary>
    public const string RollingUpgrade = "Rolling";

    /// <summary>The three modes, in the order the schema offers them.</summary>
    public static ImmutableArray<string> UpgradeModes { get; } = [ManualUpgrade, OnRestartUpgrade, RollingUpgrade];

    /// <summary>The mode a body gets when it names none.</summary>
    public const string DefaultUpgradeMode = RollingUpgrade;

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The capacity a body gets when it names none.</summary>
    public const int DefaultCapacity = 2;

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from <see cref="VirtualMachines.Schema2026" /></b>, with <c>dataDisks</c> taken
    ///     out and the descriptions that say "the machine" of a thing that is now N machines rewritten —
    ///     the class remarks say why. <c>ComputeDeclarationTests</c> holds the two schemas to agreeing
    ///     on every pointer they share.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                .. VirtualMachines.Schema2026.Properties
                    .Where(static x => x.JsonPointer != "/properties/dataDisks")
                    .Select(Reworded),
                new(
                    "/properties/capacity",
                    SchemaKind.WholeNumber,
                    true,
                    Description: "How many machines the set holds: the count a new set starts at, the "
                    + "most the scale action may run, and what quota reserves — capacity times one "
                    + "machine's size and root disk. Raise it with a PUT, which reserves the difference "
                    + "first."
                ) { Minimum = 1, Maximum = MaxCapacity, DefaultJson = DefaultCapacity.ToString(CultureInfo.InvariantCulture) },
                new(
                    "/properties/upgradePolicy",
                    SchemaKind.Nested,
                    Description: "What happens to running machines when the template changes."
                ),
                new(
                    "/properties/upgradePolicy/mode",
                    SchemaKind.Text,
                    Description: "Manual: only machines created afterwards get the new template. "
                    + "OnRestart: every machine's spec is updated and each guest takes it at its next "
                    + "restart. Rolling: the platform restarts machines onto the new template, at most "
                    + "maxUnavailable at a time."
                ) { AllowedValues = UpgradeModes, DefaultJson = "\"" + DefaultUpgradeMode + "\"" },
                new(
                    "/properties/upgradePolicy/maxUnavailable",
                    SchemaKind.WholeNumber,
                    Description: "For a Rolling upgrade, how many machines may be restarting at once."
                ) { Minimum = 1, Maximum = MaxCapacity, DefaultJson = "1" }
            ]
        );

    /// <summary>Rewrites a machine's description for a property every instance of a set shares.</summary>
    static SchemaProperty Reworded(SchemaProperty property) =>
        property.JsonPointer switch {
            "/location" => property with { Description = "The region the set is billed in." },
            "/properties" => property with { Description = "The set's own settings." },
            ClusterIdPointer => property with {
                Description = "The cluster the set's machines run in. Must be the one its image is in."
            },
            "/properties/size" => property with {
                Description = "Every machine's size, from the platform's sizing catalogue: s1.small is 1 "
                + "vCPU and 4 GiB, and each rung doubles both. A change reaches running machines as the "
                + "upgrade policy says."
            },
            "/properties/image" => property with {
                Description = "The CyberCloud.Compute/images resource every machine's root disk is "
                + "cloned from, by name, in this resource group. ⚠ The image must have finished "
                + "importing: the set waits for it and says so."
            },
            "/properties/osDiskSize" => property with {
                Description = "Each machine's root disk, in Kubernetes quantity form. At least the "
                + "image's own size. ⚠ Immutable."
            },
            "/properties/network" => property with {
                Description = "The tenant network every machine's interface joins. Both empty means the "
                + "cluster's pod network."
            },
            "/properties/cloudInit" => property with {
                Description = "First-boot configuration every machine gets, as cloud-init reads it."
            },
            _ => property
        };

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The capacity a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Capacity(JsonElement desired) =>
        ComputeBodies.Property(desired, "capacity") is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var capacity)
            ? capacity
            : DefaultCapacity;

    /// <summary>The upgrade mode a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string UpgradeMode(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "upgradePolicy", "mode"), DefaultUpgradeMode);

    /// <summary>How many machines a rolling upgrade may restart at once.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxUnavailable(JsonElement desired) =>
        ComputeBodies.Member(desired, "upgradePolicy", "maxUnavailable") is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var count)
            ? count
            : 1;

    /// <summary>Why the resource's name cannot be a set's, or empty when it can.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string NameProblem(string name) =>
        name.Length <= MaxNameLength
            ? string.Empty
            : $"'{name}' is {name.Length.ToString(CultureInfo.InvariantCulture)} characters, and a scale "
            + $"set's name may be at most {MaxNameLength.ToString(CultureInfo.InvariantCulture)}: every "
            + "machine is named {set}-{index}, and KubeVirt writes a machine's name into label values, "
            + "which stop at 63.";

    // ── The object a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>VirtualMachinePool</c> a desired body becomes, at a given replica count.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="replicas">
    ///     What the pool already says, clamped to the capacity — or the capacity, for a set that does
    ///     not exist yet. See <see cref="ReplicasOf" />.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The machine is <see cref="VirtualMachines.VirtualMachineJson" />'s, whole.</b> Its
    ///         <c>spec</c> becomes <c>virtualMachineTemplate.spec</c> at <see cref="VirtualMachines.RunAlways" />,
    ///         so the root disk is a <c>dataVolumeTemplates</c> entry named <c>{set}-root</c> — which the
    ///         pool controller suffixes with each instance's index, giving every machine a clone of its
    ///         own — and cloud-init is the one Secret, <see cref="CloudInitSecretName" />, which every machine
    ///         mounts: <c>nameGeneration.appendIndexToSecretRefs</c> is left off because the user data
    ///         is one value.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every instance runs.</b> A set's machines have no individual power state here; the
    ///         replica count is the switch. A tenant who wants none running scales to zero.
    ///     </para>
    /// </remarks>
    public static string PoolJson(string ns, string name, JsonElement desired, int replicas) {
        var objectName = ObjectNameOf(name);
        var machine = JsonNode.Parse(VirtualMachines.VirtualMachineJson(ns, objectName, desired, VirtualMachines.RunAlways))!;
        var machineSpec = machine["spec"]!.AsObject();
        machine.AsObject().Remove("spec");

        // The set's own cloud-init Secret rather than the machine's — CloudInitSecretName says why.
        foreach (var volume in (machineSpec["template"]?["spec"]?["volumes"] as JsonArray ?? []).OfType<JsonObject>()) {
            if (volume["cloudInitNoCloud"]?["secretRef"] is JsonObject secretRef) {
                secretRef["name"] = CloudInitSecretName(name);
            }
        }

        var selector = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = objectName };

        // The launcher pods carry the set's labels too, so a pod can be traced to its set.
        var podMetadata = machineSpec["template"]!["metadata"] as JsonObject ?? [];
        podMetadata["labels"] = selector.DeepClone();
        machineSpec["template"]!["metadata"] = podMetadata;

        var spec = new JsonObject {
            ["replicas"] = replicas,
            ["selector"] = new JsonObject { ["matchLabels"] = selector.DeepClone() },
            ["updateStrategy"] = UpdateStrategy(UpgradeMode(desired)),
            ["virtualMachineTemplate"] = new JsonObject {
                ["metadata"] = new JsonObject { ["labels"] = selector.DeepClone() }, ["spec"] = machineSpec
            }
        };

        if (UpgradeMode(desired) == RollingUpgrade) {
            spec["maxUnavailable"] = MaxUnavailable(desired);
        }

        return new JsonObject {
            ["kind"] = PoolKind.Kind, ["metadata"] = new JsonObject { ["name"] = objectName }, ["spec"] = spec
        }.ToJsonString();
    }

    /// <summary>The pool's <c>updateStrategy</c> for a body's upgrade mode.</summary>
    /// <param name="mode">One of <see cref="UpgradeModes" />.</param>
    public static JsonObject UpdateStrategy(string mode) =>
        mode switch {
            ManualUpgrade => new() { ["unmanaged"] = new JsonObject() },
            OnRestartUpgrade => new() { ["opportunistic"] = new JsonObject() },
            _ => new() { ["proactive"] = new JsonObject() }
        };

    /// <summary>Where the pool's machine template sits, for <c>WithTemplateLabels</c>.</summary>
    public const string MachineTemplatePath = "spec/virtualMachineTemplate";

    /// <summary>Where each machine's pod template sits.</summary>
    public const string PodTemplatePath = "spec/virtualMachineTemplate/spec/template";

    /// <summary>Where each machine's root-disk template sits.</summary>
    public const string RootDiskTemplatePath = "spec/virtualMachineTemplate/spec/dataVolumeTemplates";

    /// <summary>The three nested templates the platform's labels are stamped into.</summary>
    public static ImmutableArray<string> TemplatePaths { get; } = [MachineTemplatePath, PodTemplatePath, RootDiskTemplatePath];

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The replica count a pool read back carries, or <see langword="null" /> for none — which the
    ///     definition defaults to one and a real API server therefore never returns.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     ⚠ <b>Where the scale lives, and the reconciler reads it before every render</b> — the same
    ///     rule <see cref="VirtualMachines.RunStrategyOf" /> states for a machine's power state.
    /// </remarks>
    public static int? ReplicasOf(string objectJson) {
        var value = ComputeBodies.WholeOf(ComputeBodies.Spec(objectJson)?["replicas"]);
        return value < 0 ? null : value;
    }

    /// <summary>The replica count a pass renders: what the pool says, clamped to the body's capacity.</summary>
    /// <param name="live">What <see cref="ReplicasOf" /> read, or <see langword="null" /> for a pool not there yet.</param>
    /// <param name="desired">The validated desired body.</param>
    public static int ReplicasToRender(int? live, JsonElement desired) =>
        live is { } count ? Math.Clamp(count, 0, Capacity(desired)) : Capacity(desired);

    /// <summary>
    ///     Whether a pool read back carries what the desired body asks for, at the replica count given.
    /// </summary>
    /// <param name="objectJson">The pool's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The desired body.</param>
    /// <param name="replicas">The replica count the pass rendered.</param>
    /// <remarks>
    ///     ⚠ <b>The machine half is <see cref="VirtualMachines.Matches" /></b>, over a
    ///     <c>VirtualMachine</c> assembled from the pool's own <c>virtualMachineTemplate</c>, so the two
    ///     types compare the fields a tenant chose in one place — containment, because KubeVirt's
    ///     mutating webhook writes defaults into the template. What this adds: the replica count, the
    ///     selector, and the update strategy the body's mode renders.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, JsonElement desired, int replicas) {
        if (ComputeBodies.Kind(objectJson) != PoolKind.Kind || ComputeBodies.Spec(objectJson) is not { } spec) {
            return false;
        }

        if (ComputeBodies.WholeOf(spec["replicas"]) != replicas) {
            return false;
        }

        var strategy = spec["updateStrategy"] as JsonObject;
        var wanted = UpdateStrategy(UpgradeMode(desired)).Select(static x => x.Key).Single();

        if (strategy is null || !strategy.ContainsKey(wanted)) {
            return false;
        }

        if (UpgradeMode(desired) == RollingUpgrade && ComputeBodies.WholeOf(spec["maxUnavailable"]) != MaxUnavailable(desired)) {
            return false;
        }

        if (spec["virtualMachineTemplate"]?["spec"] is not JsonObject machineSpec) {
            return false;
        }

        var machine = new JsonObject { ["kind"] = VirtualMachines.VirtualMachineKind.Kind, ["spec"] = machineSpec.DeepClone() };

        return VirtualMachines.Matches(machine.ToJsonString(), ns, desired);
    }

    /// <summary>What KubeVirt says about a pool, read off its <c>status</c>.</summary>
    /// <param name="objectJson">The pool's JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     <see cref="VirtualMachines.ReadinessKind.Ready" /> when <c>status.readyReplicas</c> reaches
    ///     <c>spec.replicas</c> and <c>status.replicas</c> equals it — zero of zero included; <see cref="VirtualMachines.ReadinessKind.NotReported" />
    ///     when there is no status at all, which is a cluster with no pool controller behind the
    ///     definition (<c>converged-is-not-ready</c>); otherwise
    ///     <see cref="VirtualMachines.ReadinessKind.NotReady" /> with the counts and, when the pool
    ///     carries one, the message of its first false condition — <c>ReplicaFailure</c>, for a machine
    ///     the controller could not create.
    /// </remarks>
    public static VirtualMachines.Readiness ReadinessOf(string objectJson) {
        var status = ComputeBodies.Status(objectJson);

        if (status is null || status.Count == 0) {
            return new(VirtualMachines.ReadinessKind.NotReported, "KubeVirt has not reported on the pool yet");
        }

        var wanted = Math.Max(0, ComputeBodies.WholeOf(ComputeBodies.Spec(objectJson)?["replicas"]));
        var ready = Math.Max(0, ComputeBodies.WholeOf(status["readyReplicas"]));
        var current = Math.Max(0, ComputeBodies.WholeOf(status["replicas"]));

        // ⚠ AND THE CURRENT COUNT, NOT ONLY THE READY ONE. Measured on the pinned pool (2026-09-23):
        // scaled from two to one, the pool reads `readyReplicas: 1, replicas: 2` for the minute the
        // second machine takes to shut down — ready already equals the ask while a machine is still
        // running and still billing, and a scale-in is not done until it is gone.
        if (ready >= wanted && current == wanted) {
            return new(
                VirtualMachines.ReadinessKind.Ready,
                string.Create(CultureInfo.InvariantCulture, $"{ready} of {wanted} machines are ready")
            );
        }

        var failing = (status["conditions"] as JsonArray)?.OfType<JsonObject>()
            .Where(static x => ComputeBodies.TextOf(x["message"]).Length > 0)
            .Select(static x => ComputeBodies.TextOf(x["type"]) + ": " + ComputeBodies.TextOf(x["message"]))
            .FirstOrDefault();

        return new(
            VirtualMachines.ReadinessKind.NotReady,
            string.Create(CultureInfo.InvariantCulture, $"{ready} of {wanted} machines are ready, {current} exist")
            + (failing is null ? string.Empty : "; " + failing)
        );
    }

    /// <summary>The pool's <c>status.readyReplicas</c>, or zero.</summary>
    /// <param name="objectJson">The pool's JSON.</param>
    public static int ReadyReplicasOf(string objectJson) =>
        Math.Max(0, ComputeBodies.WholeOf(ComputeBodies.Status(objectJson)?["readyReplicas"]));

    /// <summary>The index an instance name carries, or <see langword="null" /> for a name that is not one of this set's.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="instance">A machine's object name.</param>
    public static int? IndexOf(string name, string instance) {
        var prefix = ObjectNameOf(name) + "-";

        return instance.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(instance.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? index
                : null;
    }

    /// <summary>
    ///     The machine the pool controller creates at <paramref name="index" />, as it writes it: the
    ///     template's spec with the root disk indexed, the template's labels, and an owner reference to
    ///     the pool by kind and name.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="index">The instance's index.</param>
    /// <param name="printableStatus">KubeVirt's word for it, written into <c>status</c>.</param>
    /// <remarks>
    ///     ⚠ <b>Nothing on the reconcile path calls this</b> — the pool controller creates the machines —
    ///     for the reason <see cref="VirtualMachines.RootDataVolumeJson" /> gives: it is what the
    ///     conformance case declares in <c>OperatorWritten</c>, so the k3s lane serves the
    ///     <c>VirtualMachine</c> kind <see cref="ListInstancesAction" /> lists, and the listing has a
    ///     machine to find. The indexing is the one measured on the pinned pool (2026-09-23): machine
    ///     <c>web-0</c>, root disk <c>web-root-0</c>.
    /// </remarks>
    public static string InstanceJson(string ns, string name, JsonElement desired, int index, string printableStatus) {
        var pool = JsonNode.Parse(PoolJson(ns, name, desired, 1))!;
        var template = pool["spec"]!["virtualMachineTemplate"]!;
        var spec = template["spec"]!.DeepClone().AsObject();
        var root = VirtualMachines.RootDataVolumeName(ObjectNameOf(name));
        var indexed = IndexedRootDataVolumeName(name, index);

        foreach (var entry in (spec["dataVolumeTemplates"] as JsonArray ?? []).OfType<JsonObject>()) {
            if (ComputeBodies.TextOf(entry["metadata"]?["name"]) == root) {
                entry["metadata"]!["name"] = indexed;
            }
        }

        foreach (var volume in (spec["template"]?["spec"]?["volumes"] as JsonArray ?? []).OfType<JsonObject>()) {
            if (volume["dataVolume"] is JsonObject dataVolume && ComputeBodies.TextOf(dataVolume["name"]) == root) {
                dataVolume["name"] = indexed;
            }
        }

        return new JsonObject {
            ["apiVersion"] = VirtualMachines.VirtualMachineKind.ApiVersion,
            ["kind"] = VirtualMachines.VirtualMachineKind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = InstanceName(name, index),
                ["namespace"] = ns,
                ["labels"] = template["metadata"]!["labels"]!.DeepClone(),
                ["ownerReferences"] = new JsonArray(
                    KubeJson.OwnerReference(
                        new() { ApiVersion = PoolKind.ApiVersion, Kind = PoolKind.Kind, Name = ObjectNameOf(name), Uid = string.Empty }
                    )
                )
            },
            ["spec"] = spec,
            ["status"] = new JsonObject { ["printableStatus"] = printableStatus }
        }.ToJsonString();
    }

    /// <summary>The root disk the pool controller gives the instance at <paramref name="index" />: <c>{set}-root-{index}</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="index">The instance's index.</param>
    public static string IndexedRootDataVolumeName(string name, int index) =>
        VirtualMachines.RootDataVolumeName(ObjectNameOf(name)) + "-" + index.ToString(CultureInfo.InvariantCulture);

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the machines run in.</param>
    /// <param name="capacity">The ceiling and the starting count.</param>
    /// <param name="image">The image every root disk clones.</param>
    /// <param name="size">The sizing preset.</param>
    /// <param name="osDiskSize">Each root disk's size.</param>
    /// <param name="upgradeMode">The upgrade mode.</param>
    /// <param name="maxUnavailable">The rolling upgrade's bound.</param>
    /// <param name="virtualNetwork">The virtual network, or empty.</param>
    /// <param name="subnet">The subnet, or empty.</param>
    /// <param name="cloudInit">The cloud-init vault handle, or empty.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        int capacity = DefaultCapacity,
        string image = VirtualMachines.DefaultImage,
        string size = VirtualMachines.DefaultSize,
        string osDiskSize = "20Gi",
        string upgradeMode = DefaultUpgradeMode,
        int maxUnavailable = 1,
        string virtualNetwork = "",
        string subnet = "",
        string cloudInit = "",
        string location = "eu-central"
    ) {
        var body = JsonNode.Parse(
            VirtualMachines.Body(clusterId, image, size, osDiskSize, [], virtualNetwork, subnet, cloudInit, location)
        )!.AsObject();

        var properties = body["properties"]!.AsObject();
        properties.Remove("dataDisks");
        properties["capacity"] = capacity;
        properties["upgradePolicy"] = new JsonObject { ["mode"] = upgradeMode, ["maxUnavailable"] = maxUnavailable };

        return body.ToJsonString();
    }
}
