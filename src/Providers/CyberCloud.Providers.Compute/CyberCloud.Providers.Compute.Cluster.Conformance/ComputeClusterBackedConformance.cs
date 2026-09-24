using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Compute.Conformance;

namespace CyberCloud.Providers.Compute.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the virtual-machine type.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN PROVES, AND WHAT IT DOES NOT.</b> The k3s this starts has
///         <b>
///             no
///             KubeVirt and no CDI
///         </b>; <c>ClusterConformanceHarness.EnsureCustomResourceDefinitionsAsync</c>
///         derives an open-schema stub per custom kind from the case's <c>Objects</c>. So this suite
///         proves the apply path against a real API server, ADR-013's labels under real admission, a
///         real <c>kubectl delete</c> corrected on the next pass, a second field manager's <c>409</c>
///         becoming a named drift event, and the refusal a missing CRD produces. It does <b>not</b>
///         prove that <c>VirtualMachines.VirtualMachineJson</c> satisfies KubeVirt's webhooks, because
///         no webhook is behind the stub.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The webhook half is proven elsewhere in this tree, for this family, for the first
///             time — for one render of each chart, on every merge.
///         </b>
///         <c>CyberCloud.Providers.Compute.KubeVirt.Cluster.Conformance § KubeVirtOnAnEmptyCluster</c>,
///         the family's second k3s lane in a process of its own, installs CDI and
///         KubeVirt through <c>charts/bundle/install.sh</c>, applies a <c>url</c> image, a blank disk
///         and a machine that attaches the disk against them, reads the guest running under KVM and
///         the disk populated and mounted, and holds <c>VirtualMachines.Matches</c> to the admitted
///         object. Every earlier family's chart-schema half is still owed to a real operator
///         (<c>charts/managed/kubernetes/conformance.yaml § owed</c>,
///         <c>a-green-cluster-suite-proves-the-apply-path-only</c>); this one's is measured for what
///         that class applies — a catalogue import, an <c>http</c> source, cloud-init on a real
///         guest and the power actions on a real KubeVirt are not among them, and
///         <c>src/Providers/README.md § What the eighteenth provider measured</c> lists them.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class VirtualMachineLifecycleConformance(ClusterConformanceFixture<VirtualMachineCase> fixture)
    : ClusterConformanceTests<VirtualMachineCase>(fixture),
    IClassFixture<ClusterConformanceFixture<VirtualMachineCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the virtual-machine type.</summary>
public sealed class VirtualMachineSiloKillConformance : SiloKillConformanceTests<VirtualMachineCase>;

/// <summary>The same suite against the disk type.</summary>
/// <param name="fixture">The harness.</param>
public sealed class DiskLifecycleConformance(ClusterConformanceFixture<DiskCase> fixture)
    : ClusterConformanceTests<DiskCase>(fixture), IClassFixture<ClusterConformanceFixture<DiskCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the disk type.</summary>
public sealed class DiskSiloKillConformance : SiloKillConformanceTests<DiskCase>;

/// <summary>The same suite against the image type.</summary>
/// <param name="fixture">The harness.</param>
public sealed class ImageLifecycleConformance(ClusterConformanceFixture<ImageCase> fixture)
    : ClusterConformanceTests<ImageCase>(fixture), IClassFixture<ClusterConformanceFixture<ImageCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the image type.</summary>
public sealed class ImageSiloKillConformance : SiloKillConformanceTests<ImageCase>;

/// <summary>The same suite against the scale set type.</summary>
/// <param name="fixture">The harness.</param>
/// <remarks>
///     ⚠ Against the committed <c>VirtualMachinePool</c> definition and no pool controller: the pool is
///     admitted by its structural schema and never makes a machine, so what this proves is the apply,
///     the labels and the read-back. The webhook and the controller are
///     <c>KubeVirtOnAnEmptyCluster</c>'s, which applies charts/managed/virtual-machine-scale-set
///     against a real KubeVirt.
/// </remarks>
public sealed class VirtualMachineScaleSetLifecycleConformance(ClusterConformanceFixture<VirtualMachineScaleSetCase> fixture)
    : ClusterConformanceTests<VirtualMachineScaleSetCase>(fixture),
    IClassFixture<ClusterConformanceFixture<VirtualMachineScaleSetCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the scale set type.</summary>
public sealed class VirtualMachineScaleSetSiloKillConformance : SiloKillConformanceTests<VirtualMachineScaleSetCase>;
