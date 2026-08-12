using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Search.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Conformance;

/// <summary>
///     <c>CyberCloud.Search/services</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One case object and two class declarations, which is the sixth time that number has
///         held.</b> What this one adds is a body whose rendered object contains an <b>array the
///         provider owns wholesale</b> — <c>spec.nodePools</c>. Every earlier case's rendered spec is a
///         tree of scalars, so <c>ObjectMatchesDesired</c> had never been asked to compare a list whose
///         order the API server is free to change.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Objects" /> is one <c>OpenSearchCluster</c> and that
///         is not an under-declaration.</b> The three StatefulSets, their Services, the generated TLS
///         Secrets, the generated admin-credentials Secret and the <c>ServiceMonitor</c> are all
///         created by the <i>operator</i>, and this suite asserts what the <i>reconciler</i> applied.
///         Listing them here would make the suite fail against every cluster that has no OpenSearch
///         operator installed, which is every cluster the Docker-free half runs against.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.ChangedBody" /> moves
///         <c>coordinatingNodes</c> rather than <c>dataNodes</c>, and the choice is the opposite of the
///         one <c>StorageCase</c> makes for its own reason.</b> That case picks the property carried in
///         two places; this one picks the property whose change alters the <b>shape</b> of the rendered
///         object rather than a value inside it — zero coordinating nodes renders two node pools and
///         one renders three. An update test that only ever moved a scalar would pass for a renderer
///         that had lost the ability to add a pool at all.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.Body" /> IS THE SMALLEST LEGAL SERVICE AND NOT THE
///         SCHEMA'S OWN DEFAULT, AND THE REASON IS A PROPERTY OF THE HARNESS THAT NO EARLIER PROVIDER
///         HAD REACHED.</b> The suite shares <b>one subscription</b> across every create it makes and
///         nothing releases the committed amounts between assertions, so the number of resources a
///         provider can create before it is refused is
///         <c>QuotaGrain.Defaults[MemoryGb] / its own memory draw</c> — 400 GiB divided by whatever the
///         case's body costs. This type's <i>schema default</i> — three data nodes at <c>m1.medium</c>
///         plus three cluster managers — draws <b>30 GiB</b>, which is twice the heaviest body in the
///         tree before it (<c>CyberCloud.Storage/accounts</c>' 15 GiB), and four of this suite's
///         assertions failed with <i>"300 committed + 90 reserved + 30 requested &gt; 400"</i>.
///         <para>
///             ⚠ <b>The failure names quota and not the provider, which is what makes it worth writing
///             down rather than just fixing.</b> A provider author meeting it sees a subscription limit
///             in a shared harness and has no reason to connect it to their own sizing table; the next
///             service in docs/plan/12 with a JVM in it will meet it again. Recorded at
///             <c>charts/managed/opensearch/conformance.yaml § owed</c>,
///             <c>conformance-quota-is-a-budget-per-provider</c>. One data node and one cluster
///             manager is 10 GiB, which is a body every assertion here is equally true of — the suite
///             asserts a <i>lifecycle</i>, and <c>OpenSearchQuotaTests</c> is where the sizing is
///             asserted.
///         </para>
///     </para>
/// </remarks>
public sealed class OpenSearchCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Search/services",
            CreateProvider = () => new SearchProvider(),
            ReconcilerType = typeof(OpenSearchServiceReconciler),
            CreateReconciler = clock => new OpenSearchServiceReconciler(clock),
            Type = OpenSearchServices.Type,
            ApiVersion = OpenSearchServices.V2026,
            // ⚠ THE SMALLEST LEGAL SERVICE, NOT THE SCHEMA'S DEFAULT — see this type's remarks for the
            // harness property that forces it and for why it is recorded rather than only fixed.
            Body = cluster => OpenSearchServices.Body(cluster, dataNodes: 1, masterNodes: 1),
            ChangedBody = cluster => OpenSearchServices.Body(
                cluster,
                dataNodes: 1,
                masterNodes: 1,
                coordinatingNodes: 1
            ),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(
                OpenSearchServices.Body(cluster, dataNodes: 1, masterNodes: 1)
            ),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = OpenSearchServices.ListKeysAction,
            Objects = (id, ns) => [OpenSearchServices.ClusterRef(ns, id.Name)],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return OpenSearchServices.Matches(objectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required per-data-node disk size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed search provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class OpenSearchServiceConformance(ProviderTestCluster<OpenSearchCase> cluster)
    : ProviderConformanceTests<OpenSearchCase>(cluster), IClassFixture<ProviderTestCluster<OpenSearchCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed search provider.</summary>
public sealed class OpenSearchClusterBackedConformance()
    : ClusterBackedConformanceTests(OpenSearchCase.ProviderCase);
