// ⚠ For `Result<T>` on the retained-volume seam. Safe beside the GlobalUsings ErrorCode alias, which
// is what disambiguates the one name this namespace collides with.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Converges one NATS cluster onto the five Kubernetes objects it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the first reconciler in the platform with no operator on the other side of
///         it.</b> The three before it apply one or two custom resources and a controller turns each
///         into a workload; <c>nats-io/nats-operator</c> was archived on 2025-04-10 and upstream's
///         answer is a Helm chart, so what this applies is the workload — a <c>ConfigMap</c>, a
///         headless <c>Service</c>, a <c>StatefulSet</c>, a client <c>Service</c> and a
///         <c>PodMonitor</c>. See the remarks on <see cref="NatsClusters" />.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Every apply is server-side, so a second pass with the same body is
///             an <c>Unchanged</c>. Nothing here counts, appends or timestamps — and in particular
///             <see cref="NatsClusters.ConfigJson" /> is a pure function of the name and the body,
///             so the <c>ConfigMap</c>'s content is stable across passes. A config digest in an
///             annotation, which is the usual Helm idiom, would have been the one thing on this type
///             capable of breaking that.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process. A field caching, say,
///             the last rendered allow-list would hand tenant B tenant A's — and a <c>readonly</c>
///             field holding a mutable dictionary is the shape that gets past a structural check,
///             because the field never reassigns. <c>NatsReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> At most five applies and five reads, all on the caller's token. ⚠
///             There is no wait for the servers to be <i>ready</i> — a JetStream Raft group forms
///             over tens of seconds and clause 3's budget is thirty, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object the body implies, never an apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The apply order is load-bearing and it is not the same shape as the Kafka one.</b>
///         There, a <c>KafkaNodePool</c> applied before its <c>Kafka</c> is silently ignored by an
///         operator. Here the dependency is at <i>runtime</i>: a pod cannot start without the
///         <c>ConfigMap</c> it mounts, and cannot find its peers without the headless <c>Service</c>
///         whose per-ordinal DNS records the route list names. Kubernetes does not refuse the
///         <c>StatefulSet</c> in either case — it creates pods that crash-loop or that come up as
///         three standalone servers, which is worse than a refusal because it looks like a working
///         cluster. The teardown reverses the order for the same reason the earlier providers reverse
///         theirs.
///     </para>
///     <para>
///         ⚠ <b>The <c>PodMonitor</c> is applied last and is the only conditional object.</b> It is
///         also the only one whose kind a cluster may not serve: <c>monitoring.coreos.com/v1</c> is
///         installed by the platform bundle rather than by Kubernetes, and a cluster without it
///         answers the apply with a <c>404</c>. That is a real failure and it is reported as one
///         rather than swallowed — since 2026-08-12 an unmapped 4xx comes back naming the API
///         server's own message instead of a <c>CodecNotFoundException</c>, so the operator of a
///         cluster missing the bundle finds out what is missing.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller, and on this type that
///         controller is plausibly an autoscaler writing <c>spec.replicas</c> through the
///         <c>StatefulSet</c>'s own <c>scale</c> subresource.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class NatsClusterReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => NatsClusters.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a NATS cluster is a StatefulSet "
                + "in a cluster. CyberCloud.Messaging/natsClusters declares RequiresCluster, so the "
                + "driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var rendered = Rendered(name, context.Desired);
        var applied = 0;

        foreach (var (kind, json) in rendered) {
            context.Log.Report(
                "applying",
                $"applying the {kind.Kind} of '{name}' to {context.Namespace}",
                Percent(applied, rendered.Length)
            );

            var problem = await Apply(context, cluster, kind, json, cancellationToken);
            if (problem is { } failed) {
                return failed;
            }

            applied++;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var target in Targets(context.Namespace, name, context.Desired)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!NatsClusters.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the NATS objects of '{name}' read back as desired", 100);

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

        context.Log.Report("deleting", $"deleting the NATS objects of '{name}'");

        // ⚠ Reverse render order — the referrers before the referents. The StatefulSet goes before
        // the ConfigMap it mounts and the Service its pods resolve peers through, so a pod that is
        // still terminating is never a pod whose configuration has already vanished.
        foreach (var (kind, json) in Rendered(name, context.Desired).Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — every pod, every PVC, every EndpointSlice —
                // and a converge loop with a bounded PASS budget would run out of passes waiting for
                // a controller it does not drive. The read-back below is what makes Background safe:
                // this returns Converged when the objects are GONE, not when the deletes were issued.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in Targets(context.Namespace, name, context.Desired)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        // ⚠ THE FILE STORES SURVIVE THIS, AND UNTIL RetainedVolumesAsync BELOW NOTHING EVER REMOVED
        // THEM. The cascade note above says the collector takes "every pod, every PVC" — it takes the
        // pods, and it does not take the claims: a claim created from a volumeClaimTemplate has no
        // owner reference to the set, which is Kubernetes' own behaviour and is what makes a recovery
        // window possible for the types that declare one. This type declares none, so its claims were
        // simply left, unreferenced and unbilled, after every hard delete.
        context.Log.Report("deleted", $"the NATS objects of '{name}' are gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One claim per server, named from the desired body</b> — see
    ///     <see cref="NatsClusters.RetainedClaims" />, which carries the argument and the caveat about
    ///     a cluster that was scaled down before it was deleted. This runs on the convergence of a
    ///     hard delete and of a purge and on nothing else, so nothing here can reach a resource that
    ///     is coming back.
    /// </remarks>
    public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<RetainedVolume>>.Success(
                NatsClusters.RetainedClaims(context.Namespace, context.Id.Name, context.Desired)
            )
        );

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ The StatefulSet alone, and not the five. A NATS cluster IS its servers: the ConfigMap and
        // the Services are how they are told where to find each other, and either present without the
        // StatefulSet is configuration for a cluster that does not exist. This is the same reading the
        // Kafka provider takes of its Kafka versus its node pool, reached from the other direction.
        var read = await cluster.GetAsync(
            NatsClusters.StatefulSetRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the NATS StatefulSet is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = NatsClusters.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the NATS StatefulSet carries the desired spec"
                : "the NATS StatefulSet has drifted"
        };
    }

    /// <summary>The objects a body implies, in apply order, with the document each becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>A method rather than a field, and that is the clause-2 rule rather than a style
    ///     choice.</b> A reconciler is a singleton serving every tenant, so any field is shared state
    ///     — and this one would be tenant-specific, which is the exact shape clause 2 forbids.
    ///     <c>NatsReconcilerTests</c> asserts the declared field count is one, the clock.
    /// </remarks>
    static ImmutableArray<(GroupVersionKind Kind, string Json)> Rendered(string name, JsonElement desired) {
        var rendered = ImmutableArray.CreateBuilder<(GroupVersionKind, string)>(5);

        rendered.Add((NatsClusters.ConfigMapKind, NatsClusters.ConfigMapJson(name, desired)));
        rendered.Add((NatsClusters.ServiceKind, NatsClusters.HeadlessServiceJson(name)));
        rendered.Add((NatsClusters.StatefulSetKind, NatsClusters.StatefulSetJson(name, desired)));
        rendered.Add((NatsClusters.ServiceKind, NatsClusters.ClientServiceJson(name, desired)));

        if (NatsClusters.MonitoringEnabled(desired)) {
            rendered.Add((NatsClusters.PodMonitorKind, NatsClusters.PodMonitorJson(name)));
        }

        return rendered.ToImmutable();
    }

    /// <summary>The objects a body implies, addressed.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>It must stay in step with <see cref="Rendered" />, and nothing enforces that but a
    ///     test.</b> An object rendered and not read back is an object the loop reports Converged
    ///     without ever having observed — clause 4's whole point — and one read back and never
    ///     rendered is a resource that never converges.
    ///     <c>NatsReconcilerTests.EveryRenderedObjectIsAlsoReadBack</c> is what holds the pair
    ///     together, for both settings of <c>monitoring.enabled</c>.
    /// </remarks>
    static IEnumerable<ObjectRef> Targets(string ns, string name, JsonElement desired) {
        yield return NatsClusters.ConfigMapRef(ns, name);
        yield return NatsClusters.HeadlessServiceRef(ns, name);
        yield return NatsClusters.StatefulSetRef(ns, name);
        yield return NatsClusters.ClientServiceRef(ns, name);

        if (NatsClusters.MonitoringEnabled(desired)) {
            yield return NatsClusters.PodMonitorRef(ns, name);
        }
    }

    /// <summary>Progress for the object at <paramref name="done" /> of <paramref name="total" />.</summary>
    /// <remarks>Capped below 100, which is the reading's to report — clause 4.</remarks>
    static int Percent(int done, int total) => 10 + (done * 80 / total);

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
            // PodMonitor CRD the bundle never installed, our own credentials — will be refused
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
}
