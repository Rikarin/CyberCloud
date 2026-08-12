using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Analytics.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Conformance;

/// <summary>
///     <c>CyberCloud.Analytics/clickhouseClusters</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the sixth time that number has
///         held.</b> What this one adds is a shape neither end of the existing range covers: two
///         objects in two different <b>API groups</b>, where one of them carries a string derived from
///         the other's name. The suite's <c>Objects</c> member takes a list, so that cost nothing; the
///         cluster-backed sibling installs two definition stubs instead of one, also derived.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Objects" /> is the two custom resources and not the
///         workload.</b> One StatefulSet per host, a Service per host, the client Service, the
///         ConfigMaps and the Keeper's own StatefulSet are all created by the <i>operator</i>, and this
///         suite asserts what the <i>reconciler</i> applied. Listing them here would make the suite
///         fail against every cluster that has no Altinity operator installed, which is every cluster
///         the Docker-free half runs against.
///     </para>
///     <para>
///         ⚠ <b>No child type, and the reason is scope rather than a blocker.</b> Every obstacle the
///         four providers before this recorded is closed — <c>IProviderCaseSource.Ancestors</c>,
///         <c>ProviderTestCluster.Address</c>, <c>CliEmitter</c> and <c>SdkEmitter</c> all carry
///         ancestors since 2026-08-12, and <c>CyberCloud.Storage/accounts/buckets</c> ships through
///         that door. What is different here is that the obvious children — databases and tables — are
///         the ones docs/plan/12 forbids in as many words: <i>"the resource does not manage tables"</i>.
///         So this is the first row whose child types are refused by the catalogue rather than by the
///         platform, which is a different kind of absence and is worth not confusing with the others.
///     </para>
/// </remarks>
public sealed class AnalyticsCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Analytics/clickhouseClusters",
            CreateProvider = () => new AnalyticsProvider(),
            ReconcilerType = typeof(ClickHouseClusterReconciler),
            CreateReconciler = clock => new ClickHouseClusterReconciler(clock),
            Type = ClickHouseClusters.Type,
            ApiVersion = ClickHouseClusters.V2026,
            Body = cluster => ClickHouseClusters.Body(cluster),
            // ⚠ Changes `shards`, which is the property that reaches the rendered object in the place
            // a copied derivation gets wrong — `spec.configuration.clusters[0].layout.shardsCount` —
            // and which moves all three meters through ClickHouseClusters.Servers. A body that
            // differed only where the reconciler ignores it would pass the update test while proving
            // the update never left the grain.
            ChangedBody = cluster => ClickHouseClusters.Body(cluster, shards: 3),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(ClickHouseClusters.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = ClickHouseClusters.ListKeysAction,
            // ⚠ THE KEEPER FIRST, MATCHING THE ORDER THE RECONCILER APPLIES THEM IN. The suite does
            // not require an order and the reconciler's reason for having one is in its remarks;
            // listing them the other way round here would be a second, quieter opinion about which
            // object comes first.
            Objects = (id, ns) => [
                ClickHouseClusters.KeeperRef(ns, id.Name),
                ClickHouseClusters.ClickHouseRef(ns, id.Name)
            ],
            // ⚠ ONE FUNCTION OVER BOTH KINDS, WHICH IS WHY ClickHouseClusters.Matches DISPATCHES ON
            // `kind` AND RETURNS FALSE FOR ONE IT DOES NOT KNOW. A Matches that defaulted to true for
            // an unrecognised document would report a Keeper that was never applied as converged, and
            // this member is the only place that shape is exercised.
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return ClickHouseClusters.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required data-volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed ClickHouse provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ClickHouseClusterConformance(ProviderTestCluster<AnalyticsCase> cluster)
    : ProviderConformanceTests<AnalyticsCase>(cluster), IClassFixture<ProviderTestCluster<AnalyticsCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed ClickHouse provider.</summary>
public sealed class ClickHouseClusterBackedConformance()
    : ClusterBackedConformanceTests(AnalyticsCase.ProviderCase);
