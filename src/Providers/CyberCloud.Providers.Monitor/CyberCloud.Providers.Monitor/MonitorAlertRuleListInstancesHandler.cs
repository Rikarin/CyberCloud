namespace CyberCloud.Providers.Monitor;

/// <summary>
///     <c>POST …/alertRules/{name}/listInstances</c> — every firing the rule keeps, oldest first, and
///     where the rule is now.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An action rather than an <c>alertRules/instances</c> child type, for the reason the
///         sending module's <c>send</c> is an action rather than a <c>messages</c> type.</b> An
///         instance is an event the evaluator produced, not desired state a tenant declares: a
///         resource for one would have a PUT nothing could apply and a DELETE that could not
///         un-fire it. docs/plan/16 § Alerts calls the collection <i>alert instances</i> and this is
///         where it is read; the write side is the evaluator's alone.
///     </para>
///     <para>
///         ⚠ <b>Synchronous, with a handler, and it reaches a grain from the request path.</b> The
///         third handler in the tree to do so after the sending module's four, and the reason
///         <c>MonitorApplicationModule</c> registers <see cref="IAlertControlPlane" /> in the gateway
///         as well as the silo.
///     </para>
/// </remarks>
/// <param name="plane">The evaluator, reached with the tenant qualification written once.</param>
public sealed class MonitorAlertRuleListInstancesHandler(IAlertControlPlane plane) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorAlertRules.Type;

    /// <inheritdoc />
    public string Action => MonitorAlertRules.ListInstancesAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var rule = await plane.GetRuleAsync(
            context.Id.TenantId,
            MonitorAlertRules.EvaluatorIdFor(context.Id),
            context.Id.Id,
            cancellationToken
        );

        if (rule.TryGetError(out var error)) {
            // ⚠ A rule the manager knows and the evaluator does not is a rule whose create has not
            // converged yet, or whose evaluator lost its state; the canonical 404 is the honest
            // answer to both and the observed state says which.
            return Result<string>.Failure(error);
        }

        return Result<string>.Success(MonitorAlertRules.InstancesJson(rule.GetValueOrThrow()));
    }
}
