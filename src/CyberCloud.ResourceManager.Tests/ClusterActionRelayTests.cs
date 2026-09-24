using CyberCloud.ResourceManager.Actions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     An action on a type that needs a cluster, dispatched from a process that can't reach one: it
///     goes to a silo, and nowhere else does.
/// </summary>
/// <remarks>
///     ⚠ <b>Found by the review of #22.</b> The gateway runs a synchronous action itself and composes
///     no cluster connection, so every action on a <c>RequiresCluster</c> type was refused there by
///     name — the cloud console's <c>connect</c> among them — while every harness passed, because each
///     one handed its dispatcher a direct connection.
/// </remarks>
/// <param name="cluster">The suite's silo, which runs a relayed action through its own dispatcher.</param>
[Collection(ResourceManagerSuite.Name)]
public sealed class ClusterActionRelayTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task AnActionThatNeedsAClusterNobodyHereReachesIsRelayedAndOnlyThatOne() {
        var relay = new RecordingRelay();
        var dispatcher = new ActionDispatcher(Handlers(), new NoClusterConnectionFactory(), new UnavailableSecretResolver(), relay: relay);
        var (registration, action) = Restart();
        var id = ResourceManagerCluster.Address("relayed");
        var caller = ResourceManagerCluster.Caller();

        using var body = JsonDocument.Parse("""{"force":true}""");

        var relayed = await dispatcher.InvokeAsync(
            id,
            registration with { RequiresCluster = true },
            action,
            Input(id),
            body.RootElement,
            caller,
            TestContext.Current.CancellationToken
        );

        relayed.GetValueOrThrow().ShouldBe(RecordingRelay.Answer);
        var (sentId, sentAction, sentBody, sentCaller) = relay.Calls.ShouldHaveSingleItem();
        sentId.ShouldBe(id);
        sentAction.ShouldBe("restart");
        sentBody.ShouldContain("force");
        sentCaller.ShouldBe(caller);

        // A type that needs no cluster runs here, as it always has.
        RestartHandler.Reset();
        var local = await dispatcher.InvokeAsync(id, registration, action, Input(id), body.RootElement, caller, TestContext.Current.CancellationToken);

        local.GetValueOrThrow().ShouldContain("restarted");
        RestartHandler.Invocations.ShouldBe(1);
        relay.Calls.Count.ShouldBe(1, "an action this process can serve went to the relay");
    }

    [Fact]
    public async Task TheSilosDispatcherNeverRelaysAgain() {
        // ⚠ What stops a silo that can't reach the cluster from bouncing the action back through the
        // relay: the worker calls InvokeHereAsync, which refuses by name as the dispatcher always has.
        var relay = new RecordingRelay();
        var dispatcher = new ActionDispatcher(Handlers(), new NoClusterConnectionFactory(), new UnavailableSecretResolver(), relay: relay);
        var (registration, action) = Restart();
        var id = ResourceManagerCluster.Address("not-relayed");

        using var body = JsonDocument.Parse("{}");

        var here = await dispatcher.InvokeHereAsync(
            id,
            registration with { RequiresCluster = true },
            action,
            Input(id),
            body.RootElement,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        here.IsFailure.ShouldBeTrue();
        here.Error!.Message.ShouldContain("declares RequiresCluster");
        relay.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheWorkerRunsTheActionOnTheSiloWithTheSilosHandlers() {
        ResourceManagerCluster.ResetDoubles();
        RestartHandler.Reset();

        var id = ResourceManagerCluster.Address("worker-runs");

        var answered = await new GrainClusterActionRelay(cluster.Grains).InvokeAsync(
            id,
            "restart",
            Input(id),
            default,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        answered.IsSuccess.ShouldBeTrue(answered.Error?.Message);
        answered.GetValueOrThrow().ShouldContain("restarted");
        RestartHandler.Invocations.ShouldBe(1, "the silo's handler was never reached");
    }

    [Fact]
    public async Task TheWorkerRefusesAnotherTenantsResource() {
        // The connection grain trusts the worker's tenant, so a resource from another tenant run there
        // would reach that tenant's cluster as this one.
        RestartHandler.Reset();

        var id = ResourceManagerCluster.Address("elsewhere", tenant: ResourceManagerCluster.OtherTenant);

        var refused = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IClusterActionGrain>(ClusterActionKeys.Worker)
            .InvokeAsync(id, "restart", Input(id), "{}", ResourceManagerCluster.Caller());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        RestartHandler.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task TheWorkerRefusesAnActionItsRegistryDoesNotDeclare() {
        var id = ResourceManagerCluster.Address("undeclared");

        var refused = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IClusterActionGrain>(ClusterActionKeys.Worker)
            .InvokeAsync(id, "no-such-action", Input(id), "{}", ResourceManagerCluster.Caller());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InternalError);
        refused.Error.Message.ShouldContain("no-such-action");
    }

    (ResourceTypeRegistration Registration, ActionRegistration Action) Restart() {
        cluster.Registry.TryGetType(ConformingReconciler.TypeName, out var registration).ShouldBeTrue();
        registration.TryGetAction("restart", out var action).ShouldBeTrue();
        return (registration, action);
    }

    static ReconcileInput Input(ResourceId id) =>
        new() { Path = id.Path, ResourceId = Guid.NewGuid(), ApiVersion = TestingProvider.V2026, Desired = TestingProvider.Body() };

    static ServiceProvider Handlers() {
        var services = new ServiceCollection();
        services.AddSingleton<RestartHandler>();
        return services.BuildServiceProvider();
    }

    sealed class RecordingRelay : IClusterActionRelay {
        public const string Answer = """{"relayed":true}""";

        public List<(ResourceId Id, string Action, string Body, CallerContext? Caller)> Calls { get; } = [];

        public Task<Result<string>> InvokeAsync(
            ResourceId id,
            string action,
            ReconcileInput input,
            JsonElement body,
            CallerContext? caller,
            CancellationToken cancellationToken = default
        ) {
            Calls.Add((id, action, body.GetRawText(), caller));
            return Task.FromResult(Result<string>.Success(Answer));
        }
    }
}
