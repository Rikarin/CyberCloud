using CyberCloud.Core.Time;

namespace CyberCloud.Providers.DBforMySQL;

/// <summary>
///     Converges one server onto a mariadb-operator <c>MariaDB</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side, so a second pass with the same body is an
///             <c>Unchanged</c>. Nothing here counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b>
///             (<c>AddCyberCloudProvider</c>'s remarks), so one instance serves every tenant in the
///             process — a field caching, say, the last rendered spec would hand tenant B tenant A's
///             database name, and a single-tenant test could not see it.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read on the caller's token. ⚠ There is no wait for
///             the server to be <i>ready</i> — a Galera bootstrap takes minutes and clause 3's budget
///             is thirty seconds, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Converged here means "the CR is applied and reads back", not "MariaDB is accepting
///         connections".</b> The honest stronger check is <c>status.conditions[type=Ready]</c>, and
///         it is not made because nothing in this repository can produce that status without the
///         operator: the Docker-free harness is a dictionary and the cluster-backed one runs a k3s
///         with no mariadb-operator in it. <c>charts/managed/mariadb/conformance.yaml</c>'s
///         <c>connect-with-a-mysql-client</c> assertion is where it is written down as owed.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>. On this type
///         the other manager is very plausibly the operator itself: <c>MariaDB.SetDefaults</c> writes
///         eleven fields into the spec it is handed, including a whole
///         <c>storage.volumeClaimTemplate</c> and a <c>tls</c> block this provider never asked for.
///         Forcing would take those back, once per reminder, forever.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MariaDbServerReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MariaDbServers.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a MariaDB server is a MariaDB "
                + "custom resource in a cluster. CyberCloud.DBforMySQL/servers declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var target = MariaDbServers.ServerRef(context.Namespace, name);

        context.Log.Report(
            "applying",
            $"applying the MariaDB '{name}' to {context.Namespace}",
            40
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(MariaDbServers.ServerKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(MariaDbServers.ServerJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, a
            // MariaDB CRD the operator never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four codes
            // that mean that are listed. KubeFailures.Classify is what turns the API server's own
            // message into one of them, so a missing CRD arrives as a refusal naming the CRD rather
            // than as a serialization failure.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather
                // than failing them. A tenant whose cluster is down has a resource that is still
                // coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the MariaDB and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                break;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!MariaDbServers.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the MariaDB '{name}' reads back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a teardown
            // with no cluster to reach has nothing left to remove, and failing would park the resource
            // in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;
        var target = MariaDbServers.ServerRef(context.Namespace, name);

        context.Log.Report("deleting", $"deleting the MariaDB '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(MariaDbServers.ServerKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(MariaDbServers.ServerJson(name, context.Desired))
            // ⚠ Foreground, matching the Valkey row and not the PostgreSQL one, and the argument is
            // stronger here than on either. A MariaDB expands into a StatefulSet, four Services and
            // ConfigMaps — all of them the CR's OWNED children — and a background cascade returns as
            // soon as the CR is gone, so the read-back below would report "not found" while three
            // database pods were still accepting writes. docs/plan/06 § Two-phase create: "never
            // silently gone while its pods still run and its meter still ticks".
            //
            // ⚠ WHAT THIS DOES NOT DECIDE IS THE VOLUMES. `spec.storage.pvcRetentionPolicy` is not
            // rendered, so what happens to three data PVCs is the StatefulSet's default rather than a
            // choice this platform made. That is recorded in conformance.yaml § owed as
            // `volumes-outlive-the-resource` rather than settled here, because the answer belongs with
            // soft delete — which docs/plan/08 § Soft delete designs and nothing yet implements, and
            // whose own text says the declaration "is the last step, not the first". Picking a
            // retention policy before there is a restore path to read a retained volume back through
            // would be picking it blind.
            .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        // ⚠ Converged once the object is GONE, read back — not once the delete was issued. Same
        // clause, other direction: believing a delete is how a resource stops being billed while its
        // pods are still running.
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the MariaDB '{name}' is gone", 100);

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
            MariaDbServers.ServerRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the MariaDB is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = MariaDbServers.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the MariaDB carries the desired spec"
                : "the MariaDB has drifted"
        };
    }
}
