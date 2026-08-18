using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one security group onto the single Kube-OVN <c>SecurityGroup</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT REFUSES A BODY THE API ALREADY ACCEPTED — AND FOR ONE RELATION ONLY, WHICH IS THE
///         NARROWEST THIS FAMILY'S RECURRING DEFECT HAS EVER BEEN.</b> The two sibling reconcilers
///         re-check a whole reserved-range table here because <c>ResourceSchema</c> compares one value
///         against constants. This one re-checks exactly <c>min &lt;= max</c> inside a port range:
///         everything else that can be wrong with a port list — a non-number, a <c>0</c>, a
///         <c>65536</c> — is refused by <see cref="PortRange.OptionalListPattern" /> with a
///         <c>400</c> and a JSON Pointer before the write path answers. The seam that would close the
///         remainder is the same one <see cref="NetworkAddressing" /> names, and it is recorded at
///         <c>charts/managed/kube-ovn-security-group/conformance.yaml § owed</c>,
///         <c>a-backwards-port-range-is-refused-after-202</c>.
///     </para>
///     <para>
///         ⚠ <b>THE REFUSAL IS TERMINAL, FOR THE REASON ITS SIBLINGS' ARE.</b> <c>443-80</c> can
///         never converge, and retrying it every thirty seconds forever would leave the resource
///         reading as "still working on it" rather than as "your rule is backwards".
///     </para>
///     <para>
///         ⚠ <b>NOTHING IS APPLIED BEFORE THE CHECK, AND ON THIS TYPE THAT IS A SECURITY PROPERTY
///         RATHER THAN TIDINESS.</b> A partially-rendered security group is a perimeter with some of
///         its rules in it, and a tenant reading <c>Failed</c> would have no reason to believe
///         anything had been programmed at all.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="NetworkSecurityGroups.SecurityGroupJson" /> is a pure
///             function of the namespace, the address and the body — including the <i>order</i> of the
///             rules it expands, which is why <see cref="NetworkSecurityGroups.Rules" /> preserves
///             declaration order and does not collapse duplicates. A renderer that sorted or
///             de-duplicated would still be idempotent; one that used a hash set would not, and that
///             is the shape this clause is guarding against here.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. ⚠ A reconciler is a <b>singleton by concrete type</b>, so one
///             instance serves every tenant in the process, and a <c>readonly</c> field holding a
///             mutable collection passes <c>ReconcilerConformance.CheckNoHiddenState</c> forever —
///             six sightings in six families. <c>NetworkReconcilerTests</c> asserts both halves,
///             because only the cross-tenant one catches that shape.
///         </item>
///         <item><b>Bounded.</b> One apply and one read, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c>, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>IT NEVER LOOKS ITS NETWORK UP, AND HERE THERE IS NOTHING TO LOOK UP.</b> A
///         <c>Subnet</c> at least carries <c>spec.vpc</c>; a <c>SecurityGroup</c> has no field naming
///         a VPC at all. The parent reaches the object only through
///         <see cref="NetworkSecurityGroups.ObjectNameOf" />, which makes the rendered <b>name</b> the
///         single thing keeping two networks' groups called <c>web</c> apart — see that method's
///         remarks for why a collision here would be invisible in the object.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class NetworkSecurityGroupReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => NetworkSecurityGroups.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a security group is a Kube-OVN "
                + "SecurityGroup in a cluster. CyberCloud.Network/virtualNetworks/securityGroups "
                + "declares RequiresCluster, so the driver should have refused this pass — see "
                + "ReconcileDriver."
            );
        }

        if (NetworkSecurityGroups.PortProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = NetworkSecurityGroups.ObjectNameOf(context.Namespace, context.Id);
        var rules = NetworkSecurityGroups.AllRules(context.Desired);

        context.Log.Report(
            "applying",
            $"applying the SecurityGroup '{name}' with {rules.Length} rule(s)",
            20
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            // ⚠ Cluster-scoped: no `InNamespace`. The namespace is inside `name`.
            .WithKind(NetworkSecurityGroups.SecurityGroupKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                NetworkSecurityGroups.SecurityGroupJson(context.Namespace, context.Id, context.Desired)
            )
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
                // ⚠ THE OTHER FIELD MANAGER IS NOT THE KUBE-OVN CONTROLLER HERE, WHICH IS A REAL
                // DIFFERENCE FROM THE OTHER TWO RECONCILERS IN THIS FAMILY. That controller writes
                // only through `patchSgStatus`, a merge patch against the `status` subresource, and
                // never updates a spec. So a conflict on THIS object is a human or a policy engine —
                // and forcing would silently overwrite a rule change somebody made deliberately, on a
                // firewall. Nothing here forces.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the SecurityGroup and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = NetworkSecurityGroups.SecurityGroupRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!NetworkSecurityGroups.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired rules",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"the SecurityGroup '{name}' reads back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>DELETING A SECURITY GROUP OPENS EVERY PORT THAT CARRIED IT, AND NOTHING WARNS ABOUT
    ///     THAT.</b> A port's security groups are named from its own annotation; removing the object
    ///     removes the port group and its default-deny with it, so a workload that was behind this
    ///     perimeter is behind no perimeter rather than behind a closed one. It is the inverse of the
    ///     usual delete hazard — the danger is not an orphan, it is the absence of one.
    ///     docs/plan/08's per-parent child counter does not help, because the dependants are
    ///     <i>ports</i> rather than child resources. Recorded at
    ///     <c>charts/managed/kube-ovn-security-group/conformance.yaml § owed</c>,
    ///     <c>deleting-a-group-removes-the-deny-with-it</c>.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = NetworkSecurityGroups.ObjectNameOf(context.Namespace, context.Id);

        context.Log.Report("deleting", $"deleting the SecurityGroup '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .WithKind(NetworkSecurityGroups.SecurityGroupKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                NetworkSecurityGroups.SecurityGroupJson(context.Namespace, context.Id, context.Desired)
            )
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = NetworkSecurityGroups.SecurityGroupRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the SecurityGroup '{name}' is gone", 100);
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
            NetworkSecurityGroups.SecurityGroupRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the security group is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = NetworkSecurityGroups.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the security group carries the desired rules"
                : "the security group has drifted"
        };
    }
}
