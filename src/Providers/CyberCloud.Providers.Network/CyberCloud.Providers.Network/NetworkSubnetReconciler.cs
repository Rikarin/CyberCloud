using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one subnet onto the single Kube-OVN <c>Subnet</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT NEVER LOOKS ITS NETWORK UP, AND THAT IS THE MOST IMPORTANT LINE IN THIS FILE.</b>
///         docs/plan/12 § Child resources makes the parent a pure function of the address, and
///         docs/plan/08 § Deleting a parent resource that has children says the platform <i>"must not
///         re-check the parent on every write to a child"</i> — the check belongs on the create, in
///         <c>ResourceManagerService.ResolveAsync</c>, where it runs before the enforcement seam and
///         answers the same <c>404</c> as an unauthorized read. The only thing this reconciler takes
///         from the parent is its <i>name</i>, off the address, in
///         <see cref="NetworkSubnets.VpcRefOf" />.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES A BODY THE API ALREADY ACCEPTED, AND HERE IT MATTERS MORE THAN ON THE
///         PARENT.</b> A virtual network's address space is a declaration; <b>this</b> prefix is what
///         the fabric programs, so a subnet overlapping the platform's underlay is the one that
///         actually breaks routing for the node it lands on. Same mechanism, same terminal
///         <c>ReconcileOutcome.Failed</c>, same reason it cannot run at the API — see
///         <see cref="NetworkAddressing" /> and <see cref="VirtualNetworkReconciler" />.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="NetworkSubnets.SubnetJson" /> is a pure function of the
///             namespace, the address and the body.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. Both halves are asserted in
///             <c>NetworkSubnetReconcilerTests</c>, because
///             <c>ReconcilerConformance.CheckNoHiddenState</c> is structurally blind to a
///             <c>readonly</c> field of a mutable collection type — confirmed five times in five
///             families.
///         </item>
///         <item><b>Bounded.</b> One apply and one read, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c>, never the apply's own result. ⚠ On this type that clause earns its
///             keep twice over, because the Kube-OVN controller <i>rewrites the spec it reads back</i>
///             — see <see cref="NetworkSubnets.Matches" />.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Its own <c>Type</c> is a separate class from
///         <see cref="VirtualNetworkReconciler" /> even though the work rhymes, and that is forced
///         rather than chosen.</b> <c>ProviderRegistry</c> stores each type's reconciler by CONCRETE
///         TYPE and <c>ReconcileDriver</c> resolves it from the container by that type, so one class
///         cannot serve two registrations — its <see cref="Type" /> can only name one of them, and
///         <c>ProviderRegistry.Build</c> refuses exactly that.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class NetworkSubnetReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => NetworkSubnets.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a subnet is a Kube-OVN Subnet in "
                + "a cluster. CyberCloud.Network/virtualNetworks/subnets declares RequiresCluster, so "
                + "the driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (NetworkSubnets.AddressProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = NetworkSubnets.ObjectNameOf(context.Namespace, context.Id);

        context.Log.Report(
            "applying",
            $"applying the Subnet '{name}' in network "
            + $"'{context.Id.Parent?.Name ?? context.Id.ParentNames}'",
            20
        );

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            // ⚠ Cluster-scoped: no `InNamespace`. The namespace is inside `name`.
            .WithKind(NetworkSubnets.SubnetKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(NetworkSubnets.SubnetJson(context.Namespace, context.Id, context.Desired))
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
                // ⚠ THE OTHER FIELD MANAGER IS ALMOST CERTAINLY THE KUBE-OVN CONTROLLER, AND FORCING
                // WOULD BE WRONG. formatSubnet writes back `gateway`, `excludeIps`, `protocol`,
                // `provider`, `gatewayType` and `enableLb`, and canonicalizes `cidrBlock`. Those are
                // the controller's fields; this provider does not send them and must not take them.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the Subnet and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = NetworkSubnets.SubnetRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!NetworkSubnets.Matches(
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

        context.Log.Report("ready", $"the Subnet '{name}' reads back as desired", 100);

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

        var name = NetworkSubnets.ObjectNameOf(context.Namespace, context.Id);

        context.Log.Report("deleting", $"deleting the Subnet '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .WithKind(NetworkSubnets.SubnetKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(NetworkSubnets.SubnetJson(context.Namespace, context.Id, context.Desired))
            // ⚠ THE NETWORK'S Vpc IS NOT TOUCHED. Deleting a subnet removes one object; a delete that
            // also tidied up the parent — or that waited for it — would be this type reaching outside
            // its own resource.
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = NetworkSubnets.SubnetRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the Subnet '{name}' is gone", 100);
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
            NetworkSubnets.SubnetRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the subnet is absent" };
        }

        var found = read.GetValueOrThrow();

        var matches = NetworkSubnets.Matches(
            found.Json,
            context.Namespace,
            context.Id,
            context.Desired
        );

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the subnet carries the desired spec" : "the subnet has drifted"
        };
    }
}
