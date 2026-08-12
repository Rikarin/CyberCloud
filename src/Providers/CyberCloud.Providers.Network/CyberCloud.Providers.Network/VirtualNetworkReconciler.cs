using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one virtual network onto the single Kube-OVN <c>Vpc</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT REFUSES A BODY THE API ALREADY ACCEPTED, AND THAT IS THE MOST IMPORTANT AND THE
///         LEAST SATISFACTORY THING IN THIS FILE.</b> docs/plan/14 requires that the <i>API</i>
///         validate a tenant's address space against a per-region reserved list and <i>"reject with
///         the conflicting range named"</i>. It cannot: <c>ResourceSchema</c> compares one value
///         against constants, and there is no provider-supplied predicate anywhere on
///         <c>ResourceManagerService</c>'s write path — the whole argument, and the one seam that
///         would close it, is on <see cref="NetworkAddressing" />. So the check runs here, after the
///         caller has been told <c>202</c>, which is the same defect class docs/plan/12's Postgres row
///         shipped. What is done about it:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>The refusal is <c>ReconcileOutcome.Failed</c>, not
///             <see cref="ReconcileOutcome.InProgress" />.</b> A body whose address space overlaps the
///             underlay can never converge, and retrying it every thirty seconds forever would leave
///             the resource in a state that reads as "still working on it". This is the one branch in
///             the family where a terminal failure is the correct answer rather than the lazy one.
///         </item>
///         <item>
///             <b>Nothing is applied first.</b> The check runs before the <c>Vpc</c> is written, so a
///             refused network leaves no object behind — which matters because the object is
///             CLUSTER-SCOPED and a stray one would hold its name against every other subscription.
///         </item>
///         <item>
///             <b>The message names the conflicting range, the reason and the tenant's own value</b>,
///             which is docs/plan/14's own requirement and docs/plan/08 § Errors' standard.
///         </item>
///     </list>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="VirtualNetworks.VpcJson" /> is a pure function of the
///             namespace, the name and the body. Nothing counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A reconciler is a <b>singleton by concrete type</b>, so one
///             instance serves every tenant in the process, and a <c>readonly</c> field holding a
///             mutable dictionary passes <c>ReconcilerConformance.CheckNoHiddenState</c> forever —
///             confirmed five times, in five families. <c>NetworkReconcilerTests</c> asserts both
///             halves, because only the cross-tenant one catches that shape.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>THE OBJECT IT APPLIES IS CLUSTER-SCOPED, AND THE NAMESPACE IT IS HANDED IS USED AS A
///         NAME COMPONENT INSTEAD OF AS A PLACEMENT.</b> See
///         <see cref="VirtualNetworks.ObjectNameOf" />. This is the first reconciler in the tree for
///         which <c>context.Namespace</c> does not reach <c>InNamespace</c>, and reading it as a
///         placement — the thing every other provider correctly does — would put two subscriptions'
///         identically-named networks on one object.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class VirtualNetworkReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualNetworks.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a virtual network is a Kube-OVN "
                + "Vpc in a cluster. CyberCloud.Network/virtualNetworks declares RequiresCluster, so "
                + "the driver should have refused this pass — see ReconcileDriver."
            );
        }

        // ── The check that belongs at the API and runs here — see the remarks on this class. ────
        if (VirtualNetworks.AddressProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = VirtualNetworks.ObjectNameOf(context.Namespace, context.Id.Name);

        context.Log.Report("applying", $"applying the Vpc '{name}'", 20);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            // ⚠ THE OBJECT IS CLUSTER-SCOPED, SO THERE IS NO `InNamespace` CALL HERE. The namespace
            // this reconcile was handed is already inside `name` — VirtualNetworks.ObjectNameOf —
            // which is what keeps two subscriptions' networks apart now that the API server's own
            // namespacing does not.
            .WithKind(VirtualNetworks.VpcKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(VirtualNetworks.VpcJson(context.Namespace, context.Id.Name, context.Desired))
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
                // ⚠ `spec.staticRoutes` is the plausible one here, and the other field manager is the
                // KUBE-OVN CONTROLLER rather than a human: formatVpc fills staticRoutes[].policy and
                // handleDeleteVpcStaticRoute removes entries outright. Forcing would fight the
                // controller every pass, which is why nothing here forces.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the Vpc and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = VirtualNetworks.VpcRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!VirtualNetworks.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the Vpc '{name}' reads back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>THE SUBNETS INSIDE THE NETWORK ARE NOT TOUCHED, AND THIS IS THE ONE PLACE THAT
    ///     DECISION IS VISIBLE AS CODE.</b> docs/plan/08 § Deleting a parent resource that has
    ///     children: <i>"a delete is refused while the resource still has children — 409, not a
    ///     cascade, and not a silent orphan"</i>. That refusal is <b>not implemented</b> — the
    ///     platform cannot enumerate children — so today deleting a network leaves its subnets
    ///     addressable, drawing quota, and bound through <c>spec.vpc</c> to a <c>Vpc</c> that is gone.
    ///     ⚠ On this family that is worse than on
    ///     <c>CyberCloud.Storage/accounts/buckets</c>, and in a specific way worth writing down: a
    ///     Kube-OVN <c>Subnet</c> whose <c>vpc</c> does not resolve is not inert — it is treated as a
    ///     subnet of the <b>default</b> VPC, which is the platform's own. So an orphaned tenant subnet
    ///     does not merely dangle; it lands in the platform's routing domain. Recorded at
    ///     <c>charts/managed/kube-ovn-subnet/conformance.yaml § owed</c>,
    ///     <c>an-orphaned-subnet-joins-the-platforms-vpc</c>.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = VirtualNetworks.ObjectNameOf(context.Namespace, context.Id.Name);

        context.Log.Report("deleting", $"deleting the Vpc '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .WithKind(VirtualNetworks.VpcKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(VirtualNetworks.VpcJson(context.Namespace, context.Id.Name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = VirtualNetworks.VpcRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            // ⚠ A Vpc carries a FINALIZER that formatVpc adds, so it stays readable until the
            // controller has torn down its logical router. Reporting InProgress rather than Converged
            // is what makes the resource's own delete wait for the fabric rather than for the API
            // server's acknowledgement.
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the Vpc '{name}' is gone", 100);
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
            VirtualNetworks.VpcRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the virtual network is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = VirtualNetworks.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the virtual network carries the desired spec"
                : "the virtual network has drifted"
        };
    }
}
