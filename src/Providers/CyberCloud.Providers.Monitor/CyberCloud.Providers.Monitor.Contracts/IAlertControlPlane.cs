using System.Collections.Immutable;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     What the alert-rule reconciler and the <c>listInstances</c> handler hold: the evaluator grains,
///     reached with the tenant qualification written once.
/// </summary>
/// <remarks>
///     <para>
///         The same shape <c>ICommunicationControlPlane</c> has, for the same reason. A reconciler
///         and an action handler are plain singletons in a silo's or a gateway's container, not
///         grains, so <c>Orleans.Multitenant</c>'s call filter never sees them and every
///         <c>GetGrain</c> has to be qualified with <c>ForTenant</c> — CC1006. One implementation over
///         an <c>IGrainFactory</c> gets that right in one place rather than in one place per caller.
///     </para>
///     <para>
///         ⚠ <b>The implementation lives in this family's own implementation assembly, which is the
///         first provider to hold one.</b> The sending module put its control plane beside its
///         grains in <c>CyberCloud.Communication</c> because identity reaches those grains and rule
///         2 forbids identity reaching a provider. Nothing outside this family reaches an
///         evaluator, so the grain and its seam stay in the provider — which is where docs/plan/03
///         § Providers put a provider's grains in the first place.
///     </para>
/// </remarks>
public interface IAlertControlPlane {
    /// <summary>Adds or replaces a rule on its workspace's evaluator.</summary>
    /// <param name="tenantId">The tenant. Qualifies the grain call.</param>
    /// <param name="evaluatorId">The workspace's evaluator, from <see cref="MonitorAlertRules.EvaluatorIdFor" />.</param>
    /// <param name="spec">The rule.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<AlertRuleSnapshot>> UpsertRuleAsync(
        Guid tenantId,
        Guid evaluatorId,
        AlertRuleSpec spec,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes a rule and its history.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="evaluatorId">The workspace's evaluator.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RemoveRuleAsync(Guid tenantId, Guid evaluatorId, Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>One rule, as held.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="evaluatorId">The workspace's evaluator.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid tenantId, Guid evaluatorId, Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>Every rule a workspace carries.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="evaluatorId">The workspace's evaluator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default);

    /// <summary>Runs one evaluation pass by hand. What a test drives instead of waiting a minute.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="evaluatorId">The workspace's evaluator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<AlertEvaluationReport>> EvaluateAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default);

    /// <summary>Whether the workspace's reminder is registered.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="evaluatorId">The workspace's evaluator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<bool>> IsArmedAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default);
}
