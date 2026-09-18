using CyberCloud.Conformance;
using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.Providers.Monitor.Contracts;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     <c>CyberCloud.Monitor/workspaces/collectors</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE FIRST CLUSTER-BACKED CHILD OF A CLUSTER-BACKED PARENT IN THIS FAMILY, AND THE
///             SUITE NEEDED NO CHANGE TO MEET IT.
///         </b> <c>AgentPools</c> taught <c>test/CyberCloud.Conformance</c> the shape — an ancestor
///         converged onto the fake cluster, then a child with its own <c>clusterId</c> converged
///         beside it — and the alert rule taught this family the ancestor machinery. The two compose
///         with nothing new.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> IS EXACT HERE, WHERE THE
///             WORKSPACE'S IS BY SHAPE, AND THE DIFFERENCE IS THE DESIGN.
///         </b> The workspace renders its
///         own GUID into every object, and the harness's <c>MatchContext</c> carries the address, so
///         that case could compare exactly and does not, for a reason its remarks give. This type
///         renders <b>nothing</b> keyed on any GUID: the configuration substitutes the workspace's
///         coordinates through the kubelet at pod start (<see cref="MonitorWorkspaces.WorkspaceEnv" />),
///         so every rendered document is a pure function of the address's names and the body, and
///         <see cref="MonitorCollectors.Matches" /> compares the configuration byte for byte.
///     </para>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> The twelve-step write path,
///         the verb grammar, the four reconciler clauses, the cross-tenant <c>404</c> and the
///         delete-read-back, over three objects on the fake cluster. It proves <b>nothing</b> about a
///         pod starting: the fake cluster schedules nothing. That is
///         <c>MonitorCollectorClusterBackedConformance.TheCollectorPodStartsAndAcceptsAnOtlpExport</c>'s,
///         on a real kubelet, and it is the one assertion on this type that reads what a node did.
///     </para>
/// </remarks>
public sealed class MonitorCollectorCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Monitor/workspaces/collectors",
            CreateProvider = static () => new MonitorProvider(),
            ReconcilerType = typeof(MonitorCollectorReconciler),
            CreateReconciler = static clock => new MonitorCollectorReconciler(clock),
            Type = MonitorCollectors.Type,
            ApiVersion = MonitorWorkspaces.V2026,
            Body = static cluster => MonitorCollectors.Body(cluster),
            // Two replicas rather than one: the update test asserts the change reached the cluster,
            // so it has to move something the reconciler applies, and the replica count is on the
            // Deployment and in the vCPU and memory meters both.
            ChangedBody = static cluster => MonitorCollectors.Body(cluster, replicas: 2),
            // Zero replicas, which the schema's Minimum refuses. Built from a valid body with one
            // property overwritten, for the reason the workspace's case gives.
            InvalidBody = static cluster => WithReplicas(MonitorCollectors.Body(cluster), 0),
            InvalidBodyTarget = "/properties/replicas",
            ActionName = MonitorCollectors.ListEndpointsAction,
            // In apply order, matching the reconciler: configuration, collector, address.
            Objects = static (id, ns) => MonitorCollectors.Objects(ns, id),
            OperatorWritten = static (_, _) => [],
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return MonitorCollectors.Matches(match.ObjectJson, match.Id, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [MonitorCase.ProviderCase];

    /// <inheritdoc />
    /// <remarks>
    ///     The same line <c>MonitorCase.ConfigureSilo</c> carries, for the same reason: the harness
    ///     registers every handler of this provider by concrete type, and a sibling's handler takes
    ///     <c>IAlertControlPlane</c>.
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => services.AddCyberCloudMonitorAlerting());

    static string WithReplicas(string body, int replicas) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["replicas"] = replicas;
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the collector type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MonitorCollectorConformance(ProviderTestCluster<MonitorCollectorCase> cluster)
    : ProviderConformanceTests<MonitorCollectorCase>(cluster), IClassFixture<ProviderTestCluster<MonitorCollectorCase>>;

/// <summary>The container-backed half, skipped loudly, against the collector type.</summary>
public sealed class MonitorCollectorBackedConformance()
    : ClusterBackedConformanceTests(MonitorCollectorCase.ProviderCase);
