namespace CyberCloud.Providers.Billing;

/// <summary>
///     Serves <c>POST …/budgets/{name}/showStatus</c>: the budget's figures and fired thresholds, read
///     from its grain. Issue #41.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="Budgets.StatusAction" /> for why this reads and never evaluates.</b> The
///     manager has already checked <c>read</c> on the budget, and a synchronous action runs in the
///     gateway process, so the grain call goes through the gateway's cluster client — the
///     <see cref="IBudgetControlPlane" /> it registers for the reconciler.
///     <para>
///         ⚠ <b>A subscription budget's figures are the subscription's spend, and <c>read</c> on the
///         budget is not <c>read</c> on the subscription.</b> A reader of the budget's group is someone
///         the cost query answers with <c>filtered</c>, and the actual, the forecast, the fired
///         thresholds and every alert's figure would hand them the whole. So a
///         <c>scope: subscription</c> budget is shown only to a caller who may read the subscription,
///         checked fully consistent here at every call: a revoke hides the figures at once, where the
///         budget grain zeroes them only at its next hourly evaluation. Anyone else gets
///         <see cref="ErrorCode.AuthorizationFailed" />, which says nothing they don't already know —
///         they read the budget, scope and all.
///     </para>
/// </remarks>
/// <param name="plane">The budget grains, reached with the tenant qualification written once.</param>
public sealed class BudgetStatusHandler(IBudgetControlPlane plane) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => Budgets.Type;

    /// <inheritdoc />
    public string Action => Budgets.StatusAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var held = await plane.GetAsync(context.Id.TenantId, context.Id.Id, cancellationToken);

        if (held.TryGetError(out var error)) {
            // ⚠ The resource exists — the manager checked before this ran — so an absent grain is a budget
            // the reconciler has not written yet, not one that is gone.
            return error.Code == ErrorCode.ResourceNotFound
                ? Result<string>.Failure(
                    ErrorCode.Conflict,
                    $"Budget '{context.Id.Name}' is not held yet: its first reconcile has not written it. Ask again once "
                    + "its provisioningState is Succeeded."
                )
                : Result<string>.Failure(error);
        }

        var budget = held.GetValueOrThrow();

        if (budget.Spec.Scope != BudgetScope.ResourceGroup) {
            var caller = new CostCaller { SubjectType = context.Caller.SubjectType, SubjectId = context.Caller.SubjectId };

            if (!await plane.MayReadSubscriptionAsync(context.Id.TenantId, budget.Spec.SubscriptionId, caller, cancellationToken)) {
                return Result<string>.Failure(
                    ErrorCode.AuthorizationFailed,
                    $"Budget '{context.Id.Name}' covers the whole subscription, so its figures are the subscription's "
                    + "spend. Showing them needs read on the subscription, not only on the budget."
                );
            }
        }

        return Result<string>.Success(Budgets.StatusJson(budget));
    }
}
