using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.DBforMySQL.Conformance;

namespace CyberCloud.Providers.DBforMySQL.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-MariaDB provider.
/// </summary>
/// <remarks>
///     ⚠ <b>Two class declarations, and that is the entire cost.</b> The case is
///     <see cref="MariaDbCase" /> — the one <c>CyberCloud.Providers.DBforMySQL.Conformance</c> already
///     declares — so a provider under both halves of the suite is described exactly once.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MariaDbServerClusterLifecycleConformance(ClusterConformanceFixture<MariaDbCase> fixture)
    : ClusterConformanceTests<MariaDbCase>(fixture), IClassFixture<ClusterConformanceFixture<MariaDbCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-MariaDB provider.</summary>
public sealed class MariaDbServerSiloKillConformance : SiloKillConformanceTests<MariaDbCase>;
