using CyberCloud.Core;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using System.Globalization;

namespace CyberCloud.Tenancy;

/// <summary>
///     The claim state machine both index grains run — docs/plan/06 § Two-phase create.
/// </summary>
/// <remarks>
///     <para>
///         Shared because a path claim and an email claim differ only in what is hashed into the
///         key. The machine is four states and the interesting transitions are the ones that happen
///         when nobody is looking:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Free → Claimed</b> on <c>TryClaim</c>, under a lease of
///             <see cref="LeaseDuration" />.
///         </item>
///         <item>
///             <b>Claimed → Free</b> by <i>expiry</i>, which is what makes "the silo dies between 1
///             and 3, the claim expires and the name is free again" true. ⚠ Evaluated <b>on read</b>,
///             not by a timer: a timer that has to fire for correctness is a timer whose silo can
///             die, and the whole failure being modelled here is a silo dying.
///         </item>
///         <item><b>Claimed → Confirmed</b> on <c>Confirm</c>. Permanent.</item>
///         <item><b>Confirmed → Free</b> on <c>Release</c>, and only on <c>Release</c>.</item>
///         <item>
///             <b>Confirmed → SoftDeleted</b> on <c>SoftDelete</c>, for a type that declares a
///             recovery window, and <b>SoftDeleted → Confirmed</b> on <c>Restore</c> — docs/plan/08
///             § Soft delete. <b>SoftDeleted → Free</b> is <c>Release</c> again, which is what a purge
///             is: the same irreversible transition, reached from the other state.
///         </item>
///     </list>
///     <para>
///         <b>The idempotence rule, which is what makes the retried <c>PUT</c> a no-op.</b> A claim
///         by the <i>same</i> id against a Claimed or Confirmed entry succeeds and changes nothing.
///         A claim by a different id is <c>ResourceAlreadyExists</c> — the gateway's <c>409</c>.
///     </para>
///     <para>
///         ⚠ <b>The idempotence rule stops at <c>SoftDeleted</c>, and that exception is deliberate.</b>
///         A <c>PUT</c> carrying the soft-deleted resource's own GUID is refused like any other,
///         because the only thing that may bring a soft-deleted resource back is a restore. Letting the
///         same id re-claim would make a create silently adopt a resource whose data plane is still
///         running and whose direct role assignments were dropped — a resurrection nobody asked for,
///         through a verb that reports a create.
///     </para>
/// </remarks>
static class IndexClaimMachine {
    /// <summary>
    ///     The claim lease. docs/plan/06 § Two-phase create, step 1: "The claim is durable and
    ///     carries a 5-minute lease."
    /// </summary>
    /// <remarks>
    ///     Five minutes is long enough that a slow create does not lose its own name and short
    ///     enough that a name freed by a dead silo comes back before a user gives up retrying. It is
    ///     measured against <see cref="IClock" /> rather than the wall clock so a test can advance
    ///     past it — see that interface's remarks.
    /// </remarks>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>The entry as it reads now, with an expired lease already collapsed to <c>Free</c>.</summary>
    public static IndexEntry Effective(IndexEntry entry, DateTimeOffset now) =>
        entry is { State: IndexEntryState.Claimed } && entry.LeaseExpiresAt <= now
            ? new() { State = IndexEntryState.Free, ModifiedAt = entry.ModifiedAt }
            : entry;

    /// <summary>Attempts the claim. Returns the new entry, or the conflict.</summary>
    public static Result<IndexEntry> TryClaim(
        IndexEntry current,
        Guid id,
        string indexedValue,
        DateTimeOffset now,
        string what
    ) {
        var entry = Effective(current, now);

        // ⚠ A SOFT-DELETED NAME IS TAKEN, AND IT IS TAKEN EVEN FROM THE RESOURCE THAT USED TO HOLD IT.
        //
        // docs/plan/08 § Soft delete: the name is held for the whole window because "a name taken by
        // somebody else leaves a restore with nowhere to go, so it would have to fail or overwrite,
        // and both are worse than making the tenant wait". The id check is skipped rather than
        // applied — see the type's remarks on why re-claiming with the old GUID is a resurrection
        // through the wrong verb.
        //
        // ⚠ THIS REFUSAL IS NOT AN ENUMERATION ORACLE, WHICH IS WORTH SAYING BECAUSE THE 404 ONE
        // SCREEN AWAY IS ABOUT EXACTLY THAT. docs/plan/07 § The enforcement seam closes the oracle at
        // the READ: a caller who cannot read the resource is told 404 by the authorization step long
        // before anything reaches here. A caller who does reach here holds write on the resource
        // group, and for them a live confirmed binding answers 409 too — so a soft-deleted name is
        // exactly as informative as a name in use, which is the property that matters. What would be
        // an oracle is a 410 on the resource's own address, and that is what ResolveAsync's refusal
        // makes unreachable.
        if (entry.State == IndexEntryState.SoftDeleted) {
            return Result<IndexEntry>.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"{what} is held by a soft-deleted resource until "
                + $"{entry.RecoverableUntil.ToString("u", CultureInfo.InvariantCulture)} and cannot be "
                + "claimed before then. Restore that resource, or purge it, and the name comes back — "
                + "docs/plan/08 § Soft delete."
            );
        }

        if (entry.State != IndexEntryState.Free && entry.BoundTo != id) {
            return Result<IndexEntry>.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"{what} is already {(entry.State == IndexEntryState.Confirmed ? "taken" : "claimed")} "
                + $"by {entry.BoundTo:D}. docs/plan/06 § Two-phase create: a taken name is a "
                + "409 Conflict."
            );
        }

        if (entry.State == IndexEntryState.Confirmed) {
            // Same id, already confirmed: the retried PUT. A no-op, and NOT a re-lease — putting a
            // confirmed binding back under a lease would let it expire out from under a live
            // resource.
            return Result<IndexEntry>.Success(entry);
        }

        return Result<IndexEntry>.Success(
            new() {
                State = IndexEntryState.Claimed,
                BoundTo = id,
                IndexedValue = indexedValue,
                LeaseExpiresAt = now + LeaseDuration,
                ModifiedAt = now
            }
        );
    }

    /// <summary>Converts a lease into a permanent binding.</summary>
    public static Result<IndexEntry> Confirm(
        IndexEntry current,
        Guid id,
        DateTimeOffset now,
        string what
    ) {
        var entry = Effective(current, now);

        return entry switch {
            { State: IndexEntryState.Free } => Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} has no live claim to confirm — the lease expired, so the name is free "
                + "again and the create must start from step 1. docs/plan/06 § Two-phase create."
            ),

            _ when entry.BoundTo != id => Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is claimed by {entry.BoundTo:D}, not by {id:D}."
            ),

            { State: IndexEntryState.Confirmed } => Result<IndexEntry>.Success(entry),

            _ => Result<IndexEntry>.Success(
                entry with {
                    State = IndexEntryState.Confirmed, LeaseExpiresAt = DateTimeOffset.MaxValue, ModifiedAt = now
                }
            )
        };
    }

    /// <summary>
    ///     Parks the binding: the resource is soft-deleted and the name is held for its window.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only a <see cref="IndexEntryState.Confirmed" /> binding can be parked.</b> A
    ///         <see cref="IndexEntryState.Claimed" /> one is a create that has not finished, and
    ///         soft-deleting it would hold a name for seven days on behalf of a resource that never
    ///         existed; a <see cref="IndexEntryState.Free" /> one has nothing to park.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Idempotent for the same id, because the delete path is re-drivable.</b> A second
    ///         park keeps the <i>original</i> deadline rather than restamping it — a re-drive from a
    ///         reminder an hour later must not silently extend the window it is re-driving, which is
    ///         the same reason <c>Confirm</c> does not re-lease a confirmed binding.
    ///     </para>
    /// </remarks>
    /// <param name="current">The entry as stored.</param>
    /// <param name="id">The bound GUID. A mismatch is a <c>Conflict</c>.</param>
    /// <param name="recoverableUntil">The end of the window, stamped by the caller from the clock.</param>
    /// <param name="now">The clock.</param>
    /// <param name="what">The name, for the message.</param>
    public static Result<IndexEntry> SoftDelete(
        IndexEntry current,
        Guid id,
        DateTimeOffset recoverableUntil,
        DateTimeOffset now,
        string what
    ) {
        var entry = Effective(current, now);

        if (entry.BoundTo != id && entry.State != IndexEntryState.Free) {
            return Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is bound to {entry.BoundTo:D}, not to {id:D}. Soft-deleting it would hold "
                + "somebody else's name."
            );
        }

        return entry.State switch {
            IndexEntryState.SoftDeleted => Result<IndexEntry>.Success(entry),

            IndexEntryState.Confirmed => Result<IndexEntry>.Success(
                entry with {
                    State = IndexEntryState.SoftDeleted,
                    LeaseExpiresAt = DateTimeOffset.MaxValue,
                    RecoverableUntil = recoverableUntil,
                    ModifiedAt = now
                }
            ),

            _ => Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is {entry.State} and only a confirmed binding can be soft-deleted. A name "
                + "under a lease belongs to a create that has not finished, and holding it for a "
                + "recovery window would reserve it for a resource that never existed."
            )
        };
    }

    /// <summary>Brings a soft-deleted binding back, so the resource is addressable again.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Refused once the window has passed, and the window is read from the entry rather
    ///         than recomputed.</b> docs/plan/08 § Soft delete makes retention immutable after
    ///         creation; a restore that recomputed the deadline from the type's current declaration
    ///         would let a provider lengthen — or shorten — a window that was already promised.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Idempotent against an already-restored binding, for the same id.</b> The restore
    ///         path writes the ReBAC edge back after this, so it has to be safe to re-drive.
    ///     </para>
    /// </remarks>
    /// <param name="current">The entry as stored.</param>
    /// <param name="id">The bound GUID. A mismatch is a <c>Conflict</c>.</param>
    /// <param name="now">The clock.</param>
    /// <param name="what">The name, for the message.</param>
    public static Result<IndexEntry> Restore(
        IndexEntry current,
        Guid id,
        DateTimeOffset now,
        string what
    ) {
        var entry = Effective(current, now);

        if (entry.State is IndexEntryState.Free or IndexEntryState.Claimed) {
            return Result<IndexEntry>.Failure(
                ErrorCode.ResourceNotFound,
                $"{what} holds nothing to restore: it is {entry.State}."
            );
        }

        if (entry.BoundTo != id) {
            return Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is bound to {entry.BoundTo:D}, not to {id:D}."
            );
        }

        if (entry.State == IndexEntryState.Confirmed) {
            return Result<IndexEntry>.Success(entry);
        }

        return entry.RecoverableUntil <= now
            ? Result<IndexEntry>.Failure(
                ErrorCode.ResourceNotFound,
                $"{what} passed the end of its recovery window at "
                + $"{entry.RecoverableUntil.ToString("u", CultureInfo.InvariantCulture)} and can no "
                + "longer be restored. docs/plan/08 § Soft delete: the window is set at creation and "
                + "is not extendable, because a window that moves is not a guarantee."
            )
            : Result<IndexEntry>.Success(
                entry with {
                    State = IndexEntryState.Confirmed,
                    LeaseExpiresAt = DateTimeOffset.MaxValue,
                    RecoverableUntil = default,
                    ModifiedAt = now
                }
            );
    }

    /// <summary>
    ///     Releases the binding so the name is immediately reusable — a hard delete, or the purge at
    ///     the end of a soft one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Reached from <see cref="IndexEntryState.Confirmed" /> and from
    ///     <see cref="IndexEntryState.SoftDeleted" />, and it is the same transition either way.</b>
    ///     docs/plan/08 § Soft delete separates the two rights — a role may hold "may delete" without
    ///     "may destroy permanently" — but that separation belongs to the permission the manager
    ///     checks, not to the state machine: from the index's point of view a purge is a release that
    ///     happened later.
    /// </remarks>
    public static Result<IndexEntry> Release(
        IndexEntry current,
        Guid id,
        DateTimeOffset now,
        string what
    ) {
        var entry = Effective(current, now);

        if (entry.State == IndexEntryState.Free) {
            // Releasing a free name is a no-op rather than an error: delete is re-driven from a
            // reminder (docs/plan/06 § Two-phase create) and the second drive must not fail.
            return Result<IndexEntry>.Success(entry);
        }

        return entry.BoundTo != id
            ? Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is bound to {entry.BoundTo:D}, not to {id:D}. Releasing it would hand "
                + "somebody else's name away."
            )
            : Result<IndexEntry>.Success(new() { State = IndexEntryState.Free, ModifiedAt = now });
    }
}
