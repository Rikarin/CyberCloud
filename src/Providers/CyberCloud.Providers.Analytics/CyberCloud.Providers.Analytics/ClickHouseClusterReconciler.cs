using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Analytics;

/// <summary>
///     Converges one ClickHouse cluster onto the two custom resources it is: a Keeper quorum and the
///     installation that coordinates through it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>TWO OBJECTS IN TWO DIFFERENT API GROUPS, AND THE ORDER THEY ARE APPLIED IN IS PART OF
///         THE DESIGN.</b> The Keeper goes first, because the installation names the Keeper's
///         <c>Service</c> in <c>spec.configuration.zookeeper.nodes</c> and a ClickHouse server that
///         cannot reach its coordination logs and retries rather than failing. Applying them the other
///         way round would produce a cluster that converges either way and spends the gap writing
///         errors nobody asked for.
///     </para>
///     <para>
///         ⚠ <b>Neither object waits for the other, and that is the same decision stated from the other
///         side.</b> There is no "the Keeper is ready" check between the two applies: readiness of a
///         Raft quorum takes longer than clause 3's budget, the installation is declarative and will
///         connect when the Service appears, and a reconciler that blocked would burn passes doing
///         nothing. Both applies happen every pass; convergence is decided by the read-back below.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both applies are server-side and both documents are pure functions
///             of the name and the body. Nothing here counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — and a <c>readonly</c>
///             field holding a mutable dictionary is the shape that gets past a structural check,
///             because the field never reassigns. <c>ClickHouseReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> Two applies and two reads, on the caller's token. ⚠ There is no wait
///             for the cluster to be <i>usable</i> — a Keeper election plus a ClickHouse start takes
///             minutes and clause 3's budget is thirty seconds, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of <b>both</b> objects, never either apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Both kinds are ones a cluster may not serve, and they are in DIFFERENT groups.</b>
///         <c>clickhouse.altinity.com/v1</c> and <c>clickhouse-keeper.altinity.com/v1</c> are installed
///         by the platform bundle rather than by Kubernetes, and a cluster without them answers the
///         apply with a <c>404</c>. Until 2026-08-12 an unmapped 4xx escaped as
///         <c>k8s.Autorest.HttpOperationException</c> with no status code and Orleans reported
///         <c>CodecNotFoundException</c>; it comes back naming the API server's own message now, so
///         the operator of a cluster missing half the bundle finds out <i>which</i> half.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing would
///         let the platform silently overwrite the operator, and on this CRD there is a second party
///         with a legitimate claim on the same subtree — <c>spec.templating.policy: auto</c> lets a
///         cluster-scoped <c>ClickHouseInstallationTemplate</c> merge into <c>spec.templates</c>, which
///         is exactly where this provider writes.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ClickHouseClusterReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ClickHouseClusters.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a ClickHouse cluster is a pair of "
                + "custom resources in a cluster. CyberCloud.Analytics/clickhouseClusters declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The Keeper first. See the remarks. ────────────────────────────────────────────────
        context.Log.Report(
            "applying-keeper",
            $"applying the ClickHouse Keeper of '{name}' to {context.Namespace}",
            20
        );

        if (await Apply(
                context,
                cluster,
                ClickHouseClusters.KeeperKind,
                ClickHouseClusters.KeeperJson(name, context.Desired),
                cancellationToken
            ) is { } keeperProblem) {
            return keeperProblem;
        }

        context.Log.Report(
            "applying-cluster",
            $"applying the ClickHouse installation of '{name}' to {context.Namespace}",
            50
        );

        if (await Apply(
                context,
                cluster,
                ClickHouseClusters.ClickHouseKind,
                ClickHouseClusters.ClickHouseJson(name, context.Desired),
                cancellationToken
            ) is { } clusterProblem) {
            return clusterProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        //
        // ⚠ BOTH, AND IN THE SAME ORDER. A read-back of the installation alone would report Converged
        // for a cluster whose Keeper apply was silently swallowed — which is a ClickHouse that starts,
        // answers SELECT 1, and cannot create a replicated table.
        foreach (var target in new[] {
                     ClickHouseClusters.KeeperRef(context.Namespace, name),
                     ClickHouseClusters.ClickHouseRef(context.Namespace, name)
                 }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!ClickHouseClusters.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"both objects of '{name}' read back as desired", 100);

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

        context.Log.Report("deleting", $"deleting both objects of '{name}'");

        // ⚠ THE INSTALLATION FIRST, WHICH IS THE REVERSE OF THE APPLY ORDER AND IS THE SAME REASON.
        // Servers that lose their coordination while still running log errors; a Keeper with no
        // servers left is idle. Neither ordering can fail, so this is about what the tenant's logs
        // look like during a teardown rather than about correctness.
        foreach (var (kind, json) in new[] {
                     (ClickHouseClusters.ClickHouseKind, ClickHouseClusters.ClickHouseJson(name, context.Desired)),
                     (ClickHouseClusters.KeeperKind, ClickHouseClusters.KeeperJson(name, context.Desired))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — here that is one StatefulSet per host, their
                // pods and every data PVC — while a converge loop with a bounded PASS budget runs out
                // of passes waiting for a controller it does not drive. The read-back below is what
                // makes Background safe: this returns Converged when the objects are GONE, not when
                // the deletes were issued.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in new[] {
                     ClickHouseClusters.ClickHouseRef(context.Namespace, name),
                     ClickHouseClusters.KeeperRef(context.Namespace, name)
                 }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is still readable",
                    TimeSpan.FromSeconds(5)
                );
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"both objects of '{name}' are gone", 100);
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

        var installation = await cluster.GetAsync(
            ClickHouseClusters.ClickHouseRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (installation.TryGetError(out _)) {
            return new() {
                Exists = false,
                ObservedAt = clock.UtcNow,
                Summary = "the ClickHouse installation is absent"
            };
        }

        var found = installation.GetValueOrThrow();

        // ⚠ THE KEEPER IS OBSERVED TOO, AND ITS ABSENCE IS DRIFT RATHER THAN ABSENCE. A cluster whose
        // Keeper was deleted out from under it still exists, still answers, and has quietly lost the
        // ability to replicate — which is the state drift detection is for.
        var keeper = await cluster.GetAsync(
            ClickHouseClusters.KeeperRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        var matches = ClickHouseClusters.Matches(found.Json, context.Desired)
            && keeper.IsSuccess
            && ClickHouseClusters.Matches(keeper.GetValueOrThrow().Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the ClickHouse cluster and its Keeper carry the desired spec"
                : "the ClickHouse cluster has drifted"
        };
    }

    /// <summary>
    ///     Applies one object, returning the outcome that ends the pass or <see langword="null" /> to
    ///     carry on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shared by both applies rather than written twice, because the three branches below are a
    ///     policy — retryable, refused, owned by somebody else — and two copies of a policy is one
    ///     copy that gets updated.
    /// </remarks>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(objectJson)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, an
            // Altinity CRD the bundle never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four codes
            // that mean that are listed.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        return outcome.Result switch {
            // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather
            // than failing them. A tenant whose cluster is down has a resource that is still coming,
            // not one that broke.
            ApplyResult.Suspended => Suspended(context, outcome),
            ApplyResult.Conflict => Conflicted(context, kind, outcome),
            _ => null
        };
    }

    static ReconcileOutcome Suspended(ReconcileContext context, ApplyOutcome outcome) {
        context.Log.Report("waiting-for-cluster", outcome.Message);

        return ReconcileOutcome.InProgress(
            outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
            TimeSpan.FromSeconds(30)
        );
    }

    static ReconcileOutcome Conflicted(
        ReconcileContext context,
        GroupVersionKind kind,
        ApplyOutcome outcome
    ) {
        var describe = outcome.Drift?.Describe()
            ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten";

        context.Log.Report("conflict", describe);

        return ReconcileOutcome.InProgress(describe, TimeSpan.FromSeconds(30));
    }
}
