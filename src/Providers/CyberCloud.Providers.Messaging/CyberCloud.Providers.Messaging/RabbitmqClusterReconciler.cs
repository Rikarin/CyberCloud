using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Converges one RabbitMQ cluster onto the single <c>RabbitmqCluster</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE object, where the two reconcilers beside it apply two and five.</b> That is what
///         the official operator buys: the <c>StatefulSet</c>, the two <c>Service</c>s, the
///         <c>ConfigMap</c>, the credential <c>Secret</c>, the ServiceAccount and its RBAC are all the
///         operator's, and none of them is this provider's to name. The contrast with
///         <see cref="NatsClusterReconciler" /> — five objects, four of them core kinds, because
///         <c>nats-operator</c> was archived — is the clearest measurement in the tree of what an
///         operator is worth per row: docs/plan/12 costs both rows at 0.8 EM.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side, so a second pass with the same body is an
///             <c>Unchanged</c>. Nothing here counts, appends or timestamps. ⚠ The one thing on this
///             type capable of breaking that is <see cref="RabbitmqClusters.AdditionalConfig" />,
///             which builds a string — and <see cref="RabbitmqClusters.Plugins" /> sorts, so two
///             bodies asking for the same plugins in different orders render the same document
///             rather than fighting each other every pass.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process. A field caching, say,
///             the last rendered <c>additionalConfig</c> would hand tenant B tenant A's default queue
///             type — and a <c>readonly</c> field holding a mutable dictionary is the shape that gets
///             past a structural check, because the field never reassigns.
///             <c>RabbitmqReconcilerTests</c> asserts both halves, because
///             <c>ReconcilerConformance.CheckNoHiddenState</c> cannot see the second.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token. ⚠ There is no wait for
///             the nodes to be <i>ready</i> — a RabbitMQ cluster forms over tens of seconds and
///             clause 3's budget is thirty, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object the body implies, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>There is no apply ORDER to get wrong, and that is worth stating because both
///         reconcilers beside this one have one and both orders are load-bearing.</b> The Kafka one
///         applies a <c>Kafka</c> before its <c>KafkaNodePool</c> because a pool naming an absent
///         cluster is silently ignored; the NATS one applies a <c>ConfigMap</c> and a headless
///         <c>Service</c> before a <c>StatefulSet</c> because a pod cannot start without them. One
///         object has no order, so the failure class simply does not exist here — and the tests that
///         would assert it are absent for that reason rather than forgotten.
///     </para>
///     <para>
///         ⚠ <b>Converged here means "the custom resource is applied and reads back", not "the broker
///         is serving", and on this operator the gap is wider than the phrase suggests.</b> A
///         scale-DOWN is accepted by the API server, refused by the controller, and reported only in
///         <c>status.conditions[ReconcileSuccess]</c> with an <c>UnsupportedOperation</c> event — so
///         a shrink reads back as desired while the <c>StatefulSet</c> keeps its old node count. The
///         honest stronger check is that condition, and it is not made because nothing in this
///         repository can produce it: the Docker-free harness is a dictionary and the cluster-backed
///         harness runs a bare k3s with the definitions installed and <b>no RabbitMQ cluster
///         operator</b> — see <c>ClusterConformanceHarness</c>, which derives them from the case's
///         own objects. A readiness gate written against a world where no controller ever sets the
///         condition would make every resource in every test hang and then fail, so the check is
///         written down as owed in <c>charts/managed/rabbitmq/conformance.yaml</c>.
///     </para>
///     <para>
///         ⚠ <b>A cluster whose API server has no <c>rabbitmq.com/v1beta1</c> answers the apply with
///         a 404, and since 2026-08-12 that comes back naming the API server's own message instead of
///         a <c>CodecNotFoundException</c>.</b> That matters more here than on the other two rows,
///         because this operator's bundle ALSO installs an admission webhook with
///         <c>failurePolicy: Fail</c> and a cert-manager dependency — so a half-installed bundle
///         refuses every create and update with a webhook error rather than a missing-kind one, and
///         the operator of the cluster needs to read which of the two it got.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller. ⚠ On this type the
///         other field manager is most plausibly <b>the operator itself</b>: its mutating webhook
///         writes <c>spec.image</c>, and its controller writes <c>metadata.annotations</c> — so a
///         conflict here is the likeliest of the three rows to be a genuine two-writer situation
///         rather than a tenant's autoscaler.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class RabbitmqClusterReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => RabbitmqClusters.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a RabbitMQ cluster is a "
                + "RabbitmqCluster in a cluster. CyberCloud.Messaging/rabbitmqClusters declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report("applying", $"applying the RabbitmqCluster '{name}' to {context.Namespace}", 40);

        var problem = await Apply(
            context,
            cluster,
            RabbitmqClusters.ClusterJson(name, context.Desired),
            cancellationToken
        );

        if (problem is { } failed) {
            return failed;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = RabbitmqClusters.ClusterRef(context.Namespace, name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!RabbitmqClusters.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the RabbitmqCluster '{name}' reads back as desired", 100);

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

        context.Log.Report("deleting", $"deleting the RabbitmqCluster '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(RabbitmqClusters.ClusterKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(RabbitmqClusters.ClusterJson(name, context.Desired))
            // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
            // collector removing every dependent, and a converge loop with a bounded pass budget
            // would run out of passes waiting for a controller it does not drive. The read-back below
            // is what makes Background safe: this returns Converged when the object is GONE, not when
            // the delete was issued.
            //
            // ⚠ AND THIS OPERATOR ADDS A FINALIZER —
            // `deletion.finalizers.rabbitmqclusters.rabbitmq.com` — so the object survives the delete
            // call until the controller removes it. That is precisely the case the read-back is for,
            // and it is the reason InProgress below is the ordinary first answer rather than a rare
            // one: with no operator installed, the object never goes away at all and the resource
            // stays in Deleting, visibly, instead of the platform claiming a teardown it did not do.
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = RabbitmqClusters.ClusterRef(context.Namespace, name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the RabbitmqCluster '{name}' is gone", 100);
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
            RabbitmqClusters.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the RabbitmqCluster is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = RabbitmqClusters.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the RabbitmqCluster carries the desired spec"
                : "the RabbitmqCluster has drifted"
        };
    }

    /// <summary>
    ///     Applies the object and turns the two outcomes that are not failures into progress.
    /// </summary>
    /// <returns>
    ///     <see langword="null" /> when the apply landed, or the outcome to return from the pass.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A static method rather than an instance one with a cached builder, and that is the
    ///     clause-2 rule rather than a style choice.</b> A reconciler is a singleton serving every
    ///     tenant, so any field is shared state.
    /// </remarks>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        string json,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(RabbitmqClusters.ClusterKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(json)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, the
            // operator's own webhook with no certificate, a CRD the bundle never installed, our own
            // credentials — will be refused identically for the next hour, and
            // ReconcileOutcome.FromFailure is where the four codes that mean that are listed.
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
                    ?? "another field manager owns part of the RabbitmqCluster and it was not "
                    + "overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }
}
