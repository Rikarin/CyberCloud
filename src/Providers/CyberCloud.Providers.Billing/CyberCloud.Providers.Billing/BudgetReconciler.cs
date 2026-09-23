using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Billing;

/// <summary>
///     Converges one <c>CyberCloud.Billing/budgets</c> resource onto its budget grain.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, over a grain rather than a cluster —
///         the shape <c>MonitorAlertRuleReconciler</c> established: idempotent (an upsert of the same
///         spec changes nothing the grain keeps), no hidden state, observes rather than assumes (a
///         second read after the write, and the reminder asked for), and <c>Converged</c> only after
///         both.
///     </para>
///     <para>
///         ⚠ <b>Held and armed are two facts, and an enabled budget needs both.</b> The grain writes
///         its spec and then registers its reminder — two stores — so a reminder-table fault between
///         them leaves a budget that reads back as desired and that nothing will ever evaluate. The
///         second question is asked on both passes, as the alert rule's reconciler asks it.
///     </para>
///     <para>
///         ⚠ <b>The sending service is checked for shape and tenant here and for existence at the
///         send</b>, for the reason the alert rule's reconciler gives: a budget created a minute before
///         its service is the order a tenant scripting both will use.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The budget grains, reached with the tenant qualification written once.</param>
public sealed class BudgetReconciler(IClock clock, IBudgetControlPlane plane) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => Budgets.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(ReconcileContext context, CancellationToken cancellationToken = default) {
        var wanted = Budgets.ToSpec(context.Id, context.Desired);
        if (wanted.TryGetError(out var invalid)) {
            return ReconcileOutcome.FromFailure(invalid);
        }

        var spec = wanted.GetValueOrThrow();
        var tenantId = context.Id.TenantId;

        var held = await plane.GetAsync(tenantId, spec.BudgetId, cancellationToken);

        if (held.TryGetValue(out var existing)) {
            if (existing.Spec.SameAs(spec)) {
                var armed = await ArmedAsync(tenantId, spec, cancellationToken);
                if (armed.TryGetError(out var armError)) {
                    return ReconcileOutcome.FromFailure(armError);
                }

                if (armed.GetValueOrThrow()) {
                    context.Log.Report("ready", $"budget '{context.Id.Name}' already carries the desired spec", 100);
                    return ReconcileOutcome.Converged;
                }

                context.Log.Report("configuring", $"budget '{context.Id.Name}' is held and not armed; re-arming", 20);
            }
        } else if (held.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(held.Error);
        }

        context.Log.Report("configuring", $"writing budget '{context.Id.Name}'", 40);

        var written = await plane.UpsertAsync(tenantId, spec, cancellationToken);
        if (written.TryGetError(out var writeError)) {
            return ReconcileOutcome.FromFailure(writeError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.GetAsync(tenantId, spec.BudgetId, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress("the budget was written and does not read back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!read.GetValueOrThrow().Spec.SameAs(spec)) {
            return ReconcileOutcome.InProgress("the budget reads back and does not yet carry the desired spec", TimeSpan.FromSeconds(5));
        }

        var ticking = await ArmedAsync(tenantId, spec, cancellationToken);
        if (ticking.TryGetError(out var tickError)) {
            return ReconcileOutcome.FromFailure(tickError);
        }

        if (!ticking.GetValueOrThrow()) {
            return ReconcileOutcome.InProgress("the budget reads back as desired and its reminder is not armed yet", TimeSpan.FromSeconds(5));
        }

        context.Log.Report(
            "ready",
            spec.Enabled
                ? $"budget '{context.Id.Name}' reads back as desired and is evaluated hourly"
                : $"budget '{context.Id.Name}' reads back as desired and is disabled",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(ReconcileContext context, CancellationToken cancellationToken = default) {
        var tenantId = context.Id.TenantId;

        context.Log.Report("removing", $"removing budget '{context.Id.Name}'");

        var removed = await plane.RemoveAsync(tenantId, context.Id.Id, cancellationToken);
        if (removed.TryGetError(out var removeError) && removeError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(removeError);
        }

        var read = await plane.GetAsync(tenantId, context.Id.Id, cancellationToken);
        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"budget '{context.Id.Name}' still reads back", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("removed", $"budget '{context.Id.Name}' is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The figures are the budget's status</b> — actual, forecast and the alerts fired — so
    ///     whoever may read the budget reads them. For a resource-group budget that is the group's own
    ///     cost; for a subscription budget the figures exist only once the budget was granted reader
    ///     on the subscription, which is the decision that makes showing them here acceptable.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) {
        var read = await plane.GetAsync(context.Id.TenantId, context.Id.Id, cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the budget is not held" };
        }

        var held = read.GetValueOrThrow();
        var matches = Budgets.Matches(held, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["enabled"] = held.Spec.Enabled,
                ["period"] = Budgets.Spell(held.Spec.Period),
                ["currency"] = held.Currency,
                ["periodStart"] = held.LastEvaluatedAt is null ? null : Stamp(held.PeriodStart),
                ["periodEnd"] = held.LastEvaluatedAt is null ? null : Stamp(held.PeriodEnd),
                ["actual"] = held.Actual,
                ["forecast"] = held.Forecast,
                ["lastEvaluatedAt"] = held.LastEvaluatedAt is { } at ? Stamp(at) : null,
                ["lastError"] = held.LastError,
                ["alerts"] = held.Alerts.IsDefault ? 0 : held.Alerts.Length
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = !matches ? "the budget has drifted from its body"
                : held.LastError.Length > 0 ? "the budget is held and its last evaluation could not run: " + held.LastError
                : "the budget is held"
        };
    }

    async Task<Result<bool>> ArmedAsync(Guid tenantId, BudgetSpec spec, CancellationToken cancellationToken) =>
        spec.Enabled ? await plane.IsArmedAsync(tenantId, spec.BudgetId, cancellationToken) : Result<bool>.Success(true);

    static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
}
