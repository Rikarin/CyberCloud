using CyberCloud.Authorization.Contracts;
using CyberCloud.Billing.Pricing;
using CyberCloud.Communication.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace CyberCloud.Billing.Grains;

/// <summary>
///     <see cref="IBudgetGrain" /> — Entity, Durable, key <c>res/{budgetId:N}</c>. One budget, one
///     hourly reminder, one evaluation at a time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="IBudgetGrain" /> first</b> for why it is durable and why a
///         subscription-scoped budget needs a grant of its own. This file is the tick.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The order inside a fire is the alert evaluator's: record, write, send, record, write.
///         </b> A threshold's alert is written <i>before</i> the send, so a silo that dies between the
///         two finds the alert on the next activation and does not fire it again; what it loses is the
///         outcome text. Sending first would page twice on the same crash. The idempotency key —
///         budget, period, kind, percentage, recipient — is the second defence, and it is a function of
///         the period so that next month's crossing is a new message and a retry of this month's is
///         not (<c>IMessageSender</c>'s remarks on why a clock reading is the wrong key).
///     </para>
///     <para>
///         ⚠
///         <b>
///             Suppression, spend limits and the carrier are the sending module's, and a refusal is
///             recorded on the alert rather than retried here.
///         </b> The same decision <c>AlertEvaluatorGrain</c> takes, for the same reason: a second list
///         kept here could disagree with the one <c>STOP</c> writes to.
///     </para>
/// </remarks>
public sealed class BudgetGrain(
    [PersistentState("budget", StorageTiers.Durable)]
    IPersistentState<BudgetState> state,
    IGrainFactory grains,
    UsagePricing pricing,
    IMessageSender sender,
    IClock clock,
    ILogger<BudgetGrain> logger
)
    : Grain, IBudgetGrain, IRemindable {
    /// <summary>The reminder's name.</summary>
    public const string ReminderName = "evaluate-budget";

    /// <summary>How far back the forecast's rate is read — docs/plan/22 § Cost visibility, "linear on the trailing 7 days".</summary>
    public static readonly TimeSpan ForecastWindow = TimeSpan.FromDays(7);

    Guid tenantId;
    Guid budgetId;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = BillingGrainKeys.TenantOf(this);
        budgetId = BillingGrainKeys.Decode(this, GrainKeyKind.Resource).Id;

        // Armed from durable state, as AlertEvaluatorGrain is: a row lost with a reminder table
        // restored from backup comes back on the next activation.
        await ArmOrDisarmAsync();
    }

    /// <inheritdoc />
    public async Task<Result<BudgetSnapshot>> UpsertAsync(BudgetSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.BudgetId != budgetId) {
            return Result<BudgetSnapshot>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The spec names budget {spec.BudgetId:D} and reached the grain of {budgetId:D}. A budget's grain is "
                + "its own resource GUID."
            );
        }

        state.State.Spec = spec;
        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result<BudgetSnapshot>.Success(Snapshot(spec));
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync() {
        if (state.State.Spec is null) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"There is no budget {budgetId:D}.");
        }

        state.State.Spec = null;
        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<BudgetSnapshot>> GetAsync() =>
        Task.FromResult(
            state.State.Spec is { } spec
                ? Result<BudgetSnapshot>.Success(Snapshot(spec))
                : Result<BudgetSnapshot>.Failure(ErrorCode.ResourceNotFound, $"There is no budget {budgetId:D}.")
        );

    /// <inheritdoc />
    public async Task<Result<BudgetEvaluationReport>> EvaluateAsync() {
        if (state.State.Spec is not { } spec) {
            return Result<BudgetEvaluationReport>.Failure(ErrorCode.ResourceNotFound, $"There is no budget {budgetId:D}.");
        }

        if (!spec.Enabled) {
            return Result<BudgetEvaluationReport>.Success(new() { Evaluated = false, Error = "the budget is disabled" });
        }

        var now = clock.UtcNow;
        var (periodStart, periodEnd) = PeriodOf(spec.Period, now);

        var figures = await FiguresAsync(spec, periodStart, periodEnd, now);
        if (figures.TryGetError(out var error)) {
            state.State.LastEvaluatedAt = now;
            state.State.LastError = error.Message;

            // ⚠ A REVOKED GRANT TAKES THE FIGURES WITH IT. The last evaluation's actual, forecast and
            // alert figures were the subscription's spend, and a budget that may no longer read the
            // subscription must not keep showing it to whoever reads the budget. The alerts themselves
            // stay, without their figure, so a threshold that already fired this period doesn't fire
            // again when the grant comes back.
            if (error.Code == ErrorCode.AuthorizationFailed) {
                state.State.Actual = 0m;
                state.State.Forecast = 0m;

                for (var i = 0; i < state.State.Alerts.Count; i++) {
                    state.State.Alerts[i] = state.State.Alerts[i] with { Figure = 0m };
                }
            }

            await state.WriteStateAsync();

            return Result<BudgetEvaluationReport>.Success(new() { Evaluated = false, Error = error.Message });
        }

        var (currency, actual, forecast) = figures.GetValueOrThrow();

        state.State.Currency = currency;
        state.State.PeriodStart = periodStart;
        state.State.PeriodEnd = periodEnd;
        state.State.Actual = actual;
        state.State.Forecast = forecast;
        state.State.LastEvaluatedAt = now;
        state.State.LastError = string.Empty;

        var fired = 0;

        foreach (var threshold in spec.Thresholds.OrderBy(static x => x.Kind).ThenBy(static x => x.Percent)) {
            var figure = threshold.Kind == ThresholdKind.Forecast ? forecast : actual;
            var line = spec.Amount * threshold.Percent / 100m;

            if (figure < line || HasFired(periodStart, threshold)) {
                continue;
            }

            fired++;

            var alert = new BudgetAlert {
                PeriodStart = periodStart,
                Percent = threshold.Percent,
                Kind = threshold.Kind,
                FiredAt = now,
                Figure = figure
            };

            state.State.Alerts.Add(alert);
            while (state.State.Alerts.Count > IBudgetGrain.AlertsKept) {
                state.State.Alerts.RemoveAt(0);
            }

            // The write between the record and the send — see the type's remarks.
            await state.WriteStateAsync();

            var outcome = await NotifyAsync(spec, alert, currency, periodEnd);
            state.State.Alerts[state.State.Alerts.IndexOf(alert)] = alert with { Notification = outcome };
        }

        await state.WriteStateAsync();

        return Result<BudgetEvaluationReport>.Success(
            new() { Evaluated = true, Actual = actual, Forecast = forecast, Fired = fired }
        );
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsArmedAsync() {
        try {
            return Result<bool>.Success(await this.GetReminder(ReminderName) is not null);
        } catch (InvalidOperationException) {
            return Result<bool>.Success(false);
        } catch (Exception error) when (error is not OperationCanceledException) {
            return Result<bool>.Failure(
                ErrorCode.InternalError,
                $"The reminder table could not say whether budget {budgetId:D} is armed: {error.Message}"
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
                "Budget {Budget} in tenant {Tenant} could not be evaluated: {Reason}",
                budgetId,
                tenantId,
                error.Message
            );
        }
    }

    // ── The figures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The period's actual cost so far and its forecast, in the scope the budget covers.</summary>
    async Task<Result<(string Currency, decimal Actual, decimal Forecast)>> FiguresAsync(
        BudgetSpec spec,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset now
    ) {
        if (spec.Scope == BudgetScope.Subscription && !await BudgetMayReadSubscriptionAsync(spec)) {
            return Result<(string, decimal, decimal)>.Failure(
                ErrorCode.AuthorizationFailed,
                $"This budget covers subscription {spec.SubscriptionId:D} and has not been granted reader on it. A "
                + "subscription-wide figure is visible only to principals that may read the subscription, and the "
                + $"budget is one: an owner of the subscription grants reader to resource:{budgetId:N}. Until then "
                + "nothing is evaluated and nobody is told anything."
            );
        }

        var trailingFrom = now - ForecastWindow;
        var from = trailingFrom < periodStart ? trailingFrom : periodStart;

        var rated = await pricing.RateAsync(tenantId, spec.SubscriptionId, from, now);
        if (rated.TryGetError(out var rateError)) {
            return Result<(string, decimal, decimal)>.Failure(rateError);
        }

        var inScope = rated.GetValueOrThrow()
            .Where(x => spec.Scope == BudgetScope.Subscription
                || string.Equals(x.ResourceGroup, spec.ResourceGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currencies = inScope.Select(static x => x.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1) {
            return Result<(string, decimal, decimal)>.Failure(
                ErrorCode.Conflict,
                $"The usage this budget covers is priced in {string.Join(" and ", currencies)}; a budget has one currency."
            );
        }

        var currency = currencies.Count == 1
            ? currencies[0]
            : pricing.Sheet.Versions[^1].Meters.Values.Select(static x => x.Currency).FirstOrDefault() ?? Currencies.Euro;

        var actual = inScope.Where(x => x.WindowStart >= periodStart).Sum(static x => x.Amount);
        var trailing = inScope.Where(x => x.WindowStart >= trailingFrom).Sum(static x => x.Amount);

        // ⚠ LINEAR, AND LABELLED AN ESTIMATE, BECAUSE docs/plan/22 ASKS FOR EXACTLY THAT: "a clever forecast
        // that is wrong is worse than a simple one that is honestly bounded." The trailing seven days'
        // spend per hour, times the hours left in the period, on top of what the period has cost. Early
        // in a period the trailing window reaches into the last one, which is the point: a budget on the
        // 2nd of the month forecasts from a week of history, not from one day.
        var hoursLeft = (decimal)Math.Max(0d, (periodEnd - now).TotalHours);
        var forecast = actual + trailing / (decimal)ForecastWindow.TotalHours * hoursLeft;

        return Result<(string, decimal, decimal)>.Success(
            (currency,
                MoneyRounding.Round(actual, currency).GetValueOrThrow(),
                MoneyRounding.Round(forecast, currency).GetValueOrThrow())
        );
    }

    /// <summary>Whether the budget itself — <c>resource:{budgetId:N}</c> — may read its subscription.</summary>
    /// <remarks>
    ///     ⚠ <b>Fully consistent, not the latency-first read a request path takes.</b> The check runs
    ///     once an hour, so its cost is nothing, and a cached answer is wrong in both directions that
    ///     matter here: a grant made after the first evaluation would be denied from the cache, and a
    ///     revoked grant would keep disclosing the subscription's spend until the entry expired.
    ///     <c>BudgetTests.ASubscriptionBudgetSeesNothingUntilItIsGrantedReaderOnTheSubscription</c> is the
    ///     first case — it failed on the cached deny before this read was changed.
    ///     <c>BudgetTests.ARevokedGrantTakesTheSubscriptionsFiguresWithIt</c> is the second, and
    ///     <see cref="EvaluateAsync" /> clears the figures the revoked grant had let it read.
    /// </remarks>
    async Task<bool> BudgetMayReadSubscriptionAsync(BudgetSpec spec) {
        var checkedRead = await grains
            .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICheckGrain>(
                GrainKeys.CheckCache(ObjectTypes.Subscription, spec.SubscriptionId.ToString("N", CultureInfo.InvariantCulture))
            )
            .CheckAsync(Permissions.Read, SubjectRef.Of(ObjectTypes.Resource, budgetId), Consistency.FullyConsistent);

        return checkedRead.TryGetValue(out var answer) && answer.Allowed;
    }

    /// <summary>The calendar period an instant falls in.</summary>
    /// <param name="period">Month, quarter or year.</param>
    /// <param name="instant">Any instant.</param>
    public static (DateTimeOffset Start, DateTimeOffset End) PeriodOf(BudgetPeriod period, DateTimeOffset instant) {
        var month = Rating.MonthOf(instant);

        return period switch {
            BudgetPeriod.Quarterly => Quarter(month),
            BudgetPeriod.Annually => (new(month.Year, 1, 1, 0, 0, 0, TimeSpan.Zero), new(month.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            _ => (month, month.AddMonths(1))
        };

        static (DateTimeOffset, DateTimeOffset) Quarter(DateTimeOffset month) {
            var start = new DateTimeOffset(month.Year, (month.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, TimeSpan.Zero);
            return (start, start.AddMonths(3));
        }
    }

    bool HasFired(DateTimeOffset periodStart, BudgetThreshold threshold) =>
        state.State.Alerts.Any(x => x.PeriodStart == periodStart && x.Kind == threshold.Kind && x.Percent == threshold.Percent);

    // ── The notification ─────────────────────────────────────────────────────────────────────────

    async Task<string> NotifyAsync(BudgetSpec spec, BudgetAlert alert, string currency, DateTimeOffset periodEnd) {
        var channel = ChannelOf(spec.Notification.Channel);
        if (channel == ChannelKind.Unknown) {
            return $"not sent: '{spec.Notification.Channel}' is not a channel the sending module has";
        }

        // ⚠ The tenant check a second time, here, because this is the one place that sends. The
        // reconciler refuses a path in another tenant (Budgets.ToSpec), and a spec that reached the grain
        // some other way would otherwise send through — and spend the limits of — another tenant's service.
        if (!ResourceId.TryParsePath(spec.Notification.ServicePath, out var service) || service.TenantId != tenantId) {
            return $"not sent: '{spec.Notification.ServicePath}' is not a sending service in this budget's tenant";
        }

        var serviceId = CommunicationGrainKeys.ResourceIdFor(service.TenantId, service.CanonicalPath);

        var body = Summary(spec, alert, currency, periodEnd);
        var outcomes = new StringBuilder();

        for (var i = 0; i < spec.Notification.Recipients.Length; i++) {
            var recipient = spec.Notification.Recipients[i];
            Result<MessageSnapshot> sent;

            try {
                sent = await sender.SendAsync(
                    tenantId,
                    new() {
                        ServiceId = serviceId,
                        Channel = channel,
                        Destination = recipient,
                        Body = body,
                        IdempotencyKey = string.Create(
                            CultureInfo.InvariantCulture,
                            $"budget-{budgetId:N}-{alert.PeriodStart:yyyyMMdd}-{alert.Kind}-{alert.Percent:0.####}-{i}"
                        )
                    }
                );
            } catch (Exception error) when (error is not OperationCanceledException) {
                sent = Result<MessageSnapshot>.Failure(
                    ErrorCode.InternalError,
                    $"the sending module did not answer ({error.GetType().Name}: {error.Message})"
                );
            }

            if (outcomes.Length > 0) {
                outcomes.Append("; ");
            }

            outcomes.Append(
                sent.TryGetValue(out var snapshot)
                    ? string.Create(CultureInfo.InvariantCulture, $"{recipient}: sent ({snapshot.Status})")
                    : string.Create(CultureInfo.InvariantCulture, $"{recipient}: refused — {sent.Error!.Message}")
            );
        }

        return outcomes.ToString();
    }

    /// <summary>The one line a notification carries.</summary>
    /// <param name="spec">The budget.</param>
    /// <param name="alert">The threshold that fired.</param>
    /// <param name="currency">The figures' currency.</param>
    /// <param name="periodEnd">The period's end.</param>
    public static string Summary(BudgetSpec spec, BudgetAlert alert, string currency, DateTimeOffset periodEnd) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(alert);

        var scope = spec.Scope == BudgetScope.Subscription
            ? string.Create(CultureInfo.InvariantCulture, $"subscription {spec.SubscriptionId:D}")
            : $"resource group {spec.ResourceGroup}";

        var figure = MoneyRounding.Format(alert.Figure, currency);
        var amount = MoneyRounding.Format(spec.Amount, currency);
        var percent = alert.Percent.ToString("0.####", CultureInfo.InvariantCulture);
        var period = string.Create(CultureInfo.InvariantCulture, $"{alert.PeriodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}");

        return alert.Kind == ThresholdKind.Forecast
            ? $"[budget] {spec.Name}: the forecast for {scope} is {figure}, {percent} % of the {amount} budget for {period}. "
            + "An estimate, linear on the trailing 7 days."
            : $"[budget] {spec.Name}: {scope} has cost {figure}, {percent} % of the {amount} budget for {period}.";
    }

    /// <summary>The sending module's channel for a body spelling, or <see cref="ChannelKind.Unknown" />.</summary>
    /// <param name="spelled">The body's word.</param>
    public static ChannelKind ChannelOf(string spelled) =>
        spelled switch {
            "sms" => ChannelKind.Sms,
            "whatsapp" => ChannelKind.WhatsApp,
            "email" => ChannelKind.Email,
            "push" => ChannelKind.Push,
            "voice" => ChannelKind.Voice,
            _ => ChannelKind.Unknown
        };

    // ── The clock ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Arms the reminder while an enabled budget is held, and disarms it otherwise.</summary>
    /// <remarks>
    ///     ⚠ Registered only when <c>GetReminder</c> answers null, for the reason
    ///     <c>AlertEvaluatorGrain</c> gives: re-registering on every upsert would push the tick out
    ///     by an hour per PUT.
    /// </remarks>
    async Task ArmOrDisarmAsync() {
        var wanted = state.State.Spec is { Enabled: true };

        try {
            var existing = await this.GetReminder(ReminderName);

            if (wanted && existing is null) {
                _ = await this.RegisterOrUpdateReminder(ReminderName, IBudgetGrain.Tick, IBudgetGrain.Tick);
            } else if (!wanted && existing is not null) {
                await this.UnregisterReminder(existing);
            }
        } catch (Exception error) when (error is not OperationCanceledException) {
            // The spec is held either way; the reconciler asks IsArmedAsync and re-drives until a row
            // exists — "held" and "armed" are two facts, as they are for the alert evaluator.
            logger.LogWarning(
                error,
                "Budget {Budget} in tenant {Tenant} could not arm or disarm its reminder",
                budgetId,
                tenantId
            );
        }
    }

    BudgetSnapshot Snapshot(BudgetSpec spec) =>
        new() {
            Spec = spec,
            Currency = state.State.Currency,
            PeriodStart = state.State.PeriodStart,
            PeriodEnd = state.State.PeriodEnd,
            Actual = state.State.Actual,
            Forecast = state.State.Forecast,
            LastEvaluatedAt = state.State.LastEvaluatedAt,
            LastError = state.State.LastError,
            Alerts = [.. state.State.Alerts]
        };
}
