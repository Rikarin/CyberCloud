using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one NAT gateway onto the single Kube-OVN <c>OvnSnatRule</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT NEVER LOOKS UP THE SUBNET OR THE ADDRESS IT NAMES, AND THAT IS THE DESIGN.</b>
///         Both joins are object names derived from this resource's own namespace, its address and
///         its body — <see cref="NatGateways.VpcSubnetOf" /> and <see cref="NatGateways.OvnEipOf" />
///         — and whether the named objects exist is the fabric's question:
///         <c>handleAddOvnSnatRule</c> fails with <i>"failed to get eip"</i> or
///         <i>
///             "failed to get vpc
///             subnet"
///         </i> and retries. A reconciler that read both first would answer the same question
///         one pass earlier, with two more reads on every pass for the life of the resource, and
///         docs/plan/08 § Deleting a parent resource that has children says the platform
///         <i>
///             "must not
///             re-check the parent on every write to a child"
///         </i>. So this reconciler applies and reads
///         back, and the readiness a tenant actually wants is on <c>POST …/showEgress</c>.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES NOTHING THE API ACCEPTED</b>, which its four siblings all do. Their
///         remainder is an address rule the schema cannot state; here both properties are names,
///         <c>ResourceNaming.Pattern</c> is the whole rule, and there is nothing left for a reconciler
///         to say before the fabric has looked.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="NatGateways.OvnSnatRuleJson" /> is a pure function of the
///             namespace, the address and the body.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. Both halves are asserted in <c>NetworkNatGatewayTests</c>, because
///             <c>ReconcilerConformance.CheckNoHiddenState</c> is structurally blind to a
///             <c>readonly</c> field of a mutable collection type.
///         </item>
///         <item><b>Bounded.</b> One apply and one read, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c>, never the apply's own result. ⚠ Converged means the rule reads back as
///             applied — not that the fabric has programmed it, which is <c>status.ready</c> and is
///             what the action reports.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class NatGatewayReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => NatGateways.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a NAT gateway is a Kube-OVN "
                + "OvnSnatRule in a cluster. CyberCloud.Network/virtualNetworks/natGateways declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = NatGateways.ObjectNameOf(context.Namespace, context.Id);

        context.Log.Report(
            "applying",
            $"applying the OvnSnatRule '{name}': subnet "
            + $"'{NatGateways.Subnet(context.Desired)}' out through "
            + $"'{NatGateways.PublicIpAddress(context.Desired)}'",
            20
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            // ⚠ Cluster-scoped: no `InNamespace`. The namespace is inside `name` and inside both
            // names the spec carries.
                .WithKind(NatGateways.OvnSnatRuleKind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(NatGateways.OvnSnatRuleJson(context.Namespace, context.Id, context.Desired))
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
                // ⚠ THE CONTROLLER NEVER WRITES THIS KIND'S SPEC, so a conflict here is a hand edit or
                // an admission policy rather than the fabric — and forcing over either is the thing
                // ADR-013 exists to make impossible.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the OvnSnatRule and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = NatGateways.OvnSnatRuleRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!NatGateways.Matches(read.GetValueOrThrow().Json, context.Namespace, context.Id, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report(
            "ready",
            $"the OvnSnatRule '{name}' reads back as desired. Whether the fabric has programmed it is "
            + $"on POST …/{NatGateways.EgressAction}.",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The address is left alone.</b> Deleting the rule makes the controller remove the NAT
    ///     row, drop the rule's finalizer and re-queue the <c>OvnEip</c> so its <c>status.nat</c>
    ///     clears — the address resource stays allocated and goes back to carrying no traffic, which
    ///     is what a tenant deleting a gateway and not an address asked for.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = NatGateways.ObjectNameOf(context.Namespace, context.Id);

        context.Log.Report("deleting", $"deleting the OvnSnatRule '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .WithKind(NatGateways.OvnSnatRuleKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(NatGateways.OvnSnatRuleJson(context.Namespace, context.Id, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = NatGateways.OvnSnatRuleRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is still readable. The fabric holds the rule until its NAT row is gone.",
                TimeSpan.FromSeconds(5)
            );
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the OvnSnatRule '{name}' is gone and the subnet no longer egresses", 100);
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
            NatGateways.OvnSnatRuleRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the NAT gateway is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = NatGateways.Matches(found.Json, context.Namespace, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the NAT gateway carries the desired spec" : "the NAT gateway has drifted"
        };
    }
}
