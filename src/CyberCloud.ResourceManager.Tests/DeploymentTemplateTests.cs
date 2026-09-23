using CyberCloud.ResourceManager.Orchestration;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The template evaluator on its own: the closed expression set, parameters and variables, and the
///     dependency order. Pure, so no cluster.
/// </summary>
public sealed class DeploymentTemplateTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    static ResourceId Scope { get; } = new(Tenant, Subscription, "prod", Deployments.Type, "d", Guid.Empty);

    static JsonObject Widget(string name, params string[] dependsOn) {
        var widget = new JsonObject {
            ["type"] = "CyberCloud.Testing/widgets",
            ["apiVersion"] = "2026-08-01",
            ["name"] = name,
            ["properties"] = new JsonObject { ["size"] = 1 }
        };

        if (dependsOn.Length > 0) {
            widget["dependsOn"] = new JsonArray([.. dependsOn.Select(static x => (JsonNode)x)]);
        }

        return widget;
    }

    static Result<DeploymentPlan> Evaluate(JsonObject template, JsonObject? parameters = null) =>
        DeploymentTemplate.Evaluate(Scope, template.ToJsonString(), parameters?.ToJsonString() ?? "");

    static JsonObject Template(params JsonObject[] resources) =>
        new() { ["resources"] = new JsonArray([.. resources.Select(static x => (JsonNode)x)]) };

    static string Path(string name, string group = "prod") =>
        $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/{group}/providers/CyberCloud.Testing/widgets/{name}";

    [Fact]
    public void DependenciesComeFirstAndTheTemplatesOrderDecidesEverythingElse() {
        var plan = Evaluate(Template(Widget("c"), Widget("a", "b"), Widget("b"))).GetValueOrThrow();

        plan.Resources.Select(static x => x.Id.Name).ShouldBe(["c", "b", "a"]);
        plan.Resources[2].DependsOn.ShouldBe([Path("b")]);
        plan.Resources.Select(static x => x.Order).ShouldBe([0, 1, 2]);
        plan.Resources.Select(static x => x.TemplateIndex).ShouldBe([0, 2, 1]);
    }

    [Fact]
    public void ACycleIsRefusedAndTheRefusalWalksIt() {
        var refused = Evaluate(Template(Widget("a", "b"), Widget("b", "c"), Widget("c", "a"), Widget("d", "a")));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("cycle");
        refused.Error.Message.ShouldContain("'a' → 'b' → 'c' → 'a'");

        Evaluate(Template(Widget("self", "self"))).Error!.Message.ShouldContain("'self' → 'self'");
    }

    [Theory]
    [InlineData("[reference('x')]", "reference()")]
    [InlineData("[uniqueString(resourceGroup().id)]", "uniqueString()")]
    [InlineData("[toLower('A')]", "toLower()")]
    public void AFunctionOutsideTheClosedSetIsRefusedNamingTheSupportedList(string expression, string named) {
        var widget = Widget("a");
        widget["properties"]!["label"] = expression;

        var refused = Evaluate(Template(widget));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(named);
        refused.Error.Message.ShouldContain("parameters(), variables(), resourceId(), concat()");
        refused.Error.Target.ShouldBe(Deployments.TemplatePointer);
    }

    [Fact]
    public void PropertyAndIndexAccessAreRefusedRatherThanGuessedAt() {
        var template = Template(Widget("a"));
        template["parameters"] = new JsonObject { ["o"] = new JsonObject { ["type"] = "object", ["defaultValue"] = new JsonObject { ["x"] = "y" } } };
        template["resources"]![0]!["properties"]!["label"] = "[parameters('o').x]";

        Evaluate(template).Error!.Message.ShouldContain("reads a property or an index");
    }

    [Fact]
    public void ParametersAndVariablesEvaluateAndAnEscapedBracketIsALiteral() {
        var template = Template(Widget("[concat(variables('stem'), '-1')]"));
        template["parameters"] = new JsonObject {
            ["env"] = new JsonObject { ["type"] = "string", ["allowedValues"] = new JsonArray("dev", "prod") },
            ["size"] = new JsonObject { ["type"] = "int", ["defaultValue"] = 4 },
            ["tags"] = new JsonObject { ["type"] = "array", ["defaultValue"] = new JsonArray("a") }
        };
        template["variables"] = new JsonObject {
            ["stem"] = "[concat('web-', parameters('env'))]",
            ["all"] = "[concat(parameters('tags'), variables('more'))]",
            ["more"] = new JsonArray("b")
        };
        template["resources"]![0]!["properties"] = new JsonObject {
            ["size"] = "[parameters('size')]",
            ["label"] = "[[not an expression]",
            ["cidrs"] = "[variables('all')]",
            ["peer"] = "[resourceId('shared', 'CyberCloud.Testing/widgets', 'hub')]"
        };

        var plan = Evaluate(template, new() { ["env"] = new JsonObject { ["value"] = "prod" } }).GetValueOrThrow();
        var resource = plan.Resources.ShouldHaveSingleItem();

        resource.Id.Name.ShouldBe("web-prod-1");

        var properties = JsonNode.Parse(resource.Body)!["properties"]!;
        properties["size"]!.GetValue<long>().ShouldBe(4);
        properties["label"]!.GetValue<string>().ShouldBe("[not an expression]");
        properties["cidrs"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["a", "b"]);
        properties["peer"]!.GetValue<string>().ShouldBe(Path("hub", "shared"));
    }

    [Fact]
    public void ParameterValuesAreCheckedAgainstTheirDeclarations() {
        var template = Template(Widget("a"));
        template["parameters"] = new JsonObject {
            ["count"] = new JsonObject { ["type"] = "int" },
            ["tier"] = new JsonObject { ["type"] = "string", ["allowedValues"] = new JsonArray("free"), ["defaultValue"] = "free" }
        };

        Evaluate(template).Error!.Message.ShouldContain("'count' has no value");
        Evaluate(template, new() { ["count"] = new JsonObject { ["value"] = "three" } }).Error!.Message.ShouldContain("declared 'int'");

        var outside = Evaluate(
            template,
            new() { ["count"] = new JsonObject { ["value"] = 3 }, ["tier"] = new JsonObject { ["value"] = "gold" } }
        );
        outside.Error!.Message.ShouldContain("allowedValues");
        outside.Error.Target.ShouldBe(Deployments.ParametersPointer);

        Evaluate(template, new() { ["count"] = new JsonObject { ["value"] = 3 }, ["extra"] = new JsonObject { ["value"] = 1 } })
            .Error!.Message.ShouldContain("does not declare");

        // The parameters FILE shape — what cyc reads from disk — is accepted as it is.
        Evaluate(
                template,
                new() {
                    ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
                    ["contentVersion"] = "1.0.0.0",
                    ["parameters"] = new JsonObject { ["count"] = new JsonObject { ["value"] = 3 } }
                }
            )
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ASecureParameterIsRefusedBecauseNothingHereWouldKeepItSecret() {
        var template = Template(Widget("a"));
        template["parameters"] = new JsonObject { ["password"] = new JsonObject { ["type"] = "securestring" } };

        Evaluate(template).Error!.Message.ShouldContain("plain text");
    }

    [Fact]
    public void AVariableThatReachesItselfIsRefused() {
        var template = Template(Widget("[variables('a')]"));
        template["variables"] = new JsonObject { ["a"] = "[variables('b')]", ["b"] = "[concat(variables('a'), 'x')]" };

        Evaluate(template).Error!.Message.ShouldContain("depends on itself");
    }

    [Fact]
    public void TheShapeIsClosedAtEveryLevelAndEachRefusalNamesWhatIsAllowed() {
        var outputs = Template(Widget("a"));
        outputs["outputs"] = new JsonObject();
        Evaluate(outputs).Error!.Message.ShouldContain("'outputs' member, which is not supported");

        var copy = Widget("a");
        copy["copy"] = new JsonObject { ["count"] = 2 };
        Evaluate(Template(copy)).Error!.Message.ShouldContain("type, name, apiVersion, location, tags, properties, dependsOn, resourceGroup");

        Evaluate(new JsonObject()).Error!.Message.ShouldContain("no 'resources'");
        Evaluate(Template()).Error!.Message.ShouldContain("empty");
    }

    [Fact]
    public void AddressesStayInsideTheDeploymentsSubscriptionAndNeverNameADeployment() {
        var nested = Widget("inner");
        nested["type"] = "CyberCloud.Resources/deployments";
        Evaluate(Template(nested)).Error!.Message.ShouldContain("nested deployments are not supported");

        var elsewhere = Widget("a");
        elsewhere["properties"]!["label"] = $"[resourceId('{Guid.NewGuid():D}', 'prod', 'CyberCloud.Testing/widgets', 'x')]";
        Evaluate(Template(elsewhere)).Error!.Message.ShouldContain("its own subscription only");

        Evaluate(Template(Widget("a"), Widget("a"))).Error!.Message.ShouldContain("the same resource");

        var child = Widget("a");
        child["type"] = "CyberCloud.Testing/widgets/parts";
        Evaluate(Template(child)).Error!.Message.ShouldContain("one segment per level");
    }

    [Fact]
    public void DependsOnAcceptsANameATypeAndNameOrAResourceIdAndRefusesWhatItCannotResolve() {
        var plan = Evaluate(
                Template(
                    Widget("x", "CyberCloud.Testing/widgets/y", "[resourceId('CyberCloud.Testing/widgets', 'z')]"),
                    Widget("y", "z"),
                    Widget("z")
                )
            )
            .GetValueOrThrow();

        plan.Resources.Select(static x => x.Id.Name).ShouldBe(["z", "y", "x"]);

        Evaluate(Template(Widget("x", "nowhere"))).Error!.Message.ShouldContain("names no resource in this template");
    }
}
