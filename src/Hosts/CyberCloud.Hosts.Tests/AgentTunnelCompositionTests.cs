using CyberCloud.Agent.Host;
using CyberCloud.Gateway.Host;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tunnel;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults;
using CyberCloud.Silo.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Shouldly;
using System.Net;
using System.Net.Sockets;

namespace CyberCloud.Hosts.Tests;

/// <summary>
///     The three hosts of the agent tunnel (#36), composed as production composes them: the silo
///     holds the grain and the seam, the gateway holds the relay and the seam, and the agent host
///     holds nothing of the platform's at all.
/// </summary>
/// <remarks>
///     ⚠ <b>Concrete types, not "something resolves"</b> — the refusing defaults
///     (<c>UnavailableAgentTunnels</c>, a <c>KubeApiClientFactory</c> with no grain factory) resolve
///     perfectly well, and a silo that kept them would be a silo on which every connected cluster
///     sits <c>Creating</c> forever with the reconciler's log blaming the tenant's agent.
/// </remarks>
public sealed class AgentTunnelCompositionTests {
    [Fact]
    public async Task TheSiloWiresTheAgentSeamAndRoutesAgentInitiatedDescriptorsThroughTheTunnel() {
        await using var silo = await BuildSiloAsync();

        silo.Services.GetRequiredService<IAgentTunnels>().ShouldBeOfType<GrainAgentTunnels>();

        silo.Services
            .GetRequiredService<IOptions<GrainTypeOptions>>()
            .Value
            .Classes
            .ShouldContain(typeof(AgentTunnelGrain));

        // The factory built WITH the grain factory: an AgentInitiated descriptor becomes a tunnel
        // client rather than the refusal a silo-less factory gives.
        var connected = await silo.Services
            .GetRequiredService<CyberCloud.Kubernetes.Connections.IKubeApiClientFactory>()
            .ConnectAsync(
                new() {
                    ClusterId = Guid.NewGuid(),
                    OwningTenantId = Guid.NewGuid(),
                    Kind = ClusterConnectionKind.AgentInitiated
                },
                TestContext.Current.CancellationToken
            );

        connected.IsSuccess.ShouldBeTrue(connected.Error?.Message);
        connected.GetValueOrThrow().ShouldBeOfType<TunnelKubeApiClient>();
    }

    [Fact]
    public async Task TheGatewayWiresTheRelayAndTheSeamAndPointsTheInstallCommandAtItself() {
        await using var gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                "--CyberCloud:Gateway:Identity:Issuer=http://127.0.0.1:1",
                "--CyberCloud:Gateway:PublicBaseUri=https://api.example.test"
            ]
        );

        gateway.Services.GetRequiredService<AgentTunnelRelay>().ShouldNotBeNull();
        gateway.Services.GetRequiredService<IAgentTunnels>().ShouldBeOfType<GrainAgentTunnels>();

        // ⚠ The default tunnel endpoint is derived from the gateway's own public base URI, so an
        // install command minted by this gateway tells the agent to dial this gateway.
        gateway.Services
            .GetRequiredService<IOptions<AgentTunnelOptions>>()
            .Value
            .TunnelEndpoint
            .ShouldBe("wss://api.example.test" + TunnelCodec.TunnelPath);
    }

    [Fact]
    public void TheAgentHostComposesWithoutAPodAndBindsTheChartsSettings() {
        // ⚠ Built, not started: starting it would dial the endpoint. What is asserted is that the
        // in-cluster client is NOT built at composition — a test process has no service account —
        // and that the options the chart sets as environment variables bind.
        var clusterId = Guid.NewGuid();

        using var app = AgentComposition.Build(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{AgentOptions.SectionName}:TunnelEndpoint=wss://api.example.test/agent/v1/tunnel",
                $"--{AgentOptions.SectionName}:ClusterId={clusterId:D}",
                $"--{AgentOptions.SectionName}:HeartbeatSeconds=20",
                $"--{AgentOptions.SectionName}:CredentialSecretName=tenant-named-credential"
            ],
            static services => services.AddSingleton<IAgentEndpoints>(new NoPod())
        );

        var options = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        options.IsConfigured.ShouldBeTrue();
        options.ParsedClusterId.ShouldBe(clusterId);
        options.HeartbeatSeconds.ShouldBe(20);

        // ⚠ The chart's cluster.credentialSecretName, which rbac.yaml scopes the Role to. Until it
        // bound, an overridden name was a Secret the agent could create and never read back.
        options.CredentialSecretName.ShouldBe("tenant-named-credential");

        app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ShouldContain(x => x is AgentService);
    }

    [Fact]
    public void TheAgentHostBindsNoOrleansRuntimeAndNoKubernetesTypes() {
        // docs/plan/03 § Assembly graph rules, rule 3 for the host, and the reference-set argument
        // in its .csproj: nothing of the platform's control plane is reachable from a tenant's pod.
        //
        // ⚠ Orleans.Serialization IS in the list and is not the control plane. The contracts
        // assembly's [GenerateSerializer] types make Microsoft.Orleans.Sdk's generator emit a type
        // manifest into every assembly that references them, and that manifest names
        // Orleans.Serialization's attributes. What a pod must not hold is a client — Orleans.Core,
        // Orleans.Runtime, the multitenant factory — and it holds none of them.
        var referenced = typeof(AgentComposition).Assembly.GetReferencedAssemblies()
            .Select(static x => x.Name ?? "")
            .ToList();

        foreach (var forbidden in new[] {
                     "Orleans.Core", "Orleans.Core.Abstractions", "Orleans.Runtime", "Orleans.Multitenant",
                     "KubernetesClient", "CyberCloud.ResourceManager", "CyberCloud.Tenancy", "CyberCloud.Authorization"
                 }) {
            referenced.ShouldNotContain(forbidden);
        }
    }

    static Task<WebApplication> BuildSiloAsync() =>
        SiloComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}"
            ]
        );

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    sealed class NoPod : IAgentEndpoints {
        public IKubeApiClient Api => throw new InvalidOperationException("not a pod");

        public IAgentCredentialStore Credentials => throw new InvalidOperationException("not a pod");
    }
}
