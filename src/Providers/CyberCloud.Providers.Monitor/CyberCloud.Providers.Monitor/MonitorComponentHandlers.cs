using CyberCloud.Providers.Monitor.Telemetry;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Serves <c>POST …/components/{name}/listConnectionString</c>: what an application's SDKs are
///     configured with.
/// </summary>
/// <remarks>
///     A pure function of the address, the namespace and the body, and it reaches nothing — the
///     argument <c>MonitorCollectorListEndpointsHandler</c> makes for the collector's endpoints. The
///     same three values are in the component's <c>ConfigMap</c>; this is for a workload that is not
///     a pod in that namespace.
/// </remarks>
public sealed class MonitorComponentConnectionStringHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorComponents.Type;

    /// <inheritdoc />
    public string Action => MonitorComponents.ListConnectionStringAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<string>.Success(
                new JsonObject {
                    ["connectionString"] = MonitorComponents.ConnectionString(context.Namespace, context.Id, context.Desired),
                    ["otlpEndpoint"] = MonitorComponents.Endpoint(context.Namespace, context.Id, context.Desired),
                    ["otlpProtocol"] = MonitorComponents.Protocol(context.Desired),
                    ["resourceAttributes"] = MonitorComponents.ResourceAttributes(context.Id),
                    ["configMap"] = MonitorComponents.ObjectNameOf(context.Id)
                }.ToJsonString()
            )
        );
}

/// <summary>
///     Serves the five views — <c>requests</c>, <c>dependencies</c>, <c>exceptions</c>,
///     <c>applicationMap</c> and <c>transaction</c> — over the workspace's ClickHouse database.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The workspace comes from <see cref="ActionContext.Parent" /> and from nowhere else.
///         </b> The database is <c>ws_{guid:N}</c> of the workspace's GUID, and the GUID is what the
///         manager read from the tenant's own index on this request. The alternatives were read and
///         refused: the workspace's row <c>ConfigMap</c> carries the database name, but it lives in a
///         namespace of a cluster the tenant may administer, and a row rewritten to name another
///         workspace's database would be a cross-tenant read; and a child's reconcile pass never learns
///         its parent's GUID, so the component's own objects cannot carry it either.
///     </para>
///     <para>
///         ⚠ <b>The tenant is checked twice before the store is asked.</b> The manager's step 1 has
///         already refused a caller from another tenant with the canonical <c>404</c>; the parent is
///         then required to be this tenant's workspace, so a dispatcher that handed over anything else
///         is a wiring fault named here rather than a query against it.
///     </para>
///     <para>
///         Synchronous and not secret, on the request path — the gateway's process — for the reason
///         <c>listInstances</c> is. The store is <see cref="ITelemetryStore" />, whose failures are
///         already fit for a caller.
///     </para>
/// </remarks>
/// <param name="store">
///     The telemetry store. ⚠ Optional, and a container without one gets
///     <see cref="UnavailableTelemetryStore" />: every harness that hosts this provider registers all
///     of its handlers into a silo container that validates on build, so a required seam would fail
///     four unrelated suites at fixture start — the lesson <c>MonitorCase.ConfigureSilo</c> records
///     for <c>IAlertControlPlane</c>. A host registers the real one through
///     <c>MonitorApplicationModule</c>.
/// </param>
public sealed class MonitorComponentViewHandler(ITelemetryStore? store = null) : IResourceActionHandler {
    readonly ITelemetryStore store = store ?? new UnavailableTelemetryStore();

    /// <inheritdoc />
    public ResourceTypeName Type => MonitorComponents.Type;

    /// <inheritdoc />
    /// <remarks>Empty: one handler serves all five views, and <see cref="ActionContext.Action" /> says which.</remarks>
    public string Action => string.Empty;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        if (context.Parent is not { } workspace
            || workspace.Id == Guid.Empty
            || workspace.Type != MonitorWorkspaces.Type
            || workspace.TenantId != context.Id.TenantId
            || !string.Equals(workspace.Name, MonitorComponents.WorkspaceNameOf(context.Id), StringComparison.Ordinal)) {
            return Task.FromResult(
                Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{context.Id.Path}' was dispatched without its workspace resolved, so there is no "
                    + "database to read. ResourceManagerService resolves the parent from the tenant's "
                    + "index for every action; a dispatcher that did not is the fault."
                )
            );
        }

        return ComponentViews.RunAsync(store, context.Action, workspace, context.Id, context.Body, cancellationToken);
    }
}
