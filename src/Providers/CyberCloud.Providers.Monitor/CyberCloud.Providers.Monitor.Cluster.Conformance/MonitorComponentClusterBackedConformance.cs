using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Monitor.Conformance;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the component type: its one <c>ConfigMap</c> against a
///     real API server, drift repaired after a real delete, and the silo-kill criterion.
/// </summary>
/// <remarks>
///     The views are not here — they read ClickHouse, not the cluster — and are
///     <c>ComponentViewsAgainstClickHouseTests</c>' beside it.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MonitorComponentClusterBackedConformance(ClusterConformanceFixture<MonitorComponentCase> fixture)
    : ClusterConformanceTests<MonitorComponentCase>(fixture),
    IClassFixture<ClusterConformanceFixture<MonitorComponentCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the component type.</summary>
public sealed class MonitorComponentSiloKillConformance : SiloKillConformanceTests<MonitorComponentCase>;
