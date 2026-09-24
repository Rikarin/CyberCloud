using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>Who is asking for costs — the two fields of the gateway's caller the ReBAC check reads.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CostCaller")]
public sealed record CostCaller {
    /// <summary>The ReBAC subject type — <c>user</c>, <c>servicePrincipal</c>, <c>managedIdentity</c>.</summary>
    [Id(0)]
    public string SubjectType { get; init; } = string.Empty;

    /// <summary>The subject's id.</summary>
    [Id(1)]
    public string SubjectId { get; init; } = string.Empty;
}

/// <summary>One cost question: a scope, a half-open period and a grouping.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CostQueryRequest")]
public sealed record CostQueryRequest {
    /// <summary>The most days one query may span. A year and a day, so a year-on-year page is one query.</summary>
    public const int MaxDays = 366;

    /// <summary>Who is asking. Every row is filtered by what this subject may read.</summary>
    [Id(0)]
    public CostCaller Caller { get; init; } = new();

    /// <summary>The subscription. The query's grain is keyed by it.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>A resource group to narrow to, or empty for the whole subscription.</summary>
    [Id(2)]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>The start of the period, inclusive. Usage is hourly, so anything finer than an hour is truncated to it.</summary>
    [Id(3)]
    public DateTimeOffset From { get; init; }

    /// <summary>The end of the period, exclusive.</summary>
    [Id(4)]
    public DateTimeOffset To { get; init; }

    /// <summary>How the rows are keyed.</summary>
    [Id(5)]
    public CostGrouping Grouping { get; init; } = CostGrouping.Unknown;
}

/// <summary>One row of a cost answer.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CostRow")]
public sealed record CostRow {
    /// <summary>What the row is — a path, a group name, a type, a meter name or a day.</summary>
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The cost, rounded for display — <see cref="MoneyRounding" />, rule 6.</summary>
    [Id(1)]
    public decimal Amount { get; init; }

    /// <summary>The quantity, for <see cref="CostGrouping.Meter" /> rows; zero for every other grouping, where units do not add.</summary>
    [Id(2)]
    public decimal Quantity { get; init; }
}

/// <summary>A cost answer.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CostQueryResult")]
public sealed record CostQueryResult {
    /// <summary>The currency every amount is in.</summary>
    [Id(0)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>The period answered, after truncation to the hour.</summary>
    [Id(1)]
    public DateTimeOffset From { get; init; }

    /// <summary>The end of the period answered.</summary>
    [Id(2)]
    public DateTimeOffset To { get; init; }

    /// <summary>The grouping.</summary>
    [Id(3)]
    public CostGrouping Grouping { get; init; } = CostGrouping.Unknown;

    /// <summary>The rows the caller may see, most expensive first, then by name.</summary>
    [Id(4)]
    public ImmutableArray<CostRow> Rows { get; init; } = [];

    /// <summary>The unrounded sum of what the caller may see, rounded once.</summary>
    [Id(5)]
    public decimal Total { get; init; }

    /// <summary>
    ///     Whether the caller may read less than the whole scope, so the answer may leave usage out.
    ///     ⚠ A statement about the caller's access and never about the usage: it doesn't change with
    ///     whether anything outside what they read was used, or it would report exactly that.
    /// </summary>
    [Id(6)]
    public bool Filtered { get; init; }

    /// <summary>
    ///     Whether the amounts were priced over what the caller may read alone, as if it were the
    ///     subscription's only usage, because the caller may not read the whole subscription.
    /// </summary>
    /// <remarks>
    ///     ⚠ Tiers are climbed per subscription, so the invoice's price for an hour depends on everyone's
    ///     usage before it. Shown to someone who may read one group, that price would say how much the
    ///     other groups used; priced alone, it says nothing about them. The cost of tiered meters can
    ///     then differ from the invoice's share of it — the free tier counted once for this caller,
    ///     not shared — while untiered meters cost the same either way. Like <see cref="Filtered" />,
    ///     it depends on the caller's access alone.
    /// </remarks>
    [Id(8)]
    public bool PricedAlone { get; init; }
}

/// <summary>
///     The cost of one subscription's usage, priced and filtered by what the caller may read.
///     docs/plan/22 § Cost visibility.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Stateless · <b>Tier</b> none · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Authorization is inside, and it is ReBAC's rather than the gateway's.</b> docs/plan/10
///         § Request pipeline puts enforcement behind the one seam dispatch calls, never in the
///         gateway. A caller who may read the subscription sees every row; otherwise each resource
///         group in the answer is checked for <c>read</c>, then each resource in a group that was
///         not; what is left is the answer. A caller who may read nothing in scope gets
///         <see cref="ErrorCode.ResourceNotFound" /> with the same sentence an absent subscription
///         gets — docs/plan/07 § The enforcement seam's "404, never 403" — and gets it before any
///         failure to price the scope could say otherwise.
///     </para>
///     <para>
///         ⚠ <b>Only a subscription reader sees the subscription's tier ladder.</b> Anyone else's
///         answer is priced over what they may read alone (<see cref="CostQueryResult.PricedAlone" />),
///         so no figure in it moves with usage they may not see.
///     </para>
/// </remarks>
[Alias("CyberCloud.Billing.ICostQueryGrain")]
public interface ICostQueryGrain : IGrainWithStringKey {
    /// <summary>Prices a period of this subscription's usage.</summary>
    /// <param name="request">The question. Its subscription must be this grain's.</param>
    Task<Result<CostQueryResult>> QueryAsync(CostQueryRequest request);
}

/// <summary>What the gateway's dispatch stage holds to ask for costs — the grain, qualified once.</summary>
public interface ICostQuery {
    /// <summary>Asks one subscription's cost grain.</summary>
    /// <param name="tenantId">The token's tenant. Qualifies the grain call.</param>
    /// <param name="request">The question.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<CostQueryResult>> QueryAsync(
        Guid tenantId,
        CostQueryRequest request,
        CancellationToken cancellationToken = default
    );
}
