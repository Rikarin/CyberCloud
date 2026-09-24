using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing;

/// <summary>
///     The <see cref="IBudgetControlPlane" /> that talks to grains — what the budget reconciler holds.
/// </summary>
/// <remarks>
///     ⚠ <b>Every <c>GetGrain</c> is qualified with <c>ForTenant</c></b>, and this class exists so that
///     is written once — CC1006, and the arrangement <c>GrainAlertControlPlane</c> has. A reconciler is
///     a plain singleton in a silo's container, not a grain, so the call filter never sees it.
/// </remarks>
/// <param name="grains">A grain factory — the silo's.</param>
public sealed class GrainBudgetControlPlane(IGrainFactory grains) : IBudgetControlPlane {
    /// <inheritdoc />
    public Task<Result<BudgetSnapshot>> UpsertAsync(
        Guid tenantId,
        BudgetSpec spec,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        return Budget(tenantId, spec.BudgetId).UpsertAsync(spec);
    }

    /// <inheritdoc />
    public Task<Result> RemoveAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default) =>
        Budget(tenantId, budgetId).RemoveAsync();

    /// <inheritdoc />
    public Task<Result<BudgetSnapshot>> GetAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default) =>
        Budget(tenantId, budgetId).GetAsync();

    /// <inheritdoc />
    public Task<Result<BudgetEvaluationReport>> EvaluateAsync(
        Guid tenantId,
        Guid budgetId,
        CancellationToken cancellationToken = default
    ) =>
        Budget(tenantId, budgetId).EvaluateAsync();

    /// <inheritdoc />
    public Task<Result<bool>> IsArmedAsync(Guid tenantId, Guid budgetId, CancellationToken cancellationToken = default) =>
        Budget(tenantId, budgetId).IsArmedAsync();

    IBudgetGrain Budget(Guid tenantId, Guid budgetId) =>
        grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture)).GetGrain<IBudgetGrain>(GrainKeys.Resource(budgetId));
}

/// <summary>
///     The <see cref="ICostQuery" /> the gateway's dispatch stage holds — one grain call over the
///     gateway's cluster client, qualified with the token's tenant.
/// </summary>
/// <remarks>
///     ⚠ <b>The tenant is the argument, never the request's.</b> <c>CostQueryRequest</c> carries no
///     tenant at all, and the grain is reached through the one the gateway took from the token — the
///     same second defence <c>GatewayRoute</c>'s rebuild is.
/// </remarks>
/// <param name="grains">The gateway's cluster client, as a grain factory.</param>
public sealed class GrainCostQuery(IGrainFactory grains) : ICostQuery {
    /// <inheritdoc />
    public Task<Result<CostQueryResult>> QueryAsync(
        Guid tenantId,
        CostQueryRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        return grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICostQueryGrain>(GrainKeys.Subscription(request.SubscriptionId))
            .QueryAsync(request);
    }
}

/// <summary>
///     The <see cref="IInvoiceReader" /> the gateway's dispatch stage holds — one grain call over the
///     gateway's cluster client, qualified with the token's tenant.
/// </summary>
/// <remarks>
///     ⚠ <b>The tenant is the argument and the grain's key both</b>, for the reason
///     <see cref="GrainCostQuery" /> gives: nothing the request carries can name another tenant's
///     billing account.
/// </remarks>
/// <param name="grains">The gateway's cluster client, as a grain factory.</param>
public sealed class GrainInvoiceReader(IGrainFactory grains) : IInvoiceReader {
    /// <inheritdoc />
    public Task<Result<ImmutableArray<Invoice>>> ListAsync(
        Guid tenantId,
        CostCaller caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        return Query(tenantId).ListAsync(caller);
    }

    /// <inheritdoc />
    public Task<Result<Invoice>> GetAsync(
        Guid tenantId,
        CostCaller caller,
        string number,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        return Query(tenantId).GetAsync(caller, number);
    }

    IInvoiceQueryGrain Query(Guid tenantId) =>
        grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture)).GetGrain<IInvoiceQueryGrain>(GrainKeys.Tenant(tenantId));
}
