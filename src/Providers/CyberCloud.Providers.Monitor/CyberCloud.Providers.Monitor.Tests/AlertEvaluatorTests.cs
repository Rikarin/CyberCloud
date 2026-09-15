using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The evaluator, ticked by hand against a real silo: a real rule converged through the real
///     reconciler, a scripted store, and the sending module wired as production wires it with an
///     in-memory carrier at the end. What the shared conformance suite cannot see, because it never
///     evaluates.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test owns a workspace, a sending service and a recipient address of its own.</b>
///         The carrier, the store substitute and the clock are static and shared by the collection,
///         so a count is only meaningful filtered to this test's destination, and a script is only
///         this test's if it is keyed on this test's workspace.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-tested.</b> <see cref="AStoreThatDoesNotAnswerLeavesAFiringRuleFiringAndSendsNothing" />
///         went red with <c>AlertEvaluation.Decide</c>'s failure branch changed to fall through to the
///         "not met" arm — the rule resolved and a resolve email went out — and
///         <see cref="ASuppressedRecipientIsRefusedByNameAndTheOtherIsStillTold" /> is what would go
///         red if the evaluator ever reached a carrier by any route but <c>IMessageSender</c>.
///     </para>
/// </remarks>
[Collection(AlertSilo.Name)]
public sealed class AlertEvaluatorTests(AlertTestCluster cluster) {
    static CancellationToken Ct => AlertTestCluster.Ct;

    /// <summary>The messages the email carrier was handed for one destination.</summary>
    static IReadOnlyList<OutboundMessage> SentTo(string destination) =>
        [.. Carriers.Email.Sent.Where(x => string.Equals(x.Destination, destination, StringComparison.OrdinalIgnoreCase))];

    static string Body(string service, string recipient, double threshold = 5, int forSeconds = 0, bool enabled = true) =>
        MonitorAlertRules.Body(service, [recipient], threshold: threshold, forSeconds: forSeconds, enabled: enabled);

    [Fact]
    public async Task ARealRuleFiresThroughTheCommunicationServiceAndResolves() {
        var service = await cluster.SendingServiceAsync("alerts-fire");
        var rule = AlertTestCluster.Rule("ws-fire", "errors-high");
        var workspace = MonitorAlertRules.WorkspaceOf(rule).CanonicalPath;
        const string Recipient = "fire-oncall@example.com";
        var body = Body(service, Recipient);

        await cluster.ConvergedAsync(rule, body);

        var held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Ok);
        held.Spec.Name.ShouldBe("errors-high");
        held.Spec.Workspace.ShouldBe("ws-fire");
        held.Spec.ActionGroup.ServiceId.ShouldBe(AlertTestCluster.ServiceIdOf(service));

        // ── The condition holds: one evaluation, one fire, one email ─────────────────────────
        AlertTestCluster.Queries.Answer(workspace, 9);

        var fired = await cluster.EvaluateAsync(rule);
        fired.Evaluated.ShouldBe(1);
        fired.Fired.ShouldBe(1);
        fired.Resolved.ShouldBe(0);
        fired.Errors.ShouldBe(0);

        held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Firing);
        held.LastValue.ShouldBe(9);
        held.Instances.Length.ShouldBe(1);
        held.Instances[0].IsOpen.ShouldBeTrue();
        held.Instances[0].Value.ShouldBe(9);
        held.Instances[0].FireNotification.ShouldContain($"{Recipient}: sent");

        var sent = SentTo(Recipient);
        sent.Count.ShouldBe(1, "one recipient, one fire, one email");
        sent[0].Body.ShouldContain("FIRING");
        sent[0].Body.ShouldContain("errors-high");
        sent[0].Body.ShouldContain("ws-fire");
        sent[0].Body.ShouldContain("9 > 5");

        // ⚠ The query the store was asked names THIS workspace and THIS tenant — the seam is
        // what a real VictoriaMetrics resolves an accountID from, and a query carrying the wrong
        // one would read another tenant's metrics.
        var asked = AlertTestCluster.Queries.Asked.Last(x => x.WorkspacePath == workspace);
        asked.TenantId.ShouldBe(AlertTestCluster.Tenant);
        asked.Signal.ShouldBe(AlertSignal.Metrics);
        asked.Lookback.ShouldBe(TimeSpan.FromSeconds(MonitorAlertRules.DefaultLookbackSeconds));

        // ── Not due yet: the same tick a minute early evaluates nothing ──────────────────────
        var early = await cluster.EvaluateAsync(rule);
        early.Evaluated.ShouldBe(0);
        early.Skipped.ShouldBe(1);
        SentTo(Recipient).Count.ShouldBe(1, "a rule that is not due is not re-evaluated, and a firing rule is not re-notified");

        // ── The condition stops holding: resolve, and say so ─────────────────────────────────
        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        AlertTestCluster.Queries.Answer(workspace, 1);

        var resolved = await cluster.EvaluateAsync(rule);
        resolved.Evaluated.ShouldBe(1);
        resolved.Resolved.ShouldBe(1);
        resolved.Fired.ShouldBe(0);

        held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Ok);
        held.Instances.Length.ShouldBe(1);
        held.Instances[0].IsOpen.ShouldBeFalse();
        held.Instances[0].ResolvedAt.ShouldBe(AlertTestCluster.Clock.UtcNow);
        held.Instances[0].ResolveNotification.ShouldContain($"{Recipient}: sent");

        sent = SentTo(Recipient);
        sent.Count.ShouldBe(2);
        sent[1].Body.ShouldContain("RESOLVED");

        // ── And the collection reads back through the action, in the shape the schema declares ──
        var listed = await cluster.ListInstancesAsync(rule, body);
        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);

        using var response = JsonDocument.Parse(listed.GetValueOrThrow());
        var valid = MonitorAlertRules.ListInstancesResponse.Validate(response.RootElement);
        valid.IsSuccess.ShouldBeTrue(valid.Error?.Message);

        response.RootElement.GetProperty("count").GetInt32().ShouldBe(1);
        response.RootElement.GetProperty("open").GetInt32().ShouldBe(0);
        response.RootElement.GetProperty("state").GetString().ShouldBe("ok");

        var line = response.RootElement.GetProperty("instances")[0].GetString()!;
        line.ShouldStartWith("resolved warning fired ");
        line.ShouldContain("value 9:");
        line.ShouldContain($"fire {Recipient}: sent");
        line.ShouldContain($"resolve {Recipient}: sent");
    }

    [Fact]
    public async Task ASuppressedRecipientIsRefusedByNameAndTheOtherIsStillTold() {
        var service = await cluster.SendingServiceAsync("alerts-suppressed");
        var rule = AlertTestCluster.Rule("ws-suppressed", "disk-full");
        var workspace = MonitorAlertRules.WorkspaceOf(rule).CanonicalPath;
        const string Told = "suppressed-a@example.com";
        const string Blocked = "suppressed-b@example.com";

        // ⚠ The complaint is on the SERVICE's list, placed the way a carrier's receipt places one.
        // The evaluator never sees the list; IMessageSender refuses before a carrier is resolved,
        // and the refusal is what the instance records.
        var suppressed = await cluster.Communication.SuppressAsync(
            AlertTestCluster.Tenant,
            AlertTestCluster.ServiceIdOf(service),
            ChannelKind.Email,
            Blocked,
            SuppressionReason.Complaint,
            "marked our alert as spam",
            Guid.Empty,
            Ct
        );

        suppressed.IsSuccess.ShouldBeTrue(suppressed.Error?.Message);

        await cluster.ConvergedAsync(rule, MonitorAlertRules.Body(service, [Told, Blocked], threshold: 90));
        AlertTestCluster.Queries.Answer(workspace, 97);

        var report = await cluster.EvaluateAsync(rule);
        report.Fired.ShouldBe(1);

        var held = await cluster.HeldAsync(rule);
        var outcome = held.Instances.Single().FireNotification;
        outcome.ShouldContain($"{Told}: sent");
        outcome.ShouldContain($"{Blocked}: refused");
        outcome.ShouldContain("suppression list");

        SentTo(Told).Count.ShouldBe(1);
        SentTo(Blocked).ShouldBeEmpty("a complaint reached the carrier — the send path's suppression check was bypassed");
    }

    [Fact]
    public async Task AForKeepsARuleQuietUntilTheConditionHasHeld() {
        var service = await cluster.SendingServiceAsync("alerts-for");
        var rule = AlertTestCluster.Rule("ws-for", "latency-high");
        var workspace = MonitorAlertRules.WorkspaceOf(rule).CanonicalPath;
        const string Recipient = "for-oncall@example.com";

        await cluster.ConvergedAsync(rule, Body(service, Recipient, threshold: 250, forSeconds: 120));
        AlertTestCluster.Queries.Answer(workspace, 400);

        var first = await cluster.EvaluateAsync(rule);
        first.Evaluated.ShouldBe(1);
        first.Fired.ShouldBe(0);

        var held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Pending);
        held.PendingSince.ShouldBe(AlertTestCluster.Clock.UtcNow);
        SentTo(Recipient).ShouldBeEmpty("a pending rule paged somebody");

        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        (await cluster.EvaluateAsync(rule)).Fired.ShouldBe(0);
        (await cluster.HeldAsync(rule)).State.ShouldBe(AlertRuleState.Pending);
        SentTo(Recipient).ShouldBeEmpty();

        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        (await cluster.EvaluateAsync(rule)).Fired.ShouldBe(1);
        (await cluster.HeldAsync(rule)).State.ShouldBe(AlertRuleState.Firing);
        SentTo(Recipient).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AStoreThatDoesNotAnswerLeavesAFiringRuleFiringAndSendsNothing() {
        var service = await cluster.SendingServiceAsync("alerts-down");
        var rule = AlertTestCluster.Rule("ws-down", "queue-deep");
        var workspace = MonitorAlertRules.WorkspaceOf(rule).CanonicalPath;
        const string Recipient = "down-oncall@example.com";
        var body = Body(service, Recipient, threshold: 100);

        await cluster.ConvergedAsync(rule, body);
        AlertTestCluster.Queries.Answer(workspace, 500);
        (await cluster.EvaluateAsync(rule)).Fired.ShouldBe(1);
        SentTo(Recipient).Count.ShouldBe(1);

        // ── The store goes away ──────────────────────────────────────────────────────────────
        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        AlertTestCluster.Queries.Fail(workspace, "vmselect: connection refused");

        var report = await cluster.EvaluateAsync(rule);
        report.Evaluated.ShouldBe(1);
        report.Errors.ShouldBe(1);
        report.Resolved.ShouldBe(0, "a store that did not answer resolved an alert");
        report.Fired.ShouldBe(0);

        var held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Firing);
        held.LastError.ShouldContain("connection refused");
        held.LastValue.ShouldBeNull();
        held.Instances.Single().IsOpen.ShouldBeTrue();
        SentTo(Recipient).Count.ShouldBe(1, "a resolve was sent for a store that did not answer");

        var observed = await cluster.ObserveAsync(rule, body);
        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("could not run");
        observed.Summary.ShouldContain("connection refused");

        // ── And it comes back: the very next answer resolves, once ───────────────────────────
        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        AlertTestCluster.Queries.Answer(workspace, 3);
        (await cluster.EvaluateAsync(rule)).Resolved.ShouldBe(1);
        (await cluster.HeldAsync(rule)).LastError.ShouldBeEmpty();
        SentTo(Recipient).Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheReminderIsArmedWhileAnEnabledRuleExistsAndDisarmedWhenTheLastIsGone() {
        var service = await cluster.SendingServiceAsync("alerts-armed");
        var rule = AlertTestCluster.Rule("ws-armed", "cpu-high");
        var evaluator = MonitorAlertRules.EvaluatorIdFor(rule);
        const string Recipient = "armed-oncall@example.com";

        async Task<bool> ArmedAsync() {
            var armed = await cluster.Alerts.IsArmedAsync(AlertTestCluster.Tenant, evaluator, Ct);
            armed.IsSuccess.ShouldBeTrue(armed.Error?.Message);
            return armed.GetValueOrThrow();
        }

        (await ArmedAsync()).ShouldBeFalse("an evaluator with no rules is ticking");

        await cluster.ConvergedAsync(rule, Body(service, Recipient));
        (await ArmedAsync()).ShouldBeTrue("a workspace with an enabled rule has no reminder, so nothing will ever evaluate it");

        // Disabled: kept, and the clock stops.
        await cluster.ConvergedAsync(rule, Body(service, Recipient, enabled: false));
        (await ArmedAsync()).ShouldBeFalse("a workspace whose only rule is disabled is still ticking");
        (await cluster.EvaluateAsync(rule)).Skipped.ShouldBe(1);

        await cluster.ConvergedAsync(rule, Body(service, Recipient));
        (await ArmedAsync()).ShouldBeTrue();

        // Deleted: gone, read back as gone, and the reminder with it.
        var deleted = await cluster.DeleteAsync(rule, Body(service, Recipient));
        deleted.IsConverged.ShouldBeTrue(deleted.ToString());

        var read = await cluster.Alerts.GetRuleAsync(AlertTestCluster.Tenant, evaluator, rule.Id, Ct);
        read.IsSuccess.ShouldBeFalse();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await ArmedAsync()).ShouldBeFalse("the last rule is gone and the evaluator is still ticking over nothing, forever");

        // A second delete is a no-op that converges — the verb grammar's retry-safety.
        (await cluster.DeleteAsync(rule, Body(service, Recipient))).IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task AWorkspaceCarriesAtMostMaxRulesAndTheFiftyFirstFailsWithoutRetrying() {
        var service = await cluster.SendingServiceAsync("alerts-cap");
        var evaluator = MonitorAlertRules.EvaluatorIdFor(AlertTestCluster.Rule("ws-cap", "any"));

        for (var i = 0; i < IAlertEvaluatorGrain.MaxRules; i++) {
            var id = AlertTestCluster.Rule("ws-cap", $"rule-{i}");
            using var desired = JsonDocument.Parse(Body(service, "cap@example.com"));
            var spec = MonitorAlertRules.ToSpec(id, desired.RootElement).GetValueOrThrow();

            var written = await cluster.Alerts.UpsertRuleAsync(AlertTestCluster.Tenant, evaluator, spec, Ct);
            written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        }

        var outcome = await cluster.ReconcileAsync(AlertTestCluster.Rule("ws-cap", "one-too-many"), Body(service, "cap@example.com"));

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("a full workspace is not a condition ten seconds fixes, and an hour of retries against it is what the default would do");
        outcome.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        outcome.Error.Message.ShouldContain(IAlertEvaluatorGrain.MaxRules.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var listed = await cluster.Alerts.ListRulesAsync(AlertTestCluster.Tenant, evaluator, Ct);
        listed.GetValueOrThrow().Length.ShouldBe(IAlertEvaluatorGrain.MaxRules);
    }

    [Fact]
    public async Task ARuleNamingAnotherTenantsServiceIsRefusedAtThePointerAndNothingIsHeld() {
        var rule = AlertTestCluster.Rule("ws-cross", "sneaky");
        var body = Body(AlertTestCluster.ServicePath("their-service", AlertTestCluster.OtherTenant), "cross@example.com");

        // ⚠ The schema accepts it — the path parses — so this is the reconciler's refusal, and it
        // has to be, because ResourceSchema.Validate does not know whose tenant a body is for.
        using var desired = JsonDocument.Parse(body);
        MonitorAlertRules.Schema2026.Validate(desired.RootElement).IsSuccess.ShouldBeTrue();

        var outcome = await cluster.ReconcileAsync(rule, body);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe("/properties/actionGroup/service");
        outcome.Error.Message.ShouldContain(AlertTestCluster.OtherTenant.D());

        var read = await cluster.Alerts.GetRuleAsync(AlertTestCluster.Tenant, MonitorAlertRules.EvaluatorIdFor(rule), rule.Id, Ct);
        read.IsSuccess.ShouldBeFalse("a rule that was refused was written anyway");
    }

    [Fact]
    public async Task AChangedConditionResolvesTheOpenInstanceWithoutANotification() {
        var service = await cluster.SendingServiceAsync("alerts-edit");
        var rule = AlertTestCluster.Rule("ws-edit", "memory-high");
        var workspace = MonitorAlertRules.WorkspaceOf(rule).CanonicalPath;
        const string Recipient = "edit-oncall@example.com";

        await cluster.ConvergedAsync(rule, Body(service, Recipient, threshold: 80));
        AlertTestCluster.Queries.Answer(workspace, 95);
        (await cluster.EvaluateAsync(rule)).Fired.ShouldBe(1);
        SentTo(Recipient).Count.ShouldBe(1);

        // The tenant raises the threshold above the value: the instance the old condition fired
        // cannot be resolved by evaluating the new one, so the edit closes it, quietly.
        await cluster.ConvergedAsync(rule, Body(service, Recipient, threshold: 99));

        var held = await cluster.HeldAsync(rule);
        held.State.ShouldBe(AlertRuleState.Ok);
        held.Spec.Condition.Threshold.ShouldBe(99);
        held.Instances.Single().IsOpen.ShouldBeFalse();
        held.Instances.Single().ResolveNotification.ShouldContain("condition was replaced");
        SentTo(Recipient).Count.ShouldBe(1, "an edit sent a resolve notification for a condition the tenant deleted");

        // The next evaluation is a fresh decision under the new condition.
        AlertTestCluster.Clock.Advance(TimeSpan.FromMinutes(1));
        (await cluster.EvaluateAsync(rule)).Fired.ShouldBe(0);
        (await cluster.HeldAsync(rule)).State.ShouldBe(AlertRuleState.Ok);
    }

    [Fact]
    public async Task ObserveReportsWhatIsHeldAndNoticesDrift() {
        var service = await cluster.SendingServiceAsync("alerts-observe");
        var rule = AlertTestCluster.Rule("ws-observe", "observed");
        var body = Body(service, "observe@example.com");

        var before = await cluster.ObserveAsync(rule, body);
        before.Exists.ShouldBeFalse();

        await cluster.ConvergedAsync(rule, body);

        var after = await cluster.ObserveAsync(rule, body);
        after.Exists.ShouldBeTrue();
        after.Summary.ShouldBe("the rule is held and is ok");

        var json = JsonNode.Parse(after.Json)!.AsObject();
        json["state"]!.GetValue<string>().ShouldBe("ok");
        json["enabled"]!.GetValue<bool>().ShouldBeTrue();
        json["instances"]!.GetValue<int>().ShouldBe(0);

        var drifted = await cluster.ObserveAsync(rule, Body(service, "observe@example.com", threshold: 6));
        drifted.Exists.ShouldBeTrue();
        drifted.Summary.ShouldBe("the rule has drifted from its body");

        // And a second identical pass is the no-op clause 1 promises.
        var again = await cluster.ReconcileAsync(rule, body);
        again.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public void EveryRuleUnderOneWorkspaceDerivesOneEvaluatorAndAnotherWorkspaceDoesNot() {
        var workspace = AlertTestCluster.Workspace("ws-shared");
        var first = AlertTestCluster.Rule("ws-shared", "a");
        var second = AlertTestCluster.Rule("ws-shared", "b");

        MonitorAlertRules.EvaluatorIdFor(first).ShouldBe(MonitorAlertRules.EvaluatorIdFor(workspace));
        MonitorAlertRules.EvaluatorIdFor(second).ShouldBe(MonitorAlertRules.EvaluatorIdFor(workspace));

        MonitorAlertRules.EvaluatorIdFor(AlertTestCluster.Rule("ws-other", "a")).ShouldNotBe(MonitorAlertRules.EvaluatorIdFor(first));
        MonitorAlertRules.EvaluatorIdFor(AlertTestCluster.Rule("ws-shared", "a", AlertTestCluster.OtherTenant))
            .ShouldNotBe(MonitorAlertRules.EvaluatorIdFor(first), "two tenants' workspaces of one name share an evaluator");

        // Case-insensitive on the type, as CanonicalPath is — one spelling, one grain.
        var upper = first with { Type = new("CYBERCLOUD.MONITOR", "WORKSPACES/ALERTRULES") };
        MonitorAlertRules.EvaluatorIdFor(upper).ShouldBe(MonitorAlertRules.EvaluatorIdFor(first));
    }
}
