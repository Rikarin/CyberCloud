using Orleans.Concurrency;
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
///         ⚠
///         <b>
///             AND THE CAP HAS A PRICE THE FIRST VERSION DID NOT PAY (2026-09-15, #32 review): A
///             SINGLE-THREADED PASS IS ALSO A QUEUE EVERY OTHER CALL WAITS IN.
///         </b> The tree configures no <c>ResponseTimeout</c>, so Orleans' 30-second default applies
///         to a reconcile pass's <see cref="GetRuleAsync" /> and to <c>listInstances</c> the same as
///         to anything else, and a pass that ran <see cref="MaxRules" /> queries at the old
///         30-second timeout could hold the activation for twenty-five minutes against a store that
///         had stopped answering — every call queued behind it timed out, and the test that pinned
///         the timeout under <see cref="Tick" /> reasoned about one query and not the pass. Two
///         things bound it now. The reads — <see cref="GetRuleAsync" />,
///         <see cref="ListRulesAsync" />, <see cref="IsArmedAsync" /> — are
///         <see cref="AlwaysInterleaveAttribute" /> for the reason <c>IExpirySweeperGrain.ArmAsync</c>
///         is: they touch nothing a pass leaves half-written between two awaits, and they are what
///         every reconcile pass and every action calls first. And the pass itself stops asking once
///         <see cref="PassBudget" /> has elapsed: the rules it did not reach stay due, are reported
///         as deferred, and go first on the next tick because the pass orders by due time before
///         rule id. The arithmetic is <see cref="PassBudget" /> plus one <see cref="QueryTimeout" />
///         under the response timeout, and
///         <c>AlertRuleDeclarationTests.TheThreeMandatoryLimitsAreInTheSchemaOrTheGrainAndNotInProse</c>
///         pins it. What it does not bound is a send: <c>IMessageSender</c> is a grain call with its
///         own response timeout, and a carrier that hangs is the sending module's problem to bound,
///         not this grain's to hide.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Keyed by the workspace's ADDRESS rather than its GUID, for the reason
///             <c>CommunicationGrainKeys.ResourceIdFor</c> gives.
///         </b> A rule's reconcile pass knows its
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
///         Providers section says a provider needing a line there means
///         <i>
///             "the first question is
///             what the manager is missing"
///         </i>, and the answer is written beside the line: the manager
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
    /// <remarks>
    ///     ⚠ Ten seconds and not thirty, because a query that ran the full length of Orleans'
    ///     response timeout would take every call queued behind the pass with it — the type's
    ///     remarks say why. A store that needs longer than this for a rule's query is running a
    ///     query the rule should not be asking every minute.
    /// </remarks>
    static TimeSpan QueryTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long one pass may keep asking. A rule reached after this much of the pass has elapsed
    ///     is deferred — left due, counted in <see cref="AlertEvaluationReport.Deferred" />, and
    ///     evaluated first on the next tick.
    /// </summary>
    /// <remarks>
    ///     ⚠ Measured on <c>IClock</c> and not a stopwatch, so a test can script a slow store by
    ///     advancing the clock from inside the seam. It is a budget for <i>starting</i> queries: the
    ///     last one started under it still runs to <see cref="QueryTimeout" />, which is why the two
    ///     sum to less than the response timeout rather than either alone.
    /// </remarks>
    static TimeSpan PassBudget => TimeSpan.FromSeconds(15);

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

    /// <summary>Removes a rule and its history, telling the recipients of an open firing that it ended.</summary>
    /// <param name="ruleId">The rule.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the grain holds no such rule.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A firing rule that is deleted sends its resolve notification, when the action group
    ///         asks for one (2026-09-15, #32 review).
    ///     </b> A disabled or re-conditioned rule keeps its
    ///     history, so those two close the instance quietly and write "not notified" on it for a
    ///     tenant to read later; a deleted rule has no history left to write on, and a recipient who
    ///     was paged FIRING with nothing after it is a recipient still holding a page. The line says
    ///     the rule was deleted rather than that the condition cleared, because it was not
    ///     evaluated.
    /// </remarks>
    Task<Result> RemoveRuleAsync(Guid ruleId);

    /// <summary>One rule, as held.</summary>
    /// <param name="ruleId">The rule.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the grain holds no such rule.</returns>
    /// <remarks>
    ///     ⚠ <see cref="AlwaysInterleaveAttribute" />, so a reconcile pass is answered while an
    ///     evaluation pass is running rather than after it — the type's remarks. What it can see
    ///     mid-pass is exactly what a crash mid-pass leaves: an instance minted and written with its
    ///     notification outcome still empty, which is the state the grain's own remarks call safe.
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid ruleId);

    /// <summary>Every rule this workspace carries, in no particular order.</summary>
    /// <remarks>⚠ <see cref="AlwaysInterleaveAttribute" /> for <see cref="GetRuleAsync" />'s reason.</remarks>
    [AlwaysInterleave]
    Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync();

    /// <summary>
    ///     Evaluates every enabled rule that is due, notifies on each transition, and re-arms or
    ///     disarms the reminder. What the reminder calls, and what a test or an operator calls by
    ///     hand.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A query that cannot be answered neither fires nor resolves.</b> The rule keeps
    ///         its state, records the error, and is due again next tick. Resolving an alert because
    ///         the store was unreachable would tell the on-call engineer the incident is over at the
    ///         exact moment nothing can be seen.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And a seam that throws is a seam that could not answer, not a pass that ends
    ///             (2026-09-15, #32 review).
    ///         </b> <see cref="IAlertQuerySeam" />'s contract asks for a
    ///         failure and does not forbid an exception, and a real seam over <c>HttpClient</c>
    ///         throws on a refused connection. The first version caught only its own timeout, so
    ///         one throwing rule ended the pass at that rule and every rule sorted after it was
    ///         never evaluated on any tick while the fault lasted. Every exception a query raises is
    ///         now the failure a returned one is, recorded on that rule, and the pass goes on —
    ///         <c>AlertEvaluatorTests.ASeamThatThrowsFailsThatRuleAndTheRestOfThePassStillRuns</c>.
    ///     </para>
    /// </remarks>
    Task<Result<AlertEvaluationReport>> EvaluateAsync();

    /// <summary>Whether the reminder is registered — armed exactly while an enabled rule exists.</summary>
    /// <returns>
    ///     Whether a row exists; <c>false</c> on a silo with no reminder service, which is the truth
    ///     from that silo's point of view. ⚠ A reminder table that could not be <i>read</i> is
    ///     <see cref="ErrorCode.InternalError" /> and not <c>false</c>: "no row" and "could not
    ///     look" are different answers, and the reconciler retries the second and re-arms on the
    ///     first.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         Reported for the reason <c>ExpirySweep.Disarmed</c> gives: Orleans exposes a reminder
    ///         to its grain and to nobody else, so this is the only way a test or an operator can
    ///         tell an evaluator that stood down from one that is still ticking.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             And it is what <c>MonitorAlertRuleReconciler</c> asks before it says
    ///             <c>Converged</c> for an enabled rule (2026-09-15, #32 review).
    ///         </b> The state write and
    ///         the reminder registration are two calls, and a reminder-table fault between them
    ///         leaves a rule that reads back as desired and that nothing will ever tick. A reconciler
    ///         that judged convergence on the held spec alone reported that rule converged on every
    ///         retry; judging it on the spec <i>and</i> this answer makes the retry re-arm it.
    ///         <see cref="AlwaysInterleaveAttribute" /> for <see cref="GetRuleAsync" />'s reason.
    ///     </para>
    /// </remarks>
    [AlwaysInterleave]
    Task<Result<bool>> IsArmedAsync();
}
