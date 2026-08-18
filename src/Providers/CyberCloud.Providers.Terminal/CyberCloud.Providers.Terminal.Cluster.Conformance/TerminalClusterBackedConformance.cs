using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Terminal.Conformance;

namespace CyberCloud.Providers.Terminal.ClusterConformance;

/// <summary>The cluster-backed suite, run against the cloud-terminal provider.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER IN THE TREE WHOSE OBJECTS A BARE k3s ALREADY SERVES.</b> Every
///         family before this one renders a custom resource, and the harness had to derive a CRD stub
///         from the case's <c>Objects</c> before a single assertion could address anything. A
///         PersistentVolumeClaim, a ServiceAccount and a NetworkPolicy are core API and are real here
///         — which makes this the one suite in which a green run is genuine evidence that the objects
///         exist, and the one in which it is <b>no</b> evidence that the derivation works.
///     </para>
///     <para>
///         ⚠ <b>AND IT IS ALSO THE ONE WHOSE GREEN PROVES LEAST ABOUT WHAT THE PROVIDER IS FOR.</b> A
///         NetworkPolicy applies, reads back and matches identically in a cluster that enforces it and
///         in one that does not — enforcement is the CNI's, and k3s' default has a policy controller
///         while a cluster with none accepts the object and ignores it. So every assertion here can
///         pass over a console whose shell reaches the whole cluster.
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>a-networkpolicy-that-nothing-enforces-still-reads-back</c>.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class CloudConsoleLifecycleConformance(ClusterConformanceFixture<CloudConsoleCase> fixture)
    : ClusterConformanceTests<CloudConsoleCase>(fixture), IClassFixture<ClusterConformanceFixture<CloudConsoleCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the cloud-terminal provider.</summary>
public sealed class CloudConsoleSiloKillConformance : SiloKillConformanceTests<CloudConsoleCase>;
