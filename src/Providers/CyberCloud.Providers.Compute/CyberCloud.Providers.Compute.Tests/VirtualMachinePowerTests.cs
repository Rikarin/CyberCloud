using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>
///     The three power actions, and the sequence the shared suite cannot drive: stop, reconcile, start.
/// </summary>
public sealed class VirtualMachinePowerTests {
    [Fact]
    public async Task StopHaltsTheObjectAndTheNextReconcileLeavesItHalted() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var handler = new VirtualMachinePowerHandler();
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var target = VirtualMachines.VirtualMachineRef(ReconcileDriver.NamespaceFor(address), "web");
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        await reconciler.ReconcileAsync(
            Compute.Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        var stopped = await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StopAction),
            TestContext.Current.CancellationToken
        );

        stopped.IsSuccess.ShouldBeTrue(stopped.Error?.Message);
        VirtualMachines.RunStrategyOf(connection.Objects[RecordingConnection.Key(target)])
            .ShouldBe(VirtualMachines.RunHalted);

        using var answer = JsonDocument.Parse(stopped.GetValueOrThrow());
        VirtualMachines.PowerResponse.Validate(answer.RootElement)
            .IsSuccess.ShouldBeTrue("the handler's body does not match the response shape it publishes");
        answer.RootElement.GetProperty("runStrategyBefore").GetString().ShouldBe(VirtualMachines.RunAlways);
        answer.RootElement.GetProperty("runStrategy").GetString().ShouldBe(VirtualMachines.RunHalted);

        // ⚠ THE SEQUENCE THE DESIGN EXISTS FOR: a PUT with an unrelated change after a stop.
        using var retagged = JsonDocument.Parse(VirtualMachineTags(VirtualMachines.Body(Compute.ClusterId)));
        await reconciler.ReconcileAsync(
            Compute.Context(connection, address, retagged.RootElement),
            TestContext.Current.CancellationToken
        );

        VirtualMachines.RunStrategyOf(connection.Objects[RecordingConnection.Key(target)])
            .ShouldBe(
                VirtualMachines.RunHalted,
                "a reconcile pass booted a machine its tenant had stopped"
            );

        // The handler's apply is the WHOLE render, under the reconciler's own field manager — a
        // partial object would prune every other field it owns, and a second manager would conflict on
        // the reconcile-hash annotation.
        var powerApply = connection.Applied.Where(static x => x.Target.Kind.Kind == "VirtualMachine").ToList()[1];
        powerApply.FieldManager.ShouldBe(connection.Applied[0].FieldManager);
        Compute.Spec(powerApply.Body)["template"].ShouldNotBeNull();

        var started = await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StartAction),
            TestContext.Current.CancellationToken
        );
        started.IsSuccess.ShouldBeTrue(started.Error?.Message);
        VirtualMachines.RunStrategyOf(connection.Objects[RecordingConnection.Key(target)])
            .ShouldBe(VirtualMachines.RunAlways);
    }

    [Fact]
    public async Task StoppingAStoppedMachineIsANoOpThatStillAnswers() {
        var (connection, address, body) = await Provisioned();
        var handler = new VirtualMachinePowerHandler();

        await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StopAction),
            TestContext.Current.CancellationToken
        );
        var again = await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StopAction),
            TestContext.Current.CancellationToken
        );

        again.IsSuccess.ShouldBeTrue(
            "a retry of a power action is a 200 that changed nothing, not an error the generated clients have to special-case"
        );
        JsonDocument.Parse(again.GetValueOrThrow())
            .RootElement.GetProperty("runStrategyBefore")
            .GetString()
            .ShouldBe(VirtualMachines.RunHalted);
    }

    [Fact]
    public async Task RestartDeletesTheInstanceAndRefusesOnAHaltedMachine() {
        var (connection, address, body) = await Provisioned();
        var handler = new VirtualMachinePowerHandler();
        var ns = ReconcileDriver.NamespaceFor(address);
        var instance = VirtualMachines.InstanceRef(ns, "web");

        connection.Objects[RecordingConnection.Key(instance)] = """{"kind":"VirtualMachineInstance"}""";

        var restarted = await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.RestartAction),
            TestContext.Current.CancellationToken
        );

        restarted.IsSuccess.ShouldBeTrue(restarted.Error?.Message);
        connection.Deleted.ShouldBe([instance]);
        connection.Objects.ContainsKey(RecordingConnection.Key(VirtualMachines.VirtualMachineRef(ns, "web")))
            .ShouldBeTrue("a restart deleted the machine rather than its instance");

        // Between two boots there is no instance, and that is a restart already under way, not an error.
        (await handler.InvokeAsync(
                Compute.Action(connection, address, body.RootElement, VirtualMachines.RestartAction),
                TestContext.Current.CancellationToken
            ))
            .IsSuccess.ShouldBeTrue();

        await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StopAction),
            TestContext.Current.CancellationToken
        );
        var refused = await handler.InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.RestartAction),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("start");
    }

    [Fact]
    public async Task AnActionOnAMachineNotYetAppliedIsRefusedRatherThanCreating() {
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        var result = await new VirtualMachinePowerHandler().InvokeAsync(
            Compute.Action(connection, address, body.RootElement, VirtualMachines.StartAction),
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        connection.Applied.ShouldBeEmpty("an action never creates — docs/plan/08 § The write path, end to end");
    }

    [Fact]
    public async Task AConflictOnTheObjectIsReportedAndNotForced() {
        var (connection, address, body) = await Provisioned();
        var conflicting = new RecordingConnection { ConflictField = "spec.runStrategy" };

        foreach (var (key, value) in connection.Objects) {
            conflicting.Objects[key] = value;
        }

        var result = await new VirtualMachinePowerHandler().InvokeAsync(
            Compute.Action(conflicting, address, body.RootElement, VirtualMachines.StopAction),
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.Conflict);
        result.Error.Message.ShouldContain("spec.runStrategy");
    }

    [Fact]
    public void TheHandlerServesEveryActionOnTheTypeAndTheProviderDeclaresExactlyThree() {
        var handler = new VirtualMachinePowerHandler();

        handler.Type.ShouldBe(VirtualMachines.Type);
        handler.Action.ShouldBeEmpty(
            "one handler switches on ActionContext.Action, as the seam documents for listKeys beside regenerateKeys"
        );

        var registry = CyberCloud.ResourceManager.Registry.ProviderRegistry.Build([new ComputeProvider()]);
        registry.TryGetType(VirtualMachines.Type, out var registration).ShouldBeTrue();

        foreach (var action in new[] {
                     VirtualMachines.StartAction, VirtualMachines.StopAction, VirtualMachines.RestartAction
                 }) {
            registration.TryGetAction(action, out var declared).ShouldBeTrue(action);
            declared.HandlerType.ShouldBe(typeof(VirtualMachinePowerHandler));
            declared.LongRunning.ShouldBeFalse(
                "a long-running action re-runs the reconciler, which cannot see which action was asked for"
            );
            declared.Response.ShouldBe(VirtualMachines.PowerResponse);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static async Task<(RecordingConnection Connection, ResourceId Address, JsonDocument Body)> Provisioned() {
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        await new VirtualMachineReconciler(new FixedClock()).ReconcileAsync(
            Compute.Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        return (connection, address, body);
    }

    static string VirtualMachineTags(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["tags"] = new JsonObject { ["env"] = "prod" };
        return node.ToJsonString();
    }
}
