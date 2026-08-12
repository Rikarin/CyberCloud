using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Messaging.Conformance;

namespace CyberCloud.Providers.Messaging.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-NATS provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations, and that is the entire cost of putting a SECOND type in this
///         namespace under the cluster-backed suite.</b> The case is <see cref="NatsCase" /> — the
///         one <c>CyberCloud.Providers.Messaging.Conformance</c> already declares — so a provider
///         under both halves of the suite is described exactly once, and a namespace with two types
///         needs no second project.
///     </para>
///     <para>
///         ⚠ <b>This is the first case in the tree whose objects are MOSTLY not custom resources, and
///         it is the counter-measurement to the one this project's header records.</b> That header
///         says the Kafka case went 5 of 6 red on a bare k3s because a custom resource has no REST
///         path until a definition exists, and that the failure named nothing —
///         <c>k8s.Autorest.HttpOperationException</c> with no status code. Four of the five objects
///         here are <c>ConfigMap</c>, <c>Service</c>, <c>Service</c> and <c>StatefulSet</c>, which
///         every cluster serves without being told. Only the <c>PodMonitor</c> needs a stub, and
///         <c>ClusterConformanceHarness</c> derives it from the case's own <c>Objects</c>.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class NatsClusterLifecycleConformance(ClusterConformanceFixture<NatsCase> fixture)
    : ClusterConformanceTests<NatsCase>(fixture), IClassFixture<ClusterConformanceFixture<NatsCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-NATS provider.</summary>
public sealed class NatsSiloKillConformance : SiloKillConformanceTests<NatsCase>;
