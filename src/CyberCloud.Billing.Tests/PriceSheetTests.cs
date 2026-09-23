using CyberCloud.Billing.Pricing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     The committed price sheet, held to <see cref="PriceSheet" />'s rules — and the rules held to
///     refusing a sheet that breaks them.
/// </summary>
public sealed class PriceSheetTests {
    /// <summary>
    ///     The digest of every version in force when this file was last changed, by effective date.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A version in force is never edited, and this is what notices.</b> docs/plan/22 § Rating:
    ///     "A price change never applies retroactively." Editing a past version's price re-rates every
    ///     draft and every cost view of the months it covers, and a customer's cost history changes
    ///     under them. A price change is a new version with a later date; adding one adds a line here,
    ///     and changing a line here is the edit this test exists to stop.
    /// </remarks>
    static readonly Dictionary<string, string> InForce = new(StringComparer.Ordinal) {
        ["2026-08-01"] = "1edc028f3db3ca686481dbd9a28aa5614a9d4fcfddc34e07822dc17b766594b6"
    };

    [Fact]
    public void TheCommittedSheetParsesAndPricesEveryMeterInEveryVersion() {
        var sheet = PriceSheet.Committed;

        sheet.Versions.ShouldNotBeEmpty();

        foreach (var version in sheet.Versions) {
            foreach (var definition in MeterCatalog.Definitions) {
                var price = version.Meters[definition.Meter];

                price.Unit.ShouldBe(definition.Unit, $"{definition.Meter} in {version.EffectiveFrom:O}");
                Currencies.IsKnown(price.Currency).ShouldBeTrue();
                price.Tiers[^1].UpTo.ShouldBeNull("the last tier is open-ended");
            }
        }
    }

    [Fact]
    public void NoVersionInForceHasBeenEdited() {
        foreach (var version in PriceSheet.Committed.Versions) {
            var key = version.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            InForce.TryGetValue(key, out var pinned)
                .ShouldBeTrue($"version {key} is new — pin its digest ({Digest(version)}) in PriceSheetTests.InForce");

            Digest(version).ShouldBe(pinned, $"version {key} has been edited. A price change is a new version with a later date.");
        }
    }

    [Fact]
    public void AVersionThatDoesNotStartAMonthIsRefused() =>
        PriceSheet.Parse(Sheet("2026-08-17T00:00:00Z")).Error!.Message.ShouldContain("does not start a month");

    [Fact]
    public void AMeterWithoutAPriceIsRefused() =>
        PriceSheet.Parse(Sheet("2026-08-01T00:00:00Z", skip: BillingMeter.EgressGb))
            .Error!.Message.ShouldContain("does not price EgressGb");

    [Fact]
    public void ALadderThatEndsIsRefused() =>
        PriceSheet.Parse(Sheet("2026-08-01T00:00:00Z", lastUpTo: "1000"))
            .Error!.Message.ShouldContain("not closed");

    [Fact]
    public void AUnitThatDisagreesWithTheCatalogIsRefused() =>
        PriceSheet.Parse(Sheet("2026-08-01T00:00:00Z", unit: "core-hour"))
            .Error!.Message.ShouldContain("MeterCatalog");

    [Fact]
    public void VersionsOutOfDateOrderAreRefused() {
        var json = "{\"versions\":[" + Version("2026-09-01T00:00:00Z") + "," + Version("2026-08-01T00:00:00Z") + "]}";

        PriceSheet.Parse(json).Error!.Message.ShouldContain("not later than");
    }

    [Fact]
    public void AnInstantBeforeTheFirstVersionHasNoPrice() =>
        PriceSheet.Committed.At(new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero)).IsFailure.ShouldBeTrue();

    /// <summary>The digest a version is pinned by: every meter, unit, currency and tier, in a fixed order.</summary>
    internal static string Digest(PriceSheetVersion version) {
        var text = new StringBuilder();
        text.Append(version.EffectiveFrom.ToString("O", CultureInfo.InvariantCulture)).Append('\n');

        foreach (var price in version.Meters.Values.OrderBy(static x => x.Meter)) {
            text.Append(CultureInfo.InvariantCulture, $"{price.Meter}|{price.Unit}|{price.Currency}");
            foreach (var tier in price.Tiers) {
                text.Append(CultureInfo.InvariantCulture, $"|{tier.UpTo?.ToString(CultureInfo.InvariantCulture) ?? "-"}@{tier.UnitPrice}");
            }

            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static string Sheet(string effectiveFrom, BillingMeter skip = BillingMeter.Unknown, string lastUpTo = "null", string unit = "") =>
        "{\"versions\":[" + Version(effectiveFrom, skip, lastUpTo, unit) + "]}";

    static string Version(string effectiveFrom, BillingMeter skip = BillingMeter.Unknown, string lastUpTo = "null", string unit = "") {
        var meters = MeterCatalog.Definitions
            .Where(x => x.Meter != skip)
            .Select(x =>
                $$"""{ "meter": "{{x.Meter}}", "unit": "{{(unit.Length > 0 && x.Meter == BillingMeter.VCpuHours ? unit : x.Unit)}}", "currency": "EUR", "tiers": [ { "upTo": {{lastUpTo}}, "unitPrice": 0.01 } ] }"""
            );

        return $$"""{ "effectiveFrom": "{{effectiveFrom}}", "meters": [ {{string.Join(",", meters)}} ] }""";
    }
}
