using CyberCloud.Providers.Monitor.Alerting;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The three-state machine, swept with a table — no silo, no clock but the one passed in.
///     <c>AlertEvaluation.Decide</c> is pure so that these can exist.
/// </summary>
public sealed class AlertEvaluationTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);

    static AlertRuleSpec Rule(
        TimeSpan? @for = null,
        AlertOperator op = AlertOperator.GreaterThan,
        double threshold = 5
    ) =>
        new() {
            RuleId = Guid.NewGuid(),
            Name = "errors-high",
            Workspace = "prod",
            Enabled = true,
            Severity = AlertSeverity.Warning,
            Condition = new() {
                Signal = AlertSignal.Metrics,
                Query = "q",
                Operator = op,
                Threshold = threshold,
                Lookback = TimeSpan.FromMinutes(5)
            },
            Interval = TimeSpan.FromMinutes(1),
            For = @for ?? TimeSpan.Zero
        };

    static Result<AlertQueryResult> Value(params double[] values) =>
        Result<AlertQueryResult>.Success(
            new() { Samples = [.. values.Select(x => new AlertSample { Labels = "{}", Value = x })] }
        );

    static Result<AlertQueryResult> Down() =>
        Result<AlertQueryResult>.Failure(ErrorCode.InternalError, "the store is down");

    [Fact]
    public void AZeroForFiresOnTheFirstEvaluationThatMeetsTheCondition() {
        var decision = AlertEvaluation.Decide(Rule(), AlertRuleState.Ok, null, Value(7), T0);

        decision.State.ShouldBe(AlertRuleState.Firing);
        decision.Transition.ShouldBe(AlertTransition.Fired);
        decision.Value.ShouldBe(7);
        decision.PendingSince.ShouldBeNull();
    }

    [Fact]
    public void AForHoldsTheRulePendingUntilTheConditionHasHeldThatLong() {
        var rule = Rule(TimeSpan.FromMinutes(2));

        var first = AlertEvaluation.Decide(rule, AlertRuleState.Ok, null, Value(7), T0);
        first.State.ShouldBe(AlertRuleState.Pending);
        first.Transition.ShouldBe(AlertTransition.Pending);
        first.PendingSince.ShouldBe(T0);

        // One minute in: still pending, and PendingSince is the ORIGINAL instant, not now.
        var second = AlertEvaluation.Decide(rule, first.State, first.PendingSince, Value(8), T0.AddMinutes(1));
        second.State.ShouldBe(AlertRuleState.Pending);
        second.Transition.ShouldBe(AlertTransition.None);
        second.PendingSince.ShouldBe(T0);

        var third = AlertEvaluation.Decide(rule, second.State, second.PendingSince, Value(9), T0.AddMinutes(2));
        third.State.ShouldBe(AlertRuleState.Firing);
        third.Transition.ShouldBe(AlertTransition.Fired);
    }

    [Fact]
    public void AConditionThatStopsHoldingWhilePendingReturnsToOkWithoutATransition() {
        var rule = Rule(TimeSpan.FromMinutes(2));

        var decision = AlertEvaluation.Decide(rule, AlertRuleState.Pending, T0, Value(1), T0.AddMinutes(1));

        decision.State.ShouldBe(AlertRuleState.Ok);
        decision.Transition.ShouldBe(AlertTransition.None);
        decision.PendingSince.ShouldBeNull();
    }

    [Fact]
    public void AFiringRuleWhoseConditionStopsHoldingResolves() {
        var decision = AlertEvaluation.Decide(Rule(), AlertRuleState.Firing, null, Value(1), T0);

        decision.State.ShouldBe(AlertRuleState.Ok);
        decision.Transition.ShouldBe(AlertTransition.Resolved);
        decision.Value.ShouldBe(1);
    }

    [Fact]
    public void AFiringRuleThatKeepsFiringDoesNotFireAgain() {
        var decision = AlertEvaluation.Decide(Rule(), AlertRuleState.Firing, null, Value(9), T0);

        decision.State.ShouldBe(AlertRuleState.Firing);
        decision.Transition.ShouldBe(AlertTransition.None);
    }

    [Theory]
    [InlineData(AlertRuleState.Ok)]
    [InlineData(AlertRuleState.Pending)]
    [InlineData(AlertRuleState.Firing)]
    public void AStoreThatDidNotAnswerMovesNothing(AlertRuleState state) {
        // ⚠ THE PROPERTY THE WHOLE EVALUATOR RESTS ON. Resolving a firing rule because the store
        // was unreachable tells the on-call engineer the incident is over at the exact moment
        // nothing can be seen; firing a pending one pages for an outage of the monitoring spelled as
        // an outage of the thing monitored. Every state stays where it was, with its PendingSince.
        var since = state == AlertRuleState.Pending ? T0 : (DateTimeOffset?)null;

        var decision = AlertEvaluation.Decide(Rule(TimeSpan.FromMinutes(2)), state, since, Down(), T0.AddHours(1));

        decision.State.ShouldBe(state);
        decision.PendingSince.ShouldBe(since);
        decision.Transition.ShouldBe(AlertTransition.None);
        decision.Value.ShouldBeNull();
    }

    [Fact]
    public void NoSamplesIsNotMetAndResolvesAFiringRule() {
        var decision = AlertEvaluation.Decide(Rule(), AlertRuleState.Firing, null, Value(), T0);

        decision.State.ShouldBe(AlertRuleState.Ok);
        decision.Transition.ShouldBe(AlertTransition.Resolved);
        decision.Value.ShouldBeNull();
    }

    [Fact]
    public void AnySampleMeetingTheConditionFiresAndTheWorstOffenderIsNamed() {
        var above = AlertEvaluation.Decide(Rule(), AlertRuleState.Ok, null, Value(1, 9, 6), T0);
        above.Transition.ShouldBe(AlertTransition.Fired);
        above.Value.ShouldBe(9, "a greater-than names the largest offending value, not the first");

        var below = AlertEvaluation.Decide(
            Rule(op: AlertOperator.LessThan, threshold: 5),
            AlertRuleState.Ok,
            null,
            Value(7, 2, 4),
            T0
        );
        below.Transition.ShouldBe(AlertTransition.Fired);
        below.Value.ShouldBe(2, "a less-than names the smallest offending value");
    }

    [Theory]
    [InlineData(AlertOperator.GreaterThan, 5, 5, false)]
    [InlineData(AlertOperator.GreaterOrEqual, 5, 5, true)]
    [InlineData(AlertOperator.LessThan, 5, 5, false)]
    [InlineData(AlertOperator.LessOrEqual, 5, 5, true)]
    [InlineData(AlertOperator.Equal, 5, 5, true)]
    [InlineData(AlertOperator.NotEqual, 5, 5, false)]
    [InlineData(AlertOperator.NotEqual, 5, 4, true)]
    public void EveryOperatorMeansWhatItsNameSays(AlertOperator op, double threshold, double value, bool met) {
        var condition = new AlertCondition { Operator = op, Threshold = threshold };
        condition.IsMetBy(value).ShouldBe(met);
    }

    [Fact]
    public void AnUnknownStateIsTreatedAsOk() {
        // A record written before the enum had a value, or a default-constructed one, must not sit
        // in Unknown forever — the first evaluation decides from Ok.
        var decision = AlertEvaluation.Decide(Rule(), AlertRuleState.Unknown, null, Value(1), T0);
        decision.State.ShouldBe(AlertRuleState.Ok);

        var failed = AlertEvaluation.Decide(Rule(), AlertRuleState.Unknown, null, Down(), T0);
        failed.State.ShouldBe(AlertRuleState.Ok);
    }
}
