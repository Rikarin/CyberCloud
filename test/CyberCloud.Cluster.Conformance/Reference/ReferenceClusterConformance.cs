using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance.Reference;

namespace CyberCloud.Cluster.Conformance.Reference;

/// <summary>
///     The cluster-backed suite, run against the reference provider.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting a provider under the cluster-backed suite, and it
///     is the same shape as the Docker-free one.</b> The case is <c>ReferenceCase</c> — the very same
///     object <c>CyberCloud.Conformance</c> already runs against, reused rather than restated, so the
///     two halves cannot drift onto different descriptions of one provider.
///     <para>
///         It exists for the reason <c>ReferenceProvider</c> exists: without it this project would
///         compile, ship, and run zero tests until somebody wrote a provider, and a suite that is
///         green because it ran nothing is exactly what <c>--minimum-expected-tests</c> is for.
///     </para>
/// </remarks>
public sealed class ReferenceProviderClusterConformance(ClusterConformanceFixture<ReferenceCase> fixture)
    : ClusterConformanceTests<ReferenceCase>(fixture),
        IClassFixture<ClusterConformanceFixture<ReferenceCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the reference provider.</summary>
public sealed class ReferenceProviderSiloKillConformance : SiloKillConformanceTests<ReferenceCase>;

/// <summary>
///     The <b>same</b> four criteria, against the reference provider's <b>child</b> type.
/// </summary>
/// <remarks>
///     ⚠ <b>Two more class declarations and nothing else, which is the claim this file exists to
///     make true for a nested type as well.</b> The child's parent is created by the harness against
///     the same real API server before the first assertion runs, so what these four exercise for a
///     child is what they exercise for its parent: the rendered manifest is one the API server
///     accepts, server-side apply works under our field manager, the seven labels survive admission,
///     and the plural addresses a real REST path — for an object whose NAME carries the parent's,
///     which is where a child differs.
///     <para>
///         ⚠ The silo-kill criterion is deliberately NOT declared for the child. It kills every silo
///         mid-create and re-drives from PostgreSQL; the parent the child hangs off was created by the
///         first harness and its binding is durable, so a second run against the same service id would
///         be asserting the parent's durability under the child's name. docs/plan/24 § Phase 1's exit
///         criterion 3 is about the operation being re-drivable, and that is proven once.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ReferenceChildClusterConformance(ClusterConformanceFixture<ReferenceChildCase> fixture)
    : ClusterConformanceTests<ReferenceChildCase>(fixture),
        IClassFixture<ClusterConformanceFixture<ReferenceChildCase>>;
