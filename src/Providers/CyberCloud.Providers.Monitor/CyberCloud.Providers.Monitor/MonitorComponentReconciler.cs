using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Converges one <c>workspaces/components</c> resource onto the one object it is: the
///     connection string, as a <c>ConfigMap</c> a pod can <c>envFrom</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It reads nothing of its workspace or its collector.</b> The endpoint is the named
///         collector's <c>Service</c> address, a pure function of two names and the namespace
///         (<see cref="MonitorComponents.Endpoint" />), so the document is a function of the address
///         and the body and clause 1 holds trivially. What a component <i>reads</i> — the workspace's
///         traces and logs — is its views', on the request path, and nothing here touches the store.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop: idempotent (one pure rendering);
///         no hidden state (the one field is the clock); bounded (one apply and one read); observes,
///         never assumes (<see cref="ReconcileOutcome.Converged" /> follows a read-back that matches).
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MonitorComponentReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorComponents.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a component's connection string is "
                + "a ConfigMap in a cluster. CyberCloud.Monitor/workspaces/components declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        context.Log.Report(
            "applying-connection-string",
            $"publishing the connection string of component '{context.Id.Name}'",
            50
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(MonitorComponents.ConfigMapKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(MonitorComponents.ConfigMapJson(context.Namespace, context.Id, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);
                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );
            case ApplyResult.Conflict:
                var describe = outcome.Drift?.Describe()
                    ?? "another field manager owns part of the ConfigMap and it was not overwritten";
                context.Log.Report("conflict", describe);
                return ReconcileOutcome.InProgress(describe, TimeSpan.FromSeconds(30));
        }

        // ── Clause 4. The apply above is a claim; this is the reading. ─────────────────────────
        var target = MonitorComponents.ConfigMapRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress($"'{target}' was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!MonitorComponents.Matches(read.GetValueOrThrow().Json, context.Namespace, context.Id, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired connection string",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report(
            "ready",
            $"component '{context.Id.Name}' sends to {MonitorComponents.Endpoint(context.Namespace, context.Id, context.Desired)} "
            + $"as service.namespace={MonitorComponents.ServiceNamespace(context.Id)}",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Converged, not Failed — the reason MonitorCollectorReconciler.DeleteAsync gives.
            return ReconcileOutcome.Converged;
        }

        context.Log.Report("deleting", $"removing the connection string of component '{context.Id.Name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(MonitorComponents.ConfigMapKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(MonitorComponents.ConfigMapJson(context.Namespace, context.Id, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var read = await cluster.GetAsync(MonitorComponents.ConfigMapRef(context.Namespace, context.Id), cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress("the connection string is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        // ⚠ The telemetry stays. It is the workspace's, under the workspace's retention and window;
        // a component is a lens over it, and removing the lens removes nothing it showed.
        context.Log.Report("deleted", $"component '{context.Id.Name}' is gone; its telemetry is the workspace's", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var found = await cluster.GetAsync(MonitorComponents.ConfigMapRef(context.Namespace, context.Id), cancellationToken);

        if (found.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the component's connection string is absent"
            };
        }

        var observed = found.GetValueOrThrow();
        var matches = MonitorComponents.Matches(observed.Json, context.Namespace, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = observed.Json,
            ObservedAt = clock.UtcNow,
            Revision = observed.ResourceVersion,
            Summary = matches ? "the connection string is as declared" : "the connection string has drifted"
        };
    }
}
