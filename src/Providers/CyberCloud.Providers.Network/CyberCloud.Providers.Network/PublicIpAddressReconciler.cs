using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one public address onto the single Kube-OVN <c>OvnEip</c> it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT APPLIES LESS THAN ANY OTHER RECONCILER IN THE FAMILY, AND THAT IS THE DESIGN
///         RATHER THAN AN UNFINISHED TYPE.</b> The rendered <c>spec</c> is one constant — <c>type:
///         nat</c> — plus a requested address when the body named one. Everything else about an
///         <c>OvnEip</c> is the fabric's: the pool comes from the operator's
///         <c>--external-gateway-switch</c>, the address and the MAC come from IPAM, and all three are
///         written back to <c>.spec</c> by the controller. A reconciler that sent them would be
///         claiming field-manager ownership of values it does not choose, and the symptom is a
///         permanent <c>ApplyResult.Conflict</c> — see <see cref="PublicIpAddresses.OvnEipJson" />.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES A BODY THE API ALREADY ACCEPTED, AS ITS TWO SIBLINGS DO, AND THE REMAINDER
///         IS MUCH SMALLER HERE.</b> <see cref="PublicIpAddresses.AddressProblem" /> is two facts
///         about one string — the family, and the fabric's refusal of an upper-case IPv6 address —
///         because <c>IpAddresses.OptionalV4Pattern</c> already refused everything else at the API,
///         with a pointer, before the write path answered. The refusal is terminal
///         (<c>ReconcileOutcome.Failed</c>) rather than <c>InProgress</c>, because a body that
///         names an address of the wrong family can never converge and retrying it forever would hide
///         that behind a spinner.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> <see cref="PublicIpAddresses.OvnEipJson" /> is a pure function of the
///             namespace, the name and the body.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. Both halves are asserted in <c>NetworkPublicIpTests</c>, because
///             <c>ReconcilerConformance.CheckNoHiddenState</c> is structurally blind to a
///             <c>readonly</c> field of a mutable collection type — seven sightings in seven families
///             and only the cross-tenant test catches it.
///         </item>
///         <item><b>Bounded.</b> One apply and one read, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c>, never the apply's own result. ⚠ On this type the reading is the whole
///             product: the tenant asked for an address and the answer arrives on
///             <c>status.v4Ip</c> one controller pass later.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class PublicIpAddressReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => PublicIpAddresses.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a public address is a Kube-OVN "
                + "OvnEip in a cluster. CyberCloud.Network/publicIpAddresses declares RequiresCluster, "
                + "so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (PublicIpAddresses.AddressProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = PublicIpAddresses.ObjectNameOf(context.Namespace, context.Id.Name);

        context.Log.Report("applying", $"applying the OvnEip '{name}'", 20);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            // ⚠ Cluster-scoped: no `InNamespace`. The namespace is inside `name`.
            .WithKind(PublicIpAddresses.OvnEipKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                PublicIpAddresses.OvnEipJson(context.Namespace, context.Id.Name, context.Desired)
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
                // ⚠ THE OTHER FIELD MANAGER IS THE KUBE-OVN CONTROLLER AND FORCING WOULD TAKE A FIELD
                // THIS PROVIDER CANNOT SUPPLY A VALUE FOR. createOrUpdateOvnEipCR writes back
                // spec.v4Ip, spec.v6Ip, spec.macAddress and spec.type on an object this provider
                // applied. Forcing would overwrite an allocated address with an empty string, and the
                // fabric would have handed out an address this object no longer names.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the OvnEip and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        var target = PublicIpAddresses.OvnEipRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    $"'{target}' was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!PublicIpAddresses.Matches(read.GetValueOrThrow().Json, context.Desired)) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report(
            "ready",
            $"the OvnEip '{name}' reads back as desired. The address the fabric allocated is on "
            + $"POST …/{PublicIpAddresses.AllocationAction}.",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A DELETE CAN LEGITIMATELY NOT FINISH, AND ON THIS TYPE THAT IS THE FABRIC PROTECTING
    ///     A TENANT RATHER THAN A FAULT.</b> <c>handleUpdateOvnEip</c>'s deletion path refuses to drop
    ///     the finalizer while a NAT rule still uses the address — <i>"is still being used by NAT
    ///     rules … waiting for them to be deleted"</i> — so the object sits in <c>Terminating</c> and
    ///     this method keeps answering <c>InProgress</c> with the object still readable. That is the
    ///     right answer: releasing the address underneath a live rule would hand it to another tenant
    ///     while traffic was still arriving. ⚠ It cannot happen in this api-version, because nothing
    ///     can attach an address yet.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = PublicIpAddresses.ObjectNameOf(context.Namespace, context.Id.Name);

        context.Log.Report("deleting", $"deleting the OvnEip '{name}'");

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .WithKind(PublicIpAddresses.OvnEipKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                PublicIpAddresses.OvnEipJson(context.Namespace, context.Id.Name, context.Desired)
            )
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var target = PublicIpAddresses.OvnEipRef(context.Namespace, context.Id.Name);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress(
                $"'{target}' is still readable. The fabric holds an address until nothing is using it.",
                TimeSpan.FromSeconds(5)
            );
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the OvnEip '{name}' is gone and the address is released", 100);
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
            PublicIpAddresses.OvnEipRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the address is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = PublicIpAddresses.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the address carries the desired spec" : "the address has drifted"
        };
    }
}
