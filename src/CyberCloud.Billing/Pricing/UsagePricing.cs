using System.Collections.Immutable;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Billing.Pricing;

/// <summary>
///     Reads a subscription's usage ledger and rates it — the one path from metering to money, shared
///     by the invoice, the cost query and the budget so that the three can never disagree about what
///     an hour cost.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Reads the whole ledger and filters, and that is an M2 size decision.</b>
///         <c>IUsageLedgerGrain.ListAsync</c> returns every entry the subscription has ever recorded;
///         a month is 24 × 31 entries per resource per meter, so a year of a hundred resources is a
///         few hundred thousand records per read. The range read belongs on the ledger, and the
///         analytical copy of it in ClickHouse's <c>usage_hourly</c> is where a cost view should
///         eventually read — docs/plan/22 § What is owed, <c>ledger-reads-are-whole</c>.
///     </para>
///     <para>
///         ⚠ <b>Two steps, because who climbs the ladder is a visibility decision.</b>
///         <see cref="RateAsync" /> prices the whole subscription, which is what the invoice charges
///         and what a subscription reader may see. A caller who may see less nets with
///         <see cref="NetAsync" />, keeps the hours it may see, and prices those alone with
///         <see cref="Price" /> — <see cref="Rating.PriceMonth" />'s remarks say what the whole
///         subscription's ladder would otherwise tell it.
///     </para>
///     <para>
///         ⚠ <b>Every <c>GetGrain</c> is qualified with <c>ForTenant</c>.</b> This is a singleton the
///         three grains share, not a grain, so the call filter does not see it — CC1006.
///     </para>
/// </remarks>
/// <param name="grains">The silo's grain factory.</param>
/// <param name="sheet">The prices.</param>
public sealed class UsagePricing(IGrainFactory grains, PriceSheet sheet) {
    /// <summary>The prices this instance rates against.</summary>
    public PriceSheet Sheet => sheet;

    /// <summary>One subscription's usage for every calendar month a period touches, rated.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="from">The start of the period.</param>
    /// <param name="to">The end of the period, exclusive.</param>
    /// <returns>
    ///     Every rated hour whose window starts in <c>[from, to)</c>. ⚠ Each month is rated whole and
    ///     then filtered, because an hour's price depends on every hour before it in its month — the
    ///     tier ladder <see cref="Rating" /> climbs.
    /// </returns>
    public async Task<Result<ImmutableArray<RatedHour>>> RateAsync(
        Guid tenantId,
        Guid subscriptionId,
        DateTimeOffset from,
        DateTimeOffset to
    ) {
        var netted = await NetAsync(tenantId, subscriptionId, from, to);
        return netted.TryGetError(out var error)
            ? Result<ImmutableArray<RatedHour>>.Failure(error)
            : Price(netted.GetValueOrThrow(), from, to);
    }

    /// <summary>
    ///     One subscription's usage for every calendar month a period touches, netted and not priced.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="from">The start of the period.</param>
    /// <param name="to">The end of the period, exclusive.</param>
    /// <returns>
    ///     Every netted hour of every month the period touches — whole months, not only the period,
    ///     because <see cref="Price" /> needs each month's hours before the period to climb its
    ///     ladder. A failure only when the ledger can't be read.
    /// </returns>
    public async Task<Result<ImmutableArray<UsageHour>>> NetAsync(
        Guid tenantId,
        Guid subscriptionId,
        DateTimeOffset from,
        DateTimeOffset to
    ) {
        var ledger = grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscriptionId));

        var listed = await ledger.ListAsync();
        if (listed.TryGetError(out var readError)) {
            return Result<ImmutableArray<UsageHour>>.Failure(readError);
        }

        var entries = listed.GetValueOrThrow();
        var hours = ImmutableArray.CreateBuilder<UsageHour>();

        for (var month = Rating.MonthOf(from); month < to; month = month.AddMonths(1)) {
            var monthly = Rating.NetMonth(entries, month);
            if (monthly.TryGetError(out var netError)) {
                return Result<ImmutableArray<UsageHour>>.Failure(netError);
            }

            hours.AddRange(monthly.GetValueOrThrow());
        }

        return Result<ImmutableArray<UsageHour>>.Success(hours.ToImmutable());
    }

    /// <summary>Prices netted hours, each month's ladder climbed over exactly the hours given.</summary>
    /// <param name="hours">Hours from <see cref="NetAsync" />, all of them or the ones a caller may see.</param>
    /// <param name="from">The start of the period.</param>
    /// <param name="to">The end of the period, exclusive.</param>
    /// <returns>
    ///     Every rated hour whose window starts in <c>[from, to)</c>. A failure when a month with usage
    ///     has no price sheet version, or when an hour given is below zero.
    /// </returns>
    public Result<ImmutableArray<RatedHour>> Price(IReadOnlyCollection<UsageHour> hours, DateTimeOffset from, DateTimeOffset to) {
        ArgumentNullException.ThrowIfNull(hours);

        var rated = ImmutableArray.CreateBuilder<RatedHour>();

        for (var month = Rating.MonthOf(from); month < to; month = month.AddMonths(1)) {
            var monthly = Rating.PriceMonth(sheet, hours, month);
            if (monthly.TryGetError(out var rateError)) {
                return Result<ImmutableArray<RatedHour>>.Failure(rateError);
            }

            rated.AddRange(monthly.GetValueOrThrow().Where(x => x.WindowStart >= from && x.WindowStart < to));
        }

        return Result<ImmutableArray<RatedHour>>.Success(rated.ToImmutable());
    }
}
