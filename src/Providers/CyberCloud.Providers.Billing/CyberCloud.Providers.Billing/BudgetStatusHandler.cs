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
///         ⚠ <b>A budget's figures are the spend of the scope it covers, and <c>read</c> on the budget
///         isn't <c>read</c> on that scope.</b> A reader of the budget's group is someone the cost query
///         answers with <c>filtered</c> for the subscription, and a reader granted on the budget
///         resource alone is someone it answers with <c>filtered</c> for the group. The actual, the
///         forecast, the fired thresholds and every alert's figure would hand either of them the whole.
///         So the figures are shown only to a caller who may read what the budget covers: its resource
///         group, or with <c>scope: subscription</c> the subscription. A reader of the subscription
///         reads every group in it through the group's parent. It's checked fully consistent at every
///         call: a revoke hides the figures at once, where the budget grain zeroes them only at its
///         next hourly evaluation. Anyone else gets <see cref="ErrorCode.AuthorizationFailed" />, which
///         says nothing they don't already know—they read the budget, scope and all.
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

        var caller = new CostCaller { SubjectType = context.Caller.SubjectType, SubjectId = context.Caller.SubjectId };

        if (!await plane.MayReadScopeAsync(context.Id.TenantId, budget.Spec, caller, cancellationToken)) {
            return Result<string>.Failure(
                ErrorCode.AuthorizationFailed,
                budget.Spec.Scope == BudgetScope.ResourceGroup
                    ? $"Budget '{context.Id.Name}' covers resource group '{budget.Spec.ResourceGroup}', so its figures are "
                    + "the group's spend. Showing them needs read on the group, not only on the budget."
                    : $"Budget '{context.Id.Name}' covers the whole subscription, so its figures are the subscription's "
                    + "spend. Showing them needs read on the subscription, not only on the budget."
            );
        }

        return Result<string>.Success(Budgets.StatusJson(budget));
    }
}
