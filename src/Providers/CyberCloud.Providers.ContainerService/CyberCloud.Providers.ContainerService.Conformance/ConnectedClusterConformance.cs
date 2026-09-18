using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.ContainerService.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.Tenancy.Contracts;
using Shouldly;
using System.Text.Json;
using System.Text.Json.Nodes;
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Providers.ContainerService.Conformance;

/// <summary>
///     <c>CyberCloud.ContainerService/connectedClusters</c>' registration into the shared harness.
/// </summary>
/// <remarks>
///     ⚠ <b>Registered into the harness and NOT into <c>ProviderConformanceTests&lt;T&gt;</c>.</b>
///     That suite asserts, in every one of its two thousand lines, that a converged resource applied
///     objects into a cluster — "declares RequiresCluster and applied nothing" is a failure there,
///     and every drift, label and teardown assertion reads the fake cluster. A connected cluster
///     applies nothing anywhere: its convergence is an event in a cluster the platform cannot see.
///     Running the shared suite over it would fail on the shape rather than on the provider, and
///     skipping half of it would leave nobody able to say which half. So this type has its own
///     suite, <see cref="ConnectedClusterConformance" />, over the same harness, the same manager
///     and the same driver — and the case below is what lets the harness build them.
/// </remarks>
public sealed class ConnectedClusterCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.ContainerService/connectedClusters",
            CreateProvider = static () => new ContainerServiceProvider(),
            ReconcilerType = typeof(ConnectedClusterReconciler),
            CreateReconciler = static clock => new ConnectedClusterReconciler(clock),
            Type = ConnectedClusters.Type,
            ApiVersion = ConnectedClusters.V2026,
            Body = static _ => ConnectedClusters.Body(),
            ChangedBody = static _ => ConnectedClusters.Body(heartbeatSeconds: 30),
            // Drops the required `/location`.
            InvalidBody = static _ => WithoutLocation(ConnectedClusters.Body()),
            InvalidBodyTarget = "/location",
            ActionName = ConnectedClusters.ListInstallCommandAction,
            // ⚠ Empty on purpose, and the reason the shared suite is not run over this case.
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            // Neither clusterless world: this type's own suite reads the tunnel seam directly, and the
            // shared suite that would ask for one is not run over it. Stated rather than defaulted —
            // see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = static _ => true
        };

    static string WithoutLocation(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node.Remove("location");
        return node.ToJsonString();
    }
}

/// <summary>
///     The connected cluster's own conformance: the attach flow of docs/plan/09 § Cluster connections'
///     <c>AgentInitiated</c> row, driven through the real manager, the real driver and the real
///     reconciler, with the agent-tunnel seam faked.
/// </summary>
/// <remarks>
///     <para>
///         The flow, as the tenant experiences it: create the resource; it is <c>Creating</c>. Ask
///         it for an install command; the response carries a one-time token. Run the command; the
///         agent connects and heartbeats. The resource is <c>Succeeded</c>, and its id is a cluster
///         other resources can be placed in. Delete it; the agent is revoked.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What a real NAT'd cluster would add is at
///             <c>
/// charts/agent/conformance.yaml
///         § owed
///             </c>.
///         </b> Nothing here opens a socket: the heartbeat is
///         <see cref="FakeAgentTunnels.Heartbeat" />, played by the test at the moment a real agent
///         would have sent it. The socket, the credential exchange and the grain are
///         <c>AgentTunnelGrainTests</c>' — real, in one process, over pipes.
///     </para>
/// </remarks>
/// <param name="cluster">The harness.</param>
public sealed class ConnectedClusterConformance(ProviderTestCluster<ConnectedClusterCase> cluster)
    : IClassFixture<ProviderTestCluster<ConnectedClusterCase>> {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    FakeAgentTunnels Agents => ConformanceState<ConnectedClusterCase>.Agents;

    RecordingClusterConnectionRegistrar Registrar => ConformanceState<ConnectedClusterCase>.Registrar;

    [Fact]
    public async Task ACreatedClusterIsCreatingUntilItsAgentHeartbeatsAndSucceededAfter() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var accepted = (await CreateAsync("byo-flow")).GetValueOrThrow();
        accepted.Resource.ProvisioningState.ShouldBe(ProvisioningState.Creating);

        // No install command yet: every pass is InProgress, and the operation stays open.
        var waiting = await DriveAsync(accepted, 3);
        waiting.IsTerminal.ShouldBeFalse("nothing has asked for an install command, so nothing can have connected");
        (await ReadAsync("byo-flow")).GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Creating);

        // The install command: a token the fake would admit, and the address the agent dials.
        var command = await InstallCommandAsync("byo-flow");
        var token = command["token"]!.GetValue<string>();
        Agents.WouldAdmit(accepted.Resource.Id, token).ShouldBeTrue();
        command["command"]!.GetValue<string>().ShouldContain(token);
        command["command"]!.GetValue<string>().ShouldContain(FakeAgentTunnels.TunnelEndpoint);
        command["tunnelEndpoint"]!.GetValue<string>().ShouldBe(FakeAgentTunnels.TunnelEndpoint);

        // Still Creating: armed is not connected.
        (await DriveAsync(accepted, 2)).IsTerminal.ShouldBeFalse();

        // The agent's first heartbeat — the event the resource converges on.
        Agents.Heartbeat(accepted.Resource.Id);

        var converged = await DriveAsync(accepted, 3);
        converged.State.ShouldBe(OperationState.Succeeded, converged.Error?.Message);
        (await ReadAsync("byo-flow")).GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Succeeded);

        // And the driver attached the cluster, under the resource's own id and the tenant's, as an
        // agent-initiated connection with no credential of the platform's.
        var attached = Registrar.Attached.ShouldHaveSingleItem();
        attached.ClusterId.ShouldBe(accepted.Resource.Id);
        attached.OwningTenantId.ShouldBe(ConformanceIds.Tenant);
        attached.Kind.ShouldBe(ClusterConnectionKind.AgentInitiated);
        attached.CredentialRef.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondInstallCommandVoidsTheFirstToken() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var accepted = (await CreateAsync("byo-reissue")).GetValueOrThrow();

        var first = (await InstallCommandAsync("byo-reissue"))["token"]!.GetValue<string>();
        var second = (await InstallCommandAsync("byo-reissue"))["token"]!.GetValue<string>();

        second.ShouldNotBe(first);
        Agents.WouldAdmit(accepted.Resource.Id, first)
            .ShouldBeFalse("a re-issued command is the tenant saying the old one is no good");
        Agents.WouldAdmit(accepted.Resource.Id, second).ShouldBeTrue();
    }

    [Fact]
    public async Task TheBodysHeartbeatIntervalReachesBothTheChartAndTheArm() {
        // ⚠ Two ways out of one number. The install command passes heartbeatSeconds to the chart,
        // and the arm carries it to the grain so the welcome — which the agent adopts — says the
        // same. The first cut did only the first, and a resource asking for 30 heartbeated at 15.
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var accepted = (await CreateAsync("byo-heartbeat", 30)).GetValueOrThrow();

        var command = await InstallCommandAsync("byo-heartbeat");

        command["command"]!.GetValue<string>().ShouldContain("--set agent.heartbeatSeconds=30");
        Agents.ArmedHeartbeats[accepted.Resource.Id].ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheInstallCommandIsMintedForTheResourcesOwnTenantAndNotForACallerFromAnother() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        await CreateAsync("byo-owner");

        // The other tenant's caller, at the other tenant's address: the canonical 404, and no token
        // minted for anybody.
        var otherAddress = ProviderTestCluster<ConnectedClusterCase>.Address(
            "byo-owner",
            ConformanceIds.OtherTenant,
            ConformanceIds.OtherSubscription
        );

        var refused = await cluster.Manager.ActionAsync(
            new() {
                Path = otherAddress.Path,
                ApiVersion = ConnectedClusters.V2026,
                Verb = WriteVerb.Post,
                Action = ConnectedClusters.ListInstallCommandAction,
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller(ConformanceIds.OtherTenant)
            },
            Ct
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        Agents.Enrollments.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAgentThatGoesAwayLeavesTheResourceSucceededAndTheObservedStateSaysUnreachable() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var accepted = (await CreateAsync("byo-away")).GetValueOrThrow();
        await InstallCommandAsync("byo-away");
        Agents.Heartbeat(accepted.Resource.Id);
        (await DriveAsync(accepted, 3)).State.ShouldBe(OperationState.Succeeded);

        Agents.Disconnect(accepted.Resource.Id);

        // docs/plan/09 § Cluster connections: reachability is health, not provisioning state.
        var observed = await new ConnectedClusterReconciler(cluster.Clock).ObserveAsync(
            new(
                ProviderTestCluster<ConnectedClusterCase>.Address("byo-away").WithId(accepted.Resource.Id),
                ConnectedClusters.V2026,
                JsonDocument.Parse(ConnectedClusters.Body()).RootElement.Clone(),
                "ns",
                null
            ) { Agents = Agents },
            Ct
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("cannot reach your cluster");
        (await ReadAsync("byo-away")).GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task DeletingRevokesTheAgent() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var accepted = (await CreateAsync("byo-delete")).GetValueOrThrow();
        await InstallCommandAsync("byo-delete");
        Agents.Heartbeat(accepted.Resource.Id);
        (await DriveAsync(accepted, 3)).State.ShouldBe(OperationState.Succeeded);

        var deleted = await cluster.Manager.DeleteAsync(
            new() {
                Path = ProviderTestCluster<ConnectedClusterCase>.Address("byo-delete").Path,
                ApiVersion = ConnectedClusters.V2026,
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller()
            },
            Ct
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        (await DriveAsync(deleted.GetValueOrThrow(), 3)).State.ShouldBe(OperationState.Succeeded);

        Agents.Revocations.ShouldContain(accepted.Resource.Id);
        (await Agents.GetStatusAsync(accepted.Resource.Id, Ct)).GetValueOrThrow().Revoked.ShouldBeTrue();
    }

    [Fact]
    public async Task ABodyWithNoLocationIsRefusedBeforeAnythingIsMinted() {
        ProviderTestCluster<ConnectedClusterCase>.Reset();

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = ProviderTestCluster<ConnectedClusterCase>.Address("byo-invalid").Path,
                ApiVersion = ConnectedClusters.V2026,
                Verb = WriteVerb.Put,
                Body = ConnectedClusterCase.ProviderCase.InvalidBody(Guid.Empty),
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller()
            },
            Ct
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        Agents.Enrollments.ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    async Task<Result<WriteAccepted>> CreateAsync(
        string name,
        int heartbeatSeconds = ConnectedClusters.DefaultHeartbeatSeconds
    ) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = ProviderTestCluster<ConnectedClusterCase>.Address(name).Path,
                ApiVersion = ConnectedClusters.V2026,
                Verb = WriteVerb.Put,
                Body = ConnectedClusters.Body(heartbeatSeconds: heartbeatSeconds),
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller()
            },
            Ct
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        return accepted;
    }

    Task<Result<ResourceSnapshot>> ReadAsync(string name) =>
        cluster.Manager.ReadAsync(
            new() {
                Path = ProviderTestCluster<ConnectedClusterCase>.Address(name).Path,
                ApiVersion = ConnectedClusters.V2026,
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller()
            },
            Ct
        );

    async Task<JsonObject> InstallCommandAsync(string name) {
        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = ProviderTestCluster<ConnectedClusterCase>.Address(name).Path,
                ApiVersion = ConnectedClusters.V2026,
                Verb = WriteVerb.Post,
                Action = ConnectedClusters.ListInstallCommandAction,
                Caller = ProviderTestCluster<ConnectedClusterCase>.Caller()
            },
            Ct
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        return JsonNode.Parse(action.GetValueOrThrow().ActionResponse)!.AsObject();
    }

    async Task<OperationStatus> DriveAsync(WriteAccepted accepted, int passes) {
        var operation = cluster.Operation(ConformanceIds.Tenant, accepted.OperationId);
        OperationStatus? last = null;

        for (var i = 0; i < passes; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        return last!;
    }
}
