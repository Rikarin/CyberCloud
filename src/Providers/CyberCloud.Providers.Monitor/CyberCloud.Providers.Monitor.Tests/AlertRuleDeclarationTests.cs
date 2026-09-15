using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.ResourceManager.Registry;
using Orleans.Concurrency;
using Orleans.Configuration;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The alert-rule declaration, checked the way a silo checks it at start, plus the body-to-spec
///     refusals the schema cannot make and the facts that live in more than one file.
/// </summary>
public sealed class AlertRuleDeclarationTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    static ResourceId Rule(string workspace = "prod", string name = "errors-high", Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, "rg", MonitorAlertRules.Type, name, Guid.NewGuid(), workspace);

    static string ServicePath(Guid? tenant = null, string ns = "CyberCloud.Communication", string type = "services") =>
        new ResourceId(tenant ?? Tenant, Subscription, "rg", new(ns, type), "alerts", Guid.Empty).Path;

    static string Body(string? service = null, params string[] recipients) =>
        MonitorAlertRules.Body(service ?? ServicePath(), recipients.Length == 0 ? ["oncall@example.com"] : [.. recipients]);

    static Result<AlertRuleSpec> Spec(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return MonitorAlertRules.ToSpec(id, desired.RootElement);
    }

    [Fact]
    public void TheTypeIsAClusterlessChartlessChildOfAClusterBackedParent() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);

        registry.TryGetType(MonitorAlertRules.Type, out var rules).ShouldBeTrue();
        registry.TryGetType(MonitorWorkspaces.Type, out var workspaces).ShouldBeTrue();

        // ⚠ THE SHAPE NOTHING IN THE TREE HAD: the parent applies objects into a cluster and the
        // child applies nothing anywhere. Both halves are asserted so that a later "tidy" that
        // copies the parent's RequiresCluster onto the child fails here, with the reason —
        // MonitorProvider's declaration comment says what that copy would break.
        workspaces.RequiresCluster.ShouldBeTrue();
        rules.RequiresCluster.ShouldBeFalse("an alert rule reads no cluster connection; declaring one makes the driver refuse every pass");
        rules.ClusterIdPointer.ShouldBeEmpty();
        rules.Chart.ShouldBeEmpty("an alert rule renders no Helm chart");
        rules.SoftDeleteDays.ShouldBe(0);
        rules.SupportsTags.ShouldBeTrue();
        rules.ReconcilerType.ShouldBe(typeof(MonitorAlertRuleReconciler));
        MonitorAlertRules.Type.Depth.ShouldBe(2);
    }

    [Fact]
    public void ListInstancesIsSynchronousWithAHandlerThatServesTheTypeAndTheAction() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorAlertRules.Type, out var registration).ShouldBeTrue();

        var action = registration.Actions.Single(x => x.Name == MonitorAlertRules.ListInstancesAction);
        action.LongRunning.ShouldBeFalse();
        action.Secret.ShouldBeFalse("an alert's history carries no credential");
        action.HandlerType.ShouldBe(typeof(MonitorAlertRuleListInstancesHandler));
        action.Response.ShouldNotBeNull();
        action.Permission.ShouldBe(registration.ReadPermission, "reading the history is reading the rule");

        var handler = new MonitorAlertRuleListInstancesHandler(new NoPlane());
        handler.Type.ShouldBe(MonitorAlertRules.Type);
        handler.Action.ShouldBe(MonitorAlertRules.ListInstancesAction);
    }

    [Fact]
    public void TheBodyHelperSatisfiesTheSchemaAndEveryPointerIsCamelCase() {
        using var desired = JsonDocument.Parse(Body());

        var valid = MonitorAlertRules.Schema2026.Validate(desired.RootElement);
        valid.IsSuccess.ShouldBeTrue(valid.Error?.Message);

        foreach (var pointer in MonitorAlertRules.Pointers2026) {
            foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
                char.IsLower(segment[0]).ShouldBeTrue($"'{pointer}' is not camelCase");
            }
        }

        // ⚠ THE FIRST PROPERTY IN THE CATALOGUE WITH THIS FORMAT — asserted so the first is also
        // known to be checked, not merely printed. A path that is not a resource id is refused at
        // the API, and the pointer is the one the portal highlights.
        MonitorAlertRules.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/actionGroup/service")
            .Format.ShouldBe(SchemaFormat.ResourceId);

        var garbage = JsonNode.Parse(Body())!.AsObject();
        garbage["properties"]!["actionGroup"]!["service"] = "not-a-path";
        using var broken = JsonDocument.Parse(garbage.ToJsonString());

        var refused = MonitorAlertRules.Schema2026.Validate(broken.RootElement);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Target.ShouldBe("/properties/actionGroup/service");
    }

    [Fact]
    public void TheThreeMandatoryLimitsAreInTheSchemaOrTheGrainAndNotInProse() {
        // docs/plan/16 § Alerts: "Query cost limits, a per-workspace concurrent-evaluation cap and a
        // max look-back are mandatory from day one, not hardening added later." Each is a number
        // the API or the grain enforces, and this is where the numbers are pinned to the sentence.
        var lookback = MonitorAlertRules.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/condition/lookbackSeconds");
        lookback.Maximum.ShouldBe(MonitorAlertRules.MaxLookbackSeconds);
        lookback.Maximum.ShouldBe(86_400, "a day");

        var query = MonitorAlertRules.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/condition/query");
        query.MaxLength.ShouldBe(MonitorAlertRules.MaxQueryLength);

        var interval = MonitorAlertRules.Schema2026.Properties.Single(x => x.JsonPointer == "/properties/evaluation/intervalSeconds");
        interval.Minimum.ShouldBe(IAlertEvaluatorGrain.Tick.TotalSeconds, "a rule cannot ask to be evaluated more often than the reminder ticks");

        IAlertEvaluatorGrain.MaxRules.ShouldBeGreaterThan(0);
        IAlertEvaluatorGrain.QueryTimeout.ShouldBeLessThan(IAlertEvaluatorGrain.Tick, "a query that outlives the tick queues the next tick behind it");

        // ⚠ THE PASS, NOT ONE QUERY (2026-09-15, #32 review). The line above reasoned about one
        // query and the activation runs a whole pass in one turn, so every GetRuleAsync and
        // listInstances queued behind it waits for the pass — and the tree configures no
        // ResponseTimeout, so Orleans' default is what they wait against. The budget bounds when the
        // last query may START; that query then runs to its own timeout; the sum is the longest a
        // pass holds the activation, and it has to clear the default with the sends still to come.
        // The default is read off Orleans' own options rather than written as 30, so a version
        // that changes it changes this test.
        var responseTimeout = new SiloMessagingOptions().ResponseTimeout;
        responseTimeout.ShouldBe(TimeSpan.FromSeconds(30), "the arithmetic in IAlertEvaluatorGrain's remarks was done against 30 s");
        (IAlertEvaluatorGrain.PassBudget + IAlertEvaluatorGrain.QueryTimeout).ShouldBeLessThan(
            responseTimeout,
            "a full pass outlives Orleans' response timeout, and every call queued behind it throws into the reconcile loop"
        );
        IAlertEvaluatorGrain.PassBudget.ShouldBeLessThan(IAlertEvaluatorGrain.Tick, "a pass that outlives the tick queues the next tick behind it");

        // And the reads are what a reconcile pass and an action call first, so they interleave.
        foreach (var name in new[] { nameof(IAlertEvaluatorGrain.GetRuleAsync), nameof(IAlertEvaluatorGrain.ListRulesAsync), nameof(IAlertEvaluatorGrain.IsArmedAsync) }) {
            typeof(IAlertEvaluatorGrain).GetMethod(name)!
                .GetCustomAttributes(typeof(AlwaysInterleaveAttribute), false)
                .ShouldNotBeEmpty($"{name} queues behind an evaluation pass");
        }
    }

    [Fact]
    public void ASpecIsDerivedFromABodyAndTheServiceIdIsTheSendingModulesOwnDerivation() {
        var id = Rule();
        var spec = Spec(id, Body()).GetValueOrThrow();

        spec.RuleId.ShouldBe(id.Id);
        spec.Name.ShouldBe("errors-high");
        spec.Workspace.ShouldBe("prod");
        spec.WorkspacePath.ShouldBe(MonitorAlertRules.WorkspaceOf(id).CanonicalPath);
        spec.Severity.ShouldBe(AlertSeverity.Warning);
        spec.Condition.Signal.ShouldBe(AlertSignal.Metrics);
        spec.Condition.Operator.ShouldBe(AlertOperator.GreaterThan);
        spec.Condition.Threshold.ShouldBe(5);
        spec.Condition.Lookback.ShouldBe(TimeSpan.FromSeconds(MonitorAlertRules.DefaultLookbackSeconds));
        spec.Interval.ShouldBe(TimeSpan.FromSeconds(MonitorAlertRules.DefaultIntervalSeconds));
        spec.For.ShouldBe(TimeSpan.Zero);
        spec.ActionGroup.Channel.ShouldBe(ChannelKind.Email);
        spec.ActionGroup.Recipients.ShouldBe(["oncall@example.com"]);

        // ⚠ THE JOIN. The service's grain id is what CommunicationGrainKeys derives from the same
        // path, so the evaluator's SendRequest lands on the grain the tenant's Communication
        // resources converged — with no index read and no reference to the other provider.
        ResourceId.TryParsePath(ServicePath(), out var service).ShouldBeTrue();
        spec.ActionGroup.ServiceId.ShouldBe(CommunicationGrainKeys.ResourceIdFor(Tenant, service.CanonicalPath));

        // And two spellings of the service's type derive one id — CanonicalPath, not Path.
        var shouted = Spec(id, Body(ServicePath(ns: "CYBERCLOUD.COMMUNICATION", type: "SERVICES"))).GetValueOrThrow();
        shouted.ActionGroup.ServiceId.ShouldBe(spec.ActionGroup.ServiceId);
    }

    [Fact]
    public void AnActionGroupNamingAnotherTenantsServiceIsRefusedAtThePointer() {
        var refused = Spec(Rule(), Body(ServicePath(tenant: Guid.Parse("22222222-2222-4222-8222-222222222222"))));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Target.ShouldBe("/properties/actionGroup/service");
        refused.Error.Message.ShouldContain("own tenant");
    }

    [Fact]
    public void AnActionGroupNamingSomethingOtherThanASendingServiceIsRefusedAtThePointer() {
        var refused = Spec(Rule(), Body(ServicePath(ns: "CyberCloud.Storage", type: "accounts")));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Target.ShouldBe("/properties/actionGroup/service");
        refused.Error.Message.ShouldContain("CyberCloud.Communication/services");
    }

    [Fact]
    public void AnActionGroupWithNoUsableRecipientIsRefusedAtThePointer() {
        var refused = Spec(Rule(), Body(null, "  ", ""));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Target.ShouldBe("/properties/actionGroup/recipients");

        var tooMany = Spec(Rule(), Body(null, [.. Enumerable.Range(0, MonitorAlertRules.MaxRecipients + 1).Select(i => $"r{i}@example.com")]));
        tooMany.IsSuccess.ShouldBeFalse();
        tooMany.Error!.Target.ShouldBe("/properties/actionGroup/recipients");
    }

    [Fact]
    public void AnIntervalThatIsNotAMultipleOfTheTickIsRefusedAtThePointer() {
        // ⚠ The schema says "a multiple of 60" and cannot enforce it — SchemaProperty has no
        // multiple-of — so 90 passes Validate. Left alone, the evaluator would have run it every
        // 120 seconds and the observed state would have called it 90.
        var ninety = MonitorAlertRules.Body(ServicePath(), ["oncall@example.com"], intervalSeconds: 90);
        using var valid = JsonDocument.Parse(ninety);
        MonitorAlertRules.Schema2026.Validate(valid.RootElement).IsSuccess.ShouldBeTrue("the schema can refuse it after all, so ToSpec's refusal is redundant");

        var refused = Spec(Rule(), ninety);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Target.ShouldBe("/properties/evaluation/intervalSeconds");
        refused.Error.Message.ShouldContain("90");

        Spec(Rule(), MonitorAlertRules.Body(ServicePath(), ["oncall@example.com"], intervalSeconds: 300)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void MatchesIsTheSpecComparisonAndSeesEveryMemberThatMatters() {
        var id = Rule();
        var body = Body();
        var spec = Spec(id, body).GetValueOrThrow();
        var held = new AlertRuleSnapshot { Spec = spec, State = AlertRuleState.Firing };

        using var same = JsonDocument.Parse(body);
        MonitorAlertRules.Matches(held, id, same.RootElement).ShouldBeTrue("state and history are not part of the comparison");

        using var threshold = JsonDocument.Parse(MonitorAlertRules.Body(ServicePath(), ["oncall@example.com"], threshold: 6));
        MonitorAlertRules.Matches(held, id, threshold.RootElement).ShouldBeFalse();

        using var recipient = JsonDocument.Parse(MonitorAlertRules.Body(ServicePath(), ["other@example.com"]));
        MonitorAlertRules.Matches(held, id, recipient.RootElement).ShouldBeFalse();

        using var disabled = JsonDocument.Parse(MonitorAlertRules.Body(ServicePath(), ["oncall@example.com"], enabled: false));
        MonitorAlertRules.Matches(held, id, disabled.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void EveryVocabularyWordRoundTripsThroughItsSpelling() {
        foreach (var word in MonitorAlertRules.SeverityValues) {
            MonitorAlertRules.Spell(MonitorAlertRules.ParseSeverity(word)).ShouldBe(word);
        }

        foreach (var word in MonitorAlertRules.OperatorValues) {
            MonitorAlertRules.Spell(MonitorAlertRules.ParseOperator(word)).ShouldBe(word);
        }

        foreach (var word in MonitorAlertRules.SignalValues) {
            MonitorAlertRules.Spell(MonitorAlertRules.ParseSignal(word)).ShouldBe(word);
        }

        // ⚠ The five channel words are the sending module's, spelled here because rule 2 forbids
        // binding its constants. Every ChannelKind but Unknown must have a word, or a channel the
        // sending module serves is one no rule can name.
        foreach (var kind in Enum.GetValues<ChannelKind>().Where(x => x != ChannelKind.Unknown)) {
            var word = MonitorAlertRules.Spell(kind);
            MonitorAlertRules.ChannelValues.ShouldContain(word);
            MonitorAlertRules.ParseChannel(word).ShouldBe(kind);
        }
    }

    [Fact]
    public void TheInstanceLineAndTheSummaryReadAsOneSentenceEach() {
        var at = new DateTimeOffset(2026, 9, 15, 14, 22, 9, TimeSpan.Zero);
        var spec = Spec(Rule(), Body()).GetValueOrThrow();

        MonitorAlertRules.Summary(spec, 7.25, at, fired: true)
            .ShouldBe("[warning] errors-high on workspace prod FIRING at 2026-09-15T14:22:09.0000000+00:00: max(rate(http_requests_errors_total[5m])) = 7.25 > 5");

        var instance = new AlertInstance {
            InstanceId = Guid.NewGuid(),
            RuleId = spec.RuleId,
            Severity = AlertSeverity.Critical,
            FiredAt = at,
            Value = 7.25,
            Summary = "s",
            FireNotification = "a@example.com: sent (Dispatched)"
        };

        MonitorAlertRules.InstanceLine(instance)
            .ShouldBe("firing critical fired 2026-09-15T14:22:09.0000000+00:00 resolved - value 7.25: s | fire a@example.com: sent (Dispatched)");

        var json = JsonNode.Parse(MonitorAlertRules.InstancesJson(new() { Spec = spec, State = AlertRuleState.Firing, Instances = [instance] }))!;
        json["count"]!.GetValue<int>().ShouldBe(1);
        json["open"]!.GetValue<int>().ShouldBe(1);
        json["state"]!.GetValue<string>().ShouldBe("firing");
    }

    [Fact]
    public void TheEvaluatorIdIsAVersionEightGuidAndNeverEmpty() {
        var id = MonitorAlertRules.EvaluatorIdFor(Rule());

        id.ShouldNotBe(Guid.Empty);
        (id.ToString("N")[12]).ShouldBe('8', "RFC 9562's custom version, which is what a SHA-256 name-based id honestly is");
    }

    /// <summary>A control plane that must never be asked — the handler's construction needs one, and only that.</summary>
    sealed class NoPlane : IAlertControlPlane {
        public Task<Result<AlertRuleSnapshot>> UpsertRuleAsync(Guid tenantId, Guid evaluatorId, AlertRuleSpec spec, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RemoveRuleAsync(Guid tenantId, Guid evaluatorId, Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<AlertRuleSnapshot>> GetRuleAsync(Guid tenantId, Guid evaluatorId, Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<ImmutableArray<AlertRuleSnapshot>>> ListRulesAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<AlertEvaluationReport>> EvaluateAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<bool>> IsArmedAsync(Guid tenantId, Guid evaluatorId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
