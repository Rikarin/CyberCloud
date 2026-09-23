using CyberCloud.Core.Policy;
using Shouldly;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Core.Tests;

/// <summary>
///     The policy language — <see cref="PolicyCondition" />, <see cref="PolicyRule" /> and
///     <see cref="PolicyModification" />: the closed sets, the comparisons, the cost bounds and the
///     rewrites. docs/plan/08 § Policy, issue #46.
/// </summary>
/// <remarks>
///     ⚠ Pure functions, so the whole surface is tested here, directly; what the write path does with
///     a decision is <c>PolicyEnforcementTests</c>, through real grains.
/// </remarks>
public class PolicyRuleTests {
    const string Target = "/properties/policyRule";

    static PolicyFacts Facts(string body, string operation = PolicyOperations.Create, string action = "") =>
        new("CyberCloud.Cache/redis", "main", operation, action, (JsonObject)JsonNode.Parse(body)!);

    static PolicyRule Rule(string json) {
        var parsed = PolicyRule.Parse(json, Target);
        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.ToString());
        return parsed.GetValueOrThrow();
    }

    static bool Holds(string condition, string body, string operation = PolicyOperations.Create) =>
        Rule($$"""{ "if": {{condition}}, "then": { "effect": "deny" } }""").Matches(Facts(body, operation));

    // ── The closed sets ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{ "field": "/properties/sku", "contains": "p" }""", "contains", "/properties/policyRule/if/contains")]
    [InlineData("""{ "allOf": [ { "field": "/x", "greater": 3 } ] }""", "greater", "/properties/policyRule/if/allOf/0/greater")]
    [InlineData("""{ "or": [] }""", "or", "/properties/policyRule/if/or")]
    public void AnOperatorOutsideTheClosedSetIsRefusedByNameWithTheListAndATarget(string condition, string word, string target) {
        var parsed = PolicyRule.Parse($$"""{ "if": {{condition}}, "then": { "effect": "deny" } }""", Target);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        parsed.Error.Message.ShouldContain($"'{word}' is not an operator");

        // The message names the whole set, so the caller can fix the rule without the documentation.
        foreach (var known in PolicyCondition.Combinators.Concat(PolicyCondition.Operators)) {
            parsed.Error.Message.ShouldContain(known);
        }

        parsed.Error.Target.ShouldBe(target);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("Deny")]
    [InlineData("denyAction")]
    public void AnEffectOutsideTheClosedSetIsRefused(string effect) {
        var parsed = PolicyRule.Parse($$"""{ "if": { "field": "type", "equals": "x" }, "then": { "effect": "{{effect}}" } }""", Target);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Target.ShouldBe(Target + "/then/effect");
        parsed.Error.Message.ShouldContain("deny, audit, modify");
    }

    [Fact]
    public void AModifyOperationOutsideAddAndReplaceIsRefused() {
        var parsed = PolicyRule.Parse(
            """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify", "operations": [ { "operation": "remove", "field": "/tags/x", "value": 1 } ] } }""",
            Target
        );

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Target.ShouldBe(Target + "/then/operations/0/operation");
        parsed.Error.Message.ShouldContain("add, replace");
    }

    [Fact]
    public void OperationsBelongToModifyAndNothingElse() {
        // A deny rule that carried operations would read as if it rewrote something.
        PolicyRule.Parse(
                """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "deny", "operations": [] } }""",
                Target
            )
            .Error!.Target.ShouldBe(Target + "/then/operations");

        // …and a modify rule without them rewrites nothing.
        PolicyRule.Parse("""{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify" } }""", Target)
            .Error!.Target.ShouldBe(Target + "/then/operations");
    }

    [Theory]
    [InlineData("properties.sku")]
    [InlineData("sku")]
    [InlineData("/")]
    [InlineData("/properties//sku")]
    [InlineData("/properties/~2")]
    public void AFieldIsAFactOrAPointerAndNothingElse(string field) {
        var parsed = PolicyRule.Parse($$"""{ "if": { "field": "{{field}}", "equals": "x" }, "then": { "effect": "audit" } }""", Target);

        parsed.IsFailure.ShouldBeTrue(field);
        parsed.Error!.Target.ShouldBe(Target + "/if/field");
    }

    [Fact]
    public void AModifyCannotWriteAFactAboutTheRequest() {
        var parsed = PolicyRule.Parse(
            """{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify", "operations": [ { "operation": "replace", "field": "type", "value": "x" } ] } }""",
            Target
        );

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Message.ShouldContain("fact about the request");
    }

    [Fact]
    public void AMemberNameWithASlashIsEscapedInTheTargetRatherThanThrowing() {
        // ⚠ Error's constructor refuses a target that is not an RFC 6901 pointer. A member named "a/b"
        // appended raw would make the refusal itself throw.
        var parsed = PolicyRule.Parse("""{ "if": { "a/b~c": 1 }, "then": { "effect": "deny" } }""", Target);

        parsed.Error!.Target.ShouldBe(Target + "/if/a~1b~0c");
    }

    // ── The cost bounds ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConditionDeeperThanTheLimitIsRefused() {
        var condition = new StringBuilder();
        for (var i = 0; i <= PolicyCondition.MaxDepth; i++) {
            condition.Append("""{ "not": """);
        }

        condition.Append("""{ "field": "type", "equals": "x" }""");
        condition.Append('}', PolicyCondition.MaxDepth + 1);

        var parsed = PolicyRule.Parse($$"""{ "if": {{condition}}, "then": { "effect": "deny" } }""", Target);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Message.ShouldContain("nests deeper");
    }

    [Fact]
    public void AConditionWiderThanTheNodeBudgetIsRefused() {
        // Five combinators of sixty leaves each: every combinator is within MaxBranches, and the whole
        // is past MaxNodes — the budget is on the rule, not on each list.
        var leaf = """{ "field": "type", "equals": "x" }""";
        var branch = "{ \"anyOf\": [" + string.Join(",", Enumerable.Repeat(leaf, 60)) + "] }";
        var condition = "{ \"allOf\": [" + string.Join(",", Enumerable.Repeat(branch, 5)) + "] }";

        var parsed = PolicyRule.Parse($$"""{ "if": {{condition}}, "then": { "effect": "deny" } }""", Target);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Message.ShouldContain("more than 256 nodes");
    }

    // ── The comparisons ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{ "field": "/properties/sku", "equals": "GP1" }""", true)]
    [InlineData("""{ "field": "/properties/sku", "equals": "gp2" }""", false)]
    [InlineData("""{ "field": "/properties/size", "equals": 4.0 }""", true)]
    [InlineData("""{ "field": "/properties/size", "in": [ 1, 2, 4 ] }""", true)]
    [InlineData("""{ "field": "/properties/public", "equals": true }""", false)]
    [InlineData("""{ "field": "/properties/missing", "equals": "x" }""", false)]
    [InlineData("""{ "not": { "field": "/properties/missing", "equals": "x" } }""", true)]
    [InlineData("""{ "field": "/tags/env", "exists": true }""", true)]
    [InlineData("""{ "field": "/tags/owner", "exists": true }""", false)]
    [InlineData("""{ "field": "/tags/owner", "exists": false }""", true)]
    [InlineData("""{ "field": "/properties/nulled", "exists": false }""", true)]
    [InlineData("""{ "field": "/properties/rules/1/port", "equals": 443 }""", true)]
    [InlineData("""{ "field": "/properties/rules/9/port", "exists": false }""", true)]
    [InlineData("""{ "field": "type", "equals": "cybercloud.cache/REDIS" }""", true)]
    [InlineData("""{ "field": "name", "like": "ma*" }""", true)]
    [InlineData("""{ "allOf": [ { "field": "/tags/env", "equals": "prod" }, { "field": "/properties/sku", "in": [ "gp1", "gp2" ] } ] }""", true)]
    [InlineData("""{ "anyOf": [ { "field": "/tags/env", "equals": "dev" }, { "field": "/properties/sku", "equals": "x" } ] }""", false)]
    public void ConditionsCompareTheWayAzureDoes(string condition, bool expected) {
        const string body = """
            {
              "location": "eu-central",
              "tags": { "env": "prod" },
              "properties": {
                "sku": "gp1", "size": 4, "public": false, "nulled": null,
                "rules": [ { "port": 80 }, { "port": 443 } ]
              }
            }
            """;

        Holds(condition, body).ShouldBe(expected, condition);
    }

    [Theory]
    [InlineData("pg-*", "pg-main", true)]
    [InlineData("pg-*", "PG-MAIN", true)]
    [InlineData("*-main", "pg-main", true)]
    [InlineData("*-main", "pg-mainx", false)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "acb", false)]
    [InlineData("a*a", "a", false)]
    [InlineData("*", "", true)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    public void LikeIsAGlobWithOneMetacharacter(string pattern, string text, bool expected) =>
        Holds($$"""{ "field": "/label", "like": "{{pattern}}" }""", $$"""{ "label": "{{text}}" }""").ShouldBe(expected);

    [Fact]
    public void LikeAgainstANonStringIsFalseRatherThanAStringification() =>
        Holds("""{ "field": "/size", "like": "4*" }""", """{ "size": 42 }""").ShouldBeFalse();

    [Fact]
    public void TheOperationAndActionFactsAreWhatTheRequestSays() {
        var rule = Rule("""{ "if": { "allOf": [ { "field": "operation", "equals": "action" }, { "field": "action", "equals": "rotateKeys" } ] }, "then": { "effect": "deny" } }""");

        rule.Matches(Facts("{}", PolicyOperations.Action, "rotateKeys")).ShouldBeTrue();
        rule.Matches(Facts("{}", PolicyOperations.Action, "listKeys")).ShouldBeFalse();
        rule.Matches(Facts("{}", PolicyOperations.Delete)).ShouldBeFalse();
    }

    // ── Where a rule applies ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AShapeRuleAppliesToWritesAndNeverToADeleteOrAnAction() {
        // ⚠ The rule PolicyRule's remarks argue for: "deny premium" must not make the premium
        // resources that exist undeletable.
        var shape = Rule("""{ "if": { "field": "/properties/sku", "equals": "premium" }, "then": { "effect": "deny" } }""");

        shape.AppliesTo(PolicyOperations.Create).ShouldBeTrue();
        shape.AppliesTo(PolicyOperations.Update).ShouldBeTrue();
        shape.AppliesTo(PolicyOperations.Delete).ShouldBeFalse();
        shape.AppliesTo(PolicyOperations.Action).ShouldBeFalse();
    }

    [Fact]
    public void ADenyRuleThatNamesTheOperationAppliesToEveryKindOfRequest() {
        var named = Rule("""{ "if": { "not": { "field": "operation", "in": [ "create" ] } }, "then": { "effect": "deny" } }""");

        foreach (var operation in PolicyOperations.All) {
            named.AppliesTo(operation).ShouldBeTrue(operation);
        }
    }

    [Fact]
    public void AuditAndModifyNeverApplyToADeleteOrAnActionEvenWhenTheyNameTheOperation() {
        var audit = Rule("""{ "if": { "field": "operation", "equals": "delete" }, "then": { "effect": "audit" } }""");

        audit.AppliesTo(PolicyOperations.Delete).ShouldBeFalse();
        audit.AppliesTo(PolicyOperations.Action).ShouldBeFalse();
    }

    // ── Modify ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AddWritesADefaultAndLeavesWhatTheCallerSent() {
        var target = (JsonObject)JsonNode.Parse("""{ "tags": { "env": "prod" } }""")!;
        var add = Modification("add", "/tags/env", "\"dev\"");
        var adds = Modification("add", "/tags/costCenter", "\"unassigned\"");

        add.ApplyTo(target, target).GetValueOrThrow().ShouldBeFalse("the caller's value stands");
        adds.ApplyTo(target, target).GetValueOrThrow().ShouldBeTrue();

        target.ToJsonString().ShouldBe("""{"tags":{"env":"prod","costCenter":"unassigned"}}""");
    }

    [Fact]
    public void ReplaceWritesWhateverTheCallerSentAndCreatesTheObjectsAboveIt() {
        var target = (JsonObject)JsonNode.Parse("""{ "properties": { "sku": "premium" } }""")!;

        Modification("replace", "/properties/sku", "\"gp1\"").ApplyTo(target, target).GetValueOrThrow().ShouldBeTrue();
        Modification("replace", "/properties/network/public", "false").ApplyTo(target, target).GetValueOrThrow().ShouldBeTrue();

        target.ToJsonString().ShouldBe("""{"properties":{"sku":"gp1","network":{"public":false}}}""");
    }

    [Fact]
    public void OnAPatchAddAsksTheMergedBodyAndWritesThePatch() {
        // ⚠ A merge patch omits what it does not change. "Is costCenter set" is a question about the
        // resource, so it is asked of the merged body; the answer is written into the patch, because the
        // patch is what the write sends.
        var patch = (JsonObject)JsonNode.Parse("""{ "properties": { "label": "b" } }""")!;
        var merged = (JsonObject)JsonNode.Parse("""{ "tags": { "costCenter": "c-1" }, "properties": { "label": "b" } }""")!;

        Modification("add", "/tags/costCenter", "\"unassigned\"").ApplyTo(patch, merged).GetValueOrThrow().ShouldBeFalse();
        Modification("replace", "/properties/tier", "\"hot\"").ApplyTo(patch, merged).GetValueOrThrow().ShouldBeTrue();

        patch.ToJsonString().ShouldBe("""{"properties":{"label":"b","tier":"hot"}}""");
        merged["properties"]!["tier"]!.GetValue<string>().ShouldBe("hot", "the two documents are kept in step");
    }

    [Fact]
    public void AReplaceUnderAScalarIsRefusedRatherThanReshapingTheBody() {
        var target = (JsonObject)JsonNode.Parse("""{ "properties": "flat" }""")!;

        var made = Modification("replace", "/properties/sku", "\"gp1\"").ApplyTo(target, target);

        made.IsFailure.ShouldBeTrue();
        made.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        target.ToJsonString().ShouldBe("""{"properties":"flat"}""");
    }

    static PolicyModification Modification(string operation, string field, string value) =>
        Rule(
                $$"""{ "if": { "field": "type", "like": "*" }, "then": { "effect": "modify", "operations": [ { "operation": "{{operation}}", "field": "{{field}}", "value": {{value}} } ] } }"""
            )
            .Operations[0];

    [Fact]
    public void ARuleRoundTripsThroughItsJsonText() {
        // The catalog stores a rule as text and parses it once per cache miss; the parse from text has
        // to agree with the parse from the element the manager validated.
        const string json = """{ "if": { "field": "/tags/env", "exists": false }, "then": { "effect": "audit" } }""";

        using var document = JsonDocument.Parse(json);
        var fromElement = PolicyRule.Parse(document.RootElement, Target).GetValueOrThrow();
        var fromText = PolicyRule.Parse(json, Target).GetValueOrThrow();

        fromText.Effect.ShouldBe(fromElement.Effect);
        fromText.Matches(Facts("{}")).ShouldBe(fromElement.Matches(Facts("{}")));
        fromText.Matches(Facts("{}")).ShouldBeTrue();
    }
}
