using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Converges one file share onto a <c>ReadWriteMany</c> claim against its account's CSI driver,
///     applying the driver on the way if the account has none yet.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE DRIVER IS SHARED AND THIS RECONCILER STILL APPLIES IT ON EVERY PASS.</b> A
///         <c>SeaweedCSIDriver</c> is one per filer, so every share of an account renders the same
///         document — <see cref="StorageFileShares.DriverJson" /> is a pure function of the account
///         and the namespace — and server-side apply of an unchanged document is a no-op. What the
///         re-apply buys is drift correction: a driver a well-meant <c>kubectl</c> pointed at another
///         filer is put back by the next share that reconciles. What it costs is that the seven
///         labels on the driver name whichever share applied it last, which is recorded at
///         <c>conformance.yaml § owed</c>, <c>the-driver-carries-one-shares-labels</c>.
///     </para>
///     <para>
///         ⚠ <b>The driver goes first and the claim second, because a claim against a class that does
///         not exist stays <c>Pending</c> with no event naming the cause.</b> The external-provisioner
///         only watches claims whose class it serves, so a claim applied before its driver is not
///         refused, not retried, and not reported — it waits. The order here makes that window one
///         pass long at most.
///     </para>
///     <para>
///         ⚠ <b>Never looks its account up</b>, for the reasons <see cref="StorageBucketReconciler" />
///         gives. The only thing it takes from the parent is its name, off the address, and the operator
///         is what resolves that name to a filer — and reports <c>ClusterReachable=False</c> on the
///         driver when it cannot, which is where a share placed in the wrong cluster shows up.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both documents are pure functions of the address, the namespace and
///             the body. Nothing counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />. <c>StorageFileShareReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> Two applies, two reads, on the caller's token. The delete adds one
///             listing.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of both objects, never the apply's own result. ⚠ It does <i>not</i> wait
///             for the claim to be <c>Bound</c>: binding needs the driver's controller pod to be
///             scheduled and running, which is minutes on a cold node, and clause 3's budget is thirty
///             seconds. A <c>Pending</c> claim carrying the desired spec is converged as far as this
///             platform's writes go; what is still coming is reported by <c>listMountTargets</c>, which
///             refuses until the claim is bound rather than answering with an empty path.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class StorageFileShareReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageFileShares.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a file share is a claim in a "
                + "cluster. CyberCloud.Storage/accounts/fileShares declares RequiresCluster, so the "
                + "driver should have refused this pass — see ReconcileDriver."
            );
        }

        var account = StorageFileShares.AccountOf(context.Id);

        context.Log.Report("applying", $"ensuring the CSI driver of account '{account}' is applied", 20);

        var driver = await Apply(context, cluster, StorageFileShares.CsiDriverKind, StorageFileShares.DriverJson(context.Namespace, context.Id))
            .ApplyAsync(cancellationToken);

        if (driver.TryGetError(out var driverError)) {
            return ReconcileOutcome.FromFailure(driverError);
        }

        if (Unfinished(context, driver.GetValueOrThrow(), "the account's CSI driver") is { } stalled) {
            return stalled;
        }

        context.Log.Report("applying", $"applying the claim of '{context.Id.Name}' in account '{account}'", 50);

        var claim = await Apply(context, cluster, StorageFileShares.ClaimKind, StorageFileShares.ClaimJson(context.Namespace, context.Id, context.Desired))
            .ApplyAsync(cancellationToken);

        if (claim.TryGetError(out var claimError)) {
            return ReconcileOutcome.FromFailure(claimError);
        }

        if (Unfinished(context, claim.GetValueOrThrow(), "the claim") is { } waiting) {
            return waiting;
        }

        // ── Clause 4. Everything above this line is a claim; these are the readings. ────────────
        foreach (var target in Targets(context.Namespace, context.Id)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!StorageFileShares.Matches(read.GetValueOrThrow().Json, context.Id, context.Namespace, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the objects of '{context.Id.Name}' read back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE CLAIM ALWAYS, THE DRIVER ONLY WHEN THIS WAS THE ACCOUNT'S LAST SHARE.</b> The
    ///         platform cannot enumerate a resource's children — the finding recorded at
    ///         <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>,
    ///         <c>parent-delete-orphans-buckets</c> — so "last" is asked of the <i>cluster</i> instead:
    ///         the claims in this namespace carrying <see cref="StorageFileShares.AccountLabel" /> for
    ///         this account, once this share's own is gone. Removing the driver while a sibling's claim
    ///         is bound would tear the node plugin out from under every mount in the account, which is
    ///         why the listing fails closed: a connection that cannot list answers a failure, and a
    ///         failure here leaves the driver standing rather than guessing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The race this leaves is the harmless one.</b> Two shares deleted at once may each see
    ///         the other's claim and neither remove the driver, which leaves a driver with no claims
    ///         until the account is deleted — a Deployment and two DaemonSets idling, not data at risk.
    ///         A share created while the last one is being deleted may see its driver removed under it,
    ///         and its next pass applies it again. The direction that would lose data — removing a
    ///         driver with a bound claim — needs the listing to have answered "none" while a claim
    ///         existed, which a real API server does not do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A claim a pod still mounts does not go</b>: <c>kubernetes.io/pvc-protection</c> holds
    ///         it until the pod is gone, and this reports <c>InProgress</c> for as long as that takes.
    ///         That is the tenant's pod and the tenant's decision, and the reminder keeps asking.
    ///     </para>
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var account = StorageFileShares.AccountOf(context.Id);
        var claimRef = StorageFileShares.ClaimRef(context.Namespace, context.Id);

        context.Log.Report("deleting", $"deleting the claim of '{context.Id.Name}'");

        var deleted = await Apply(context, cluster, StorageFileShares.ClaimKind, Placeholder(claimRef.Name))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var read = await cluster.GetAsync(claimRef, cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{claimRef}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        // ── The driver, if nothing else in the account still needs it ──────────────────────────
        var siblings = await cluster.ListAsync(
            StorageFileShares.ClaimKind,
            context.Namespace,
            KubeLabels.ResourceType + "=" + KubeLabels.ResourceTypeValue(StorageFileShares.Type)
            + "," + StorageFileShares.AccountLabel + "=" + account,
            cancellationToken
        );

        if (siblings.TryGetError(out var listError)) {
            // ⚠ Fail rather than remove. See the remarks: an answer of "none" this platform invented
            // is the one that unmounts a sibling's data.
            return ReconcileOutcome.FromFailure(listError);
        }

        if (siblings.GetValueOrThrow().Count > 0) {
            context.Log.Report(
                "deleted",
                $"the claim of '{context.Id.Name}' is gone; the account's CSI driver stays, "
                + $"{siblings.GetValueOrThrow().Count} other share(s) still use it",
                100
            );

            return ReconcileOutcome.Converged;
        }

        var driverRef = StorageFileShares.DriverRef(context.Namespace, context.Id);

        context.Log.Report("deleting", $"deleting the CSI driver of account '{account}' — this was its last share");

        var driverDeleted = await Apply(context, cluster, StorageFileShares.CsiDriverKind, Placeholder(driverRef.Name))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (driverDeleted.TryGetError(out var driverError) && driverError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(driverError);
        }

        var driverRead = await cluster.GetAsync(driverRef, cancellationToken);

        if (driverRead.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{driverRef}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (driverRead.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(driverRead.Error);
        }

        context.Log.Report("deleted", $"the objects of '{context.Id.Name}' are gone", 100);
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

        var read = await cluster.GetAsync(StorageFileShares.ClaimRef(context.Namespace, context.Id), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the share's claim is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = StorageFileShares.Matches(found.Json, context.Id, context.Namespace, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the share's claim carries the desired spec" : "the share's claim has drifted"
        };
    }

    /// <summary>Every object a share owns, in apply order: the account's driver, then the claim.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The share's address.</param>
    static ObjectRef[] Targets(string ns, ResourceId id) => [
        StorageFileShares.DriverRef(ns, id),
        StorageFileShares.ClaimRef(ns, id)
    ];

    /// <summary>A command over one of this share's objects, carrying the account label.</summary>
    /// <remarks>
    ///     ⚠ <see cref="StorageFileShares.AccountLabel" /> goes through <c>WithLabels</c> on the driver
    ///     as well as on the claim. The delete only lists claims, but a driver that says which account
    ///     it serves is one <c>kubectl get</c> away from being placed, which a digest in its name is not.
    /// </remarks>
    static IKubeCommandBuilder Apply(ReconcileContext context, IKubeClusterConnection cluster, GroupVersionKind kind, string json) =>
        KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .WithLabels((StorageFileShares.AccountLabel, StorageFileShares.AccountOf(context.Id)))
            .ObjectJson(json);

    /// <summary>The smallest object a delete command will accept — a name and nothing else.</summary>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    /// <summary>
    ///     Turns an apply that did not land into the outcome that comes back for it, or
    ///     <see langword="null" /> when it landed.
    /// </summary>
    static ReconcileOutcome? Unfinished(ReconcileContext context, ApplyOutcome outcome, string what) {
        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ `.spec.resources.requests.storage` is the plausible one on the claim: a tenant's own
                // resize tooling would reach for it, and forcing would undo them every pass.
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
}
