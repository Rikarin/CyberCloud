using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>
///     The <see cref="IAlertControlPlane" /> that talks to grains. What the alert-rule reconciler and
///     the <c>listInstances</c> handler hold.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         Every <c>GetGrain</c> here is qualified with <c>ForTenant</c>, and this class exists so
///         that is written once — CC1006.
///     </b> The same arrangement
///     <c>GrainCommunicationControlPlane</c> has, for the same reason: a reconciler and a handler are
///     plain singletons, not grains, so the call filter never sees them. No caching and no retry;
///     each method is one grain call, and the grain is where the state lives.
/// </remarks>
public sealed class GrainAlertControlPlane(IGrainFactory grains) : IAlertControlPlane {
    /// <inheritdoc />
    public Task<Result<AlertRuleSnapshot>> UpsertRuleAsync(
        Guid tenantId,
        Guid evaluatorId,
        AlertRuleSpec spec,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        return Evaluator(tenantId, evaluatorId).UpsertRuleAsync(spec);
    }

    /// <inheritdoc />
    public Task<Result> RemoveRuleAsync(
        Guid tenantId,
        Guid evaluatorId,
        Guid ruleId,
        CancellationToken cancellationToken = default
    ) =>
        Evaluator(tenantId, evaluatorId).RemoveRuleAsync(ruleId);

    /// <inheritdoc />
    public Task<Result<AlertRuleSnapshot>> GetRuleAsync(
        Guid tenantId,
        Guid evaluatorId,
        Guid ruleId,
        CancellationToken cancellationToken = default
    ) =>
        Evaluator(tenantId, evaluatorId).GetRuleAsync(ruleId);

    /// <inheritdoc />
    public Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync(
        Guid tenantId,
        Guid evaluatorId,
        CancellationToken cancellationToken = default
    ) =>
        Evaluator(tenantId, evaluatorId).ListRulesAsync();

    /// <inheritdoc />
    public Task<Result<AlertEvaluationReport>> EvaluateAsync(
        Guid tenantId,
        Guid evaluatorId,
        CancellationToken cancellationToken = default
    ) =>
        Evaluator(tenantId, evaluatorId).EvaluateAsync();

    /// <inheritdoc />
    public Task<Result<bool>> IsArmedAsync(
        Guid tenantId,
        Guid evaluatorId,
        CancellationToken cancellationToken = default
    ) =>
        Evaluator(tenantId, evaluatorId).IsArmedAsync();

    IAlertEvaluatorGrain Evaluator(Guid tenantId, Guid evaluatorId) =>
        grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IAlertEvaluatorGrain>(GrainKeys.Resource(evaluatorId));
}
