using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Sample;

/// <summary>
///     Converges one widget onto one <c>ConfigMap</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each one is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side, so a second pass with the same body is an
///             <c>Unchanged</c>, and the read-back below finds what the first pass left. Nothing is
///             counted, appended to or timestamped.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory —
///             <c>ReconcilerConformance.CheckNoHiddenState</c> excludes primary-constructor captures
///             by name, and this reconciler is the first real one that exclusion has to be right
///             about.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, both on the caller's token. There is no wait
///             loop here at all: a <c>ConfigMap</c> has no readiness to wait for, so the honest answer
///             is always available inside one pass.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows
///             <c>ctx.Cluster.GetAsync</c> and <c>SampleWidgets.Matches</c>, never the apply's own
///             result. ⚠ That read is why <c>IKubeClusterConnection.GetAsync</c> exists: before this
///             provider the reconciler-facing connection had only <c>ApplyAsync</c> and
///             <c>DeleteAsync</c>, so the only reachable way to report <c>Converged</c> was to believe
///             the apply — the exact violation clause 4 names.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed.</b>
///         ADR-013 (docs/plan/09 § The command builder) makes a conflict <i>"a drift event with a
///         name"</i> rather than an error, and the outcome carries the field and the manager that took
///         it into <c>operation-progress</c>. Retrying without <c>force</c> will not win the field
///         back, so the operation eventually times out at sixty minutes with the conflict as its last
///         progress entry — which is the actionable ending. Forcing would let the platform silently
///         overwrite a tenant's own controller.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class WidgetReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => SampleWidgets.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly — from the
            // conformance harness, for one — and a NullReferenceException there would be attributed to
            // the harness rather than to the wiring.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a widget is a ConfigMap in a "
                + "cluster. CyberCloud.Sample/widgets declares RequiresCluster, so the driver should "
                + "have refused this pass — see ReconcileDriver."
            );
        }

        context.Log.Report("applying", $"applying the ConfigMap '{context.Id.Name}' to {context.Namespace}", 40);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(SampleWidgets.ConfigMapKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(SampleWidgets.ConfigMapJson(context.Id.Name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // Retryable: an apply that failed on our side is a request that can be made again. A body
            // the API server rejects outright comes back as a non-retryable code from the connection.
            return ReconcileOutcome.Failed(applyError, true);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles
                // rather than failing them. A tenant whose cluster is down has a resource that is
                // still coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);
                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);
                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of this ConfigMap and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                break;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"the ConfigMap '{context.Id.Name}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.Failed(readError, true);
        }

        if (!SampleWidgets.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"the ConfigMap '{context.Id.Name}' is readable and does not yet carry the desired data",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the ConfigMap '{context.Id.Name}' reads back as desired", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate. A teardown
            // with no cluster to reach has nothing left to remove, and failing here would leave the
            // resource permanently Deleting and permanently visible — docs/plan/06 § Two-phase create
            // makes that the state a stuck teardown parks in, and parking there for a wiring reason
            // rather than a cluster reason is a support ticket nobody can close.
            return ReconcileOutcome.Converged;
        }

        context.Log.Report("deleting", $"deleting the ConfigMap '{context.Id.Name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(SampleWidgets.ConfigMapKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(SampleWidgets.ConfigMapJson(context.Id.Name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.Failed(deleteError, true);
        }

        // ⚠ Converged once the object is GONE, read back — not once a delete was issued. Same clause,
        // other direction: believing the delete is how a resource stops being billed while its objects
        // are still running.
        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress(
                $"the ConfigMap '{context.Id.Name}' is still readable",
                TimeSpan.FromSeconds(5)
            );
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.Failed(read.Error, true);
        }

        context.Log.Report("deleted", $"the ConfigMap '{context.Id.Name}' is gone", 100);
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

        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the ConfigMap is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = SampleWidgets.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the ConfigMap carries the desired data" : "the ConfigMap has drifted"
        };
    }

    /// <summary>The one object a widget owns.</summary>
    static ObjectRef Target(string ns, string name) =>
        new() { Kind = SampleWidgets.ConfigMapKind, Namespace = ns, Name = name };
}
