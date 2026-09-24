using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Serves <c>POST …/virtualMachines/{name}/start</c>, <c>/stop</c> and <c>/restart</c> — the three
///     power actions docs/plan/13 § Virtual Machines lists.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST HANDLER IN THE TREE THAT WRITES A CLUSTER.</b> Every earlier handler read:
///         a status, a Secret, a compile-time constant. <c>start</c> and <c>stop</c> apply the whole
///         <c>VirtualMachine</c> with a different <c>spec.runStrategy</c>, and <c>restart</c> deletes
///         the <c>VirtualMachineInstance</c>. <see cref="VirtualMachines" />' class remarks carry why
///         the power state is on the object and not in the body; what this file adds is how a write
///         from the action path stays consistent with the reconciler's.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE WHOLE RENDER, UNDER THE RECONCILER'S OWN FIELD MANAGER — never a partial object and
///             never a second manager.
///         </b> Server-side apply treats a manager's document as the complete
///         set of fields it owns, so a partial <c>{spec: {runStrategy}}</c> under the reconciler's
///         manager would prune every other field the reconciler had written; and a second manager
///         would meet <c>KubeCommandBuilder</c>'s reconcile-hash annotation, which is injected
///         non-overridably and differs between two bodies, as a <c>FieldManagerConflict</c> — the
///         wall <c>NetworkProvider</c>'s peering paragraph describes. So the handler renders the same
///         document the reconciler would, from the same body, with one field changed, and applies it
///         the same way.
///     </para>
///     <para>
///         ⚠ <b>An action never creates.</b> A machine with no <c>VirtualMachine</c> yet — one whose
///         first pass has not applied, or whose image is still importing — is refused with
///         <see cref="ErrorCode.OperationInProgress" /> rather than brought into being by a power
///         button. The reconciler's first apply says <c>Always</c>, so a fresh machine boots without
///         anyone pressing anything.
///     </para>
/// </remarks>
public sealed class VirtualMachinePowerHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualMachines.Type;

    /// <summary>Empty: one handler for all three, switching on <see cref="ActionContext.Action" />.</summary>
    public string Action => string.Empty;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Unreachable in production — the type declares RequiresCluster and ActionDispatcher
            // refuses before a handler is reached. Here because "unreachable" is a claim about a call
            // site, and a null dereference would be the symptom.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a virtual machine's power state is "
                + "a field on a KubeVirt object in a cluster."
            );
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        var read = await cluster.GetAsync(VirtualMachines.VirtualMachineRef(ns, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? Result<string>.Failure(
                    ErrorCode.OperationInProgress,
                    $"'{context.Id.Path}' has no VirtualMachine in its cluster yet, so there is nothing to "
                    + $"{context.Action}. A machine is provisioning until its reconciler has applied it — "
                    + "and it boots on its own when it is; an action never creates."
                )
                : Result<string>.Failure(readError);
        }

        var current = VirtualMachines.RunStrategyOf(read.GetValueOrThrow().Json);

        switch (context.Action.ToLowerInvariant()) {
            case VirtualMachines.StartAction:
                return await SetRunStrategyAsync(
                    context,
                    cluster,
                    read.GetValueOrThrow(),
                    VirtualMachines.RunAlways,
                    cancellationToken
                );

            case VirtualMachines.StopAction:
                return await SetRunStrategyAsync(
                    context,
                    cluster,
                    read.GetValueOrThrow(),
                    VirtualMachines.RunHalted,
                    cancellationToken
                );

            case VirtualMachines.RestartAction:
                return await RestartAsync(context, cluster, current, cancellationToken);

            default:
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{context.Action}' is not a power action this handler serves, and the registry "
                    + "routed it here. ComputeProvider declares start, stop and restart on this type."
                );
        }
    }

    /// <summary>How many read-then-apply attempts a power change makes before it answers that the machine keeps moving.</summary>
    public const int MaxAttempts = KubeCoWriter.MaxAttempts;

    /// <summary>Applies the machine with its run strategy set, and answers with both states.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Idempotent: stopping a stopped machine is a <c>200</c> that changed nothing.</b> An
    ///         apply of the same document is a no-op on the API server, and refusing would make the
    ///         generated clients' retry into an error.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Conditional on the read, and read again when the machine moved</b> —
    ///         <see cref="IKubeCommandBuilder.IfResourceVersion" />, the half of
    ///         <c>power-state-can-lose-a-race</c>'s fix that sits on the action's side. A reconcile pass
    ///         that applied between this handler's read and its apply is refused here as
    ///         <see cref="ApplyResult.Stale" /> rather than overwritten; three attempts, as
    ///         <see cref="KubeCoWriter" /> makes, then <see cref="ErrorCode.OperationInProgress" /> so the
    ///         caller retries rather than reading the refusal as final.
    ///     </para>
    /// </remarks>
    static async Task<Result<string>> SetRunStrategyAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        KubeObject live,
        string wanted,
        CancellationToken cancellationToken
    ) {
        for (var attempt = 1;; attempt++) {
            var current = VirtualMachines.RunStrategyOf(live.Json);
            var answered = await ApplyRunStrategyAsync(context, cluster, live, current, wanted, cancellationToken);

            if (answered is not null) {
                return answered.Value;
            }

            if (attempt == MaxAttempts) {
                return Result<string>.Failure(
                    ErrorCode.OperationInProgress,
                    $"'{context.Id.Path}' moved on every one of {MaxAttempts} read-then-apply attempts — its "
                    + "reconciler is applying it — and its power state was not changed. Retry the action."
                );
            }

            var reread = await cluster.GetAsync(
                VirtualMachines.VirtualMachineRef(context.Namespace, context.Id.Name),
                cancellationToken
            );

            if (reread.TryGetError(out var rereadError)) {
                return Result<string>.Failure(rereadError);
            }

            live = reread.GetValueOrThrow();
        }
    }

    /// <summary>One conditional apply: the answer, or <see langword="null" /> when the machine moved and must be read again.</summary>
    static async Task<Result<string>?> ApplyRunStrategyAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        KubeObject live,
        string current,
        string wanted,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(VirtualMachines.VirtualMachineKind)
            .WithApiVersion(context.ApiVersion)
            .WithTemplateLabels(VirtualMachineReconciler.PodTemplatePath, VirtualMachineReconciler.RootDiskTemplatePath)
            .IfResourceVersion(live.ResourceVersion)
            .ObjectJson(VirtualMachines.VirtualMachineJson(context.Namespace, context.Id.Name, context.Desired, wanted))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return Result<string>.Failure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        if (outcome.Result == ApplyResult.Stale) {
            return null;
        }

        if (outcome.Result == ApplyResult.Suspended) {
            return Result<string>.Failure(
                ErrorCode.OperationInProgress,
                outcome.Message.Length > 0
                    ? outcome.Message
                    : "the cluster is unreachable, so the machine's power state could not be changed"
            );
        }

        if (outcome.Result == ApplyResult.Conflict) {
            return Result<string>.Failure(
                ErrorCode.Conflict,
                outcome.Drift?.Describe()
                ?? "another field manager owns part of the VirtualMachine and the power state was not changed"
            );
        }

        return Result<string>.Success(Answer(context.Action, current, wanted));
    }

    /// <summary>Deletes the running instance so <see cref="VirtualMachines.RunAlways" /> brings a new one up.</summary>
    /// <remarks>
    ///     ⚠ Refuses on a halted machine rather than starting it: there is no instance to delete and
    ///     "restart" on a stopped machine is a request whose intent the caller has to make explicit.
    ///     An instance that is not there on a running machine — between two boots — is a restart that
    ///     is already happening, and is answered as one.
    /// </remarks>
    static async Task<Result<string>> RestartAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        string current,
        CancellationToken cancellationToken
    ) {
        if (current == VirtualMachines.RunHalted) {
            return Result<string>.Failure(
                ErrorCode.Conflict,
                $"'{context.Id.Path}' is stopped, so there is no instance to restart. Use start."
            );
        }

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(VirtualMachines.InstanceKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                new JsonObject {
                    ["metadata"] = new JsonObject { ["name"] = VirtualMachines.ObjectNameOf(context.Id.Name) }
                }.ToJsonString()
            )
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return Result<string>.Failure(deleteError);
        }

        return Result<string>.Success(Answer(context.Action, current, current));
    }

    /// <summary>The <c>200</c> body: which action, what the run strategy was, what it is now.</summary>
    static string Answer(string action, string before, string after) =>
        new JsonObject {
            ["action"] = action.ToLowerInvariant(), ["runStrategyBefore"] = before, ["runStrategy"] = after
        }.ToJsonString();
}
