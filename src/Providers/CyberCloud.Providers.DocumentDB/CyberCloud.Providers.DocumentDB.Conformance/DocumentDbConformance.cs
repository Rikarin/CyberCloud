using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.DocumentDB.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB.Conformance;

/// <summary>
///     <c>CyberCloud.DocumentDB/accounts</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the sixth time that number has
///         held.</b> What this one adds is the shape none of the five before it had: four objects
///         across four API groups, one of them expanded by an operator and three of them not. The
///         suite needed no change for it, which is the claim that shape has been making since the
///         second provider.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Objects" /> lists all four, and the operator's
///         expansion of one of them is deliberately absent.</b> The <c>Cluster</c> becomes instance
///         pods, PVCs, three Services and CloudNativePG's own PodMonitor; listing those would make the
///         suite fail against every cluster with no CloudNativePG installed, which is every cluster
///         the Docker-free half runs against. The other three have no controller above them at all,
///         so for those the applied set genuinely is the whole set.
///     </para>
///     <para>
///         ⚠ <b>No <c>Ancestors</c>, because this is a depth-1 type</b> — see
///         <c>charts/managed/ferretdb/conformance.yaml § owed</c>, <c>child-types</c>, for why the
///         obvious children of a document database are the wrong children rather than blocked ones.
///     </para>
/// </remarks>
public sealed class DocumentDbCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.DocumentDB/accounts",
            CreateProvider = () => new DocumentDbProvider(),
            ReconcilerType = typeof(DocumentDbAccountReconciler),
            CreateReconciler = clock => new DocumentDbAccountReconciler(clock),
            Type = DocumentDbAccounts.Type,
            ApiVersion = DocumentDbAccounts.V2026,
            Body = cluster => DocumentDbAccounts.Body(cluster),
            // ⚠ Changes `postgres.instances`, which the rendered Cluster carries AND which the quota
            // meters re-reserve on the update. ⚠ Not `gateway.replicas`, which would have been the
            // tempting choice: it moves the Deployment, which is the object with no operator, so a
            // harness that silently stopped applying the Cluster would still pass. Changing the
            // operator-backed object is what makes the update assertion about the harder half.
            ChangedBody = cluster => DocumentDbAccounts.Body(cluster, instances: 3),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(DocumentDbAccounts.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = DocumentDbAccounts.ListKeysAction,
            Objects = (id, ns) =>
                [
                    DocumentDbAccounts.ClusterRef(ns, id.Name),
                    DocumentDbAccounts.DeploymentRef(ns, id.Name),
                    DocumentDbAccounts.ServiceRef(ns, id.Name),
                    DocumentDbAccounts.PodMonitorRef(ns, id.Name)
                ],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return DocumentDbAccounts.Matches(objectJson, desired.RootElement);
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

/// <summary>The shared suite, run against the managed document-database provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class DocumentDbAccountConformance(ProviderTestCluster<DocumentDbCase> cluster)
    : ProviderConformanceTests<DocumentDbCase>(cluster), IClassFixture<ProviderTestCluster<DocumentDbCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed document-database provider.</summary>
public sealed class DocumentDbClusterBackedConformance()
    : ClusterBackedConformanceTests(DocumentDbCase.ProviderCase);
