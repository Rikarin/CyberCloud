using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Cache.Conformance;

namespace CyberCloud.Providers.Cache.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-Valkey provider.
/// </summary>
/// <remarks>
///     ⚠ <b>Two class declarations, and that is the entire cost.</b> The case is
///     <see cref="ValkeyCase" /> — the one <c>CyberCloud.Providers.Cache.Conformance</c> already
///     declares — so a provider under both halves of the suite is described exactly once.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class ValkeyCacheClusterLifecycleConformance(ClusterConformanceFixture<ValkeyCase> fixture)
    : ClusterConformanceTests<ValkeyCase>(fixture), IClassFixture<ClusterConformanceFixture<ValkeyCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-Valkey provider.</summary>
public sealed class ValkeyCacheSiloKillConformance : SiloKillConformanceTests<ValkeyCase>;
