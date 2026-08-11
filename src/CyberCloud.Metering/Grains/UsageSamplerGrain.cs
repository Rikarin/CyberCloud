using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Metering.Grains;

/// <summary>
///     <see cref="IUsageSamplerGrain" /> — Worker, Hot, key <c>sub/{subscriptionId:N}</c>.
///     docs/plan/22 § Two kinds of meter's "5-minute sampler over the resource-graph projection".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything this grain meters comes from <see cref="IMeteredResourceSource" />, which
///         is a view of what <i>exists</i>.</b> docs/plan/22 § Two kinds of meter: "A stopped VM
///         still has a disk; a <c>Deployment</c> scaled to zero still has a
///         <c>PersistentVolumeClaim</c>. Metrics know about running pods; the resource graph knows
///         what exists." <see cref="MeteredResource.RunState" /> is never read here — deliberately,
///         and asserted by <c>StateBasedMetersIgnoreRunState</c>.
///     </para>
///     <para>
///         ⚠ <b>Reminder period versus sample period.</b> Orleans' minimum reminder period is one
///         minute and the sample window is five, so the reminder fires on the window period directly.
///         A reminder that fires twice, or a reminder that fires late and lands in the same window as
///         the last one, produces the same window and therefore the same keys, and the rollup
///         collapses the second pass. That is the whole of the "runs twice, one record" property and
///         it needs no lock.
///     </para>
/// </remarks>
public sealed class UsageSamplerGrain(
    [PersistentState("usage-sampler", StorageTiers.Hot)] IPersistentState<UsageSamplerState> state,
    IMeteredResourceSource resources,
    IUsageEmitter emitter,
    IClock clock
)
    : Grain, IUsageSamplerGrain, IRemindable {
    /// <summary>The reminder's name. One per subscription.</summary>
    public const string ReminderName = "usage-sampler";

    Guid subscriptionId;
    Guid tenantId;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = MeteringGrainKeys.TenantOf(this);
        subscriptionId = MeteringGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;

        if (state.State.Running) {
            await EnsureReminderAsync();
        }
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
    public async Task<Result<bool>> IsRunningAsync() =>
        Result<bool>.Success(await this.GetReminder(ReminderName) is not null);

    /// <inheritdoc />
    public async Task<Result<UsageSampleReport>> SampleAsync() {
        var now = clock.UtcNow;

        // ⚠ THE SNAP. The window is the five-minute slot containing now, anchored at the Unix epoch —
        // not "now minus five minutes". Two passes inside one slot therefore compute one window,
        // produce one set of idempotency keys, and the second collapses at the rollup. Using
        // clock.UtcNow as the window start instead would make every pass a new record, and
        // docs/plan/22's dedup would never fire on anything.
        var window = UsageWindow.SampleAt(now);

        var listed = await resources.ListAsync(tenantId, subscriptionId);

        // ⚠ A source failure PROPAGATES. Recording an empty pass would write zero usage for a window
        // that may have had plenty, and afterwards a zero is indistinguishable from an idle
        // subscription — docs/plan/22 § Effort: "usage that was never recorded cannot be recovered".
        if (listed.TryGetError(out var sourceError)) {
            return Result<UsageSampleReport>.Failure(sourceError);
        }

        var found = listed.GetValueOrThrow();
        var records = ImmutableArray.CreateBuilder<UsageEvent>();

        foreach (var resource in found) {
            if (resource.ResourceId == Guid.Empty || resource.Quantities.IsDefaultOrEmpty) {
                continue;
            }

            foreach (var quantity in resource.Quantities) {
                // The vocabulary bridge, and the only place it is crossed: the source reports stocks
                // in the QuotaMeter families the registry declared and the write path reserved, and
                // MeterCatalog says which billing meters integrate each. One declaration, two uses —
                // see MeterCatalog on why this is a derivation rather than a second declaration.
                foreach (var meter in MeterCatalog.MetersOf(quantity.Family)) {
                    // ⚠ Only the meters a state-based sampler owns. An event-based meter reaching
                    // this loop would be sampled, and sampling "would miss everything between
                    // samples" (docs/plan/22 § Two kinds of meter). MeterCatalog.MetersOf never
                    // returns one today because event-based meters have no family; the guard is here
                    // because the day one acquires a family is the day this loop silently starts
                    // under-counting requests.
                    if (MeterCatalog.KindOf(meter) != MeterKind.StateBased) {
                        continue;
                    }

                    // ⚠ BackupGbMonths shares QuotaMeter.StorageGb with StorageGbMonths, so a naive
                    // derivation would bill every provisioned gibibyte twice — once as storage and
                    // once as backup. Only the primary meter of a family accrues from the stock; a
                    // secondary meter is a provider's own split and reaches the pipeline as an
                    // explicit emission. See MeterCatalog.MetersOf, which returns both in
                    // declaration order.
                    if (MeterCatalog.MetersOf(quantity.Family)[0] != meter) {
                        continue;
                    }

                    var accrued = MeterCatalog.Accrue(meter, quantity.Amount, window.Length);
                    if (accrued.TryGetError(out var accrualError)) {
                        return Result<UsageSampleReport>.Failure(accrualError);
                    }

                    var built = UsageEvent.ForSample(
                        tenantId,
                        subscriptionId,
                        resource.ResourceId,
                        resource.ResourcePath,
                        meter,
                        accrued.GetValueOrThrow(),
                        window,
                        resource.Region,
                        now
                    );

                    if (built.TryGetError(out var buildError)) {
                        return Result<UsageSampleReport>.Failure(buildError);
                    }

                    records.Add(built.GetValueOrThrow());
                }
            }
        }

        var emitted = records.DrainToImmutable();
        var receipts = await emitter.EmitAsync(emitted);

        if (receipts.TryGetError(out var emitError)) {
            return Result<UsageSampleReport>.Failure(emitError);
        }

        var outcomes = receipts.GetValueOrThrow();

        state.State.LastWindowStart = window.Start;
        state.State.Passes++;
        await state.WriteStateAsync();

        return Result<UsageSampleReport>.Success(
            new() {
                WindowStart = window.Start,
                WindowEnd = window.End,
                ResourcesSeen = found.Length,
                Emitted = emitted.Length,
                Accepted = outcomes.Count(x => x.Outcome == UsageIngestOutcome.Accepted),
                Duplicates = outcomes.Count(x => x.Outcome == UsageIngestOutcome.Duplicate)
            }
        );
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal)) {
            return;
        }

        _ = await SampleAsync();
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    async Task EnsureReminderAsync() =>
        await this.RegisterOrUpdateReminder(ReminderName, UsageWindow.SamplePeriod, UsageWindow.SamplePeriod);
}
