using System.Collections.Immutable;

namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>What one evaluation did to a rule's state.</summary>
public enum AlertTransition {
    /// <summary>The state did not change, or changed without anybody needing to know.</summary>
    None = 0,

    /// <summary>The condition started holding and <c>for</c> has not elapsed.</summary>
    Pending = 1,

    /// <summary>The condition has held for <c>for</c>. Notify.</summary>
    Fired = 2,

    /// <summary>The condition stopped holding while the rule was firing. Notify, if asked.</summary>
    Resolved = 3
}

/// <summary>Where a rule is after one evaluation, and what moved.</summary>
/// <param name="State">The state after.</param>
/// <param name="PendingSince">When the condition started holding, while <see cref="AlertRuleState.Pending" />.</param>
/// <param name="Transition">What moved.</param>
/// <param name="Value">
///     The value the decision was made on — the worst offending sample when the condition is met,
///     the first sample when it is not, <see langword="null" /> when the query returned nothing.
/// </param>
public sealed record AlertDecision(
    AlertRuleState State,
    DateTimeOffset? PendingSince,
    AlertTransition Transition,
    double? Value);

/// <summary>
///     The three-state machine every alerting product converges on — <c>ok → pending → firing → ok</c>
///     — as a pure function, so it can be tested with a rule and a clock and no silo.
/// </summary>
/// <remarks>
///     <para>
///         Pure and static for the reason <c>ReconcileSchedule</c> is: the grain that calls it holds
///         the state and the clock, and the property that matters — that a condition must hold for
///         <c>for</c> before anybody is paged, and that a query that could not run moves nothing —
///         is one a test sweeps with a table rather than waits for.
///     </para>
///     <para>
///         ⚠ <b>Any sample meeting the condition fires the rule.</b> A MetricsQL query over a
///         workspace commonly returns one series per instance, and a rule saying "error rate above
///         five" means any instance, not the average. The value carried is the worst offender —
///         the largest for a greater-than, the smallest for a less-than — so the notification names
///         the number that crossed the line rather than the first one the store happened to return.
///         One rule is one instance regardless of how many series offend; per-series instances are
///         the api-version that grows the tree.
///     </para>
///     <para>
///         ⚠
///         <b>
///             No samples is "not met", and the reason is stated because the other reading is
///             defensible.
///         </b> Azure lets a rule choose what an empty result means; here an empty
///         result resolves a firing rule and never fires one. A query that matches nothing is most
///         often a series that stopped being written, and paging on that is a separate rule
///         (<c>absent()</c> in MetricsQL says so explicitly) rather than a default every rule pays
///         for. What an empty result must never do is what a <i>failed</i> query must never do
///         either, and the two are kept apart: a failure is a <c>Result</c> error and moves nothing.
///     </para>
/// </remarks>
public static class AlertEvaluation {
    /// <summary>Decides where a rule goes after one evaluation.</summary>
    /// <param name="spec">The rule.</param>
    /// <param name="state">Where it was.</param>
    /// <param name="pendingSince">When the condition started holding, if it was pending.</param>
    /// <param name="answer">
    ///     What the store said. ⚠ A failure moves nothing: the rule keeps <paramref name="state" />
    ///     and <paramref name="pendingSince" /> exactly, because resolving an alert on a store that
    ///     did not answer tells the on-call engineer the incident is over at the moment nothing can
    ///     be seen — and firing one on it would page for an outage of the monitoring, spelled as an
    ///     outage of the thing monitored.
    /// </param>
    /// <param name="now">The evaluation instant, from <c>IClock</c>.</param>
    public static AlertDecision Decide(
        AlertRuleSpec spec,
        AlertRuleState state,
        DateTimeOffset? pendingSince,
        Result<AlertQueryResult> answer,
        DateTimeOffset now
    ) {
        ArgumentNullException.ThrowIfNull(spec);

        if (!answer.TryGetValue(out var result)) {
            return new(
                state == AlertRuleState.Unknown ? AlertRuleState.Ok : state,
                pendingSince,
                AlertTransition.None,
                null
            );
        }

        var samples = result.Samples.IsDefault ? [] : result.Samples;
        var offending = samples.Where(x => spec.Condition.IsMetBy(x.Value))
            .Select(static x => x.Value)
            .ToImmutableArray();
        var met = offending.Length > 0;

        double? value = met
            ? Worst(spec.Condition.Operator, offending)
            : samples.Length > 0 ? samples[0].Value : null;

        if (met) {
            switch (state) {
                case AlertRuleState.Firing:
                    return new(AlertRuleState.Firing, null, AlertTransition.None, value);

                case AlertRuleState.Pending when pendingSince is { } since && now - since >= spec.For:
                    return new(AlertRuleState.Firing, null, AlertTransition.Fired, value);

                case AlertRuleState.Pending:
                    return new(AlertRuleState.Pending, pendingSince ?? now, AlertTransition.None, value);

                default:
                    return spec.For <= TimeSpan.Zero
                        ? new(AlertRuleState.Firing, null, AlertTransition.Fired, value)
                        : new(AlertRuleState.Pending, now, AlertTransition.Pending, value);
            }
        }

        return state == AlertRuleState.Firing
            ? new(AlertRuleState.Ok, null, AlertTransition.Resolved, value)
            : new(AlertRuleState.Ok, null, AlertTransition.None, value);
    }

    /// <summary>The offending value a notification should name.</summary>
    static double Worst(AlertOperator op, ImmutableArray<double> offending) =>
        op switch {
            AlertOperator.GreaterThan or AlertOperator.GreaterOrEqual => offending.Max(),
            AlertOperator.LessThan or AlertOperator.LessOrEqual => offending.Min(),
            _ => offending[0]
        };
}
