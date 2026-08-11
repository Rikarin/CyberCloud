using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Metering.Grains;

/// <summary>
///     <see cref="IUsageRollupGrain" /> — Coordinator, Durable, key <c>sub/{subscriptionId:N}</c>.
///     docs/plan/22 § The pipeline's rollup worker.
/// </summary>
public sealed class UsageRollupGrain(
    [PersistentState("usage-rollup", StorageTiers.Durable)] IPersistentState<UsageRollupState> state,
    IUsageSink sink,
    IGrainFactory grains,
    IClock clock
)
    : Grain, IUsageRollupGrain, IRemindable {
    /// <summary>The reminder's name. One per subscription.</summary>
    public const string ReminderName = "usage-rollup";

    /// <summary>
    ///     How long an idempotency key is remembered.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Seven days, matching docs/plan/22 § The pipeline's own transport retention</b>
    ///     ("NATS <c>cc.{tenant}.usage.{meter}</c> — JetStream, durable, 7-day retention"). The
    ///     bound is what makes this grain's state finite, and the argument for it is that a
    ///     redelivery cannot outlive the buffer that would redeliver it. Beyond the horizon the
    ///     second defence takes over: <see cref="CloseHourAsync" /> refuses an hour that has already
    ///     settled, so a very late record produces a conflict a human resolves with a ledger
    ///     correction rather than a silent second count.
    /// </remarks>
    public static readonly TimeSpan DedupRetention = TimeSpan.FromDays(7);

    /// <summary>
    ///     The close reminder's period. docs/plan/04 § Reminders, "Rollups — metering aggregation
    ///     windows".
    /// </summary>
    public static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(15);

    Guid subscriptionId;
    Guid tenantId;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = MeteringGrainKeys.TenantOf(this);
        subscriptionId = MeteringGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;

        // A grain that was running before it was deactivated is running again. The reminder survives
        // the activation, but re-registering is idempotent and covers the case where it did not.
        if (state.State.Running) {
            await EnsureReminderAsync();
        }
    }

    /// <inheritdoc />
    public async Task<Result<UsageIngestReceipt>> IngestAsync(UsageEvent usage) {
        var one = await IngestAsync(usage is null ? [] : [usage]);

        if (one.TryGetError(out var error)) {
            return Result<UsageIngestReceipt>.Failure(error);
        }

        var receipts = one.GetValueOrThrow();
        return receipts.Length == 1
            ? Result<UsageIngestReceipt>.Success(receipts[0])
            : Result<UsageIngestReceipt>.Failure(ErrorCode.InvalidRequestBody, "An ingest needs a record.");
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<UsageIngestReceipt>>> IngestAsync(ImmutableArray<UsageEvent> usage) {
        if (usage.IsDefaultOrEmpty) {
            return Result<ImmutableArray<UsageIngestReceipt>>.Success([]);
        }

        var now = clock.UtcNow;
        var receipts = ImmutableArray.CreateBuilder<UsageIngestReceipt>(usage.Length);
        var changed = Prune(now);

        foreach (var record in usage) {
            var checkedRecord = Validate(record);
            if (checkedRecord.TryGetError(out var error)) {
                return Result<ImmutableArray<UsageIngestReceipt>>.Failure(error);
            }

            // ── The dedup. docs/plan/22 § The pipeline: "a redelivery after a silo restart
            // collapses". Delivery is at-least-once (docs/plan/04 § Streams), so this branch is the
            // expected path after a restart and not an error — see UsageIngestOutcome.Duplicate.
            if (state.State.SeenKeys.ContainsKey(record.IdempotencyKey)) {
                receipts.Add(new(UsageIngestOutcome.Duplicate, record.IdempotencyKey));
                continue;
            }

            state.State.SeenKeys[record.IdempotencyKey] = record.WindowEnd;
            state.State.Pending.Add(record);
            changed = true;

            receipts.Add(new(UsageIngestOutcome.Accepted, record.IdempotencyKey));
        }

        // ⚠ THE KEY IS RECORDED AND THE STATE IS WRITTEN BEFORE THE CALLER IS TOLD IT LANDED. If the
        // write throws, the caller sees a failure, retries, and the retry is accepted as new —
        // because the key was never persisted. The other order (reply, then write) would tell an
        // emitter the record is safe while it is only in memory, and a silo lost between the two
        // would lose usage that "cannot be recovered" (docs/plan/22 § Effort).
        if (changed) {
            await state.WriteStateAsync();
        }

        return Result<ImmutableArray<UsageIngestReceipt>>.Success(receipts.DrainToImmutable());
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<UsageAggregate>>> CloseHourAsync(DateTimeOffset hourStart) {
        var hour = UsageWindow.HourAt(hourStart);

        if (hour.Start != hourStart.ToUniversalTime()) {
            return Result<ImmutableArray<UsageAggregate>>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{hourStart:O} is not an hour boundary. Snap it with UsageWindow.HourAt — an hour "
                + "that is snapped for you here is a caller who guessed, and a rollup boundary "
                + "nobody agrees on is how two silos settle the same usage twice."
            );
        }

        if (state.State.ClosedHours.Contains(hour.Start)) {
            return Result<ImmutableArray<UsageAggregate>>.Failure(
                ErrorCode.Conflict,
                $"The hour {hour} has already been settled for subscription {subscriptionId:D}. "
                + "Re-closing it would append its aggregate to an append-only ledger a second time, "
                + "and nothing can remove it afterwards. A record that arrived after settlement is a "
                + "ledger correction with a reason — IUsageLedgerGrain.AppendCorrectionAsync."
            );
        }

        var forHour = state.State.Pending.Where(x => x.WindowStart >= hour.Start && x.WindowEnd <= hour.End).ToList();

        if (forHour.Count == 0) {
            // An hour with no usage still settles: a subscription that genuinely did nothing must be
            // distinguishable afterwards from one whose rollup never ran, and the closed-hours list
            // is what says which.
            state.State.ClosedHours.Add(hour.Start);
            await state.WriteStateAsync();
            return Result<ImmutableArray<UsageAggregate>>.Success([]);
        }

        var aggregates = Aggregate(forHour, hour);

        // ── 1. The ledger, which is the record of account and therefore commits first.
        var ledger = grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscriptionId));

        foreach (var aggregate in aggregates) {
            var appended = await ledger.AppendAsync(
                new() {
                    TenantId = aggregate.TenantId,
                    ResourceId = aggregate.ResourceId,
                    ResourcePath = aggregate.ResourcePath,
                    Meter = aggregate.Meter,
                    Region = aggregate.Region,
                    WindowStart = aggregate.HourStart,
                    WindowEnd = aggregate.HourEnd,
                    Quantity = aggregate.Quantity,
                    SampleCount = aggregate.SampleCount
                }
            );

            // ⚠ A Conflict here means the ledger already holds this hour for this (resource, meter),
            // which means a previous close committed the ledger and then failed before dropping the
            // raw records. Re-driving must not fail on it: the entry we wanted is already there, and
            // treating it as an error would strand the hour open forever.
            if (appended.TryGetError(out var ledgerError) && ledgerError.Code != ErrorCode.Conflict) {
                return Result<ImmutableArray<UsageAggregate>>.Failure(ledgerError);
            }
        }

        // ── 2. The sink — the analytical copy. docs/plan/22 § The pipeline's usage_raw and
        // usage_hourly. A failure here leaves the hour open and the records in durable state, and
        // the next reminder retries. That is why IUsageSink returns Result rather than void.
        var raw = await sink.WriteRawAsync([.. forHour]);
        if (raw.TryGetError(out var rawError)) {
            return Result<ImmutableArray<UsageAggregate>>.Failure(rawError);
        }

        var hourly = await sink.WriteHourlyAsync(aggregates);
        if (hourly.TryGetError(out var hourlyError)) {
            return Result<ImmutableArray<UsageAggregate>>.Failure(hourlyError);
        }

        // ── 3. Only now. The keys stay in SeenKeys for the whole retention horizon; it is the raw
        // records that go, and they go because both durable destinations have them.
        state.State.Pending.RemoveAll(x => x.WindowStart >= hour.Start && x.WindowEnd <= hour.End);
        state.State.ClosedHours.Add(hour.Start);
        await state.WriteStateAsync();

        return Result<ImmutableArray<UsageAggregate>>.Success(aggregates);
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<UsageAggregate>>> CloseElapsedHoursAsync() {
        // ⚠ The CURRENT hour is never closed. A five-minute sample of [13:55, 14:00) is emitted at
        // 14:00; closing 13:00 at 13:59 would settle an hour whose last sample has not been taken.
        var currentHour = UsageWindow.HourAt(clock.UtcNow).Start;

        var open = state.State.Pending
            .Select(x => UsageWindow.HourAt(x.WindowStart).Start)
            .Where(x => x < currentHour)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var written = ImmutableArray.CreateBuilder<UsageAggregate>();

        foreach (var hour in open) {
            var closed = await CloseHourAsync(hour);

            if (closed.TryGetError(out var error)) {
                // Conflict means somebody already settled it — the hour is done and the loop moves
                // on. Anything else stops: a sink that is down will be down for the next hour too,
                // and closing later hours first would settle them out of order.
                if (error.Code == ErrorCode.Conflict) {
                    continue;
                }

                return Result<ImmutableArray<UsageAggregate>>.Failure(error);
            }

            written.AddRange(closed.GetValueOrThrow());
        }

        return Result<ImmutableArray<UsageAggregate>>.Success(written.DrainToImmutable());
    }

    /// <inheritdoc />
    public async Task<Result> StartAsync() {
        state.State.Running = true;
        await state.WriteStateAsync();
        await EnsureReminderAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> StopAsync() {
        state.State.Running = false;
        await state.WriteStateAsync();

        var reminder = await this.GetReminder(ReminderName);
        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<UsageEvent>>> ListPendingAsync() =>
        Task.FromResult(Result<ImmutableArray<UsageEvent>>.Success([.. state.State.Pending]));

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal)) {
            return;
        }

        _ = await CloseElapsedHoursAsync();
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Everything a record must be before its key is trusted.
    /// </summary>
    /// <remarks>
    ///     ⚠ The key check is the important one. A record whose <c>IdempotencyKey</c> is not the one
    ///     its own contents produce cannot be deduplicated correctly in either direction — a key
    ///     that collides with an unrelated record silently discards this one, and a key that
    ///     collides with nothing lets a redelivery in as new usage.
    /// </remarks>
    Result Validate(UsageEvent record) {
        if (record is null) {
            return Result.Failure(ErrorCode.InvalidRequestBody, "An ingest needs a record.");
        }

        if (record.SubscriptionId != subscriptionId) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"This rollup is subscription {subscriptionId:D} and the record names "
                + $"{record.SubscriptionId:D}. A record billed to the wrong subscription is a "
                + "cross-tenant charge, so it is refused rather than re-homed."
            );
        }

        if (record.TenantId != tenantId) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"This rollup is tenant {tenantId:D} and the record names {record.TenantId:D}."
            );
        }

        if (record.Meter == BillingMeter.Unknown) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "A usage record names a meter. BillingMeter.Unknown is the zero value a "
                + "default-constructed wire type carries, not a meter."
            );
        }

        if (record.Quantity < 0) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"A usage quantity cannot be negative; {record.Quantity} is. Unwinding usage is a "
                + "ledger correction — docs/plan/22 § The pipeline."
            );
        }

        if (!record.IsKeyConsistent()) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The record's idempotency key is not the one its contents produce. docs/plan/22 "
                + "§ The pipeline makes the key deterministic — sha256(resourceId | meter | "
                + "windowStart | windowEnd) — precisely so a redelivery collapses; a key that does "
                + "not match its record breaks dedup in whichever direction it happens to point. "
                + "Build records with UsageEvent.ForSample or UsageEvent.ForEvent, never with an "
                + "object initialiser."
            );
        }

        return Result.Success;
    }

    /// <summary>Groups one hour's raw records into <c>usage_hourly</c> rows.</summary>
    /// <remarks>
    ///     The grouping key is (resource, meter) and <b>not</b> the region or the path: both are
    ///     carried from the last record in the hour, because a resource that moved region mid-hour
    ///     is a thing that does not happen and a resource that was renamed mid-hour is, and the
    ///     rename must not split one hour into two invoice lines.
    /// </remarks>
    ImmutableArray<UsageAggregate> Aggregate(List<UsageEvent> records, UsageWindow hour) =>
        [
            .. records
                .GroupBy(x => (x.ResourceId, x.Meter))
                .OrderBy(x => x.Key.ResourceId)
                .ThenBy(x => x.Key.Meter)
                .Select(group => {
                        var last = group.OrderBy(x => x.WindowStart).Last();

                        return new UsageAggregate {
                            TenantId = tenantId,
                            SubscriptionId = subscriptionId,
                            ResourceId = group.Key.ResourceId,
                            ResourcePath = last.ResourcePath,
                            Meter = group.Key.Meter,
                            Region = last.Region,
                            HourStart = hour.Start,
                            HourEnd = hour.End,
                            Quantity = group.Sum(x => x.Quantity),
                            SampleCount = group.Count()
                        };
                    }
                )
        ];

    /// <summary>Drops keys and closed hours past the retention horizon. Returns whether anything went.</summary>
    bool Prune(DateTimeOffset now) {
        var horizon = now - DedupRetention;

        var deadKeys = state.State.SeenKeys.Where(x => x.Value < horizon).Select(x => x.Key).ToList();
        foreach (var key in deadKeys) {
            state.State.SeenKeys.Remove(key);
        }

        var removedHours = state.State.ClosedHours.RemoveAll(x => x + UsageWindow.RollupPeriod < horizon);

        return deadKeys.Count > 0 || removedHours > 0;
    }

    async Task EnsureReminderAsync() =>
        await this.RegisterOrUpdateReminder(ReminderName, ReminderPeriod, ReminderPeriod);
}
