using System.Collections.Immutable;

namespace CyberCloud.Metering.Tests;

/// <summary>
///     The rollup worker — dedup by idempotency key, then the hourly aggregate.
///     docs/plan/22 § The pipeline.
/// </summary>
[Collection(MeteringClusterFixture.Name)]
public sealed class RollupTests(MeteringCluster cluster) {
    static readonly Guid Widget = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1");
    static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb2");

    // ── FAILURE CLASS: a redelivered event collapses ───────────────────────────────────────────

    /// <summary>
    ///     docs/plan/22 § The pipeline: <i>"The key is deterministic … so a redelivery after a silo
    ///     restart collapses. NATS is at-least-once and this is the only correct answer to that."</i>
    /// </summary>
    [Fact]
    public async Task TheSameEventFedTwiceIsOneRow() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var record = Sample(subscription, Widget, TestClock.Start);
        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);

        var first = (await rollup.IngestAsync(record)).GetValueOrThrow();
        var second = (await rollup.IngestAsync(record)).GetValueOrThrow();

        first.Outcome.ShouldBe(UsageIngestOutcome.Accepted);
        second.Outcome.ShouldBe(UsageIngestOutcome.Duplicate);
        second.IdempotencyKey.ShouldBe(first.IdempotencyKey);

        (await rollup.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(1);
    }

    /// <summary>
    ///     ⚠ <b>And a different window is two rows.</b> The other half of the failure class, and the
    ///     more important one: a dedup that swallows genuine usage is worse than a duplicate, because
    ///     a duplicate is visible as an over-charge and a swallowed record is invisible forever.
    /// </summary>
    [Fact]
    public async Task ADifferentWindowIsTwoRows() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);

        var first = Sample(subscription, Widget, TestClock.Start);
        var next = Sample(subscription, Widget, TestClock.Start + UsageWindow.SamplePeriod);

        (await rollup.IngestAsync(first)).GetValueOrThrow().Outcome.ShouldBe(UsageIngestOutcome.Accepted);
        (await rollup.IngestAsync(next)).GetValueOrThrow().Outcome.ShouldBe(UsageIngestOutcome.Accepted);

        var pending = (await rollup.ListPendingAsync()).GetValueOrThrow();

        pending.Length.ShouldBe(2);
        pending.Select(x => x.IdempotencyKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
    }

    /// <summary>
    ///     A different resource in the same window is also two rows — the dedup is per (resource,
    ///     meter, window) and not per window.
    /// </summary>
    [Fact]
    public async Task ADifferentResourceInTheSameWindowIsTwoRows() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);

        _ = await rollup.IngestAsync(Sample(subscription, Widget, TestClock.Start));
        _ = await rollup.IngestAsync(Sample(subscription, Other, TestClock.Start));

        (await rollup.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(2);
    }

    /// <summary>
    ///     A batch carrying a redelivery beside new usage keeps the new and collapses the old — a
    ///     redelivery is per-record and not per-batch.
    /// </summary>
    [Fact]
    public async Task ABatchIsJudgedRecordByRecord() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var first = Sample(subscription, Widget, TestClock.Start);
        var next = Sample(subscription, Widget, TestClock.Start + UsageWindow.SamplePeriod);

        _ = await rollup.IngestAsync(first);

        var receipts = (await rollup.IngestAsync([first, next])).GetValueOrThrow();

        receipts.Length.ShouldBe(2);
        receipts[0].Outcome.ShouldBe(UsageIngestOutcome.Duplicate);
        receipts[1].Outcome.ShouldBe(UsageIngestOutcome.Accepted);

        (await rollup.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(2);
    }

    /// <summary>
    ///     ⚠ Deactivation is the closest this suite gets to a silo restart, and it is the scenario
    ///     docs/plan/22 § The pipeline names. The dedup is durable state, so it survives.
    /// </summary>
    [Fact]
    public async Task ARedeliveryAfterDeactivationStillCollapses() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var record = Sample(subscription, Widget, TestClock.Start);
        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);

        _ = await rollup.IngestAsync(record);
        await rollup.DeactivateAsync();

        var afterRestart = cluster.Rollup(MeteringCluster.Tenant, subscription);

        (await afterRestart.IngestAsync(record)).GetValueOrThrow()
            .Outcome.ShouldBe(UsageIngestOutcome.Duplicate);

        (await afterRestart.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(1);
    }

    /// <summary>
    ///     A record whose key does not match its contents is refused. Accepting one breaks dedup in
    ///     whichever direction the wrong key happens to point.
    /// </summary>
    /// <remarks>
    ///     ⚠ The forgery has to touch a <i>key component</i>. Rewriting the quantity would not be
    ///     detected and must not be — quantity is deliberately outside the key, so that a re-sample
    ///     that saw a resized disk still collapses instead of double-billing the resize
    ///     (<c>IdempotencyKeyTests.QuantityIsNotPartOfTheKey</c>). Rewriting the resource is the real
    ///     hazard: a record carrying another resource's key would discard that resource's usage.
    /// </remarks>
    [Fact]
    public async Task ARecordWithAnInconsistentKeyIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var forged = Sample(subscription, Widget, TestClock.Start) with { ResourceId = Other };

        var refused = await cluster.Rollup(MeteringCluster.Tenant, subscription).IngestAsync(forged);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("idempotency key");
    }

    /// <summary>
    ///     ⚠ The complement, asserted at the rollup rather than only on the type: a re-sample of one
    ///     window that read a <i>different</i> quantity still collapses. The window is the unit of
    ///     account, so two readings of it must not sum — that would double-bill every resize.
    /// </summary>
    [Fact]
    public async Task AReSampleOfOneWindowWithADifferentQuantityStillCollapses() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var first = Sample(subscription, Widget, TestClock.Start);
        var resized = first with { Quantity = first.Quantity * 2m };

        (await rollup.IngestAsync(first)).GetValueOrThrow().Outcome.ShouldBe(UsageIngestOutcome.Accepted);
        (await rollup.IngestAsync(resized)).GetValueOrThrow().Outcome.ShouldBe(UsageIngestOutcome.Duplicate);

        var pending = (await rollup.ListPendingAsync()).GetValueOrThrow();

        pending.Length.ShouldBe(1);
        pending[0].Quantity.ShouldBe(first.Quantity);
    }

    /// <summary>
    ///     ⚠ A record naming another subscription is refused rather than re-homed: silently billing
    ///     it here would be a cross-tenant charge, and re-homing it would make the rollup a router.
    /// </summary>
    [Fact]
    public async Task ARecordForAnotherSubscriptionIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var stray = Sample(Fresh(), Widget, TestClock.Start);

        var refused = await cluster.Rollup(MeteringCluster.Tenant, subscription).IngestAsync(stray);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("cross-tenant charge");
    }

    // ── The hourly aggregate ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Twelve five-minute samples become one hourly row whose quantity is their sum — the
    ///     <c>usage_hourly</c> of docs/plan/22 § The pipeline.
    /// </summary>
    [Fact]
    public async Task AnHourOfSamplesBecomesOneAggregate() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        var expected = 0m;
        for (var i = 0; i < 12; i++) {
            var record = Sample(subscription, Widget, hour.Start + (UsageWindow.SamplePeriod * i));
            expected += record.Quantity;
            _ = await rollup.IngestAsync(record);
        }

        var closed = (await rollup.CloseHourAsync(hour.Start)).GetValueOrThrow();

        closed.Length.ShouldBe(1);
        closed[0].SampleCount.ShouldBe(12);
        closed[0].Quantity.ShouldBe(expected);
        closed[0].HourStart.ShouldBe(hour.Start);
        closed[0].HourEnd.ShouldBe(hour.End);

        // The aggregate reached both destinations, and the raw records reached the sink.
        TestSink.Instance.Hourly.Count(x => x.ResourceId == Widget).ShouldBe(1);
        TestSink.Instance.Raw.Count(x => x.ResourceId == Widget).ShouldBe(12);

        // ⚠ And the hour's raw records are gone from grain state, because both durable destinations
        // have them. The seen keys are not — they stay for the retention horizon.
        (await rollup.ListPendingAsync()).GetValueOrThrow().ShouldBeEmpty();

        var ledger = (await cluster.Ledger(MeteringCluster.Tenant, subscription).ListAsync()).GetValueOrThrow();
        ledger.Length.ShouldBe(1);
        ledger[0].Quantity.ShouldBe(expected);
        ledger[0].SampleCount.ShouldBe(12);
    }

    /// <summary>
    ///     ⚠ A redelivery is still collapsed <i>after</i> its hour has been settled and its raw
    ///     records dropped. The key set outlives the records precisely so this holds.
    /// </summary>
    [Fact]
    public async Task ARedeliveryAfterTheHourClosedStillCollapses() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);
        var record = Sample(subscription, Widget, hour.Start);

        _ = await rollup.IngestAsync(record);
        _ = (await rollup.CloseHourAsync(hour.Start)).GetValueOrThrow();

        (await rollup.IngestAsync(record)).GetValueOrThrow().Outcome.ShouldBe(UsageIngestOutcome.Duplicate);

        (await rollup.ListPendingAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await cluster.Ledger(MeteringCluster.Tenant, subscription).CountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    /// <summary>
    ///     Re-closing a settled hour is refused — it would append the same aggregate to an
    ///     append-only ledger a second time, and nothing can remove it afterwards.
    /// </summary>
    [Fact]
    public async Task ClosingASettledHourIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        _ = await rollup.IngestAsync(Sample(subscription, Widget, hour.Start));
        _ = await rollup.CloseHourAsync(hour.Start);

        var refused = await rollup.CloseHourAsync(hour.Start);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await cluster.Ledger(MeteringCluster.Tenant, subscription).CountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    /// <summary>An unsnapped hour boundary is a caller who guessed, and it is refused rather than fixed.</summary>
    [Fact]
    public async Task AnUnsnappedHourBoundaryIsRefused() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var refused = await cluster.Rollup(MeteringCluster.Tenant, subscription)
            .CloseHourAsync(TestClock.Start.AddMinutes(7));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("not an hour boundary");
    }

    /// <summary>
    ///     ⚠ <b>The sink outage.</b> A failed sink write leaves the hour open and the records in
    ///     durable grain state — the whole reason <see cref="IUsageSink" /> returns a
    ///     <see cref="Result" /> rather than <c>void</c>. Nothing is lost; the next reminder retries.
    /// </summary>
    [Fact]
    public async Task ASinkOutageLeavesTheHourOpenRatherThanLosingIt() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        _ = await rollup.IngestAsync(Sample(subscription, Widget, hour.Start));

        TestSink.Instance.Fail = true;
        var failed = await rollup.CloseHourAsync(hour.Start);
        failed.IsFailure.ShouldBeTrue();

        // The record is still here.
        (await rollup.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(1);

        // ⚠ And the retry succeeds without double-appending to the ledger: the first attempt
        // committed the ledger entry before the sink failed, so the re-drive finds it already there
        // and carries on rather than treating the Conflict as an error.
        TestSink.Instance.Fail = false;
        var recovered = (await rollup.CloseHourAsync(hour.Start)).GetValueOrThrow();

        recovered.Length.ShouldBe(1);
        (await rollup.ListPendingAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await cluster.Ledger(MeteringCluster.Tenant, subscription).CountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    /// <summary>
    ///     ⚠ The current hour is never closed. A sample for [13:55, 14:00) arrives at 14:00, and an
    ///     hour settled the instant it ended would settle before its last sample landed.
    /// </summary>
    [Fact]
    public async Task CloseElapsedHoursLeavesTheCurrentHourOpen() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        _ = await rollup.IngestAsync(Sample(subscription, Widget, hour.Start));

        (await rollup.CloseElapsedHoursAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await rollup.ListPendingAsync()).GetValueOrThrow().Length.ShouldBe(1);

        TestClock.Instance.Advance(TimeSpan.FromHours(1));

        (await rollup.CloseElapsedHoursAsync()).GetValueOrThrow().Length.ShouldBe(1);
        (await rollup.ListPendingAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    /// <summary>
    ///     Two resources and two meters in one hour produce one row each, grouped by (resource,
    ///     meter) — not merged into a subscription total.
    /// </summary>
    [Fact]
    public async Task AggregationGroupsByResourceAndMeter() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        _ = await rollup.IngestAsync(Sample(subscription, Widget, hour.Start));
        _ = await rollup.IngestAsync(Sample(subscription, Other, hour.Start));
        _ = await rollup.IngestAsync(Sample(subscription, Widget, hour.Start, BillingMeter.MemoryGbHours));

        var closed = (await rollup.CloseHourAsync(hour.Start)).GetValueOrThrow();

        closed.Length.ShouldBe(3);
        closed.ShouldAllBe(x => x.SampleCount == 1);
        closed.Select(x => (x.ResourceId, x.Meter)).Distinct().Count().ShouldBe(3);
    }

    /// <summary>
    ///     An hour with no usage still settles. A subscription that genuinely did nothing must be
    ///     distinguishable afterwards from one whose rollup never ran.
    /// </summary>
    [Fact]
    public async Task AnEmptyHourSettlesToNothingAndIsClosed() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var rollup = cluster.Rollup(MeteringCluster.Tenant, subscription);
        var hour = UsageWindow.HourAt(TestClock.Start);

        (await rollup.CloseHourAsync(hour.Start)).GetValueOrThrow().ShouldBeEmpty();
        (await rollup.CloseHourAsync(hour.Start)).Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    static UsageEvent Sample(
        Guid subscription,
        Guid resource,
        DateTimeOffset at,
        BillingMeter meter = BillingMeter.VCpuHours
    ) {
        var window = UsageWindow.SampleAt(at);

        return UsageEvent.ForSample(
                MeteringCluster.Tenant,
                subscription,
                resource,
                "/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Sample/widgets/w",
                meter,
                MeterCatalog.Accrue(meter, 4m, window.Length).GetValueOrThrow(),
                window,
                "eu-central",
                at
            )
            .GetValueOrThrow();
    }

    static Guid Fresh() => Guid.NewGuid();
}
