using CyberCloud.ResourceManager.Orchestration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <c>CyberCloud.Resources/deployments</c> through real grains: a parent operation with a child per
///     resource, each child written through the write path as the deployment's creator. docs/plan/08
///     § Long-running operations, "Nested operations".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every grain here is the real one</b> — the deployment's own resource, index, group and
///         operation grains, and every child's — and the children are written by the silo's own
///         <see cref="IResourceManager" />, the one <c>OperationGrain</c> resolves, not by this suite's.
///         What is doubled is the authorization engine (<see cref="SwitchableAuthorizer" />), which is
///         what lets a test say "this caller holds nothing on that group"; the same refusal against
///         the real engine is <c>CyberCloud.Isolation</c>'s <c>DeploymentAuthorizationTests</c>.
///     </para>
///     <para>
///         ⚠ <b>Driven by hand, with the one-way notification racing the hand.</b> A child that ends
///         tells its parent (<see cref="IOperationGrain.NotifyChildTerminalAsync" />), so the parent may
///         run a pass the test did not ask for. <see cref="DriveToEndAsync" /> therefore drives until
///         the parent is terminal rather than counting passes, and every assertion is about where the
///         run ended, never about how many passes it took.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class DeploymentTests(ResourceManagerCluster cluster) {
    const string WidgetType = "CyberCloud.Testing/widgets";

    /// <summary>
    ///     Two widgets, the one that depends on the other listed FIRST, so an evaluator that deployed in
    ///     template order would get it wrong. The front end's name and label are expressions over a
    ///     parameter, a variable and <c>resourceId()</c>.
    /// </summary>
    static string TwoWidgets(string backEndGroup = "", string backEndName = "[concat(parameters('prefix'), '-backend')]") =>
        new JsonObject {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = new JsonObject {
                ["prefix"] = new JsonObject { ["type"] = "string" },
                ["size"] = new JsonObject { ["type"] = "int", ["defaultValue"] = 3 }
            },
            ["variables"] = new JsonObject { ["backend"] = backEndName },
            ["resources"] = new JsonArray(
                new JsonObject {
                    ["type"] = WidgetType,
                    ["apiVersion"] = TestingProvider.V2026,
                    ["name"] = "[concat(parameters('prefix'), '-frontend')]",
                    ["location"] = "eu-central",
                    ["properties"] = new JsonObject {
                        ["size"] = "[parameters('size')]",
                        ["label"] = backEndGroup.Length == 0
                            ? "[concat('talks-to ', resourceId('CyberCloud.Testing/widgets', variables('backend')))]"
                            : "front"
                    },
                    ["dependsOn"] = new JsonArray(
                        backEndGroup.Length == 0
                            ? "[resourceId('CyberCloud.Testing/widgets', variables('backend'))]"
                            : "[variables('backend')]"
                    )
                },
                BackEnd(backEndGroup)
            )
        }.ToJsonString();

    static JsonObject BackEnd(string group) {
        var backEnd = new JsonObject {
            ["type"] = WidgetType,
            ["apiVersion"] = TestingProvider.V2026,
            ["name"] = "[variables('backend')]",
            ["location"] = "eu-central",
            ["properties"] = new JsonObject { ["size"] = 1 }
        };

        if (group.Length > 0) {
            backEnd["resourceGroup"] = group;
        }

        return backEnd;
    }

    static string Parameters(string prefix) =>
        new JsonObject { ["prefix"] = new JsonObject { ["value"] = prefix } }.ToJsonString();

    static string DeploymentBody(string template, string parameters) =>
        new JsonObject {
            ["properties"] = new JsonObject { ["template"] = template, ["parameters"] = parameters }
        }.ToJsonString();

    static ResourceId Deployment(string name) =>
        new(ResourceManagerCluster.Tenant, ResourceManagerCluster.Subscription, "prod", Deployments.Type, name, Guid.Empty);

    static ResourceId Widget(string name, string group = "prod") =>
        ResourceManagerCluster.Address(name, group);

    static CallerContext Alice => ResourceManagerCluster.Caller();

    [Fact]
    public async Task ATwoResourceTemplateDeploysInDependencyOrderAsItsCreatorAndRecordsItsHistory() {
        ResourceManagerCluster.ResetDoubles();

        var deployment = Deployment("two-widgets");
        var accepted = await PutAsync(deployment, TwoWidgets(), Parameters("shop"));

        accepted.Trace.IsCanonicalPrefix().ShouldBeTrue("the deployment's own PUT is an ordinary write.");
        accepted.Resource.ProvisioningState.ShouldBe(ProvisioningState.Creating);

        var ended = await DriveToEndAsync(accepted.OperationId);

        ended.State.ShouldBe(OperationState.Succeeded, Describe(ended));
        ended.PercentComplete.ShouldBe(100);
        ended.Children.Length.ShouldBe(2, "one child operation per template resource.");

        // ── The order: the back end first, although the template lists it second ─────────────────
        var first = (await cluster.Operation(ResourceManagerCluster.Tenant, ended.Children[0]).GetAsync()).GetValueOrThrow();
        var second = (await cluster.Operation(ResourceManagerCluster.Tenant, ended.Children[1]).GetAsync()).GetValueOrThrow();

        first.ResourcePath.ShouldBe(Widget("shop-backend").Path);
        second.ResourcePath.ShouldBe(Widget("shop-frontend").Path);

        // ── Each child is a child: it names its parent, and it reached its own terminal state ───────
        first.ParentOperationId.ShouldBe(accepted.OperationId);
        second.ParentOperationId.ShouldBe(accepted.OperationId);
        first.State.ShouldBe(OperationState.Succeeded);
        second.State.ShouldBe(OperationState.Succeeded);

        // ── As the caller: every child's step 3 was asked for Alice at the child's own address ──────
        var checks = SwitchableAuthorizer.Checks.ToList();

        foreach (var child in new[] { Widget("shop-backend"), Widget("shop-frontend") }) {
            checks.ShouldContain(
                x => x.Path == child.Path && x.Caller == Alice.ToString() && x.Permission == "write",
                $"no write check at '{child.Path}' as {Alice}."
            );
        }

        checks.Select(static x => x.Caller).Distinct().ShouldBe([Alice.ToString()], "a check was made as somebody else.");

        var frontEnd = await ReadAsync(Widget("shop-frontend"));
        frontEnd.CreatedBy.ShouldBe(Alice.ToString(), "the child was created as the platform rather than as its caller.");

        // ── The expressions were evaluated into the bodies the children were written with ──────────
        using (var body = JsonDocument.Parse(frontEnd.Body)) {
            var properties = body.RootElement.GetProperty("properties");
            properties.GetProperty("size").GetInt32().ShouldBe(3, "the parameter's defaultValue");
            properties.GetProperty("label").GetString().ShouldBe("talks-to " + Widget("shop-backend").Path);
        }

        // ── The history is the deployment's body ─────────────────────────────────────────────────────
        var record = await ReadAsync(deployment, Deployments.V2026);
        record.ProvisioningState.ShouldBe(ProvisioningState.Succeeded);

        using (var body = JsonDocument.Parse(record.Body)) {
            var properties = body.RootElement.GetProperty("properties");

            properties.GetProperty("outputResources").EnumerateArray().Select(static x => x.GetString())
                .ShouldBe([Widget("shop-backend").Path, Widget("shop-frontend").Path]);

            var steps = properties.GetProperty("steps").EnumerateArray().Select(static x => x.GetString()!).ToList();
            steps.Count.ShouldBe(2);
            steps[0].ShouldStartWith("Succeeded " + Widget("shop-backend").Path);
            steps[0].ShouldContain(ended.Children[0].ToString("D"));
            steps[1].ShouldStartWith("Succeeded " + Widget("shop-frontend").Path);

            properties.GetProperty("error").GetString().ShouldBe("");
            properties.GetProperty("rollback").GetString().ShouldBe("");

            // The template itself is still the body a GET returns.
            properties.GetProperty("template").GetString().ShouldBe(TwoWidgets());
        }
    }

    [Fact]
    public async Task AChildTheCallerMayNotWriteIsRefusedAndTheDeploymentFailsNamingItWithTheRollbackRecorded() {
        ResourceManagerCluster.ResetDoubles();

        // Alice holds nothing on the group the back end is placed in. The front end is in "prod",
        // where she may write — so the deployment must get as far as the refusal and no further.
        SwitchableAuthorizer.DeniedGroups["sealed"] = true;

        var deployment = Deployment("refused-child");

        // ⚠ The back end is in "sealed" and the front end depends on it, so the refusal comes FIRST
        // here; the ordering-then-refusal case with something already created is the next test's.
        var accepted = await PutAsync(deployment, TwoWidgets("sealed"), Parameters("vault"));
        var ended = await DriveToEndAsync(accepted.OperationId);

        ended.State.ShouldBe(OperationState.Failed, Describe(ended));
        ended.Children.ShouldBeEmpty("the refused child was never accepted, so it has no operation.");

        var refused = Widget("vault-backend", "sealed").Path;
        ended.Error.ShouldNotBeNull();
        ended.Error.Message.ShouldContain(refused);
        ended.Error.Message.ShouldContain("ResourceNotFound");
        ended.Error.Target.ShouldBe(refused);

        // The check that refused it was made as Alice, at the child's own address.
        SwitchableAuthorizer.Checks.ShouldContain(x => x.Path == refused && x.Caller == Alice.ToString());

        // Nothing after it was written.
        (await cluster.Index(Widget("vault-frontend")).ResolveAsync()).IsFailure.ShouldBeTrue(
            "the front end was written after the resource it depends on was refused."
        );

        var record = await ReadAsync(deployment, Deployments.V2026);
        record.ProvisioningState.ShouldBe(ProvisioningState.Failed);
        record.LastFailure.ShouldContain(refused);

        using var body = JsonDocument.Parse(record.Body);
        var properties = body.RootElement.GetProperty("properties");
        var steps = properties.GetProperty("steps").EnumerateArray().Select(static x => x.GetString()!).ToList();

        steps[0].ShouldStartWith("Failed " + refused);
        steps[0].ShouldContain("refused by the write path — ResourceNotFound");
        steps[1].ShouldStartWith("NotStarted " + Widget("vault-frontend").Path);
        properties.GetProperty("error").GetString()!.ShouldContain(refused);
        properties.GetProperty("rollback").GetString()!.ShouldStartWith("Not performed");
    }

    [Fact]
    public async Task AChildsFailureIsVisibleOnTheParentAndWhatWasCreatedBeforeItIsLeftAndRecorded() {
        ResourceManagerCluster.ResetDoubles();

        var deployment = Deployment("failing-child");
        var accepted = await PutAsync(deployment, TwoWidgets(), Parameters("burn"));
        var parent = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        // Drive until the SECOND child exists, then make its reconciler refuse — so one resource has
        // been created and the next one fails.
        OperationStatus status = (await parent.DriveAsync()).GetValueOrThrow();

        for (var i = 0; i < 20 && status.Children.Length < 2; i++) {
            foreach (var child in status.Children) {
                _ = await cluster.Operation(ResourceManagerCluster.Tenant, child).DriveAsync();
            }

            status = (await parent.DriveAsync()).GetValueOrThrow();
        }

        status.Children.Length.ShouldBe(2, Describe(status));

        var failing = (await cluster.Operation(ResourceManagerCluster.Tenant, status.Children[1]).GetAsync()).GetValueOrThrow();
        FakeWorld.FailWith[failing.ResourceId] = "the widget press jammed";

        var ended = await DriveToEndAsync(accepted.OperationId);

        ended.State.ShouldBe(OperationState.Failed, Describe(ended));
        ended.Error.ShouldNotBeNull();

        // ⚠ The child's failure is the parent's, named: the resource, the child operation and the
        // child's own reason all appear on the parent's status.
        ended.Error.Message.ShouldContain(Widget("burn-frontend").Path);
        ended.Error.Message.ShouldContain(status.Children[1].ToString("D"));
        ended.Error.Message.ShouldContain("the widget press jammed");

        var record = await ReadAsync(deployment, Deployments.V2026);
        record.ProvisioningState.ShouldBe(ProvisioningState.Failed);

        using var body = JsonDocument.Parse(record.Body);
        var properties = body.RootElement.GetProperty("properties");

        properties.GetProperty("outputResources").EnumerateArray().Select(static x => x.GetString())
            .ShouldBe([Widget("burn-backend").Path], "the back end succeeded and is the one output.");

        var rollback = properties.GetProperty("rollback").GetString()!;
        rollback.ShouldStartWith("Not performed.");
        rollback.ShouldContain(Widget("burn-backend").Path);
        rollback.ShouldContain(Widget("burn-frontend").Path + ", whose create failed");

        // And nothing was rolled back: the back end is still there, Succeeded.
        (await ReadAsync(Widget("burn-backend"))).ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task CancellingTheParentCancelsTheChildInFlightAndStartsNothingAfterIt() {
        ResourceManagerCluster.ResetDoubles();

        var deployment = Deployment("cancelled");
        var accepted = await PutAsync(deployment, TwoWidgets(), Parameters("halt"));
        var parent = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        var status = (await parent.DriveAsync()).GetValueOrThrow();
        status.Children.Length.ShouldBe(1, Describe(status));

        var child = cluster.Operation(ResourceManagerCluster.Tenant, status.Children[0]);
        var inFlight = (await child.GetAsync()).GetValueOrThrow();

        // The back end's reconciler never converges, so the child is mid-flight when the cancel lands.
        FakeWorld.StayInProgress[inFlight.ResourceId] = true;
        (await child.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Running);

        (await parent.CancelAsync("the change window closed")).IsSuccess.ShouldBeTrue();

        // ⚠ Propagated at once, not at the parent's next pass.
        var told = (await child.GetAsync()).GetValueOrThrow();
        told.CancelRequested.ShouldBeTrue("the parent's cancellation did not reach the child in flight.");
        told.CancelReason.ShouldContain(accepted.OperationId.ToString("D"));

        FakeWorld.StayInProgress.TryRemove(inFlight.ResourceId, out _);

        var ended = await DriveToEndAsync(accepted.OperationId);

        ended.State.ShouldBe(OperationState.Canceled, Describe(ended));
        (await child.GetAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Canceled);
        ended.Children.Length.ShouldBe(1, "a child was started after the cancellation.");

        (await cluster.Index(Widget("halt-frontend")).ResolveAsync()).IsFailure.ShouldBeTrue();

        var record = await ReadAsync(deployment, Deployments.V2026);
        record.ProvisioningState.ShouldBe(ProvisioningState.Canceled);

        using var body = JsonDocument.Parse(record.Body);
        var steps = body.RootElement.GetProperty("properties").GetProperty("steps").EnumerateArray()
            .Select(static x => x.GetString()!)
            .ToList();

        steps[0].ShouldStartWith("Canceled " + Widget("halt-backend").Path);
        steps[1].ShouldStartWith("NotStarted " + Widget("halt-frontend").Path);
    }

    [Fact]
    public async Task ADeploymentWhoseOwnClaimIsGoneCancelsBeforeWritingAnyChild() {
        ResourceManagerCluster.ResetDoubles();

        // ⚠ THE BATCH-3 GHOST FIX, ON THE NEW BRANCH. OperationGrain confirms a create's index claim on
        // its first pass and cancels the create if the name has gone to somebody else (issue #44). A
        // deployment's pass is DeploymentDriver rather than a reconciler, and that branch must sit
        // AFTER the confirmation — otherwise a deployment whose silo died between steps 10 and 3
        // would write every child of a deployment nobody can address. Built by hand, as
        // ClaimConfirmedByTheOperationTests builds the same state for an ordinary resource.
        var deployment = Deployment("ghost");
        var resourceId = Guid.NewGuid();
        var rival = Guid.NewGuid();
        var index = cluster.Index(deployment);

        (await index.TryClaimAsync(deployment.WithId(resourceId), resourceId)).IsSuccess.ShouldBeTrue();
        (await index.ReleaseAsync(resourceId)).IsSuccess.ShouldBeTrue();
        (await index.TryClaimAsync(deployment.WithId(rival), rival)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(rival)).IsSuccess.ShouldBeTrue();

        var operationId = Guid.NewGuid();
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);

        (await operation.StartAsync(
                new() {
                    OperationId = operationId,
                    Kind = OperationKind.Create,
                    ResourcePath = deployment.Path,
                    ResourceId = resourceId,
                    TenantId = ResourceManagerCluster.Tenant,
                    SubscriptionId = ResourceManagerCluster.Subscription,
                    ApiVersion = Deployments.V2026,
                    Desired = DeploymentBody(TwoWidgets(), Parameters("ghost")),
                    IndexClaimed = true,
                    Caller = Alice
                }
            )).IsSuccess.ShouldBeTrue();

        var ended = await DriveToEndAsync(operationId);

        ended.State.ShouldBe(OperationState.Canceled, Describe(ended));
        ended.CancelReason.ShouldContain("could not be confirmed");
        ended.Children.ShouldBeEmpty("a deployment whose name is not its own wrote a child.");
        (await cluster.Index(Widget("ghost-backend")).ResolveAsync()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AParentReDrivenAfterItsActivationIsLostResumesAtItsCursorWithoutRewritingAChild() {
        ResourceManagerCluster.ResetDoubles();

        var accepted = await PutAsync(Deployment("resumed"), TwoWidgets(), Parameters("again"));
        var parent = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        var status = (await parent.DriveAsync()).GetValueOrThrow();
        status.Children.Length.ShouldBe(1);
        var firstChild = status.Children[0];

        await parent.DeactivateAsync();

        var resumed = (await parent.DriveAsync()).GetValueOrThrow();
        resumed.Activations.ShouldBe(status.Activations + 1, "the parent did not come back from durable state.");
        resumed.Children.ShouldBe([firstChild], "the re-drive re-planned and wrote the first child again.");

        (await DriveToEndAsync(accepted.OperationId)).State.ShouldBe(OperationState.Succeeded);
    }

    [Fact]
    public async Task ATemplateThatDoesNotEvaluateIsRefusedAtThePutAndClaimsNothing() {
        ResourceManagerCluster.ResetDoubles();

        var deployment = Deployment("unsupported");
        var template = TwoWidgets(backEndName: "[uniqueString(parameters('prefix'))]");

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = deployment.Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Put,
                Body = DeploymentBody(template, Parameters("x")),
                Caller = Alice
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("uniqueString()");
        refused.Error.Message.ShouldContain("parameters(), variables(), resourceId(), concat()");
        refused.Error.Target.ShouldBe(Deployments.TemplatePointer);

        (await cluster.Index(deployment).ResolveAsync()).IsFailure.ShouldBeTrue("a refused template claimed the name.");
    }

    [Fact]
    public async Task WhatIfSaysCreateModifyAndNoChangeWithAPropertyDiffReadAsTheCallerAndWritesNothing() {
        ResourceManagerCluster.ResetDoubles();

        // Two widgets that already exist: one the template changes, one it leaves alone.
        await CreateWidgetAsync(Widget("whatif-changed"));
        await CreateWidgetAsync(Widget("whatif-same"));

        var template = new JsonObject {
            ["resources"] = new JsonArray(
                Resource("whatif-changed", new JsonObject { ["size"] = 5 }),
                Resource("whatif-new", new JsonObject { ["size"] = 1, ["label"] = "fresh" }),
                Resource("whatif-same", new JsonObject { ["size"] = 2, ["label"] = "first" })
            )
        }.ToJsonString();

        var manager = new DeploymentManagerService(
            cluster.Manager,
            cluster.Registry,
            new SwitchableAuthorizer(),
            cluster.Grains
        );

        SwitchableAuthorizer.Checks.Clear();

        var deployment = Deployment("never-created");
        var answered = await manager.WhatIfAsync(
            new() {
                Path = deployment.Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Post,
                Action = Deployments.WhatIfAction,
                Body = DeploymentBody(template, ""),
                Caller = Alice
            },
            TestContext.Current.CancellationToken
        );

        answered.IsSuccess.ShouldBeTrue(answered.Error?.Message);
        var changes = answered.GetValueOrThrow().Changes;

        changes.Select(static x => (x.ResourceId, x.ChangeType)).ShouldBe(
            [
                (Widget("whatif-changed").Path, WhatIfChangeTypes.Modify),
                (Widget("whatif-new").Path, WhatIfChangeTypes.Create),
                (Widget("whatif-same").Path, WhatIfChangeTypes.NoChange)
            ]
        );

        // The diff: size moves, and the label the template no longer sets is taken away by the PUT.
        changes[0].Delta.ShouldBe(
            [
                new("/properties/label", WhatIfChangeTypes.PropertyDelete, "\"first\"", null),
                new("/properties/size", WhatIfChangeTypes.PropertyModify, "2", "5")
            ]
        );

        changes[1].Delta.ShouldContain(new WhatIfPropertyChange("/properties/label", WhatIfChangeTypes.PropertyCreate, null, "\"fresh\""));
        changes[2].Delta.ShouldBeEmpty();

        // The whole answer renders as the response body.
        using (var rendered = JsonDocument.Parse(answered.GetValueOrThrow().ToJson())) {
            rendered.RootElement.GetProperty("changes")[0].GetProperty("delta")[1].GetProperty("after").GetInt32().ShouldBe(5);
        }

        // ⚠ The action was checked at the deployment's address (absent, so its group) and every
        // comparison was a READ as Alice. Nothing was written: neither the new widget's name nor the
        // deployment's is claimed.
        var checks = SwitchableAuthorizer.Checks.ToList();
        checks.ShouldContain(x => x.Path == deployment.Path && x.Permission == "write");
        var reads = checks.Where(x => x.Path != deployment.Path).ToList();
        reads.Count.ShouldBe(3);
        reads.ShouldAllBe(x => x.Permission == "read");
        reads.Select(static x => x.Caller).Distinct().ShouldBe([Alice.ToString()]);

        (await cluster.Index(Widget("whatif-new")).ResolveAsync()).IsFailure.ShouldBeTrue();
        (await cluster.Index(deployment).ResolveAsync()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task WhatIfRefusesACallerWhoCannotDeployToTheGroupWithTheCanonical404() {
        ResourceManagerCluster.ResetDoubles();
        SwitchableAuthorizer.DeniedGroups["prod"] = true;

        var manager = new DeploymentManagerService(cluster.Manager, cluster.Registry, new SwitchableAuthorizer(), cluster.Grains);

        var answered = await manager.WhatIfAsync(
            new() {
                Path = Deployment("hidden").Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Post,
                Body = DeploymentBody(TwoWidgets(), Parameters("x")),
                Caller = Alice
            },
            TestContext.Current.CancellationToken
        );

        answered.IsFailure.ShouldBeTrue();
        answered.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    static JsonObject Resource(string name, JsonObject properties) =>
        new() {
            ["type"] = WidgetType,
            ["apiVersion"] = TestingProvider.V2026,
            ["name"] = name,
            ["location"] = "eu-central",
            ["properties"] = properties
        };

    async Task<WriteAccepted> PutAsync(ResourceId deployment, string template, string parameters) {
        var written = await cluster.Manager.WriteAsync(
            new() {
                Path = deployment.Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Put,
                Body = DeploymentBody(template, parameters),
                Caller = Alice
            }
        );

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        return written.GetValueOrThrow();
    }

    async Task CreateWidgetAsync(ResourceId widget) {
        var written = await cluster.Manager.WriteAsync(
            new() {
                Path = widget.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = Alice
            }
        );

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, written.GetValueOrThrow().OperationId);

        for (var i = 0; i < 5; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().State == OperationState.Succeeded) {
                return;
            }
        }

        throw new InvalidOperationException($"'{widget.Path}' did not converge.");
    }

    async Task<ResourceSnapshot> ReadAsync(ResourceId address, string apiVersion = TestingProvider.V2026) {
        var read = await cluster.Manager.ReadAsync(new() { Path = address.Path, ApiVersion = apiVersion, Caller = Alice });
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        return read.GetValueOrThrow();
    }

    /// <summary>
    ///     Drives the parent and every child it has so far until the parent is terminal — the reminder
    ///     ticks a silo would run, run by hand.
    /// </summary>
    async Task<OperationStatus> DriveToEndAsync(Guid parentId) {
        var parent = cluster.Operation(ResourceManagerCluster.Tenant, parentId);
        OperationStatus status = null!;

        for (var i = 0; i < 40; i++) {
            status = (await parent.DriveAsync()).GetValueOrThrow();

            if (status.IsTerminal) {
                return status;
            }

            foreach (var child in status.Children) {
                _ = await cluster.Operation(ResourceManagerCluster.Tenant, child).DriveAsync();
            }
        }

        return status;
    }

    static string Describe(OperationStatus status) =>
        $"{status.State}: {status.Error?.Message} — "
        + string.Join(" | ", status.Progress.TakeLast(6).Select(static x => x.ToString()));
}
