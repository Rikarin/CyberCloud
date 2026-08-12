using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Cache.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Conformance;

/// <summary>
///     <c>CyberCloud.Cache/redis</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting the third provider under conformance.</b>
///     docs/plan/03 § Providers: <i>"It is one xUnit theory that every provider must pass … A provider
///     is not registered in the platform bundle until it passes."</i> Nothing in
///     <c>test/CyberCloud.Conformance</c> changed for a type that renders one object where the last
///     one rendered two, or for an <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> that is
///     a containment test rather than a field comparison.
/// </remarks>
public sealed class ValkeyCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Cache/redis",
            CreateProvider = () => new ValkeyCacheProvider(),
            ReconcilerType = typeof(ValkeyCacheReconciler),
            CreateReconciler = clock => new ValkeyCacheReconciler(clock),
            Type = ValkeyCaches.Type,
            ApiVersion = ValkeyCaches.V2026,
            Body = cluster => ValkeyCaches.Body(cluster),
            // ⚠ Changes `replicas`, which the reconciler renders into `spec.redis.replicas` and
            // ValkeyCaches.Matches reads back. A body that differed only where the reconciler ignores
            // it would pass the update test while proving the update never left the grain.
            ChangedBody = cluster => ValkeyCaches.Body(cluster, replicas: 5),
            // Drops the required `/properties/version`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutVersion(ValkeyCaches.Body(cluster)),
            InvalidBodyTarget = "/properties/version",
            ActionName = ValkeyCaches.ListKeysAction,
            // ⚠ ONE OBJECT, and the count is a fact about the operator rather than about this suite. A
            // RedisFailover expands into a StatefulSet, a Deployment, three Services and two
            // ConfigMaps; none of them is applied by this provider, so none of them belongs here. A
            // case listing an object the provider does not apply fails every world-facing assertion for
            // the case's reason rather than the provider's.
            Objects = (id, ns) => [ValkeyCaches.FailoverRef(ns, id.Name)],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return ValkeyCaches.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required major version removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutVersion(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove("version");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-Valkey provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ValkeyCacheConformance(ProviderTestCluster<ValkeyCase> cluster)
    : ProviderConformanceTests<ValkeyCase>(cluster), IClassFixture<ProviderTestCluster<ValkeyCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-Valkey provider.</summary>
/// <remarks>
///     ⚠ <b>Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///     that makes the same assertions against a real API server.</b> The skips are not redundant with
///     it: they run on a machine with no Docker daemon and say, by name, which criteria were not
///     checked. Deleting them here would make "conformance: green" readable as "the cluster-backed
///     criteria were met" on exactly the machines where they were not — which is the reading
///     <c>ClusterBackedConformanceTests</c> exists to prevent.
/// </remarks>
public sealed class ValkeyCacheClusterBackedConformance() : ClusterBackedConformanceTests(ValkeyCase.ProviderCase);
