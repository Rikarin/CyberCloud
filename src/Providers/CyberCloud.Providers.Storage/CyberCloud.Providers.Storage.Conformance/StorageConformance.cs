using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Storage.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Conformance;

/// <summary>
///     <c>CyberCloud.Storage/accounts</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the fifth time that number has
///         held.</b> What this one adds is the other end of the range: the Kafka and NATS cases proved
///         the suite could host a provider rendering five objects across three API groups; this one
///         renders <b>one</b>, and it needed no change either. Between them the shape is now bounded
///         on both sides rather than only above.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Objects" /> is one <c>Seaweed</c> and that is not
///         an under-declaration.</b> The masters, the volume servers, the filer, the S3 gateway, their
///         Services and — when monitoring is on — four <c>ServiceMonitor</c>s are all created by the
///         <i>operator</i>, and this suite asserts what the <i>reconciler</i> applied. Listing them
///         here would make the suite fail against every cluster that has no SeaweedFS operator
///         installed, which is every cluster the Docker-free half runs against.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/15's <c>buckets</c> child type is not declared here, and the reason is this
///         file rather than the provider.</b> <see cref="ProviderConformanceCase" /> is single-type,
///         and both <c>ProviderTestCluster.Address</c> and <c>ClusterConformanceHarness.Address</c>
///         construct a <see cref="ResourceId" /> with no <c>ParentNames</c> — so a depth-2
///         <c>Case.Type</c> throws in the constructor and every test in the suite fails before it
///         runs. src/Providers/README.md's hard rule is that a provider is not registered until it
///         passes conformance, so a child type cannot ship through that door at all. See
///         <c>charts/managed/seaweedfs/conformance.yaml § owed</c>, <c>bucket-child-type</c>.
///     </para>
/// </remarks>
public sealed class StorageCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Storage/accounts",
            CreateProvider = () => new StorageProvider(),
            ReconcilerType = typeof(StorageAccountReconciler),
            CreateReconciler = clock => new StorageAccountReconciler(clock),
            Type = StorageAccounts.Type,
            ApiVersion = StorageAccounts.V2026,
            Body = cluster => StorageAccounts.Body(cluster),
            // ⚠ Changes `volumeServers`, which is the property the rendered object carries in TWO
            // places — `spec.volume.replicas` and, through the meters, the amount the update
            // re-reserves. A body that differed only where the reconciler ignores it would pass the
            // update test while proving the update never left the grain.
            ChangedBody = cluster => StorageAccounts.Body(cluster, volumeServers: 5),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(StorageAccounts.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = StorageAccounts.ListKeysAction,
            Objects = (id, ns) => [StorageAccounts.SeaweedRef(ns, id.Name)],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return StorageAccounts.Matches(objectJson, desired.RootElement);
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

/// <summary>The shared suite, run against the managed object-storage provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class StorageAccountConformance(ProviderTestCluster<StorageCase> cluster)
    : ProviderConformanceTests<StorageCase>(cluster), IClassFixture<ProviderTestCluster<StorageCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed object-storage provider.</summary>
public sealed class StorageClusterBackedConformance() : ClusterBackedConformanceTests(StorageCase.ProviderCase);
