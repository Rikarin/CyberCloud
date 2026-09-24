using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Compute.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Conformance;

/// <summary>
///     <c>CyberCloud.Compute/virtualMachines</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             ONE CASE OBJECT AND TWO CLASS DECLARATIONS, and the first time that shape has held for
///             a type whose actions WRITE the cluster.
///         </b> <c>AnActionOnAnExistingResourceIsAccepted</c>
///         invokes <see cref="VirtualMachines.StopAction" /> after the create has converged: the
///         handler reads the <c>VirtualMachine</c> the reconciler applied, applies it again with
///         <c>Halted</c>, and answers a body the suite validates against
///         <see cref="VirtualMachines.PowerResponse" />. What the suite cannot see is that the NEXT
///         reconcile pass preserves the <c>Halted</c> — the harness converges once and stops —
///         so <c>VirtualMachinePowerTests</c> drives that sequence by hand.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The image is not there, and every assertion here passes anyway — by the reconciler's
///             own rule rather than by the harness's leniency.
///         </b> <see cref="FakeKubeCluster" /> holds no
///         <c>DataVolume</c> for the body's image, and <c>VirtualMachineReconciler</c> proceeds on an
///         absent image because CDI's admission is the honest refuser of a clone with no source.
///         An image that exists and is importing is the case it waits for, and that case has no
///         harness object either; <c>VirtualMachineReconcilerTests</c> builds it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> is
///             <c>VirtualMachines.Matches</c>, which ignores the run strategy on purpose
///         </b> — a stopped
///         machine still carries its desired spec — and takes the namespace, because the logical
///         switch a body names is spelled with it.
///     </para>
/// </remarks>
public sealed class VirtualMachineCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Compute/virtualMachines",
            CreateProvider = static () => new ComputeProvider(),
            ReconcilerType = typeof(VirtualMachineReconciler),
            CreateReconciler = static clock => new VirtualMachineReconciler(clock),
            Type = VirtualMachines.Type,
            ApiVersion = VirtualMachines.V2026,
            Body = static cluster => VirtualMachines.Body(cluster),
            // ⚠ Changes `size`, which the rendered VirtualMachine carries in TWO places —
            // domain.cpu.cores and domain.memory.guest — and which two of the three meters read. A
            // body that differed only where the reconciler ignores it would pass the update test while
            // proving the update never left the grain.
            ChangedBody = static cluster => VirtualMachines.Body(cluster, size: "s1.medium"),
            // Drops the required `/properties/image`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = static cluster => Without(VirtualMachines.Body(cluster), "image"),
            InvalidBodyTarget = "/properties/image",
            ActionName = VirtualMachines.StopAction,
            // ⚠ ONE OBJECT, NOT TWO: the body names no cloud-init, so no Secret is rendered, and a
            // second reference here would be an object the suite removes, reads back, and finds
            // missing for the right reason and the wrong test. The cloud-init half is
            // VirtualMachineReconcilerTests' to assert.
            Objects = static (id, ns) => [VirtualMachines.VirtualMachineRef(ns, id.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            // ⚠ THE ROOT DISK, WHICH virt-controller CREATES FROM dataVolumeTemplates AND OWNS. The
            // power actions read the VirtualMachine the reconciler applied and nothing an operator
            // writes; what this declaration is for is the KIND. The k3s lane serves a definition for
            // every kind the case names here or in Objects, and since #91 a read of a kind the
            // cluster does not serve is a named failure rather than a NotFound — so a lane that
            // served VirtualMachine alone failed the image read on the kind, not the object, and
            // every lifecycle assertion with it. VirtualMachines.RootDataVolumeJson has the account.
            OperatorWritten = static (id, ns) => [
                (
                    Disks.DataVolumeRef(ns, VirtualMachines.RootDataVolumeName(id.Name)),
                    VirtualMachines.RootDataVolumeJson(
                        ns,
                        id.Name,
                        Desired(VirtualMachines.Body(Guid.Empty)),
                        Cdi.Succeeded
                    )
                )
            ],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return VirtualMachines.Matches(match.ObjectJson, match.Namespace, desired.RootElement);
            }
        };

    /// <summary>A valid body with one property removed.</summary>
    /// <param name="body">A valid body.</param>
    /// <param name="property">The property under <c>/properties</c> to drop.</param>
    public static string Without(string body, string property) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove(property);
        return node.ToJsonString();
    }

    /// <summary>A body as the reconciler sees it: the validated element, cloned out of its document.</summary>
    /// <param name="body">A valid body.</param>
    public static JsonElement Desired(string body) {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>A valid body with a tag bag on it.</summary>
    /// <param name="body">A valid body.</param>
    /// <param name="key">The tag.</param>
    /// <param name="value">Its value.</param>
    public static string WithTag(string body, string key, string value) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["tags"] = new JsonObject { [key] = value };
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Compute/disks</c>, registered into the same shared suite.
/// </summary>
/// <remarks>
///     ⚠ <b>THE CHANGED BODY DIFFERS BY A TAG, AND THAT IS THE ONLY LEGAL DIFFERENCE THIS TYPE HAS.</b>
///     Every tenant-facing property of a disk is immutable — a CDI <c>DataVolume</c>'s spec cannot be
///     changed after creation and shrinking a claim is never possible — so a changed body that moved
///     any of them would be refused by the manager and the update test would assert a refusal. A tag
///     is accepted, converges, and reaches no object; the update test on this type therefore proves
///     the grain path and nothing about the cluster, by construction rather than by leniency, and
///     <c>charts/managed/disk/conformance.yaml § owed</c>, <c>nothing-mutable-reaches-the-cluster</c>,
///     says so where the next reader looks.
/// </remarks>
public sealed class DiskCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Compute/disks",
            CreateProvider = static () => new ComputeProvider(),
            ReconcilerType = typeof(DiskReconciler),
            CreateReconciler = static clock => new DiskReconciler(clock),
            Type = Disks.Type,
            ApiVersion = Disks.V2026,
            Body = static cluster => Disks.Body(cluster),
            ChangedBody = static cluster => VirtualMachineCase.WithTag(Disks.Body(cluster), "tier", "data"),
            // Drops the required `/properties/size`.
            InvalidBody = static cluster => VirtualMachineCase.Without(Disks.Body(cluster), "size"),
            InvalidBodyTarget = "/properties/size",
            // ⚠ No action, and saying so: a disk is attached by a machine's body, grown by nothing yet,
            // and snapshotted by nothing yet — charts/managed/disk/conformance.yaml § owed carries the
            // last two. The suite skips its two POST assertions loudly for an empty name.
            ActionName = string.Empty,
            Objects = static (id, ns) => [Disks.DataVolumeRef(ns, id.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return Disks.Matches(match.ObjectJson, desired.RootElement);
            }
        };
}

/// <summary>
///     <c>CyberCloud.Compute/images</c>, registered into the same shared suite.
/// </summary>
/// <remarks>
///     The same immutable shape as <see cref="DiskCase" />, for the same reason: an imported image
///     cannot be re-imported in place. The changed body differs by a tag, and
///     <c>charts/managed/image/conformance.yaml § owed</c> carries the same row.
/// </remarks>
public sealed class ImageCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Compute/images",
            CreateProvider = static () => new ComputeProvider(),
            ReconcilerType = typeof(ImageReconciler),
            CreateReconciler = static clock => new ImageReconciler(clock),
            Type = Images.Type,
            ApiVersion = Images.V2026,
            Body = static cluster => Images.Body(cluster),
            ChangedBody = static cluster => VirtualMachineCase.WithTag(Images.Body(cluster), "os", "ubuntu"),
            // Drops the required `/properties/size`.
            InvalidBody = static cluster => VirtualMachineCase.Without(Images.Body(cluster), "size"),
            InvalidBodyTarget = "/properties/size",
            ActionName = string.Empty,
            Objects = static (id, ns) => [Images.DataVolumeRef(ns, id.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return Images.Matches(match.ObjectJson, desired.RootElement);
            }
        };
}

/// <summary>
///     <c>CyberCloud.Compute/virtualMachineScaleSets</c>, registered into the same shared suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The action is <c>listInstances</c>, not <c>scale</c>, and the suite's shape decides
///         it.</b> The shared POST carries no body, and <c>scale</c> declares a required
///         <c>replicas</c>, so a body-less scale is a request the manager refuses before any handler —
///         which is correct and asserts nothing about the handler. <c>listInstances</c> takes no body,
///         reads the pool the reconciler applied, lists the machines by the pool's own selector and
///         answers a body the suite validates against
///         <see cref="VirtualMachineScaleSets.InstancesResponse" />. <c>scale</c> and the sequence the
///         design exists for — scale, reconcile, still scaled — are <c>VirtualMachineScaleSetTests</c>'.
///     </para>
///     <para>
///         ⚠ <b>The operator-written objects are one machine and its root disk</b>, as the pool
///         controller writes them (<see cref="VirtualMachineScaleSets.InstanceJson" />). They make the
///         k3s lane serve <c>VirtualMachine</c>, which the listing lists, and <c>DataVolume</c>, which
///         the image gate reads — the same reason <see cref="VirtualMachineCase" /> declares its root
///         disk — and they give the listing a machine to find.
///     </para>
/// </remarks>
public sealed class VirtualMachineScaleSetCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Compute/virtualMachineScaleSets",
            CreateProvider = static () => new ComputeProvider(),
            ReconcilerType = typeof(VirtualMachineScaleSetReconciler),
            CreateReconciler = static clock => new VirtualMachineScaleSetReconciler(clock),
            Type = VirtualMachineScaleSets.Type,
            ApiVersion = VirtualMachineScaleSets.V2026,
            Body = static cluster => VirtualMachineScaleSets.Body(cluster),
            // ⚠ Changes `size`, which the pool carries in its machine template's cores and guest memory
            // and which three meters read times the capacity.
            ChangedBody = static cluster => VirtualMachineScaleSets.Body(cluster, size: "s1.medium"),
            // Drops the required `/properties/image`.
            InvalidBody = static cluster => VirtualMachineCase.Without(VirtualMachineScaleSets.Body(cluster), "image"),
            InvalidBodyTarget = "/properties/image",
            ActionName = VirtualMachineScaleSets.ListInstancesAction,
            Objects = static (id, ns) => [VirtualMachineScaleSets.PoolRef(ns, id.Name)],
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = static (id, ns) => [
                (
                    VirtualMachines.VirtualMachineRef(ns, VirtualMachineScaleSets.InstanceName(id.Name, 0)),
                    VirtualMachineScaleSets.InstanceJson(
                        ns,
                        id.Name,
                        VirtualMachineCase.Desired(VirtualMachineScaleSets.Body(Guid.Empty)),
                        0,
                        "Running"
                    )
                ),
                (
                    Disks.DataVolumeRef(ns, VirtualMachineScaleSets.IndexedRootDataVolumeName(id.Name, 0)),
                    RootDisk(ns, id.Name, 0)
                )
            ],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return VirtualMachineScaleSets.Matches(
                    match.ObjectJson,
                    match.Namespace,
                    desired.RootElement,
                    VirtualMachineScaleSets.ReplicasOf(match.ObjectJson) ?? -1
                );
            }
        };

    /// <summary>The root disk the pool controller's machine at <paramref name="index" /> clones into, as CDI leaves it.</summary>
    /// <remarks>
    ///     ⚠ <b>Owned by the pool here, and by the machine on a real cluster.</b> The harness resolves an
    ///     operator-written object's owner only among objects the reconciler applied
    ///     (<c>ProviderConformanceTests.PlantOperatorObjects</c>), and the reconciler applies the pool and
    ///     not the machine. The collector's answer is the same either way — the chain is pool, machine,
    ///     disk, so the disk goes when the pool does.
    /// </remarks>
    static string RootDisk(string ns, string name, int index) {
        var disk = JsonNode.Parse(
            VirtualMachines.RootDataVolumeJson(
                ns,
                VirtualMachineScaleSets.InstanceName(name, index),
                VirtualMachineCase.Desired(VirtualMachineScaleSets.Body(Guid.Empty)),
                Cdi.Succeeded
            )
        )!.AsObject();

        disk["metadata"]!["name"] = VirtualMachineScaleSets.IndexedRootDataVolumeName(name, index);
        disk["metadata"]!["ownerReferences"] = new JsonArray(
            KubeJson.OwnerReference(
                new() {
                    ApiVersion = VirtualMachineScaleSets.PoolKind.ApiVersion,
                    Kind = VirtualMachineScaleSets.PoolKind.Kind,
                    Name = VirtualMachineScaleSets.ObjectNameOf(name),
                    Uid = string.Empty
                }
            )
        );

        return disk.ToJsonString();
    }
}

/// <summary>The shared suite, run against the virtual-machine type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class VirtualMachineConformance(ProviderTestCluster<VirtualMachineCase> cluster)
    : ProviderConformanceTests<VirtualMachineCase>(cluster), IClassFixture<ProviderTestCluster<VirtualMachineCase>>;

/// <summary>The <b>same</b> suite, run against the disk type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class DiskConformance(ProviderTestCluster<DiskCase> cluster)
    : ProviderConformanceTests<DiskCase>(cluster), IClassFixture<ProviderTestCluster<DiskCase>>;

/// <summary>The <b>same</b> suite, run against the image type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ImageConformance(ProviderTestCluster<ImageCase> cluster)
    : ProviderConformanceTests<ImageCase>(cluster), IClassFixture<ProviderTestCluster<ImageCase>>;

/// <summary>The <b>same</b> suite, run against the scale set type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class VirtualMachineScaleSetConformance(ProviderTestCluster<VirtualMachineScaleSetCase> cluster)
    : ProviderConformanceTests<VirtualMachineScaleSetCase>(cluster),
    IClassFixture<ProviderTestCluster<VirtualMachineScaleSetCase>>;

/// <summary>The container-backed half, skipped loudly, against the virtual-machine type.</summary>
public sealed class VirtualMachineClusterBackedConformance()
    : ClusterBackedConformanceTests(VirtualMachineCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the disk type.</summary>
public sealed class DiskClusterBackedConformance() : ClusterBackedConformanceTests(DiskCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the image type.</summary>
public sealed class ImageClusterBackedConformance() : ClusterBackedConformanceTests(ImageCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the scale set type.</summary>
public sealed class VirtualMachineScaleSetClusterBackedConformance()
    : ClusterBackedConformanceTests(VirtualMachineScaleSetCase.ProviderCase);

/// <summary>
///     What this provider's three registrations into the shared suite are <b>shaped</b> like.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about the SUITE'S shape, not about the provider.</b> It lives in
///     this project rather than in <c>CyberCloud.Providers.Compute.Tests</c> because that project
///     deliberately does not reference this one, and these three test classes are the subjects.
/// </remarks>
public sealed class ComputeSuiteShapeTests {
    [Fact]
    public void TheFourTypesRunTheSameAssertions() {
        // ⚠ "The four types run the same suite" is a claim about a COUNT, and a claim about a count
        // that nothing counts is how a suite goes green by asking less.
        var machine = RunnableFactsOf(typeof(VirtualMachineConformance));
        var disk = RunnableFactsOf(typeof(DiskConformance));
        var image = RunnableFactsOf(typeof(ImageConformance));
        var set = RunnableFactsOf(typeof(VirtualMachineScaleSetConformance));

        disk.ShouldBe(machine);
        image.ShouldBe(machine);
        set.ShouldBe(machine);
        machine.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void NoCaseDescribesAnAncestorBecauseNoneIsAChild() {
        // ⚠ A disk looks like a machine's child and is not one — ComputeProvider says why — so the
        // four cases are four roots, and the parent-existence assertion self-skips on each.
        AncestorsOf<VirtualMachineCase>().ShouldBeEmpty();
        AncestorsOf<DiskCase>().ShouldBeEmpty();
        AncestorsOf<ImageCase>().ShouldBeEmpty();
        AncestorsOf<VirtualMachineScaleSetCase>().ShouldBeEmpty();

        VirtualMachines.Type.Depth.ShouldBe(1);
        Disks.Type.Depth.ShouldBe(1);
        Images.Type.Depth.ShouldBe(1);
        VirtualMachineScaleSets.Type.Depth.ShouldBe(1);
    }

    [Fact]
    public void TheMachineAndTheSetDeclareActionsAndTheDiskAndImageDoNot() {
        VirtualMachineCase.ProviderCase.ActionName.ShouldBe(VirtualMachines.StopAction);
        VirtualMachineScaleSetCase.ProviderCase.ActionName.ShouldBe(VirtualMachineScaleSets.ListInstancesAction);
        DiskCase.ProviderCase.ActionName.ShouldBeEmpty();
        ImageCase.ProviderCase.ActionName.ShouldBeEmpty();
    }

    [Fact]
    public void EveryCaseOwnsExactlyOneObjectAndTheMachinesIsTheVirtualMachine() {
        var id = new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", VirtualMachines.Type, "web", Guid.NewGuid());

        VirtualMachineCase.ProviderCase.Objects(id, "ns").Length.ShouldBe(1);
        VirtualMachineCase.ProviderCase.Objects(id, "ns")[0].Kind.Kind.ShouldBe("VirtualMachine");
        DiskCase.ProviderCase.Objects(id, "ns").Length.ShouldBe(1);
        ImageCase.ProviderCase.Objects(id, "ns").Length.ShouldBe(1);
        VirtualMachineScaleSetCase.ProviderCase.Objects(id, "ns").Single().Kind.Kind.ShouldBe("VirtualMachinePool");
    }

    static ImmutableArray<ProviderConformanceCase> AncestorsOf<TSource>()
        where TSource : IProviderCaseSource =>
        TSource.Ancestors;

    /// <summary>Every <c>[Fact]</c> a test class runs, by name, ordered.</summary>
    /// <param name="suite">The closed test class.</param>
    static ImmutableArray<string> RunnableFactsOf(Type suite) => [
        .. suite
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(static x => x.GetCustomAttributes(typeof(FactAttribute), true).Length > 0)
            .Select(static x => x.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
    ];
}
