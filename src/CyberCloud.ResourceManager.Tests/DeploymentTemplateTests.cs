using CyberCloud.ResourceManager.Orchestration;
using CyberCloud.ResourceManager.Registry;
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
    public void AnOutputThatDoublesThroughItsVariablesIsRefusedBeforeItIsBuilt() {
        // Thirty doublings of sixteen characters describe sixteen billion — an OutOfMemoryException in
        // whichever process evaluates it, and the gateway is one. Each variable is under every input cap.
        const int Links = 30;
        var variables = new JsonObject { ["v0"] = "aaaaaaaaaaaaaaaa" };

        for (var i = 1; i <= Links; i++) {
            variables[$"v{i}"] = $"[concat(variables('v{i - 1}'), variables('v{i - 1}'))]";
        }

        var widget = Widget("a");
        widget["properties"]!["label"] = $"[variables('v{Links}')]";
        var template = Template(widget);
        template["variables"] = variables;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refused = Evaluate(template);
        clock.Stop();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain($"more than {DeploymentLimits.MaxEvaluatedLength} characters");
        refused.Error.Target.ShouldBe(Deployments.TemplatePointer);
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        // One large variable referenced many times is the same problem without concat().
        var wide = Template(Widget("b"));
        wide["variables"] = new JsonObject { ["big"] = new string('x', 100_000) };

        for (var i = 0; i < 50; i++) {
            wide["resources"]![0]!["properties"]![$"p{i}"] = "[variables('big')]";
        }

        Evaluate(wide).Error!.Message.ShouldContain("which is the cap");

        // And a template well inside the cap is untouched by it: ten doublings is sixteen thousand.
        variables = new JsonObject { ["v0"] = "aaaaaaaaaaaaaaaa" };

        for (var i = 1; i <= 10; i++) {
            variables[$"v{i}"] = $"[concat(variables('v{i - 1}'), variables('v{i - 1}'))]";
        }

        widget = Widget("c");
        widget["properties"]!["label"] = "[variables('v10')]";
        template = Template(widget);
        template["variables"] = variables;

        var plan = Evaluate(template).GetValueOrThrow();
        JsonNode.Parse(plan.Resources[0].Body)!["properties"]!["label"]!.GetValue<string>().Length.ShouldBe(16 * 1024);
    }

    /// <summary>The registry step 2 resolves each template resource's schema from.</summary>
    static ProviderRegistry Registry { get; } = ProviderRegistry.Build([new TestingProvider(), new DeploymentsProvider()]);

    static System.Text.Json.JsonElement DeploymentBody(JsonObject properties) =>
        System.Text.Json.JsonSerializer.SerializeToElement(new JsonObject { ["properties"] = properties });

    /// <summary>
    ///     A template that sets a child's secret property is refused at step 2, whether the value is a
    ///     literal or a parameter, and on a partial patch as well as a <c>PUT</c> — the review of #39
    ///     read the value back from the deployment's own <c>GET</c>.
    /// </summary>
    [Fact]
    public void ATemplateThatSetsASecretPropertyIsRefusedAtStepTwoWhereverItsValueComesFrom() {
        var validator = new DeploymentBodyValidator(Registry);

        var literal = Widget("a");
        literal["properties"]!["adminPassword"] = "hunter2";

        var put = validator.Validate(
            Scope,
            DeploymentBody(new() { ["template"] = Template(literal).ToJsonString() }),
            WriteVerb.Put
        );

        put.IsFailure.ShouldBeTrue();
        put.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        put.Error.Message.ShouldContain("'/properties/adminPassword' on CyberCloud.Testing/widgets is a secret property");
        put.Error.Target.ShouldBe("/properties/template/resources/0");

        // ⚠ The value is a parameter the patch doesn't carry, so the template can't be evaluated here —
        // and the key alone is enough to refuse it, before the template is stored.
        var fromParameter = Widget("b");
        fromParameter["properties"]!["adminPassword"] = "[parameters('password')]";
        var template = Template(fromParameter);
        template["parameters"] = new JsonObject { ["password"] = new JsonObject { ["type"] = "string" } };

        validator.Validate(Scope, DeploymentBody(new() { ["template"] = template.ToJsonString() }), WriteVerb.Patch)
            .Error!.Message.ShouldContain("is a secret property");

        // ⚠ An api-version that is itself an expression hides the type's schema from the structural
        // read; the evaluated plan resolves it.
        var hidden = Widget("c");
        hidden["apiVersion"] = "[variables('version')]";
        hidden["properties"]!["adminPassword"] = "hunter2";
        var byExpression = Template(hidden);
        byExpression["variables"] = new JsonObject { ["version"] = "2026-08-01" };

        validator.Validate(Scope, DeploymentBody(new() { ["template"] = byExpression.ToJsonString() }), WriteVerb.Put)
            .Error!.Message.ShouldContain("is a secret property");

        // The control: the same widget without the secret deploys.
        validator.Validate(Scope, DeploymentBody(new() { ["template"] = Template(Widget("d")).ToJsonString() }), WriteVerb.Put)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void APatchIsCheckedAtStepTwoOnlyWhenItDecidesTheWholeTemplateAndItsParameters() {
        var template = Template(Widget("[parameters('name')]"));
        template["parameters"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } };

        static System.Text.Json.JsonElement Body(JsonObject properties) =>
            System.Text.Json.JsonSerializer.SerializeToElement(new JsonObject { ["properties"] = properties });

        var validator = new DeploymentBodyValidator(Registry);

        // A new template whose required parameter is already stored: the merged body deploys, and this
        // patch cannot see the stored value — refusing it was the false 400.
        validator.Validate(Scope, Body(new() { ["template"] = template.ToJsonString() }), WriteVerb.Patch)
            .IsSuccess.ShouldBeTrue();

        // Both halves in the patch decide the merged body, so it is checked, and refused, here.
        var both = validator.Validate(
            Scope,
            Body(new() { ["template"] = template.ToJsonString(), ["parameters"] = "{}" }),
            WriteVerb.Patch
        );

        both.IsFailure.ShouldBeTrue();
        both.Error!.Message.ShouldContain("'name' has no value");

        // A PUT is the whole body, so it is always checked.
        validator.Validate(Scope, Body(new() { ["template"] = template.ToJsonString() }), WriteVerb.Put)
            .IsFailure.ShouldBeTrue();
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
