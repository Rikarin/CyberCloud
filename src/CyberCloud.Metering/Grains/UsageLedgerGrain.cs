using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Metering.Grains;

/// <summary>
///     <see cref="IUsageLedgerGrain" /> — Coordinator, Durable, key <c>sub/{subscriptionId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="IUsageLedgerGrain" /> first: the absence of a mutator is the design, not
///     an oversight.</b> This class holds to it in the only two places it could be broken —
///     <see cref="AppendAsync" /> and <see cref="AppendCorrectionAsync" /> both call
///     <c>state.State.Entries.Add</c> and nothing else ever touches the list.
/// </remarks>
public sealed class UsageLedgerGrain(
    [PersistentState("usage-ledger", StorageTiers.Durable)] IPersistentState<UsageLedgerState> state,
    IClock clock
)
    : Grain, IUsageLedgerGrain {
    Guid subscriptionId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = MeteringGrainKeys.TenantOf(this);
        subscriptionId = MeteringGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<UsageLedgerEntry>> AppendAsync(UsageLedgerAppend append) {
        if (append is null) {
            return Result<UsageLedgerEntry>.Failure(ErrorCode.InvalidRequestBody, "An append needs a body.");
        }

        if (append.Meter == BillingMeter.Unknown) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A ledger entry names a meter. BillingMeter.Unknown is the zero value a "
                + "default-constructed wire type carries, not a meter."
            );
        }

        if (append.ResourceId == Guid.Empty) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidResourceId,
                "A ledger entry names a resource by GUID — docs/plan/06 § Identifiers."
            );
        }

        if (append.Quantity < 0) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A usage entry's quantity cannot be negative; {append.Quantity} is. Reducing a "
                + "recorded figure is AppendCorrectionAsync, which carries a reason and a link to "
                + "the original — docs/plan/22 § The pipeline."
            );
        }

        if (append.WindowEnd <= append.WindowStart) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A ledger entry's window must be positive and half-open; "
                + $"[{append.WindowStart:O}, {append.WindowEnd:O}) is not."
            );
        }

        // ⚠ THE LAST LINE OF DEFENCE, and it is here because this is the only object in the pipeline
        // from which nothing can be removed. UsageRollupGrain already deduplicates by idempotency
        // key; if that ever fails, a duplicate reaching an append-only ledger is permanent and the
        // only remedy is a correction entry a human has to justify. Refusing costs nothing when the
        // rollup is working and saves the unrecoverable case when it is not.
        foreach (var existing in state.State.Entries) {
            if (!existing.IsCorrection
                && existing.ResourceId == append.ResourceId
                && existing.Meter == append.Meter
                && existing.WindowStart == append.WindowStart
                && existing.WindowEnd == append.WindowEnd) {
                return Result<UsageLedgerEntry>.Failure(
                    ErrorCode.Conflict,
                    $"The ledger already holds entry #{existing.Sequence} for {append.Meter} on "
                    + $"resource {append.ResourceId:D} over [{append.WindowStart:O}, "
                    + $"{append.WindowEnd:O}). Nothing can be removed from this ledger, so a second "
                    + "entry for one window would be a permanent double count. If the first figure "
                    + "is wrong, correct it — AppendCorrectionAsync."
                );
            }
        }

        var entry = new UsageLedgerEntry {
            Sequence = state.State.NextSequence,
            EntryId = Guid.NewGuid(),
            TenantId = append.TenantId == Guid.Empty ? tenantId : append.TenantId,
            SubscriptionId = subscriptionId,
            ResourceId = append.ResourceId,
            ResourcePath = append.ResourcePath,
            Meter = append.Meter,
            Region = append.Region,
            WindowStart = append.WindowStart,
            WindowEnd = append.WindowEnd,
            Quantity = append.Quantity,
            SampleCount = append.SampleCount,
            RecordedAt = clock.UtcNow,
            CorrectsEntryId = null,
            Reason = string.Empty
        };

        state.State.NextSequence++;
        state.State.Entries.Add(entry);
        await state.WriteStateAsync();

        return Result<UsageLedgerEntry>.Success(entry);
    }

    /// <inheritdoc />
    public async Task<Result<UsageLedgerEntry>> AppendCorrectionAsync(
        Guid correctsEntryId,
        decimal delta,
        string reason
    ) {
        if (string.IsNullOrWhiteSpace(reason)) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A correction needs a reason. docs/plan/22 § The pipeline: \"Corrections are new "
                + "entries with a reason and a link to the original, never edits.\" A correction "
                + "with no reason is an edit wearing a hat — it changes the total and explains "
                + "nothing, which is exactly the thing an auditable ledger exists to prevent."
            );
        }

        if (delta == 0) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A correction of zero changes nothing and would leave an entry in the ledger that "
                + "says a figure was disputed and upheld. If that is what happened, it belongs in "
                + "the audit trail, not in the meter."
            );
        }

        UsageLedgerEntry? original = null;
        foreach (var candidate in state.State.Entries) {
            if (candidate.EntryId == correctsEntryId) {
                original = candidate;
                break;
            }
        }

        if (original is null) {
            return Result<UsageLedgerEntry>.Failure(
                ErrorCode.ResourceNotFound,
                $"There is no ledger entry {correctsEntryId:D} in subscription {subscriptionId:D}. "
                + "A correction must point at the entry it corrects — a correction pointing at "
                + "nothing is an unexplained adjustment, which is the shape of the thing this "
                + "ledger refuses to be."
            );
        }

        var entry = new UsageLedgerEntry {
            Sequence = state.State.NextSequence,
            EntryId = Guid.NewGuid(),
            TenantId = original.TenantId,
            SubscriptionId = subscriptionId,
            ResourceId = original.ResourceId,
            ResourcePath = original.ResourcePath,
            Meter = original.Meter,
            Region = original.Region,
            WindowStart = original.WindowStart,
            WindowEnd = original.WindowEnd,
            Quantity = delta,
            SampleCount = 0,
            RecordedAt = clock.UtcNow,
            CorrectsEntryId = original.EntryId,
            Reason = reason
        };

        state.State.NextSequence++;
        state.State.Entries.Add(entry);
        await state.WriteStateAsync();

        return Result<UsageLedgerEntry>.Success(entry);
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<UsageLedgerEntry>>> ListAsync() =>
        Task.FromResult(Result<ImmutableArray<UsageLedgerEntry>>.Success([.. state.State.Entries]));

    /// <inheritdoc />
    public Task<Result<ImmutableArray<UsageLedgerEntry>>> ListCorrectionsAsync(Guid entryId) =>
        Task.FromResult(
            Result<ImmutableArray<UsageLedgerEntry>>.Success(
                [.. state.State.Entries.Where(x => x.CorrectsEntryId == entryId)]
            )
        );

    /// <inheritdoc />
    public Task<Result<decimal>> NetQuantityAsync(BillingMeter meter) =>
        Task.FromResult(
            Result<decimal>.Success(state.State.Entries.Where(x => x.Meter == meter).Sum(x => x.Quantity))
        );

    /// <inheritdoc />
    public Task<Result<long>> CountAsync() => Task.FromResult(Result<long>.Success(state.State.Entries.Count));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
