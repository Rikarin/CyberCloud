namespace CyberCloud.Metering.Tests;

/// <summary>
///     The per-subscription sampler — docs/plan/22 § Two kinds of meter.
/// </summary>
[Collection(MeteringClusterFixture.Name)]
public sealed class SamplerTests(MeteringCluster cluster) {
    static readonly Guid Widget = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1");
    static readonly Guid Volume = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa2");

    // ── FAILURE CLASS: a sampler that runs twice produces one record ───────────────────────────

    /// <summary>
    ///     docs/plan/22 § Two kinds of meter: <i>"The sampler … emits one event per (resource, meter,
    ///     window) with a deterministic key — so a sampler that runs twice produces one record."</i>
    /// </summary>
    /// <remarks>
    ///     ⚠ Time is <b>not</b> advanced between the two passes, which is the point: both compute the
    ///     same five-minute window, therefore the same idempotency key, therefore the second is a
    ///     duplicate. Note this is not a lock and not a cursor check — the second pass genuinely runs,
    ///     genuinely reads the resource graph and genuinely emits, and the collapse happens on
    ///     arithmetic.
    /// </remarks>
    [Fact]
    public async Task ASamplerThatRunsTwiceProducesOneRecord() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 4m));

        var sampler = cluster.Sampler(MeteringCluster.Tenant, subscription);

        var first = (await sampler.SampleAsync()).GetValueOrThrow();
        var second = (await sampler.SampleAsync()).GetValueOrThrow();

        first.Emitted.ShouldBe(1);
        first.Accepted.ShouldBe(1);
        first.Duplicates.ShouldBe(0);

        // The second pass DID run — it read the source and built a record.
        second.Emitted.ShouldBe(1);
        second.Accepted.ShouldBe(0);
        second.Duplicates.ShouldBe(1);
        second.Window.ShouldBe(first.Window);

        // ── One row.
        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        pending.Length.ShouldBe(1);
        pending[0].Meter.ShouldBe(BillingMeter.VCpuHours);
    }

    /// <summary>
    ///     ⚠ The other half. A sampler that runs in the <i>next</i> window must produce a second
    ///     record — a dedup that swallowed it would silently bill one twelfth of an hour per hour.
    /// </summary>
    [Fact]
    public async Task ASamplerThatRunsInTheNextWindowProducesASecondRecord() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 4m));

        var sampler = cluster.Sampler(MeteringCluster.Tenant, subscription);

        _ = (await sampler.SampleAsync()).GetValueOrThrow();
        TestClock.Instance.Advance(UsageWindow.SamplePeriod);
        var second = (await sampler.SampleAsync()).GetValueOrThrow();

        second.Accepted.ShouldBe(1);
        second.Duplicates.ShouldBe(0);
        second.Window.ShouldNotBe(UsageWindow.SampleAt(TestClock.Start));

        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        pending.Length.ShouldBe(2);
        pending.Select(x => x.IdempotencyKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
    }

    /// <summary>
    ///     Three passes spread across one window collapse to one, which is what a reminder that fires
    ///     early, late and again looks like in production.
    /// </summary>
    [Fact]
    public async Task PassesSpreadAcrossOneWindowStillProduceOneRecord() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 2m));

        var sampler = cluster.Sampler(MeteringCluster.Tenant, subscription);

        // TestClock.Start is 12:03:17, so the window is [12:00, 12:05). All three land inside it.
        _ = await sampler.SampleAsync();
        TestClock.Instance.Advance(TimeSpan.FromSeconds(30));
        _ = await sampler.SampleAsync();
        TestClock.Instance.Advance(TimeSpan.FromSeconds(60));
        _ = await sampler.SampleAsync();

        (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow()
            .Length.ShouldBe(1);
    }

    // ── FAILURE CLASS: state-based meters see a resource that exists and is not running ────────

    /// <summary>
    ///     docs/plan/22 § Two kinds of meter: <i>"A stopped VM still has a disk; a
    ///     <c>Deployment</c> scaled to zero still has a <c>PersistentVolumeClaim</c>. Metrics know
    ///     about running pods; the resource graph knows what exists. Getting this backwards
    ///     under-bills storage."</i>
    /// </summary>
    /// <remarks>
    ///     ⚠ The assertion is <b>byte-identical output</b> across every run state there is, not
    ///     merely "non-zero when stopped". A sampler that billed a stopped resource at a discount
    ///     would pass a non-zero check and still be the bug.
    /// </remarks>
    [Theory]
    [InlineData(ObservedRunState.Running)]
    [InlineData(ObservedRunState.Stopped)]
    [InlineData(ObservedRunState.ScaledToZero)]
    [InlineData(ObservedRunState.Unknown)]
    public async Task StateBasedMetersIgnoreRunState(ObservedRunState runState) {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        // A 100 GiB volume. The disk exists whatever the workload is doing.
        FakeResourceSource.Hold(
            subscription,
            MeteringCluster.Resource(Volume, QuotaMeter.StorageGb, 100m, runState, "data")
        );

        _ = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow();

        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        pending.Length.ShouldBe(1);
        pending[0].Meter.ShouldBe(BillingMeter.StorageGbMonths);
        pending[0].Quantity.ShouldBe(
            MeterCatalog.Accrue(BillingMeter.StorageGbMonths, 100m, UsageWindow.SamplePeriod).GetValueOrThrow()
        );

        // ⚠ And the quantity is the same one a running resource produces. Same window, same stock,
        // same figure — the run state is not an input.
        pending[0].Quantity.ShouldBeGreaterThan(0m);
    }

    /// <summary>
    ///     ⚠ The same property stated as an identity: two subscriptions differing only in run state
    ///     produce records whose <i>idempotency keys</i> and quantities match. If run state ever
    ///     leaked into the sampler, this is the test that would catch it however it leaked.
    /// </summary>
    [Fact]
    public async Task AStoppedResourceAndARunningOneMeterIdentically() {
        MeteringCluster.ResetDoubles();

        var running = Fresh();
        var stopped = Fresh();

        FakeResourceSource.Hold(running, MeteringCluster.Resource(Volume, QuotaMeter.StorageGb, 100m));
        FakeResourceSource.Hold(
            stopped,
            MeteringCluster.Resource(Volume, QuotaMeter.StorageGb, 100m, ObservedRunState.Stopped)
        );

        _ = await cluster.Sampler(MeteringCluster.Tenant, running).SampleAsync();
        _ = await cluster.Sampler(MeteringCluster.Tenant, stopped).SampleAsync();

        var a = (await cluster.Rollup(MeteringCluster.Tenant, running).ListPendingAsync()).GetValueOrThrow();
        var b = (await cluster.Rollup(MeteringCluster.Tenant, stopped).ListPendingAsync()).GetValueOrThrow();

        b.Length.ShouldBe(a.Length);
        b[0].Quantity.ShouldBe(a[0].Quantity);

        // The resource GUID, meter and window are the same, so the keys are too — which is the
        // strongest statement available that nothing about running-ness reached the record.
        b[0].IdempotencyKey.ShouldBe(a[0].IdempotencyKey);
    }

    // ── The sampler's other obligations ────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ A source failure is not an empty pass. Writing zero for a window we could not read is a
    ///     loss that looks exactly like an idle subscription afterwards — docs/plan/22 § Effort's
    ///     "usage that was never recorded cannot be recovered".
    /// </summary>
    [Fact]
    public async Task AResourceSourceFailureFailsThePassRatherThanRecordingZero() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 4m));
        FakeResourceSource.Fail = true;

        var failed = await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync();

        failed.IsFailure.ShouldBeTrue();

        (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow()
            .ShouldBeEmpty();

        // ⚠ And the window is retried rather than lost: once the source recovers, the same window
        // produces its record.
        FakeResourceSource.Fail = false;
        var recovered = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync())
            .GetValueOrThrow();

        recovered.Accepted.ShouldBe(1);
    }

    /// <summary>
    ///     An empty subscription is a legitimate zero — distinct from a failure, which is the
    ///     distinction <see cref="IMeteredResourceSource.ListAsync" /> insists on.
    /// </summary>
    [Fact]
    public async Task AnEmptySubscriptionSamplesToNothingAndSucceeds() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var report = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow();

        report.ResourcesSeen.ShouldBe(0);
        report.Emitted.ShouldBe(0);
    }

    /// <summary>
    ///     ⚠ <c>QuotaMeter.StorageGb</c> accrues two meters and only the primary is sampled. Billing
    ///     every provisioned gibibyte as both storage and backup would silently double the storage
    ///     line on every invoice — see <c>MeterCatalog.MetersOf</c>.
    /// </summary>
    [Fact]
    public async Task AFamilyWithTwoMetersIsNotBilledTwice() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Volume, QuotaMeter.StorageGb, 50m));

        _ = await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync();

        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        pending.Length.ShouldBe(1);
        pending[0].Meter.ShouldBe(BillingMeter.StorageGbMonths);
        pending.ShouldNotContain(x => x.Meter == BillingMeter.BackupGbMonths);
    }

    [Fact]
    public async Task ARunningSamplerHasItsReminderRegistered() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var sampler = cluster.Sampler(MeteringCluster.Tenant, subscription);

        (await sampler.IsRunningAsync()).GetValueOrThrow().ShouldBeFalse();

        (await sampler.StartAsync()).IsSuccess.ShouldBeTrue();
        (await sampler.IsRunningAsync()).GetValueOrThrow().ShouldBeTrue();

        // ⚠ Starting twice registers or updates one reminder, never two — a doubled reminder would
        // double the pass rate, which the dedup would absorb but which nobody would notice.
        (await sampler.StartAsync()).IsSuccess.ShouldBeTrue();
        (await sampler.IsRunningAsync()).GetValueOrThrow().ShouldBeTrue();

        (await sampler.StopAsync()).IsSuccess.ShouldBeTrue();
        (await sampler.IsRunningAsync()).GetValueOrThrow().ShouldBeFalse();
    }

    /// <summary>
    ///     The sampler survives deactivation and keeps sampling the same window — the "Hot tier is
    ///     enough" claim in <c>IUsageSamplerGrain</c>, exercised.
    /// </summary>
    [Fact]
    public async Task ADeactivatedSamplerStillCollapsesItsOwnWindow() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 1m));

        var sampler = cluster.Sampler(MeteringCluster.Tenant, subscription);

        _ = await sampler.SampleAsync();
        await sampler.DeactivateAsync();

        var afterRestart = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync())
            .GetValueOrThrow();

        afterRestart.Duplicates.ShouldBe(1);

        (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow()
            .Length.ShouldBe(1);
    }

    static Guid Fresh() => Guid.NewGuid();
}
