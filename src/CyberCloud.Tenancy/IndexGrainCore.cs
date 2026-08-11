using CyberCloud.Core;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;

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
///     </list>
///     <para>
///         <b>The idempotence rule, which is what makes the retried <c>PUT</c> a no-op.</b> A claim
///         by the <i>same</i> id against a Claimed or Confirmed entry succeeds and changes nothing.
///         A claim by a different id is <c>ResourceAlreadyExists</c> — the gateway's <c>409</c>.
///     </para>
/// </remarks>
static class IndexClaimMachine
{
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
            ? new IndexEntry { State = IndexEntryState.Free, ModifiedAt = entry.ModifiedAt }
            : entry;

    /// <summary>Attempts the claim. Returns the new entry, or the conflict.</summary>
    public static Result<IndexEntry> TryClaim(
        IndexEntry current,
        Guid id,
        string indexedValue,
        DateTimeOffset now,
        string what)
    {
        var entry = Effective(current, now);

        if (entry.State != IndexEntryState.Free && entry.BoundTo != id)
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"{what} is already {(entry.State == IndexEntryState.Confirmed ? "taken" : "claimed")} "
                + $"by {entry.BoundTo:D}. docs/plan/06 § Two-phase create: a taken name is a "
                + "409 Conflict.");
        }

        if (entry.State == IndexEntryState.Confirmed)
        {
            // Same id, already confirmed: the retried PUT. A no-op, and NOT a re-lease — putting a
            // confirmed binding back under a lease would let it expire out from under a live
            // resource.
            return Result<IndexEntry>.Success(entry);
        }

        return Result<IndexEntry>.Success(new IndexEntry
        {
            State = IndexEntryState.Claimed,
            BoundTo = id,
            IndexedValue = indexedValue,
            LeaseExpiresAt = now + LeaseDuration,
            ModifiedAt = now,
        });
    }

    /// <summary>Converts a lease into a permanent binding.</summary>
    public static Result<IndexEntry> Confirm(
        IndexEntry current,
        Guid id,
        DateTimeOffset now,
        string what)
    {
        var entry = Effective(current, now);

        return entry switch
        {
            { State: IndexEntryState.Free } => Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} has no live claim to confirm — the lease expired, so the name is free "
                + "again and the create must start from step 1. docs/plan/06 § Two-phase create."),

            _ when entry.BoundTo != id => Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is claimed by {entry.BoundTo:D}, not by {id:D}."),

            { State: IndexEntryState.Confirmed } => Result<IndexEntry>.Success(entry),

            _ => Result<IndexEntry>.Success(entry with
            {
                State = IndexEntryState.Confirmed,
                LeaseExpiresAt = DateTimeOffset.MaxValue,
                ModifiedAt = now,
            }),
        };
    }

    /// <summary>Releases the binding so the name is immediately reusable.</summary>
    public static Result<IndexEntry> Release(
        IndexEntry current,
        Guid id,
        DateTimeOffset now,
        string what)
    {
        var entry = Effective(current, now);

        if (entry.State == IndexEntryState.Free)
        {
            // Releasing a free name is a no-op rather than an error: delete is re-driven from a
            // reminder (docs/plan/06 § Two-phase create) and the second drive must not fail.
            return Result<IndexEntry>.Success(entry);
        }

        return entry.BoundTo != id
            ? Result<IndexEntry>.Failure(
                ErrorCode.Conflict,
                $"{what} is bound to {entry.BoundTo:D}, not to {id:D}. Releasing it would hand "
                + "somebody else's name away.")
            : Result<IndexEntry>.Success(new IndexEntry
            {
                State = IndexEntryState.Free,
                ModifiedAt = now,
            });
    }
}
