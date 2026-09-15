using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Converges one <c>workspaces/alertRules</c> resource onto one rule on its workspace's
///     <see cref="IAlertEvaluatorGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, over a grain rather than a cluster:
///         idempotent (an upsert of the same spec is a no-op the grain reports as such), no hidden
///         state (everything is in <c>ReconcileContext</c> and the two constructor seams), observes
///         and never assumes (a second read after the write, compared with
///         <see cref="MonitorAlertRules.Matches" />), and <c>Converged</c> follows that read.
///     </para>
///     <para>
///         ⚠ <b>The first reconciler in this family with a <see langword="null" />
///         <c>ReconcileContext.Cluster</c>, next to one that cannot work without it.</b> The
///         workspace applies three objects into the cluster its body names; a rule under it applies
///         nothing and never reads the connection. The registration says so —
///         <c>MonitorProvider</c> declares no <c>RequiresCluster</c> on this type — and the
///         conformance suite reads it through an <c>IConvergedModule</c> for that reason.
///     </para>
///     <para>
///         ⚠ <b>The action group's service is checked for shape and tenant here, and for
///         existence at delivery.</b> <see cref="MonitorAlertRules.ToSpec" /> refuses a path that
///         is not this tenant's <c>CyberCloud.Communication/services</c>; whether that service has
///         been created and has the channel configured is a fact the sending module owns and
///         answers when a notification is sent, per recipient, on the instance. A reconciler that
///         asked the service grain first would refuse a rule created a minute before its service —
///         the order a tenant scripting both will use — and would still be wrong the day the service
///         is deleted with the rule left standing.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
/// <param name="plane">The evaluator, reached with the tenant qualification written once.</param>
public sealed class MonitorAlertRuleReconciler(IClock clock, IAlertControlPlane plane) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorAlertRules.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var wanted = MonitorAlertRules.ToSpec(context.Id, context.Desired);
        if (wanted.TryGetError(out var invalid)) {
            return ReconcileOutcome.FromFailure(invalid);
        }

        var spec = wanted.GetValueOrThrow();
        var evaluatorId = MonitorAlertRules.EvaluatorIdFor(context.Id);
        var tenantId = context.Id.TenantId;

        var held = await plane.GetRuleAsync(tenantId, evaluatorId, context.Id.Id, cancellationToken);

        if (held.TryGetValue(out var existing)) {
            if (existing.Spec.SameAs(spec)) {
                context.Log.Report("ready", $"rule '{context.Id.Name}' already carries the desired condition and action group", 100);
                return ReconcileOutcome.Converged;
            }
        } else if (held.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(held.Error);
        }

        context.Log.Report("configuring", $"writing rule '{context.Id.Name}' onto workspace '{spec.Workspace}'", 40);

        var written = await plane.UpsertRuleAsync(tenantId, evaluatorId, spec, cancellationToken);
        if (written.TryGetError(out var writeError)) {
            // ⚠ The rule cap is a refusal a retry cannot fix, and a retry every ten seconds for an
            // hour against a full workspace is what FromFailure's default would do.
            return writeError.Code == ErrorCode.QuotaExceeded
                ? ReconcileOutcome.Failed(writeError)
                : ReconcileOutcome.FromFailure(writeError);
        }

        // ── Clause 4. ───────────────────────────────────────────────────────────────────────────
        var read = await plane.GetRuleAsync(tenantId, evaluatorId, context.Id.Id, cancellationToken);
        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress("the rule was written and does not read back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        if (!read.GetValueOrThrow().Spec.SameAs(spec)) {
            return ReconcileOutcome.InProgress("the rule reads back and does not yet carry the desired spec", TimeSpan.FromSeconds(5));
        }

        context.Log.Report(
            "ready",
            spec.Enabled
                ? $"rule '{context.Id.Name}' reads back as desired and is evaluated every {spec.Interval.TotalSeconds:0} s"
                : $"rule '{context.Id.Name}' reads back as desired and is disabled",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var evaluatorId = MonitorAlertRules.EvaluatorIdFor(context.Id);
        var tenantId = context.Id.TenantId;

        context.Log.Report("removing", $"removing rule '{context.Id.Name}' and its history");

        var removed = await plane.RemoveRuleAsync(tenantId, evaluatorId, context.Id.Id, cancellationToken);
        if (removed.TryGetError(out var removeError) && removeError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(removeError);
        }

        var read = await plane.GetRuleAsync(tenantId, evaluatorId, context.Id.Id, cancellationToken);
        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"rule '{context.Id.Name}' still reads back", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("removed", $"rule '{context.Id.Name}' is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var evaluatorId = MonitorAlertRules.EvaluatorIdFor(context.Id);
        var read = await plane.GetRuleAsync(context.Id.TenantId, evaluatorId, context.Id.Id, cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the rule is not on its workspace's evaluator" };
        }

        var held = read.GetValueOrThrow();
        var matches = MonitorAlertRules.Matches(held, context.Id, context.Desired);
        var open = held.Instances.IsDefault ? 0 : held.Instances.Count(x => x.IsOpen);

        return new() {
            Exists = true,
            Json = new JsonObject {
                ["state"] = MonitorAlertRules.Spell(held.State),
                ["enabled"] = held.Spec.Enabled,
                ["severity"] = MonitorAlertRules.Spell(held.Spec.Severity),
                ["lastEvaluatedAt"] = held.LastEvaluatedAt is { } at ? MonitorAlertRules.Stamp(at) : null,
                ["lastValue"] = held.LastValue,
                ["lastError"] = held.LastError,
                ["nextDueAt"] = held.NextDueAt is { } due ? MonitorAlertRules.Stamp(due) : null,
                ["instances"] = held.Instances.IsDefault ? 0 : held.Instances.Length,
                ["open"] = open
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Summary = !matches ? "the rule has drifted from its body"
                : held.LastError.Length > 0 ? "the rule is held and its last evaluation could not run: " + held.LastError
                : $"the rule is held and is {MonitorAlertRules.Spell(held.State)}"
        };
    }
}
