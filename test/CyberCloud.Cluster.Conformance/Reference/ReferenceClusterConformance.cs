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
