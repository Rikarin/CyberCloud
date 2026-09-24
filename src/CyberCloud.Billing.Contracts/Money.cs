using System.Collections.Frozen;
using System.Globalization;

namespace CyberCloud.Billing.Contracts;

/// <summary>
///     The currencies a price sheet may be written in, and how many decimal places each one's minor
///     unit has — ISO 4217's exponent.
/// </summary>
/// <remarks>
///     ⚠ <b>A closed set, and deliberately small.</b> A currency code the platform has not listed is
///     refused rather than assumed to have two decimals: <c>JPY</c> has none, and an invoice that
///     printed yen to two places would be a rounding error on every line. Adding a currency is one
///     line here and one test in <c>MoneyRoundingTests</c>.
/// </remarks>
public static class Currencies {
    /// <summary>The currency every committed price sheet version is written in today.</summary>
    public const string Euro = "EUR";

    static readonly FrozenDictionary<string, int> Exponents = new Dictionary<string, int>(StringComparer.Ordinal) {
        ["EUR"] = 2,
        ["USD"] = 2,
        ["GBP"] = 2,
        ["CHF"] = 2,
        ["CZK"] = 2,
        ["PLN"] = 2,
        ["SEK"] = 2,
        ["DKK"] = 2,
        ["NOK"] = 2,
        ["HUF"] = 2,
        ["JPY"] = 0
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every currency code the platform prices in, ordinally.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Exponents.Keys.Order(StringComparer.Ordinal)];

    /// <summary>How many decimal places a currency's minor unit has.</summary>
    /// <param name="currency">An ISO 4217 code, upper case — <c>EUR</c>, not <c>eur</c>.</param>
    /// <returns>The exponent, or <see cref="ErrorCode.InvalidRequestBody" /> naming the closed set.</returns>
    public static Result<int> ExponentOf(string? currency) =>
        currency is not null && Exponents.TryGetValue(currency, out var exponent)
            ? Result<int>.Success(exponent)
            : Result<int>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{currency}' is not a currency this platform prices in. The set is closed and is "
                + $"[{string.Join(", ", All)}] — Currencies in CyberCloud.Billing.Contracts."
            );

    /// <summary>Whether the platform prices in a currency.</summary>
    /// <param name="currency">An ISO 4217 code.</param>
    public static bool IsKnown(string? currency) => currency is not null && Exponents.ContainsKey(currency);
}

/// <summary>
///     The rounding rules of docs/plan/22 § Rating, in one place: where money is rounded, to what, and
///     which way a half goes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rules, in the order an invoice meets them.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Rating never rounds.</b> One hour of one meter on one resource is priced at full
///             <see cref="decimal" /> precision — quantity × unit price per tier portion — and carried
///             unrounded. An hour of a public IP at 0.004 € is 0.004 €, not 0.00 €.
///         </item>
///         <item>
///             <b>An invoice line is rounded once</b>, to the currency's minor unit, after its hours
///             are summed. ⚠ Rounding per hour and summing would lose the whole of that IP: 744 hours
///             of 0.004 € is 2.976 € and rounds to 2.98 €, whereas 744 hours each rounded to 0.00 € is
///             nothing. <c>MoneyRoundingTests.AMonthOfSubCentHoursIsBilledAndNotRoundedAway</c> pins it.
///         </item>
///         <item>
///             <b>The subtotal is the sum of the rounded lines</b>, so an invoice adds up as printed.
///         </item>
///         <item>
///             <b>Tax is computed once on the subtotal and rounded once.</b> Not per line: per-line
///             tax rounding makes the tax on a hundred small lines differ from the tax on their sum,
///             and the printed rate times the printed base would not equal the printed tax.
///         </item>
///         <item>
///             <b>A half goes away from zero</b> (<see cref="MidpointRounding.AwayFromZero" />) —
///             0.125 € is 0.13 € and −0.125 € is −0.13 €. It is the commercial convention the EU
///             member states' VAT rules assume, it is what a customer checking a line with a
///             calculator gets, and it is symmetric, so a credit note for a whole invoice is exactly
///             the invoice negated. ⚠ Not banker's rounding, which is what
///             <see cref="Math.Round(decimal, int)" /> does by default and which would print 0.12 €.
///         </item>
///         <item>
///             <b>A cost view is not a document.</b> Its rows are rounded for display and its total
///             is the unrounded sum rounded once, so the rows of a cost view can differ from its total
///             by up to half a minor unit per row. The invoice is the document of record.
///         </item>
///     </list>
/// </remarks>
public static class MoneyRounding {
    /// <summary>Rounds an amount to its currency's minor unit, half away from zero.</summary>
    /// <param name="amount">The unrounded amount. May be negative — a credit.</param>
    /// <param name="currency">An ISO 4217 code in <see cref="Currencies" />.</param>
    /// <returns>The rounded amount, or a failure naming an unknown currency.</returns>
    public static Result<decimal> Round(decimal amount, string currency) {
        var exponent = Currencies.ExponentOf(currency);

        return exponent.TryGetError(out var error)
            ? Result<decimal>.Failure(error)
            : Result<decimal>.Success(Math.Round(amount, exponent.GetValueOrThrow(), MidpointRounding.AwayFromZero));
    }

    /// <summary>
    ///     A rounded amount in minor units — cents for <c>EUR</c>, yen for <c>JPY</c> — which is what a
    ///     payment service provider is handed.
    /// </summary>
    /// <param name="amount">An amount already rounded by <see cref="Round" />.</param>
    /// <param name="currency">Its currency.</param>
    /// <returns>
    ///     The integer amount, or <see cref="ErrorCode.InvalidRequestBody" /> for an amount with more
    ///     decimals than the currency has — which means it was never rounded, and guessing would move
    ///     money.
    /// </returns>
    public static Result<long> ToMinorUnits(decimal amount, string currency) {
        var exponent = Currencies.ExponentOf(currency);
        if (exponent.TryGetError(out var error)) {
            return Result<long>.Failure(error);
        }

        var scaled = amount * Pow10(exponent.GetValueOrThrow());

        return scaled == decimal.Truncate(scaled)
            ? Result<long>.Success((long)scaled)
            : Result<long>.Failure(
                ErrorCode.InvalidRequestBody,
                amount.ToString(CultureInfo.InvariantCulture)
                + $" {currency} has more decimal places than {currency} has. Round it with "
                + "MoneyRounding.Round first; converting an unrounded amount would move money by "
                + "whatever the truncation dropped."
            );
    }

    /// <summary>An amount as an invoice prints it — invariant, with exactly the currency's decimals.</summary>
    /// <param name="amount">A rounded amount.</param>
    /// <param name="currency">Its currency.</param>
    public static string Format(decimal amount, string currency) {
        var exponent = Currencies.ExponentOf(currency).TryGetValue(out var known) ? known : 2;
        return amount.ToString("F" + exponent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            + " "
            + currency;
    }

    static decimal Pow10(int exponent) {
        var result = 1m;
        for (var i = 0; i < exponent; i++) {
            result *= 10m;
        }

        return result;
    }
}
