using CyberCloud.Billing.Pricing;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The rating engine — docs/plan/22 § Rating: tiers against the monthly aggregate, the version in
///     force at the window, corrections netted before pricing.
/// </summary>
public sealed class RatingTests {
    static readonly DateTimeOffset August = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset September = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly Guid A = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    static readonly Guid B = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    long sequence;

    [Fact]
    public void AnHourThatCrossesATierBoundaryIsSplitAcrossIt() {
        // Committed StorageGbMonths: the first 50 free, then 0.08 up to 102400.
        var entries = new[] { Entry(A, BillingMeter.StorageGbMonths, August, 49.9m), Entry(A, BillingMeter.StorageGbMonths, August.AddHours(1), 0.3m) };

        var rated = Rating.RateMonth(PriceSheet.Committed, entries, August).GetValueOrThrow();

        rated[0].Amount.ShouldBe(0m, "49.9 is inside the free tier");
        rated[1].Amount.ShouldBe(0.2m * 0.080m, "0.1 of the hour is free and 0.2 is at the second tier's price");
    }

    /// <summary>
    ///     ⚠ docs/plan/22's "common and expensive mistake" is pricing per event. Pricing every hour on a
    ///     ladder climbed in time order must give exactly what pricing the month's aggregate gives.
    /// </summary>
    [Fact]
    public void PricingEveryHourSumsToPricingTheMonthlyAggregate() {
        var random = new Random(38);
        var entries = new List<UsageLedgerEntry>();

        for (var hour = 0; hour < 24 * 31; hour++) {
            entries.Add(Entry(A, BillingMeter.EgressGb, August.AddHours(hour), Math.Round((decimal)random.NextDouble() * 40m, 6)));
            entries.Add(Entry(B, BillingMeter.EgressGb, August.AddHours(hour), Math.Round((decimal)random.NextDouble() * 5m, 6)));
        }

        var rated = Rating.RateMonth(PriceSheet.Committed, entries, August).GetValueOrThrow();
        var ladder = PriceSheet.Committed.At(August).GetValueOrThrow().Meters[BillingMeter.EgressGb].Tiers;

        rated.Sum(static x => x.Amount).ShouldBe(Rating.Climb(ladder, 0m, entries.Sum(static x => x.Quantity)));
        entries.Sum(static x => x.Quantity).ShouldBeGreaterThan(10240m, "the month must reach the third tier for the sum to mean anything");
    }

    [Fact]
    public void TheFreeTierIsConsumedByWhoeverUsedTheMeterFirstInTheMonth() {
        // 100 GiB of egress are free; A uses 80 at 01:00 and B uses 80 at 02:00.
        var entries = new[] { Entry(B, BillingMeter.EgressGb, August.AddHours(2), 80m), Entry(A, BillingMeter.EgressGb, August.AddHours(1), 80m) };

        var rated = Rating.RateMonth(PriceSheet.Committed, entries, August).GetValueOrThrow();

        rated.Single(x => x.ResourceId == A).Amount.ShouldBe(0m);
        rated.Single(x => x.ResourceId == B).Amount.ShouldBe(60m * 0.050m);
    }

    [Fact]
    public void TheLadderStartsAgainEveryMonth() {
        var entries = new[] {
            Entry(A, BillingMeter.EgressGb, August.AddDays(30), 100m),
            Entry(A, BillingMeter.EgressGb, September, 100m)
        };

        Rating.RateMonth(PriceSheet.Committed, entries, August).GetValueOrThrow().Single().Amount.ShouldBe(0m);
        Rating.RateMonth(PriceSheet.Committed, entries, September).GetValueOrThrow().Single().Amount.ShouldBe(0m, "September's first 100 GiB are free again");
    }

    [Fact]
    public void ACorrectionNetsIntoItsHourBeforeAnythingIsPriced() {
        var original = Entry(A, BillingMeter.VCpuHours, August, 4m);
        var correction = original with { EntryId = Guid.NewGuid(), Sequence = ++sequence, Quantity = -1m, CorrectsEntryId = original.EntryId, Reason = "resize double-counted" };

        var rated = Rating.RateMonth(PriceSheet.Committed, [original, correction], August).GetValueOrThrow().ShouldHaveSingleItem();

        rated.Quantity.ShouldBe(3m);
        rated.Amount.ShouldBe(3m * 0.0250m);
    }

    [Fact]
    public void ACorrectionThatTakesAnHourBelowZeroIsRefusedByName() {
        var original = Entry(A, BillingMeter.VCpuHours, August, 1m);
        var correction = original with { EntryId = Guid.NewGuid(), Quantity = -2m, CorrectsEntryId = original.EntryId, Reason = "x" };

        Rating.RateMonth(PriceSheet.Committed, [original, correction], August).Error!.Message.ShouldContain("below zero");
    }

    /// <summary>
    ///     ⚠ docs/plan/22: "A price change never applies retroactively; the rating engine picks the list
    ///     in effect at the usage window." August's usage is priced at August's price after September's
    ///     version exists.
    /// </summary>
    [Fact]
    public void APriceChangeNeverAppliesRetroactively() {
        var sheet = PriceSheet.Parse(TwoVersions(augustVcpu: 0.02m, septemberVcpu: 0.05m)).GetValueOrThrow();
        var entries = new[] { Entry(A, BillingMeter.VCpuHours, August.AddDays(3), 10m), Entry(A, BillingMeter.VCpuHours, September.AddDays(3), 10m) };

        var august = Rating.RateMonth(sheet, entries, August).GetValueOrThrow().Single();
        var september = Rating.RateMonth(sheet, entries, September).GetValueOrThrow().Single();

        august.Amount.ShouldBe(0.2m);
        august.PriceVersion.ShouldBe(August);
        september.Amount.ShouldBe(0.5m);
        september.PriceVersion.ShouldBe(September);
    }

    [Fact]
    public void AMonthWithNoUsageNeedsNoPriceSheetVersion() =>
        Rating.RateMonth(PriceSheet.Committed, [], new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .GetValueOrThrow()
            .ShouldBeEmpty();

    [Fact]
    public void ARatedHourKnowsItsGroupAndTypeFromItsPath() {
        var tenant = Guid.NewGuid();
        var subscription = Guid.NewGuid();
        var hour = Rating.RateMonth(
                PriceSheet.Committed,
                [Entry(A, BillingMeter.VCpuHours, August, 1m, BillingCluster.PathOf(tenant, subscription, "prod", "db", "CyberCloud.DBforPostgreSQL/servers"))],
                August
            )
            .GetValueOrThrow()
            .Single();

        hour.ResourceGroup.ShouldBe("prod");
        hour.ResourceType.ShouldBe("CyberCloud.DBforPostgreSQL/servers");
    }

    UsageLedgerEntry Entry(Guid resource, BillingMeter meter, DateTimeOffset hour, decimal quantity, string path = "/x") =>
        new() {
            Sequence = ++sequence,
            EntryId = Guid.NewGuid(),
            ResourceId = resource,
            ResourcePath = path,
            Meter = meter,
            WindowStart = hour,
            WindowEnd = hour.AddHours(1),
            Quantity = quantity
        };

    static string TwoVersions(decimal augustVcpu, decimal septemberVcpu) {
        return "{\"versions\":[" + Version("2026-08-01T00:00:00Z", augustVcpu) + "," + Version("2026-09-01T00:00:00Z", septemberVcpu) + "]}";

        static string Version(string at, decimal vcpu) {
            var meters = MeterCatalog.Definitions.Select(x =>
                $$"""{ "meter": "{{x.Meter}}", "unit": "{{x.Unit}}", "currency": "EUR", "tiers": [ { "upTo": null, "unitPrice": {{(x.Meter == BillingMeter.VCpuHours ? vcpu : 0m).ToString(System.Globalization.CultureInfo.InvariantCulture)}} } ] }"""
            );
            return $$"""{ "effectiveFrom": "{{at}}", "meters": [ {{string.Join(",", meters)}} ] }""";
        }
    }
}
