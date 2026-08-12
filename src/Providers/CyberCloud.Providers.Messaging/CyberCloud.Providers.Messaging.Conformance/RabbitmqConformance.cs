using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Messaging.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Conformance;

/// <summary>
///     <c>CyberCloud.Messaging/rabbitmqClusters</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, in a project that already holds two other
///         types' — which turns the NATS case's claim into a measurement.</b> That file said adding a
///         second type to a namespace cost no new project and read it as the first evidence that
///         docs/plan/03's five-project shape is per <i>namespace</i>. A third type costing exactly
///         the same again is what makes the cost flat rather than merely small once.
///     </para>
///     <para>
///         ⚠ <b>This is the SIMPLEST case in the tree by object count, and that is the counter-reading
///         to <see cref="NatsCase" />'s.</b> That one renders five objects across three API groups,
///         two of which share a kind, because <c>nats-operator</c> was archived. This one renders
///         <b>one</b>, because the RabbitMQ Cluster Operator is alive and official — so
///         <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> needs no <c>kind</c> dispatch
///         at all, and the cluster-backed half needs exactly one definition stub.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.ChangedBody" /> moves <c>nodes</c> rather than the
///         default queue type, and the choice is deliberate.</b> The update test asserts that a
///         changed body reaches the cluster; <c>queues.defaultType</c> reaches
///         <c>spec.rabbitmq.additionalConfig</c>, which <see cref="RabbitmqClusters.Matches" />
///         compares with <c>Contains</c> — so a body that changed only it would be a weaker probe than
///         one that changes a field compared by equality. <c>RabbitmqMatchesTests</c> covers the
///         config line directly and by both settings.
///     </para>
/// </remarks>
public sealed class RabbitmqCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Messaging/rabbitmqClusters",
            CreateProvider = () => new MessagingProvider(),
            ReconcilerType = typeof(RabbitmqClusterReconciler),
            CreateReconciler = clock => new RabbitmqClusterReconciler(clock),
            Type = RabbitmqClusters.Type,
            ApiVersion = RabbitmqClusters.V2026,
            Body = cluster => RabbitmqClusters.Body(cluster),
            // ⚠ Changes `nodes`, which is the field the operator's own controller is most likely to
            // fight over — it declares a scale subresource — and the one whose read-back is an
            // equality comparison rather than a containment one.
            ChangedBody = cluster => RabbitmqClusters.Body(cluster, nodes: 5),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(RabbitmqClusters.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = RabbitmqClusters.ListKeysAction,
            // ⚠ ONE, AND NOTHING ABOUT IT IS CONDITIONAL. `Objects` is handed the address and the
            // namespace and NOT the body, so a case cannot branch on a setting — which is the
            // constraint that forced NatsCase's body to pin `monitoring.enabled`. This type has no
            // conditional object to pin, because the operator owns everything a setting could turn
            // on or off.
            Objects = (id, ns) => [RabbitmqClusters.ClusterRef(ns, id.Name)],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return RabbitmqClusters.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required message-store size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-RabbitMQ provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class RabbitmqClusterConformance(ProviderTestCluster<RabbitmqCase> cluster)
    : ProviderConformanceTests<RabbitmqCase>(cluster), IClassFixture<ProviderTestCluster<RabbitmqCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-RabbitMQ provider.</summary>
public sealed class RabbitmqClusterBackedConformance() : ClusterBackedConformanceTests(RabbitmqCase.ProviderCase);
