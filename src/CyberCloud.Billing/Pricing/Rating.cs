using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Pricing;

/// <summary>
///     One hour of one meter on one resource, priced — the rated line item docs/plan/22 § The
///     pipeline's <c>rating (meter × plan × price) → charges</c> produces.
/// </summary>
/// <param name="ResourceId">The resource.</param>
/// <param name="ResourcePath">Its path, as the ledger recorded it.</param>
/// <param name="Meter">The meter.</param>
/// <param name="WindowStart">The hour, inclusive.</param>
/// <param name="WindowEnd">The hour, exclusive.</param>
/// <param name="Quantity">The net quantity — the ledger entry plus every correction to it.</param>
/// <param name="Amount">
///     The price, unrounded — <see cref="MoneyRounding" />, rule 1. The sum over the tier portions this
///     hour spans.
/// </param>
/// <param name="Currency">The price's currency.</param>
/// <param name="PriceVersion">The effective date of the price sheet version it was priced against.</param>
public sealed record RatedHour(
    Guid ResourceId,
    string ResourcePath,
    BillingMeter Meter,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    decimal Quantity,
    decimal Amount,
    string Currency,
    DateTimeOffset PriceVersion
) {
    /// <summary>The resource group the path names, or empty when the path does not parse.</summary>
    public string ResourceGroup => Parsed is { } id ? id.ResourceGroup : string.Empty;

    /// <summary>The resource type the path names — <c>{namespace}/{type}</c> — or empty.</summary>
    public string ResourceType => Parsed is { } id ? id.Type.ToString() : string.Empty;

    // ⚠ Qualified: the positional member ResourceId is a Guid and shadows the type inside this record.
    Core.Resources.ResourceId? Parsed => Core.Resources.ResourceId.TryParsePath(ResourcePath, out var id) ? id : null;
}

/// <summary>
///     The rating engine: a subscription's usage ledger for one month, priced. docs/plan/22 § Rating.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Tiers are walked over the month's usage in time order, and that is what makes the tier
///             rule and a per-resource cost agree.
///         </b> docs/plan/22: tiered pricing "must be computed
///         against the monthly aggregate, not per event — a common and expensive mistake." A monthly
///         aggregate alone cannot say what one resource cost, and a cost view and a budget need exactly
///         that. So the ladder is climbed hour by hour in window order — resource id breaking ties —
///         and each hour is charged for the tier portions it actually occupies. The sum over a month is
///         what pricing the monthly aggregate would give, to the last digit
///         (<c>RatingTests.PricingEveryHourSumsToPricingTheMonthlyAggregate</c>), and every hour still
///         has its own figure. The consequence to know: the free tier is consumed by whoever used the
///         meter first in the month.
///     </para>
///     <para>
///         <b>A correction nets into its hour before anything is priced.</b> The usage ledger's
///         corrections are deltas pointing at an original (<c>IUsageLedgerGrain</c>), so an hour's
///         quantity is the original plus its corrections, and a draft invoice rated after a correction
///         is rated as if the figure had always been right. After the month is finalized a correction
///         cannot move the invoice; it becomes a credit note — <c>IBillingAccountGrain.ProposeCorrectionAsync</c>.
///     </para>
///     <para>
///         ⚠ <b>Nothing here rounds</b> — <see cref="MoneyRounding" />, rule 1.
///     </para>
/// </remarks>
public static class Rating {
    /// <summary>The first instant of the calendar month an instant falls in, UTC.</summary>
    /// <param name="instant">Any instant.</param>
    public static DateTimeOffset MonthOf(DateTimeOffset instant) {
        var utc = instant.ToUniversalTime();
        return new(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Whether an instant is the first instant of a month, UTC.</summary>
    /// <param name="instant">The candidate.</param>
    public static bool IsMonthStart(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero && MonthOf(instant) == instant;

    /// <summary>Prices one subscription's usage for one calendar month.</summary>
    /// <param name="sheet">The prices.</param>
    /// <param name="entries">
    ///     The subscription's ledger — every entry, or any superset of the month's; entries outside the
    ///     month are ignored.
    /// </param>
    /// <param name="monthStart">The first instant of the month, UTC.</param>
    /// <returns>
    ///     One rated hour per (resource, meter, hour) with a non-zero net quantity, in window order.
    ///     A failure when the month has no price sheet version, or when corrections have taken an
    ///     hour below zero — which the ledger can hold and no price can mean.
    /// </returns>
    public static Result<ImmutableArray<RatedHour>> RateMonth(
        PriceSheet sheet,
        IEnumerable<UsageLedgerEntry> entries,
        DateTimeOffset monthStart
    ) {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsMonthStart(monthStart)) {
            return Result<ImmutableArray<RatedHour>>.Failure(
                ErrorCode.InvalidRequestBody,
                string.Create(CultureInfo.InvariantCulture, $"{monthStart:O} is not the first instant of a month in UTC.")
            );
        }

        var monthEnd = monthStart.AddMonths(1);

        // ── 1. Net every hour: the original plus its corrections. ──────────────────────────────
        var hours = new Dictionary<(Guid, BillingMeter, DateTimeOffset, DateTimeOffset), (decimal Quantity, string Path)>();

        foreach (var entry in entries) {
            if (entry.WindowStart < monthStart || entry.WindowStart >= monthEnd) {
                continue;
            }

            var key = (entry.ResourceId, entry.Meter, entry.WindowStart, entry.WindowEnd);
            var path = !entry.IsCorrection || !hours.TryGetValue(key, out var seen) ? entry.ResourcePath : seen.Path;
            hours[key] = (hours.TryGetValue(key, out var held) ? held.Quantity + entry.Quantity : entry.Quantity, path);
        }

        // A month with no usage is priced at nothing whether or not a price sheet covers it — which
        // is what lets a period that starts before the first version be queried at all.
        if (hours.Count == 0) {
            return Result<ImmutableArray<RatedHour>>.Success([]);
        }

        var version = sheet.At(monthStart);
        if (version.TryGetError(out var unpriced)) {
            return Result<ImmutableArray<RatedHour>>.Failure(unpriced);
        }

        var prices = version.GetValueOrThrow();

        // ── 2. Walk each meter's ladder in time order. ─────────────────────────────────────────
        var rated = ImmutableArray.CreateBuilder<RatedHour>(hours.Count);
        var climbed = new Dictionary<BillingMeter, decimal>();

        foreach (var ((resource, meter, start, end), (quantity, path)) in hours
                     .OrderBy(static x => x.Key.Item3)
                     .ThenBy(static x => x.Key.Item2)
                     .ThenBy(static x => x.Key.Item1)) {
            if (quantity < 0) {
                return Result<ImmutableArray<RatedHour>>.Failure(
                    ErrorCode.InternalError,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The ledger's corrections take {meter} on resource {resource:D} for {start:O} to {quantity}, below zero."
                    )
                    + " A correction is a delta and cannot remove more than was recorded; the correction entry is "
                    + "wrong and has to be corrected in turn before the month can be rated."
                );
            }

            if (quantity == 0) {
                continue;
            }

            if (!prices.Meters.TryGetValue(meter, out var price)) {
                return Result<ImmutableArray<RatedHour>>.Failure(
                    ErrorCode.InternalError,
                    $"Price sheet version {prices.EffectiveFrom:O} has no price for {meter}. PriceSheet.Parse refuses such a sheet."
                );
            }

            var before = climbed.GetValueOrDefault(meter);
            rated.Add(
                new(resource, path, meter, start, end, quantity, Climb(price.Tiers, before, quantity), price.Currency, prices.EffectiveFrom)
            );
            climbed[meter] = before + quantity;
        }

        return Result<ImmutableArray<RatedHour>>.Success(rated.ToImmutable());
    }

    /// <summary>
    ///     The price of <paramref name="quantity" /> units taken from a tier ladder that has already
    ///     been climbed to <paramref name="before" />.
    /// </summary>
    /// <param name="tiers">The ladder, ascending, the last tier open-ended.</param>
    /// <param name="before">The cumulative monthly quantity before these units.</param>
    /// <param name="quantity">The units to price. Non-negative.</param>
    /// <remarks>
    ///     ⚠ <b>A tier boundary inside one hour splits the hour.</b> Fifty GiB-months free and the
    ///     next at 0.08 € means an hour that takes the ladder from 49.9 to 50.2 is 0.1 free and 0.2 at
    ///     the price — <c>RatingTests.AnHourThatCrossesATierBoundaryIsSplitAcrossIt</c> is that boundary.
    /// </remarks>
    public static decimal Climb(ImmutableArray<PriceTier> tiers, decimal before, decimal quantity) {
        var amount = 0m;
        var at = before;
        var remaining = quantity;

        foreach (var tier in tiers) {
            if (remaining <= 0) {
                break;
            }

            if (tier.UpTo is { } upTo && at >= upTo) {
                continue;
            }

            var room = tier.UpTo is { } end ? end - at : remaining;
            var taken = Math.Min(room, remaining);

            amount += taken * tier.UnitPrice;
            at += taken;
            remaining -= taken;
        }

        return amount;
    }
}
