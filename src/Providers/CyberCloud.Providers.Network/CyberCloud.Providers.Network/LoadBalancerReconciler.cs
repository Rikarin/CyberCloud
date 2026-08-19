using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one L4 load balancer onto the two objects it is: an HAProxy configuration and the
///     proxy that reads it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST RECONCILER IN THIS FAMILY THAT APPLIES A WORKLOAD RATHER THAN A ROW IN A
///         DATABASE.</b> Its three siblings each apply one Kube-OVN custom resource and the fabric does
///         the rest. This one applies a <c>ConfigMap</c> and a <c>Deployment</c> into the tenant's own
///         namespace, and the thing that carries traffic is a pod — which is why this type draws
///         <c>QuotaMeter.Vcpu</c> and <c>QuotaMeter.MemoryGb</c> where the rest of the family draws
///         only <c>Resources</c>, and why <c>ENABLE_LB=false</c> is what forced it there. See
///         <see cref="LoadBalancers" />.
///     </para>
///     <para>
///         ⚠ <b>THE ORDER IS THE CONFIGURATION FIRST, AND THAT IS ABOUT THE MESSAGE A TENANT GETS.</b>
///         A <c>Deployment</c> whose <c>ConfigMap</c> does not exist yet schedules a pod that cannot
///         mount it and reports <c>CreateContainerConfigError</c> — which reads as a broken image or a
///         broken volume rather than as an apply that has not happened yet. Both orders converge; only
///         one of them is quiet while it does.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES A BODY THE API ALREADY ACCEPTED, AS ALL THREE OF ITS SIBLINGS DO.</b>
///         <see cref="LoadBalancers.BackendProblem" /> is what the pattern could not say: that each
///         address parses, that there are not too many, and that none of them is the proxy's own
///         frontend. The refusal is terminal (<c>ReconcileOutcome.Failed</c>) rather than
///         <c>InProgress</c>, because none of those can become true by waiting.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both rendered documents are pure functions of the namespace, the
///             address and the body — including the config hash on the pod template, which is a hash of
///             a deterministic rendering. ⚠ A rendering that varied by so much as a number's formatting
///             would roll the proxy once per reminder, forever.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. <c>ReconcilerConformance.CheckNoHiddenState</c> is structurally
///             blind to a <c>readonly</c> field of a mutable collection type — seven sightings in seven
///             families — so <c>NetworkLoadBalancerTests</c> holds the cross-tenant test that covers
///             what a field's type cannot show.
///         </item>
///         <item><b>Bounded.</b> Two applies and two reads, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of <b>both</b> objects, never either apply's own result.
///             ⚠ <b>Converged does not mean the proxy is serving</b> — it means both objects read back
///             as desired. Whether a pod is running is
///             <c>POST …/{LoadBalancers.BackendsAction}</c>'s answer, and the progress log says so
///             rather than letting "ready" imply it.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class LoadBalancerReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => LoadBalancers.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a load balancer is an HAProxy "
                + "Deployment in a cluster. CyberCloud.Network/virtualNetworks/loadBalancers declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (LoadBalancers.BackendProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = LoadBalancers.ObjectNameOf(context.Id);

        context.Log.Report("applying-config", $"applying the HAProxy configuration of '{name}'", 20);

        if (await Apply(
                context,
                cluster,
                LoadBalancers.ConfigMapKind,
                LoadBalancers.ConfigMapJson(context.Id, context.Desired),
                "the proxy configuration",
                cancellationToken
            ) is { } configProblem) {
            return configProblem;
        }

        context.Log.Report("applying-proxy", $"applying the proxy '{name}'", 50);

        if (await Apply(
                context,
                cluster,
                LoadBalancers.DeploymentKind,
                LoadBalancers.DeploymentJson(context.Namespace, context.Id, context.Desired),
                "the proxy",
                cancellationToken
            ) is { } proxyProblem) {
            return proxyProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var target in LoadBalancers.Objects(context.Namespace, context.Id)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!LoadBalancers.Matches(
                    read.GetValueOrThrow().Json,
                    context.Namespace,
                    context.Id,
                    context.Desired
                )) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        // ⚠ "CONFIGURED" RATHER THAN "READY", SAID OUT LOUD IN THE TENANT'S OWN PROGRESS LOG, ON
        // CyberCloud.Terminal/consoles' rule. Both objects reading back is not a proxy that is
        // serving: the pod may be pulling an image, waiting for an address the subnet cannot give it,
        // or crash-looping on a port it cannot bind. What this pass proves is that the cluster holds
        // what the tenant asked for, and the action is what proves the rest.
        context.Log.Report(
            "configured",
            $"the proxy '{name}' and its configuration are in the cluster. Whether it is running, and "
            + $"what it is balancing, is on POST …/{LoadBalancers.BackendsAction}.",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>THE PROXY FIRST AND THE CONFIGURATION SECOND, WHICH IS THE REVERSE OF THE APPLY
    ///     ORDER.</b> A <c>ConfigMap</c> deleted underneath a running pod does not stop the pod —
    ///     HAProxy read the file at start and holds no reference to the object — but it does make the
    ///     pod unschedulable the moment the kubelet next restarts it, which is a load balancer that
    ///     works until it does not, on nobody's schedule. Removing the proxy first means a teardown
    ///     that dies half way leaves nothing serving rather than something serving a configuration the
    ///     platform can no longer see.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed: a teardown with no cluster to reach has nothing left to remove,
            // and failing would park the resource in Deleting — visible, billed and permanent.
            return ReconcileOutcome.Converged;
        }

        var name = LoadBalancers.ObjectNameOf(context.Id);

        context.Log.Report("deleting", $"removing the proxy '{name}' and its configuration");

        var targets = new[] {
            LoadBalancers.DeploymentRef(context.Namespace, context.Id),
            LoadBalancers.ConfigMapRef(context.Namespace, context.Id)
        };

        foreach (var target in targets) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(target.Name))
                // ⚠ Foreground, because a background cascade returns as soon as the Deployment is
                // marked and the pod behind it can still be accepting connections. A load balancer
                // that stops being billed while traffic is still flowing through it is docs/plan/06
                // § Two-phase create's "never silently gone while its pods still run".
                .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in targets) {
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

        context.Log.Report(
            "deleted",
            $"the proxy '{name}' is gone and its frontend address is back in the subnet's pool",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>THE DEPLOYMENT DECIDES <see cref="ObservedState.Exists" /> AND THE CONFIGURATION IS
    ///     FOLDED INTO THE MATCH.</b> A load balancer whose <c>ConfigMap</c> was hand-deleted still has
    ///     a proxy in the cluster serving the last configuration it read, which is a drift the scanner
    ///     must repair rather than a resource that is gone — and reporting <c>Exists = false</c> would
    ///     make the manager treat it as absent.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var proxy = await cluster.GetAsync(
            LoadBalancers.DeploymentRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (proxy.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the proxy is absent"
            };
        }

        var found = proxy.GetValueOrThrow();

        var config = await cluster.GetAsync(
            LoadBalancers.ConfigMapRef(context.Namespace, context.Id),
            cancellationToken
        );

        var matches =
            LoadBalancers.Matches(found.Json, context.Namespace, context.Id, context.Desired)
            && config.IsSuccess
            && LoadBalancers.Matches(
                config.GetValueOrThrow().Json,
                context.Namespace,
                context.Id,
                context.Desired
            );

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the proxy carries the desired configuration"
                : "the proxy has drifted"
        };
    }

    /// <summary>
    ///     Applies one object, returning the outcome that ends the pass or <see langword="null" /> to
    ///     carry on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shared by both applies rather than written twice, on <c>CloudConsoleReconciler</c>'s rule:
    ///     the branches below are a policy, and two copies of a policy is one copy that gets forgotten.
    /// </remarks>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        string what,
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
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ REPORTED AND RETRIED, NEVER FORCED, and on this type the plausible rival is a
                // mutating admission policy rather than a controller: a cluster running Kyverno or a
                // sidecar injector edits pod templates, which is exactly the field this provider owns.
                // Forcing would take the field back once per reminder and roll the proxy each time.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of {what} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>The smallest object a delete needs: a name.</summary>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();
}
