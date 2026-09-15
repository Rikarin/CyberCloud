using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using CyberCloud.Kubernetes.Tunnel;
using Shouldly;
using System.IO.Pipes;

namespace CyberCloud.Kubernetes.Tests.Tunnel;

/// <summary>
///     The grain path of an agent-initiated connection, against a fake cluster: a tenant's reconciler
///     reaches <see cref="ClusterConnectionGrain" />, which reaches <see cref="AgentTunnelGrain" />
///     through the production factory's <see cref="TunnelKubeApiClient" />, which reaches a real
///     <see cref="TunnelAgent" /> through a real <see cref="AgentTunnelRelay" /> — and the agent
///     answers from a <see cref="RecordingApiClient" />.
/// </summary>
/// <remarks>
///     <para>
///         What stands in for a network: this test process is the gateway (an Orleans client holding
///         the socket) and a pair of pipes is the WebSocket. Everything between the reacher grain and
///         the fake API server is the shipping code.
///     </para>
///     <para>
///         ⚠ <b>The tenancy assertions are the reason this suite exists.</b> docs/plan/09 § Cluster
///         connections: "a compromised agent must not be able to act as another tenant". The
///         connection grain's owner check is <c>ClusterConnectionTenancyTests</c>' business; what is
///         pinned here is the tunnel grain's own gate — that a tenant grain cannot exchange down the
///         tunnel directly, that a spent enrollment token admits nobody twice, and that a revoked
///         cluster admits nobody at all.
///     </para>
/// </remarks>
[Collection(KubeClusterSuite.Name)]
public sealed class AgentTunnelGrainTests(KubeTestCluster cluster) {
    static readonly GroupVersionKind Deployments =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnApplyFromTheOwningTenantTravelsThroughTheConnectionGrainAndTheTunnelToTheAgentsCluster() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        await using var agent = await ConnectAgentAsync(clusterId, enrollment.Plaintext);
        agent.Session.EnrollmentConsumed.ShouldBeTrue();

        await AttachAsync(clusterId, owner);

        var outcome = await cluster.Reacher(owner).ReachApplyAsync(clusterId, CommandFor(owner));

        outcome.ShouldBe(nameof(ApplyResult.Created));
        agent.Api.Applies.ShouldHaveSingleItem().TenantId.ShouldBe(owner);

        // And the connection grain counted the answer as the cluster being up.
        (await cluster.Reacher(owner).ReachHealthAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Healthy));
    }

    [Fact]
    public async Task TheFirstHeartbeatIsRecordedAndReadableByTheOwner() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        await using var agent = await ConnectAgentAsync(clusterId, enrollment.Plaintext);

        var status = await WaitForHeartbeatAsync(clusterId);

        status.Armed.ShouldBeTrue();
        status.Connected.ShouldBeTrue();
        status.HasHeartbeated.ShouldBeTrue();
        status.AgentVersion.ShouldBe("test-agent");
        status.KubernetesVersion.ShouldBe("v1.35.0");
        status.EnrollmentOpen.ShouldBeFalse("the token was spent on this connection");

        (await cluster.Reacher(owner).ReachTunnelStatusAsync(clusterId)).ShouldBe("armed");
    }

    [Fact]
    public async Task ATenantGrainCannotExchangeDownAnotherTenantsTunnel() {
        // ⚠ THE LOAD-BEARING ONE. Tenant → null-tenant edges are allowed platform-wide, so a tenant
        // grain CAN address this key. Only the grain's own check stops it.
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        await using var agent = await ConnectAgentAsync(clusterId, enrollment.Plaintext);
        await WaitForHeartbeatAsync(clusterId);
        var pingsBefore = agent.Api.Pings;

        (await cluster.Reacher(other).ReachTunnelExchangeAsync(clusterId))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>", "another tenant must be refused with the canonical 404");

        // ⚠ Even the OWNER is refused on the direct route: the connection grain is the only caller,
        // because it is the only one that has made the owner check first.
        (await cluster.Reacher(owner).ReachTunnelExchangeAsync(clusterId))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>", "the tunnel is reached through the connection grain or not at all");

        // And the other tenant cannot read the status either.
        (await cluster.Reacher(other).ReachTunnelStatusAsync(clusterId)).ShouldBe($"<{ErrorCode.ResourceNotFound}>");

        agent.Api.Pings.ShouldBe(pingsBefore, "no ping reached the agent from either refused route");
    }

    [Fact]
    public async Task AnotherTenantsReconcilerIsRefusedByTheConnectionGrainBeforeTheTunnelIsReached() {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        await using var agent = await ConnectAgentAsync(clusterId, enrollment.Plaintext);
        await AttachAsync(clusterId, owner);

        (await cluster.Reacher(other).ReachApplyAsync(clusterId, CommandFor(other)))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");

        agent.Api.Applies.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheEnrollmentTokenAdmitsExactlyOneConnectionAndTheCredentialAdmitsTheNext() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        string credential;
        await using (var first = await ConnectAgentAsync(clusterId, enrollment.Plaintext)) {
            first.Session.EnrollmentConsumed.ShouldBeTrue();
            credential = await first.CredentialAsync();
            credential.ShouldStartWith(AgentCredentials.CredentialPrefix);
        }

        // The token again: spent.
        var replay = await new AgentTunnelRelay(cluster.Grains).AdmitAsync(clusterId, enrollment.Plaintext, "replay");
        replay.IsFailure.ShouldBeTrue();
        replay.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        // The credential: admitted, and no second credential is minted.
        await using var second = await ConnectAgentAsync(clusterId, credential);
        second.Session.EnrollmentConsumed.ShouldBeFalse();
        (await second.WelcomeAsync()).Credential.ShouldBeNull();

        // A wrong credential of the right shape: the same refusal, with the same message.
        var forged = await new AgentTunnelRelay(cluster.Grains).AdmitAsync(clusterId, AgentCredentials.MintCredential().Plaintext, "forged");
        forged.IsFailure.ShouldBeTrue();
        forged.Error!.Message.ShouldBe(replay.Error.Message, "a stolen token must not be able to tell which refusal it got");
    }

    [Fact]
    public async Task AnUnarmedClusterAdmitsNobodyAndAJwtIsRefusedWithoutAGrainCall() {
        var relay = new AgentTunnelRelay(cluster.Grains);

        var unarmed = await relay.AdmitAsync(Guid.NewGuid(), AgentCredentials.MintEnrollment().Plaintext, "x");
        unarmed.IsFailure.ShouldBeTrue();
        unarmed.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        var jwt = await relay.AdmitAsync(Guid.NewGuid(), "eyJhbGciOiJSUzI1NiJ9.e30.sig", "x");
        jwt.IsFailure.ShouldBeTrue();
        jwt.Error!.Message.ShouldContain("not an agent credential");
    }

    [Fact]
    public async Task WithNoAgentConnectedTheConnectionGrainFailsTheCallAndGoesDegradedAfterTheWindow() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        await ArmAsync(clusterId, owner);
        await AttachAsync(clusterId, owner);

        var outcome = await cluster.Reacher(owner).ReachApplyAsync(clusterId, CommandFor(owner));
        outcome.ShouldBe($"<{ErrorCode.ProvisioningFailed}>");

        // docs/plan/09 § Cluster connections: a cluster that has never answered and just failed is
        // Degraded — the tracker does not wait out the window for a cluster with no last success —
        // and a Degraded cluster SUSPENDS applies rather than failing them.
        (await cluster.Reacher(owner).ReachPingAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Degraded));
        (await cluster.Reacher(owner).ReachApplyAsync(clusterId, CommandFor(owner))).ShouldBe(nameof(ApplyResult.Suspended));
    }

    [Fact]
    public async Task RevokingDropsTheSessionAndTheCredentialStopsWorking() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        var enrollment = await ArmAsync(clusterId, owner);

        await using var agent = await ConnectAgentAsync(clusterId, enrollment.Plaintext);
        var credential = await agent.CredentialAsync();

        (await Tunnel(clusterId).RevokeAsync()).IsSuccess.ShouldBeTrue();

        var reason = await agent.EndedAsync();
        reason.ShouldContain("revoked");

        var again = await new AgentTunnelRelay(cluster.Grains).AdmitAsync(clusterId, credential, "again");
        again.IsFailure.ShouldBeTrue();

        var status = (await Tunnel(clusterId).GetStatusAsync()).GetValueOrThrow();
        status.Revoked.ShouldBeTrue();
        status.Connected.ShouldBeFalse();

        // Idempotent, because a delete pass runs until it converges.
        (await Tunnel(clusterId).RevokeAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ReArmingForAnotherTenantIsRefused() {
        var owner = Guid.NewGuid();
        var clusterId = Guid.NewGuid();
        await ArmAsync(clusterId, owner);

        var stolen = await Tunnel(clusterId).ArmAsync(
            new() {
                OwningTenantId = Guid.NewGuid(),
                EnrollmentHash = AgentCredentials.MintEnrollment().Hash,
                ExpiresAt = SharedTestClock.Instance.UtcNow.AddHours(1)
            }
        );

        stolen.IsFailure.ShouldBeTrue();
        stolen.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The gateway, played by this process ──────────────────────────────────────────────────────

    IAgentTunnelGrain Tunnel(Guid clusterId) =>
        cluster.Grains.GetGrain<IAgentTunnelGrain>(GrainKeys.ClusterConnection(clusterId));

    async Task<MintedCredential> ArmAsync(Guid clusterId, Guid owner) {
        var minted = AgentCredentials.MintEnrollment();

        var armed = await Tunnel(clusterId).ArmAsync(
            new() {
                OwningTenantId = owner,
                EnrollmentHash = minted.Hash,
                ExpiresAt = SharedTestClock.Instance.UtcNow + AgentCredentials.EnrollmentLifetime
            }
        );

        armed.IsSuccess.ShouldBeTrue(armed.Error?.Message);
        return minted;
    }

    async Task AttachAsync(Guid clusterId, Guid owner) {
        var attached = await cluster.Connection(clusterId).AttachAsync(
            new() {
                ClusterId = clusterId,
                OwningTenantId = owner,
                Kind = ClusterConnectionKind.AgentInitiated,
                Endpoint = "agent://" + clusterId.ToString("D"),
                DisplayName = "byo"
            }
        );

        attached.IsSuccess.ShouldBeTrue(attached.Error?.Message);
    }

    async Task<ConnectedAgent> ConnectAgentAsync(Guid clusterId, string presented) {
        var admitted = await new AgentTunnelRelay(cluster.Grains).AdmitAsync(clusterId, presented, "test-agent");
        admitted.IsSuccess.ShouldBeTrue(admitted.Error?.Message);

        var agent = new ConnectedAgent(admitted.GetValueOrThrow());
        agent.Start();
        return agent;
    }

    async Task<AgentTunnelStatus> WaitForHeartbeatAsync(Guid clusterId) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline) {
            var status = (await Tunnel(clusterId).GetStatusAsync()).GetValueOrThrow();

            if (status.HasHeartbeated && status.KubernetesVersion.Length > 0) {
                return status;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("the agent's heartbeat never reached the grain");
    }

    static KubeCommand CommandFor(Guid tenantId) {
        var id = new ResourceId(
            tenantId,
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "main",
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(tenantId)
            .WithResourceId(id)
            .WithKind(Deployments)
            .InNamespace("ns")
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .Build();
    }

    /// <summary>
    ///     An admitted session with a real agent on the far side of a pair of pipes — the gateway's
    ///     socket and the tenant's pod, both in this process.
    /// </summary>
    sealed class ConnectedAgent : IAsyncDisposable {
        readonly AnonymousPipeServerStream gatewayToAgent = new(PipeDirection.Out);
        readonly AnonymousPipeServerStream agentToGateway = new(PipeDirection.Out);
        readonly AnonymousPipeClientStream agentReads;
        readonly AnonymousPipeClientStream gatewayReads;
        readonly StreamTunnelTransport gatewayTransport;
        readonly StreamTunnelTransport agentTransport;
        readonly CancellationTokenSource stop = new();
        Task<string>? relay;
        Task<string>? agentRun;

        public AgentSession Session { get; }

        public RecordingApiClient Api { get; } = new();

        public TunnelAgent Agent { get; }

        public ConnectedAgent(AgentSession session) {
            Session = session;
            agentReads = new(PipeDirection.In, gatewayToAgent.ClientSafePipeHandle);
            gatewayReads = new(PipeDirection.In, agentToGateway.ClientSafePipeHandle);
            gatewayTransport = new(gatewayReads, gatewayToAgent);
            agentTransport = new(agentReads, agentToGateway);
            Agent = new(agentTransport, Api, new() { HeartbeatInterval = TimeSpan.FromMilliseconds(100), AgentVersion = "test-agent" });
        }

        public void Start() {
            relay = Session.RunAsync(gatewayTransport, stop.Token);
            agentRun = Agent.RunAsync(stop.Token);
        }

        public async Task<WelcomeBody> WelcomeAsync() {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while (Agent.Welcome is null && DateTime.UtcNow < deadline) {
                await Task.Delay(20, Ct);
            }

            return Agent.Welcome ?? throw new TimeoutException("no welcome arrived");
        }

        public async Task<string> CredentialAsync() =>
            (await WelcomeAsync()).Credential ?? throw new InvalidOperationException("the welcome carried no credential");

        public Task<string> EndedAsync() => relay!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        public async ValueTask DisposeAsync() {
            await stop.CancelAsync();

            foreach (var task in new[] { agentRun, relay }) {
                if (task is null) {
                    continue;
                }

                try {
                    await task;
                } catch (OperationCanceledException) {
                    // Stopped.
                }
            }

            Agent.Dispose();
            await gatewayTransport.DisposeAsync();
            await agentTransport.DisposeAsync();
            await gatewayToAgent.DisposeAsync();
            await agentToGateway.DisposeAsync();
            await agentReads.DisposeAsync();
            await gatewayReads.DisposeAsync();
            stop.Dispose();
        }
    }
}
