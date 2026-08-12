using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Cache;

/// <summary>
///     Converges one cache onto a spotahome <c>RedisFailover</c>.
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
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — a field caching, say, the
///             last rendered spec would hand tenant B tenant A's eviction policy, and a single-tenant
///             test could not see it. <c>ValkeyReconcilerTests</c> holds both checks: the structural
///             one, and the cross-tenant one that catches what the structural one cannot.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token. ⚠ There is no wait for
///             the cache to be <i>ready</i>: the operator has a StatefulSet, three Sentinels and a
///             failover election to run, and clause 3's budget is thirty seconds, so readiness is
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Converged here means "the CR is applied and reads back", not "Valkey is answering
///         PING".</b> The honest stronger check is the operator's own readiness, and it is not made
///         because nothing in this repository can produce it: the Docker-free harness is a dictionary
///         and no operator runs anywhere either suite reaches. A readiness gate written against a world
///         that never sets the condition would make every resource in every test hang and then fail.
///         <c>charts/managed/valkey/conformance.yaml</c>'s <c>connect-through-sentinel</c> assertion is
///         where that is written down as owed.
///     </para>
///     <para>
///         ⚠ <b>One object, where the first provider had two, and the difference is worth stating.</b>
///         A <c>RedisFailover</c> expands into a StatefulSet, a Deployment, three Services and two
///         ConfigMaps — all of them the operator's, none of them this provider's. The reconciler
///         applies the one object it owns and reads back the one object it owns; the rest is the
///         operator's business and is exactly what <see cref="ReconcileOutcome.Converged" /> is
///         careful not to claim.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced</b>, and on this type the rival is plausibly the operator itself: spotahome's
///         <c>Validate()</c> fills in images, ports and exporter images and prepends its own
///         <c>replica-priority 100</c> to <c>spec.redis.customConfig</c>. ADR-013 makes a conflict "a
///         drift event with a name"; forcing would take a list field back from the controller that
///         maintains it, once per reminder, forever.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ValkeyCacheReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ValkeyCaches.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a Valkey cache is a RedisFailover "
                + "in a cluster. CyberCloud.Cache/redis declares RequiresCluster, so the driver should "
                + "have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var target = ValkeyCaches.FailoverRef(context.Namespace, name);

        context.Log.Report(
            "applying",
            $"applying the RedisFailover '{name}' to {context.Namespace}",
            40
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(ValkeyCaches.FailoverKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(ValkeyCaches.RedisFailoverJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site — and this provider is where the reason was
            // written down. The comment here used to report that the CyberCloud.Kubernetes gap was
            // open: every 4xx other than a 409 escaped KubeApiClient.ApplyAsync as a raw
            // k8s.Autorest.HttpOperationException, which Orleans could not serialize, so a missing
            // RedisFailover CRD reached the operation as "CodecNotFoundException" with the status and
            // the API server's message nowhere in it. Found by running this provider's own
            // .Cluster.Conformance suite against a k3s with no such CRD. KubeFailures.Classify closed
            // it, so there is now a code to read, and ReconcileOutcome.FromFailure is what reads it:
            // the four refusals end the operation on this pass and everything else still comes back.
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
                    ?? "another field manager owns part of the RedisFailover and it was not overwritten",
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

        if (!ValkeyCaches.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the RedisFailover '{name}' reads back as desired", 100);

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
        var target = ValkeyCaches.FailoverRef(context.Namespace, name);

        context.Log.Report("deleting", $"deleting the RedisFailover '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(ValkeyCaches.FailoverKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(ValkeyCaches.RedisFailoverJson(name, context.Desired))
            // ⚠ Foreground rather than Background, and this is the one place this provider differs from
            // the first on a platform default. The operator's StatefulSet, Deployment, Services and
            // ConfigMaps are the RedisFailover's OWNED children, and `keepAfterDeletion` on the volume
            // claim means the claim outlives them by design. A background cascade returns as soon as
            // the CR is gone, so the teardown below would read "not found" while the pods were still
            // running — and a resource that stops being billed while it is still serving traffic is the
            // failure the read-back exists to prevent.
            .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        // ⚠ Converged once the object is GONE, read back — not once the delete was issued.
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the RedisFailover '{name}' is gone", 100);

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
            ValkeyCaches.FailoverRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the RedisFailover is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = ValkeyCaches.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the RedisFailover carries the desired spec"
                : "the RedisFailover has drifted"
        };
    }
}
