using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Moves the claims an operator owns out of its custody before a teardown, and back into the
///     custody of the object a restore re-creates.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> docs/plan/08 § Soft delete keeps a soft-deleted resource's volumes
///         because
///         <i>
///             "deleting a <c>StatefulSet</c> does not delete the
///             <c>PersistentVolumeClaim</c>s its <c>volumeClaimTemplate</c> created"
///         </i>. That sentence
///         is true of a family that renders its own set and false of every operator that creates its
///         claims itself and stamps a controller reference on each — CloudNativePG does, through
///         <c>SetAsOwnedBy(Controller: true)</c> — because Kubernetes garbage-collects a dependent
///         with its controller, before any window has started. The teardown is the same on a soft
///         and a hard delete by design, so the provider cannot skip the delete; what it can do is
///         what <c>kubectl cnpg destroy --keep-pvc</c> does by hand: take the reference off first.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A detached claim is invisible to its operator, which is why there are two
///             directions and not one.
///         </b> CloudNativePG lists a cluster's claims by controller reference
///         (<c>getManagedPVCs</c> over the <c>.metadata.controller</c> field index), so a claim with
///         no controller is not a dangling claim it reattaches — it is a claim it has never heard of.
///         A re-created <c>Cluster</c> with the same name would then see no claims, bootstrap a fresh
///         primary, find the claim's name already taken and, by
///         <c>EnsureTargetDirectoriesDoNotExist</c>, rename the tenant's data directory aside and
///         <c>initdb</c> beside it. So the restore writes a controller reference naming the new
///         <c>Cluster</c>'s uid before the operator's first pass — <see cref="AdoptAsync" /> — and the
///         provider pauses the operator across that write, because the operator's own restart of the
///         data directory is milliseconds behind the create.
///     </para>
///     <para>
///         ⚠ <b>The same guard as the purge, in both directions.</b> Every claim is read back and
///         <see cref="RetainedVolume.CheckOwnership" /> runs against the stored object before any
///         reference is written or cleared. A detach of the wrong claim orphans somebody else's
///         volume from its owner; an adopt of the wrong claim hands somebody else's volume to a
///         database that will mount it. Neither destroys data by itself and both are the first step
///         of something that does, so the refusal is the whole batch, before the first write — the
///         reclaimer's rule.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Here in the contracts assembly rather than beside <c>VolumeReclaimer</c>, because
///             the caller is a provider.
///         </b> The reclaimer runs in the manager, on the branch a hard delete
///         and a purge share; this runs inside a provider's own <c>DeleteAsync</c> and
///         <c>ReconcileAsync</c>, where the order relative to the provider's other applies is the
///         whole point, and a provider cannot name the manager's assembly.
///     </para>
/// </remarks>
public static class VolumeCustody {
    /// <summary>
    ///     Clears the owner references of every claim in <paramref name="volumes" /> that carries
    ///     any, so that the owner's deletion leaves them standing.
    /// </summary>
    /// <param name="cluster">The cluster holding the claims.</param>
    /// <param name="volumes">The claims, each with the labels that prove it is the resource's.</param>
    /// <param name="context">The resource whose teardown this precedes.</param>
    /// <param name="cancellationToken">Cancels the detach.</param>
    /// <returns>
    ///     How many claims were detached by this call — zero on a second pass, which is what makes
    ///     the teardown idempotent — or the failure that stopped it before any reference was
    ///     cleared. A claim that is already gone is skipped rather than failed.
    /// </returns>
    public static Task<Result<int>> DetachAsync(
        IKubeClusterConnection cluster,
        ImmutableArray<RetainedVolume> volumes,
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        MoveAsync(cluster, volumes, null, context, cancellationToken);

    /// <summary>
    ///     Makes <paramref name="owner" /> the controller of every claim in
    ///     <paramref name="volumes" /> that it does not already control.
    /// </summary>
    /// <param name="cluster">The cluster holding the claims.</param>
    /// <param name="volumes">The claims, each with the labels that prove it is the resource's.</param>
    /// <param name="owner">
    ///     The re-created object, <b>read back</b> so that its uid is the API server's. ⚠ Refused
    ///     when incomplete: an adoption naming a predicted uid hands the claims to nothing, and the
    ///     garbage collector treats an owner that does not exist as one that was deleted.
    /// </param>
    /// <param name="context">The resource being restored.</param>
    /// <param name="cancellationToken">Cancels the adopt.</param>
    /// <returns>
    ///     How many claims changed hands, or the failure that stopped it before any reference was
    ///     written.
    /// </returns>
    public static Task<Result<int>> AdoptAsync(
        IKubeClusterConnection cluster,
        ImmutableArray<RetainedVolume> volumes,
        OwnerRef owner,
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(owner);

        if (!owner.IsComplete) {
            return Task.FromResult(
                Result<int>.Failure(
                    ErrorCode.InternalError,
                    $"'{context.Id.Path}' asked for its claims to be adopted by '{owner}', which is "
                    + "not a complete owner reference. The uid is read back from the object the API "
                    + "server created, never predicted — an owner reference to a uid that does not "
                    + "exist is what the garbage collector deletes a dependent over."
                )
            );
        }

        return MoveAsync(cluster, volumes, owner, context, cancellationToken);
    }

    static async Task<Result<int>> MoveAsync(
        IKubeClusterConnection cluster,
        ImmutableArray<RetainedVolume> volumes,
        OwnerRef? owner,
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(cluster);

        if (volumes.IsDefaultOrEmpty) {
            return Result<int>.Success(0);
        }

        // ── The guard, before anything is written ───────────────────────────────────────────────
        //
        // ⚠ EVERY CLAIM IS CHECKED BEFORE THE FIRST WRITE, the same rule VolumeReclaimer keeps. A
        // list whose third entry is not ours is a provider that got the list wrong, and a loop that
        // had already moved the first two would have acted on that wrong list before finding out.
        var pending = new List<RetainedVolume>(volumes.Length);

        foreach (var volume in volumes) {
            if (volume.CheckAddress(context) is { } addressError) {
                return Result<int>.Failure(addressError);
            }

            var read = await cluster.GetAsync(volume.Claim, cancellationToken);

            if (read.TryGetError(out var readError)) {
                if (readError.Code == ErrorCode.ResourceNotFound) {
                    // Gone already: on a detach that is the operator or a previous pass; on an
                    // adopt it is a claim the restore has nothing to hand over. Neither is ours to
                    // fail on.
                    continue;
                }

                return Result<int>.Failure(readError);
            }

            var json = read.GetValueOrThrow().Json;

            if (volume.CheckOwnership(json, context) is { } ownershipError) {
                return Result<int>.Failure(ownershipError);
            }

            if (NeedsMoving(json, owner)) {
                pending.Add(volume);
            }
        }

        foreach (var volume in pending) {
            var moved = await cluster.SetOwnerAsync(volume.Claim, owner, cancellationToken);

            if (moved.TryGetError(out var moveError) && moveError.Code != ErrorCode.ResourceNotFound) {
                return Result<int>.Failure(
                    moveError.Code,
                    (owner is null
                            ? $"'{volume.Claim}' could not be detached from its owner, so the teardown of "
                            + $"'{context.Id.Path}' stops before deleting anything: "
                            : $"'{volume.Claim}' could not be handed to '{owner}', so the restore of "
                            + $"'{context.Id.Path}' stops before the operator can see it: ")
                    + moveError.Message
                );
            }

            context.Log.Report(
                owner is null ? "detaching" : "adopting",
                owner is null
                    ? $"'{volume.Claim.Name}' no longer belongs to the object about to be deleted — {volume.Reason}"
                    : $"'{volume.Claim.Name}' now belongs to {owner.Kind} '{owner.Name}' — {volume.Reason}"
            );
        }

        // ── Read back, so a reported move is a move the API server holds ────────────────────────
        foreach (var volume in pending) {
            var read = await cluster.GetAsync(volume.Claim, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? Result<int>.Failure(
                        ErrorCode.InternalError,
                        $"'{volume.Claim}' was read, its ownership was changed, and it is gone on the "
                        + "read-back. Nothing here deletes a claim, so something else did, and the "
                        + $"pass over '{context.Id.Path}' stops rather than reporting a volume it "
                        + "cannot see."
                    )
                    : Result<int>.Failure(readError);
            }

            if (NeedsMoving(read.GetValueOrThrow().Json, owner)) {
                return Result<int>.Failure(
                    ErrorCode.InternalError,
                    $"'{volume.Claim}' still reads back "
                    + (owner is null ? "with an owner" : $"without '{owner}' as its controller")
                    + " after the change was accepted, so the API server did not hold it. The pass "
                    + $"over '{context.Id.Path}' stops here."
                );
            }
        }

        return Result<int>.Success(pending.Count);
    }

    /// <summary>
    ///     Whether the stored claim is not yet in the requested custody: it has an owner when none is
    ///     wanted, or lacks the wanted controller.
    /// </summary>
    static bool NeedsMoving(string json, OwnerRef? owner) {
        JsonNode? document;

        try {
            document = JsonNode.Parse(json);
        } catch (System.Text.Json.JsonException) {
            return false;
        }

        if (owner is null) {
            return KubeJson.HasOwners(document);
        }

        return KubeJson.ControllerOf(document) is not { } controller
            || !string.Equals(controller.Uid, owner.Uid, StringComparison.Ordinal);
    }

    /// <summary>A count phrased for a progress line.</summary>
    /// <param name="claims">How many claims.</param>
    public static string Count(int claims) =>
        claims == 1 ? "1 claim" : claims.ToString(CultureInfo.InvariantCulture) + " claims";
}
