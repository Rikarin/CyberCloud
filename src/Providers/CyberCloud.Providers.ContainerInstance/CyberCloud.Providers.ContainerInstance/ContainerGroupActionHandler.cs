using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerInstance;

/// <summary>
///     Serves <c>POST …/containerGroups/{name}/logs</c> and <c>/restart</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>logs</c> IS THE FIRST ACTION IN THE TREE THAT READS WHAT A TENANT'S WORKLOAD WROTE.</b>
///         docs/plan/13 § Container Instances names this type as the one that proves "the log-streaming
///         path", and until it landed there was no path: <c>IKubeClusterConnection</c> addressed the API
///         server's objects and nothing else. <c>ReadLogsAsync</c> is the seam it added — the pod's
///         <c>log</c> subresource, through the connection grain, as a tail of at most
///         <see cref="ContainerGroups.MaxTailLines" /> lines and a mebibyte. A container the kubelet has
///         not started answers <see cref="ErrorCode.OperationInProgress" />, which a caller retries.
///     </para>
///     <para>
///         ⚠ <b><c>restart</c> REPLACES THE POD, BECAUSE KUBERNETES HAS NO VERB THAT RESTARTS ONE.</b> The
///         handler deletes the pod, waits for it to be gone — the new one takes its name, and
///         <see cref="ContainerGroups.TerminationGracePeriodSeconds" /> is ten seconds so that fits one
///         request — and applies the same render the reconciler would, under the same field manager.
///         ⚠ <b>An action never creates</b>: a group with no pod yet is refused with
///         <see cref="ErrorCode.OperationInProgress" />. A pod that is still shutting down after the
///         wait is answered the same way; the group's next reconcile pass creates it, because the
///         reconciler converges a missing pod whatever made it missing.
///     </para>
///     <para>
///         ⚠ <b>The render needs the Secrets the reconciler wrote, and only their names.</b> The pod
///         names <c>{name}-env</c> and <c>{name}-pull</c> when the body has a secure environment or a
///         pull credential; both already exist once the group has converged, so the handler resolves
///         nothing and holds no vault value.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <c>readAt</c>. The handler's only field, and it is not mutable.</param>
public sealed class ContainerGroupActionHandler(IClock clock) : IResourceActionHandler {
    /// <summary>How long a restart waits for the old pod to be gone.</summary>
    /// <remarks>
    ///     ⚠ <b>Inside the dispatcher's budget, which it was not.</b> <c>ActionDispatcher</c> cancels a
    ///     handler at <c>ReconcileDriver.PassBudget</c> (30 s) and answers <c>InternalError</c>, "abandoned".
    ///     This read 45 s, so a pod that took longer than 30 s to go never reached the retryable
    ///     <see cref="ErrorCode.OperationInProgress" /> below — #28's review. Twenty is the ten-second
    ///     grace period (<see cref="ContainerGroups.TerminationGracePeriodSeconds" />) twice over, and
    ///     leaves ten for the apply and the read after it.
    /// </remarks>
    public static readonly TimeSpan RestartBudget = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public ResourceTypeName Type => ContainerGroups.Type;

    /// <summary>Empty: one handler for both actions, switching on <see cref="ActionContext.Action" />.</summary>
    public string Action => string.Empty;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a container group is a pod in a cluster."
            );
        }

        var pod = ContainerGroups.PodRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(pod, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? Result<string>.Failure(
                    ErrorCode.OperationInProgress,
                    $"'{context.Id.Path}' has no pod in its cluster right now, so there is nothing to "
                    + $"{context.Action}. A group is provisioning until its reconciler has applied it, and is "
                    + "being replaced for a moment after a change; an action never creates."
                )
                : Result<string>.Failure(readError);
        }

        return context.Action switch {
            ContainerGroups.LogsAction => await LogsAsync(context, cluster, pod, cancellationToken),
            ContainerGroups.RestartAction => await RestartAsync(context, cluster, pod, read.GetValueOrThrow(), cancellationToken),
            _ => Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Action}' is not an action this handler serves, and the registry routed it here. "
                + "ContainerInstanceProvider declares logs and restart on this type."
            )
        };
    }

    async Task<Result<string>> LogsAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        ObjectRef pod,
        CancellationToken cancellationToken
    ) {
        var containers = ContainerGroups.Containers(context.Desired);
        var container = Member(context.Body, "container") is { Length: > 0 } named ? named : containers.FirstOrDefault().Name ?? string.Empty;

        if (!containers.Any(x => x.Name == container)) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{container}' is not a container of '{context.Id.Path}', which has "
                + string.Join(", ", containers.Select(static x => x.Name))
                + ".",
                "/container"
            );
        }

        var tail = context.Body.ValueKind == JsonValueKind.Object
            && context.Body.TryGetProperty("tailLines", out var lines)
            && lines.TryGetInt32(out var count)
                ? count
                : ContainerGroups.DefaultTailLines;

        var log = await cluster.ReadLogsAsync(pod, container, tail, cancellationToken);

        if (log.TryGetError(out var logError)) {
            return Result<string>.Failure(logError);
        }

        return Result<string>.Success(
            new JsonObject {
                ["container"] = container,
                ["log"] = log.GetValueOrThrow(),
                ["readAt"] = clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }.ToJsonString()
        );
    }

    static async Task<Result<string>> RestartAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        ObjectRef pod,
        KubeObject live,
        CancellationToken cancellationToken
    ) {
        var before = ContainerGroups.UidOf(live.Json);

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(ContainerGroups.PodKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = pod.Name } }.ToJsonString())
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return Result<string>.Failure(deleteError);
        }

        // ⚠ The new pod takes the old one's name, so it cannot be applied until the old one is gone —
        // an apply onto a terminating pod patches the pod that is being deleted.
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (true) {
            var gone = await cluster.GetAsync(pod, cancellationToken);

            if (gone.TryGetError(out var goneError)) {
                if (goneError.Code == ErrorCode.ResourceNotFound) {
                    break;
                }

                return Result<string>.Failure(goneError);
            }

            if (ContainerGroups.UidOf(gone.GetValueOrThrow().Json) is { Length: > 0 } uid && uid != before) {
                // The reconciler got there first and created the replacement; the restart has happened.
                return Answer(before, uid);
            }

            if (waited.Elapsed > RestartBudget) {
                return Result<string>.Failure(
                    ErrorCode.OperationInProgress,
                    $"the pod of '{context.Id.Path}' is still shutting down after "
                    + $"{RestartBudget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds; the group's next "
                    + "reconcile creates its replacement."
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(ContainerGroups.PodKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(ContainerGroups.PodJson(context.Namespace, context.Id.Name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return Result<string>.Failure(applyError);
        }

        if (applied.GetValueOrThrow().Result == ApplyResult.Suspended) {
            return Result<string>.Failure(
                ErrorCode.OperationInProgress,
                "the cluster became unreachable after the pod was removed; the group's next reconcile creates it."
            );
        }

        var after = await cluster.GetAsync(pod, cancellationToken);

        return Answer(before, after.IsSuccess ? ContainerGroups.UidOf(after.GetValueOrThrow().Json) : string.Empty);
    }

    static Result<string> Answer(string before, string after) =>
        Result<string>.Success(
            new JsonObject { ["action"] = ContainerGroups.RestartAction, ["podUidBefore"] = before, ["podUid"] = after }
                .ToJsonString()
        );

    static string Member(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
