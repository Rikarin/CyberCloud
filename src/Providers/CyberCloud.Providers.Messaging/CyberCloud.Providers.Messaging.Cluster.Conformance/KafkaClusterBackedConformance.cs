using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Messaging.Conformance;

namespace CyberCloud.Providers.Messaging.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-Kafka provider.
/// </summary>
/// <remarks>
///     ⚠ <b>Two class declarations, and that is the entire cost.</b> The case is
///     <see cref="KafkaCase" /> — the one <c>CyberCloud.Providers.Messaging.Conformance</c> already
///     declares — so a provider under both halves of the suite is described exactly once.
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class KafkaClusterLifecycleConformance(ClusterConformanceFixture<KafkaCase> fixture)
    : ClusterConformanceTests<KafkaCase>(fixture), IClassFixture<ClusterConformanceFixture<KafkaCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-Kafka provider.</summary>
public sealed class KafkaSiloKillConformance : SiloKillConformanceTests<KafkaCase>;
