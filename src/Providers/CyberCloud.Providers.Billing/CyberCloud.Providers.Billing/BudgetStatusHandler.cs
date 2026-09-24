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

        return Result<string>.Success(Budgets.StatusJson(held.GetValueOrThrow()));
    }
}
