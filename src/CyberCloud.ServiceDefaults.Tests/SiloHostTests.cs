using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Modularity;

namespace CyberCloud.ServiceDefaults.Tests;

/// <summary>
///     The smallest thing ABP will accept as an application root.
/// </summary>
/// <remarks>
///     Empty on purpose: the point is that <see cref="OrleansApplication.CreateSilo" /> requires
///     <i>some</i> module, not that it requires a particular one. A real host's module is where the
///     provider modules are pulled in with <c>[DependsOn]</c> (ADR-006).
/// </remarks>
sealed class SiloTestModule : AbpModule;

/// <summary>
///     <see cref="OrleansApplication.CreateSilo" /> started for real, and its probes asked the way
///     Kubernetes asks them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why over HTTP and not against the DI container.</b>
///         <see cref="HealthCheckWiringTests" /> proves the tags are right. It cannot prove that
///         <c>/health</c> returns 200 on a silo that is actually serving, that
///         <c>SiloReadinessHealthCheck</c> can resolve <see cref="ILocalSiloDetails" /> at all, or
///         that the <c>IManagementGrain</c> call it makes works from inside the silo — and those are
///         the three ways this check could be wired correctly and still be useless. A readiness
///         probe is only shown not to lie by being asked over the wire while the silo runs.
///     </para>
///     <para>
///         ⚠ <b>The silo lives on a class fixture, not on the test class.</b> xUnit constructs a
///         fresh instance of a test class for every <c>[Fact]</c>, so a class that is itself
///         <see cref="IAsyncLifetime" /> starts one silo per test — and
///         <c>UseLocalhostClustering()</c> binds the fixed Orleans ports 11111/30000, so the second
///         one dies with <c>AddressInUseException</c>. Observed, before this was a fixture: six
///         tests, one pass, five "Address already in use". A fixture is created once per class.
///     </para>
/// </remarks>
public sealed class SiloHostFixture : IAsyncLifetime
{
    WebApplication app = null!;

    /// <summary>The running silo's service provider.</summary>
    public IServiceProvider Services => app.Services;

    /// <summary>An HTTP client pointed at the silo's own listener.</summary>
    public HttpClient Http { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // --environment Development is what selects UseLocalhostClustering over UseKubeMembership
        // (ADR-004). Port 0 lets the OS pick the HTTP one.
        //
        // ⚠ The two Orleans ports are picked here rather than left at Orleans' 11111/30000
        // defaults, and that is not test hygiene — it is the reason CyberCloudClusterOptions has
        // LocalhostSiloPort/LocalhostGatewayPort at all. On the machine this was written on, 30000
        // was already held by an unrelated process, and the failure was an AddressInUseException
        // from a socket bind inside Orleans that named neither port nor process.
        var builder = OrleansApplication.CreateSilo([
            "--environment", "Development",
            "--urls", "http://127.0.0.1:0",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
        ]);

        // ⚠ Not optional, and not obvious from docs/plan/04:41-68. CreateSilo calls
        // builder.Host.UseAutofac(), and ABP's service-provider factory resolves IModuleContainer
        // during Build(). Without a module registered first, Build() throws
        // "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer" — a message that
        // names neither UseAutofac nor the missing call. Survival's hosts all do this
        // (Survival.Backend.Host/Program.cs:13); the plan's excerpt omits it. Doing it here is what
        // makes this test cover the real startup sequence.
        await builder.Services.AddApplicationAsync<SiloTestModule>();

        app = builder.Build();
        app.MapDefaultEndpoints();
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        Http = new HttpClient { BaseAddress = new Uri(address) };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();

        if (app is null)
        {
            return;
        }

        // ⚠ StopAsync on a silo that never reached Running throws its own exception, which xUnit
        // reports as a fixture-cleanup failure and which then hides the startup failure that is the
        // real diagnosis. Dispose is enough for a host that never started.
        try
        {
            await app.StopAsync();
        }
        catch (InvalidOperationException)
        {
            // The host never started; nothing to stop.
        }

        await app.DisposeAsync();
    }

    /// <summary>Asks the OS for a port nothing is listening on.</summary>
    /// <remarks>
    ///     Inherently racy — the port is free at the moment it is released and could be taken before
    ///     Orleans binds it — but the window is microseconds and the alternative is a fixed port
    ///     that is provably not free on at least one developer machine.
    /// </remarks>
    static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>The assertions against the running silo.</summary>
public sealed class SiloHostTests(SiloHostFixture silo) : IClassFixture<SiloHostFixture>
{
    HttpClient Http => silo.Http;

    [Fact]
    public async Task TheSiloStartsAndReachesActive()
    {
        var local = silo.Services.GetRequiredService<ILocalSiloDetails>();
        var hosts = await silo.Services.GetRequiredService<IGrainFactory>()
            .GetGrain<IManagementGrain>(0)
            .GetHosts();

        hosts[local.SiloAddress].ShouldBe(SiloStatus.Active);
    }

    [Fact]
    public async Task LivenessPasses()
    {
        var response = await Http.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadinessPassesOnlyBecauseTheSiloIsActuallyServing()
    {
        // The claim under test: 200 here means the silo answered an IManagementGrain call and found
        // itself Active in the result. Both are asserted directly above; this asserts the endpoint
        // agrees.
        var response = await Http.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheDetailEndpointReportsEveryCheckAndNoStackTraces()
    {
        var response = await Http.GetAsync(new Uri("/api/health", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);

        json.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");

        var names = json.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ToList();

        names.ShouldContain("self");
        names.ShouldContain("silo-ready");
        names.ShouldContain("silo-participants");
        names.ShouldContain("cluster");

        // docs/plan/08:190 — no exception detail in a body a caller can read. The health endpoint
        // is reachable from the cluster network, so "at" frames leaking here is an information
        // disclosure, not a cosmetic issue.
        body.ShouldNotContain("   at ");
        body.ShouldNotContain(".cs:line");
    }

    [Fact]
    public void ActivityPropagationIsWiredOrTracingStopsAtTheGateway()
    {
        // docs/plan/04:78. There is no public "is it on?" flag, so this asserts the observable
        // consequence: AddActivityPropagation registers its filters as IIncomingGrainCallFilter and
        // IOutgoingGrainCallFilter in the silo's container. Without the call, both are empty.
        silo.Services.GetServices<IIncomingGrainCallFilter>().ShouldNotBeEmpty();
        silo.Services.GetServices<IOutgoingGrainCallFilter>().ShouldNotBeEmpty();
    }

    [Fact]
    public void TheSiloHasNoGrainStorageBecauseThatWiringIsADeliberateSeam()
    {
        // ⚠ This is not a bug being ratified; it is the scope line being made visible. CreateSilo
        // wires no storage provider (docs/plan/04:50-56 is a marked seam in its body), so a silo
        // built without `configureCluster` cannot persist a grain. If somebody later adds a default
        // provider, this test fails and forces the question "which tier, and sharded how?" — which
        // is the question docs/plan/05 exists to answer and the one a silently-added
        // AddMemoryGrainStorage would bury.
        silo.Services.GetServices<IGrainStorage>().ShouldBeEmpty();
    }
}
