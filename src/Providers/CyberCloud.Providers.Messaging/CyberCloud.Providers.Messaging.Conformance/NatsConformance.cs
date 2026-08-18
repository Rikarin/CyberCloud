using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Messaging.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Conformance;

/// <summary>
///     <c>CyberCloud.Messaging/natsClusters</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, in a project that already holds another
///         provider's — which is the first evidence that docs/plan/03's five-project shape is per
///         NAMESPACE rather than per TYPE.</b> Three providers had said the shape was cheap; this is
///         the first to add a resource type without adding a project.
///     </para>
///     <para>
///         ⚠ <b>Nothing in <c>test/CyberCloud.Conformance</c> changed to accommodate a type that
///         renders FIVE objects across three API groups, two of which share a kind.</b> That is the
///         claim <see cref="ProviderConformanceCase.Objects" /> was making and it has not been tested
///         this hard before: the sample renders one <c>ConfigMap</c>, PostgreSQL two custom
///         resources, Kafka two. ⚠ The one thing that did have to be right is that
///         <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> is <b>one function over every
///         object the resource owns</b> — which is why <see cref="NatsClusters.Matches" /> dispatches
///         on <c>kind</c>, and why it tells the two <c>Service</c>s apart by <c>clusterIP</c> rather
///         than by name: the function is not given the resource's name.
///     </para>
///     <para>
///         ⚠ <b>The cluster-backed half needs exactly ONE definition stub, where Kafka needed
///         two.</b> <c>ClusterConformanceHarness</c> derives them from
///         <see cref="ProviderConformanceCase.Objects" />, and four of the five kinds here are core
///         or <c>apps</c> kinds a bare k3s already serves. The exception is <c>PodMonitor</c>, which
///         the platform bundle installs rather than Kubernetes.
///     </para>
/// </remarks>
public sealed class NatsCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Messaging/natsClusters",
            CreateProvider = () => new MessagingProvider(),
            ReconcilerType = typeof(NatsClusterReconciler),
            CreateReconciler = clock => new NatsClusterReconciler(clock),
            Type = NatsClusters.Type,
            ApiVersion = NatsClusters.V2026,
            Body = cluster => NatsClusters.Body(cluster),
            // ⚠ Changes `servers`, which reaches THREE of the five objects — the StatefulSet's
            // replica count, the ConfigMap's route list, and neither Service. A body that differed
            // only where the reconciler ignores it would pass the update test while proving the
            // update never left the grain.
            //
            // ⚠ AND IT CHANGES AN OBJECT THAT IS NOT THE FIRST ONE APPLIED. The ConfigMap is applied
            // first and the StatefulSet third; a changed body that moved only a ConfigMap field would
            // leave the later applies unexercised by the update path.
            ChangedBody = cluster => NatsClusters.Body(cluster, servers: 5),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(NatsClusters.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = NatsClusters.ListKeysAction,
            // ⚠ FIVE, AND THE FIFTH IS UNCONDITIONAL HERE BECAUSE `Body` ABOVE TURNS MONITORING ON
            // EXPLICITLY. `Objects` is handed the address and the namespace and NOT the body, so a
            // case cannot branch on a setting; the honest way to have a conditional object under
            // conformance is for the case's own body to pin the condition. NatsReconcilerTests covers
            // the other branch, where the PodMonitor is neither rendered nor read back.
            Objects = (id, ns) => [
                NatsClusters.ConfigMapRef(ns, id.Name),
                NatsClusters.HeadlessServiceRef(ns, id.Name),
                NatsClusters.StatefulSetRef(ns, id.Name),
                NatsClusters.ClientServiceRef(ns, id.Name),
                NatsClusters.PodMonitorRef(ns, id.Name)
            ],
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return NatsClusters.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required file-store size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-NATS provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class NatsClusterConformance(ProviderTestCluster<NatsCase> cluster)
    : ProviderConformanceTests<NatsCase>(cluster), IClassFixture<ProviderTestCluster<NatsCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-NATS provider.</summary>
public sealed class NatsClusterBackedConformance() : ClusterBackedConformanceTests(NatsCase.ProviderCase);
