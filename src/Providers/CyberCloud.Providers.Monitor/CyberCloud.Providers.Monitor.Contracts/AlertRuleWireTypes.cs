// ⚠ For ChannelKind — the one type an alert rule's action group shares with the sending module.
// This is the fifth module edge this family takes (module-layering.txt § CyberCloud.Providers.Monitor
// -> CyberCloud.Communication), and it is to the .Contracts assembly only.

using CyberCloud.Communication.Contracts;
using System.Collections.Immutable;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>Which of the workspace's two stores a rule's query runs against.</summary>
[Alias("CyberCloud.Monitor.AlertSignal")]
public enum AlertSignal {
    /// <summary>The zero value a default-constructed wire type carries. Never a signal.</summary>
    Unknown = 0,

    /// <summary>MetricsQL over the workspace's VictoriaMetrics <c>accountID</c>.</summary>
    Metrics = 1,

    /// <summary>SQL over the workspace's ClickHouse database.</summary>
    Logs = 2
}

/// <summary>
///     How loud an alert is. Azure's <c>Sev0</c>–<c>Sev3</c>, named rather than numbered so a
///     notification reads as a word.
/// </summary>
[Alias("CyberCloud.Monitor.AlertSeverity")]
public enum AlertSeverity {
    /// <summary>The zero value a default-constructed wire type carries. Never a severity.</summary>
    Unknown = 0,

    /// <summary>Somebody is paged.</summary>
    Critical = 1,

    /// <summary>Something is broken and somebody looks today.</summary>
    Error = 2,

    /// <summary>Something will break if nobody looks this week.</summary>
    Warning = 3,

    /// <summary>Worth knowing, not worth waking anybody.</summary>
    Informational = 4
}

/// <summary>How a sampled value is compared with the threshold.</summary>
[Alias("CyberCloud.Monitor.AlertOperator")]
public enum AlertOperator {
    /// <summary>The zero value a default-constructed wire type carries. Never an operator.</summary>
    Unknown = 0,

    /// <summary><c>value &gt; threshold</c>.</summary>
    GreaterThan = 1,

    /// <summary><c>value ≥ threshold</c>.</summary>
    GreaterOrEqual = 2,

    /// <summary><c>value &lt; threshold</c>.</summary>
    LessThan = 3,

    /// <summary><c>value ≤ threshold</c>.</summary>
    LessOrEqual = 4,

    /// <summary><c>value = threshold</c>.</summary>
    Equal = 5,

    /// <summary><c>value ≠ threshold</c>.</summary>
    NotEqual = 6
}

/// <summary>Where a rule is in its life. The three states every alerting product converges on.</summary>
/// <remarks>
///     ⚠ <b><see cref="Pending" /> is what <c>for</c> buys.</b> A condition that is true on one
///     evaluation and false on the next is noise; a rule with a <c>for</c> of five minutes stays
///     <see cref="Pending" /> until the condition has held that long, and notifies only then. A
///     <c>for</c> of zero skips the state.
/// </remarks>
[Alias("CyberCloud.Monitor.AlertRuleState")]
public enum AlertRuleState {
    /// <summary>The zero value a default-constructed wire type carries. Never a state.</summary>
    Unknown = 0,

    /// <summary>The condition is not met.</summary>
    Ok = 1,

    /// <summary>The condition is met and has not yet held for <c>for</c>.</summary>
    Pending = 2,

    /// <summary>The condition has held for <c>for</c>, and the action group was notified.</summary>
    Firing = 3
}

/// <summary>What a rule asks the workspace and what answer counts.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertCondition")]
public sealed record AlertCondition {
    /// <summary>Which store.</summary>
    [Id(0)]
    public AlertSignal Signal { get; init; } = AlertSignal.Unknown;

    /// <summary>
    ///     The query, verbatim — MetricsQL for <see cref="AlertSignal.Metrics" />, SQL for
    ///     <see cref="AlertSignal.Logs" />. ⚠ Tenant-authored and run on shared infrastructure; see
    ///     <c>MonitorAlertRules</c> for the limits that go with that.
    /// </summary>
    [Id(1)]
    public string Query { get; init; } = string.Empty;

    /// <summary>The comparison.</summary>
    [Id(2)]
    public AlertOperator Operator { get; init; } = AlertOperator.Unknown;

    /// <summary>The number the sampled value is compared with.</summary>
    [Id(3)]
    public double Threshold { get; init; }

    /// <summary>How far back the query may read. Capped by the schema, because it is the query's cost.</summary>
    [Id(4)]
    public TimeSpan Lookback { get; init; }

    /// <summary>Whether a sampled value satisfies this condition.</summary>
    /// <param name="value">One sample.</param>
    public bool IsMetBy(double value) =>
        Operator switch {
            AlertOperator.GreaterThan => value > Threshold,
            AlertOperator.GreaterOrEqual => value >= Threshold,
            AlertOperator.LessThan => value < Threshold,
            AlertOperator.LessOrEqual => value <= Threshold,
            AlertOperator.Equal => value.Equals(Threshold),
            AlertOperator.NotEqual => !value.Equals(Threshold),
            _ => false
        };
}

/// <summary>Who is told, and through which <c>CyberCloud.Communication/services</c> resource.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One channel, many recipients, and the schema decided that rather than the product.</b>
///         Azure's action group is a list of receivers each with its own type; this platform's schema
///         has no array of objects (the remarks on <c>SchemaKind.Array</c>), so a rule names one
///         channel and an array of destinations on it. A second channel is a second rule. The
///         api-version that grows the tree grows this too.
///     </para>
///     <para>
///         ⚠ <b><see cref="ServiceId" /> is derived from the service's address, not looked up.</b> The
///         body names the service by its resource id path, and
///         <c>CommunicationGrainKeys.ResourceIdFor</c> turns that into the grain id the sending module
///         keys the service on — no index read, no cross-provider assembly reference, which is
///         docs/plan/03 § Assembly graph rules, rule 2's sanctioned route taken literally.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertActionGroup")]
public sealed record AlertActionGroup {
    /// <summary>The <c>CyberCloud.Communication/services</c> resource's path, as the body spelled it.</summary>
    [Id(0)]
    public string ServicePath { get; init; } = string.Empty;

    /// <summary>The service's grain id, derived from <see cref="ServicePath" />.</summary>
    [Id(1)]
    public Guid ServiceId { get; init; }

    /// <summary>Which of the service's channels carries the notification.</summary>
    [Id(2)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>Where it goes — addresses or E.164 numbers, one send each.</summary>
    [Id(3)]
    public ImmutableArray<string> Recipients { get; init; } = [];

    /// <summary>Whether a resolve is notified as well as a fire.</summary>
    [Id(4)]
    public bool NotifyOnResolve { get; init; } = true;

    /// <summary>Whether two action groups say the same thing. Recipients compare in order.</summary>
    /// <param name="other">The other.</param>
    public bool SameAs(AlertActionGroup? other) =>
        other is not null
        && string.Equals(ServicePath, other.ServicePath, StringComparison.Ordinal)
        && ServiceId == other.ServiceId
        && Channel == other.Channel
        && NotifyOnResolve == other.NotifyOnResolve
        && Recipients.AsSpan().SequenceEqual(other.Recipients.AsSpan());
}

/// <summary>Everything the evaluator needs to know about one rule. What a reconcile pass converges.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertRuleSpec")]
public sealed record AlertRuleSpec {
    /// <summary>The <c>workspaces/{workspace}/alertRules/{name}</c> resource's GUID.</summary>
    [Id(0)]
    public Guid RuleId { get; init; }

    /// <summary>The rule's name, for the notification text.</summary>
    [Id(1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The workspace's name, for the notification text.</summary>
    [Id(2)]
    public string Workspace { get; init; } = string.Empty;

    /// <summary>The workspace's canonical path — what the query seam resolves a store from.</summary>
    [Id(3)]
    public string WorkspacePath { get; init; } = string.Empty;

    /// <summary>Whether the rule is evaluated at all. Off keeps the rule and its history and stops the clock.</summary>
    [Id(4)]
    public bool Enabled { get; init; } = true;

    /// <summary>How loud.</summary>
    [Id(5)]
    public AlertSeverity Severity { get; init; } = AlertSeverity.Unknown;

    /// <summary>What is asked and what answer counts.</summary>
    [Id(6)]
    public AlertCondition Condition { get; init; } = new();

    /// <summary>How often the condition is evaluated.</summary>
    [Id(7)]
    public TimeSpan Interval { get; init; }

    /// <summary>How long the condition must hold before the rule fires. Zero fires on the first evaluation.</summary>
    [Id(8)]
    public TimeSpan For { get; init; }

    /// <summary>Who is told.</summary>
    [Id(9)]
    public AlertActionGroup ActionGroup { get; init; } = new();

    /// <summary>Whether two specs say the same thing — the reconciler's clause-4 comparison.</summary>
    /// <param name="other">The other.</param>
    public bool SameAs(AlertRuleSpec? other) =>
        other is not null
        && RuleId == other.RuleId
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Workspace, other.Workspace, StringComparison.Ordinal)
        && string.Equals(WorkspacePath, other.WorkspacePath, StringComparison.Ordinal)
        && Enabled == other.Enabled
        && Severity == other.Severity
        && Condition == other.Condition
        && Interval == other.Interval
        && For == other.For
        && ActionGroup.SameAs(other.ActionGroup);
}

/// <summary>One firing of one rule: when it fired, when it resolved, and whether anybody was told.</summary>
/// <remarks>
///     ⚠ <b>The notification outcome is recorded on the instance and never thrown away.</b> A send
///     through the sending module can refuse — a suppressed recipient, a channel with no carrier, a
///     spend limit reached — and an alert that was raised and not delivered is the failure
///     docs/plan/16 § Cost and retention honesty warns about in another form: a monitoring product
///     that quietly loses data is trusted, and so is one that quietly loses a page.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertInstance")]
public sealed record AlertInstance {
    /// <summary>This firing's own id, minted when it fired and reused for every send about it.</summary>
    [Id(0)]
    public Guid InstanceId { get; init; }

    /// <summary>The rule.</summary>
    [Id(1)]
    public Guid RuleId { get; init; }

    /// <summary>The severity the rule carried when it fired.</summary>
    [Id(2)]
    public AlertSeverity Severity { get; init; } = AlertSeverity.Unknown;

    /// <summary>When the rule moved to <see cref="AlertRuleState.Firing" />.</summary>
    [Id(3)]
    public DateTimeOffset FiredAt { get; init; }

    /// <summary>When the condition stopped holding, or <see langword="null" /> while it still fires.</summary>
    [Id(4)]
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>The sampled value that fired it.</summary>
    [Id(5)]
    public double Value { get; init; }

    /// <summary>The one-line text the fire notification carried.</summary>
    [Id(6)]
    public string Summary { get; init; } = string.Empty;

    /// <summary>What happened to the fire notification, per recipient — <c>sent</c>, or the refusal.</summary>
    [Id(7)]
    public string FireNotification { get; init; } = string.Empty;

    /// <summary>What happened to the resolve notification, or empty while unresolved or not asked for.</summary>
    [Id(8)]
    public string ResolveNotification { get; init; } = string.Empty;

    /// <summary>Whether this instance is still firing.</summary>
    public bool IsOpen => ResolvedAt is null;
}

/// <summary>A rule as the evaluator holds it: the spec, where it is, and what it has done.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertRuleSnapshot")]
public sealed record AlertRuleSnapshot {
    /// <summary>What was converged.</summary>
    [Id(0)]
    public AlertRuleSpec Spec { get; init; } = new();

    /// <summary>Where the rule is.</summary>
    [Id(1)]
    public AlertRuleState State { get; init; } = AlertRuleState.Unknown;

    /// <summary>When the condition started holding, while <see cref="AlertRuleState.Pending" />.</summary>
    [Id(2)]
    public DateTimeOffset? PendingSince { get; init; }

    /// <summary>When the rule was last evaluated, or <see langword="null" /> before the first pass.</summary>
    [Id(3)]
    public DateTimeOffset? LastEvaluatedAt { get; init; }

    /// <summary>The value the last evaluation compared, or <see langword="null" /> when it had none.</summary>
    [Id(4)]
    public double? LastValue { get; init; }

    /// <summary>Why the last evaluation could not run, or empty when it could.</summary>
    [Id(5)]
    public string LastError { get; init; } = string.Empty;

    /// <summary>When the rule is next due.</summary>
    [Id(6)]
    public DateTimeOffset? NextDueAt { get; init; }

    /// <summary>Every firing, oldest first, capped at <c>IAlertEvaluatorGrain.InstancesKept</c>.</summary>
    [Id(7)]
    public ImmutableArray<AlertInstance> Instances { get; init; } = [];
}

/// <summary>What one evaluation pass over a workspace did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertEvaluationReport")]
public sealed record AlertEvaluationReport {
    /// <summary>How many rules were due and were evaluated.</summary>
    [Id(0)]
    public int Evaluated { get; init; }

    /// <summary>How many were not due yet, or disabled, and were skipped.</summary>
    [Id(1)]
    public int Skipped { get; init; }

    /// <summary>How many moved to <see cref="AlertRuleState.Firing" /> on this pass.</summary>
    [Id(2)]
    public int Fired { get; init; }

    /// <summary>How many moved from <see cref="AlertRuleState.Firing" /> to <see cref="AlertRuleState.Ok" />.</summary>
    [Id(3)]
    public int Resolved { get; init; }

    /// <summary>How many evaluations the query seam could not answer.</summary>
    [Id(4)]
    public int Errors { get; init; }

    /// <summary>Whether the pass left the reminder armed.</summary>
    [Id(5)]
    public bool Armed { get; init; }

    /// <summary>
    ///     How many rules were due and were not asked, because <see cref="IAlertEvaluatorGrain.PassBudget" />
    ///     had elapsed when the pass reached them. They stay due and go first next tick.
    /// </summary>
    [Id(6)]
    public int Deferred { get; init; }
}

/// <summary>One question to a workspace's store, asked by the evaluator through <see cref="IAlertQuerySeam" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertQuery")]
public sealed record AlertQuery {
    /// <summary>The tenant. The seam must never answer from another tenant's store.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>
    ///     The workspace's canonical path. ⚠ The path and not the GUID, because a child's reconcile
    ///     pass never learns its parent's GUID (<c>CommunicationGrainKeys.ResourceIdFor</c> says why).
    ///     A real seam resolves the store from it — the row the workspace publishes carries the
    ///     <c>accountID</c> and the database name.
    /// </summary>
    [Id(1)]
    public string WorkspacePath { get; init; } = string.Empty;

    /// <summary>Which store.</summary>
    [Id(2)]
    public AlertSignal Signal { get; init; } = AlertSignal.Unknown;

    /// <summary>The query, verbatim.</summary>
    [Id(3)]
    public string Expression { get; init; } = string.Empty;

    /// <summary>How far back it may read.</summary>
    [Id(4)]
    public TimeSpan Lookback { get; init; }

    /// <summary>The instant the query is evaluated at.</summary>
    [Id(5)]
    public DateTimeOffset At { get; init; }
}

/// <summary>One value a query produced, with the labels that distinguish it from the others.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertSample")]
public sealed record AlertSample {
    /// <summary>The series' labels, spelled as one string — <c>{instance="web-1"}</c>.</summary>
    [Id(0)]
    public string Labels { get; init; } = string.Empty;

    /// <summary>The value.</summary>
    [Id(1)]
    public double Value { get; init; }
}

/// <summary>What a query produced.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertQueryResult")]
public sealed record AlertQueryResult {
    /// <summary>Every sample. Empty means the query ran and matched nothing, which is not an error.</summary>
    [Id(0)]
    public ImmutableArray<AlertSample> Samples { get; init; } = [];
}
