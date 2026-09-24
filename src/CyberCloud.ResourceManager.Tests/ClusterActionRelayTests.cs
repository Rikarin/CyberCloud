using CyberCloud.ResourceManager.Actions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            caller: caller,
            cancellationToken: TestContext.Current.CancellationToken
        );

        relayed.GetValueOrThrow().ShouldBe(RecordingRelay.Answer);
        var sent = relay.Calls.ShouldHaveSingleItem();
        sent.Id.ShouldBe(id);
        sent.Action.ShouldBe("restart");
        sent.Body.ShouldContain("force");
        sent.Caller.ShouldBe(caller);

        // A type that needs no cluster runs here, as it always has.
        RestartHandler.Reset();
        var local = await dispatcher.InvokeAsync(
            id,
            registration,
            action,
            Input(id),
            body.RootElement,
            caller: caller,
            cancellationToken: TestContext.Current.CancellationToken
        );

        local.GetValueOrThrow().ShouldContain("restarted");
        RestartHandler.Invocations.ShouldBe(1);
        relay.Calls.Count.ShouldBe(1, "an action this process can serve went to the relay");
    }

    [Fact]
    public async Task ARelayedActionSaysWhetherItsCallerMayCreateAndWhoItsParentIs() {
        // ⚠ Found by the second review of #22, against master's #30: the manager hands a synchronous
        // action a creator bound to its caller, and a relay that dropped it would run a relayed
        // `recover` on a RequiresCluster vault with the refusing one. The object can't cross; that it
        // existed, and the parent, do.
        var relay = new RecordingRelay();
        var dispatcher = new ActionDispatcher(Handlers(), new NoClusterConnectionFactory(), new UnavailableSecretResolver(), relay: relay);
        var (registration, action) = Restart();
        var id = ResourceManagerCluster.Address("relayed-creator");
        var parent = ResourceManagerCluster.Address("relayed-parent");

        using var body = JsonDocument.Parse("{}");

        (await dispatcher.InvokeAsync(
            id,
            registration with { RequiresCluster = true },
            action,
            Input(id),
            body.RootElement,
            new StubCreator(),
            ResourceManagerCluster.Caller(),
            parent,
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        (await dispatcher.InvokeAsync(
            id,
            registration with { RequiresCluster = true },
            action,
            Input(id),
            body.RootElement,
            new RefusingResourceCreator(),
            ResourceManagerCluster.Caller(),
            cancellationToken: TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        relay.Calls.Count.ShouldBe(2);
        relay.Calls[0].CreatesAsCaller.ShouldBeTrue("a creator the manager handed over was dropped at the relay");
        relay.Calls[0].Parent.ShouldBe(parent);
        relay.Calls[1].CreatesAsCaller.ShouldBeFalse("the refusing default was relayed as a creator");
        relay.Calls[1].Parent.ShouldBeNull();
    }

    [Fact]
    public async Task ARelayedActionCreatesOnTheSiloAsItsCallerAndOnlyWhenTheManagerSaidSo() {
        // The silo rebuilds the creator for the caller it was sent, and the create goes through the
        // silo manager's whole write path: CloneHandler is registered there as it would be for a vault's
        // `recover`. The gauge's `write` is what a relayed clone is authorized against.
        ResourceManagerCluster.ResetDoubles();

        var source = await CreateAsync("relay-clone-source");
        var relay = new GrainClusterActionRelay(cluster.Grains);
        var bob = ResourceManagerCluster.Caller(subject: "bob");
        using var target = JsonDocument.Parse(new JsonObject { ["name"] = "relay-clone-target" }.ToJsonString());
        using var refusedTarget = JsonDocument.Parse(new JsonObject { ["name"] = "relay-clone-refused" }.ToJsonString());

        var created = await relay.InvokeAsync(
            source,
            "clone",
            Input(source),
            target.RootElement,
            bob,
            null,
            true,
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        using (var response = JsonDocument.Parse(created.GetValueOrThrow())) {
            var status = await DriveAsync(Guid.Parse(response.RootElement.GetProperty("operationId").GetString()!));
            status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);
        }

        var read = await cluster.Manager.ReadAsync(
            new() {
                Path = ResourceManagerCluster.Address("relay-clone-target").Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().CreatedBy.ShouldContain("bob", Case.Sensitive, "the relayed create was not the caller's write");

        // Without the manager's say-so the silo hands out nothing, as the gateway wouldn't have.
        var refused = await relay.InvokeAsync(
            source,
            "clone",
            Input(source),
            refusedTarget.RootElement,
            bob,
            null,
            false,
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("the silo built a creator the manager never handed over");
        refused.Error!.Message.ShouldContain("carries no resource creator");
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
            caller: ResourceManagerCluster.Caller(),
            cancellationToken: TestContext.Current.CancellationToken
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
            null,
            false,
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
            .InvokeAsync(id, "restart", Input(id), "{}", ResourceManagerCluster.Caller(), null, false);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        RestartHandler.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task TheWorkerRefusesAnActionItsRegistryDoesNotDeclare() {
        var id = ResourceManagerCluster.Address("undeclared");

        var refused = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IClusterActionGrain>(ClusterActionKeys.Worker)
            .InvokeAsync(id, "no-such-action", Input(id), "{}", ResourceManagerCluster.Caller(), null, false);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InternalError);
        refused.Error.Message.ShouldContain("no-such-action");
    }

    (ResourceTypeRegistration Registration, ActionRegistration Action) Restart() {
        cluster.Registry.TryGetType(ConformingReconciler.TypeName, out var registration).ShouldBeTrue();
        registration.TryGetAction("restart", out var action).ShouldBeTrue();
        return (registration, action);
    }

    async Task<ResourceId> CreateAsync(string name) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address(name).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        (await DriveAsync(accepted.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);
        return ResourceManagerCluster.Address(name).WithId(accepted.GetValueOrThrow().Resource.Id);
    }

    async Task<OperationStatus> DriveAsync(Guid operationId) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < 20; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        return last!;
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

        public List<(ResourceId Id, string Action, string Body, CallerContext Caller, ResourceId? Parent, bool CreatesAsCaller)> Calls { get; } = [];

        public Task<Result<string>> InvokeAsync(
            ResourceId id,
            string action,
            ReconcileInput input,
            JsonElement body,
            CallerContext caller,
            ResourceId? parent,
            bool createsAsCaller,
            CancellationToken cancellationToken = default
        ) {
            Calls.Add((id, action, body.GetRawText(), caller, parent, createsAsCaller));
            return Task.FromResult(Result<string>.Success(Answer));
        }
    }

    /// <summary>A creator the dispatcher only has to see; nothing here calls it.</summary>
    sealed class StubCreator : IResourceCreator {
        public Task<Result<ResourceCreated>> CreateAsync(
            ResourceTypeName type,
            string name,
            string apiVersion,
            string body,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException("A relayed action's creator is rebuilt on the silo, never called here.");
    }
}
