using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using CyberCloud.Kubernetes.Tunnel;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests.Tunnel;

/// <summary>
///     The agent tunnel end to end in one process: a real <see cref="TunnelExchange" /> and a real
///     <see cref="TunnelAgent" /> over a pair of pipes, with <see cref="TunnelKubeApiClient" /> on
///     top and a scripted API server underneath.
/// </summary>
/// <remarks>
///     docs/plan/09 § Cluster connections, the <c>AgentInitiated</c> row. What this pins is the
///     protocol — that every <see cref="IKubeApiClient" /> call the platform makes arrives at the
///     agent's client as the same call with the same arguments, that the answer comes back with its
///     error code intact, that requests interleave, that a heartbeat arrives without being asked
///     for, and that the two ways a tunnel dies each produce the failure the health tracker needs
///     to see. What it does not pin is any network, and <c>charts/agent/conformance.yaml § owed</c>
///     says which claims wait on one.
/// </remarks>
public sealed class TunnelEndToEndTests {
    static readonly GroupVersionKind Deployments =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnApplyCrossesTheTunnelWithItsLabelsAndComesBackWithItsOutcome() {
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        var command = Command();
        tunnel.Api.NextApply = Result<ApplyOutcome>.Success(
            new() { Result = ApplyResult.Updated, Target = command.Target, ResourceVersion = "1234" }
        );

        var outcome = await tunnel.Client.ApplyAsync(command, Ct);

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);
        outcome.GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);
        outcome.GetValueOrThrow().ResourceVersion.ShouldBe("1234");
        outcome.GetValueOrThrow().Target.ShouldBe(command.Target);
        tunnel.Exchange.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task TheAgentRefusesACommandThatLostItsMandatoryLabelsInTransit() {
        // ⚠ Sabotage on the wire, not in the builder: the builder cannot produce an unlabelled command
        // and this proves the agent would not apply one even if something between the two could.
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        var stripped = KubeCommandJson.ToJson(Command()).Replace(KubeLabels.TenantId, "x-not-a-label", StringComparison.Ordinal);

        var response = await tunnel.Exchange.ExchangeAsync(TunnelFrame.Request(0, TunnelOperations.Apply, stripped), Ct);

        response.IsSuccess.ShouldBeTrue();
        var opened = TunnelOperations.Open<ApplyOutcome>(response.GetValueOrThrow().Payload);
        opened.IsFailure.ShouldBeTrue();
        opened.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        opened.Error.Message.ShouldContain(KubeLabels.TenantId);
    }

    [Fact]
    public async Task AFailureTheApiServerGaveKeepsItsCodeAcrossTheWire() {
        // ⚠ The code is what ClusterConnectionGrain's health tracker reads: a ResourceNotFound is the
        // cluster ANSWERING, and a tunnel that flattened it to InternalError would turn every
        // "not created yet" read into evidence the cluster is down.
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        var read = await tunnel.Client.GetAsync(new() { Kind = Deployments, Namespace = "ns", Name = "main" }, Ct);

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        read.Error.Message.ShouldBe("not scripted");
        KubeFailures.MeansTheClusterAnswered(read.Error.Code).ShouldBeTrue();
    }

    [Fact]
    public async Task EveryUnaryOperationReachesTheAgentsClient() {
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        (await tunnel.Client.PingAsync(Ct)).GetValueOrThrow().ShouldBe("v1.35.0");
        tunnel.Api.Pings.ShouldBeGreaterThanOrEqualTo(1);

        (await tunnel.Client.DeleteAsync(new() { Kind = Deployments, Namespace = "ns", Name = "main" }, CascadePolicy.Foreground, Ct))
            .IsSuccess.ShouldBeTrue();

        var owner = new OwnerRef { ApiVersion = "apps/v1", Kind = "Deployment", Name = "main", Uid = "uid-main" };
        (await tunnel.Client.SetOwnerAsync(new() { Kind = Deployments, Namespace = "ns", Name = "claim" }, owner, Ct))
            .IsSuccess.ShouldBeTrue();
        tunnel.Api.OwnerChanges.Any(x => x.Owner?.Uid == "uid-main").ShouldBeTrue();

        (await tunnel.Client.SetOwnerAsync(new() { Kind = Deployments, Namespace = "ns", Name = "claim" }, null, Ct))
            .IsSuccess.ShouldBeTrue();
        tunnel.Api.OwnerChanges.Any(x => x.Owner == null).ShouldBeTrue();

        tunnel.Api.Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Deployments]);
        var kinds = await tunnel.Client.DiscoverNamespacedKindsAsync(Ct);
        kinds.IsSuccess.ShouldBeTrue(kinds.Error?.Message);
        kinds.GetValueOrThrow().ShouldBe(tunnel.Api.Discovery.GetValueOrThrow());

        tunnel.Api.Pages.Enqueue(Result<ListPage>.Success(new(["{\"a\":1}", "{\"b\":2}"], "77", "next-page")));
        var page = await tunnel.Client.ListAsync(Deployments, "ns", "app=x", "42", null, 100, Ct);
        page.IsSuccess.ShouldBeTrue(page.Error?.Message);
        page.GetValueOrThrow().Items.ShouldBe(["{\"a\":1}", "{\"b\":2}"]);
        page.GetValueOrThrow().ResourceVersion.ShouldBe("77");
        page.GetValueOrThrow().ContinueToken.ShouldBe("next-page");
        tunnel.Api.Lists.ShouldContain(x => x.Kind == Deployments && x.Namespace == "ns" && x.Selector == "app=x" && x.ResourceVersion == "42");
    }

    [Fact]
    public void TheWireHasASpellingForEveryUnaryMemberOfTheClient() {
        // Every IKubeApiClient member except the one stream (WatchAsync) and Dispose.
        var unary = typeof(IKubeApiClient)
            .GetMethods()
            .Where(x => x.Name != nameof(IKubeApiClient.WatchAsync) && x.Name != nameof(IDisposable.Dispose))
            .Select(x => x.Name)
            .Order()
            .ToArray();

        unary.Length.ShouldBe(TunnelOperations.All.Count, string.Join(", ", unary));
    }

    [Fact]
    public async Task ASlowRequestDoesNotHoldAFastOne() {
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        var gate = new TaskCompletionSource();
        tunnel.Api.ThrowOnApply = null;
        tunnel.Api.PingResult = Result<string>.Success("v-fast");

        // Applies wait on the gate; pings do not. Two applies in flight, then a ping: the ping must
        // come back while both applies are still open.
        tunnel.Api.BeforeApply = () => gate.Task;

        var first = tunnel.Client.ApplyAsync(Command(), Ct);
        var second = tunnel.Client.ApplyAsync(Command(), Ct);

        var ping = await tunnel.Client.PingAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        ping.GetValueOrThrow().ShouldBe("v-fast");
        tunnel.Exchange.Pending.ShouldBe(2);

        gate.SetResult();
        (await first).IsSuccess.ShouldBeTrue();
        (await second).IsSuccess.ShouldBeTrue();
        tunnel.Exchange.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task TheAgentHeartbeatsWithoutBeingAskedAndAdoptsTheWelcomedInterval() {
        await using var tunnel = new InProcessTunnel(heartbeat: TimeSpan.FromMilliseconds(100));
        tunnel.Start();

        var first = await tunnel.FirstHeartbeatAsync();
        var body = TunnelCodec.Deserialize<HeartbeatBody>(first.Payload).GetValueOrThrow();
        body.AgentVersion.ShouldBe("test-agent");
        body.KubernetesVersion.ShouldBe("v1.35.0");

        await tunnel.WelcomeAsync(new() { SessionId = Guid.NewGuid(), HeartbeatSeconds = 1 }, Ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (tunnel.Agent.Welcome is null && DateTime.UtcNow < deadline) {
            await Task.Delay(20, Ct);
        }

        tunnel.Agent.Welcome.ShouldNotBeNull();
        tunnel.Agent.Welcome.HeartbeatSeconds.ShouldBe(1);
    }

    [Fact]
    public async Task ARequestTheAgentNeverAnswersTimesOutWithACodeTheHealthTrackerReadsAsUnreachable() {
        await using var tunnel = new InProcessTunnel(requestTimeout: TimeSpan.FromMilliseconds(300));
        tunnel.Start();

        var never = new TaskCompletionSource();
        tunnel.Api.BeforeApply = () => never.Task;

        var outcome = await tunnel.Client.ApplyAsync(Command(), Ct);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.OperationTimeout);
        KubeFailures.MeansTheClusterAnswered(outcome.Error.Code).ShouldBeFalse();
        tunnel.Exchange.Pending.ShouldBe(0);

        never.SetResult();
    }

    [Fact]
    public async Task AKilledAgentFailsEveryPendingRequestRatherThanLeavingItOpen() {
        await using var tunnel = new InProcessTunnel(requestTimeout: TimeSpan.FromSeconds(30));
        tunnel.Start();

        var never = new TaskCompletionSource();
        tunnel.Api.BeforeApply = () => never.Task;

        var pending = tunnel.Client.ApplyAsync(Command(), Ct);

        // Give the request time to reach the agent, then pull the plug on the agent's side.
        await Task.Delay(100, Ct);
        tunnel.Exchange.Pending.ShouldBe(1);

        await tunnel.KillAgentAsync();
        tunnel.Exchange.FailAll("the socket closed");

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.ProvisioningFailed);
        outcome.Error.Message.ShouldContain("the socket closed");

        never.SetResult();
    }

    [Fact]
    public async Task AnOperationTheAgentDoesNotServeIsRefusedByNameRatherThanForwarded() {
        await using var tunnel = new InProcessTunnel();
        tunnel.Start();

        var response = await tunnel.Exchange.ExchangeAsync(TunnelFrame.Request(0, "exec", "{}"), Ct);

        response.IsSuccess.ShouldBeTrue();
        var opened = TunnelOperations.Open(response.GetValueOrThrow().Payload);
        opened.IsFailure.ShouldBeTrue();
        opened.Error!.Message.ShouldContain("'exec' is not an operation this agent serves");
    }

    [Fact]
    public void WatchIsRefusedByNameOnTheTunnelClient() {
        var client = new TunnelKubeApiClient(Guid.NewGuid(), new NeverRoute());

        var refused = Should.Throw<NotSupportedException>(() => client.WatchAsync(Deployments, "ns", "", "1"));
        refused.Message.ShouldContain("informers-do-not-cross-the-tunnel");
    }

    [Fact]
    public void AFrameRoundTripsThroughTheCodecAndAnUnknownKindIsRefused() {
        var frame = TunnelFrame.Request(7, TunnelOperations.Get, "{\"name\":\"x\"}");
        var decoded = TunnelCodec.Decode(TunnelCodec.Encode(frame));

        decoded.GetValueOrThrow().ShouldBe(frame);

        TunnelCodec.Decode("{\"id\":1}"u8).IsFailure.ShouldBeTrue();
        TunnelCodec.Decode("not json"u8).IsFailure.ShouldBeTrue();
    }

    static KubeCommand Command() {
        var tenant = Guid.Parse("9f2c1b7e-0000-4000-8000-000000000001");

        var id = new ResourceId(
            tenant,
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "main",
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(tenant)
            .WithResourceId(id)
            .WithKind(Deployments)
            .InNamespace("ns")
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .Build();
    }

    sealed class NeverRoute : ITunnelRoute {
        public Task<Result<TunnelFrame>> ExchangeAsync(TunnelFrame request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not expected");
    }
}
