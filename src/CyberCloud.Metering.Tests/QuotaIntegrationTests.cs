namespace CyberCloud.Metering.Tests;

/// <summary>
///     The quota integration — docs/plan/22 § Quota: <i>"Distinct from billing and enforced earlier:
///     a reservation in <c>IQuotaGrain</c> before the provider is called, released on failure."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here reimplements a reservation, and that is the point.</b>
///         <c>IQuotaGrain</c> already exists in <c>CyberCloud.Tenancy</c> with reservation, commit,
///         release and expiry; the resource manager's write path already calls it
///         (<c>ResourceManagerService.ReserveAsync</c>, step 6). What metering owes is not a second
///         quota mechanism but a <b>shared vocabulary</b>: the families the registry declares and the
///         write path reserves must be the families the sampler meters. These tests assert that
///         against the <b>real</b> grain.
///     </para>
///     <para>
///         ⚠ <b>The vocabulary conflict, resolved.</b> docs/plan/08 § The provider registry's example
///         writes <c>.Meters(Meter.VCpuHours, Meter.StorageGbMonths, Meter.BackupGbMonths)</c> —
///         billing meters. docs/plan/06 § Quota's families are <c>vcpu</c>, <c>memoryGb</c>,
///         <c>storageGb</c>, <c>publicIps</c>, <c>clusters</c>, <c>resources</c>, and that is what
///         <c>IQuotaGrain.TryReserveAsync</c> takes. <b>08 is the document that is wrong</b>: a
///         GB-month names no instant and cannot be reserved or released, so half of 06's mechanism is
///         undefined on it. <c>MeterCatalog</c> is the bridge and
///         <c>MeterVocabularyTests</c> asserts it is total.
///     </para>
/// </remarks>
[Collection(MeteringClusterFixture.Name)]
public sealed class QuotaIntegrationTests(MeteringCluster cluster) {
    static readonly Guid Widget = Guid.Parse("dddddddd-dddd-4ddd-8ddd-ddddddddddd1");

    // ── FAILURE CLASS: quota release on failure ────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>Asserted against the real <c>QuotaGrain</c>, not reimplemented.</b> A lease taken and
    ///     released frees the quota it held; the resource never existed, so it is never metered.
    /// </summary>
    [Fact]
    public async Task AReleasedLeaseFreesItsQuotaAndMetersNothing() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);
        var operationId = Guid.NewGuid();

        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 8m, operationId)).GetValueOrThrow();
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Reserved.ShouldBe(8m);

        // The provider call failed. docs/plan/22 § Quota: "released on failure".
        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        var usage = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow();
        usage.Reserved.ShouldBe(0m);
        usage.Committed.ShouldBe(0m);

        // ⚠ And metering saw nothing, because the resource-graph projection never gained a resource
        // — a reservation that was released is not a thing that exists.
        var report = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow();
        report.Emitted.ShouldBe(0);
    }

    /// <summary>
    ///     ⚠ Releasing twice succeeds rather than failing — <c>IQuotaGrain.ReleaseAsync</c>'s own
    ///     documented behaviour, "so a failure path re-driven from a reminder is safe to run twice".
    ///     Asserted here because metering's own re-drive paths depend on the same discipline.
    /// </summary>
    [Fact]
    public async Task ReleasingTwiceIsSafe() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 2m, Guid.NewGuid())).GetValueOrThrow();

        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();
        (await quota.ReleaseAsync(lease.LeaseId)).IsSuccess.ShouldBeTrue();

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Reserved.ShouldBe(0m);
    }

    /// <summary>
    ///     The committed path: a lease that commits becomes usage, and what metering then samples is
    ///     the same family in the same units.
    /// </summary>
    [Fact]
    public async Task CommittedQuotaAndMeteredStockSpeakTheSameVocabulary() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid())).GetValueOrThrow();

        var committed = (await quota.CommitAsync(lease.LeaseId)).GetValueOrThrow();
        committed.Committed.ShouldBe(4m);

        // The resource now exists, holding exactly what quota committed — in QuotaMeter units.
        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 4m));

        _ = await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync();

        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        pending.Length.ShouldBe(1);

        // ⚠ The meter that accrues is derived from the family that was reserved. One declaration,
        // two uses — see MeterCatalog.
        pending[0].Meter.ShouldBe(MeterCatalog.MetersOf(QuotaMeter.Vcpu)[0]);
        MeterCatalog.FamilyOf(pending[0].Meter).ShouldBe(QuotaMeter.Vcpu);

        pending[0].Quantity.ShouldBe(
            MeterCatalog.Accrue(BillingMeter.VCpuHours, committed.Committed, UsageWindow.SamplePeriod)
                .GetValueOrThrow()
        );
    }

    /// <summary>
    ///     ⚠ <b>A GB-month is not a reservable quantity — the concrete reason docs/plan/08's registry
    ///     example is the wrong vocabulary.</b> <c>QuotaMeter</c> has no member that could express
    ///     one, so the code cannot even be written; what the write path reserves is
    ///     <c>StorageGb</c>, and <c>StorageGbMonths</c> is what that accrues over time.
    /// </summary>
    [Fact]
    public async Task QuotaReservesAStockAndMeteringBillsTheFlowItAccrues() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);

        // The stock: 100 GiB, reservable, releasable, and meaningful at an instant.
        var lease = (await quota.TryReserveAsync(QuotaMeter.StorageGb, 100m, Guid.NewGuid())).GetValueOrThrow();
        _ = await quota.CommitAsync(lease.LeaseId);

        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.StorageGb, 100m));
        _ = await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync();

        var pending = (await cluster.Rollup(MeteringCluster.Tenant, subscription).ListPendingAsync())
            .GetValueOrThrow();

        // The flow: GB-months, which accrue and can only be summed over a window.
        pending[0].Meter.ShouldBe(BillingMeter.StorageGbMonths);
        MeterCatalog.Define(BillingMeter.StorageGbMonths).GetValueOrThrow().Period.ShouldBe(MeterCatalog.BillingMonth);

        // ⚠ Five minutes of 100 GiB is a small fraction of a GB-month — a figure that would be
        // nonsense as a quota limit and is exactly right as a bill.
        pending[0].Quantity.ShouldBeLessThan(1m);
        pending[0].Quantity.ShouldBeGreaterThan(0m);
    }

    /// <summary>
    ///     Quota refuses over the limit, and a refused create is never metered. docs/plan/22 § Quota:
    ///     quota is "a safety mechanism, not a sales mechanism" and bounds the damage from a runaway
    ///     loop — including the metering bill it would otherwise produce.
    /// </summary>
    [Fact]
    public async Task QuotaRefusalMeansThereIsNothingToMeter() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);
        (await quota.SetLimitAsync(QuotaMeter.Vcpu, 4m)).IsSuccess.ShouldBeTrue();

        var refused = await quota.TryReserveAsync(QuotaMeter.Vcpu, 8m, Guid.NewGuid());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);

        var report = (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow();
        report.Emitted.ShouldBe(0);
    }

    /// <summary>
    ///     ⚠ <b>THE DEFECT THIS USED TO PIN, NOW ASSERTED CLOSED.</b> Committed quota came back to
    ///     zero across a delete.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What was wrong.</b> <c>OperationGrain.ReturnCommittedQuotaAsync</c> delegated to
    ///         <c>ReleaseAsync</c>, which releases <i>leases</i> — and a delete holds none, because the
    ///         amounts were <b>committed</b> by the create. <c>IQuotaGrain.ReturnAsync</c> was the
    ///         method that unwinds committed usage and nothing called it, so a subscription's
    ///         committed figure climbed by one resource's worth on every delete and never came down.
    ///         Closing it needed the committed amounts on the operation spec — a wire change to
    ///         <c>CyberCloud.ResourceManager.Contracts</c>, which is
    ///         <c>OperationSpec.CommittedQuota</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This test was written to fail when the fix landed, and it did.</b> Its previous
    ///         form asserted <c>Committed == 4</c> after a delete and said in as many words that the
    ///         expectation should become <c>0</c>. It is now <c>0</c>. What is asserted here is the
    ///         <i>quota half</i> of the fix, which is all this assembly can reach — it references
    ///         <c>CyberCloud.Tenancy</c> and not the resource manager, so the delete is modelled the
    ///         way the operation grain now performs it. The end-to-end assertion, driven through the
    ///         real write and delete paths, is
    ///         <c>CyberCloud.ResourceManager.Tests.DeletePathTests.ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The containment claim is kept because it is still true and still worth knowing.</b>
    ///         Quota is a <i>limit</i>, not a bill, so the drift refused creates that should have been
    ///         allowed rather than charging for anything. Metering reads
    ///         <see cref="IMeteredResourceSource" /> — what exists — and never reads committed quota,
    ///         so the meter was correct throughout, before and after.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task CommittedQuotaIsReturnedOnDeleteAndTheMeterWasNeverAffectedEitherWay() {
        var subscription = Fresh();
        MeteringCluster.ResetDoubles();

        var quota = cluster.Quota(MeteringCluster.Tenant, subscription);

        // Create: reserve, commit.
        var lease = (await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid())).GetValueOrThrow();
        _ = await quota.CommitAsync(lease.LeaseId);
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(4m);

        FakeResourceSource.Hold(subscription, MeteringCluster.Resource(Widget, QuotaMeter.Vcpu, 4m));
        (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow()
            .Emitted.ShouldBe(1);

        // Delete: the resource goes, and the operation returns what the create committed. It holds no
        // lease to release — this is exactly the call the delete path was missing.
        FakeResourceSource.Hold(subscription);
        TestClock.Instance.Advance(UsageWindow.SamplePeriod);

        (await quota.ListLeasesAsync()).GetValueOrThrow()
            .ShouldBeEmpty("a committed lease is gone; there is nothing left for ReleaseAsync to do");

        (await quota.ReturnAsync(QuotaMeter.Vcpu, 4m)).IsSuccess.ShouldBeTrue();

        // ⚠ THE FIX: the allowance is back, in full and exactly.
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(
                0m,
                "committed quota comes back on delete — IQuotaGrain.ReturnAsync, which the operation "
                + "grain now calls with the amounts carried on OperationSpec.CommittedQuota"
            );

        // ⚠ And the subscription can use it again, which is what the drift was silently taking away.
        var reused = await quota.TryReserveAsync(QuotaMeter.Vcpu, 4m, Guid.NewGuid());
        reused.IsSuccess.ShouldBeTrue("returned quota is quota a create may draw on again");
        _ = await quota.ReleaseAsync(reused.GetValueOrThrow().LeaseId);

        // ⚠ THE CONTAINMENT, UNCHANGED: metering reads what exists rather than what quota thinks is
        // committed, so it reported nothing for the deleted resource before the fix and after it.
        (await cluster.Sampler(MeteringCluster.Tenant, subscription).SampleAsync()).GetValueOrThrow()
            .Emitted.ShouldBe(0);
    }

    static Guid Fresh() => Guid.NewGuid();
}
