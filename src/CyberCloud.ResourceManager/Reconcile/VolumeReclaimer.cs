using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>
///     Removes the volumes a converged teardown deliberately left, and refuses to remove anything
///     else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE ONE PLACE IN THE PLATFORM THAT IS SUPPOSED TO DESTROY A TENANT'S DATA,
///         WHICH MAKES PRECISION THE WHOLE JOB.</b> Everywhere else a wrong delete costs an object
///         that a reconcile pass puts back. Here there is nothing to put back: a
///         <c>PersistentVolumeClaim</c> removed under a <c>Delete</c> reclaim policy takes the
///         volume with it and the data is gone with no recovery. So this class never acts on a name.
///         It reads the claim back, checks every label in
///         <see cref="RetainedVolume.OwnedBy" /> against the object the API server is holding, and
///         refuses the <b>whole</b> reclaim — non-retryably, naming the claim and the label that
///         disagreed — the moment one does not match.
///     </para>
///     <para>
///         ⚠ <b>Four refusals happen before a single read, and they are refusals rather than
///         filters.</b> A claim addressed at the wrong kind, in the wrong namespace, with no name, or
///         with no ownership evidence is a provider getting it wrong in a way that a silent skip
///         would hide until an operator went looking for the disks. Each is a purge that fails with
///         a reason, which is the actionable outcome — docs/plan/08 § The reconcile loop's <i>"a
///         resource stuck forever is worse than a resource that failed, because a failure is
///         actionable"</i>, applied to the one step whose mistakes are permanent.
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
            if (Addressable(volume, context) is { } addressError) {
                return ReconcileOutcome.Failed(addressError, false);
            }

            var read = await cluster.GetAsync(volume.Claim, cancellationToken);

            if (read.TryGetError(out var readError)) {
                if (readError.Code == ErrorCode.ResourceNotFound) {
                    continue;
                }

                return ReconcileOutcome.Failed(readError, true);
            }

            if (Owned(volume, read.GetValueOrThrow().Json, context) is { } ownershipError) {
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

    /// <summary>
    ///     Whether the claim is even addressable as one of this resource's volumes, or the refusal
    ///     that says why not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The namespace check is the one that matters most and is the cheapest.</b> A namespace
    ///     is per resource group per cluster, so a claim outside <see cref="ReconcileContext.Namespace" />
    ///     belongs to a different resource group and possibly a different tenant. Nothing a provider
    ///     can compute from its own desired body should ever land outside it, and the one bug that
    ///     would — a name built from a field a caller controls — is exactly the one that reaches
    ///     another tenant's disks.
    /// </remarks>
    static Error? Addressable(RetainedVolume volume, ReconcileContext context) {
        if (volume.Claim is null || volume.Claim.Name.Length == 0) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared a retained volume with no name. A purge acts only on "
                + "claims it can address, and refuses rather than guessing."
            );
        }

        if (volume.Claim.Kind != RetainedVolume.ClaimKind) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared '{volume.Claim.Name}' as a retained volume of kind "
                + $"'{volume.Claim.Kind}'. A purge removes {RetainedVolume.ClaimKind.Kind}s and "
                + "nothing else — a teardown is what removes workloads, and it has already run."
            );
        }

        if (!string.Equals(volume.Claim.Namespace, context.Namespace, StringComparison.Ordinal)) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared a retained volume '{volume.Claim.Name}' in namespace "
                + $"'{volume.Claim.Namespace}' and the resource lives in '{context.Namespace}'. A "
                + "namespace is one resource group on one cluster, so a claim outside it belongs to "
                + "somebody else and this purge will not touch it."
            );
        }

        if (volume.OwnedBy is null || volume.OwnedBy.IsEmpty) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared '{volume.Claim.Name}' as a retained volume with no "
                + "ownership labels. A claim named without evidence is a name, and a purge that acts "
                + "on a name is how the wrong tenant's disk goes — see RetainedVolume.OwnedBy."
            );
        }

        return null;
    }

    /// <summary>
    ///     Whether the object the API server is holding really is the resource's, or the refusal that
    ///     says which label disagreed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read from the STORED object, never from the document we would have written.</b> The
    ///     claim was created by the <c>StatefulSet</c> controller and not by this platform, so the
    ///     only thing that can say whom it belongs to is what the API server has. Checking a rendered
    ///     copy would be checking our own arithmetic twice.
    /// </remarks>
    static Error? Owned(RetainedVolume volume, string json, ReconcileContext context) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error) {
            return new(
                ErrorCode.InternalError,
                $"The API server's copy of '{volume.Claim}' did not parse as JSON, so its ownership "
                + $"cannot be checked and it will not be removed: {error.Message}"
            );
        }

        var metadata = root?["metadata"] as JsonObject;
        var labels = metadata?["labels"] as JsonObject;

        // ⚠ ABSENT IS NOT EMPTY. A claim with no labels at all is not one this platform's
        // volumeClaimTemplate produced — Kubernetes copies the set's selector onto every claim it
        // creates — so it is somebody else's object wearing a name we predicted.
        if (labels is null) {
            return new(
                ErrorCode.InternalError,
                $"'{volume.Claim}' carries no labels, so nothing connects it to '{context.Id.Path}'. "
                + "A claim created from this resource's volumeClaimTemplate carries the set's "
                + "selector; this one does not, so it belongs to something else and the purge "
                + "refuses it."
            );
        }

        var stored = metadata?["namespace"]?.GetValue<string>();

        if (stored is { Length: > 0 } && !string.Equals(stored, context.Namespace, StringComparison.Ordinal)) {
            return new(
                ErrorCode.InternalError,
                $"'{volume.Claim}' came back from the API server in namespace '{stored}' rather than "
                + $"'{context.Namespace}'. The purge refuses a claim it did not address."
            );
        }

        foreach (var (key, expected) in volume.OwnedBy) {
            var actual = labels[key] switch {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                null => null,
                var other => other.ToString()
            };

            if (actual is null) {
                return new(
                    ErrorCode.InternalError,
                    $"'{volume.Claim}' does not carry '{key}', which '{context.Id.Path}' says every "
                    + "claim of its own carries. The purge refuses a volume it cannot prove it owns."
                );
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
                return new(
                    ErrorCode.InternalError,
                    $"'{volume.Claim}' carries '{key}={actual}' and '{context.Id.Path}' owns only "
                    + $"claims carrying '{key}={expected}'. Something else in this namespace has the "
                    + "name this purge predicted, so the purge stops rather than destroying it."
                );
            }
        }

        return null;
    }

    /// <summary>The smallest object a delete command will accept.</summary>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    static string Count(int volumes) =>
        volumes == 1 ? "1 volume" : $"{volumes.ToString(System.Globalization.CultureInfo.InvariantCulture)} volumes";

    static string Names(ImmutableArray<RetainedVolume> volumes) =>
        string.Join(", ", volumes.Select(x => x.Claim.Name));
}
