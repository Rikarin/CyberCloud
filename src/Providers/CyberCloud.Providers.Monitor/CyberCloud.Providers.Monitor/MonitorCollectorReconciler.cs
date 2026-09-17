using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Converges one <c>workspaces/collectors</c> resource onto the three objects it is: the
///     collector's configuration, the collector, and the address workloads send to.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT READS NOTHING OF ITS WORKSPACE, AND THAT IS THE WHOLE DESIGN.</b> The obvious
///         shape — read the workspace's row, render the accountID into the configuration — makes
///         every rendered document a function of a cluster read, puts the ingest key through the
///         control plane a second time, and makes a collector created a minute before its workspace
///         fail its pass. The rendered <c>Deployment</c> instead names the row and the key by
///         <c>configMapKeyRef</c> and <c>secretKeyRef</c> and the kubelet does the reading —
///         <see cref="MonitorWorkspaces.WorkspaceEnv" />. So every document here is a pure function
///         of the address and the body, clause 1 holds trivially, and a collector whose workspace has
///         not converged is a pod the kubelet holds by name until it has.
///     </para>
///     <para>
///         ⚠ <b>THE CONFIGURATION FIRST, AND THE ORDER IS ABOUT THE MESSAGE A TENANT GETS</b> —
///         <c>LoadBalancerReconciler</c>'s argument: a <c>Deployment</c> whose <c>ConfigMap</c> does not
///         exist yet reports <c>CreateContainerConfigError</c>, which reads as a broken image. The
///         <c>Service</c> last, so nothing is addressable before there is something to address.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES ONE BODY THE API ACCEPTED.</b> <see cref="MonitorCollectors.ReceiverProblem" />
///         — both receivers off — is a cross-property fact <c>ResourceSchema</c> cannot state, and the
///         refusal is terminal because waiting changes nothing about it.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop: idempotent (three pure renderings,
///         a config hash of a deterministic text); no hidden state (the one field is the primary
///         constructor's clock); bounded (three applies and three reads, on the caller's token);
///         observes, never assumes (<see cref="ReconcileOutcome.Converged" /> follows a
///         <c>GetAsync</c> of all three, and <b>converged does not mean the pod is running</b> — the
///         cluster-backed suite is what asserts a pod starts and accepts an export).
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MonitorCollectorReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorCollectors.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a collector is a Deployment in a "
                + "cluster. CyberCloud.Monitor/workspaces/collectors declares RequiresCluster, so the "
                + "driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (MonitorCollectors.ReceiverProblem(context.Desired) is { } problem) {
            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, $"'{context.Id.Path}': {problem}");
        }

        var name = context.Id.Name;

        context.Log.Report("applying-config", $"applying the configuration of collector '{name}'", 20);

        if (await Apply(context, cluster, MonitorCollectors.ConfigMapKind, MonitorCollectors.ConfigMapJson(context.Id, context.Desired), cancellationToken) is { } configProblem) {
            return configProblem;
        }

        context.Log.Report("applying-collector", $"applying collector '{name}' at {MonitorCollectors.Image}", 50);

        if (await Apply(context, cluster, MonitorCollectors.DeploymentKind, MonitorCollectors.DeploymentJson(context.Id, context.Desired), cancellationToken) is { } deploymentProblem) {
            return deploymentProblem;
        }

        context.Log.Report("applying-service", $"applying the endpoint of collector '{name}'", 75);

        if (await Apply(context, cluster, MonitorCollectors.ServiceKind, MonitorCollectors.ServiceJson(context.Id, context.Desired), cancellationToken) is { } serviceProblem) {
            return serviceProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ─────────────
        foreach (var target in MonitorCollectors.Objects(context.Namespace, context.Id)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress($"'{target}' was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!MonitorCollectors.Matches(read.GetValueOrThrow().Json, context.Id, context.Desired)) {
                return ReconcileOutcome.InProgress($"'{target}' is readable and does not yet carry the desired collector", TimeSpan.FromSeconds(5));
            }
        }

        context.Log.Report(
            "ready",
            $"all three objects of collector '{name}' read back as desired; workloads send to "
            + $"{MonitorCollectors.ServiceHost(context.Namespace, context.Id)}. Whether the pod is running is "
            + "the Deployment's status, not this pass's",
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
            // Converged, not Failed — a teardown with no cluster to reach has nothing left to remove,
            // and failing would park the resource in Deleting for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        context.Log.Report("deleting", $"removing collector '{context.Id.Name}'");

        // The reverse of the apply order: the address goes first, so a workload stops resolving the
        // collector before the pods behind it disappear, rather than getting connection refused from
        // an address that still resolves.
        foreach (var (kind, json) in new[] {
                     (MonitorCollectors.ServiceKind, MonitorCollectors.ServiceJson(context.Id, context.Desired)),
                     (MonitorCollectors.DeploymentKind, MonitorCollectors.DeploymentJson(context.Id, context.Desired)),
                     (MonitorCollectors.ConfigMapKind, MonitorCollectors.ConfigMapJson(context.Id, context.Desired))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // Background: the Deployment's ReplicaSets and pods are the garbage collector's to
                // remove, and the read-back below is what makes Converged mean "gone".
                    .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in MonitorCollectors.Objects(context.Namespace, context.Id).Reverse()) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"collector '{context.Id.Name}' is gone", 100);

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

        // The Deployment is the observation: it is the object that runs, and a collector whose
        // Deployment is gone has stopped collecting whatever its ConfigMap says.
        var deployment = await cluster.GetAsync(MonitorCollectors.DeploymentRef(context.Namespace, context.Id), cancellationToken);

        if (deployment.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the collector's Deployment is absent" };
        }

        var found = deployment.GetValueOrThrow();
        var matches = MonitorCollectors.Matches(found.Json, context.Id, context.Desired);

        // The Service is observed too: a collector whose Service was deleted is running and
        // unreachable, which is drift rather than absence.
        var service = await cluster.GetAsync(MonitorCollectors.ServiceRef(context.Namespace, context.Id), cancellationToken);
        matches = matches && service.IsSuccess && MonitorCollectors.Matches(service.GetValueOrThrow().Json, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the collector and its endpoint are as declared" : "the collector has drifted"
        };
    }

    /// <summary>Applies one object, returning the outcome that ends the pass or <see langword="null" /> to carry on.</summary>
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
                var describe = outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten";

                context.Log.Report("conflict", describe);

                return ReconcileOutcome.InProgress(describe, TimeSpan.FromSeconds(30));
            default:
                return null;
        }
    }
}
