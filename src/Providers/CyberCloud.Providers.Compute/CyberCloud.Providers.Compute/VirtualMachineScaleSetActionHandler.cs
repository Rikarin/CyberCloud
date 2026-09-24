using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Serves <c>POST …/virtualMachineScaleSets/{name}/scale</c> and <c>/listInstances</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>scale</c> IS THE POWER HANDLER'S SHAPE WITH A NUMBER IN IT.</b> The replica count is
///         on the pool, as a machine's run strategy is on the machine — <see cref="VirtualMachineScaleSets" />'
///         class remarks — so the handler renders the whole pool the reconciler would, from the stored
///         body, with <c>spec.replicas</c> changed, under the reconciler's own field manager and
///         conditional on the version it read. <see cref="VirtualMachinePowerHandler" />' remarks carry
///         why the whole render and one manager; what this adds is the ceiling. A request above the
///         body's <c>capacity</c> is refused with <see cref="ErrorCode.InvalidRequestBody" /> naming the
///         capacity, because capacity is what quota reserved and scaling past it would run machines no
///         reservation covers — raising it is a PUT, which reserves first.
///     </para>
///     <para>
///         ⚠ <b><c>listInstances</c> reads what the pool made, not what it was asked for.</b> The
///         machines are listed by the selector the pool itself selects by, and each is read for KubeVirt's
///         printable status — one <c>GetAsync</c> per machine, bounded by
///         <see cref="VirtualMachineScaleSets.MaxCapacity" /> — so a machine that is still shutting down
///         after a scale-in is listed, which is the fact a tenant asking "what is running" wants.
///     </para>
///     <para>
///         ⚠ <b>An action never creates.</b> A set with no pool yet is refused with
///         <see cref="ErrorCode.OperationInProgress" />, as a machine with no <c>VirtualMachine</c> is.
///     </para>
/// </remarks>
public sealed class VirtualMachineScaleSetActionHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualMachineScaleSets.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a scale set is a pool in a cluster."
            );
        }

        var read = await cluster.GetAsync(
            VirtualMachineScaleSets.PoolRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? Result<string>.Failure(
                    ErrorCode.OperationInProgress,
                    $"'{context.Id.Path}' has no VirtualMachinePool in its cluster yet, so there is nothing to "
                    + $"{context.Action}. A set is provisioning until its reconciler has applied it; an action "
                    + "never creates."
                )
                : Result<string>.Failure(readError);
        }

        return context.Action switch {
            VirtualMachineScaleSets.ScaleAction => await ScaleAsync(context, cluster, read.GetValueOrThrow(), cancellationToken),
            VirtualMachineScaleSets.ListInstancesAction => await ListAsync(
                context,
                cluster,
                read.GetValueOrThrow(),
                cancellationToken
            ),
            _ => Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Action}' is not an action this handler serves, and the registry routed it here. "
                + "ComputeProvider declares scale and listInstances on this type."
            )
        };
    }

    static async Task<Result<string>> ScaleAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        KubeObject live,
        CancellationToken cancellationToken
    ) {
        var capacity = VirtualMachineScaleSets.Capacity(context.Desired);
        var wanted = context.Body.ValueKind == JsonValueKind.Object
            && context.Body.TryGetProperty("replicas", out var replicas)
            && replicas.TryGetInt32(out var count)
                ? count
                : -1;

        if (wanted < 0) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "scale takes {\"replicas\": n}, a whole number from 0 to the set's capacity.",
                "/replicas"
            );
        }

        if (wanted > capacity) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{context.Id.Path}' has a capacity of {capacity.ToString(CultureInfo.InvariantCulture)}, and "
                + $"{wanted.ToString(CultureInfo.InvariantCulture)} machines were asked for. Capacity is what "
                + "quota reserved for this set; raise it with a PUT, which reserves the difference first, and "
                + "scale again.",
                "/replicas"
            );
        }

        for (var attempt = 1;; attempt++) {
            var before = VirtualMachineScaleSets.ReplicasOf(live.Json) ?? capacity;

            var applied = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(VirtualMachineScaleSets.PoolKind)
                .WithApiVersion(context.ApiVersion)
                .WithTemplateLabels([.. VirtualMachineScaleSets.TemplatePaths])
                .IfResourceVersion(live.ResourceVersion)
                .ObjectJson(VirtualMachineScaleSets.PoolJson(context.Namespace, context.Id.Name, context.Desired, wanted))
                .ApplyAsync(cancellationToken);

            if (applied.TryGetError(out var applyError)) {
                return Result<string>.Failure(applyError);
            }

            var outcome = applied.GetValueOrThrow();

            switch (outcome.Result) {
                case ApplyResult.Suspended:
                    return Result<string>.Failure(
                        ErrorCode.OperationInProgress,
                        outcome.Message.Length > 0
                            ? outcome.Message
                            : "the cluster is unreachable, so the set was not scaled"
                    );

                case ApplyResult.Conflict:
                    return Result<string>.Failure(
                        ErrorCode.Conflict,
                        outcome.Drift?.Describe()
                        ?? "another field manager owns part of the VirtualMachinePool and the set was not scaled"
                    );

                case ApplyResult.Stale when attempt < VirtualMachinePowerHandler.MaxAttempts:
                    var reread = await cluster.GetAsync(
                        VirtualMachineScaleSets.PoolRef(context.Namespace, context.Id.Name),
                        cancellationToken
                    );

                    if (reread.TryGetError(out var rereadError)) {
                        return Result<string>.Failure(rereadError);
                    }

                    live = reread.GetValueOrThrow();
                    continue;

                case ApplyResult.Stale:
                    return Result<string>.Failure(
                        ErrorCode.OperationInProgress,
                        $"'{context.Id.Path}' moved on every one of {VirtualMachinePowerHandler.MaxAttempts} "
                        + "read-then-apply attempts — its reconciler is applying it — and it was not scaled. "
                        + "Retry the action."
                    );
            }

            return Result<string>.Success(
                new JsonObject { ["replicasBefore"] = before, ["replicas"] = wanted, ["capacity"] = capacity }
                    .ToJsonString()
            );
        }
    }

    static async Task<Result<string>> ListAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        KubeObject live,
        CancellationToken cancellationToken
    ) {
        var listed = await cluster.ListAsync(
            VirtualMachines.VirtualMachineKind,
            context.Namespace,
            VirtualMachineScaleSets.InstanceSelector(context.Id.Name),
            cancellationToken
        );

        if (listed.TryGetError(out var listError)) {
            return Result<string>.Failure(listError);
        }

        var names = listed.GetValueOrThrow()
            .Select(static x => x.Name)
            .Select(x => (Name: x, Index: VirtualMachineScaleSets.IndexOf(context.Id.Name, x)))
            .Where(static x => x.Index is not null)
            .OrderBy(static x => x.Index)
            .Select(static x => x.Name)
            .ToList();

        var states = new JsonArray();

        foreach (var instance in names) {
            var machine = await cluster.GetAsync(
                VirtualMachines.VirtualMachineRef(context.Namespace, instance),
                cancellationToken
            );

            // ⚠ A machine that went between the list and the read is one a scale-in just finished
            // removing; it is reported as gone rather than failing the whole answer.
            states.Add(
                machine.IsSuccess
                    ? VirtualMachines.PrintableStatus(machine.GetValueOrThrow().Json)
                    : machine.Error!.Code == ErrorCode.ResourceNotFound ? "Deleted" : string.Empty
            );
        }

        return Result<string>.Success(
            new JsonObject {
                ["instances"] = new JsonArray([.. names.Select(static x => (JsonNode)JsonValue.Create(x))]),
                ["states"] = states,
                ["replicas"] = VirtualMachineScaleSets.ReplicasOf(live.Json) ?? 0,
                ["readyReplicas"] = VirtualMachineScaleSets.ReadyReplicasOf(live.Json),
                ["capacity"] = VirtualMachineScaleSets.Capacity(context.Desired)
            }.ToJsonString()
        );
    }
}
