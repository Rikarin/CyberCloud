using CyberCloud.Communication.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>
///     <see cref="IAlertEvaluatorGrain" /> — Coordinator, Durable, key <c>res/{evaluatorId:N}</c>.
///     One workspace's rules, one reminder, one query at a time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="IAlertEvaluatorGrain" /> first</b> for why there is one of these per
///         workspace, why it is durable, and why its key is the workspace's address. This file is
///         the tick.
///     </para>
///     <para>
///         ⚠ <b>The order inside a fire is: record, write, send, record, write — and the write in
///         the middle is what makes a retry safe.</b> An <see cref="AlertInstance.InstanceId" /> is
///         minted when the rule fires and is half of every idempotency key sent about it. If the
///         silo dies between the send and the second write, the next activation finds the instance
///         with an empty <see cref="AlertInstance.FireNotification" />, and a hand-driven or
///         reminder-driven pass does not re-fire — the rule is already <c>Firing</c> — so nothing is
///         sent twice. What is lost is the outcome text, which the next resolve's send does not
///         depend on. Writing the instance <i>after</i> the send would make the same crash mint a
///         second id on the next tick and page twice.
///     </para>
///     <para>
///         ⚠ <b>Suppression is honoured by the sending module and not re-implemented here, and that
///         is the point of going through <c>IMessageSender</c>.</b> <c>MessageGrain.DispatchAsync</c>
///         checks the service's list before a carrier is resolved, for the tenant's sends and the
///         platform's alike; a recipient on it comes back as a refusal, which this grain records on
///         the instance by name. An evaluator that kept its own list would be a second list that
///         could disagree with the one <c>STOP</c> writes to.
///     </para>
///     <para>
///         ⚠ <b>The reminder is registered only when <c>GetReminder</c> answers null</b>, which is
///         the guard <c>ResourceGroupGrain.ArmOrDisarmAsync</c> carries for #83 and
///         <c>ExpirySweeperGrain</c> for the #12 review: <c>RegisterOrUpdateReminder</c> rewrites an
///         existing row with a fresh due time, so calling it on every rule upsert would push the
///         tick out by a minute per PUT and a workspace with a busy tenant would never be evaluated.
///     </para>
/// </remarks>
public sealed class AlertEvaluatorGrain(
    [PersistentState("alert-evaluator", StorageTiers.Durable)]
    IPersistentState<AlertEvaluatorState> state,
    IAlertQuerySeam queries,
    IMessageSender sender,
    IClock clock,
    ILogger<AlertEvaluatorGrain> logger
)
    : Grain, IAlertEvaluatorGrain, IRemindable {
    /// <summary>The reminder's name. One per workspace.</summary>
    public const string ReminderName = "evaluate-alerts";

    Guid tenantId;
    Guid evaluatorId;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = MonitorGrainDecoder.TenantOf(this);
        evaluatorId = MonitorGrainDecoder.ResourceOf(this);

        // ⚠ ARMED FROM DURABLE STATE, THE WAY ResourceGroupGrain ARMS AND ExpirySweeperGrain DOES
        // NOT. That grain holds nothing and argues that an activation is not evidence; this one
        // holds the rules, already loaded, so "is there anything to evaluate" is a read of memory
        // and not a call. It is also what puts a reminder row back after a table restored from a
        // backup, or a rule converged on a silo with no reminder service.
        await ArmOrDisarmAsync();
    }

    /// <inheritdoc />
    public async Task<Result<AlertRuleSnapshot>> UpsertRuleAsync(AlertRuleSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.RuleId == Guid.Empty) {
            return Result<AlertRuleSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                "An alert rule needs the resource's GUID. Guid.Empty is what a path-parsed ResourceId "
                + "carries before the index answers — see ReconcileContext.Id."
            );
        }

        var now = clock.UtcNow;

        if (!state.State.Rules.TryGetValue(spec.RuleId, out var record)) {
            if (state.State.Rules.Count >= IAlertEvaluatorGrain.MaxRules) {
                return Result<AlertRuleSnapshot>.Failure(
                    ErrorCode.QuotaExceeded,
                    $"Workspace '{spec.Workspace}' already carries {state.State.Rules.Count} alert rules, "
                    + $"which is the most one workspace may ({IAlertEvaluatorGrain.MaxRules}). Every rule "
                    + "is a tenant-authored query on the shared store, and the cap is docs/plan/16 "
                    + "§ Alerts' query-cost limit in its coarsest form. Delete a rule, or split the "
                    + "workspace."
                );
            }

            record = new() { Spec = spec, State = AlertRuleState.Ok };
            state.State.Rules[spec.RuleId] = record;
        } else {
            // ⚠ A CHANGED CONDITION RESOLVES AN OPEN INSTANCE, WITHOUT A NOTIFICATION. The instance
            // was fired by a condition that no longer exists, so no evaluation of the new one can
            // resolve it — it would stay open forever, or the new condition's first firing would be
            // read as its continuation. Neither is what the tenant meant by editing the rule.
            if (record.Spec.Condition != spec.Condition && record.State != AlertRuleState.Ok) {
                Close(record, now, "not notified: the condition was replaced");
            }

            if (!spec.Enabled && record.State != AlertRuleState.Ok) {
                Close(record, now, "not notified: the rule was disabled");
            }

            record.Spec = spec;
        }

        // Due on the next tick, whatever the interval — a rule just written should be evaluated
        // soon, and a tenant testing one should not wait an hour to learn the query is wrong.
        record.NextDueAt = null;

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result<AlertRuleSnapshot>.Success(record.ToSnapshot());
    }

    /// <inheritdoc />
    public async Task<Result> RemoveRuleAsync(Guid ruleId) {
        if (!state.State.Rules.Remove(ruleId)) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"This workspace carries no alert rule {ruleId:D}.");
        }

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid ruleId) =>
        Task.FromResult(
            state.State.Rules.TryGetValue(ruleId, out var record)
                ? Result<AlertRuleSnapshot>.Success(record.ToSnapshot())
                : Result<AlertRuleSnapshot>.Failure(ErrorCode.ResourceNotFound, $"This workspace carries no alert rule {ruleId:D}.")
        );

    /// <inheritdoc />
    public Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync() =>
        Task.FromResult(
            Result<ImmutableArray<AlertRuleSnapshot>>.Success([.. state.State.Rules.Values.Select(x => x.ToSnapshot())])
        );

    /// <inheritdoc />
    public async Task<Result<AlertEvaluationReport>> EvaluateAsync() {
        var now = clock.UtcNow;
        int evaluated = 0, skipped = 0, fired = 0, resolved = 0, errors = 0;

        // ⚠ BY RULE ID, SO TWO TICKS OVER ONE STATE EVALUATE IN ONE ORDER. Dictionary order is not a
        // contract, and a test that asserts "the first rule fired before the second was queried"
        // needs one that is.
        foreach (var record in state.State.Rules.Values.OrderBy(x => x.Spec.RuleId).ToArray()) {
            if (!record.Spec.Enabled || (record.NextDueAt is { } due && due > now)) {
                skipped++;
                continue;
            }

            evaluated++;

            var answer = await AskAsync(record.Spec, now);
            var decision = AlertEvaluation.Decide(record.Spec, record.State, record.PendingSince, answer, now);

            record.LastEvaluatedAt = now;
            record.NextDueAt = now + record.Spec.Interval;
            record.LastValue = decision.Value;
            record.LastError = answer.TryGetError(out var failed) ? failed.Message : string.Empty;
            record.State = decision.State;
            record.PendingSince = decision.PendingSince;

            if (failed is not null) {
                errors++;
            }

            switch (decision.Transition) {
                case AlertTransition.Fired: {
                    fired++;

                    var instance = new AlertInstance {
                        InstanceId = Guid.NewGuid(),
                        RuleId = record.Spec.RuleId,
                        Severity = record.Spec.Severity,
                        FiredAt = now,
                        Value = decision.Value ?? 0d,
                        Summary = MonitorAlertRules.Summary(record.Spec, decision.Value ?? 0d, now, fired: true)
                    };

                    record.Instances.Add(instance);
                    while (record.Instances.Count > IAlertEvaluatorGrain.InstancesKept) {
                        record.Instances.RemoveAt(0);
                    }

                    // The write between the mint and the send — see the type's remarks.
                    await state.WriteStateAsync();

                    var outcome = await NotifyAsync(record.Spec, instance.InstanceId, instance.Summary, "fired");
                    record.Instances[record.Instances.IndexOf(instance)] = instance with { FireNotification = outcome };
                    break;
                }

                case AlertTransition.Resolved: {
                    resolved++;

                    if (record.Open is { } open) {
                        var closed = open with { ResolvedAt = now };
                        var index = record.Instances.IndexOf(open);
                        record.Instances[index] = closed;

                        if (record.Spec.ActionGroup.NotifyOnResolve) {
                            await state.WriteStateAsync();

                            var summary = MonitorAlertRules.Summary(record.Spec, decision.Value ?? closed.Value, now, fired: false);
                            var outcome = await NotifyAsync(record.Spec, closed.InstanceId, summary, "resolved");
                            record.Instances[index] = closed with { ResolveNotification = outcome };
                        } else {
                            record.Instances[index] = closed with { ResolveNotification = "not asked for" };
                        }
                    }

                    break;
                }
            }
        }

        await state.WriteStateAsync();
        var armed = await ArmOrDisarmAsync();

        return Result<AlertEvaluationReport>.Success(
            new() { Evaluated = evaluated, Skipped = skipped, Fired = fired, Resolved = resolved, Errors = errors, Armed = armed }
        );
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsArmedAsync() {
        try {
            return Result<bool>.Success(await this.GetReminder(ReminderName) is not null);
        } catch (InvalidOperationException) {
            // A silo with no reminder service has no row and never will have one, which is exactly
            // what `false` says to the operator asking.
            return Result<bool>.Success(false);
        }
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ReminderName, StringComparison.Ordinal)) {
            return;
        }

        var report = await EvaluateAsync();

        if (report.TryGetError(out var error)) {
            logger.LogWarning(
                "The alert evaluator for workspace {Evaluator} in tenant {Tenant} could not complete a pass: {Reason}",
                evaluatorId,
                tenantId,
                error.Message
            );
        }
    }

    // ── The question ─────────────────────────────────────────────────────────────────────────

    /// <summary>Asks the store, under the timeout, and turns a timeout into a failure the rule records.</summary>
    async Task<Result<AlertQueryResult>> AskAsync(AlertRuleSpec spec, DateTimeOffset now) {
        using var timeout = new CancellationTokenSource(IAlertEvaluatorGrain.QueryTimeout);

        try {
            return await queries.QueryAsync(
                new() {
                    TenantId = tenantId,
                    WorkspacePath = spec.WorkspacePath,
                    Signal = spec.Condition.Signal,
                    Expression = spec.Condition.Query,
                    Lookback = spec.Condition.Lookback,
                    At = now
                },
                timeout.Token
            );
        } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
            return Result<AlertQueryResult>.Failure(
                ErrorCode.OperationTimeout,
                $"The {MonitorAlertRules.Spell(spec.Condition.Signal)} store did not answer rule "
                + $"'{spec.Name}' within {IAlertEvaluatorGrain.QueryTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} "
                + "seconds. The rule keeps its state and is due again next tick — docs/plan/16 § Alerts' "
                + "query cost limit."
            );
        }
    }

    // ── The answer ───────────────────────────────────────────────────────────────────────────

    /// <summary>Sends one line to every recipient, and says per recipient what happened.</summary>
    /// <remarks>
    ///     ⚠ <b>One idempotency key per recipient per instance per kind of notification.</b> The
    ///     instance id is durable before this runs, so a re-drive computes the same keys and the
    ///     sending module hands back the message already sent rather than sending again. The
    ///     recipient's index is in the key because two destinations under one key would differ in
    ///     content and come back <c>Conflict</c> — a broken second send rather than a duplicate.
    /// </remarks>
    async Task<string> NotifyAsync(AlertRuleSpec spec, Guid instanceId, string summary, string kind) {
        var outcomes = new StringBuilder();

        for (var i = 0; i < spec.ActionGroup.Recipients.Length; i++) {
            var recipient = spec.ActionGroup.Recipients[i];

            var sent = await sender.SendAsync(
                tenantId,
                new() {
                    ServiceId = spec.ActionGroup.ServiceId,
                    Channel = spec.ActionGroup.Channel,
                    Destination = recipient,
                    Body = summary,
                    IdempotencyKey = string.Create(CultureInfo.InvariantCulture, $"alert-{instanceId:N}-{kind}-{i}")
                }
            );

            if (outcomes.Length > 0) {
                outcomes.Append("; ");
            }

            if (sent.TryGetValue(out var snapshot)) {
                outcomes.Append(string.Create(CultureInfo.InvariantCulture, $"{recipient}: sent ({snapshot.Status})"));
            } else {
                // ⚠ Recorded, never swallowed. A suppressed recipient, a channel with no carrier and
                // a spend limit reached all land here, each with the sending module's own sentence.
                outcomes.Append(string.Create(CultureInfo.InvariantCulture, $"{recipient}: refused — {sent.Error!.Message}"));
            }
        }

        return outcomes.ToString();
    }

    // ── The clock ────────────────────────────────────────────────────────────────────────────

    /// <summary>Arms the reminder while an enabled rule exists and disarms it otherwise.</summary>
    /// <returns>Whether the reminder is armed afterwards.</returns>
    async Task<bool> ArmOrDisarmAsync() {
        var wanted = state.State.Rules.Values.Any(x => x.Spec.Enabled);

        try {
            var existing = await this.GetReminder(ReminderName);

            if (wanted) {
                if (existing is null) {
                    _ = await this.RegisterOrUpdateReminder(ReminderName, IAlertEvaluatorGrain.Tick, IAlertEvaluatorGrain.Tick);
                }

                return true;
            }

            if (existing is not null) {
                await this.UnregisterReminder(existing);
            }

            return false;
        } catch (InvalidOperationException error) {
            logger.LogWarning(
                "The alert evaluator for workspace {Evaluator} in tenant {Tenant} could not arm its reminder "
                + "because this silo has no reminder service: {Reason} Rules here are evaluated only when "
                + "EvaluateAsync is called by hand.",
                evaluatorId,
                tenantId,
                error.Message
            );

            return false;
        }
    }

    /// <summary>Resolves an open instance without a notification, and returns the rule to <c>Ok</c>.</summary>
    static void Close(AlertRuleRecord record, DateTimeOffset now, string why) {
        if (record.Open is { } open) {
            record.Instances[record.Instances.IndexOf(open)] = open with { ResolvedAt = now, ResolveNotification = why };
        }

        record.State = AlertRuleState.Ok;
        record.PendingSince = null;
    }
}
