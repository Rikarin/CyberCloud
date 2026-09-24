using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Pricing;

/// <summary>
///     One hour of one meter on one resource, netted and not yet priced — what <see cref="Rating.NetMonth" />
///     returns and <see cref="Rating.PriceMonth" /> prices.
/// </summary>
/// <param name="ResourceId">The resource.</param>
/// <param name="ResourcePath">Its path, as the ledger recorded it.</param>
/// <param name="Meter">The meter.</param>
/// <param name="WindowStart">The hour, inclusive.</param>
/// <param name="WindowEnd">The hour, exclusive.</param>
/// <param name="Quantity">
///     The net quantity — the ledger entry plus every correction to it. It can be zero, and it can be
///     negative when a correction is wrong; pricing refuses the second.
/// </param>
public sealed record UsageHour(
    Guid ResourceId,
    string ResourcePath,
    BillingMeter Meter,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    decimal Quantity
) {
    /// <summary>The resource group the path names, or empty when the path does not parse.</summary>
    public string ResourceGroup => ResourcePaths.GroupOf(ResourcePath);
}

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
    public string ResourceGroup => ResourcePaths.GroupOf(ResourcePath);

    /// <summary>The resource type the path names — <c>{namespace}/{type}</c> — or empty.</summary>
    public string ResourceType => ResourcePaths.TypeOf(ResourcePath);
}

/// <summary>The two parts of a ledger path the cost views key by.</summary>
static class ResourcePaths {
    /// <summary>The resource group a path names, or empty when the path does not parse.</summary>
    public static string GroupOf(string path) => ResourceId.TryParsePath(path, out var id) ? id.ResourceGroup : string.Empty;

    /// <summary>The <c>{namespace}/{type}</c> a path names, or empty when the path does not parse.</summary>
    public static string TypeOf(string path) => ResourceId.TryParsePath(path, out var id) ? id.Type.ToString() : string.Empty;
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
///         meter first in the month, so an hour's price reveals how much was used before it —
///         <see cref="PriceMonth" />'s remarks say who that may be shown to.
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

        var netted = NetMonth(entries, monthStart);
        return netted.TryGetError(out var error)
            ? Result<ImmutableArray<RatedHour>>.Failure(error)
            : PriceMonth(sheet, netted.GetValueOrThrow(), monthStart);
    }

    /// <summary>
    ///     Nets one month of a ledger into hours — each original plus its corrections — without pricing
    ///     anything.
    /// </summary>
    /// <param name="entries">
    ///     The subscription's ledger — every entry, or any superset of the month's; entries outside the
    ///     month are ignored.
    /// </param>
    /// <param name="monthStart">The first instant of the month, UTC.</param>
    /// <returns>
    ///     One hour per (resource, meter, hour) the month has entries for, zero and negative net
    ///     quantities included, in no particular order. A failure only when
    ///     <paramref name="monthStart" /> isn't a month's first instant.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Apart from pricing so that a caller can choose whose hours climb the ladder.</b> The cost
    ///     query decides what a caller may see on netted hours, and prices only those when the caller
    ///     may not read the whole subscription — <see cref="PriceMonth" />'s remarks say why.
    /// </remarks>
    public static Result<ImmutableArray<UsageHour>> NetMonth(IEnumerable<UsageLedgerEntry> entries, DateTimeOffset monthStart) {
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsMonthStart(monthStart)) {
            return Result<ImmutableArray<UsageHour>>.Failure(NotAMonth(monthStart));
        }

        var monthEnd = monthStart.AddMonths(1);
        var hours = new Dictionary<(Guid, BillingMeter, DateTimeOffset, DateTimeOffset), (decimal Quantity, string Path)>();

        foreach (var entry in entries) {
            if (entry.WindowStart < monthStart || entry.WindowStart >= monthEnd) {
                continue;
            }

            var key = (entry.ResourceId, entry.Meter, entry.WindowStart, entry.WindowEnd);
            var path = !entry.IsCorrection || !hours.TryGetValue(key, out var seen) ? entry.ResourcePath : seen.Path;
            hours[key] = (hours.TryGetValue(key, out var held) ? held.Quantity + entry.Quantity : entry.Quantity, path);
        }

        return Result<ImmutableArray<UsageHour>>.Success(
            [.. hours.Select(static x => new UsageHour(x.Key.Item1, x.Value.Path, x.Key.Item2, x.Key.Item3, x.Key.Item4, x.Value.Quantity))]
        );
    }

    /// <summary>Prices one month of netted hours, climbing each meter's ladder over exactly the hours given.</summary>
    /// <param name="sheet">The prices.</param>
    /// <param name="hours">Netted hours, from <see cref="NetMonth" />; any outside the month are ignored.</param>
    /// <param name="monthStart">The first instant of the month, UTC.</param>
    /// <returns>
    ///     One rated hour per hour with a non-zero net quantity, in window order. A failure when the
    ///     month has usage and no price sheet version, or when an hour's net quantity is below zero.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The ladder is the hours passed in, and that decides whose usage a figure can reveal.</b>
    ///     Given the whole subscription's month, an hour's price depends on everyone's usage before it:
    ///     80 GiB of egress costs nothing when it's the month's first and 3.00 € when another group used
    ///     80 GiB earlier, so the figure tells whoever reads it how much the rest of the subscription
    ///     used. Given only the hours one caller may see, the figure depends on nothing else
    ///     (<c>CostVisibilityTests.AGroupReadersFiguresDoNotMoveWithAnotherGroupsUsage</c>).
    /// </remarks>
    public static Result<ImmutableArray<RatedHour>> PriceMonth(
        PriceSheet sheet,
        IEnumerable<UsageHour> hours,
        DateTimeOffset monthStart
    ) {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(hours);

        if (!IsMonthStart(monthStart)) {
            return Result<ImmutableArray<RatedHour>>.Failure(NotAMonth(monthStart));
        }

        var monthEnd = monthStart.AddMonths(1);
        var month = hours.Where(x => x.WindowStart >= monthStart && x.WindowStart < monthEnd).ToList();

        // A month with no usage is priced at nothing whether or not a price sheet covers it — which
        // is what lets a period that starts before the first version be queried at all.
        if (month.Count == 0) {
            return Result<ImmutableArray<RatedHour>>.Success([]);
        }

        var version = sheet.At(monthStart);
        if (version.TryGetError(out var unpriced)) {
            return Result<ImmutableArray<RatedHour>>.Failure(unpriced);
        }

        var prices = version.GetValueOrThrow();

        // Each meter's ladder, climbed in time order.
        var rated = ImmutableArray.CreateBuilder<RatedHour>(month.Count);
        var climbed = new Dictionary<BillingMeter, decimal>();

        foreach (var hour in month
                     .OrderBy(static x => x.WindowStart)
                     .ThenBy(static x => x.Meter)
                     .ThenBy(static x => x.ResourceId)) {
            if (hour.Quantity < 0) {
                return Result<ImmutableArray<RatedHour>>.Failure(
                    ErrorCode.InternalError,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The ledger's corrections take {hour.Meter} on resource {hour.ResourceId:D} for {hour.WindowStart:O} to {hour.Quantity}, below zero."
                    )
                    + " A correction is a delta and cannot remove more than was recorded; the correction entry is "
                    + "wrong and has to be corrected in turn before the month can be rated."
                );
            }

            if (hour.Quantity == 0) {
                continue;
            }

            if (!prices.Meters.TryGetValue(hour.Meter, out var price)) {
                return Result<ImmutableArray<RatedHour>>.Failure(
                    ErrorCode.InternalError,
                    $"Price sheet version {prices.EffectiveFrom:O} has no price for {hour.Meter}. PriceSheet.Parse refuses such a sheet."
                );
            }

            var before = climbed.GetValueOrDefault(hour.Meter);
            rated.Add(
                new(
                    hour.ResourceId,
                    hour.ResourcePath,
                    hour.Meter,
                    hour.WindowStart,
                    hour.WindowEnd,
                    hour.Quantity,
                    Climb(price.Tiers, before, hour.Quantity),
                    price.Currency,
                    prices.EffectiveFrom
                )
            );
            climbed[hour.Meter] = before + hour.Quantity;
        }

        return Result<ImmutableArray<RatedHour>>.Success(rated.ToImmutable());
    }

    static Error NotAMonth(DateTimeOffset monthStart) =>
        new(ErrorCode.InvalidRequestBody, string.Create(CultureInfo.InvariantCulture, $"{monthStart:O} is not the first instant of a month in UTC."));

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
