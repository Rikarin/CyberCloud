using CyberCloud.Gateway.Host;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ResourceManager.Grains;
using CyberCloud.ResourceManager.Reconcile;
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
///     What the two production hosts actually compose.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here calls the host's own composition method and nothing else.</b> That is
///         the whole design of this file. The gap it was written for survived because it was invisible
///         to the only kind of test anybody wrote: a <c>TestCluster</c> that registers the resource
///         manager and a provider proves that <i>a</i> silo can reconcile, and says nothing about
///         whether <c>CyberCloud.Silo.Host</c> is one. It was not — the host referenced no provider
///         module, called no <c>AddCyberCloudResourceManager</c>, and had
///         <c>AddCyberCloudProvider</c>'s only mention in the tree be its own declaration.
///     </para>
///     <para>
///         ⚠ <b>Composition only — nothing here starts a silo or connects a client.</b> Both hosts are
///         built with a Development environment and free Orleans ports, so <c>Build()</c> completes
///         without a cluster, a Redis or a PostgreSQL. What that costs is stated where it bites: this
///         suite cannot see a failure that only appears once grains activate.
///         <c>CyberCloud.ServiceDefaults.Tests</c> starts a silo for real.
///     </para>
/// </remarks>
public sealed class HostCompositionTests {
    /// <summary>
    ///     The twelve provider namespaces both hosts must serve, spelled out rather than counted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Typed-out literals, and a count would not do.</b> A count passes when one provider is
    ///     swapped for another, and the failure that produced this file was not "one fewer provider" —
    ///     it was "no providers, and the registry built fine". Naming them makes adding a provider a
    ///     deliberate edit here and removing one a red test.
    /// </remarks>
    static readonly string[] EveryProviderNamespace = [
        "CyberCloud.Analytics",
        "CyberCloud.Cache",
        "CyberCloud.ContainerService",
        "CyberCloud.DBforMySQL",
        "CyberCloud.DBforPostgreSQL",
        "CyberCloud.DocumentDB",
        "CyberCloud.Messaging",
        "CyberCloud.Network",
        "CyberCloud.Sample",
        "CyberCloud.Search",
        "CyberCloud.Storage",
        "CyberCloud.Terminal"
    ];

    // ── Failure class (a): a host that composes the manager and registers nothing ─────────────────

    /// <summary>
    ///     ⚠ The silo serves every provider namespace, out of the registry the real host built.
    /// </summary>
    [Fact]
    public async Task TheSiloComposesEveryProviderModule() {
        await using var silo = await BuildSiloAsync();

        var registry = silo.Services.GetRequiredService<IProviderRegistry>();

        registry.Namespaces
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(
                EveryProviderNamespace,
                customMessage:
                "docs/plan/04 § Silo composition: \"Every silo loads every provider module. There are "
                + "no specialised silo roles.\" A namespace missing here is a provider whose resource "
                + "types nothing in production reconciles."
            );
    }

    /// <summary>
    ///     ⚠ The gateway routes from a registry with the same namespaces in it.
    /// </summary>
    [Fact]
    public async Task TheGatewayComposesEveryProviderModule() {
        await using var gateway = await BuildGatewayAsync();

        var registry = gateway.Services.GetRequiredService<IProviderRegistry>();

        registry.Namespaces
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(
                EveryProviderNamespace,
                customMessage:
                "Stage 6 resolves a request path against this registry. A namespace missing here is "
                + "every path under it answering the canonical 404, which is the same answer a caller "
                + "gets for a type that does not exist."
            );
    }

    /// <summary>
    ///     ⚠ A container with the resource manager and no provider refuses to produce a registry.
    /// </summary>
    /// <remarks>
    ///     <b>This is the failure that was invisible.</b> Both hosts composed the manager, neither
    ///     registered a provider, <c>ProviderRegistry.Build</c> returned an empty registry, and
    ///     <c>RouteStage</c> answered <c>404</c> to everything with nothing in the log. An empty
    ///     registry describes a platform that serves no resource type at all, which is a wiring
    ///     mistake rather than a supported shape.
    /// </remarks>
    [Fact]
    public void ComposingTheResourceManagerWithNoProviderIsRefused() {
        var services = new ServiceCollection();
        services.AddCyberCloudResourceManager();

        using var provider = services.BuildServiceProvider();

        var thrown = Should.Throw<InvalidOperationException>(
            provider.GetRequiredService<IProviderRegistry>
        );

        thrown.Message.ShouldContain("No IResourceProvider is registered");
    }

    // ── Failure class (b): the two processes disagreeing about what exists ────────────────────────

    /// <summary>
    ///     ⚠ The silo and the gateway describe the same platform, type for type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two <c>[DependsOn]</c> lists are separate lists in separate files, and nothing in
    ///         the assembly graph can merge them: rule 4 lets only a host reference a
    ///         <c>*.Application</c> assembly, so there is no shared place to put one list. A
    ///         difference is silent in both directions — a provider in the gateway and not the silo
    ///         accepts creates that never converge, and one in the silo and not the gateway reconciles
    ///         resources nobody can address.
    ///     </para>
    ///     <para>
    ///         ⚠ It compares the resource types rather than the namespaces, because two providers can
    ///         share a namespace and differ in what they declare — an api-version added to one host's
    ///         copy of a provider and not the other's is the same class of drift with a smaller blast
    ///         radius, and the type list is where it shows.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheSiloAndTheGatewayAgreeAboutWhatExists() {
        await using var silo = await BuildSiloAsync();
        await using var gateway = await BuildGatewayAsync();

        Types(silo).ShouldBe(
            Types(gateway),
            customMessage:
            "CyberCloud.Silo.Host and CyberCloud.Gateway.Host built different provider registries. "
            + "The gateway routes from ITS registry and the silo reconciles from ITS container, so a "
            + "type in one and not the other either cannot be reached or never converges. The two "
            + "[DependsOn] lists — SiloHostModule and GatewayHostModule — have to be the same list."
        );

        return;

        static string[] Types(WebApplication host) =>
        [
            .. host.Services
                .GetRequiredService<IProviderRegistry>()
                .Types
                .Select(x => x.Type.ToString())
                .OrderBy(x => x, StringComparer.Ordinal)
        ];
    }

    // ── Failure class (c): a reference that does not surface the grains ───────────────────────────

    /// <summary>
    ///     ⚠ The write path's grains are in the silo's grain manifest, not merely on its disk.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A <c>ProjectReference</c> that satisfies the compiler and never reaches Orleans is
    ///         the same defect with more steps.</b> Orleans discovers grains by scanning referenced
    ///         assemblies, and what it scans is decided by the SDK per reference — so "the host builds"
    ///         proves the assembly is on disk and proves nothing about whether a silo would activate
    ///         <c>ResourceGrain</c>. This reads <c>GrainTypeOptions</c>, which is what the manifest is
    ///         built from.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>ConnectionGrain</c> is in the list on purpose. It shipped in the gateway host —
    ///         an Orleans <i>client</i> — where no silo could ever have loaded it, and the tests passed
    ///         because they constructed it with <c>new</c>. It moved to
    ///         <c>CyberCloud.ResourceManager</c>; this is the assertion that it arrived somewhere a
    ///         silo composes.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheResourceManagerGrainsAreInTheSilosManifest() {
        await using var silo = await BuildSiloAsync();

        var classes = silo.Services
            .GetRequiredService<IOptions<GrainTypeOptions>>()
            .Value
            .Classes;

        foreach (var grain in new[] { typeof(ResourceGrain), typeof(OperationGrain), typeof(ConnectionGrain) }) {
            classes.ShouldContain(
                grain,
                $"{grain.Name} is not in the silo's grain manifest, so no silo would activate it. "
                + "Orleans discovers grains by scanning referenced assemblies — a ProjectReference the "
                + "compiler is happy with is not the same as a grain the runtime can place."
            );
        }
    }

    /// <summary>
    ///     ⚠ Every reconciler the registry names resolves from the silo's own container.
    /// </summary>
    /// <remarks>
    ///     <c>ReconcileDriver</c> resolves <c>ReconcilerType</c> from the container by the concrete
    ///     type the registry stores, and a type whose reconciler is missing fails inside the reminder
    ///     that drives it — hours after the create, in a log nobody is reading. Asserting it at
    ///     composition is what turns that into a red test.
    /// </remarks>
    [Fact]
    public async Task EveryReconcilerTheRegistryNamesResolvesInTheSilo() {
        await using var silo = await BuildSiloAsync();

        var registry = silo.Services.GetRequiredService<IProviderRegistry>();

        var missing = registry.Types
            .Where(x => x.ReconcilerType is not null)
            .Where(x => silo.Services.GetService(x.ReconcilerType!) is null)
            .Select(x => $"{x.Type} declares {x.ReconcilerType!.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ The driver itself resolves, with every seam its constructor asks for.
    /// </summary>
    [Fact]
    public async Task TheReconcileDriverResolvesInTheSilo() {
        await using var silo = await BuildSiloAsync();

        silo.Services.GetService<ReconcileDriver>().ShouldNotBeNull();
    }

    // ── Composing is not starting, and the difference cost a debugging session ────────────────────

    /// <summary>
    ///     ⚠ Both hosts <b>start</b>, not merely compose.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Every other test in this file passed while <c>CyberCloud.Silo.Host</c> could not
    ///         start at all.</b> Loading the eleven provider modules brings
    ///         <c>AbpDddApplicationModule</c>'s graph in, and with it enough of ASP.NET Core's
    ///         authorization surface that <c>WebApplication</c> inserts <c>UseAuthorization</c> into
    ///         the pipeline by itself. That middleware then looks for the marker only
    ///         <c>AddAuthorization()</c> adds and throws — from <c>ConfigureApplication</c>, which runs
    ///         inside <c>StartAsync</c> and <b>not</b> inside <c>Build</c>. So a suite that stopped at
    ///         <c>Build()</c> was green against a silo that died on every launch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>CyberCloud.AppHost.Tests</c> was the only suite in the repository that caught
    ///         it</b>, because it was the only one that starts the real silo — and it caught it as nine
    ///         fixtures timing out after ten minutes, which names nothing. This test is the cheap
    ///         version: no containers, no Aspire, and the failure is the exception itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The gateway is started against the silo, in that order, because it is an Orleans
    ///         client.</b> Started alone it never reaches its web pipeline at all — the cluster client's
    ///         hosted service exhausts its connection retries and the process dies with a
    ///         <c>TaskCanceledException</c> from <c>OutsideRuntimeClient</c>, which would make this test
    ///         fail for a reason that has nothing to do with what it is asserting.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task BothHostsStartAndNotOnlyCompose() {
        var siloPort = FreePort();
        var gatewayPort = FreePort();

        await using var silo = await SiloComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={siloPort}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}"
            ]
        );

        await silo.StartAsync(TestContext.Current.CancellationToken);

        await using var gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}"
            ]
        );

        await gateway.StartAsync(TestContext.Current.CancellationToken);

        // Resolving the registry from a STARTED host is the second half: the factory runs at first
        // resolve, so a provider whose Describe throws would surface here rather than at composition.
        gateway.Services.GetRequiredService<IProviderRegistry>().Types.ShouldNotBeEmpty();
        silo.Services.GetRequiredService<IProviderRegistry>().Types.ShouldNotBeEmpty();

        await gateway.StopAsync(TestContext.Current.CancellationToken);
        await silo.StopAsync(TestContext.Current.CancellationToken);
    }

    // ── The cluster fabric: a created cluster has to be connectable ───────────────────────────────

    /// <summary>
    ///     ⚠ The silo wires the real cluster seams rather than the refusing defaults.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both defaults are correct and neither is a connection.</b>
    ///         <c>NoClusterConnectionFactory</c> answers <see langword="null" /> to every
    ///         <c>Connect</c>, so <c>ReconcileDriver</c> refuses any type declaring
    ///         <c>RequiresCluster</c> by name; <c>UnavailableClusterConnectionRegistrar</c> refuses
    ///         every attach, so a managed cluster converges and is never registered. Every host in the
    ///         tree held both, and the only implementations that were not refusals were test fakes.
    ///     </para>
    ///     <para>
    ///         ⚠ It checks the concrete types rather than that something resolves, because the
    ///         refusing defaults resolve perfectly well — that is what made their presence invisible.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheSiloWiresTheClusterFabricRatherThanTheRefusingDefaults() {
        await using var silo = await BuildSiloAsync();

        silo.Services
            .GetRequiredService<IClusterConnectionFactory>()
            .ShouldBeOfType<GrainClusterConnectionFactory>();

        silo.Services
            .GetRequiredService<IClusterConnectionRegistrar>()
            .ShouldBeOfType<GrainClusterConnectionRegistrar>();
    }

    /// <summary>
    ///     ⚠ The connection grain is in the silo's manifest, so there is something for the registrar
    ///     to write to.
    /// </summary>
    /// <remarks>
    ///     A registrar that takes a reference to a grain no silo can activate is the same defect as
    ///     the one it replaced, one layer down: the attach would fail at runtime with a message about
    ///     a grain type rather than about wiring. <c>AddCyberCloudKubernetes</c> is what composes it,
    ///     and the <c>ProjectReference</c> is what lets Orleans see it.
    /// </remarks>
    [Fact]
    public async Task TheClusterConnectionGrainIsInTheSilosManifest() {
        await using var silo = await BuildSiloAsync();

        silo.Services
            .GetRequiredService<IOptions<GrainTypeOptions>>()
            .Value
            .Classes
            .ShouldContain(typeof(ClusterConnectionGrain));
    }

    // ── Failure class (d): two hosts driving the same reminder ────────────────────────────────────

    /// <summary>
    ///     ⚠ The gateway holds no reminder service, so it cannot start a second reconcile loop.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The silo owns the loop, and what stops the gateway from starting one is what the
    ///         gateway <i>is</i>.</b> A reconcile tick is a reminder registered by <c>OperationGrain</c>
    ///         (docs/plan/04 § Reminders, item 1). Reminders are registered by grains, grains activate
    ///         on silos, and the gateway is an Orleans client — <c>CreateClient</c>, docs/plan/10
    ///         § Shape — so it activates nothing and has no reminder table to register into. Composing
    ///         the resource manager there registers <c>ReconcileDriver</c> and <c>DriftScanner</c> and
    ///         resolves neither.
    ///     </para>
    ///     <para>
    ///         ⚠ Two <i>silos</i> are not a second driver either, and for a different reason: Orleans
    ///         places one activation of a grain key cluster-wide, so one operation grain drives one
    ///         resource however many silos are running. That is a property of Orleans rather than of
    ///         this wiring, which is why it is stated here and not asserted.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheGatewayHasNoReminderService() {
        await using var gateway = await BuildGatewayAsync();

        gateway.Services.GetService<IReminderService>().ShouldBeNull(
            "the gateway is an Orleans client. A reminder service here would mean a second process "
            + "able to drive a resource's reconcile loop, and a resource driven twice concurrently is "
            + "two reconcilers applying to one cluster."
        );
    }

    // ── The hosts, composed the way Program.cs composes them ─────────────────────────────────────

    /// <summary>Builds the real silo host.</summary>
    /// <remarks>
    ///     Development selects <c>UseLocalhostClustering</c> over <c>UseKubeMembership</c> (ADR-004),
    ///     and the two Orleans ports are picked rather than left at Orleans' 11111/30000 defaults —
    ///     30000 collides with unrelated software often enough that it was hit on the first machine
    ///     this repository ran on, and the failure is an <c>AddressInUseException</c> from a socket
    ///     bind inside Orleans naming neither the port nor the holder.
    /// </remarks>
    static Task<WebApplication> BuildSiloAsync() =>
        SiloComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}"
            ]
        );

    /// <summary>Builds the real gateway host.</summary>
    static Task<WebApplication> BuildGatewayAsync() =>
        GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}"
            ]
        );

    /// <summary>Asks the OS for a port nothing is listening on.</summary>
    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
