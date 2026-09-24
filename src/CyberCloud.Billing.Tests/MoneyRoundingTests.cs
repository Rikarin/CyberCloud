using CyberCloud.Billing.Pricing;
using System.Collections.Immutable;

namespace CyberCloud.Billing.Tests;

/// <summary>
///     <see cref="MoneyRounding" />'s rules, at their boundaries — docs/plan/22 § Rating, and the
///     remarks on <see cref="MoneyRounding" /> for the rules themselves.
/// </summary>
public sealed class MoneyRoundingTests {
    [Theory]
    // ⚠ The distinguishing case: banker's rounding (Math.Round's default) prints 0.12.
    [InlineData("0.125", "EUR", "0.13")]
    [InlineData("-0.125", "EUR", "-0.13")]
    [InlineData("0.005", "EUR", "0.01")]
    [InlineData("0.00499999999", "EUR", "0.00")]
    [InlineData("2.675", "EUR", "2.68")]
    [InlineData("2.665", "EUR", "2.67")]
    [InlineData("12.5", "JPY", "13")]
    [InlineData("-12.5", "JPY", "-13")]
    [InlineData("12.4999", "JPY", "12")]
    public void AHalfGoesAwayFromZeroToTheCurrencysMinorUnit(string amount, string currency, string expected) =>
        MoneyRounding.Round(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency)
            .GetValueOrThrow()
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void AnUnknownCurrencyIsRefusedRatherThanAssumedToHaveTwoDecimals() {
        var refused = MoneyRounding.Round(1.005m, "XAU");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("EUR");
    }

    /// <summary>
    ///     ⚠ Rule 2: an invoice line is rounded once, after its hours are summed. 744 hours of a public
    ///     IP at 0.004 € is 2.976 € — 2.98 € on the invoice, where rounding each hour first would have
    ///     billed nothing at all.
    /// </summary>
    [Fact]
    public void AMonthOfSubCentHoursIsBilledAndNotRoundedAway() {
        var subscription = Guid.NewGuid();
        var hours = Enumerable.Range(0, 744)
            .Select(i => new RatedHour(
                    Guid.Empty,
                    "/x",
                    BillingMeter.PublicIpHours,
                    new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i),
                    new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.Zero).AddHours(i),
                    1m,
                    0.004m,
                    "EUR",
                    default
                )
            )
            .ToList();

        var line = InvoiceBuilder.Lines(subscription, hours, "EUR").GetValueOrThrow().ShouldHaveSingleItem();

        line.Amount.ShouldBe(2.98m);
        hours.Sum(static x => MoneyRounding.Round(x.Amount, "EUR").GetValueOrThrow()).ShouldBe(0m, "the per-hour rounding this rule refuses");
    }

    [Fact]
    public void MinorUnitsAreExactAndAnUnroundedAmountIsRefused() {
        MoneyRounding.ToMinorUnits(12.34m, "EUR").GetValueOrThrow().ShouldBe(1234L);
        MoneyRounding.ToMinorUnits(1234m, "JPY").GetValueOrThrow().ShouldBe(1234L);

        var refused = MoneyRounding.ToMinorUnits(12.345m, "EUR");
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("MoneyRounding.Round");
    }

    [Fact]
    public void AnInvoiceAddsUpAsPrintedAndTaxIsRoundedOnceOnTheSubtotal() {
        // Three lines each rounding up by 0.004: the subtotal is the sum of what is printed, and the tax
        // is computed on that subtotal — never the sum of three rounded taxes.
        ImmutableArray<InvoiceLine> lines = [
            new() { Amount = 0.01m, Meter = BillingMeter.VCpuHours },
            new() { Amount = 0.01m, Meter = BillingMeter.MemoryGbHours },
            new() { Amount = 0.01m, Meter = BillingMeter.PublicIpHours }
        ];

        var invoice = InvoiceBuilder.Draft(
                Guid.NewGuid(),
                new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                BillingCluster.Issuer,
                new() { LegalName = "A", Country = "CZ", Currency = "EUR" },
                lines,
                [],
                Tax.EuVatTaxService.Committed
            )
            .GetValueOrThrow();

        invoice.Subtotal.ShouldBe(0.03m);
        invoice.Tax.Amount.ShouldBe(0.01m, "21 % of 0.03 is 0.0063, which rounds to 0.01 once — three per-line taxes would be 0.00");
        invoice.Total.ShouldBe(0.04m);
    }
}
