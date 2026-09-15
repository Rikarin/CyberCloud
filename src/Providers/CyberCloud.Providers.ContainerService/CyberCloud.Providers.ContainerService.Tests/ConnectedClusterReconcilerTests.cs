using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     <see cref="ConnectedClusterReconciler" /> against a scripted <see cref="IAgentTunnels" />: the
///     three states a pass can find the agent in, what each reports, and what the delete does.
/// </summary>
/// <remarks>
///     The seam is faked here and real in <c>ConnectedClusterConformance</c>, which drives the
///     same reconciler through the manager. What a unit test adds is the branch nothing in a
///     conformance run visits on purpose — a tunnel whose token expired unspent — and the clause-2
///     structural check every reconciler in the tree gets.
/// </remarks>
public sealed class ConnectedClusterReconcilerTests {
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid ResourceId = Guid.Parse("66666666-6666-4666-8666-666666666666");

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new ConnectedClusterReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANeverArmedClusterIsInProgressAndTellsTheTenantWhichActionToCall() {
        var agents = new ScriptedAgents { Status = new() { ClusterId = ResourceId, Armed = false } };
        var log = new CollectingLog();

        var outcome = await Reconcile(agents, log);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.RetryAfter.ShouldBe(ConnectedClusterReconciler.WaitingForInstall);
        log.Entries.ShouldContain(x => x.Phase == "waiting-for-install-command"
                                       && x.Detail.Contains(ConnectedClusters.ListInstallCommandAction, StringComparison.Ordinal));
        agents.Revocations.ShouldBe(0);
    }

    [Fact]
    public async Task AnArmedClusterWithNoHeartbeatIsInProgressAndSaysWhetherTheTokenIsStillGood() {
        var open = new ScriptedAgents { Status = new() { ClusterId = ResourceId, Armed = true, EnrollmentOpen = true } };
        var openLog = new CollectingLog();

        (await Reconcile(open, openLog)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        openLog.Entries.ShouldContain(x => x.Phase == "waiting-for-agent" && x.Detail.Contains("has not connected yet", StringComparison.Ordinal));

        // ⚠ The token expired unspent: the same InProgress, but the message now names the action
        // again, because the tenant has to ask for a fresh command and nothing else will tell them.
        var expired = new ScriptedAgents { Status = new() { ClusterId = ResourceId, Armed = true, EnrollmentOpen = false } };
        var expiredLog = new CollectingLog();

        (await Reconcile(expired, expiredLog)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        expiredLog.Entries.ShouldContain(x => x.Phase == "waiting-for-agent" && x.Detail.Contains("expired or been spent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFirstHeartbeatConvergesAndReportsAnAgentInitiatedConnectionWithNoCredential() {
        var agents = new ScriptedAgents {
            Status = new() {
                ClusterId = ResourceId,
                Armed = true,
                Connected = true,
                FirstHeartbeatAt = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero),
                LastHeartbeatAt = new(2026, 9, 15, 10, 0, 15, TimeSpan.Zero),
                AgentVersion = "1.0.0"
            }
        };
        var sink = new RecordingClusterSink();

        var outcome = await Reconcile(agents, clusters: sink);

        outcome.ShouldBe(ReconcileOutcome.Converged);

        var descriptor = sink.Descriptor.ShouldNotBeNull();
        descriptor.Kind.ShouldBe(ClusterConnectionKind.AgentInitiated);
        descriptor.CredentialRef.ShouldBeEmpty("the platform holds no credential for this kind — the agent does");
        descriptor.DisplayName.ShouldBe("byo");

        // ⚠ Left for the driver to stamp, so a provider cannot register a cluster under a tenant
        // that does not own it.
        descriptor.ClusterId.ShouldBe(Guid.Empty);
        descriptor.OwningTenantId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task AHeartbeatedClusterWhoseAgentIsAwayStillConvergesAndSaysSo() {
        // docs/plan/09 § Cluster connections: reachability is health, not provisioning state.
        var agents = new ScriptedAgents {
            Status = new() {
                ClusterId = ResourceId,
                Armed = true,
                Connected = false,
                FirstHeartbeatAt = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero),
                LastHeartbeatAt = new(2026, 9, 15, 10, 5, 0, TimeSpan.Zero)
            }
        };
        var log = new CollectingLog();

        (await Reconcile(agents, log, new RecordingClusterSink())).ShouldBe(ReconcileOutcome.Converged);
        log.Entries.ShouldContain(x => x.Phase == "connected" && x.Detail.Contains("not connected right now", StringComparison.Ordinal));

        var observed = await new ConnectedClusterReconciler(new FixedClock()).ObserveAsync(
            new(Address(), ConnectedClusters.V2026, Desired(), "ns", null) { Agents = agents },
            TestContext.Current.CancellationToken
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("cannot reach your cluster");
    }

    [Fact]
    public async Task DeleteRevokesTheAgentAndConverges() {
        var agents = new ScriptedAgents { Status = new() { ClusterId = ResourceId, Armed = true } };
        var log = new CollectingLog();

        var outcome = await new ConnectedClusterReconciler(new FixedClock()).DeleteAsync(
            Context(agents, log),
            TestContext.Current.CancellationToken
        );

        outcome.ShouldBe(ReconcileOutcome.Converged);
        agents.Revocations.ShouldBe(1);
        log.Entries.ShouldContain(x => x.Phase == "revoked");
    }

    [Fact]
    public async Task ASeamThatFailsFailsThePassRatherThanReportingAnAgentState() {
        // The refusing default every hand-built context carries: a pass must not read "not armed"
        // out of a seam that answered nothing.
        var outcome = await new ConnectedClusterReconciler(new FixedClock()).ReconcileAsync(
            new(Address(), ConnectedClusters.V2026, Desired(), null, "ns", null, new UnavailableSecretResolver(), new NullLog()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("IAgentTunnels");
    }

    [Fact]
    public void TheInstallCommandQuotesEveryValueAndPassesTheTokenAsAString() {
        var enrollment = new AgentEnrollment {
            ClusterId = ResourceId,
            EnrollmentToken = "cca-enroll-abc'def",
            TunnelEndpoint = "wss://api.example/agent/v1/tunnel",
            ChartReference = "oci://ghcr.io/x/cybercloud-agent",
            AgentImage = "ghcr.io/x/cybercloud-agent-host@sha256:abc",
            HeartbeatInterval = TimeSpan.FromSeconds(20)
        };

        var command = ConnectedClusters.InstallCommand(enrollment);

        command.ShouldStartWith("helm upgrade --install cybercloud-agent 'oci://ghcr.io/x/cybercloud-agent'");
        command.ShouldContain("--namespace cybercloud-system --create-namespace");
        command.ShouldContain("--set-string cluster.id=66666666-6666-4666-8666-666666666666");
        // ⚠ --set-string, and the quote inside the token is escaped rather than ending the string.
        command.ShouldContain("--set-string cluster.enrollmentToken='cca-enroll-abc'\\''def'");
        command.ShouldContain("--set agent.heartbeatSeconds=20");
        command.ShouldContain("--set-string image.reference='ghcr.io/x/cybercloud-agent-host@sha256:abc'");
        command.ShouldNotContain("--set cluster.enrollmentToken");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static Task<ReconcileOutcome> Reconcile(
        IAgentTunnels agents,
        IReconcileLog? log = null,
        IClusterConnectionSink? clusters = null
    ) =>
        new ConnectedClusterReconciler(new FixedClock()).ReconcileAsync(
            Context(agents, log, clusters),
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(IAgentTunnels agents, IReconcileLog? log = null, IClusterConnectionSink? clusters = null) =>
        new(
            Address(),
            ConnectedClusters.V2026,
            Desired(),
            null,
            ReconcileDriver.NamespaceFor(Address()),
            null,
            new UnavailableSecretResolver(),
            log ?? new NullLog()
        ) {
            Agents = agents,
            ClusterConnections = clusters ?? new RecordingClusterSink()
        };

    static ResourceId Address() => new(TenantA, SubscriptionA, "prod", ConnectedClusters.Type, "byo", ResourceId);

    static JsonElement Desired() => JsonDocument.Parse(ConnectedClusters.Body()).RootElement.Clone();

    sealed class ScriptedAgents : IAgentTunnels {
        public AgentTunnelStatus Status { get; set; } = new();

        public int Revocations { get; private set; }

        public Task<Result<AgentEnrollment>> EnrollAsync(
            Guid clusterId,
            Guid owningTenantId,
            TimeSpan heartbeatInterval,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException("a reconcile pass never mints");

        public Task<Result<AgentTunnelStatus>> GetStatusAsync(Guid clusterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<AgentTunnelStatus>.Success(Status));

        public Task<Result> RevokeAsync(Guid clusterId, CancellationToken cancellationToken = default) {
            Revocations++;
            Status = Status with { Revoked = true, Connected = false };
            return Task.FromResult(Result.Success);
        }
    }
}
