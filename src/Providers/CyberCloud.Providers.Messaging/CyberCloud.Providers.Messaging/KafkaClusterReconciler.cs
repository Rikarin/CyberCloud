using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Converges one Kafka cluster onto a Strimzi <c>Kafka</c> and the <c>KafkaNodePool</c> that
///     gives it nodes.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both applies are server-side, so a second pass with the same body
///             is an <c>Unchanged</c>. Nothing here counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process. A field caching, say,
///             the last rendered listener list would hand tenant B tenant A's allow-list — and a
///             <c>readonly</c> field holding a mutable dictionary is the shape that gets past a
///             structural check, because the field never reassigns. <c>KafkaStatelessnessTests</c>
///             asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> At most two applies and two reads, all on the caller's token. ⚠ There
///             is no wait for the brokers to be <i>ready</i> — a Kafka cluster forms a KRaft quorum
///             over minutes and clause 3's budget is thirty seconds, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object the body implies, never an apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The <c>Kafka</c> is applied before the <c>KafkaNodePool</c>, and the order is
///         load-bearing in a way the PostgreSQL provider's is not.</b> A pool whose
///         <c>strimzi.io/cluster</c> label names a <c>Kafka</c> that does not exist yet is not an
///         error the operator reports — it is a pool the operator ignores, with no event and no
///         status. The teardown reverses it for the same reason the PostgreSQL provider deletes its
///         <c>Pooler</c> first: removing the referent before the referrer leaves the operator
///         reconciling something whose target is gone.
///     </para>
///     <para>
///         ⚠ <b>Converged here means "the CRs are applied and read back", not "the brokers are
///         serving".</b> The honest stronger check is <c>status.conditions[type=Ready]</c> on the
///         <c>Kafka</c>, and it is not made because nothing in this repository can produce that
///         status: the Docker-free harness is a dictionary, and the cluster-backed harness runs a
///         bare k3s with the CRDs installed and <b>no Strimzi cluster operator</b> — see
///         <c>ProviderConformanceCase.RequiredCrds</c>. A readiness gate written against a world
///         where no controller ever sets the condition would make every resource in every test hang
///         and then fail, so the check is written down as owed in
///         <c>charts/managed/kafka/conformance.yaml</c>'s <c>produce-and-consume</c> assertion.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller, and on this type that
///         controller is plausibly an autoscaler writing <c>spec.replicas</c> through the node pool's
///         own <c>scale</c> subresource.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class KafkaClusterReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => KafkaClusters.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a Kafka cluster is a Strimzi "
                + "Kafka in a cluster. CyberCloud.Messaging/kafkaClusters declares RequiresCluster, "
                + "so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report("applying", $"applying the Strimzi Kafka '{name}' to {context.Namespace}", 30);

        var applied = await Apply(
            context,
            cluster,
            KafkaClusters.KafkaKind,
            KafkaClusters.KafkaJson(name, context.Desired),
            KraftAnnotations,
            [],
            cancellationToken
        );

        if (applied is { } kafkaProblem) {
            return kafkaProblem;
        }

        context.Log.Report(
            "applying",
            $"applying the KafkaNodePool '{KafkaClusters.NodePoolName(name)}'",
            60
        );

        var pool = await Apply(
            context,
            cluster,
            KafkaClusters.NodePoolKind,
            KafkaClusters.NodePoolJson(name, context.Desired),
            [],
            [(KafkaClusters.ClusterLabel, name)],
            cancellationToken
        );

        if (pool is { } poolProblem) {
            return poolProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var target in Targets(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.Failed(readError, true);
            }

            if (!KafkaClusters.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            $"the Kafka '{name}' and its node pool read back as desired",
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

        context.Log.Report("deleting", $"deleting the Strimzi objects of '{name}'");

        // ⚠ The node pool first — the referrer before the referent. See the type's remarks.
        foreach (var (kind, json) in new[] {
                     (KafkaClusters.NodePoolKind, KafkaClusters.NodePoolJson(name, context.Desired)),
                     (KafkaClusters.KafkaKind, KafkaClusters.KafkaJson(name, context.Desired))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — every broker pod, every PVC, every Service —
                // and a converge loop with a bounded pass budget would run out of passes waiting for
                // a controller it does not drive. The read-back below is what makes Background safe:
                // this returns Converged when the objects are GONE, not when the deletes were issued.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.Failed(deleteError, true);
            }
        }

        foreach (var target in Targets(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.Failed(read.Error, true);
            }
        }

        context.Log.Report("deleted", $"the Strimzi objects of '{name}' are gone", 100);
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

        // ⚠ The Kafka alone, and not the node pool. A cluster IS its Kafka: the pool is how the
        // operator is told what to run it on, and a pool present without its Kafka is an object the
        // operator ignores rather than a resource that exists.
        var read = await cluster.GetAsync(
            KafkaClusters.KafkaRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the Strimzi Kafka is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = KafkaClusters.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the Strimzi Kafka carries the desired spec"
                : "the Strimzi Kafka has drifted"
        };
    }

    /// <summary>
    ///     The two annotations that put a <c>Kafka</c> into KRaft mode with node-pool topology.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A property rather than a static field, and that is the clause-2 rule rather than a
    ///     style choice.</b> A reconciler is a singleton serving every tenant, so any field is shared
    ///     state; this one would be immutable and harmless, and the check that would allow it is a
    ///     check that has to reason about mutability. <c>KafkaStatelessnessTests</c> asserts the
    ///     declared field count is one — the clock — and an exception for "but this one is fine" is
    ///     how the next field gets in.
    /// </remarks>
    static (string Key, string Value)[] KraftAnnotations => [
        (KafkaClusters.KraftAnnotation, KafkaClusters.Enabled),
        (KafkaClusters.NodePoolsAnnotation, KafkaClusters.Enabled)
    ];

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
        (string Key, string Value)[] annotations,
        (string Key, string Value)[] labels,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            // ⚠ Through the builder rather than written into the rendered document, so that the two
            // operator-facing keys go through the same syntax check ADR-013's seven do — and so that
            // an attempt to spell one of the seven this way is a loud ArgumentException rather than a
            // silent merge.
            .WithAnnotations(annotations)
            .WithLabels(labels)
            .ObjectJson(json)
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
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>The objects a body implies, in apply order.</summary>
    /// <remarks>
    ///     ⚠ Both, unconditionally, and unlike the PostgreSQL provider's <c>Pooler</c> there is no
    ///     body that turns one of them off. With node pools enabled the <c>Kafka</c> carries no
    ///     replica count and no storage of its own, so a cluster without its pool is not a smaller
    ///     cluster — it is one with nowhere to run.
    /// </remarks>
    static IEnumerable<ObjectRef> Targets(string ns, string name) {
        yield return KafkaClusters.KafkaRef(ns, name);
        yield return KafkaClusters.NodePoolRef(ns, name);
    }
}
