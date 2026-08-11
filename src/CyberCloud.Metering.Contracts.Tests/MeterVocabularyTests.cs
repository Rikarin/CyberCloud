using System.Collections.Immutable;

namespace CyberCloud.Metering.Contracts.Tests;

/// <summary>
///     The meter vocabulary — docs/plan/06 § Quota's reservable families against docs/plan/22's
///     billing meters, and the window snap the sampler's idempotency depends on.
/// </summary>
/// <remarks>
///     ⚠ <b>The gap these tests exist to close.</b> docs/plan/08 § The provider registry declares
///     <c>.Meters(Meter.VCpuHours, Meter.StorageGbMonths, Meter.BackupGbMonths)</c> while
///     docs/plan/06 § Quota's families are <c>vcpu</c>, <c>memoryGb</c>, <c>storageGb</c>,
///     <c>publicIps</c>, <c>clusters</c>, <c>resources</c>. Only one can be what
///     <c>IQuotaGrain.TryReserveAsync</c> takes, and it is 06's — see <c>MeterCatalog</c>. What is
///     asserted here is the consequence: the two vocabularies are connected by a <i>total</i>
///     function, so a family the write path can reserve always has a meter, and a state-based meter
///     always integrates a family.
/// </remarks>
public sealed class MeterVocabularyTests {
    static readonly ImmutableArray<QuotaMeter> Families =
        [.. Enum.GetValues<QuotaMeter>().Where(x => x != QuotaMeter.Unknown)];

    /// <summary>
    ///     ⚠ The one that matters. A family the registry can declare and the write path can reserve,
    ///     with no meter to bill it, is a resource that is quota-limited and free — the shape of a
    ///     free-compute exploit, and invisible because quota looks like it is working.
    /// </summary>
    [Fact]
    public void EveryQuotaFamilyHasAtLeastOneBillingMeter() {
        foreach (var family in Families) {
            MeterCatalog.MetersOf(family)
                .ShouldNotBeEmpty($"{family} is reservable through IQuotaGrain and nothing bills it.");
        }
    }

    /// <summary>
    ///     The converse. A state-based meter with no family is a meter the sampler can never produce,
    ///     because the sampler's only input is quantities in family units.
    /// </summary>
    [Fact]
    public void EveryStateBasedMeterIntegratesAQuotaFamily() {
        foreach (var definition in MeterCatalog.Definitions.Where(x => x.Kind == MeterKind.StateBased)) {
            definition.Family.ShouldNotBe(
                QuotaMeter.Unknown,
                $"{definition.Meter} is state-based and integrates no family, so nothing can sample it."
            );
        }
    }

    /// <summary>
    ///     ⚠ And an event-based meter must <b>not</b> have one. If it did, the sampler's derivation
    ///     would pick it up and start sampling a meter where "sampling would miss everything between
    ///     samples" (docs/plan/22 § Two kinds of meter).
    /// </summary>
    [Fact]
    public void NoEventBasedMeterIntegratesAQuotaFamily() {
        foreach (var definition in MeterCatalog.Definitions.Where(x => x.Kind == MeterKind.EventBased)) {
            definition.Family.ShouldBe(QuotaMeter.Unknown, $"{definition.Meter} would be sampled.");
        }
    }

    [Fact]
    public void EveryDeclaredMeterHasAKindAndAUnit() {
        foreach (var definition in MeterCatalog.Definitions) {
            definition.Kind.ShouldNotBe(MeterKind.Unknown);
            definition.Unit.ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    ///     Every member of the enum is in the catalogue. A meter that exists as a name and not as a
    ///     definition would be accepted by a provider and dropped by the sampler.
    /// </summary>
    [Fact]
    public void EveryBillingMeterIsDefined() {
        foreach (var meter in Enum.GetValues<BillingMeter>().Where(x => x != BillingMeter.Unknown)) {
            MeterCatalog.Define(meter).IsSuccess.ShouldBeTrue($"{meter} has no definition.");
        }
    }

    [Fact]
    public void UnknownIsNotAMeter() {
        MeterCatalog.Define(BillingMeter.Unknown).IsFailure.ShouldBeTrue();
        MeterCatalog.KindOf(BillingMeter.Unknown).ShouldBe(MeterKind.Unknown);
        MeterCatalog.MetersOf(QuotaMeter.Unknown).ShouldBeEmpty();
    }

    /// <summary>
    ///     The specific mapping docs/plan/08's example got backwards, spelled out: the registry
    ///     declares <c>Vcpu</c> and what accrues from it is <c>VCpuHours</c>.
    /// </summary>
    [Fact]
    public void VcpuTheFamilyAccruesVCpuHoursTheMeter() {
        MeterCatalog.MetersOf(QuotaMeter.Vcpu).ShouldBe([BillingMeter.VCpuHours]);
        MeterCatalog.FamilyOf(BillingMeter.VCpuHours).ShouldBe(QuotaMeter.Vcpu);
    }

    /// <summary>
    ///     ⚠ <c>storageGb</c> is the family with two meters, which is why <c>MetersOf</c> returns an
    ///     array rather than one value — and why <c>UsageSamplerGrain</c> accrues only the first.
    ///     Billing every provisioned gibibyte as both storage and backup would double the storage
    ///     line on every invoice.
    /// </summary>
    [Fact]
    public void StorageGbAccruesTwoMetersAndStorageGbMonthsIsThePrimary() {
        var meters = MeterCatalog.MetersOf(QuotaMeter.StorageGb);

        meters.Length.ShouldBe(2);
        meters[0].ShouldBe(BillingMeter.StorageGbMonths);
        meters.ShouldContain(BillingMeter.BackupGbMonths);
    }

    // ── The accrual arithmetic ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AnHourOfOneVcpuIsOneVCpuHour() =>
        MeterCatalog.Accrue(BillingMeter.VCpuHours, 1m, TimeSpan.FromHours(1))
            .GetValueOrThrow()
            .ShouldBe(1m);

    [Fact]
    public void AFiveMinuteWindowOfFourVcpusIsOneThirdOfAVCpuHour() =>
        MeterCatalog.Accrue(BillingMeter.VCpuHours, 4m, TimeSpan.FromMinutes(5))
            .GetValueOrThrow()
            .ShouldBe(0.333333333333m);

    /// <summary>
    ///     ⚠ The 730-hour month of <c>MeterCatalog.BillingMonth</c>, checked at the boundary rather
    ///     than assumed. A calendar month here would make the same disk cost 7 % more in March.
    /// </summary>
    [Fact]
    public void SevenHundredAndThirtyHoursOfOneGibibyteIsOneGbMonth() =>
        MeterCatalog.Accrue(BillingMeter.StorageGbMonths, 1m, TimeSpan.FromHours(730))
            .GetValueOrThrow()
            .ShouldBe(1m);

    /// <summary>
    ///     ⚠ 288 five-minute windows is one day, and a day of 100 GiB must come to
    ///     <c>24/730 × 100</c> whichever order the windows are added in. This is what the rounding in
    ///     <c>Accrue</c> buys, and a billing figure that depends on addition order cannot be
    ///     reproduced when a customer disputes it.
    /// </summary>
    [Fact]
    public void ADayOfFiveMinuteSamplesSumsToADayRegardlessOfOrder() {
        var window = MeterCatalog.Accrue(BillingMeter.StorageGbMonths, 100m, TimeSpan.FromMinutes(5))
            .GetValueOrThrow();

        var forward = 0m;
        for (var i = 0; i < 288; i++) {
            forward += window;
        }

        var backward = 0m;
        for (var i = 288; i > 0; i--) {
            backward += window;
        }

        forward.ShouldBe(backward);
        forward.ShouldBe(288m * window);
        Math.Round(forward, 6).ShouldBe(Math.Round(100m * 24m / 730m, 6));
    }

    [Fact]
    public void AccruingAnEventBasedMeterIsRefused() {
        var refused = MeterCatalog.Accrue(BillingMeter.Requests, 1m, TimeSpan.FromMinutes(5));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("EventBased");
    }

    [Fact]
    public void ANegativeStockIsRefused() =>
        MeterCatalog.Accrue(BillingMeter.VCpuHours, -1m, TimeSpan.FromMinutes(5)).IsFailure.ShouldBeTrue();

    [Fact]
    public void AZeroWindowIsRefused() =>
        MeterCatalog.Accrue(BillingMeter.VCpuHours, 1m, TimeSpan.Zero).IsFailure.ShouldBeTrue();

    /// <summary>
    ///     ⚠ Zero stock is <b>not</b> refused. A resource that exists holding nothing of a family is
    ///     a legitimate zero, and refusing it would make the sampler fail a whole pass over one
    ///     resource with an empty disk.
    /// </summary>
    [Fact]
    public void AZeroStockAccruesZero() =>
        MeterCatalog.Accrue(BillingMeter.VCpuHours, 0m, TimeSpan.FromMinutes(5)).GetValueOrThrow().ShouldBe(0m);

    // ── The window snap ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ The arithmetic the whole "runs twice, one record" property rests on. Every instant
    ///     inside one five-minute slot floors to the same window, so a second pass computes the same
    ///     key. Note the inputs are deliberately not on the grid.
    /// </summary>
    [Fact]
    public void EveryInstantInOneSlotSnapsToOneWindow() {
        var expected = new UsageWindow(
            new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            new(2026, 8, 11, 12, 5, 0, TimeSpan.Zero)
        );

        UsageWindow.SampleAt(new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)).ShouldBe(expected);
        UsageWindow.SampleAt(new(2026, 8, 11, 12, 3, 17, TimeSpan.Zero)).ShouldBe(expected);
        UsageWindow.SampleAt(new(2026, 8, 11, 12, 4, 59, TimeSpan.Zero)).ShouldBe(expected);
    }

    [Fact]
    public void TheNextSlotIsADifferentWindow() =>
        UsageWindow.SampleAt(new(2026, 8, 11, 12, 5, 0, TimeSpan.Zero))
            .ShouldNotBe(UsageWindow.SampleAt(new(2026, 8, 11, 12, 4, 59, TimeSpan.Zero)));

    /// <summary>The grid is a UTC grid, so an instant expressed elsewhere lands in the same slot.</summary>
    [Fact]
    public void TheGridIsAnchoredInUtc() =>
        UsageWindow.SampleAt(new DateTimeOffset(2026, 8, 11, 14, 3, 17, TimeSpan.FromHours(2)))
            .ShouldBe(UsageWindow.SampleAt(new(2026, 8, 11, 12, 3, 17, TimeSpan.Zero)));

    [Fact]
    public void TheSampleWindowIsFiveMinutesAndTheRollupWindowIsAnHour() {
        UsageWindow.SampleAt(TestInstant).Length.ShouldBe(TimeSpan.FromMinutes(5));
        UsageWindow.HourAt(TestInstant).Length.ShouldBe(TimeSpan.FromHours(1));
    }

    /// <summary>Twelve sample windows fit exactly in one rollup hour — the aggregate's arithmetic.</summary>
    [Fact]
    public void TwelveSampleWindowsFitInOneHour() {
        var hour = UsageWindow.HourAt(TestInstant);
        var slot = UsageWindow.SampleAt(hour.Start);

        var count = 0;
        while (slot.End <= hour.End) {
            count++;
            slot = UsageWindow.SampleAt(slot.End);
        }

        count.ShouldBe(12);
    }

    static readonly DateTimeOffset TestInstant = new(2026, 8, 11, 12, 3, 17, TimeSpan.Zero);
}
