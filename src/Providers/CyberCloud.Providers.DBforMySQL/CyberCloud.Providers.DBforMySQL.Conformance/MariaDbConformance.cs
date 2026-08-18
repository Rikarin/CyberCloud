using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.DBforMySQL.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Conformance;

/// <summary>
///     <c>CyberCloud.DBforMySQL/servers</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting the sixth provider under conformance.</b>
///     docs/plan/03 § Providers: <i>"It is one xUnit theory that every provider must pass … A provider
///     is not registered in the platform bundle until it passes."</i>
/// </remarks>
public sealed class MariaDbCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.DBforMySQL/servers",
            CreateProvider = () => new MariaDbProvider(),
            ReconcilerType = typeof(MariaDbServerReconciler),
            CreateReconciler = clock => new MariaDbServerReconciler(clock),
            Type = MariaDbServers.Type,
            ApiVersion = MariaDbServers.V2026,
            Body = cluster => MariaDbServers.Body(cluster),
            // ⚠ Changes `bootstrap.database`, which the reconciler renders into `spec.database` and
            // MariaDbServers.Matches reads back. A body that differed only where the reconciler
            // ignores it would pass the update test while proving the update never left the grain.
            //
            // ⚠ NOT `highAvailability`, which is the obvious axis and is Immutable — an update test
            // driving an immutable property would be testing the write path's refusal rather than the
            // reconciler's convergence, and would fail for the case's reason.
            ChangedBody = cluster => MariaDbServers.Body(cluster, database: "orders"),
            // Drops the required `/properties/version`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutVersion(MariaDbServers.Body(cluster)),
            InvalidBodyTarget = "/properties/version",
            ActionName = MariaDbServers.ListKeysAction,
            // ⚠ ONE OBJECT. A MariaDB expands into a StatefulSet, four Services and ConfigMaps; none
            // of them is applied by this provider, so none of them belongs here. A case listing an
            // object the provider does not apply fails every world-facing assertion for the case's
            // reason rather than the provider's.
            Objects = (id, ns) => [MariaDbServers.ServerRef(ns, id.Name)],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return MariaDbServers.Matches(match.ObjectJson, desired.RootElement);
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

/// <summary>The shared suite, run against the managed-MariaDB provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MariaDbServerConformance(ProviderTestCluster<MariaDbCase> cluster)
    : ProviderConformanceTests<MariaDbCase>(cluster), IClassFixture<ProviderTestCluster<MariaDbCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-MariaDB provider.</summary>
/// <remarks>
///     ⚠ <b>Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///     that makes the same assertions against a real API server.</b> The skips are not redundant with
///     it: they run on a machine with no Docker daemon and say, by name, which criteria were not
///     checked. Deleting them here would make "conformance: green" readable as "the cluster-backed
///     criteria were met" on exactly the machines where they were not.
/// </remarks>
public sealed class MariaDbServerClusterBackedConformance() : ClusterBackedConformanceTests(MariaDbCase.ProviderCase);
