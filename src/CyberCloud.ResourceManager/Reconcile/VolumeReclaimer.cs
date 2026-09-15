using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     Removes the volumes a converged teardown deliberately left, and refuses to remove anything
///     else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THIS IS THE ONE PLACE IN THE PLATFORM THAT IS SUPPOSED TO DESTROY A TENANT'S DATA,
///             WHICH MAKES PRECISION THE WHOLE JOB.
///         </b> Everywhere else a wrong delete costs an object
///         that a reconcile pass puts back. Here there is nothing to put back: a
///         <c>PersistentVolumeClaim</c> removed under a <c>Delete</c> reclaim policy takes the
///         volume with it and the data is gone with no recovery. So this class never acts on a name.
///         It reads the claim back, checks every label in
///         <see cref="RetainedVolume.OwnedBy" /> against the object the API server is holding, and
///         refuses the <b>whole</b> reclaim — non-retryably, naming the claim and the label that
///         disagreed — the moment one does not match.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Four refusals happen before a single read, and they are refusals rather than
///             filters.
///         </b> A claim addressed at the wrong kind, in the wrong namespace, with no name, or
///         with no ownership evidence is a provider getting it wrong in a way that a silent skip
///         would hide until an operator went looking for the disks. Each is a purge that fails with
///         a reason, which is the actionable outcome — docs/plan/08 § The reconcile loop's
///         <i>
///             "a
///             resource stuck forever is worse than a resource that failed, because a failure is
///             actionable"
///         </i>, applied to the one step whose mistakes are permanent.
///     </para>
///     <para>
///         ⚠ <b>Absence converges and is not an error.</b> A claim that is already gone is the
///         goal — the previous attempt removed it, or the type never had one on this cluster — and a
///         reclaim re-driven from a reminder must reach <c>Converged</c> on its second pass. What is
///         <i>not</i> treated as absence is a read that failed for any other reason: that is the
///         cluster not answering, and converging on it would report disks destroyed that are still
///         there.
///     </para>
///     <para>
///         ⚠ <b>The delete is issued and the claim is then read back</b>, exactly as every provider's
///         <c>DeleteAsync</c> does, because <c>Converged</c> means <i>gone</i> rather than
///         <i>accepted</i>. A claim still bound to a terminating pod is held by the
///         <c>kubernetes.io/pvc-protection</c> finalizer until that pod is gone, so
///         <see cref="ReconcileOutcome.InProgress" /> here is ordinary rather than exceptional — the
///         operation is re-driven and the ceiling in <c>ReconcileSchedule</c> is what ends a wait
///         that never finishes.
///     </para>
/// </remarks>
public static class VolumeReclaimer {
    /// <summary>How long the whole reclaim may take before the pass is over budget.</summary>
    /// <remarks>
    ///     ⚠ The same budget a reconcile pass gets, for the same reason: this runs inside a grain
    ///     turn and Orleans grains are single-threaded — docs/plan/08 § The reconcile loop, clause 3.
    /// </remarks>
    public static TimeSpan Budget => ReconcileDriver.PassBudget;

    /// <summary>
    ///     Asks the reconciler what its teardown kept, checks each claim really is the resource's,
    ///     and removes it.
    /// </summary>
    /// <param name="reconciler">The resource's reconciler.</param>
    /// <param name="context">
    ///     The context the teardown ran with — <see cref="ReconcileContext.Desired" /> is what names
    ///     the claims, so this must be called before the resource grain is cleared.
    /// </param>
    /// <param name="cancellationToken">Cancels the reclaim.</param>
    /// <returns>
    ///     <see cref="ReconcileOutcome.Converged" /> once every declared claim reads back as gone;
    ///     <see cref="ReconcileOutcome.InProgress" /> while one is still terminating; a
    ///     <b>non-retryable</b> failure when a claim is not the resource's, and a retryable one when
    ///     the cluster did not answer.
    /// </returns>
    public static async Task<ReconcileOutcome> ReclaimAsync(
        IResourceReconciler reconciler,
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reconciler);

        var declared = await reconciler.RetainedVolumesAsync(context, cancellationToken);

        if (declared.TryGetError(out var declaredError)) {
            // ⚠ Retryable. A provider that could not enumerate its own claims — a desired body it
            // could not read, a size it could not parse — has told us it does not know what to
            // remove, and "do not know" must never converge on this path.
            return ReconcileOutcome.Failed(declaredError, true);
        }

        var volumes = declared.GetValueOrThrow();

        if (volumes.IsDefaultOrEmpty) {
            // The honest answer for a type with no volumeClaimTemplate, and the default on
            // IResourceReconciler. Nothing to do, and nothing owed.
            return ReconcileOutcome.Converged;
        }

        if (context.Cluster is not { } cluster) {
            // ⚠ NOT Converged, and this is the asymmetry with a reconciler's own DeleteAsync — which
            // converges with no cluster because a teardown with nothing to reach has nothing left to
            // remove. Here there IS something left: the provider just named it. Converging would
            // report a purge that destroyed disks it never reached, which is the exact shape of the
            // defect this class exists to close. Retryable, so a connection that comes back finishes
            // the job and one that does not ends at ReconcileSchedule's ceiling with a reason.
            return ReconcileOutcome.Failed(
                new Error(
                    ErrorCode.InternalError,
                    $"'{context.Id.Path}' kept {Count(volumes.Length)} through its teardown and there "
                    + "is no cluster connection to remove them with, so the purge cannot finish. The "
                    + $"claims are still there: {Names(volumes)}."
                ),
                true
            );
        }

        context.Log.Report(
            "reclaiming",
            $"removing {Count(volumes.Length)} the teardown of '{context.Id.Name}' kept"
        );

        // ── The guard, before anything is deleted ───────────────────────────────────────────────
        //
        // ⚠ EVERY CLAIM IS CHECKED BEFORE THE FIRST DELETE IS ISSUED, rather than checked-then-deleted
        // one at a time. A list whose third entry is not ours is a provider that got the whole list
        // wrong, and a loop that had already destroyed the first two would have acted on that wrong
        // list before finding out.
        var verified = ImmutableArray.CreateBuilder<RetainedVolume>(volumes.Length);

        foreach (var volume in volumes) {
            if (volume.CheckAddress(context) is { } addressError) {
                return ReconcileOutcome.Failed(addressError, false);
            }

            var read = await cluster.GetAsync(volume.Claim, cancellationToken);

            if (read.TryGetError(out var readError)) {
                if (readError.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return ReconcileOutcome.Failed(readError, true);
            }

            if (volume.CheckOwnership(read.GetValueOrThrow().Json, context) is { } ownershipError) {
                return ReconcileOutcome.Failed(ownershipError, false);
            }

            verified.Add(volume);
        }

        if (verified.Count == 0) {
            context.Log.Report(
                "reclaimed",
                $"every volume '{context.Id.Name}' kept is already gone",
                100
            );

            return ReconcileOutcome.Converged;
        }

        foreach (var volume in verified) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(volume.Claim.Namespace)
                .WithKind(volume.Claim.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(volume.Claim.Name))
                // ⚠ Background. A claim's dependents are the volume beneath it, which the
                // StorageClass's reclaim policy disposes of on its own schedule and which no pass of
                // ours can wait for.
                    .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.Failed(deleteError, true);
            }

            context.Log.Report("reclaiming", $"removed '{volume.Claim.Name}' — {volume.Reason}");
        }

        // ── Gone, read back ─────────────────────────────────────────────────────────────────────
        foreach (var volume in verified) {
            var read = await cluster.GetAsync(volume.Claim, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress(
                    $"'{volume.Claim}' has been deleted and is still readable. A claim is held under "
                    + "the pvc-protection finalizer until the pods that mounted it are gone.",
                    TimeSpan.FromSeconds(5)
                );
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.Failed(read.Error, true);
            }
        }

        context.Log.Report(
            "reclaimed",
            $"{Count(verified.Count)} '{context.Id.Name}' kept through its recovery window "
            + "are gone",
            100
        );

        return ReconcileOutcome.Converged;
    }

    // ⚠ The two guards this class used to hold — Addressable and Owned — are RetainedVolume.CheckAddress
    // and RetainedVolume.CheckOwnership now, because VolumeCustody runs the same checks before a
    // detach and an adopt, and a guard written twice is a guard that drifts.

    /// <summary>The smallest object a delete command will accept.</summary>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    static string Count(int volumes) =>
        volumes == 1 ? "1 volume" : $"{volumes.ToString(System.Globalization.CultureInfo.InvariantCulture)} volumes";

    static string Names(ImmutableArray<RetainedVolume> volumes) => string.Join(", ", volumes.Select(x => x.Claim.Name));
}
