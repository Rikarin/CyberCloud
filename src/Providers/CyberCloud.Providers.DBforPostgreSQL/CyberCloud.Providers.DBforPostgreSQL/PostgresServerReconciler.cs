using CyberCloud.Core.Time;

namespace CyberCloud.Providers.DBforPostgreSQL;

/// <summary>
///     Converges one server onto a CloudNativePG <c>Cluster</c> and, while pooling is on, a
///     <c>Pooler</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both applies are server-side, so a second pass with the same body is
///             an <c>Unchanged</c>. Nothing here counts, appends or timestamps, and the pooler
///             teardown below reads before it deletes so a converged pass with pooling off does no
///             work at all.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ This matters more
///             here than it did for the sample: a reconciler is registered <b>as a singleton, by
///             concrete type</b> (<c>AddCyberCloudProvider</c>'s remarks), so one instance serves
///             every tenant in the process — a field caching, say, the last rendered
///             <c>Cluster</c> would hand tenant B tenant A's spec, and a single-tenant test could not
///             see it.
///         </item>
///         <item>
///             <b>Bounded.</b> At most two applies, two reads and one conditional delete, all on the
///             caller's token. ⚠ There is no wait for the cluster to be <i>ready</i> — a
///             CloudNativePG bootstrap takes minutes and clause 3's budget is thirty seconds, so
///             readiness is reported as <see cref="ReconcileOutcome.InProgress" /> and the reminder
///             comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object the body implies, never an apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Converged here means "the CRs are applied and read back", not "PostgreSQL is
///         accepting connections".</b> The honest stronger check is
///         <c>status.conditions[type=Ready]</c> on the <c>Cluster</c>, and it is not made because
///         nothing in this repository can produce that status: the conformance harness is a
///         dictionary (<c>FakeKubeCluster</c>'s own remarks say so) and no operator runs anywhere the
///         suite reaches. A readiness gate written against a world that never sets the condition would
///         make every resource in every test hang for eight passes and then fail — so the check
///         belongs with <c>ClusterBackedConformanceTests</c>, where a real API server does, and
///         <c>charts/managed/postgres/conformance.yaml</c>'s <c>connect-in-cluster</c> assertion is
///         where it is written down as owed.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller, and on this type that
///         controller is plausibly CloudNativePG itself editing a field it owns.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class PostgresServerReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => PostgresServers.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a PostgreSQL server is a "
                + "CloudNativePG Cluster in a cluster. CyberCloud.DBforPostgreSQL/servers declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var pooling = PostgresServers.PoolingEnabled(context.Desired);

        context.Log.Report(
            "applying",
            $"applying the CloudNativePG Cluster '{name}' to {context.Namespace}",
            30
        );

        var applied = await Apply(
            context,
            cluster,
            PostgresServers.ClusterKind,
            PostgresServers.ClusterJson(name, context.Desired),
            cancellationToken
        );

        if (applied is { } clusterProblem) {
            return clusterProblem;
        }

        if (pooling) {
            context.Log.Report(
                "applying",
                $"applying the PgBouncer Pooler '{PostgresServers.PoolerName(name)}'",
                60
            );

            var pooler = await Apply(
                context,
                cluster,
                PostgresServers.PoolerKind,
                PostgresServers.PoolerJson(name, context.Desired),
                cancellationToken
            );

            if (pooler is { } poolerProblem) {
                return poolerProblem;
            }
        } else if (await RemovePoolerAsync(context, cluster, cancellationToken) is { } removal) {
            return removal;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var target in Targets(context.Namespace, name, pooling)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!PostgresServers.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            pooling
                ? $"the Cluster '{name}' and its Pooler read back as desired"
                : $"the Cluster '{name}' reads back as desired",
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
            // teardown with no cluster to reach has nothing left to remove, and failing would park the
            // resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the CloudNativePG objects of '{name}'");

        // ⚠ The Pooler first. It references the Cluster by name, and removing the referent before the
        // referrer leaves the operator reconciling a Pooler whose cluster is gone — which is noise in
        // the tenant's own event stream for as long as the two deletes are apart.
        foreach (var (kind, json) in new[] {
                     (PostgresServers.PoolerKind, PostgresServers.PoolerJson(name, context.Desired)),
                     (PostgresServers.ClusterKind, PostgresServers.ClusterJson(name, context.Desired))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        // ⚠ Converged once the objects are GONE, read back — not once the deletes were issued. Same
        // clause, other direction: believing a delete is how a resource stops being billed while its
        // pods are still running.
        foreach (var target in Targets(context.Namespace, name, pooling: true)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        // ⚠ AND THE DATA GOES WITH THEM, WHICH IS THIS ROW'S ANSWER TO RetainedVolumesAsync AND IS
        // NOT THE ANSWER THE OTHER TWO DATABASE ROWS GIVE.
        //
        // This reconciler deliberately does NOT implement IResourceReconciler.RetainedVolumesAsync,
        // and taking the default here is a decision rather than the oversight that member's remarks
        // warn about. CloudNativePG does not use a volumeClaimTemplate: it creates each instance's
        // PVCs itself and stamps an OWNER REFERENCE onto every one of them —
        // pkg/reconciler/persistentvolumeclaim/build.go's `.WithClusterInheritance(cluster)`, which
        // reaches utils.SetAsOwnedBy with `Controller: true`. So deleting the Cluster above makes
        // Kubernetes garbage-collect `{name}-{serial}`, its `-wal` claim and every tablespace claim,
        // and there is nothing left for a purge to remove. Naming them here would be a reclaim that
        // reads back "not found" every time.
        //
        // ⚠ THE COST OF THAT IS ON THE OTHER SIDE OF THE WINDOW AND IT IS RECORDED RATHER THAN
        // FIXED HERE. A soft delete runs this same teardown, so a parked server's disks are gone
        // before its recovery window starts and a restore comes back to an initdb —
        // charts/managed/postgres/conformance.yaml § owed,
        // `the-recovery-window-is-hollow-because-cloudnativepg-owns-its-claims`. It is not fixable
        // through the retained-volume seam, which names claims for a purge to REMOVE; this needs
        // claims DETACHED before a delete.
        context.Log.Report("deleted", $"the CloudNativePG objects of '{name}' are gone", 100);
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

        // ⚠ The Cluster alone, and not the Pooler. A server IS its Cluster: the pooler is an optional
        // front end whose absence is a legal configuration, so folding it in would make "the resource
        // exists" depend on a setting rather than on the resource.
        var read = await cluster.GetAsync(
            PostgresServers.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the CloudNativePG Cluster is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = PostgresServers.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the CloudNativePG Cluster carries the desired spec"
                : "the CloudNativePG Cluster has drifted"
        };
    }

    /// <summary>
    ///     Applies one object and turns the two outcomes that are not failures into progress.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when the apply landed, or the outcome to return from the pass.
    /// </returns>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string json,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(json)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, a
            // Cluster CRD the operator never installed, our own credentials — will be refused
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
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>
    ///     Removes the pooler when the body has turned pooling off.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when there is nothing left to remove, or the outcome to return.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Reads before it deletes, so the steady state of a server with no pooler is zero
    ///     writes.</b> An unconditional delete would be correct and would issue one request per
    ///     reminder for the life of the resource, which is clause 1's "changes nothing" read as "does
    ///     nothing observable" rather than as "does nothing".
    ///     <para>
    ///         ⚠ <b>Turning pooling off is a visible change to the connection string, by design.</b>
    ///         <c>charts/managed/postgres/conformance.yaml</c>'s
    ///         <c>pooler-is-the-default-endpoint</c> assertion is what says so: the advertised host is
    ///         the pooler's while pooling is on, so this teardown moves it. That is the honest
    ///         behaviour and it is why docs/plan/12 makes pooling on by default — the change is
    ///         cheaper to never make than to make later.
    ///     </para>
    /// </remarks>
    static async Task<ReconcileOutcome?> RemovePoolerAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var target = PostgresServers.PoolerRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? null
                : ReconcileOutcome.FromFailure(readError);
        }

        context.Log.Report("removing-pooler", $"pooling is off, so '{target}' is being removed", 60);

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(PostgresServers.PoolerKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(PostgresServers.PoolerJson(context.Id.Name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        return deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound
            ? ReconcileOutcome.FromFailure(deleteError)
            : null;
    }

    /// <summary>The objects a body implies, in apply order.</summary>
    static IEnumerable<ObjectRef> Targets(string ns, string name, bool pooling) {
        yield return PostgresServers.ClusterRef(ns, name);

        if (pooling) {
            yield return PostgresServers.PoolerRef(ns, name);
        }
    }
}
