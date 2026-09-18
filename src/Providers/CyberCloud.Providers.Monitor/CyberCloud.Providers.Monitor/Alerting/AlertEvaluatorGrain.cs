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
///         ⚠
///         <b>
///             The order inside a fire is: record, write, send, record, write — and the write in
///             the middle is what makes a retry safe.
///         </b> An <see cref="AlertInstance.InstanceId" /> is
///         minted when the rule fires and is half of every idempotency key sent about it. If the
///         silo dies between the send and the second write, the next activation finds the instance
///         with an empty <see cref="AlertInstance.FireNotification" />, and a hand-driven or
///         reminder-driven pass does not re-fire — the rule is already <c>Firing</c> — so nothing is
///         sent twice. What is lost is the outcome text, which the next resolve's send does not
///         depend on. Writing the instance <i>after</i> the send would make the same crash mint a
///         second id on the next tick and page twice.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Suppression is honoured by the sending module and not re-implemented here, and that
///             is the point of going through <c>IMessageSender</c>.
///         </b> <c>MessageGrain.DispatchAsync</c>
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
        if (!state.State.Rules.TryGetValue(ruleId, out var record)) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"This workspace carries no alert rule {ruleId:D}.");
        }

        // ⚠ THE SEND BEFORE THE REMOVE, AND THE IDEMPOTENCY KEY IS WHAT MAKES THAT SAFE. A silo
        // that dies between the two leaves the rule held and firing, the reconciler's delete pass
        // re-drives, and the sending module hands back the message it already sent under the
        // instance's resolve key rather than paging twice — the same argument the type's remarks
        // make for a fire. Removing first and sending second would lose the instance id the key is
        // made of.
        if (record is { State: AlertRuleState.Firing, Open: { } open, Spec.ActionGroup.NotifyOnResolve: true }) {
            var now = clock.UtcNow;
            var summary = MonitorAlertRules.Summary(record.Spec, open.Value, now, false)
                + " — the rule was deleted";
            _ = await NotifyAsync(record.Spec, open.InstanceId, summary, "resolved");
        }

        state.State.Rules.Remove(ruleId);
        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid ruleId) =>
        Task.FromResult(
            state.State.Rules.TryGetValue(ruleId, out var record)
                ? Result<AlertRuleSnapshot>.Success(record.ToSnapshot())
                : Result<AlertRuleSnapshot>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"This workspace carries no alert rule {ruleId:D}."
                )
        );

    /// <inheritdoc />
    public Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync() =>
        Task.FromResult(
            Result<ImmutableArray<AlertRuleSnapshot>>.Success(
                [.. state.State.Rules.Values.Select(static x => x.ToSnapshot())]
            )
        );

    /// <inheritdoc />
    public async Task<Result<AlertEvaluationReport>> EvaluateAsync() {
        var now = clock.UtcNow;
        int evaluated = 0, skipped = 0, deferred = 0, fired = 0, resolved = 0, errors = 0;

        // ⚠ MOST OVERDUE FIRST, THEN BY RULE ID, SO TWO TICKS OVER ONE STATE EVALUATE IN ONE ORDER
        // AND A RULE THE BUDGET DEFERRED IS THE FIRST ONE ASKED NEXT TIME. Dictionary order is not
        // a contract, and a test that asserts "the first rule fired before the second was queried"
        // needs one that is. A null due time is "the next tick", which is as overdue as it gets; a
        // rule that was evaluated carries now + interval and sorts behind every rule that was not.
        // Rule id alone was the first order, and under it a store slow enough to exhaust the budget
        // would have evaluated the same head of the list on every tick and the tail on none.
        var ordered = state.State.Rules.Values
            .OrderBy(static x => x.NextDueAt ?? DateTimeOffset.MinValue)
            .ThenBy(static x => x.Spec.RuleId)
            .ToArray();

        foreach (var record in ordered) {
            if (!record.Spec.Enabled || (record.NextDueAt is { } due && due > now)) {
                skipped++;
                continue;
            }

            // ⚠ THE BUDGET, READ FROM THE CLOCK AND NOT FROM `now`, which is the pass's one stamp
            // and does not move. A rule reached past it keeps its due time, so it is still due on
            // the next tick and, by the order above, first.
            if (clock.UtcNow - now >= IAlertEvaluatorGrain.PassBudget) {
                deferred++;
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
                        Summary = MonitorAlertRules.Summary(record.Spec, decision.Value ?? 0d, now, true)
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

                            var summary = MonitorAlertRules.Summary(
                                record.Spec,
                                decision.Value ?? closed.Value,
                                now,
                                false
                            );
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

        // A pass that asked nothing changed nothing, and a workspace whose every rule is on an
        // hourly interval should not rewrite its whole history — fifty rules of instances — on the
        // fifty-nine ticks in between.
        if (evaluated > 0) {
            await state.WriteStateAsync();
        }

        var armed = await ArmOrDisarmAsync();

        return Result<AlertEvaluationReport>.Success(
            new() {
                Evaluated = evaluated,
                Skipped = skipped,
                Deferred = deferred,
                Fired = fired,
                Resolved = resolved,
                Errors = errors,
                Armed = armed
            }
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
        } catch (Exception error) when (error is not OperationCanceledException) {
            // ⚠ NOT `false`. The table exists and could not be read — Redis is away — and the
            // reconciler that asked would re-arm on `false`, which is one more call into the same
            // table. A failure is retried on the reconcile loop's schedule instead.
            return Result<bool>.Failure(
                ErrorCode.InternalError,
                $"The reminder table could not say whether workspace {evaluatorId:D}'s evaluator is armed: {error.Message}"
            );
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

    /// <summary>
    ///     Asks the store, under the timeout, and turns a timeout or a thrown exception into a
    ///     failure the rule records.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing a query raises leaves this method (2026-09-15, #32 review).</b> The first
    ///     version caught its own timeout and nothing else, on the strength of a seam contract that
    ///     asks for a failure but does not forbid an exception — and a real seam over
    ///     <c>HttpClient</c> throws <c>HttpRequestException</c> when the store's address does not
    ///     resolve. One such rule ended the pass at that rule, and every rule sorted after it went
    ///     unevaluated on every tick the fault lasted, which nothing in this tree could see because
    ///     the refusing default and the scripted store both return. A thrown query is what a
    ///     returned failure is: recorded on this rule, and the pass goes on.
    /// </remarks>
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
        } catch (Exception error) when (error is not OperationCanceledException) {
            logger.LogWarning(
                error,
                "The {Signal} query seam threw for rule {Rule} on workspace {Evaluator} in tenant {Tenant}; "
                + "the rule records it and keeps its state",
                spec.Condition.Signal,
                spec.Name,
                evaluatorId,
                tenantId
            );

            return Result<AlertQueryResult>.Failure(
                ErrorCode.InternalError,
                $"The {MonitorAlertRules.Spell(spec.Condition.Signal)} store's query seam threw for rule "
                + $"'{spec.Name}': {error.GetType().Name}: {error.Message} The rule keeps its state and is "
                + "due again next tick."
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

            Result<MessageSnapshot> sent;

            try {
                sent = await sender.SendAsync(
                    tenantId,
                    new() {
                        ServiceId = spec.ActionGroup.ServiceId,
                        Channel = spec.ActionGroup.Channel,
                        Destination = recipient,
                        Body = summary,
                        IdempotencyKey = string.Create(CultureInfo.InvariantCulture, $"alert-{instanceId:N}-{kind}-{i}")
                    }
                );
            } catch (Exception error) when (error is not OperationCanceledException) {
                // ⚠ The same decision AskAsync takes, for the same reason: a sending module that
                // threw — a response timeout on a busy message grain — must not end the pass for
                // the rules after this one. What is recorded is honest about what is known: the
                // send may or may not have gone, and the instance's key makes a re-drive safe.
                sent = Result<MessageSnapshot>.Failure(
                    ErrorCode.InternalError,
                    $"the sending module did not answer ({error.GetType().Name}: {error.Message})"
                );
            }

            if (outcomes.Length > 0) {
                outcomes.Append("; ");
            }

            if (sent.TryGetValue(out var snapshot)) {
                outcomes.Append(string.Create(CultureInfo.InvariantCulture, $"{recipient}: sent ({snapshot.Status})"));
            } else {
                // ⚠ Recorded, never swallowed. A suppressed recipient, a channel with no carrier and
                // a spend limit reached all land here, each with the sending module's own sentence.
                outcomes.Append(
                    string.Create(CultureInfo.InvariantCulture, $"{recipient}: refused — {sent.Error!.Message}")
                );
            }
        }

        return outcomes.ToString();
    }

    // ── The clock ────────────────────────────────────────────────────────────────────────────

    /// <summary>Arms the reminder while an enabled rule exists and disarms it otherwise.</summary>
    /// <returns>Whether the reminder is armed afterwards.</returns>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A reminder-table fault is logged and answered <c>false</c>, and the reconciler is
    ///         what turns that into a retry (2026-09-15, #32 review).
    ///     </b> The write that precedes every
    ///     call here has already happened, so throwing would fail an upsert whose rule is held —
    ///     and the first version did throw, out of the upsert, leaving a held rule with no row that
    ///     the next pass judged converged on its spec alone. Now the upsert succeeds, "held" and
    ///     "armed" are two facts, and <c>MonitorAlertRuleReconciler</c> asks
    ///     <see cref="IsArmedAsync" /> for the second before it says <c>Converged</c>.
    /// </remarks>
    async Task<bool> ArmOrDisarmAsync() {
        var wanted = state.State.Rules.Values.Any(static x => x.Spec.Enabled);

        try {
            var existing = await this.GetReminder(ReminderName);

            if (wanted) {
                if (existing is null) {
                    _ = await this.RegisterOrUpdateReminder(
                        ReminderName,
                        IAlertEvaluatorGrain.Tick,
                        IAlertEvaluatorGrain.Tick
                    );
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
        } catch (Exception error) when (error is not OperationCanceledException) {
            logger.LogWarning(
                error,
                "The alert evaluator for workspace {Evaluator} in tenant {Tenant} could not read or write its "
                + "reminder row. The rules are held; the reconciler re-arms on its next pass, and the next "
                + "activation arms from state.",
                evaluatorId,
                tenantId
            );

            return false;
        }
    }

    /// <summary>Resolves an open instance without a notification, and returns the rule to <c>Ok</c>.</summary>
    static void Close(AlertRuleRecord record, DateTimeOffset now, string why) {
        if (record.Open is { } open) {
            record.Instances[record.Instances.IndexOf(open)] =
                open with { ResolvedAt = now, ResolveNotification = why };
        }

        record.State = AlertRuleState.Ok;
        record.PendingSince = null;
    }
}
