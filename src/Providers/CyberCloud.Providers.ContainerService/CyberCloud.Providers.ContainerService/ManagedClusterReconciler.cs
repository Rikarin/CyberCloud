using CyberCloud.Core.Time;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Converges one managed Kubernetes cluster onto the three Cluster API objects it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THREE OBJECTS IN THREE API GROUPS AND THE ORDER IS PART OF THE DESIGN.</b> The
///         infrastructure and the control plane go first; the <c>Cluster</c> — which names both by
///         group, kind and name — goes last. Cluster API resolves those references on every pass and
///         reports an unresolvable one as a condition, so applying the <c>Cluster</c> first would
///         produce a cluster that converges either way and spends the gap writing errors nobody asked
///         for. That is the same argument <c>ClickHouseClusterReconciler</c> makes for applying a
///         Keeper before the installation that points at it, one level up.
///     </para>
///     <para>
///         ⚠ <b>THIS IS THE ONLY RECONCILER IN THE TREE WHOSE <c>Converged</c> READS A <c>status</c>,
///         AND THE REASON IS THAT THE PRODUCT IS NOT THE OBJECTS.</b> Nine families decide convergence
///         from a spec read-back, which is right for them: what they applied <i>is</i> what the tenant
///         bought, modulo an operator that will get there. What this applies is a <b>request for a
///         cluster</b>. A <c>Cluster</c> whose spec reads back perfectly can be a tenant with no API
///         server, no nodes and no kubeconfig — docs/plan/09 § Kubernetes in Kubernetes budgets six to
///         nine minutes between those two states — so the read-back below is followed by
///         <see cref="ManagedClusters.Readiness" />, and the reason a control plane is not ready is
///         reported through <see cref="IReconcileLog" /> as docs/plan/08 intends: <i>"those entries
///         stream to operation-progress … which is what turns a four-minute cluster creation from a
///         spinner into a story."</i>
///     </para>
///     <para>
///         ⚠ <b>AND THE THIRD ANSWER — a status nobody has written — CONVERGES, WHICH IS A HOLE.</b>
///         <see cref="ManagedClusters.Readiness" /> carries the argument and
///         <c>charts/managed/kubernetes/conformance.yaml § owed</c> carries the item,
///         <c>converged-is-not-ready</c>. The short form: the platform cannot distinguish "Cluster API
///         has not looked yet" from "Cluster API is not running", both conformance harnesses produce
///         the second forever, and the case where the CRDs are absent entirely is caught one layer
///         earlier by the apply.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> All three applies are server-side and all three documents are pure
///             functions of the name and the body. Nothing here counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A reconciler is a <b>singleton by concrete type</b>, so one
///             instance serves every tenant in the process, and a <c>readonly</c> field holding a
///             mutable dictionary passes the structural check forever —
///             <c>ManagedClusterReconcilerTests</c> asserts both halves, because only the cross-tenant
///             one catches that shape. Five provider families have now confirmed that.
///         </item>
///         <item>
///             <b>Bounded.</b> Three applies and three reads, on the caller's token. ⚠ There is no
///             wait for the cluster to be <i>usable</i>: that is minutes and clause 3's budget is
///             thirty seconds, so readiness is reported as <see cref="ReconcileOutcome.InProgress" />
///             and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of <b>all three</b> objects, never any apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>, and on these
///         objects there is a second party with a legitimate claim: the Kamaji control-plane provider
///         <i>patches</i> <c>spec.controlPlaneEndpoint</c> onto the <c>KubevirtCluster</c> this
///         reconciler applies. Forcing would take that field back every pass and hand it to nobody.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ManagedClusterReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ManagedClusters.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a managed Kubernetes cluster is "
                + "three Cluster API objects in a management cluster. "
                + "CyberCloud.ContainerService/managedClusters declares RequiresCluster, so the driver "
                + "should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report("applying-infrastructure", $"applying the KubevirtCluster of '{name}'", 10);

        if (await Apply(
                context,
                cluster,
                ManagedClusters.InfrastructureKind,
                ManagedClusters.InfrastructureJson(name),
                cancellationToken
            ) is { } infrastructureProblem) {
            return infrastructureProblem;
        }

        context.Log.Report("applying-control-plane", $"applying the control plane of '{name}'", 25);

        if (await Apply(
                context,
                cluster,
                ManagedClusters.ControlPlaneKind,
                ManagedClusters.ControlPlaneJson(name, context.Desired),
                cancellationToken
            ) is { } controlPlaneProblem) {
            return controlPlaneProblem;
        }

        context.Log.Report("applying-cluster", $"applying the Cluster of '{name}'", 40);

        if (await Apply(
                context,
                cluster,
                ManagedClusters.ClusterKind,
                ManagedClusters.ClusterJson(name, context.Desired),
                cancellationToken
            ) is { } clusterProblem) {
            return clusterProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        //
        // ⚠ ALL THREE, AND THE INFRASTRUCTURE OBJECT IS THE ONE MOST LIKELY TO BE MISSED. It carries
        // no tenant-facing spec at all, so a reconciler that read back only the two objects with
        // fields in them would report Converged for a cluster whose infrastructureRef points at
        // nothing — which Cluster API accepts, stores, and never provisions.
        foreach (var target in new[] {
                     ManagedClusters.InfrastructureRef(context.Namespace, name),
                     ManagedClusters.ControlPlaneRef(context.Namespace, name),
                     ManagedClusters.ClusterRef(context.Namespace, name)
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

            if (!ManagedClusters.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        // ── The half no other provider has. See this class's remarks. ─────────────────────────
        var clusterObject = await cluster.GetAsync(
            ManagedClusters.ClusterRef(context.Namespace, name),
            cancellationToken
        );

        if (clusterObject.TryGetError(out var statusError)) {
            return ReconcileOutcome.FromFailure(statusError);
        }

        var clusterJson = clusterObject.GetValueOrThrow().Json;
        var readiness = ManagedClusters.Readiness(clusterJson);

        // ── Making the product reachable — the seam is IClusterConnectionRegistrar ────────────────
        //
        // ⚠ REPORTED, NOT ATTACHED, AND THIS RECONCILER MAY NOT DO MORE THAN REPORT. A provider
        // references CyberCloud.Kubernetes.Contracts only (docs/plan/03 § The .Contracts split), so
        // IClusterConnectionGrain.AttachAsync is not reachable from here — and module-layering.txt
        // records that refusal as a decision rather than an obstacle: "attaching a connection is the
        // resource manager's job rather than a reconciler's". ReconcileDriver performs the write,
        // after this pass, and only if it returns Converged.
        //
        // ⚠ ONLY WHEN Cluster API SAYS Ready, WHICH IS NARROWER THAN WHEN THIS METHOD CONVERGES. The
        // NotReported branch below also converges — the harnesses have no Cluster API controller
        // behind the CRDs, and conformance.yaml § owed, `converged-is-not-ready` is that hole — but a
        // connection registered against a control plane no controller has confirmed is a connection
        // every later placement fails against, for the six to eight minutes docs/plan/09 § Kubernetes
        // in Kubernetes budgets before an API server exists. So the two conditions differ on purpose:
        // converging early is a hole this type already owns, and attaching early would hand the
        // consequences to the NEXT resource a tenant creates.
        //
        // ⚠ AND ONLY WITH AN ENDPOINT. A descriptor with no address is a connection whose first call
        // fails on a URL nobody wrote, so a Ready cluster with no controlPlaneEndpoint is reported as
        // in progress rather than registered — it is a state Cluster API passes through, and the next
        // pass finds the field.
        if (readiness.Kind == ClusterReadinessKind.Ready) {
            var endpoint = ManagedClusters.ApiServerEndpoint(clusterJson);

            if (string.IsNullOrEmpty(endpoint)) {
                return ReconcileOutcome.InProgress(
                    $"'{name}' reports its control plane ready and has no spec.controlPlaneEndpoint "
                    + "yet, so there is no address to register a connection against",
                    TimeSpan.FromSeconds(15)
                );
            }

            // ⚠ ClusterId and OwningTenantId are deliberately left unset: ReconcileDriver stamps
            // both from the resource and its operation, so a provider cannot register a cluster
            // under a tenant that does not own it.
            context.ClusterConnections.Produced(
                new() {
                    Kind = ClusterConnectionKind.InHouse,
                    CredentialRef = ManagedClusters.KubeconfigCredentialRef(context.Namespace, name),
                    Endpoint = endpoint,
                    DisplayName = name
                }
            );
        }

        if (readiness.Kind == ClusterReadinessKind.NotReady) {
            // ⚠ THE STEP LIST docs/plan/09 ASKS FOR, AND THE WORDS ARE UPSTREAM'S. That document's
            // table names image pull, DHCP and cloud-init as the flakiest step by far, and none of
            // those is a phrase this platform could have invented — so the detail reported is Cluster
            // API's own condition message wherever there is one.
            context.Log.Report("waiting-for-cluster-api", readiness.Detail, 70);

            return ReconcileOutcome.InProgress(readiness.Detail, TimeSpan.FromSeconds(30));
        }

        context.Log.Report(
            "ready",
            readiness.Kind == ClusterReadinessKind.Ready
                ? $"'{name}' reports its control plane ready"
                // ⚠ SAID OUT LOUD IN THE TENANT'S OWN PROGRESS LOG, because this is the branch that
                // converges without evidence and a tenant reading "ready" for a cluster that is not
                // one deserves to see which claim was made.
                : $"the three objects of '{name}' read back as desired; no Cluster API controller has "
                + "reported on them yet",
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

        context.Log.Report("deleting", $"deleting the three objects of '{name}'");

        // ⚠ THE Cluster FIRST, WHICH IS THE REVERSE OF THE APPLY ORDER AND IS NOT COSMETIC. Cluster
        // API owns the teardown of everything it created from a Cluster — machines, the control
        // plane's own child objects, the kubeconfig Secret — and deleting the pieces out from under
        // it leaves the controller reconciling references to objects that are gone.
        //
        // ⚠ AND THIS DELETE DESTROYS EVERY WORKLOAD IN THE TENANT'S CLUSTER, WHICH IS THE LARGEST
        // BLAST RADIUS OF ANY DELETE IN THE CATALOGUE. docs/plan/08 § Deleting a parent resource that
        // has children would refuse a cluster with live agent pools — 409, not a cascade — and that
        // check is NOT implemented, so today a cluster delete leaves its pools addressable and their
        // MachineDeployments naming a Cluster that is gone. charts/managed/kubernetes/conformance.yaml
        // § owed, `parent-delete-orphans-pools`.
        foreach (var (kind, json) in new[] {
                     (ManagedClusters.ClusterKind, ManagedClusters.ClusterJson(name, context.Desired)),
                     (ManagedClusters.ControlPlaneKind,
                         ManagedClusters.ControlPlaneJson(name, context.Desired)),
                     (ManagedClusters.InfrastructureKind, ManagedClusters.InfrastructureJson(name))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — here that is every Machine, every VM and every
                // DataVolume in the cluster — while a converge loop with a bounded PASS budget runs
                // out of passes waiting for a controller it does not drive. The read-back below is
                // what makes Background safe: this returns Converged when the objects are GONE, not
                // when the deletes were issued.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in new[] {
                     ManagedClusters.ClusterRef(context.Namespace, name),
                     ManagedClusters.ControlPlaneRef(context.Namespace, name),
                     ManagedClusters.InfrastructureRef(context.Namespace, name)
                 }) {
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

        var found = await cluster.GetAsync(
            ManagedClusters.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (found.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the Cluster is absent"
            };
        }

        var read = found.GetValueOrThrow();

        // ⚠ THE OTHER TWO ARE OBSERVED TOO, AND THEIR ABSENCE IS DRIFT RATHER THAN ABSENCE. A Cluster
        // whose control plane was deleted out from under it still exists and still answers; what it
        // has lost is the API server the tenant was sold, which is exactly the state drift detection
        // is for.
        var others = await Task.WhenAll(
            cluster.GetAsync(
                ManagedClusters.ControlPlaneRef(context.Namespace, context.Id.Name),
                cancellationToken
            ),
            cluster.GetAsync(
                ManagedClusters.InfrastructureRef(context.Namespace, context.Id.Name),
                cancellationToken
            )
        );

        var matches = ManagedClusters.Matches(read.Json, context.Desired)
            && others.All(x => x.IsSuccess && ManagedClusters.Matches(x.GetValueOrThrow().Json, context.Desired));

        var readiness = ManagedClusters.Readiness(read.Json);

        return new() {
            Exists = true,
            Json = read.Json,
            ObservedAt = clock.UtcNow,
            Revision = read.ResourceVersion,
            // ⚠ THE SUMMARY SAYS BOTH THINGS, because on this type they are genuinely different
            // questions and a tenant reading one answer would infer the other. "Carries the desired
            // spec" is about the request; "the control plane is ready" is about the product.
            Summary = matches
                ? "the cluster's objects carry the desired spec; " + readiness.Detail
                : "the cluster has drifted"
        };
    }

    /// <summary>
    ///     Applies one object, returning the outcome that ends the pass or <see langword="null" /> to
    ///     carry on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shared by all three applies rather than written three times, because the branches below
    ///     are a policy — retryable, refused, owned by somebody else — and three copies of a policy is
    ///     two copies that get forgotten.
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
            // ⚠ The code decides, not this call site. An apply that could not reach the management
            // cluster is a request that can be made again; one the API server refused — an admission
            // policy, a Cluster API CRD the bundle never installed, our own credentials — will be
            // refused identically for the next hour. ⚠ ON THIS TYPE THAT SECOND BRANCH IS THE ONE
            // THAT KEEPS ManagedClusters.Readiness' HOLE SMALL: a management cluster with no Cluster
            // API in it fails HERE, by name, rather than converging with no status.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        return outcome.Result switch {
            // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather
            // than failing them. A tenant whose management cluster is down has a resource that is
            // still coming, not one that broke.
            ApplyResult.Suspended => Suspended(context, outcome),
            ApplyResult.Conflict => Conflicted(context, kind, outcome),
            _ => null
        };
    }

    static ReconcileOutcome Suspended(ReconcileContext context, ApplyOutcome outcome) {
        context.Log.Report("waiting-for-cluster", outcome.Message);

        return ReconcileOutcome.InProgress(
            outcome.Message.Length > 0 ? outcome.Message : "the management cluster is unreachable",
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
