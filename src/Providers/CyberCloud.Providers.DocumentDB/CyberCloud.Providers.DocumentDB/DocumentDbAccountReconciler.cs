using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Providers.DocumentDB;

/// <summary>
///     Converges one document-database account onto the four objects it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>FOUR OBJECTS ACROSS FOUR API GROUPS, AND ONE OF THEM IS ANOTHER PROVIDER'S OPERATOR'S
///         CRD.</b> A CloudNativePG <c>Cluster</c>, a FerretDB <c>Deployment</c>, a <c>Service</c> and
///         — when monitoring is on — a <c>PodMonitor</c>. The <c>Cluster</c> expands into instance
///         pods, PVCs, three Services and its own PodMonitor, none of which is applied here: writing
///         any of them would be this provider competing with the controller that owns them.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Every render is a pure function of the name and the body. Nothing
///             here counts, appends or timestamps — in particular there is <b>no configuration digest
///             annotation</b> on the pod template, which is the usual Helm idiom and which would make
///             every apply depend on a hash of a document this provider also writes.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — and a <c>readonly</c>
///             field holding a mutable dictionary is the shape that gets past a structural check,
///             because the field never reassigns. <c>DocumentDbReconcilerTests</c> asserts both
///             halves, and the second one is the only one that catches it.
///         </item>
///         <item>
///             <b>Bounded.</b> Four applies and four reads, on the caller's token. ⚠ There is no wait
///             for the cluster to be <i>ready</i> — CloudNativePG's initdb, the DocumentDB extension's
///             installation and a primary election take minutes and clause 3's budget is thirty
///             seconds — so readiness is reported as <see cref="ReconcileOutcome.InProgress" /> and
///             the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object, never any apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>THE APPLY ORDER IS THE CLUSTER FIRST AND IT IS NOT COSMETIC.</b> The
///         <c>Deployment</c>'s pods mount the <c>Secret</c> CloudNativePG generates from the
///         <c>Cluster</c>. Applying the <c>Deployment</c> first would create pods that sit in
///         <c>ContainerCreating</c> until the operator caught up — recoverable, but it turns the
///         ordinary first pass into a minute of a state that looks identical to the one
///         <c>charts/managed/seaweedfs</c> is permanently stuck in, and an operator reading a
///         dashboard cannot tell "waiting for its own database" from "waiting for a Secret nobody will
///         ever write". The teardown runs the same list <b>reversed</b>, so the pods that mount the
///         Secret go before the cluster that owns it.
///     </para>
///     <para>
///         ⚠ <b>Two of the four kinds are ones a cluster may not serve.</b>
///         <c>postgresql.cnpg.io/v1</c> and <c>monitoring.coreos.com/v1</c> are installed by the
///         platform bundle rather than by Kubernetes, and a cluster without either answers the apply
///         with a <c>404</c>. Until 2026-08-12 an unmapped 4xx escaped as
///         <c>k8s.Autorest.HttpOperationException</c> with no status code and Orleans reported
///         <c>CodecNotFoundException</c>; it comes back naming the API server's own message now.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite the operator, which writes <c>.status</c> on the
///         <c>Cluster</c> — and, the case that matters, would undo a tenant's own HorizontalPodAutoscaler
///         on <c>spec.replicas</c> of the gateway Deployment every pass.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class DocumentDbAccountReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => DocumentDbAccounts.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a document-database account is a "
                + "CloudNativePG Cluster and a FerretDB Deployment in a cluster. "
                + "CyberCloud.DocumentDB/accounts declares RequiresCluster, so the driver should have "
                + "refused this pass — see ReconcileDriver."
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

            if (!DocumentDbAccounts.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the objects of '{name}' read back as desired", 100);

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

        context.Log.Report("deleting", $"deleting the objects of '{name}'");

        // ⚠ Reverse render order — the referrers before the referents. The gateway Deployment mounts
        // a Secret the Cluster owns, so a pod that is still terminating is never a pod whose
        // credential has already vanished.
        foreach (var (kind, json) in Rendered(name, context.Desired).Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — every instance pod, every PVC, every
                // ReplicaSet — while a converge loop with a bounded PASS budget runs out of passes
                // waiting for a controller it does not drive. The read-back below is what makes
                // Background safe: this returns Converged when the objects are GONE, not when the
                // deletes were issued.
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

        context.Log.Report("deleted", $"the objects of '{name}' are gone", 100);
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

        // ⚠ THE CLUSTER, NOT THE DEPLOYMENT, AND THE CHOICE IS ABOUT WHAT AN ACCOUNT *IS*. A FerretDB
        // Deployment with no PostgreSQL behind it is a proxy to nothing — it starts, fails readiness
        // and holds no data. A Cluster with no proxy is a database nobody can reach and every document
        // is still in it. So the account exists exactly when its Cluster does, which is the same
        // reading CyberCloud.Messaging/natsClusters takes of its StatefulSet against its ConfigMap and
        // Services, reached from the other direction: there the workload was the thing, here it is the
        // storage.
        var read = await cluster.GetAsync(
            DocumentDbAccounts.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the PostgreSQL cluster is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = DocumentDbAccounts.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the PostgreSQL cluster carries the desired spec"
                : "the PostgreSQL cluster has drifted"
        };
    }

    /// <summary>The objects a body implies, in apply order, with the document each becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>A method rather than a field, and that is the clause-2 rule rather than a style
    ///     choice.</b> A reconciler is a singleton serving every tenant, so any field is shared state
    ///     — and this one would be tenant-specific, which is the exact shape clause 2 forbids.
    ///     <c>DocumentDbReconcilerTests</c> asserts the declared field count is one, the clock.
    /// </remarks>
    static ImmutableArray<(GroupVersionKind Kind, string Json)> Rendered(string name, JsonElement desired) {
        var rendered = ImmutableArray.CreateBuilder<(GroupVersionKind, string)>(4);

        rendered.Add((DocumentDbAccounts.ClusterKind, DocumentDbAccounts.ClusterJson(name, desired)));
        rendered.Add((DocumentDbAccounts.DeploymentKind, DocumentDbAccounts.DeploymentJson(name, desired)));
        rendered.Add((DocumentDbAccounts.ServiceKind, DocumentDbAccounts.ServiceJson(name)));

        if (DocumentDbAccounts.MonitoringEnabled(desired)) {
            rendered.Add((DocumentDbAccounts.PodMonitorKind, DocumentDbAccounts.PodMonitorJson(name)));
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
    ///     <c>DocumentDbReconcilerTests.EveryRenderedObjectIsAlsoReadBack</c> is what holds the pair
    ///     together, for both settings of <c>monitoring.enabled</c>.
    /// </remarks>
    static IEnumerable<ObjectRef> Targets(string ns, string name, JsonElement desired) {
        yield return DocumentDbAccounts.ClusterRef(ns, name);
        yield return DocumentDbAccounts.DeploymentRef(ns, name);
        yield return DocumentDbAccounts.ServiceRef(ns, name);

        if (DocumentDbAccounts.MonitoringEnabled(desired)) {
            yield return DocumentDbAccounts.PodMonitorRef(ns, name);
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
            // CloudNativePG or PodMonitor CRD the bundle never installed, our own credentials — will
            // be refused identically for the next hour, and ReconcileOutcome.FromFailure is where the
            // four codes that mean that are listed.
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
                    ?? "another field manager owns part of the object and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        return null;
    }
}
