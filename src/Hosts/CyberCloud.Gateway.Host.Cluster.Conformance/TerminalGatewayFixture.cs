using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.Identity.Validation;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Terminal;
using CyberCloud.Providers.Terminal.Conformance;
using CyberCloud.Providers.Terminal.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Terminals;
using CyberCloud.ServiceDefaults.RateLimiting;
using CyberCloud.Tenancy.Directory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Cluster.Conformance;

/// <summary>
///     The cluster-conformance harness for <c>CyberCloud.Terminal/consoles</c> — a real silo over a
///     real k3s — with the gateway composed in front of it the way <c>CyberCloud.Gateway.Host</c>
///     composes itself: the eight real stages, the real ticket store, the real hubs and a resource
///     manager of its own over the silo's grains.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gateway's resource manager is composed as <c>AddCyberCloudGateway</c> composes it:
///         no cluster connection, and a relay.</b> A synchronous action runs inside the process that
///         serves the request, and this gateway, like the real one, can't reach a cluster —
///         <see cref="NoClusterConnectionFactory" /> — so <c>connect</c> is relayed to
///         <see cref="IClusterActionGrain" /> in the silo, which runs the handler there.
///         <c>HostCompositionTests.TheGatewayRelaysAClusterActionAndTheSiloRunsItItself</c> pins the
///         same two registrations on the real host. ⚠ Handing this gateway a direct connection instead
///         would pass every test here and hide the reason a real gateway without the relay refuses
///         every <c>connect</c>.
///     </para>
///     <para>
///         ⚠ <b>The silo reaches the cluster through the connection grain</b>, as
///         <see cref="TerminalGatewayCase" /> composes it, and the connection grain is attached here
///         for <see cref="ConformanceIds.Cluster" /> before the first test. So the attach is the
///         production one end to end: session grain → dialer → the connection grain's tenancy check →
///         a client built from the descriptor → <c>pods/attach</c>.
///     </para>
///     <para>
///         ⚠ <b>What is substituted, and why none of it is under test.</b> Stage 2's token validator
///         is the gateway's own issued-token resolver rather than a JWKS one — the identity host is
///         not part of this story and its tokens are proven elsewhere; the scope, role-assignment and
///         graph seams of stage 8 are substitutes no route here reaches. The kubeconfig comes from
///         <see cref="TerminalGatewayCase.Kubeconfig" /> rather than a vault. The ticket store, the hub,
///         the grains and the cluster are all real. ⚠ The gateway and the silo still share this
///         process, as every <c>TestCluster</c> does, so the Orleans wire between them is the
///         in-process one — <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>the-session-grain-has-not-crossed-a-process-boundary</c>.
///     </para>
/// </remarks>
public sealed class TerminalGatewayFixture : ClusterConformanceFixture<TerminalGatewayCase>, IAsyncLifetime {
    readonly IssuedTokenCallerContextResolver tokens = new(ClusterConformanceState<TerminalGatewayCase>.Clock);
    WebApplication? app;

    /// <summary>Where the gateway listens, or <see langword="null" /> when the cluster did not come up.</summary>
    public Uri? BaseUri { get; private set; }

    /// <summary>A client for the gateway's ordinary requests.</summary>
    public HttpClient Http { get; } = new();

    /// <inheritdoc />
    async ValueTask IAsyncLifetime.InitializeAsync() {
        // ⚠ Before the silo starts, which is what reads it: the containers are started once per
        // process, so this is the same k3s the harness is about to use.
        if (await ClusterInfrastructure.TryStartAsync(TestContext.Current.CancellationToken) is { } endpoints) {
            TerminalGatewayCase.Kubeconfig = endpoints.Kubeconfig;
        }

        await InitializeAsync();

        if (Harness is not { } harness) {
            return;
        }

        // The connection the platform registers when a cluster resource converges, registered by hand:
        // this story has no cluster resource. The first attach establishes the owner.
        var attached = await harness.Grains
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(ConformanceIds.Cluster))
            .AttachAsync(
                new() {
                    ClusterId = ConformanceIds.Cluster,
                    OwningTenantId = ConformanceIds.Tenant,
                    Kind = ClusterConnectionKind.Kubeconfig,
                    CredentialRef = "conformance-k3s",
                    DisplayName = "the conformance k3s"
                }
            );

        attached.IsSuccess.ShouldBeTrue(attached.Error?.Message);

        var clock = ClusterConformanceState<TerminalGatewayCase>.Clock;
        var options = new GatewayOptions();
        var tickets = new InMemoryHubTicketStore(clock);

        var directory = new TenantDirectoryCache(harness.Grains, NullLogger<TenantDirectoryCache>.Instance);
        directory.Apply(
            new() {
                Version = 1,
                IsFullSnapshot = true,
                Entries = [
                    new() { TenantId = ConformanceIds.Tenant, Slug = "terminal-a", Status = TenantStatus.Active },
                    new() { TenantId = ConformanceIds.OtherTenant, Slug = "terminal-b", Status = TenantStatus.Active }
                ]
            }
        );

        var manager = Manager(harness);

        IGatewayStage[] stages = [
            new CorrelationStage(),
            new AuthenticateStage(tokens, tickets),
            new ResolveTenantStage(directory, NullLogger<ResolveTenantStage>.Instance),
            new RegionRoutingStage(options, new UnconfiguredRegionProxy()),
            new RateLimitStage(new GatewayRateLimiter(new InMemoryRateLimitCounters(clock))),
            new RouteStage(harness.Registry, options),
            new ValidateStage(options),
            new DispatchStage(
                manager,
                Substitute.For<IScopeManager>(),
                Substitute.For<IRoleAssignmentManager>(),
                Substitute.For<IResourceGraphQuery>(),
                new TenantScopedOperationReader(manager),
                tickets,
                options
            )
        ];

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        foreach (var stage in stages) {
            builder.Services.AddSingleton(stage);
        }

        builder.Services.AddSingleton<GatewayPipeline>();
        builder.Services.AddSingleton(harness.Grains);
        builder.Services.AddSingleton<IConcurrencyLimiter>(new ProcessConcurrencyLimiter(new()));
        builder.Services.AddSignalR();

        app = builder.Build();
        app.MapGateway();
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUri = new(address + "/");
        Http.BaseAddress = BaseUri;
    }

    /// <summary>Issues a bearer token for a person in a tenant.</summary>
    /// <param name="tenant">The <c>tid</c> claim.</param>
    /// <param name="person">The <c>sub</c> claim; the subject type is always <c>user</c>.</param>
    public string Token(Guid tenant, string person) =>
        tokens.Issue(
            new TokenClaims(
                tenant,
                "user",
                person,
                "",
                "",
                ClusterConformanceState<TerminalGatewayCase>.Clock.UtcNow.AddDays(1)
            )
        );

    /// <summary>Posts an action through the whole pipeline, as the portal does.</summary>
    /// <param name="path">The action's path, without the query.</param>
    /// <param name="token">The caller's token.</param>
    public async Task<(int Status, string Body)> PostAsync(string path, string token) {
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "?api-version=" + CloudConsoles.V2026) {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Opens the terminal hub the way the portal does: a ticket over HTTP, then the socket with it.</summary>
    /// <param name="token">The caller's token — the ticket is bound to it.</param>
    public async Task<HubPane> OpenPaneAsync(string token) {
        var (status, body) = await PostAsync("/hubs/" + HubNames.Terminal + "/ticket", token);
        status.ShouldBe(200, body);

        var ticket = JsonDocument.Parse(body).RootElement.GetProperty("ticket").GetString()!;
        var uri = new Uri($"ws://{BaseUri!.Authority}/hubs/{HubNames.Terminal}?{HubTickets.QueryParameter}={ticket}");

        return await HubPane.OpenAsync(uri);
    }

    /// <inheritdoc />
    async ValueTask IAsyncDisposable.DisposeAsync() {
        if (app is not null) {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        Http.Dispose();
        await DisposeAsync();
    }

    /// <summary>
    ///     The gateway's own resource manager over the silo's grains: no cluster connection, and the
    ///     relay that sends an action needing one to the silo.
    /// </summary>
    static ResourceManagerService Manager(ClusterConformanceHarness<TerminalGatewayCase> harness) {
        var handlers = new ServiceCollection();

        handlers.AddSingleton<IClock>(ClusterConformanceState<TerminalGatewayCase>.Clock);
        handlers.AddSingleton<ISecretResolver>(ClusterConformanceState<TerminalGatewayCase>.Vault);
        handlers.AddSingleton<ISecretWriter>(ClusterConformanceState<TerminalGatewayCase>.Vault);
        handlers.AddSingleton(Options.Create(new CloudShellImageOptions { Default = TerminalGatewayCase.ShellImage }));
        handlers.AddSingleton<CloudConsoleSessionHandler>();

        return new(
            harness.Registry,
            ClusterConformanceState<TerminalGatewayCase>.Authorizer,
            ClusterConformanceState<TerminalGatewayCase>.Relations,
            ClusterConformanceState<TerminalGatewayCase>.Locks,
            new NotSupportedPolicyEvaluator(),
            ClusterConformanceState<TerminalGatewayCase>.Changes,
            harness.Grains,
            new ActionDispatcher(
                handlers.BuildServiceProvider(),
                new NoClusterConnectionFactory(),
                ClusterConformanceState<TerminalGatewayCase>.Vault,
                terminals: new GrainTerminalSessions(harness.Grains),
                relay: new GrainClusterActionRelay(harness.Grains)
            ),
            NullLogger<ResourceManagerService>.Instance
        );
    }
}
