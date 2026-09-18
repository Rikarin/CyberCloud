namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>What <see cref="AlertEvaluatorGrain" /> holds: every rule of one workspace, with its history.</summary>
/// <remarks>
///     ⚠ <b>Durable, and the reasoning is beside the grain's line in <c>durable-grains.txt</c>.</b>
///     Nothing here is a credential — a rule's action group carries recipients' addresses, which are
///     what a notification is sent to and are the tenant's own data, and never a carrier handle or
///     an ingest key.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertEvaluatorState")]
public sealed class AlertEvaluatorState {
    /// <summary>Every rule, by its resource GUID.</summary>
    [Id(0)]
    public Dictionary<Guid, AlertRuleRecord> Rules { get; set; } = [];
}

/// <summary>One rule as the evaluator tracks it between ticks.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Monitor.AlertRuleRecord")]
public sealed class AlertRuleRecord {
    /// <summary>What was converged.</summary>
    [Id(0)]
    public AlertRuleSpec Spec { get; set; } = new();

    /// <summary>Where the rule is.</summary>
    [Id(1)]
    public AlertRuleState State { get; set; } = AlertRuleState.Ok;

    /// <summary>When the condition started holding, while pending.</summary>
    [Id(2)]
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>When the rule was last evaluated.</summary>
    [Id(3)]
    public DateTimeOffset? LastEvaluatedAt { get; set; }

    /// <summary>The value the last evaluation compared.</summary>
    [Id(4)]
    public double? LastValue { get; set; }

    /// <summary>Why the last evaluation could not run, or empty.</summary>
    [Id(5)]
    public string LastError { get; set; } = string.Empty;

    /// <summary>When the rule is next due, or <see langword="null" /> for "the next tick".</summary>
    [Id(6)]
    public DateTimeOffset? NextDueAt { get; set; }

    /// <summary>Every firing, oldest first.</summary>
    [Id(7)]
    public List<AlertInstance> Instances { get; set; } = [];

    /// <summary>The firing that has not resolved, or <see langword="null" />.</summary>
    public AlertInstance? Open => Instances.LastOrDefault(static x => x.IsOpen);

    /// <summary>What a caller sees.</summary>
    public AlertRuleSnapshot ToSnapshot() =>
        new() {
            Spec = Spec,
            State = State,
            PendingSince = PendingSince,
            LastEvaluatedAt = LastEvaluatedAt,
            LastValue = LastValue,
            LastError = LastError,
            NextDueAt = NextDueAt,
            Instances = [.. Instances]
        };
}
