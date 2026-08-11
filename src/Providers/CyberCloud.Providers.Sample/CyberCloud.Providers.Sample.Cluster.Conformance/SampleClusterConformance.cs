using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Sample.Conformance;

namespace CyberCloud.Providers.Sample.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the sample provider.
/// </summary>
/// <remarks>
///     ⚠ <b>Two class declarations, and that is the entire cost.</b> The case is
///     <see cref="SampleCase" /> — the one <c>CyberCloud.Providers.Sample.Conformance</c> already
///     declares — so a provider under both halves of the suite is described exactly once.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class SampleWidgetClusterLifecycleConformance(ClusterConformanceFixture<SampleCase> fixture)
    : ClusterConformanceTests<SampleCase>(fixture), IClassFixture<ClusterConformanceFixture<SampleCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the sample provider.</summary>
public sealed class SampleWidgetSiloKillConformance : SiloKillConformanceTests<SampleCase>;
