using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Messaging.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Conformance;

/// <summary>
///     <c>CyberCloud.Messaging/kafkaClusters</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>One case object and two class declarations, and that is the entire cost of putting the
///     third provider under conformance.</b> docs/plan/03 § Providers: <i>"It is one xUnit theory
///     that every provider must pass … A provider is not registered in the platform bundle until it
///     passes."</i> Nothing in <c>test/CyberCloud.Conformance</c> changed to accommodate a type whose
///     two objects are both custom resources and whose second is bound to the first by a label the
///     builder injects.
/// </remarks>
public sealed class KafkaCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Messaging/kafkaClusters",
            CreateProvider = () => new MessagingProvider(),
            ReconcilerType = typeof(KafkaClusterReconciler),
            CreateReconciler = clock => new KafkaClusterReconciler(clock),
            Type = KafkaClusters.Type,
            ApiVersion = KafkaClusters.V2026,
            Body = cluster => KafkaClusters.Body(cluster),
            // ⚠ Changes `nodes`, which the reconciler renders into the node pool's `spec.replicas`
            // and KafkaClusters.Matches reads back. A body that differed only where the reconciler
            // ignores it would pass the update test while proving the update never left the grain.
            //
            // ⚠ AND IT CHANGES THE OBJECT THAT IS NOT THE FIRST ONE. The Kafka is applied first and
            // is what ObserveAsync reads; a changed body that moved only a Kafka field would leave
            // the second apply unexercised by the update path. `nodes` is the KafkaNodePool's alone.
            ChangedBody = cluster => KafkaClusters.Body(cluster, nodes: 5),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(KafkaClusters.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = KafkaClusters.ListKeysAction,
            // ⚠ TWO OBJECTS AND NO `when`. The PostgreSQL case's second object is conditional on the
            // body it also supplies; this one's is not, because with node pools enabled a Kafka
            // without its pool has no replica count and no storage anywhere — it is a cluster with
            // nowhere to run rather than a smaller cluster.
            Objects = (id, ns) => [
                KafkaClusters.KafkaRef(ns, id.Name),
                KafkaClusters.NodePoolRef(ns, id.Name)
            ],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return KafkaClusters.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required log-volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-Kafka provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class KafkaClusterConformance(ProviderTestCluster<KafkaCase> cluster)
    : ProviderConformanceTests<KafkaCase>(cluster), IClassFixture<ProviderTestCluster<KafkaCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-Kafka provider.</summary>
public sealed class KafkaClusterBackedConformance() : ClusterBackedConformanceTests(KafkaCase.ProviderCase);
