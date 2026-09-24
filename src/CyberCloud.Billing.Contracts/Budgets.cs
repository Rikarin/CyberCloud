using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>One threshold on a budget: a percentage of the amount, against the actual or the forecast.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetThreshold")]
public sealed record BudgetThreshold {
    /// <summary>The percentage of the budget's amount — <c>80</c> fires at 80 %. Above 100 is allowed.</summary>
    [Id(0)]
    public decimal Percent { get; init; }

    /// <summary>Whether the actual cost or the forecast is compared.</summary>
    [Id(1)]
    public ThresholdKind Kind { get; init; } = ThresholdKind.Unknown;
}

/// <summary>Who hears about a crossed threshold, and through which sending service.</summary>
/// <remarks>
///     ⚠
///     <b>
///         The service is its path and the channel is the body's spelling — no grain id, no
///         <c>ChannelKind</c> — for the reason this assembly's <c>.csproj</c> gives.
///     </b> The provider family that converges a budget binds this type, and the sending module's
///     key derivation or enum here would give that family a module edge it does not need.
///     <c>CyberCloud.Billing</c> derives the service's grain id from the path
///     (<c>CommunicationGrainKeys.ResourceIdFor</c>) and resolves the spelling when it sends.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetNotification")]
public sealed record BudgetNotification {
    /// <summary>The <c>CyberCloud.Communication/services</c> resource's id path, in the budget's own tenant.</summary>
    [Id(0)]
    public string ServicePath { get; init; } = string.Empty;

    /// <summary>The channel, as the body spells it — <c>email</c>, <c>sms</c>.</summary>
    [Id(1)]
    public string Channel { get; init; } = string.Empty;

    /// <summary>Who is told. One send each, every one checked against the service's suppression list.</summary>
    [Id(2)]
    public ImmutableArray<string> Recipients { get; init; } = [];
}

/// <summary>What one <c>CyberCloud.Billing/budgets</c> resource asks for.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetSpec")]
public sealed record BudgetSpec {
    /// <summary>The budget resource's GUID. It is also the budget grain's key.</summary>
    [Id(0)]
    public Guid BudgetId { get; init; }

    /// <summary>The budget's name, for the notification.</summary>
    [Id(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The subscription the budget lives in.</summary>
    [Id(2)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource group the budget lives in, and the one it covers at <see cref="BudgetScope.ResourceGroup" />.</summary>
    [Id(3)]
    public string ResourceGroup { get; init; } = string.Empty;

    /// <summary>What the figure covers.</summary>
    [Id(4)]
    public BudgetScope Scope { get; init; } = BudgetScope.Unknown;

    /// <summary>The amount, in the billing account's currency.</summary>
    [Id(5)]
    public decimal Amount { get; init; }

    /// <summary>The period, calendar-aligned in UTC.</summary>
    [Id(6)]
    public BudgetPeriod Period { get; init; } = BudgetPeriod.Unknown;

    /// <summary>The thresholds, each of which fires at most once per period.</summary>
    [Id(7)]
    public ImmutableArray<BudgetThreshold> Thresholds { get; init; } = [];

    /// <summary>Who is told.</summary>
    [Id(8)]
    public BudgetNotification Notification { get; init; } = new();

    /// <summary>Whether the budget is evaluated. Off keeps its history and stops the clock.</summary>
    [Id(9)]
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     Whether two specs ask for the same thing. Records compare <see cref="ImmutableArray{T}" />
    ///     by reference, so the generated equality would call two identical bodies different.
    /// </summary>
    /// <param name="other">The other spec.</param>
    public bool SameAs(BudgetSpec? other) =>
        other is not null
        && BudgetId == other.BudgetId
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && SubscriptionId == other.SubscriptionId
        && string.Equals(ResourceGroup, other.ResourceGroup, StringComparison.Ordinal)
        && Scope == other.Scope
        && Amount == other.Amount
        && Period == other.Period
        && Enabled == other.Enabled
        && Thresholds.SequenceEqual(other.Thresholds)
        && string.Equals(Notification.ServicePath, other.Notification.ServicePath, StringComparison.Ordinal)
        && string.Equals(Notification.Channel, other.Notification.Channel, StringComparison.Ordinal)
        && Notification.Recipients.SequenceEqual(other.Notification.Recipients, StringComparer.Ordinal);
}

/// <summary>One threshold that fired, and what the sending module said about telling people.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetAlert")]
public sealed record BudgetAlert {
    /// <summary>The period it fired in.</summary>
    [Id(0)]
    public DateTimeOffset PeriodStart { get; init; }

    /// <summary>The threshold's percentage.</summary>
    [Id(1)]
    public decimal Percent { get; init; }

    /// <summary>Actual or forecast.</summary>
    [Id(2)]
    public ThresholdKind Kind { get; init; } = ThresholdKind.Unknown;

    /// <summary>When it fired.</summary>
    [Id(3)]
    public DateTimeOffset FiredAt { get; init; }

    /// <summary>The figure that crossed it, rounded to the currency.</summary>
    [Id(4)]
    public decimal Figure { get; init; }

    /// <summary>
    ///     Per recipient: sent, or refused and why. Empty until a send has finished, and an alert an
    ///     evaluation recorded and didn't get to send is sent by the next one.
    /// </summary>
    [Id(5)]
    public string Notification { get; init; } = string.Empty;
}

/// <summary>A budget as its grain holds it: the spec, the last evaluation, and what has fired.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetSnapshot")]
public sealed record BudgetSnapshot {
    /// <summary>What the resource asks for.</summary>
    [Id(0)]
    public BudgetSpec Spec { get; init; } = new();

    /// <summary>The currency the figures are in, from the last evaluation.</summary>
    [Id(1)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>The period the figures are for.</summary>
    [Id(2)]
    public DateTimeOffset PeriodStart { get; init; }

    /// <summary>The end of that period.</summary>
    [Id(3)]
    public DateTimeOffset PeriodEnd { get; init; }

    /// <summary>What the period has cost so far, rounded.</summary>
    [Id(4)]
    public decimal Actual { get; init; }

    /// <summary>What it will cost at the trailing seven days' rate, rounded. ⚠ An estimate.</summary>
    [Id(5)]
    public decimal Forecast { get; init; }

    /// <summary>When it was last evaluated.</summary>
    [Id(6)]
    public DateTimeOffset? LastEvaluatedAt { get; init; }

    /// <summary>Why the last evaluation could not run, or empty.</summary>
    [Id(7)]
    public string LastError { get; init; } = string.Empty;

    /// <summary>Every threshold that has fired, oldest first.</summary>
    [Id(8)]
    public ImmutableArray<BudgetAlert> Alerts { get; init; } = [];
}

/// <summary>What one evaluation did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetEvaluationReport")]
public sealed record BudgetEvaluationReport {
    /// <summary>Whether the figures were computed. False for a disabled budget or an evaluation that failed.</summary>
    [Id(0)]
    public bool Evaluated { get; init; }

    /// <summary>The actual cost so far.</summary>
    [Id(1)]
    public decimal Actual { get; init; }

    /// <summary>The forecast.</summary>
    [Id(2)]
    public decimal Forecast { get; init; }

    /// <summary>How many thresholds fired on this pass.</summary>
    [Id(3)]
    public int Fired { get; init; }

    /// <summary>Why the figures could not be computed, or empty.</summary>
    [Id(4)]
    public string Error { get; init; } = string.Empty;
}

/// <summary>
///     One budget — <c>CyberCloud.Billing/budgets</c> — evaluated on a reminder against the cost of
///     its scope. docs/plan/22 § Cost visibility, "Budgets and alerts: threshold at 50/80/100/forecast,
///     delivered via [17]".
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>res/{budgetId:N}</c>, tenant-qualified
///         — the budget resource's own GUID, so the grain and the resource are one identity.
///     </para>
///     <para>
///         ⚠ <b>Durable, because what has fired is state nothing can recompute.</b> A threshold fires
///         once per period; a grain that forgot it had fired would page again on every activation,
///         and the sending module's idempotency key would only save it if the key were a function of
///         the period — which it is, and that is the second defence rather than the first.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A subscription-scoped budget sees the subscription's cost only after the budget itself
///             has been granted <c>reader</c> on the subscription.
///         </b> The budget resource lives in one resource group and anybody who may write there may
///         create one; without the check, <c>scope: subscription</c> would hand the whole
///         subscription's spend to a contributor of one group — in the status the resource shows and
///         in every notification. The write path does not carry the caller to a reconciler, so the
///         check cannot be "may the author read the subscription"; it is "may this budget", with the
///         budget as the ReBAC subject <c>resource:{budgetId:N}</c>, which only a subscription owner
///         can grant. A budget without the grant evaluates nothing and says why.
///     </para>
///     <para>
///         <b>The grant is the platform's existing one, not a billing invention.</b>
///         <c>RoleAssignmentService.PrincipalTypes</c> admits <c>resource</c> as a principal since #90 —
///         "Azure's system-assigned identity, without the identity" — so the owner writes the role
///         assignment <c>reader-resource-{budgetId:N}</c> at the subscription, the same request that
///         lets a backup vault read the shares it protects — the <c>N</c> spelling, which
///         <c>RoleAssignmentService</c> requires of a resource principal and this check uses.
///     </para>
/// </remarks>
[Alias("CyberCloud.Billing.IBudgetGrain")]
public interface IBudgetGrain : IGrainWithStringKey {
    /// <summary>How often the reminder fires. Usage lands in hourly rollups, so an hour is the resolution there is.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    /// <summary>How many fired alerts one budget keeps. The oldest is dropped past it.</summary>
    const int AlertsKept = 100;

    /// <summary>Sets the spec. A changed amount, period or scope keeps the alerts already fired this period.</summary>
    /// <param name="spec">The spec.</param>
    Task<Result<BudgetSnapshot>> UpsertAsync(BudgetSpec spec);

    /// <summary>Removes the budget and its reminder.</summary>
    Task<Result> RemoveAsync();

    /// <summary>The budget as held, or <see cref="ErrorCode.ResourceNotFound" /> when there is none.</summary>
    Task<Result<BudgetSnapshot>> GetAsync();

    /// <summary>Runs one evaluation now. What the reminder calls, and what a test drives.</summary>
    Task<Result<BudgetEvaluationReport>> EvaluateAsync();

    /// <summary>Whether the reminder is registered.</summary>
    Task<Result<bool>> IsArmedAsync();
}

/// <summary>
///     What the budget reconciler holds: the budget grains, reached with the tenant qualification
///     written once — the shape <c>IAlertControlPlane</c> and <c>ICommunicationControlPlane</c> have.
/// </summary>
public interface IBudgetControlPlane {
    /// <summary>Sets a budget's spec.</summary>
    /// <param name="tenantId">The tenant. Qualifies the grain call.</param>
    /// <param name="spec">The spec.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<BudgetSnapshot>> UpsertAsync(Guid tenantId, BudgetSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Removes a budget.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="budgetId">The budget resource's GUID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RemoveAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default);

    /// <summary>A budget as held.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="budgetId">The budget resource's GUID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<BudgetSnapshot>> GetAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default);

    /// <summary>Runs one evaluation by hand.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="budgetId">The budget resource's GUID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<BudgetEvaluationReport>> EvaluateAsync(
        Guid tenantId,
        Guid budgetId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Whether a budget's reminder is registered.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="budgetId">The budget resource's GUID.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<bool>> IsArmedAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Whether a caller may read what a budget covers—its resource group, or with
    ///     <see cref="BudgetScope.Subscription" /> its subscription—checked fully consistent. A check
    ///     that couldn't be answered is a no.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What <c>showStatus</c> asks before it shows a budget's figures</b>, which are the spend of
    ///     the scope the budget covers. <c>read</c> on the budget isn't <c>read</c> on its group: a
    ///     reader granted on the budget resource alone is someone the cost query answers with only the
    ///     rows they may read. A reader of the subscription reads every group in it through the group's
    ///     parent. Fully consistent for the reason <c>IInvoiceQueryGrain</c>'s check is: a revoked
    ///     reader must stop seeing the figures at the revoke, not at the next evaluation.
    /// </remarks>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="spec">The budget as held. Its scope, subscription, and group name the object checked.</param>
    /// <param name="caller">Who is asking. An empty subject is nobody, and nobody reads anything.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<bool> MayReadScopeAsync(
        Guid tenantId,
        BudgetSpec spec,
        CostCaller caller,
        CancellationToken cancellationToken = default
    );
}
