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
///         ⚠ <b>The gateway's resource manager is its own, because in production it is.</b> A
///         synchronous action runs inside the process that serves the request — the gateway — and
///         <c>connect</c>'s handler runs there, reaching the silo's session grain through this
///         process's Orleans client. The harness's own manager would do the same through its own
///         handler container, which has no shell image configured; this one carries
///         <see cref="CloudShellImageOptions" /> the way a deployment's configuration would.
///     </para>
///     <para>
///         ⚠ <b>What is substituted, and why none of it is under test.</b> Stage 2's token validator
///         is the gateway's own issued-token resolver rather than a JWKS one — the identity host is
///         not part of this story and its tokens are proven elsewhere; the scope, role-assignment and
///         graph seams of stage 8 are substitutes no route here reaches. The ticket store, the hub,
///         the grains and the cluster are all real.
///     </para>
/// </remarks>
public sealed class TerminalGatewayFixture : ClusterConformanceFixture<CloudConsoleCase>, IAsyncLifetime {
    /// <summary>
    ///     The shell image the story runs: PostgreSQL's Alpine image, by digest — <c>bash</c>,
    ///     BusyBox's <c>stty</c> and <c>psql</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A stand-in for docs/plan/19's image, which nothing in this repository builds —
    ///     <c>charts/managed/cloud-shell/conformance.yaml § owed</c>, <c>no-image-pipeline</c>. It is
    ///     handed to the platform exactly as the real one will be: as
    ///     <see cref="CloudShellImageOptions.Default" />, pinned.
    /// </remarks>
    public const string ShellImage =
        "docker.io/library/postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24";

    readonly IssuedTokenCallerContextResolver tokens = new(ClusterConformanceState<CloudConsoleCase>.Clock);
    WebApplication? app;

    /// <summary>Where the gateway listens, or <see langword="null" /> when the cluster did not come up.</summary>
    public Uri? BaseUri { get; private set; }

    /// <summary>A client for the gateway's ordinary requests.</summary>
    public HttpClient Http { get; } = new();

    /// <inheritdoc />
    async ValueTask IAsyncLifetime.InitializeAsync() {
        await InitializeAsync();

        if (Harness is not { } harness) {
            return;
        }

        var clock = ClusterConformanceState<CloudConsoleCase>.Clock;
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
                ClusterConformanceState<CloudConsoleCase>.Clock.UtcNow.AddDays(1)
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

    /// <summary>The gateway's own resource manager over the silo's grains and the real cluster.</summary>
    static ResourceManagerService Manager(ClusterConformanceHarness<CloudConsoleCase> harness) {
        var handlers = new ServiceCollection();

        handlers.AddSingleton<IClock>(ClusterConformanceState<CloudConsoleCase>.Clock);
        handlers.AddSingleton<ISecretResolver>(ClusterConformanceState<CloudConsoleCase>.Vault);
        handlers.AddSingleton<ISecretWriter>(ClusterConformanceState<CloudConsoleCase>.Vault);
        handlers.AddSingleton(Options.Create(new CloudShellImageOptions { Default = ShellImage }));
        handlers.AddSingleton<CloudConsoleSessionHandler>();

        return new(
            harness.Registry,
            ClusterConformanceState<CloudConsoleCase>.Authorizer,
            ClusterConformanceState<CloudConsoleCase>.Relations,
            ClusterConformanceState<CloudConsoleCase>.Locks,
            new NotSupportedPolicyEvaluator(),
            ClusterConformanceState<CloudConsoleCase>.Changes,
            harness.Grains,
            new ActionDispatcher(
                handlers.BuildServiceProvider(),
                new RealClusterConnectionFactory(harness.Connection),
                ClusterConformanceState<CloudConsoleCase>.Vault,
                terminals: new GrainTerminalSessions(harness.Grains)
            ),
            NullLogger<ResourceManagerService>.Instance
        );
    }
}
