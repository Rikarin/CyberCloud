using CyberCloud.Conformance;
using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Providers.Monitor.Query;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     <c>CyberCloud.Monitor/workspaces/components</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The collector's shape with one object</b> — a cluster-backed child of the
///         cluster-backed workspace, converged onto the fake cluster beside its ancestor — and the
///         suite needed no change to meet it. The action it posts is <c>listConnectionString</c>,
///         which reaches nothing.
///     </para>
///     <para>
///         ⚠ <b>What a green run here does not say: anything about a view.</b> The five views read a
///         ClickHouse database, which this Docker-free suite does not have; the handler here is the
///         refusing store's. <c>ComponentViewsAgainstClickHouseTests</c> runs them against the real
///         collector and a real ClickHouse, through the same harness and the real manager.
///     </para>
/// </remarks>
public sealed class MonitorComponentCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Monitor/workspaces/components",
            CreateProvider = static () => new MonitorProvider(),
            ReconcilerType = typeof(MonitorComponentReconciler),
            CreateReconciler = static clock => new MonitorComponentReconciler(clock),
            Type = MonitorComponents.Type,
            ApiVersion = MonitorWorkspaces.V2026,
            Body = static cluster => MonitorComponents.Body(cluster),
            // The protocol moves the endpoint's port, which is in the ConfigMap, so the update test
            // sees the change reach the cluster.
            ChangedBody = static cluster => MonitorComponents.Body(cluster, protocol: MonitorComponents.Grpc),
            // A protocol the SDKs do not have, which the schema's AllowedValues refuses.
            InvalidBody = static cluster => WithProtocol(MonitorComponents.Body(cluster), "udp"),
            InvalidBodyTarget = "/properties/protocol",
            ActionName = MonitorComponents.ListConnectionStringAction,
            Objects = static (id, ns) => MonitorComponents.Objects(ns, id),
            OperatorWritten = static (_, _) => [],
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return MonitorComponents.Matches(match.ObjectJson, match.Target.Namespace, match.Id, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [MonitorCase.ProviderCase];

    /// <inheritdoc />
    /// <remarks>
    ///     The line <c>MonitorCollectorCase</c> carries, for the siblings' handlers and the workspace's
    ///     reconciler. ⚠ It read <c>AddCyberCloudMonitorAlerting()</c> alone until #28's merge ran this
    ///     suite on master: #41's second review gave the other two cases <c>AddCyberCloudMonitorQuery</c>
    ///     (the query handlers and <c>IMonitorAccounts</c>) and #32's components merged beside it with the
    ///     older line, so every case in this class failed in its fixture.
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => services.AddCyberCloudMonitorAlerting().AddCyberCloudMonitorQuery(new()));

    static string WithProtocol(string body, string protocol) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["protocol"] = protocol;
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the component type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MonitorComponentConformance(ProviderTestCluster<MonitorComponentCase> cluster)
    : ProviderConformanceTests<MonitorComponentCase>(cluster), IClassFixture<ProviderTestCluster<MonitorComponentCase>>;

/// <summary>The container-backed half, skipped loudly, against the component type.</summary>
public sealed class MonitorComponentBackedConformance()
    : ClusterBackedConformanceTests(MonitorComponentCase.ProviderCase);
