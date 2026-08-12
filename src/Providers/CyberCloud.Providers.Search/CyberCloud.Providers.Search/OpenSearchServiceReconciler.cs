using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Search;

/// <summary>
///     Converges one search service onto the single <c>OpenSearchCluster</c> custom resource it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE OBJECT, AND THREE STATEFULSETS INSIDE IT.</b> The operator expands
///         <c>spec.nodePools</c> into a StatefulSet per component, their Services, the TLS Secrets it
///         generates, the admin-credentials Secret it generates, and a <c>ServiceMonitor</c> when
///         monitoring is on. None of those is applied here, and writing any of them would be this
///         provider competing with the controller that owns them — the rule
///         <c>charts/managed/valkey</c> states.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side and
///             <see cref="OpenSearchServices.ClusterJson" /> is a pure function of the name and the
///             body — including <c>spec.nodePools</c>, which is rendered in a fixed component order so
///             that a body which changed nothing produces bytes that changed nothing. Nothing here
///             counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — and a <c>readonly</c>
///             field holding a mutable dictionary is the shape that gets past a structural check,
///             because the field never reassigns. <c>OpenSearchReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token. ⚠ There is no wait for
///             the cluster to be <i>green</i> — a cluster-manager quorum plus three JVMs plus shard
///             allocation takes minutes and clause 3's budget is thirty seconds, so readiness is
///             reported as <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>UNLIKE <c>CyberCloud.Storage/accounts</c>, THIS SERVICE DOES FINISH WITHOUT PIECE 5,
///         AND THAT IS A FACT ABOUT THE OPERATOR RATHER THAN ABOUT THIS FILE.</b> The OpenSearch
///         operator generates its own admin credential when none is referenced —
///         <c>helpers.EnsureAdminCredentialsSecret</c>, quoted at
///         <see cref="OpenSearchServices" /> — so the cluster comes up authenticated with a password
///         the platform cannot read. That is the <c>CyberCloud.DBforPostgreSQL/servers</c> shape: a
///         working service whose <c>listKeys</c> has nothing behind it, rather than a service that
///         visibly never converges.
///     </para>
///     <para>
///         ⚠ <b>The <c>OpenSearchCluster</c> kind is one a cluster may not serve, and the failure now
///         names itself.</b> <c>opensearch.opster.io/v1</c> is installed by the platform bundle rather
///         than by Kubernetes, and a cluster without it answers the apply with a <c>404</c>. Until
///         2026-08-12 an unmapped 4xx escaped as <c>k8s.Autorest.HttpOperationException</c> with no
///         status code and Orleans reported <c>CodecNotFoundException</c>; it comes back naming the
///         API server's own message now, so the operator of a cluster missing the bundle finds out
///         what is missing.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite the operator, which writes <c>.status</c> on this
///         object — and, the case that matters here, owns <c>spec.confMgmt.smartScaler</c>, which the
///         CRD defaults and requires. Forcing that field back would fight the API server itself on
///         every pass.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class OpenSearchServiceReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => OpenSearchServices.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a search service is an "
                + "OpenSearchCluster custom resource in a cluster. CyberCloud.Search/services declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report(
            "applying",
            $"applying the OpenSearchCluster of '{name}' to {context.Namespace}",
            20
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(OpenSearchServices.ClusterKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(OpenSearchServices.ClusterJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, an
            // OpenSearchCluster CRD the bundle never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four codes
            // that mean that are listed.
            return ReconcileOutcome.FromFailure(applyError);
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
                    ?? "another field manager owns part of the OpenSearchCluster and it was not "
                    + "overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = OpenSearchServices.ClusterRef(context.Namespace, name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!OpenSearchServices.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired node pools",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report(
            "ready",
            $"the OpenSearchCluster of '{name}' reads back as desired",
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
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a
            // teardown with no cluster to reach has nothing left to remove, and failing would park
            // the resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the OpenSearchCluster of '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(OpenSearchServices.ClusterKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(OpenSearchServices.ClusterJson(name, context.Desired))
            // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
            // collector removing every dependent — three StatefulSets, their pods, every node's PVC
            // and the generated TLS Secrets — while a converge loop with a bounded PASS budget runs
            // out of passes waiting for a controller it does not drive. The read-back below is what
            // makes Background safe: this returns Converged when the object is GONE, not when the
            // delete was issued.
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = OpenSearchServices.ClusterRef(context.Namespace, name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the OpenSearchCluster of '{name}' is gone", 100);
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

        var read = await cluster.GetAsync(
            OpenSearchServices.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false,
                ObservedAt = clock.UtcNow,
                Summary = "the OpenSearch cluster is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = OpenSearchServices.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the OpenSearch cluster carries the desired node pools"
                : "the OpenSearch cluster has drifted"
        };
    }
}
