using CyberCloud.Core.Time;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Converges one node pool onto the three Cluster API objects it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT NEVER LOOKS ITS CLUSTER UP, AND THAT IS THE MOST IMPORTANT LINE IN THIS FILE.</b>
///         docs/plan/12 § Child resources makes the parent a pure function of the address, and
///         docs/plan/08 § Deleting a parent resource that has children says the platform <i>"must not
///         re-check the parent on every write to a child"</i> — the check belongs on the create, in
///         <c>ResourceManagerService.ResolveAsync</c>, where it runs before the enforcement seam and
///         answers the same 404 as an unauthorized read.
///         <para>
///             ⚠ <b>And on THIS type there is a second reason, which is the opposite of the one
///             <c>StorageBucketReconciler</c> gives.</b> That one cannot wait for its parent because
///             its parent never reports <c>Succeeded</c>. This one <i>could</i> wait — a
///             <c>Cluster</c> does report ready — and must not, because Cluster API is built for the
///             other order: a <c>MachineDeployment</c> applied before its control plane exists is
///             adopted the moment the control plane does, and the first worker joining is a step in
///             docs/plan/09's own provisioning table rather than something that happens afterwards.
///             A pool that waited would add minutes to every cluster create for no benefit.
///         </para>
///     </para>
///     <para>
///         ⚠ <b>THREE OBJECTS, WHICH IS EXACTLY WHAT ITS PARENT RENDERS.</b> The two templates go
///         first and the <c>MachineDeployment</c> — which names both by group, kind and name — goes
///         last, for the reason <c>ManagedClusterReconciler</c> gives about resolvable references.
///     </para>
///     <para>
///         ⚠ <b>UNLIKE ITS PARENT, <c>Converged</c> DOES NOT READ A STATUS, AND THAT IS A DECISION
///         RATHER THAN AN INCONSISTENCY.</b> A cluster's product is an API server and its readiness is
///         a claim about whether the tenant has one. A pool's product is machines, and
///         <c>MachineDeployment.status.readyReplicas</c> reaching the requested count is a claim about
///         whether the <i>workload placed on them</i> can run — which is a question about the cluster
///         inside, not about the request. ⚠ There is also a plainer reason and it is worth writing
///         down: a pool under an autoscaler has no target replica count to compare a status against,
///         because the number the platform asked for and the number that should exist are deliberately
///         different. <c>conformance.yaml § owed</c>, <c>pool-readiness-is-not-observed</c>.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> All three documents are pure functions of the address and the body.
///             ⚠ Including the machine template, which is the one a rolling-upgrade implementation
///             would be tempted to make unique per pass — see
///             <see cref="AgentPools.UpgradeNodeImageAction" /> for where that belongs instead.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A <c>readonly</c> field holding a mutable dictionary passes the
///             structural check forever — <c>AgentPoolReconcilerTests</c> asserts both halves, because
///             only the cross-tenant one catches that shape.
///         </item>
///         <item><b>Bounded.</b> Three applies and three reads, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of all three objects, never any apply's own result.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class AgentPoolReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => AgentPools.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a node pool is three Cluster API "
                + "objects in a management cluster. CyberCloud.ContainerService/managedClusters/"
                + "agentPools declares RequiresCluster, so the driver should have refused this pass — "
                + "see ReconcileDriver."
            );
        }

        var name = AgentPools.ObjectNameOf(context.Id);

        context.Log.Report(
            "applying-machine-template",
            $"applying the machine template of '{context.Id.Name}' in cluster "
            + $"'{AgentPools.ClusterNameOf(context.Id)}'",
            20
        );

        foreach (var (kind, json) in Renders(context)) {
            if (await Apply(context, cluster, kind, json, cancellationToken) is { } problem) {
                return problem;
            }
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        foreach (var target in Targets(context)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!AgentPools.Matches(read.GetValueOrThrow().Json, context.Id, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the three objects of '{name}' read back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = AgentPools.ObjectNameOf(context.Id);

        context.Log.Report("deleting", $"deleting the three objects of '{name}'");

        // ⚠ THE MachineDeployment FIRST, WHICH IS THE REVERSE OF THE APPLY ORDER. Cluster API owns
        // the teardown of the machines it created from it, and removing the templates first leaves
        // the controller unable to describe what it is replacing while it drains.
        //
        // ⚠ THE CLUSTER'S OWN OBJECTS ARE NOT TOUCHED, AND IT WOULD BE EASY TO REACH FOR. Deleting a
        // pool removes its three objects; a delete that also tidied up the Cluster — or waited for it
        // — would be this type reaching outside its own resource. The whole of what a pool owns is
        // named below.
        foreach (var (kind, json) in Renders(context).Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in Targets(context)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"the three objects of '{name}' are gone", 100);
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
            AgentPools.MachineDeploymentRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the node pool is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = AgentPools.Matches(found.Json, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the node pool carries the desired spec" : "the node pool has drifted"
        };
    }

    /// <summary>The three objects, in apply order.</summary>
    /// <remarks>
    ///     ⚠ One list, read by the apply loop, the delete loop (reversed) and nothing else. Two lists
    ///     is how a fourth object gets applied and never deleted.
    /// </remarks>
    static (GroupVersionKind Kind, string Json)[] Renders(ReconcileContext context) => [
        (AgentPools.MachineTemplateKind, AgentPools.MachineTemplateJson(context.Id, context.Desired)),
        (AgentPools.BootstrapKind, AgentPools.BootstrapJson(context.Id)),
        (AgentPools.MachineDeploymentKind, AgentPools.MachineDeploymentJson(context.Id, context.Desired))
    ];

    /// <summary>The three objects to read back, in the same order.</summary>
    static ObjectRef[] Targets(ReconcileContext context) => [
        AgentPools.MachineTemplateRef(context.Namespace, context.Id),
        AgentPools.BootstrapRef(context.Namespace, context.Id),
        AgentPools.MachineDeploymentRef(context.Namespace, context.Id)
    ];

    /// <summary>Applies one object, or ends the pass.</summary>
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
                    outcome.Message.Length > 0 ? outcome.Message : "the management cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ `spec.replicas` is the plausible one here, and it has a legitimate second owner:
                // a cluster-autoscaler writes exactly that field. Forcing would make the platform and
                // the autoscaler fight over the pool's size every pass — which is the reason
                // AgentPools.EffectiveCount reserves the ceiling rather than trying to track it.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        return null;
    }
}
