using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Messaging.Conformance;

namespace CyberCloud.Providers.Messaging.ClusterConformance;

/// <summary>
///     The cluster-backed suite, run against the managed-RabbitMQ provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two class declarations, which is the whole cost of putting a THIRD type in this
///         namespace under the cluster-backed suite.</b> The case is <see cref="RabbitmqCase" /> —
///         the one <c>CyberCloud.Providers.Messaging.Conformance</c> already declares — so a provider
///         under both halves of the suite is described exactly once. The NATS file next to this one
///         made that claim for a second type; a third at the same price is what makes it a shape
///         rather than an anecdote.
///     </para>
///     <para>
///         ⚠ <b>Failure class (d): this is where the seven mandatory labels meet a REAL API server.</b>
///         <c>ClusterConformanceHarness</c> derives one <c>CustomResourceDefinition</c> stub from the
///         case's own <c>Objects</c> — <c>rabbitmqclusters.rabbitmq.com</c> at <c>v1beta1</c> — and
///         then applies the rendered object through <c>KubeCommand</c>, which is what injects
///         ADR-013's seven. A label value that is not legal Kubernetes syntax is refused at
///         admission, per object, with a message from the API server rather than from this repository
///         — and <c>cybercloud.messaging_rabbitmqclusters</c> is the value at issue, which
///         <c>RabbitmqOpenApiCasingTests</c> also pins as a literal without a cluster.
///     </para>
///     <para>
///         ⚠ <b>The derived stub is a stub, and on this operator that gap is wider than on the other
///         two.</b> A definition derived from group, version, kind and plural has no schema, no
///         <c>default:</c> values and no admission webhook — where the real
///         <c>rabbitmq.com_rabbitmqclusters.yaml</c> has all three, including a mutating webhook with
///         <c>failurePolicy: Fail</c> that writes <c>spec.image</c>. So this suite proves the object
///         is accepted, labelled and readable back; it cannot prove that
///         <see cref="RabbitmqClusters.Matches" /> survives the defaulting it was written for. That
///         is recorded at <c>charts/managed/rabbitmq/conformance.yaml § owed</c> as
///         <c>defaults-are-untested-against-the-real-crd</c>, because a suite whose limits are not
///         written down reads as a suite that proved more than it did.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class RabbitmqClusterLifecycleConformance(ClusterConformanceFixture<RabbitmqCase> fixture)
    : ClusterConformanceTests<RabbitmqCase>(fixture), IClassFixture<ClusterConformanceFixture<RabbitmqCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-RabbitMQ provider.</summary>
public sealed class RabbitmqSiloKillConformance : SiloKillConformanceTests<RabbitmqCase>;
