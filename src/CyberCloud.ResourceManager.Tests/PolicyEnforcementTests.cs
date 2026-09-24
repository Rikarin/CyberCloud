using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Contracts.Generation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Step 5 with the real engine behind it: definitions and assignments written through
///     <see cref="PolicyManagerService" /> into the tenant's <see cref="IPolicyCatalogGrain" />, and
///     every write kind evaluated by <see cref="CatalogPolicyEvaluator" /> — docs/plan/08 § Policy,
///     issue #46.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real grains for everything this issue is about</b> — the catalog, the resource, the
///         index, the quota, the subscription and the management groups the walk reads. The
///         authorization seam and the relation writer stay doubled: whether a policy decides is the
///         question here, and a ReBAC refusal would stop the request two steps earlier. Who may write
///         a policy is <c>test/CyberCloud.Isolation</c>'s <c>PolicyIsolationTests</c>, against the real
///         schema.
///     </para>
///     <para>
///         ⚠ <b>Its own subscription and a resource group per test</b>, because an assignment reaches
///         everything beneath it: an assignment left at a shared scope would refuse other classes'
///         writes the day they run through this engine. The one test that needs the fixture's shared
///         scope — the isolation test, which needs the same subscription GUID in two tenants — removes
///         what it assigned before it returns.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class PolicyEnforcementTests(ResourceManagerCluster cluster) : IAsyncLifetime {
    /// <summary>This class's subscription, so its assignments reach nothing another class writes.</summary>
    static readonly Guid PolicySubscription = Guid.Parse("46464646-4646-4646-8646-464646464646");

    ResourceManagerService manager = null!;
    PolicyManagerService policies = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var tenant = cluster.For(ResourceManagerCluster.Tenant);

        (await tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(PolicySubscription)).CreateAsync("policy"))
            .IsSuccess.ShouldBeTrue();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, PolicySubscription);
        foreach (var meter in Enum.GetValues<QuotaMeter>().Where(static x => x != QuotaMeter.Unknown)) {
            (await quota.SetLimitAsync(meter, 1_000_000m)).IsSuccess.ShouldBeTrue();
        }

        var handlers = new ServiceCollection();
        handlers.AddSingleton<RestartHandler>();
        handlers.AddSingleton<ListKeysHandler>();

        manager = new(
            cluster.Registry,
            new SwitchableAuthorizer(),
            new RecordingRelationWriter(),
            new SwitchableLockResolver(),
            new CatalogPolicyEvaluator(cluster.Grains, NullLogger<CatalogPolicyEvaluator>.Instance),
            new RecordingChangeSink(),
            cluster.Grains,
            new ActionDispatcher(handlers.BuildServiceProvider(), new NoClusterConnectionFactory(), new UnavailableSecretResolver()),
            NullLogger<ResourceManagerService>.Instance
        );

        policies = new(new SwitchableScopeAuthorizer(), cluster.Grains, NullLogger<PolicyManagerService>.Instance);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── A provider cannot skip it ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryWriteKindEntersStepFiveAndADenyStopsEachOne() {
        // ⚠ THE PROPERTY THE ISSUE CALLS LOAD-BEARING: "in the write path", for every write kind. Five
        // shapes — a PUT that creates, a PUT that updates, a PATCH, a DELETE and an action — each
        // refused by an assignment whose rule names that request's operation, each refused before
        // anything below step 5 moved, and each let through with WriteStep.Policy in its trace once the
        // assignment goes. A provider is never reached before step 5: the reconciler runs from the
        // operation step 10 starts, and the action's handler after the fork this step precedes.
        ResourceManagerCluster.ResetDoubles();
        RestartHandler.Reset();

        var group = await GroupAsync("kinds");
        var address = Widget(group, "kinds");

        // ── create ──
        await AssignOperationDenyAsync(group, "create");
        var refusedCreate = await PutAsync(address);
        refusedCreate.Error!.Code.ShouldBe(ErrorCode.PolicyViolation, "a create is refused by a rule about creates");
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free, "no name was claimed");
        await UnassignAsync(group, "deny-create");

        var created = await PutAsync(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
        created.GetValueOrThrow().Trace.IsCanonicalPrefix().ShouldBeTrue(created.GetValueOrThrow().Trace.ToString());
        await ConvergeAsync(created.GetValueOrThrow());

        // ── update, by PUT and by PATCH ──
        await AssignOperationDenyAsync(group, "update");
        (await PutAsync(address, TestingProvider.Body(3))).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        (await PatchAsync(address, """{"properties":{"label":"patched"}}""")).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        await UnassignAsync(group, "deny-update");

        var patched = await PatchAsync(address, """{"properties":{"label":"patched"}}""");
        patched.IsSuccess.ShouldBeTrue(patched.Error?.Message);
        patched.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
        await ConvergeAsync(patched.GetValueOrThrow());

        // ── action ──
        await AssignOperationDenyAsync(group, "action");
        (await ActionAsync(address, "restart")).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        RestartHandler.Invocations.ShouldBe(0, "a refused action never reached its handler");
        await UnassignAsync(group, "deny-action");

        var restarted = await ActionAsync(address, "restart");
        restarted.IsSuccess.ShouldBeTrue(restarted.Error?.Message);
        restarted.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
        RestartHandler.Invocations.ShouldBe(1);

        // ── delete ──
        await AssignOperationDenyAsync(group, "delete");
        (await DeleteAsync(address)).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State
            .ShouldBe(IndexEntryState.Confirmed, "a refused delete released no name");
        await UnassignAsync(group, "deny-delete");

        var deleted = await DeleteAsync(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        deleted.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);

        foreach (var trace in new[] { created, patched, restarted, deleted }.Select(static x => x.GetValueOrThrow().Trace)) {
            for (var i = 1; i < trace.Reached.Length; i++) {
                ((int)trace.Reached[i]).ShouldBeGreaterThan((int)trace.Reached[i - 1], trace.ToString());
            }
        }
    }

    [Fact]
    public async Task AShapeRuleDoesNotMakeTheResourcesThatBreakItUndeletable() {
        // ⚠ PolicyRule.AppliesTo's argument, through the write path: "deny label legacy" refuses the
        // write that would make one, and lets the delete of one that already exists through.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("shape");
        var address = Widget(group, "legacy");

        var created = await PutAsync(address, TestingProvider.Body(2, "legacy"));
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await ConvergeAsync(created.GetValueOrThrow());

        await DefineAsync(group, "no-legacy", """{ "if": { "field": "/properties/label", "equals": "legacy" }, "then": { "effect": "deny" } }""");
        await AssignAsync(group, "no-legacy", "no-legacy");

        (await PutAsync(address, TestingProvider.Body(3, "legacy"))).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);

        var deleted = await DeleteAsync(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        deleted.GetValueOrThrow().Trace.Policy.ShouldBeEmpty("a shape rule is not evaluated for a delete");
    }

    // ── Deny ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADenyIsAStructuredErrorNamingTheAssignmentAndTheDefinitionAndTargetingTheField() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("named");
        var definition = await DefineAsync(
            group,
            "no-banned-labels",
            """{ "if": { "field": "/properties/label", "in": [ "banned", "forbidden" ] }, "then": { "effect": "deny" } }"""
        );
        var assignment = await AssignAsync(group, "labels", "no-banned-labels");

        var refused = await PutAsync(Widget(group, "w"), TestingProvider.Body(2, "Forbidden"));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Target.ShouldBe("/properties/label", "the refusal points at the field the rule tests");
        refused.Error.Message.ShouldContain(assignment);
        refused.Error.Message.ShouldContain(definition);
        refused.Error.Details.Select(static x => x.Message).ShouldBe(
            ["policyAssignmentId: " + assignment, "policyDefinitionId: " + definition]
        );
    }

    [Fact]
    public async Task APatchIsJudgedOnTheBodyItLeavesNotOnThePatchAlone() {
        // ⚠ A merge patch omits what it does not change. A rule about /properties/size has to see the
        // stored size under a PATCH that only touches the label, or "PATCH { label }" would walk any
        // resource past every rule about the fields it did not repeat.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("patch");
        var address = Widget(group, "p");

        var created = await PutAsync(address, TestingProvider.Body(7));
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await ConvergeAsync(created.GetValueOrThrow());

        await DefineAsync(group, "no-seven", """{ "if": { "field": "/properties/size", "equals": 7 }, "then": { "effect": "deny" } }""");
        await AssignAsync(group, "no-seven", "no-seven");

        var refused = await PatchAsync(address, """{"properties":{"label":"only-the-label"}}""");
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation, "the stored size is seven, and the patch leaves it seven");

        var allowed = await PatchAsync(address, """{"properties":{"size":8}}""");
        allowed.IsSuccess.ShouldBeTrue(allowed.Error?.Message);
    }

    [Fact]
    public async Task APatchThatLeavesOutTheTagsKeepsTheTagsTheRuleWasJudgedOn() {
        // ⚠ FOUND BY THE REVIEW OF #46, AS A BYPASS. Step 5 judged a PATCH on the stored superset with
        // the stored tags put back, and step 9 then sent the tag bag of the PATCH ALONE — empty, for a
        // patch that doesn't mention tags — which the resource grain stored in place of the old one. So
        // "deny when /tags/env doesn't exist" refused a PUT without the tag and let a PATCH through
        // that stripped it: the body judged was not the body the write left. The write now sends the
        // tag bag of the merged body the rule judged, and a merge patch that omits `tags` leaves them.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("tagged");
        var address = Widget(group, "t");

        var created = await PutAsync(address, Tagged(TestingProvider.Body(2), ("env", "prod"), ("team", "a")));
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await ConvergeAsync(created.GetValueOrThrow());

        await DefineAsync(group, "needs-env", """{ "if": { "field": "/tags/env", "exists": false }, "then": { "effect": "deny" } }""");
        await AssignAsync(group, "needs-env", "needs-env");

        (await PutAsync(address, TestingProvider.Body(2))).Error!.Code
            .ShouldBe(ErrorCode.PolicyViolation, "a PUT without the tag leaves no tag");

        var patched = await PatchAsync(address, """{"properties":{"label":"z"}}""");
        patched.IsSuccess.ShouldBeTrue(patched.Error?.Message);
        patched.GetValueOrThrow().Resource.Tags.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + x.Value)
            .ShouldBe(["env=prod", "team=a"], "a patch that leaves out `tags` leaves them as they were");
        await ConvergeAsync(patched.GetValueOrThrow());

        // A merge patch merges the bag, as RFC 7386 merges any object: a member adds or replaces one
        // tag and leaves the rest. ⚠ Step 2 refuses a null here as it refuses one for every property,
        // so a tag is removed by a PUT — which the rule judges like any other.
        var merged = await PatchAsync(address, """{"tags":{"owner":"b","team":"c"}}""");
        merged.IsSuccess.ShouldBeTrue(merged.Error?.Message);
        merged.GetValueOrThrow().Resource.Tags.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + x.Value)
            .ShouldBe(["env=prod", "owner=b", "team=c"]);
        await ConvergeAsync(merged.GetValueOrThrow());

        (await PutAsync(address, Tagged(TestingProvider.Body(2), ("owner", "b")))).Error!.Code
            .ShouldBe(ErrorCode.PolicyViolation, "a PUT that drops the tag is refused");

        var read = await cluster.Resource(ResourceManagerCluster.Tenant, created.GetValueOrThrow().Resource.Id).GetAsync(string.Empty, []);
        read.GetValueOrThrow().Tags.ShouldContainKeyAndValue("env", "prod", "nothing refused changed the bag");
    }

    [Fact]
    public async Task TheManagersRenderingIsTheDocumentsSchema() {
        // ⚠ The review of #46 put policy's addresses into the generated document, and every SDK now
        // reads a definition and an assignment by the schema there. A member the manager renders that
        // the schema doesn't name is one a generated client refuses (additionalProperties: false); a
        // required member it doesn't render is one a generated client can't construct. So the member
        // sets are held against the real manager's output, not against a copy of it.
        ResourceManagerCluster.ResetDoubles();

        var document = OpenApiEmitter.Emit(cluster.Registry, ApiVersion.Parse(TestingProvider.V2026));
        var group = await GroupAsync("rendered");
        var definitionPath = await DefineAsync(group, "rendered", """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "audit" } }""");
        var assignmentPath = await AssignAsync(group, "rendered", "rendered");

        var definition = await policies.ReadAsync(Request(definitionPath), TestContext.Current.CancellationToken);
        var assignment = await policies.ReadAsync(Request(assignmentPath), TestContext.Current.CancellationToken);

        foreach (var (snapshot, component) in new[] {
                     (definition.GetValueOrThrow(), OpenApiEmitter.PolicyDefinitionSchema),
                     (assignment.GetValueOrThrow(), OpenApiEmitter.PolicyAssignmentSchema)
                 }) {
            var schema = document["components"]!["schemas"]![component]!["properties"]!["properties"]!;
            var declared = schema["properties"]!.AsObject().Select(static x => x.Key).Order(StringComparer.Ordinal);
            var required = schema["required"]!.AsArray().Select(static x => x!.GetValue<string>()).Order(StringComparer.Ordinal);
            var rendered = JsonNode.Parse(snapshot.Properties)!.AsObject().Select(static x => x.Key).Order(StringComparer.Ordinal);

            rendered.ShouldBe(declared, component + ": the manager renders exactly what the document declares");
            rendered.ShouldBe(required, component + ": and every member, every time, as the document requires");
        }
    }

    // ── Modify ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AModifyRewritesTheStoredBodyAndTheTraceShowsWhatItWrote() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("modify");
        await DefineAsync(
            group,
            "defaults",
            """
            { "if": { "field": "type", "like": "CyberCloud.Testing/*" },
              "then": { "effect": "modify", "operations": [
                { "operation": "add", "field": "/tags/costCenter", "value": "unassigned" },
                { "operation": "replace", "field": "/properties/label", "value": "enforced" } ] } }
            """
        );
        var assignment = await AssignAsync(group, "defaults", "defaults");

        var accepted = await PutAsync(Widget(group, "m"), TestingProvider.Body(2, "asked-for"));
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        // The trace says what step 5 did, inside step 5's span.
        var entry = accepted.GetValueOrThrow().Trace.Policy.ShouldHaveSingleItem();
        entry.AssignmentPath.ShouldBe(assignment);
        entry.Effect.ShouldBe("modify");
        entry.Matched.ShouldBeTrue();
        entry.Applied.ShouldBe(["add /tags/costCenter = \"unassigned\"", "replace /properties/label = \"enforced\""]);

        // And the resource is what the policy made of the request, not what the request said.
        var stored = accepted.GetValueOrThrow().Resource;
        JsonNode.Parse(stored.Body)!["properties"]!["label"]!.GetValue<string>().ShouldBe("enforced");
        stored.Tags["costCenter"].ShouldBe("unassigned");
    }

    [Fact]
    public async Task AModifyIsValidatedAgainstTheSchemaBeforeAnythingBelowStepFiveSeesIt() {
        // ⚠ A policy engine whose output skipped the schema would be a way past it. The rewritten body
        // is validated at the request's api-version, and a rewrite the schema refuses is the write's
        // refusal — before quota, before the name.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("invalid");
        await DefineAsync(
            group,
            "stringly",
            """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify", "operations": [ { "operation": "replace", "field": "/properties/size", "value": "big" } ] } }"""
        );
        await AssignAsync(group, "stringly", "stringly");

        var address = Widget(group, "i");
        var refused = await PutAsync(address);

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Target.ShouldBe("/properties/size");
        refused.Error.Message.ShouldContain("schema refuses");
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    [Fact]
    public async Task AModifyOfASecretPropertyRefusesTheWriteRatherThanDisagreeWithItsTrace() {
        // ⚠ Found by the review of #46. The catalog rewrites a body with the secrets taken out, and the
        // write path rewrites the real one. Over a secret the caller sent, an `add` was traced as made
        // and did nothing, and a `replace` overwrote the caller's password with the definition's
        // constant. Neither is honest, so the write is refused, naming the property.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("secret-modify");
        await DefineAsync(
            group,
            "password",
            """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify", "operations": [ { "operation": "replace", "field": "/properties/adminPassword", "value": "hunter2" } ] } }"""
        );
        var assignment = await AssignAsync(group, "password", "password");

        var body = JsonNode.Parse(TestingProvider.Body())!.AsObject();
        body["properties"]!["adminPassword"] = "the-callers-own";
        var address = Widget(group, "s");

        var refused = await PutAsync(address, body.ToJsonString());

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Target.ShouldBe("/properties/adminPassword");
        refused.Error.Message.ShouldContain(assignment);
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    [Fact]
    public async Task ABodyWithAMemberNamedTwiceIsAFourHundredAtStepTwo() {
        // ⚠ Found by the review of #46: JsonDocument kept the last value and passed it, and step 5's
        // JsonNode copy threw on the duplicate — a 500 for a malformed request.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("duplicate");
        var refused = await PutAsync(
            Widget(group, "d"),
            """{"location":"eu-central","properties":{"size":2,"label":"a","label":"b"}}"""
        );

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task AModifyRunsBeforeADenySoAFixedBodyIsNotRefused() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("order");
        await DefineAsync(group, "no-legacy", """{ "if": { "field": "/properties/label", "equals": "legacy" }, "then": { "effect": "deny" } }""");
        await DefineAsync(
            group,
            "rename-legacy",
            """{ "if": { "field": "/properties/label", "equals": "legacy" }, "then": { "effect": "modify", "operations": [ { "operation": "replace", "field": "/properties/label", "value": "modern" } ] } }"""
        );
        await AssignAsync(group, "a-deny", "no-legacy");
        await AssignAsync(group, "b-modify", "rename-legacy");

        var accepted = await PutAsync(Widget(group, "o"), TestingProvider.Body(2, "legacy"));

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.GetValueOrThrow().Trace.Policy.Select(static x => (x.Effect, x.Matched))
            .ShouldBe([("modify", true), ("deny", false)], "modify first, then deny against what modify left");
    }

    // ── Audit ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAuditRecordsComplianceQueryableAtTheGroupAndTheSubscriptionAndADeleteForgetsIt() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("audit");
        await DefineAsync(group, "labelled", """{ "if": { "field": "/properties/label", "equals": "unlabelled" }, "then": { "effect": "audit" } }""");
        var assignment = await AssignAsync(group, "labelled", "labelled");
        var address = Widget(group, "a");

        var first = await PutAsync(address, TestingProvider.Body(2, "unlabelled"));
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        await ConvergeAsync(first.GetValueOrThrow());

        var atGroup = await StatesAsync(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group));
        var state = atGroup.ShouldHaveSingleItem();
        state.ResourcePath.ShouldBe(address.CanonicalPath);
        state.AssignmentPath.ShouldBe(assignment);
        state.State.ShouldBe(PolicyComplianceState.NonCompliant);

        (await StatesAsync(ScopeId.Subscription(ResourceManagerCluster.Tenant, PolicySubscription)))
            .ShouldContain(x => x.ResourcePath == address.CanonicalPath, "a subscription's states hold its groups'");

        var fixedUp = await PutAsync(address, TestingProvider.Body(2, "labelled"));
        fixedUp.IsSuccess.ShouldBeTrue(fixedUp.Error?.Message);
        await ConvergeAsync(fixedUp.GetValueOrThrow());

        (await StatesAsync(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group)))
            .ShouldHaveSingleItem().State.ShouldBe(PolicyComplianceState.Compliant);

        (await DeleteAsync(address)).IsSuccess.ShouldBeTrue();
        (await StatesAsync(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group)))
            .ShouldBeEmpty("a resource whose delete was accepted is no longer being judged");
    }

    [Fact]
    public async Task AVerdictEvaluatedBeforeItsAssignmentOrRuleChangedIsNotRecordedAfterIt() {
        // ⚠ Evaluation (step 5) and recording (after step 9) are two turns of the catalog, and a policy
        // write can land between them. The review of issue #46 found such a verdict written back; each
        // one now carries the versions it was judged under, and RecordStatesAsync drops a stale one.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("stale");
        var scope = ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group);
        const string Rule = """{ "if": { "field": "/properties/label", "equals": "unlabelled" }, "then": { "effect": "audit" } }""";
        await DefineAsync(group, "stale-audit", Rule);
        await AssignAsync(group, "stale-audit", "stale-audit");

        var address = Widget(group, "v");
        var catalog = Catalog();

        // ── The rule changed between evaluation and recording ──
        var before = await EvaluateDirectlyAsync(address);
        before.ShouldHaveSingleItem().State.ShouldBe(PolicyComplianceState.NonCompliant);
        await DefineAsync(group, "stale-audit", Rule.Replace("unlabelled", "blank", StringComparison.Ordinal));

        (await catalog.RecordStatesAsync(address.CanonicalPath, before)).IsSuccess.ShouldBeTrue();
        (await StatesAsync(scope)).ShouldBeEmpty("a verdict about the old rule was recorded after the rule changed");

        // ── The assignment went between evaluation and recording ──
        var current = await EvaluateDirectlyAsync(address);
        await UnassignAsync(group, "stale-audit");

        (await catalog.RecordStatesAsync(address.CanonicalPath, current)).IsSuccess.ShouldBeTrue();
        (await StatesAsync(scope)).ShouldBeEmpty("a verdict of a deleted assignment was recorded after it went");

        // ── And a verdict about what is in force is recorded, so the check isn't dropping everything ──
        await AssignAsync(group, "stale-audit", "stale-audit");
        var fresh = await EvaluateDirectlyAsync(address);
        (await catalog.RecordStatesAsync(address.CanonicalPath, fresh)).IsSuccess.ShouldBeTrue();
        (await StatesAsync(scope)).ShouldHaveSingleItem().ResourcePath.ShouldBe(address.CanonicalPath);

        await UnassignAsync(group, "stale-audit");
    }

    [Fact]
    public async Task ForgettingADeletedScopeTakesItsPolicyAndEveryAssignmentOfItsDefinitionsAndNothingElse() {
        // ⚠ The catalog half of the scope delete — ScopeManagerService calls it beside the tuple sweep,
        // and ManagementGroupTests in test/CyberCloud.Isolation drives that through the real scope
        // manager. Here: exactly what goes, including an assignment elsewhere of a definition the
        // deleted group held (a subscription that moved out of the group after assigning it).
        ResourceManagerCluster.ResetDoubles();

        var catalog = Catalog();
        var gone = ScopeId.ManagementGroupOf(ResourceManagerCluster.Tenant, "policy-forgotten");
        var subscription = ScopeId.Subscription(ResourceManagerCluster.Tenant, PolicySubscription);
        const string Audit = """{ "if": { "field": "/properties/label", "equals": "unlabelled" }, "then": { "effect": "audit" } }""";

        var groupDefinition = PolicyAddress.Definition(gone, "forgotten").Path;
        (await catalog.PutDefinitionAsync(new() { Path = groupDefinition, Rule = Audit })).IsSuccess.ShouldBeTrue();
        (await catalog.PutAssignmentAsync(new() { Path = PolicyAddress.Assignment(gone, "at-group").Path, DefinitionPath = groupDefinition }))
            .IsSuccess.ShouldBeTrue();

        var movedOut = PolicyAddress.Assignment(subscription, "moved-out").Path;
        (await catalog.PutAssignmentAsync(new() { Path = movedOut, DefinitionPath = groupDefinition })).IsSuccess.ShouldBeTrue();

        // A subscription definition assigned at the subscription and at a resource group, with a verdict.
        var kept = await DefineAsync("kept", "kept", Audit);
        var keptAtSubscription = PolicyAddress.Assignment(subscription, "kept").Path;
        (await catalog.PutAssignmentAsync(new() { Path = keptAtSubscription, DefinitionPath = kept })).IsSuccess.ShouldBeTrue();

        var group = await GroupAsync("forgotten-rg");
        var atGroup = await AssignAsync(group, "kept", "kept");
        var address = Widget(group, "f");
        (await catalog.RecordStatesAsync(address.CanonicalPath, await EvaluateDirectlyAsync(address))).IsSuccess.ShouldBeTrue();

        try {
            // ── The management group ──
            (await catalog.ForgetScopeAsync(gone.Path)).IsSuccess.ShouldBeTrue();

            (await catalog.GetDefinitionAsync(groupDefinition)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
            (await catalog.ListAssignmentsAsync(gone.Path)).GetValueOrThrow().ShouldBeEmpty();
            (await catalog.GetAssignmentAsync(movedOut)).Error!.Code.ShouldBe(
                ErrorCode.ResourceNotFound,
                "an assignment of a deleted group's definition outlived it, and would name a definition that no longer exists"
            );
            (await catalog.GetAssignmentAsync(keptAtSubscription)).IsSuccess.ShouldBeTrue("an unrelated assignment went with the group");
            (await catalog.ForgetScopeAsync(gone.Path)).IsSuccess.ShouldBeTrue("a re-driven delete forgets again");

            // ── The resource group ──
            var groupScope = ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group);
            (await StatesAsync(groupScope)).ShouldNotBeEmpty();
            (await catalog.ForgetScopeAsync(groupScope.Path)).IsSuccess.ShouldBeTrue();

            (await catalog.GetAssignmentAsync(atGroup)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
            (await StatesAsync(groupScope)).ShouldBeEmpty("the verdicts beneath a deleted group outlived it");
            (await catalog.GetDefinitionAsync(kept)).IsSuccess.ShouldBeTrue("the subscription's definition is not the group's to take");
            (await catalog.GetAssignmentAsync(keptAtSubscription)).IsSuccess.ShouldBeTrue();

            // ── Refused by name: a subscription, whose delete isn't built, and another tenant's group ──
            (await catalog.ForgetScopeAsync(subscription.Path)).Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
            (await catalog.ForgetScopeAsync(ScopeId.ManagementGroupOf(ResourceManagerCluster.OtherTenant, "policy-forgotten").Path))
                .Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        } finally {
            await catalog.DeleteAssignmentAsync(keptAtSubscription);
            await catalog.DeleteAssignmentAsync(atGroup);
            await catalog.DeleteAssignmentAsync(movedOut);
            await catalog.DeleteDefinitionAsync(kept);
            await catalog.DeleteDefinitionAsync(groupDefinition);
        }
    }

    [Fact]
    public async Task ASecretPropertyIsNotVisibleToACondition() {
        // ⚠ An audit verdict is readable by anyone with read on the scope, one bit per rule, and the
        // rule is the scope owner's to write — so a condition over a password would be an oracle for
        // it. The secret is removed from the document the engine judges; to the rule it is absent.
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("secret");
        await DefineAsync(group, "password-set", """{ "if": { "field": "/properties/adminPassword", "exists": true }, "then": { "effect": "audit" } }""");
        await AssignAsync(group, "password-set", "password-set");

        var body = """{"location":"eu-central","properties":{"size":2,"label":"x","adminPassword":"hunter2"}}""";
        var accepted = await PutAsync(Widget(group, "s"), body);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        accepted.GetValueOrThrow().Trace.Policy.ShouldHaveSingleItem().Matched.ShouldBeFalse();
    }

    // ── Inheritance, exclusion, isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task AnAssignmentAtAManagementGroupReachesEveryGroupBeneathItUnlessExcluded() {
        // ⚠ Two levels of management group above the subscription, and the assignment at the TOP one —
        // the walk that PolicyCatalogGrain makes only when the tenant has an assignment at a group.
        ResourceManagerCluster.ResetDoubles();

        var tenant = cluster.For(ResourceManagerCluster.Tenant);
        (await tenant.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("policy-root")).CreateAsync("root", "", 0)).IsSuccess.ShouldBeTrue();
        (await tenant.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("policy-leaf")).CreateAsync("leaf", "policy-root", 1)).IsSuccess.ShouldBeTrue();
        (await tenant.GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("policy-leaf")).AddSubscriptionAsync(PolicySubscription)).IsSuccess.ShouldBeTrue();

        var subscription = tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(PolicySubscription));
        (await subscription.SetManagementGroupAsync("policy-leaf")).IsSuccess.ShouldBeTrue();

        var root = ScopeId.ManagementGroupOf(ResourceManagerCluster.Tenant, "policy-root");
        var reached = await GroupAsync("inherited");
        var excluded = await GroupAsync("excluded");

        try {
            (await policies.PutAsync(
                    Request(
                        PolicyAddress.Definition(root, "no-writes").Path,
                        """{ "properties": { "policyRule": { "if": { "field": "type", "like": "*" }, "then": { "effect": "deny" } } } }"""
                    ),
                    TestContext.Current.CancellationToken
                ))
                .IsSuccess.ShouldBeTrue();

            var put = await policies.PutAsync(
                Request(
                    PolicyAddress.Assignment(root, "no-writes").Path,
                    $$"""
                    { "properties": {
                        "policyDefinitionId": "{{PolicyAddress.Definition(root, "no-writes").Path}}",
                        "notScopes": [ "{{ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, excluded).Path}}" ] } }
                    """
                ),
                TestContext.Current.CancellationToken
            );
            put.IsSuccess.ShouldBeTrue(put.Error?.Message);

            var refused = await PutAsync(Widget(reached, "r"));
            refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation, "the root group's assignment reaches two levels down");

            var allowed = await PutAsync(Widget(excluded, "e"));
            allowed.IsSuccess.ShouldBeTrue("an excluded group is not reached: " + allowed.Error?.Message);
            allowed.GetValueOrThrow().Trace.Policy.ShouldBeEmpty();

            // ⚠ A management group excluded from an assignment above it: its path is a prefix of
            // nothing, so the exclusion is decided by the chain the walk found, not by spelling.
            var leafExcluded = await policies.PutAsync(
                Request(
                    PolicyAddress.Assignment(root, "no-writes").Path,
                    $$"""
                    { "properties": {
                        "policyDefinitionId": "{{PolicyAddress.Definition(root, "no-writes").Path}}",
                        "notScopes": [ "{{ScopeId.ManagementGroupOf(ResourceManagerCluster.Tenant, "policy-leaf").Path}}" ] } }
                    """
                ),
                TestContext.Current.CancellationToken
            );
            leafExcluded.IsSuccess.ShouldBeTrue(leafExcluded.Error?.Message);

            var underLeaf = await PutAsync(Widget(reached, "under-leaf"));
            underLeaf.IsSuccess.ShouldBeTrue("the leaf group is excluded, so nothing beneath it is reached: " + underLeaf.Error?.Message);
        } finally {
            await policies.DeleteAsync(Request(PolicyAddress.Assignment(root, "no-writes").Path), TestContext.Current.CancellationToken);
            await policies.DeleteAsync(Request(PolicyAddress.Definition(root, "no-writes").Path), TestContext.Current.CancellationToken);
            (await subscription.SetManagementGroupAsync("")).IsSuccess.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task TenantAsAssignmentNeverAppliesToTenantBEvenAtTheSamePath() {
        // ⚠ THE SAME SUBSCRIPTION GUID, THE SAME GROUP NAME AND THE SAME RESOURCE NAME IN TWO TENANTS —
        // the fixture creates the shared subscription in both. Tenant A denies every write at that
        // group; tenant B's identical write goes through, because B's writes reach B's catalog and A's
        // assignment is not in it. The isolation is the grain key's qualification, and B's catalog is
        // read back to show it holds nothing of A's.
        ResourceManagerCluster.ResetDoubles();

        var shared = ScopeId.Group(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription, "prod");
        var definition = PolicyAddress.Definition(ScopeId.Subscription(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription), "deny-all");
        var assignment = PolicyAddress.Assignment(shared, "deny-all");

        try {
            (await policies.PutAsync(
                    Request(definition.Path, """{ "properties": { "policyRule": { "if": { "field": "type", "like": "*" }, "then": { "effect": "deny" } } } }"""),
                    TestContext.Current.CancellationToken
                ))
                .IsSuccess.ShouldBeTrue();
            (await policies.PutAsync(
                    Request(assignment.Path, $$"""{ "properties": { "policyDefinitionId": "{{definition.Path}}" } }"""),
                    TestContext.Current.CancellationToken
                ))
                .IsSuccess.ShouldBeTrue();

            var mine = ResourceManagerCluster.Address("policy-isolation");
            var theirs = ResourceManagerCluster.Address("policy-isolation", tenant: ResourceManagerCluster.OtherTenant);

            (await PutAsync(mine)).Error!.Code.ShouldBe(ErrorCode.PolicyViolation, "tenant A's own write is refused");

            var other = await PutAsync(theirs);
            other.IsSuccess.ShouldBeTrue("tenant B is not governed by tenant A's policy: " + other.Error?.Message);
            other.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
            other.GetValueOrThrow().Trace.Policy.ShouldBeEmpty();

            var theirCatalog = cluster.For(ResourceManagerCluster.OtherTenant)
                .GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(ResourceManagerCluster.OtherTenant));
            (await theirCatalog.ListAssignmentsAsync(assignment.WithTenant(ResourceManagerCluster.OtherTenant).Scope.Path))
                .GetValueOrThrow().ShouldBeEmpty();
        } finally {
            await policies.DeleteAsync(Request(assignment.Path), TestContext.Current.CancellationToken);
            await policies.DeleteAsync(Request(definition.Path), TestContext.Current.CancellationToken);
        }
    }

    // ── The catalog's own rules ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompiledRulesAreCachedPerScopeAndAWriteToTheDefinitionInvalidatesThem() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("cache");
        await DefineAsync(group, "cached", """{ "if": { "field": "/properties/label", "equals": "one" }, "then": { "effect": "deny" } }""");
        await AssignAsync(group, "cached", "cached");

        var catalog = cluster.For(ResourceManagerCluster.Tenant).GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(ResourceManagerCluster.Tenant));

        (await PutAsync(Widget(group, "c1"), TestingProvider.Body(2, "one"))).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        var warm = await catalog.GetStatisticsAsync();

        (await PutAsync(Widget(group, "c2"), TestingProvider.Body(2, "one"))).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        var reused = await catalog.GetStatisticsAsync();

        reused.Compilations.ShouldBe(warm.Compilations, "a second write compiles nothing");
        reused.Hits.ShouldBeGreaterThan(warm.Hits);

        // The definition changes; the scope's compiled set is dropped and the next write judges the new rule.
        await DefineAsync(group, "cached", """{ "if": { "field": "/properties/label", "equals": "two" }, "then": { "effect": "deny" } }""");

        (await PutAsync(Widget(group, "c3"), TestingProvider.Body(2, "one"))).IsSuccess.ShouldBeTrue("the old rule no longer applies");
        (await catalog.GetStatisticsAsync()).Compilations.ShouldBeGreaterThan(reused.Compilations);
        (await PutAsync(Widget(group, "c4"), TestingProvider.Body(2, "two"))).Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
    }

    [Fact]
    public async Task ADefinitionAnAssignmentNamesCannotBeDeletedAndAnUnknownOneCannotBeAssigned() {
        ResourceManagerCluster.ResetDoubles();

        var group = await GroupAsync("held");
        var definition = await DefineAsync(group, "held", """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "audit" } }""");
        var assignment = await AssignAsync(group, "held", "held");

        var refused = await policies.DeleteAsync(Request(definition), TestContext.Current.CancellationToken);
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain(assignment);

        var unknown = await policies.PutAsync(
            Request(
                PolicyAddress.Assignment(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group), "ghost").Path,
                $$"""{ "properties": { "policyDefinitionId": "{{definition.Replace("/held", "/ghost", StringComparison.Ordinal)}}" } }"""
            ),
            TestContext.Current.CancellationToken
        );
        unknown.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        unknown.Error.Target.ShouldBe("/properties/policyDefinitionId");

        (await policies.DeleteAsync(Request(assignment), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await policies.DeleteAsync(Request(definition), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ADefinitionIsUsableOnlyAtItsScopeAndBeneathIt() {
        // A subscription's definition is not another subscription's to assign.
        ResourceManagerCluster.ResetDoubles();

        var elsewhere = PolicyAddress.Definition(ScopeId.Subscription(ResourceManagerCluster.Tenant, ResourceManagerCluster.IsolatedSubscription), "elsewhere");
        (await policies.PutAsync(
                Request(elsewhere.Path, """{ "properties": { "policyRule": { "if": { "field": "type", "like": "*" }, "then": { "effect": "audit" } } } }"""),
                TestContext.Current.CancellationToken
            ))
            .IsSuccess.ShouldBeTrue();

        try {
            var group = await GroupAsync("scoped");
            var refused = await policies.PutAsync(
                Request(
                    PolicyAddress.Assignment(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group), "borrowed").Path,
                    $$"""{ "properties": { "policyDefinitionId": "{{elsewhere.Path}}" } }"""
                ),
                TestContext.Current.CancellationToken
            );

            refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
            refused.Error.Message.ShouldContain("not '");
        } finally {
            await policies.DeleteAsync(Request(elsewhere.Path), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TheManagerRefusesAnOperatorOutsideTheClosedSetNamingTheList() {
        ResourceManagerCluster.ResetDoubles();

        var put = await policies.PutAsync(
            Request(
                PolicyAddress.Definition(ScopeId.Subscription(ResourceManagerCluster.Tenant, PolicySubscription), "bad").Path,
                """{ "properties": { "policyRule": { "if": { "field": "/properties/label", "matches": "a.*" }, "then": { "effect": "deny" } } } }"""
            ),
            TestContext.Current.CancellationToken
        );

        put.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        put.Error.Target.ShouldBe("/properties/policyRule/if/matches");
        put.Error.Message.ShouldContain("allOf, anyOf or not");
        put.Error.Message.ShouldContain("equals, in, like, exists");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    async Task<string> GroupAsync(string name) {
        var made = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(PolicySubscription, name))
            .CreateAsync(ResourceManagerCluster.Tenant, "eu-west-1");

        made.IsSuccess.ShouldBeTrue(made.Error?.Message);
        return name;
    }

    static ResourceId Widget(string group, string name) =>
        new(ResourceManagerCluster.Tenant, PolicySubscription, group, ConformingReconciler.TypeName, name, Guid.Empty);

    async Task<string> DefineAsync(string group, string name, string rule) {
        _ = group;
        var path = PolicyAddress.Definition(ScopeId.Subscription(ResourceManagerCluster.Tenant, PolicySubscription), name).Path;

        var put = await policies.PutAsync(
            Request(path, $$"""{ "properties": { "displayName": "{{name}}", "policyRule": {{rule}} } }"""),
            TestContext.Current.CancellationToken
        );

        put.IsSuccess.ShouldBeTrue(put.Error?.Message);
        return path;
    }

    async Task<string> AssignAsync(string group, string name, string definition) {
        var path = PolicyAddress.Assignment(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group), name).Path;
        var definitionPath = PolicyAddress.Definition(ScopeId.Subscription(ResourceManagerCluster.Tenant, PolicySubscription), definition).Path;

        var put = await policies.PutAsync(
            Request(path, $$"""{ "properties": { "policyDefinitionId": "{{definitionPath}}" } }"""),
            TestContext.Current.CancellationToken
        );

        put.IsSuccess.ShouldBeTrue(put.Error?.Message);
        return path;
    }

    async Task AssignOperationDenyAsync(string group, string operation) {
        await DefineAsync(group, "deny-" + operation, $$"""{ "if": { "field": "operation", "equals": "{{operation}}" }, "then": { "effect": "deny" } }""");
        await AssignAsync(group, "deny-" + operation, "deny-" + operation);
    }

    async Task UnassignAsync(string group, string name) {
        var path = PolicyAddress.Assignment(ScopeId.Group(ResourceManagerCluster.Tenant, PolicySubscription, group), name).Path;
        (await policies.DeleteAsync(Request(path), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    async Task<IReadOnlyList<PolicyStateRecord>> StatesAsync(ScopeId scope) {
        var listed = await policies.ListAsync(
            new() { Path = PolicyAddress.States(scope).Path, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return listed.GetValueOrThrow().States;
    }

    IPolicyCatalogGrain Catalog() =>
        cluster.For(ResourceManagerCluster.Tenant).GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(ResourceManagerCluster.Tenant));

    /// <summary>Step 5's evaluation of a create, asked of the catalog directly, returning the verdicts it would record.</summary>
    async Task<ImmutableArray<PolicyStateRecord>> EvaluateDirectlyAsync(ResourceId address) {
        var evaluated = await Catalog().EvaluateAsync(
            new() {
                ResourcePath = address.CanonicalPath,
                SubscriptionId = address.SubscriptionId,
                ResourceGroup = address.ResourceGroup,
                ResourceType = address.Type.ToString(),
                ResourceName = address.Name,
                Operation = "create",
                Document = """{"properties":{"label":"unlabelled"}}"""
            }
        );

        evaluated.IsSuccess.ShouldBeTrue(evaluated.Error?.Message);
        return evaluated.GetValueOrThrow().States;
    }

    /// <summary>A body with a tag bag added at the root, where a request carries it.</summary>
    static string Tagged(string body, params (string Key, string Value)[] tags) {
        var document = JsonNode.Parse(body)!.AsObject();
        var bag = new JsonObject();
        foreach (var (key, value) in tags) {
            bag[key] = value;
        }

        document["tags"] = bag;
        return document.ToJsonString();
    }

    static PolicyRequest Request(string path, string body = "{}") =>
        new() { Path = path, Body = body, Caller = ResourceManagerCluster.Caller() };

    Task<Result<WriteAccepted>> PutAsync(ResourceId address, string? body = null) =>
        manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = body ?? TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller(address.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> PatchAsync(ResourceId address, string body) =>
        manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Patch,
                Body = body,
                Caller = ResourceManagerCluster.Caller(address.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> DeleteAsync(ResourceId address) =>
        manager.DeleteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Delete,
                Caller = ResourceManagerCluster.Caller(address.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> ActionAsync(ResourceId address, string action) =>
        manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = action,
                Caller = ResourceManagerCluster.Caller(address.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    async Task ConvergeAsync(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
