using System.Collections.Immutable;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     One meter's fixed properties: its kind, its unit, and the quota family it integrates.
/// </summary>
/// <param name="Meter">The meter.</param>
/// <param name="Kind">State-based or event-based — docs/plan/22 § Two kinds of meter.</param>
/// <param name="Unit">The unit as a price list and an invoice line will spell it.</param>
/// <param name="Family">
///     The <see cref="QuotaMeter" /> stock this meter integrates, or <see cref="QuotaMeter.Unknown" />
///     for an event-based meter, which integrates nothing.
/// </param>
/// <param name="Period">
///     How long one unit of the meter is, for a state-based meter — an hour for <c>*Hours</c>, a
///     month for <c>*Months</c>. <see cref="TimeSpan.Zero" /> for an event-based meter, which is a
///     count and has no period.
/// </param>
public readonly record struct MeterDefinition(
    BillingMeter Meter,
    MeterKind Kind,
    string Unit,
    QuotaMeter Family,
    TimeSpan Period
);

/// <summary>
///     The one place the platform's two meter vocabularies meet.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/08 § The provider registry and docs/plan/06 § Quota disagree, and 08 is the
///         one that is wrong.</b> 08's registry example writes
///         <c>.Meters(Meter.VCpuHours, Meter.StorageGbMonths, Meter.BackupGbMonths)</c> — those are
///         <i>billing</i> meters. 06 § Quota's families are <c>vcpu</c>, <c>memoryGb</c>,
///         <c>storageGb</c>, <c>publicIps</c>, <c>clusters</c>, <c>resources</c>, and that is what
///         <c>IQuotaGrain.TryReserveAsync</c> takes and what the write path reserves against. The
///         resolution is 06's, for two reasons that are independent of which document is nicer:
///     </para>
///     <list type="number">
///         <item>
///             <b>A GB-month is not a reservable quantity.</b> A reservation is a claim on a stock
///             that exists at an instant and is released or committed within the hour
///             (<c>QuotaGrain.LeaseDuration</c>). "Reserve 40 GB-months" names no instant and can
///             neither be exceeded nor released. Half of docs/plan/06 § Quota's mechanism —
///             "Reservation, not a counter" — is undefined on it.
///         </item>
///         <item>
///             <b>06 owns the grain the write path actually calls.</b>
///             <c>CyberCloud.ResourceManager.Contracts.Registry.MeterRegistration</c> already takes
///             <see cref="QuotaMeter" />, so the registry as built matches 06 and not 08. 08's
///             example is prose that was never true of the code.
///         </item>
///     </list>
///     <para>
///         <b>So there is exactly one declared vocabulary and it is <see cref="QuotaMeter" />.</b>
///         Billing meters are <i>derived</i> here, by a total function, rather than declared a
///         second time on the registry. That is deliberate and it is the same argument
///         docs/plan/08 § The provider registry makes for the registry itself — "the same registry
///         that generates the CLI is the one that validates the request body … that identity is what
///         makes drift impossible rather than merely detectable". A provider that declared
///         <c>.Meters(Vcpu)</c> for quota and forgot <c>.BillingMeters(VCpuHours)</c> would be
///         quota-limited and free, and nothing would report it.
///     </para>
///     <para>
///         ⚠ <b>What that costs, stated rather than hidden: event-based meters have no quota family
///         and therefore cannot be derived.</b> <see cref="BillingMeter.Requests" />,
///         <see cref="BillingMeter.EgressGb" /> and <see cref="BillingMeter.MessagesSent" /> map to
///         <see cref="QuotaMeter.Unknown" /> and no registry declaration reaches them — a provider
///         emits them through <see cref="IUsageEmitter" /> and the catalogue's job is only to say
///         they are event-based so nothing tries to sample them. Declaring event-based meters per
///         resource type is a <c>.BillingMeters(…)</c> addition owed to
///         <c>CyberCloud.ResourceManager.Contracts.Registry.IResourceTypeBuilder</c>; it is not in
///         this assembly's reach and is not M1's problem, because M1 has no event-based provider.
///     </para>
/// </remarks>
public static class MeterCatalog {
    /// <summary>
    ///     The billing month, for <c>*GbMonths</c>: 730 hours.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A fixed 730 hours, not a calendar month, and the difference is a bug report waiting
    ///     to happen either way.</b> 730 is 8 760 / 12 — the convention every cloud with a GB-month
    ///     meter uses. A calendar month would mean the same disk costs 7 % more in March than in
    ///     February, which is correct only if the price list says so and is a support ticket in
    ///     every other case. Because accrual is per five-minute window and each window contributes
    ///     <c>windowLength / 730 h</c>, a 31-day month bills 31 × 24 / 730 = 1.019 months and a
    ///     28-day month bills 0.921 — the meter reports elapsed time honestly and the price list
    ///     decides what a "month" costs. Rating is M2 (docs/plan/22 § Effort) and this constant is
    ///     the thing it will be handed.
    /// </remarks>
    public static readonly TimeSpan BillingMonth = TimeSpan.FromHours(730);

    static readonly ImmutableArray<MeterDefinition> All = [
        new(BillingMeter.VCpuHours, MeterKind.StateBased, "vCPU-hour", QuotaMeter.Vcpu, TimeSpan.FromHours(1)),
        new(BillingMeter.MemoryGbHours, MeterKind.StateBased, "GiB-hour", QuotaMeter.MemoryGb, TimeSpan.FromHours(1)),
        new(BillingMeter.StorageGbMonths, MeterKind.StateBased, "GiB-month", QuotaMeter.StorageGb, BillingMonth),
        new(BillingMeter.BackupGbMonths, MeterKind.StateBased, "GiB-month", QuotaMeter.StorageGb, BillingMonth),
        new(BillingMeter.PublicIpHours, MeterKind.StateBased, "IP-hour", QuotaMeter.PublicIps, TimeSpan.FromHours(1)),
        new(BillingMeter.ClusterHours, MeterKind.StateBased, "cluster-hour", QuotaMeter.Clusters, TimeSpan.FromHours(1)),
        new(BillingMeter.ResourceHours, MeterKind.StateBased, "resource-hour", QuotaMeter.Resources, TimeSpan.FromHours(1)),
        new(BillingMeter.Requests, MeterKind.EventBased, "request", QuotaMeter.Unknown, TimeSpan.Zero),
        new(BillingMeter.EgressGb, MeterKind.EventBased, "GiB", QuotaMeter.Unknown, TimeSpan.Zero),
        new(BillingMeter.MessagesSent, MeterKind.EventBased, "message", QuotaMeter.Unknown, TimeSpan.Zero)
    ];

    static readonly ImmutableDictionary<BillingMeter, MeterDefinition> ByMeter =
        All.ToImmutableDictionary(x => x.Meter);

    static readonly ImmutableDictionary<QuotaMeter, ImmutableArray<BillingMeter>> ByFamily =
        All
            .Where(x => x.Family != QuotaMeter.Unknown)
            .GroupBy(x => x.Family)
            .ToImmutableDictionary(x => x.Key, x => x.Select(y => y.Meter).ToImmutableArray());

    /// <summary>Every meter the platform knows, in declaration order.</summary>
    public static ImmutableArray<MeterDefinition> Definitions => All;

    /// <summary>One meter's definition.</summary>
    /// <param name="meter">The meter. <see cref="BillingMeter.Unknown" /> has no definition.</param>
    /// <returns>The definition, or a failure naming the meter.</returns>
    public static Result<MeterDefinition> Define(BillingMeter meter) =>
        ByMeter.TryGetValue(meter, out var definition)
            ? Result<MeterDefinition>.Success(definition)
            : Result<MeterDefinition>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{meter}' is not a meter this platform declares. It knows "
                + $"[{string.Join(", ", All.Select(x => x.Meter))}] — MeterCatalog is the whole "
                + "vocabulary and there is no second list."
            );

    /// <summary>Whether a meter is sampled or emitted.</summary>
    /// <param name="meter">The meter.</param>
    /// <returns>Its kind, or <see cref="MeterKind.Unknown" /> for a meter that is not declared.</returns>
    public static MeterKind KindOf(BillingMeter meter) =>
        ByMeter.TryGetValue(meter, out var definition) ? definition.Kind : MeterKind.Unknown;

    /// <summary>The quota family a state-based meter integrates.</summary>
    /// <param name="meter">The meter.</param>
    /// <returns>
    ///     The family, or <see cref="QuotaMeter.Unknown" /> for an event-based meter — which
    ///     integrates nothing and is not reservable.
    /// </returns>
    public static QuotaMeter FamilyOf(BillingMeter meter) =>
        ByMeter.TryGetValue(meter, out var definition) ? definition.Family : QuotaMeter.Unknown;

    /// <summary>
    ///     The billing meters a quota family accrues — the derivation that makes one declaration do
    ///     both jobs.
    /// </summary>
    /// <param name="family">
    ///     A family the registry declared and the write path reserves against.
    /// </param>
    /// <returns>
    ///     Every meter that integrates it, possibly more than one — <see cref="QuotaMeter.StorageGb" />
    ///     accrues both <see cref="BillingMeter.StorageGbMonths" /> and
    ///     <see cref="BillingMeter.BackupGbMonths" />, because a provider's storage draw covers both
    ///     and only the provider knows the split. ⚠ Empty for <see cref="QuotaMeter.Unknown" />,
    ///     which is not a family.
    /// </returns>
    public static ImmutableArray<BillingMeter> MetersOf(QuotaMeter family) =>
        ByFamily.TryGetValue(family, out var meters) ? meters : [];

    /// <summary>
    ///     Integrates a stock over a window — the whole of a state-based meter's arithmetic.
    /// </summary>
    /// <param name="meter">The meter. Must be state-based.</param>
    /// <param name="stock">
    ///     How much of the quota family exists, at the moment of the sample. Provisioned, not used.
    /// </param>
    /// <param name="window">How long the window is.</param>
    /// <returns>
    ///     <c>stock × window / period</c>, or a failure when the meter is not state-based, the stock
    ///     is negative, or the window is not positive.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Decimal, and rounded to 12 places rather than left exact.</b> A five-minute window
    ///     of one GiB is <c>1 / 8 760</c> of a GB-month, which has no terminating decimal
    ///     representation; leaving the division unrounded means the sum of 288 windows depends on
    ///     the order they are added in, and a billing figure that depends on addition order cannot
    ///     be reproduced when a customer disputes it. Twelve places is far below any price and far
    ///     above the accumulated error of a month of samples.
    /// </remarks>
    public static Result<decimal> Accrue(BillingMeter meter, decimal stock, TimeSpan window) {
        var defined = Define(meter);
        if (defined.TryGetError(out var error)) {
            return Result<decimal>.Failure(error);
        }

        var definition = defined.GetValueOrThrow();

        if (definition.Kind != MeterKind.StateBased) {
            return Result<decimal>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{meter}' is {definition.Kind} and cannot be accrued from a stock. "
                + "docs/plan/22 § Two kinds of meter: sampling an event-based meter \"would miss "
                + "everything between samples\". It is emitted by the provider at the moment, "
                + "through IUsageEmitter."
            );
        }

        if (stock < 0) {
            return Result<decimal>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A stock cannot be negative; {stock} is. A negative meter is a billing figure "
                + "nobody can explain."
            );
        }

        if (window <= TimeSpan.Zero) {
            return Result<decimal>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A sample window must be positive; {window} is not."
            );
        }

        var ratio = (decimal)window.Ticks / definition.Period.Ticks;
        return Result<decimal>.Success(Math.Round(stock * ratio, 12, MidpointRounding.ToEven));
    }
}
