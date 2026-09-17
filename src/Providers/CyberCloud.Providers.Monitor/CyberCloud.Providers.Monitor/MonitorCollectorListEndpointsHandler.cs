using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Serves <c>POST …/collectors/{name}/listEndpoints</c>: where a tenant's workloads send their
///     telemetry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A pure function of the address, the namespace and the body, and it reaches
///         nothing.</b> The endpoints are the <c>Service</c>'s DNS name and two well-known ports,
///         which exist the moment the reconciler applies the Service and are the same whether or
///         not a pod is behind them — so a collector that has not converged yet gets an address that
///         does not answer rather than an error that says nothing, which is what
///         <c>MonitorWorkspaceListKeysHandler</c> decided for the workspace's endpoints.
///     </para>
///     <para>
///         ⚠ <b>Not secret, and that is a statement about the collector's ingress.</b> Anything
///         inside the cluster that can reach the <c>ClusterIP</c> can send to it; the collector
///         authenticates its <i>exports</i> to the workspace and nothing authenticates a workload to
///         the collector. <c>conformance.yaml § owed</c>, <c>collector-ingress-is-unauthenticated</c>.
///     </para>
/// </remarks>
public sealed class MonitorCollectorListEndpointsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorCollectors.Type;

    /// <inheritdoc />
    public string Action => MonitorCollectors.ListEndpointsAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<string>.Success(
                new JsonObject {
                    ["otlpGrpcEndpoint"] = MonitorCollectors.OtlpGrpcEndpoint(context.Namespace, context.Id, context.Desired),
                    ["otlpHttpEndpoint"] = MonitorCollectors.OtlpHttpEndpoint(context.Namespace, context.Id, context.Desired),
                    ["service"] = MonitorCollectors.ServiceHost(context.Namespace, context.Id),
                    ["workspace"] = MonitorCollectors.WorkspaceNameOf(context.Id)
                }.ToJsonString()
            )
        );
}
