using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Sample.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Sample.Conformance;

/// <summary>
///     <c>CyberCloud.Sample/widgets</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting a provider under conformance, and that is the
///     point being demonstrated.</b> docs/plan/03 § Providers: <i>"The conformance suite is what makes
///     the catalogue safe to grow. It is one xUnit theory that every provider must pass … A provider
///     is not registered in the platform bundle until it passes."</i> A suite that a provider had to
///     copy would drift a clause at a time across twenty copies, which is the same argument
///     docs/plan/08 makes for the write path being one component.
/// </remarks>
public sealed class SampleCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Sample/widgets",
            CreateProvider = () => new SampleProvider(),
            ReconcilerType = typeof(WidgetReconciler),
            CreateReconciler = clock => new WidgetReconciler(clock),
            Type = SampleWidgets.Type,
            ApiVersion = SampleWidgets.V2026,
            Body = cluster => SampleWidgets.Body(cluster),
            // ⚠ Changes `message`, which is a key the reconciler puts in the ConfigMap's data. A body
            // that differed only where the reconciler ignores it would pass the update test while
            // proving the update never reached the cluster.
            ChangedBody = cluster => SampleWidgets.Body(cluster, "goodbye", enabled: false),
            // Drops the required `/properties/message`.
            // ⚠ Built from a valid body with one required property removed, rather than hand-written.
            // A hand-written invalid body drifts out of date the day the schema gains a property, and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => Without(SampleWidgets.Body(cluster), "message"),
            InvalidBodyTarget = "/properties/message",
            ActionName = "ping",
            // One widget owns exactly one ConfigMap, named after the resource, in the resource
            // group's namespace.
            Objects = (id, ns) =>
                [new() { Kind = SampleWidgets.ConfigMapKind, Namespace = ns, Name = id.Name }],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return SampleWidgets.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A body with one property removed from <c>/properties</c>.</summary>
    /// <param name="body">A valid body.</param>
    /// <param name="property">The property to drop.</param>
    static string Without(string body, string property) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove(property);
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the sample provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class SampleWidgetConformance(ProviderTestCluster<SampleCase> cluster)
    : ProviderConformanceTests<SampleCase>(cluster), IClassFixture<ProviderTestCluster<SampleCase>>;

/// <summary>
///     The signpost to the container-backed half, which runs in
///     <c>CyberCloud.Providers.Sample.Cluster.Conformance</c>.
/// </summary>
public sealed class SampleWidgetClusterSignpost() : ClusterBackedConformanceTests(SampleCase.ProviderCase);
