// ⚠ For `Result<ApplyOutcome>`, which the co-writer answers — the same import, for the same reason,
// NetworkProvider carries: the `ErrorCode` alias in GlobalUsings still wins.

using CyberCloud.Core;
using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one peering onto the two Kube-OVN <c>Vpc</c> objects it is a slice of — the parent
///     network's and the remote's — as a second writer on each.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST RECONCILER IN THE TREE THAT OWNS NO OBJECT.</b> Every sibling applies
///         objects labelled with its own resource id through <c>KubeCommand.For(cluster)</c>. This one
///         applies nothing that way: both objects it touches are owned by <c>virtualNetworks</c>
///         resources, and it reaches them through <see cref="ReconcileContext.CoWriter" /> —
///         docs/plan/09 § A second writer on an object — which reads each object, merges this
///         peering's fragment with the other co-writers', applies under the owner's shared manager
///         with the live <c>resourceVersion</c>, and reads again when the object moved.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES WHAT THE API ACCEPTED, TERMINALLY, AND ONLY ON THE RANGES.</b>
///         <see cref="VirtualNetworkPeerings.AddressProblem" /> is the cross-property rule the schema
///         cannot state — three ranges that must be pairwise disjoint — and a body that fails it can
///         never converge, so the outcome is <c>Failed</c> naming both values rather than an
///         <c>InProgress</c> that reads as "still working on it" for an hour. The same shape as
///         <c>VirtualNetworkReconciler</c>'s reserved-range refusal, and the same defect:
///         <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///         <c>address-space-is-validated-after-202</c>.
///     </para>
///     <para>
///         ⚠ <b>AN ABSENT NETWORK IS <c>InProgress</c>, NOT <c>Failed</c>, AND NEVER A CREATE.</b>
///         The co-writer answers <c>ResourceNotFound</c> when the owner's object is not there — a
///         co-writer that created it would create an unlabelled <c>Vpc</c> under the network's name.
///         Absence is transient for a network that is still <c>Creating</c> and permanent for a name
///         that was mistyped, and this reconciler cannot tell which from the cluster alone; so it
///         waits, says which network, and lets the operation's ladder time it out — the
///         <see cref="NatGateways" /> shape, where a name the fabric cannot resolve is also a resource
///         that never becomes ready.
///     </para>
///     <para>
///         ⚠ <b>ORDER: LOCAL FIRST, THEN REMOTE, AND A FAILURE ON THE SECOND LEAVES THE FIRST.</b>
///         The two applies are not a transaction. A pass that wrote the local fragment and found the
///         remote absent reports <c>InProgress</c> with the local half in place; the next pass
///         re-applies the local half as a no-op and tries the remote again. What the intermediate
///         state costs is a route on the local router to a range nothing answers for yet, which is
///         where the packets were going anyway.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both fragments are pure functions of the namespace, the address and
///             the body, and the co-owned apply reports <c>Unchanged</c> when the stored fragment's
///             hash is this one's.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. <c>NetworkPeeringTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> Two co-owned applies, each at most
///             <see cref="KubeCoWriter.MaxAttempts" /> read-then-apply rounds, and two reads, on the
///             caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows two
///             <c>GetAsync</c> calls and two <see cref="VirtualNetworkPeerings.Matches" /> readings,
///             never the applies' own results. ⚠ Converged means both <c>Vpc</c>s carry the slice —
///             not that OVN built the peer ports, which is <c>status.vpcPeerings</c> and is what
///             <c>POST …/showRoutes</c> reports.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class VirtualNetworkPeeringReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualNetworkPeerings.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a peering is two slices of two "
                + "Kube-OVN Vpc objects in a cluster. CyberCloud.Network/virtualNetworks/peerings "
                + "declares RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (VirtualNetworkPeerings.AddressProblem(context.Id, context.Desired) is { } problem) {
            // ⚠ TERMINAL. Overlapping ranges cannot converge on any pass, and a retry ladder over a
            // body that will never be right hands the tenant an OperationTimeout an hour late in
            // place of the sentence that names the problem.
            context.Log.Report("refused", problem);
            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var local = VirtualNetworkPeerings.LocalVpcRef(context.Namespace, context.Id);
        var remote = VirtualNetworkPeerings.RemoteVpcRef(context.Namespace, context.Desired);

        context.Log.Report(
            "applying",
            $"writing the peering onto '{local.Name}' and '{remote.Name}' over "
            + $"'{VirtualNetworkPeerings.LinkV4(context.Desired)}'",
            20
        );

        var localApplied = await context.CoWriter.ApplyFragmentAsync(
            context.Id,
            local,
            VirtualNetworkPeerings.LocalFragmentJson(context.Namespace, context.Id, context.Desired),
            cancellationToken
        );

        if (Interrupted(context, localApplied, local, "local") is { } localOutcome) {
            return localOutcome;
        }

        var remoteApplied = await context.CoWriter.ApplyFragmentAsync(
            context.Id,
            remote,
            VirtualNetworkPeerings.RemoteFragmentJson(context.Namespace, context.Id, context.Desired),
            cancellationToken
        );

        if (Interrupted(context, remoteApplied, remote, "remote") is { } remoteOutcome) {
            return remoteOutcome;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        foreach (var (target, side) in new[] {
                     (local, VirtualNetworkPeerings.Side.Local), (remote, VirtualNetworkPeerings.Side.Remote)
                 }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was written onto and is not readable back — its network is being "
                        + "deleted underneath this peering",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!VirtualNetworkPeerings.Matches(
                    read.GetValueOrThrow().Json,
                    context.Namespace,
                    context.Id,
                    context.Desired,
                    side
                )) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry this peering's entry and route",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            $"both '{local.Name}' and '{remote.Name}' carry the peering. Whether the fabric has built the "
            + $"peer ports is on POST …/{VirtualNetworkPeerings.RoutesAction}.",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     The outcome to return when a co-owned apply did not land, or <see langword="null" /> when
    ///     it did.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="applied">The co-writer's answer.</param>
    /// <param name="target">The object written onto.</param>
    /// <param name="side">Which network it is, for the message.</param>
    static ReconcileOutcome? Interrupted(
        ReconcileContext context,
        Result<ApplyOutcome> applied,
        ObjectRef target,
        string side
    ) {
        if (applied.TryGetError(out var error)) {
            if (error.Code == ErrorCode.ResourceNotFound) {
                // ⚠ InProgress and not Failed: see the class remarks. The message names the side, so
                // a tenant who mistyped the remote reads which name was not found.
                context.Log.Report("waiting-for-network", error.Message);

                return ReconcileOutcome.InProgress(
                    $"the {side} network's Vpc '{target.Name}' is not in the cluster yet. A peering "
                    + "never creates a network's object; its own reconcile has to converge first.",
                    TimeSpan.FromSeconds(10)
                );
            }

            return ReconcileOutcome.FromFailure(error);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Stale:
                // The co-writer already read and applied again three times; the object is moving
                // faster than that, which on a Vpc is another peering or the controller. Short
                // retry, nothing forced.
                context.Log.Report("stale", outcome.Message);
                return ReconcileOutcome.InProgress(outcome.Message, TimeSpan.FromSeconds(5));

            case ApplyResult.Conflict:
                // ⚠ On a Vpc the likeliest other manager is the Kube-OVN controller's Update — see
                // VirtualNetworkPeerings' remarks on why the route carries its policy explicitly —
                // and forcing over it is the thing ADR-013 exists to make impossible.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {side} Vpc and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Both networks are left standing.</b> A withdrawal applies what the other co-writers
    ///     hold and drops this peering's three annotations; the <c>Vpc</c>s, their labels and their
    ///     owners' specs are untouched. A network that is already gone counts as withdrawn — the
    ///     owner's delete wins — so deleting a peering whose remote was deleted first converges.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var local = VirtualNetworkPeerings.LocalVpcRef(context.Namespace, context.Id);

        // ⚠ The remote is read off the body, which is the one place a changed remoteNetwork bites:
        // the fragment on the OLD remote is not withdrawn by this pass, and DriftScanner names it
        // as a slice left behind (Diverged, not Orphan — the grain exists). VirtualNetworkPeerings'
        // remarks record it as owed.
        var remote = VirtualNetworkPeerings.RemoteVpcRef(context.Namespace, context.Desired);

        context.Log.Report("withdrawing", $"withdrawing the peering from '{local.Name}' and '{remote.Name}'");

        foreach (var (target, side) in new[] { (local, "local"), (remote, "remote") }) {
            var withdrawn = await context.CoWriter.WithdrawFragmentAsync(context.Id, target, cancellationToken);

            if (Interrupted(context, withdrawn, target, side) is { } outcome) {
                return outcome;
            }
        }

        foreach (var target in new[] { local, remote }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                if (readError.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return ReconcileOutcome.FromFailure(readError);
            }

            if (VirtualNetworkPeerings.CarriesFragmentOf(read.GetValueOrThrow().Json, context.Id.Id)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' still carries this peering's fragment after the withdrawal",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "withdrawn",
            $"neither '{local.Name}' nor '{remote.Name}' carries the peering any more",
            100
        );
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Observes the <b>local</b> network's <c>Vpc</c>: the peering exists when that object carries
    ///     its fragment, and the summary says whether the remote's does too. The revision is the local
    ///     object's, because a drift scan joins a co-writer on its fragment and not on a version.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var local = await cluster.GetAsync(
            VirtualNetworkPeerings.LocalVpcRef(context.Namespace, context.Id),
            cancellationToken
        );

        if (local.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the local network's Vpc is absent" };
        }

        var found = local.GetValueOrThrow();

        if (!VirtualNetworkPeerings.CarriesFragmentOf(found.Json, context.Id.Id)) {
            return new() {
                Exists = false,
                Json = found.Json,
                ObservedAt = clock.UtcNow,
                Revision = found.ResourceVersion,
                Summary = "the local network's Vpc carries no fragment of this peering"
            };
        }

        var localMatches = VirtualNetworkPeerings.Matches(
            found.Json,
            context.Namespace,
            context.Id,
            context.Desired,
            VirtualNetworkPeerings.Side.Local
        );

        var remote = await cluster.GetAsync(
            VirtualNetworkPeerings.RemoteVpcRef(context.Namespace, context.Desired),
            cancellationToken
        );

        var remoteMatches = remote.IsSuccess
            && VirtualNetworkPeerings.Matches(
                remote.GetValueOrThrow().Json,
                context.Namespace,
                context.Id,
                context.Desired,
                VirtualNetworkPeerings.Side.Remote
            );

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (localMatches, remoteMatches) switch {
                (true, true) => "both networks carry the peering as desired",
                (true, false) => "the local network carries the peering and the remote does not",
                (false, true) => "the remote network carries the peering and the local has drifted",
                _ => "both networks have drifted from the peering"
            }
        };
    }
}
