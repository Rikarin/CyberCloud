using System.Collections.Immutable;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     One workspace's alert rules, evaluated on a schedule and delivered through the sending module.
///     Coordinator, Durable, key <c>res/{evaluatorId:N}</c> where the id is
///     <see cref="MonitorAlertRules.EvaluatorIdFor" />'s.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § Alerts:
///         <i>
///             "a query, a threshold, a duration, a severity, an action group. Evaluated … against
///             the tenant's data; firing alerts … consumed by a notification grain that fans out via
///             [17]"
///         </i>. This is both halves in one grain: the evaluation and the fan-out. The document
///         names <c>vmalert</c> as the evaluator and NATS as the path between the two, and neither
///         is here — the platform's own scheduling seam, docs/plan/04 § Reminders, is what runs the
///         rules, and the reason is the first paragraph of <see cref="IAlertQuerySeam" />.
///     </para>
///     <para>
///         ⚠
///         <b>
///             ONE GRAIN PER WORKSPACE AND NOT ONE PER RULE, AND THAT IS docs/plan/16'S
///             CONCURRENCY CAP AND docs/plan/04'S REMINDER ARITHMETIC ANSWERED BY ONE DECISION.
///         </b> docs/plan/16 § Alerts makes
///         <i>"a per-workspace concurrent-evaluation cap"</i> mandatory from day one: a rule's query
///         is tenant-authored and runs on shared infrastructure. An Orleans activation is
///         single-threaded, so a grain per workspace evaluates that workspace's rules one at a time
///         by construction — a cap of one, with nothing to configure and nothing that can be
///         misconfigured. And docs/plan/04 § Reminders warns that one reminder per resource is
///         <i>"a real scaling number and it is easy to get wrong"</i>; one reminder per workspace is
///         the same mitigation that document applies to drift.
///     </para>
///     <para>
///         ⚠ <b>Keyed by the workspace's ADDRESS rather than its GUID, for the reason
///         <c>CommunicationGrainKeys.ResourceIdFor</c> gives.</b> A rule's reconcile pass knows its
///         workspace by name — <c>ReconcileContext.Id.ParentNames</c> — and nothing hands it the
///         parent's GUID; asking the index is a provider calling the index, which docs/plan/08 § The
///         reconcile loop forbids. So the grain id is derived from the workspace's canonical path
///         and every rule under one workspace derives the same one. What that costs is what it cost
///         the sending module: the key borrows <c>GrainKeys.Resource</c>'s shape for something that
///         is not a resource, and a workspace deleted and recreated under the same name lands on the
///         same evaluator. Here the second is harmless — its rules were removed with it.
///     </para>
///     <para>
///         ⚠ <b>Durable, and the first provider grain on <c>durable-grains.txt</c>.</b> That file's
///         Providers section says a provider needing a line there means <i>"the first question is
///         what the manager is missing"</i>, and the answer is written beside the line: the manager
///         holds one desired body and one observed state per resource, and an alert's history is
///         neither — it is a sequence of events with timestamps and delivery outcomes, and it is
///         what a tenant reads at 03:00 to learn whether they were told. It cannot be rebuilt from
///         the stores: a sample's retention is days, and whether a page was sent was never in any
///         store but this one.
///     </para>
/// </remarks>
[Alias("CyberCloud.Monitor.IAlertEvaluatorGrain")]
public interface IAlertEvaluatorGrain : IGrainWithStringKey {
    /// <summary>
    ///     How many rules one workspace may carry. docs/plan/16 § Alerts' query-cost limit in its
    ///     coarsest form: the most a single tick can ask of the shared store on one workspace's behalf.
    /// </summary>
    const int MaxRules = 50;

    /// <summary>
    ///     How many firings a rule keeps, oldest dropped first. Thirty days of a rule flapping hourly.
    /// </summary>
    const int InstancesKept = 720;

    /// <summary>
    ///     How often the reminder fires. ⚠ Orleans' minimum reminder period, and every rule's
    ///     interval is a multiple of it — a rule at five minutes is evaluated on every fifth tick and
    ///     skipped on the other four.
    /// </summary>
    static TimeSpan Tick => TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How long one query may take. The second of docs/plan/16 § Alerts' three mandatory limits;
    ///     the third, the look-back, is the schema's.
    /// </summary>
    static TimeSpan QueryTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Adds a rule, or replaces the one with the same <see cref="AlertRuleSpec.RuleId" />.</summary>
    /// <param name="spec">The rule.</param>
    /// <returns>
    ///     <see cref="ErrorCode.QuotaExceeded" /> for the rule past <see cref="MaxRules" />;
    ///     <see cref="ErrorCode.InvalidRequestBody" /> for a spec with no id. A replaced rule keeps its
    ///     state and its instances when the condition is unchanged, and returns to
    ///     <see cref="AlertRuleState.Ok" /> — resolving an open instance — when it is not, because an
    ///     instance fired by a condition that no longer exists cannot be resolved by anything else.
    /// </returns>
    Task<Result<AlertRuleSnapshot>> UpsertRuleAsync(AlertRuleSpec spec);

    /// <summary>Removes a rule and its history.</summary>
    /// <param name="ruleId">The rule.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the grain holds no such rule.</returns>
    Task<Result> RemoveRuleAsync(Guid ruleId);

    /// <summary>One rule, as held.</summary>
    /// <param name="ruleId">The rule.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the grain holds no such rule.</returns>
    Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid ruleId);

    /// <summary>Every rule this workspace carries, in no particular order.</summary>
    Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync();

    /// <summary>
    ///     Evaluates every enabled rule that is due, notifies on each transition, and re-arms or
    ///     disarms the reminder. What the reminder calls, and what a test or an operator calls by
    ///     hand.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A query that cannot be answered neither fires nor resolves.</b> The rule keeps its
    ///     state, records the error, and is due again next tick. Resolving an alert because the
    ///     store was unreachable would tell the on-call engineer the incident is over at the exact
    ///     moment nothing can be seen.
    /// </remarks>
    Task<Result<AlertEvaluationReport>> EvaluateAsync();

    /// <summary>Whether the reminder is registered — armed exactly while an enabled rule exists.</summary>
    /// <remarks>
    ///     Reported for the reason <c>ExpirySweep.Disarmed</c> gives: Orleans exposes a reminder to
    ///     its grain and to nobody else, so this is the only way a test or an operator can tell an
    ///     evaluator that stood down from one that is still ticking.
    /// </remarks>
    Task<Result<bool>> IsArmedAsync();
}
