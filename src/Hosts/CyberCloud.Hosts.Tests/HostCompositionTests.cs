using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Gateway.Host.Principals;
using CyberCloud.Registry.Feeds.Host;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Contracts;
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
using System.Reflection;
// Orleans has an ErrorCode too, and the Orleans global using arrives with both hosts' reference sets.
using ErrorCode = CyberCloud.Core.ErrorCode;

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
    ///     The sixteen provider namespaces both hosts must serve, spelled out rather than counted.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The prose said "twelve" over a list of fourteen until <c>CyberCloud.Mail</c> made it
    ///         fifteen, and <c>CyberCloud.Communication</c> made it sixteen.
    ///     </b> The list is what the test reads and the list was right; the number beside it
    ///     was three behind, which is the ordinary fate of a count written next to the thing it
    ///     counts. It is corrected rather than deleted because a reader who sees a number can tell at
    ///     a glance whether an entry went missing, and that is the failure this file exists for.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>Typed-out literals, and a count would not do.</b> A count passes when one provider is
    ///     swapped for another, and the failure that produced this file was not "one fewer provider" —
    ///     it was "no providers, and the registry built fine". Naming them makes adding a provider a
    ///     deliberate edit here and removing one a red test.
    /// </remarks>
    static readonly string[] EveryProviderNamespace = [
        "CyberCloud.Analytics",
        "CyberCloud.Cache",
        "CyberCloud.Communication",
        "CyberCloud.Compute",
        "CyberCloud.ContainerRegistry",
        "CyberCloud.ContainerService",
        "CyberCloud.DBforMySQL",
        "CyberCloud.DBforPostgreSQL",
        "CyberCloud.DocumentDB",
        "CyberCloud.Mail",
        "CyberCloud.Messaging",
        "CyberCloud.Monitor",
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
    ///     ⚠ Both hosts hold the two seams <c>CyberCloud.Communication/services</c>' synchronous
    ///     actions reach the sending domain through.
    /// </summary>
    /// <remarks>
    ///     A synchronous action runs inside <c>ResourceManagerService</c>, which in production is the
    ///     gateway's process — so the gateway, which hosts no grain, still has to be able to
    ///     construct <c>IMessageSender</c> and <c>ICommunicationControlPlane</c> over its cluster
    ///     client. <c>AddCyberCloudCommunicationClient</c> is the one line that provides them, and
    ///     this is the test that fails when a host loses it: the handlers are registered by
    ///     concrete type, so without the seams the first <c>send</c> would be a resolution error on
    ///     the request path rather than anything a start-up check could see.
    /// </remarks>
    [Fact]
    public async Task BothHostsResolveTheSeamsTheCommunicationActionsHold() {
        await using var gateway = await BuildGatewayAsync();
        await using var silo = await BuildSiloAsync();

        foreach (var host in new[] { gateway.Services, silo.Services }) {
            host.GetService<CyberCloud.Communication.Contracts.IMessageSender>()
                .ShouldNotBeNull("a host with no IMessageSender cannot serve `send` or `status`");
            host.GetService<CyberCloud.Communication.Contracts.ICommunicationControlPlane>()
                .ShouldNotBeNull("a host with no ICommunicationControlPlane cannot serve `checkSuppression` or `listSuppressions`");
        }
    }

    /// <summary>
    ///     ⚠ Both hosts hold the seam <c>CyberCloud.Monitor/workspaces/alertRules</c>' reconciler
    ///     and its <c>listInstances</c> handler take, and the silo holds the one the evaluator grain
    ///     needs beyond it.
    /// </summary>
    /// <remarks>
    ///     The first provider whose handler reaches a grain from its own family's implementation
    ///     assembly, so the registration is the family's application module's rather than a platform
    ///     module's — <c>MonitorApplicationModule</c> calls <c>AddCyberCloudMonitorAlerting</c> in
    ///     both hosts. The query seam is asserted as the <i>refusing</i> one: no host in this tree
    ///     registers a real VictoriaMetrics client, and a test that only checked for presence would
    ///     pass over a stub that answered "no samples" and paged nobody.
    /// </remarks>
    [Fact]
    public async Task BothHostsResolveTheSeamsTheAlertRulesHold() {
        await using var gateway = await BuildGatewayAsync();
        await using var silo = await BuildSiloAsync();

        foreach (var host in new[] { gateway.Services, silo.Services }) {
            host.GetService<CyberCloud.Providers.Monitor.Contracts.IAlertControlPlane>()
                .ShouldNotBeNull("a host with no IAlertControlPlane cannot converge an alert rule or serve `listInstances`");
            host.GetService<CyberCloud.Providers.Monitor.Contracts.IAlertQuerySeam>()
                .ShouldBeOfType<CyberCloud.Providers.Monitor.Alerting.UnavailableAlertQuerySeam>(
                    "a host registered a query seam this tree does not ship; if it is real, charts/managed/monitor-workspace/conformance.yaml § owed's alert-rules-query-seam-is-refusing closes"
                );
        }

        // The evaluator delivers through the sending module, which the silo hosts and the
        // gateway reaches as a client — the grain activates on the silo only.
        silo.Services.GetService<CyberCloud.Communication.Contracts.IMessageSender>()
            .ShouldNotBeNull("the silo has no IMessageSender, so an alert evaluator cannot be activated");
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

        var thrown = Should.Throw<InvalidOperationException>(provider.GetRequiredService<IProviderRegistry>);

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

        static string[] Types(WebApplication host) => [
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
    ///         <b>
    ///             A <c>ProjectReference</c> that satisfies the compiler and never reaches Orleans is
    ///             the same defect with more steps.
    ///         </b> Orleans discovers grains by scanning referenced
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
    ///         <b>
    ///             Every other test in this file passed while <c>CyberCloud.Silo.Host</c> could not
    ///             start at all.
    ///         </b> Loading the twelve provider modules brings
    ///         <c>AbpDddApplicationModule</c>'s graph in, and with it enough of ASP.NET Core's
    ///         authorization surface that <c>WebApplication</c> inserts <c>UseAuthorization</c> into
    ///         the pipeline by itself. That middleware then looks for the marker only
    ///         <c>AddAuthorization()</c> adds and throws — from <c>ConfigureApplication</c>, which runs
    ///         inside <c>StartAsync</c> and <b>not</b> inside <c>Build</c>. So a suite that stopped at
    ///         <c>Build()</c> was green against a silo that died on every launch.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>CyberCloud.AppHost.Tests</c> was the only suite in the repository that caught
    ///             it
    ///         </b>, because it was the only one that starts the real silo — and it caught it as nine
    ///         fixtures timing out after ten minutes, which names nothing. This test is the cheap
    ///         version: no containers, no Aspire, and the failure is the exception itself.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The gateway is started against the silo, in that order, because it is an Orleans
    ///             client.
    ///         </b> Started alone it never reaches its web pipeline at all — the cluster client's
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
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}",
                IssuerArgument
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

    // ── The principal directory: a grant has to be checkable ─────────────────────────────────────

    /// <summary>
    ///     ⚠ The gateway wires the directory over the identity grains and the silo keeps the refusal,
    ///     asserted on the composed hosts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The same failure class as the cluster seams above, found by the review of #86.</b>
    ///         <c>AddCyberCloudResourceManager</c> <c>TryAdd</c>s <see cref="UnavailablePrincipalDirectory" />,
    ///         and the gateway's real registration was a <c>TryAdd</c> on the line before that call —
    ///         correct, and held only by line order. With the two lines swapped the refusal stayed,
    ///         every <c>PUT</c> on a role assignment answered <c>500</c>, and this suite and the
    ///         gateway's both stayed green: the gateway suite drives the nine stages against a
    ///         recording manager and never calls <c>AddCyberCloudGateway</c>, and nothing here resolved
    ///         the seam. The registration is now a <c>Replace</c>, which wins in either order; this is
    ///         the assertion that says so of the composed host rather than of the file.
    ///     </para>
    ///     <para>
    ///         ⚠ It checks the concrete type rather than that something resolves, because the refusing
    ///         default resolves perfectly well — and it checks the count, because that is the one
    ///         thing <c>Replace</c> buys over an <c>Add</c> that a single resolution cannot tell apart
    ///         (<c>VaultSeamWiringTests.OptingInLeavesNoRefusingResolverBehindIt</c>).
    ///     </para>
    ///     <para>
    ///         ⚠ The silo half is not decoration. <c>GrainPrincipalDirectory</c>'s remarks say a silo
    ///         never serves a grant and keeps the refusal; a silo that resolved the real one would
    ///         mean somebody made it the manager's default, which is the "tempting fix"
    ///         <c>VaultSeamWiringTests.WiringOneSiloDoesNotChangeWhatAnotherSiloGets</c> exists to
    ///         refuse.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheGatewayWiresTheDirectoryAndTheSiloKeepsTheRefusal() {
        await using var gateway = await BuildGatewayAsync();
        await using var silo = await BuildSiloAsync();

        gateway.Services
            .GetRequiredService<IPrincipalDirectory>()
            .ShouldBeOfType<GrainPrincipalDirectory>(
                "the composed gateway must check a grant's principal against the identity grains; the "
                + "refusing default here means every PUT on a role assignment is a 500 — "
                + "https://github.com/Rikarin/CyberCloud/issues/86"
            );

        gateway.Services
            .GetServices<IPrincipalDirectory>()
            .Count()
            .ShouldBe(
                1,
                "the gateway should hold one IPrincipalDirectory, not the real one stacked on a "
                + "refusing one that GetServices would still hand out"
            );

        silo.Services
            .GetRequiredService<IPrincipalDirectory>()
            .ShouldBeOfType<UnavailablePrincipalDirectory>(
                "a silo never serves a grant and must keep the manager's refusing default; the real "
                + "directory resolving here means it became the default for every host"
            );
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

    /// <summary>
    ///     ⚠ A silo given no kubeconfig root reads nothing off its host's disk, and says so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             Reading a kubeconfig off the filesystem is a capability, and this is the assertion
    ///             that it stays opt-in.
    ///         </b> <c>SiloComposition</c> § <c>ConfigureKubeconfigResolver</c>
    ///         registers a resolver only when <c>CyberCloud:Silo:KubeconfigRoot</c> names a directory,
    ///         and <c>CyberCloud.Silo.Host</c>'s shipped <c>appsettings.json</c> sets no such key — so
    ///         a deployed silo keeps <c>KubeApiClientFactory</c>'s refusal until
    ///         <c>CyberCloud.KeyVault</c> (docs/plan/18) can answer for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It drives <c>ConnectAsync</c> rather than reading the registration</b>, because
    ///         the registration is not the property. <c>AddCyberCloudKubernetes</c> registers a
    ///         <c>KubeApiClientFactory</c> either way — the difference is whether its
    ///         <c>ResolveKubeconfig</c> is null, which is an <c>init</c>-only property no test can see
    ///         from the outside. Asserting the type would pass against a silo that reads every path on
    ///         the host.
    ///     </para>
    ///     <para>
    ///         The rooting half — which paths a configured resolver will and will not read — is
    ///         <c>CyberCloud.Silo.Host.Tests</c>' <c>LocalKubeconfigFilesTests</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheSiloReadsNoKubeconfigUntilItIsGivenARoot() {
        await using var silo = await BuildSiloAsync();

        var result = await silo.Services
            .GetRequiredService<IKubeApiClientFactory>()
            .ConnectAsync(
                new() {
                    ClusterId = Guid.NewGuid(),
                    Kind = ClusterConnectionKind.Kubeconfig,
                    CredentialRef = "file:///etc/kubernetes/admin.conf"
                },
                TestContext.Current.CancellationToken
            );

        result.TryGetError(out var error)
            .ShouldBeTrue(
                "a silo with no CyberCloud:Silo:KubeconfigRoot connected to a cluster using a kubeconfig "
                + "path it was never given a root for."
            );

        error!.Code.ShouldBe(ErrorCode.InternalError);
        error.Message.ShouldContain("ResolveKubeconfig");
    }

    /// <summary>
    ///     ⚠ A silo given a root resolves inside it, and refuses outside it — through the composed
    ///     factory rather than through the resolver on its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The two halves are one test because the wiring is what joins them.</b>
    ///     <c>ConfigureKubeconfigResolver</c> runs <i>before</i> <c>AddCyberCloudKubernetes</c> and
    ///     both registrations are <c>TryAdd</c>, so the order is what decides whether the configured
    ///     resolver or the refusing default wins. Reverse those two lines and every assertion in
    ///     <c>LocalKubeconfigFilesTests</c> still passes while the silo reads nothing.
    /// </remarks>
    [Fact]
    public async Task TheSiloGivenARootResolvesInsideItAndRefusesOutsideIt() {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "cc-silo-kubeconfig-" + Guid.NewGuid().ToString("N"))
        );

        try {
            var config = Path.Combine(root.FullName, "kubeconfig.yaml");
            await File.WriteAllTextAsync(config, "not a kubeconfig", TestContext.Current.CancellationToken);

            await using var silo = await SiloComposition.BuildAsync(
                [
                    "--environment", "Development",
                    "--urls", "http://127.0.0.1:0",
                    $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                    $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                    "--CyberCloud:Silo:KubeconfigRoot=" + root.FullName
                ]
            );

            var factory = silo.Services.GetRequiredService<IKubeApiClientFactory>();

            // Inside the root: the file is read, and the failure that follows is about the YAML
            // rather than about the resolver. That is the whole difference being asserted — a silo
            // with no root never gets far enough to complain about the contents.
            var inside = await factory.ConnectAsync(
                Descriptor(new Uri(config).AbsoluteUri),
                TestContext.Current.CancellationToken
            );

            inside.TryGetError(out var read).ShouldBeTrue();
            read!.Message.ShouldNotContain("ResolveKubeconfig");

            // Outside it: refused before the filesystem is touched, and the refusal names the root.
            var outside = await factory.ConnectAsync(
                Descriptor("file:///etc/kubernetes/admin.conf"),
                TestContext.Current.CancellationToken
            );

            outside.TryGetError(out var refused).ShouldBeTrue();
            refused!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
            refused.Message.ShouldContain(root.FullName);
        } finally {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A kubeconfig-kind connection naming one credential reference.</summary>
    static ClusterConnectionDescriptor Descriptor(string credentialRef) =>
        new() { ClusterId = Guid.NewGuid(), Kind = ClusterConnectionKind.Kubeconfig, CredentialRef = credentialRef };

    // ── Failure class (d): two hosts driving the same reminder ────────────────────────────────────

    /// <summary>
    ///     ⚠ The gateway holds no reminder service, so it cannot start a second reconcile loop.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>
    ///             The silo owns the loop, and what stops the gateway from starting one is what the
    ///             gateway <i>is</i>.
    ///         </b> A reconcile tick is a reminder registered by <c>OperationGrain</c>
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

        gateway.Services.GetService<IReminderService>()
            .ShouldBeNull(
                "the gateway is an Orleans client. A reminder service here would mean a second process "
                + "able to drive a resource's reconcile loop, and a resource driven twice concurrently is "
                + "two reconcilers applying to one cluster."
            );
    }

    // ── Failure class (e): a seam that exists, is correct, and has no registration ───────────────

    /// <summary>
    ///     ⚠ The gateway refuses to compose with no identity host to validate tokens against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is https://github.com/Rikarin/CyberCloud/issues/68, as a red test.</b> The
    ///         deployed gateway registered no <c>ICallerContextResolver</c>, built, started, passed
    ///         <c>/health</c> and answered <c>500</c> to every real request — and this file was one
    ///         of two suites composing that exact host and staying green, because
    ///         <c>UseAutofac()</c> replaces the provider factory <c>ValidateOnBuild</c> belongs to,
    ///         so nothing resolved the pipeline until the first request did.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted on the message naming the section, not only on the type: an operator meets
    ///         this exception in a crash loop, and a crash loop that names
    ///         <c>CyberCloud:Gateway:Identity:Issuer</c> is one they can fix.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheGatewayRefusesToComposeWithoutAnIdentityIssuer() {
        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            GatewayComposition.BuildAsync(
                [
                    "--environment", "Development",
                    "--urls", "http://127.0.0.1:0",
                    $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                    // ⚠ Blanked explicitly, because the shipped appsettings.json carries the
                    // production origin and this test is about a deployment that removed it.
                    "--CyberCloud:Gateway:Identity:Issuer="
                ]
            )
        );

        thrown.Message.ShouldContain("ICallerContextResolver");
        thrown.Message.ShouldContain("CyberCloud:Gateway:Identity:Issuer");
    }

    /// <summary>
    ///     ⚠ With an issuer, the gateway's stage 2 is the JWKS validator and not a stand-in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Checked by the implementation's name, because "something resolves" is exactly the
    ///         assertion that would pass against the in-process token table the issue warns against
    ///         — and by reflection, because both the seam and its implementation are <c>internal</c>
    ///         to the gateway and this suite deliberately sees no internals. Widening
    ///         <c>InternalsVisibleTo</c> to a second suite for one assertion is the shape the issue
    ///         asked not to take casually; a type name is a smaller thing to reach for.
    ///     </para>
    ///     <para>
    ///         ⚠ Resolving it is also the second half of the guard: the guard checks that a
    ///         registration exists, and this checks that the registration can be constructed —
    ///         with the validation service, the clock and the logger its constructor asks for.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheGatewayValidatesBearerTokensAgainstTheIdentityHostsKeySet() {
        await using var gateway = await BuildGatewayAsync();

        var seam = typeof(GatewayComposition).Assembly
            .GetType("CyberCloud.Gateway.Host.Authentication.ICallerContextResolver", throwOnError: true)!;

        gateway.Services
            .GetRequiredService(seam)
            .GetType()
            .Name
            .ShouldBe(
                "JwksCallerContextResolver",
                "stage 2 must validate against the identity host's published key set; any other "
                + "implementation in the composed production gateway is a token table somebody wired "
                + "by mistake — https://github.com/Rikarin/CyberCloud/issues/68."
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

    /// <summary>
    ///     The identity host this suite's gateways are told to trust, as the argument that names it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Supplied the way a deployment supplies it, and not through <c>configure</c>.
    ///         </b> <c>GatewayComposition.BuildAsync</c> registers stage 2's resolver from
    ///         <c>CyberCloud:Gateway:Identity:Issuer</c> and refuses to compose without one, so a
    ///         suite that composes the real gateway has to say which identity host it trusts — and
    ///         saying it here, in configuration, means every test in this file composes the exact
    ///         object graph <c>Program.cs</c> composes, resolver included. This file used to build
    ///         the gateway with no resolver at all, and stayed green, which is the quiet the guard
    ///         exists to end.
    ///     </para>
    ///     <para>
    ///         ⚠ The origin is a port nothing listens on, and that is fine: the resolver fetches the
    ///         discovery document on the first token it is handed, and nothing in this file sends a
    ///         request. A suite that did would need the real identity host —
    ///         <c>CyberCloud.AppHost.Tests</c>' <c>TenantOverHttpTests</c> starts one.
    ///     </para>
    /// </remarks>
    const string IssuerArgument = "--CyberCloud:Gateway:Identity:Issuer=http://127.0.0.1:1";

    /// <summary>Builds the real gateway host.</summary>
    static Task<WebApplication> BuildGatewayAsync() =>
        GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                IssuerArgument
            ]
        );

    // ── The feeds host — docs/plan/13 § Artifact feeds, the third bearer-token host ─────────────

    /// <summary>
    ///     ⚠ The feeds host composes a registry of exactly one family, and that family's two types.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Not the silo's fifteen, and the difference is asserted rather than tolerated. The
    ///         gateway needs every provider because it routes every path; this host reads one type
    ///         through <c>IResourceManager.ReadAsync</c> and would put fourteen implementation
    ///         assemblies into a data-plane process for nothing. <c>FeedsHostModule</c> makes the
    ///         argument; this is what holds it to one line.
    ///     </para>
    ///     <para>
    ///         ⚠ Both types of the family and not the feeds type alone, because the module registers
    ///         the provider and a provider's <c>Describe</c> is not divisible. A host that could load
    ///         half a family would be a registry that disagrees with the silo's about the family's
    ///         shape.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheFeedsHostComposesTheContainerRegistryFamilyAndNoOther() {
        await using var feeds = await BuildFeedsAsync();

        var registry = feeds.Services.GetRequiredService<IProviderRegistry>();

        registry.Namespaces.ShouldBe(["CyberCloud.ContainerRegistry"]);

        registry.Types
            .Select(x => x.Type.ToString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(["CyberCloud.ContainerRegistry/feeds", "CyberCloud.ContainerRegistry/registries"]);
    }

    /// <summary>
    ///     ⚠ The feeds host's registration of the feeds type is the silo's, type for type.
    /// </summary>
    /// <remarks>
    ///     The same class-(b) failure the silo-and-gateway assertion above guards against, one host
    ///     over: a feeds host that read a registration the silo did not reconcile would resolve
    ///     feeds the silo never opened. Compared on the one type this host serves.
    /// </remarks>
    [Fact]
    public async Task TheFeedsHostAndTheSiloAgreeAboutTheFeedsType() {
        await using var silo = await BuildSiloAsync();
        await using var feeds = await BuildFeedsAsync();

        var type = new ResourceTypeName("CyberCloud.ContainerRegistry", "feeds");

        silo.Services.GetRequiredService<IProviderRegistry>().TryGetType(type, out var atSilo).ShouldBeTrue();
        feeds.Services.GetRequiredService<IProviderRegistry>().TryGetType(type, out var atFeeds).ShouldBeTrue();

        atFeeds.ReconcilerType.ShouldBe(atSilo.ReconcilerType);
        atFeeds.RequiresCluster.ShouldBe(atSilo.RequiresCluster);
        atFeeds.ReadPermission.ShouldBe(atSilo.ReadPermission);
        atFeeds.WritePermission.ShouldBe(atSilo.WritePermission);
        atFeeds.ApiVersions.Select(x => x.Version).ShouldBe(atSilo.ApiVersions.Select(x => x.Version));
    }

    /// <summary>
    ///     ⚠ The feeds host refuses to compose with no identity host to validate tokens against —
    ///     the same refusal as the gateway's, for the same #68 reason.
    /// </summary>
    [Fact]
    public async Task TheFeedsHostRefusesToComposeWithoutAnIdentityIssuer() {
        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            FeedsComposition.BuildAsync(
                [
                    "--environment", "Development",
                    "--urls", "http://127.0.0.1:0",
                    $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                    "--CyberCloud:Feeds:Identity:Issuer="
                ]
            )
        );

        thrown.Message.ShouldContain("IBearerTokenValidator");
        thrown.Message.ShouldContain("CyberCloud:Feeds:Identity:Issuer");
    }

    /// <summary>
    ///     ⚠ With an issuer, both bearer-token hosts validate through the SAME JWKS validator.
    /// </summary>
    /// <remarks>
    ///     By type name, for the reason the gateway assertion gives. The feeds host resolves
    ///     <c>IBearerTokenValidator</c> directly; the gateway resolves it behind its stage-2 adapter,
    ///     and the adapter's dependency is asserted here too so that the two hosts cannot drift onto
    ///     two validators for one token.
    /// </remarks>
    [Fact]
    public async Task BothBearerTokenHostsValidateThroughTheOneJwksValidator() {
        await using var feeds = await BuildFeedsAsync();
        await using var gateway = await BuildGatewayAsync();

        var seam = typeof(FeedsComposition).Assembly
            .GetReferencedAssemblies()
            .Select(Assembly.Load)
            .Select(x => x.GetType("CyberCloud.Identity.Validation.IBearerTokenValidator"))
            .First(x => x is not null)!;

        feeds.Services.GetRequiredService(seam).GetType().Name.ShouldBe("JwksBearerTokenValidator");
        gateway.Services.GetRequiredService(seam).GetType().Name.ShouldBe("JwksBearerTokenValidator");
    }

    /// <summary>
    ///     ⚠ The two hosts that touch a feed's bytes — the feeds host to store, the silo to tear
    ///     down — both wire the S3 store when <c>CyberCloud:ObjectStorage</c> is configured, and
    ///     both keep the refusing default when it is not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         By concrete type, for the reason <see cref="TheSiloWiresTheClusterFabricRatherThanTheRefusingDefaults" />
    ///         gives: <c>UnavailableObjectStore</c> resolves perfectly well, and a silo left with it
    ///         refuses every feed teardown with a message about configuration rather than converging
    ///         over bytes it never removed. That refusal is the right default and the wrong
    ///         production — so the configured half of this test is what a deployment relies on and
    ///         the unconfigured half is what a developer machine relies on.
    ///     </para>
    ///     <para>
    ///         ⚠ The endpoint is plain http with <c>AllowInsecureTransport</c> set, because without
    ///         the flag <c>AddS3ObjectStore</c> throws at composition — which is its own contract and
    ///         <c>ObjectStorageWiringTests</c>' to hold, not this test's. Nothing here connects to
    ///         the endpoint: composition builds the store, it does not call it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task BothHostsThatTouchAFeedsBytesWireTheS3StoreOnlyWhenObjectStorageIsConfigured() {
        await using var bareSilo = await BuildSiloAsync();
        await using var bareFeeds = await BuildFeedsAsync();

        bareSilo.Services.GetRequiredService<IObjectStore>().ShouldBeOfType<UnavailableObjectStore>();
        bareFeeds.Services.GetRequiredService<IObjectStore>().ShouldBeOfType<UnavailableObjectStore>();

        await using var silo = await SiloComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                .. ObjectStorageArguments
            ]
        );

        await using var feeds = await FeedsComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                FeedsIssuerArgument,
                .. ObjectStorageArguments
            ]
        );

        silo.Services.GetRequiredService<IObjectStore>().GetType().Name.ShouldBe("S3ObjectStore");
        feeds.Services.GetRequiredService<IObjectStore>().GetType().Name.ShouldBe("S3ObjectStore");
    }

    /// <summary>
    ///     A complete <c>CyberCloud:ObjectStorage</c> section, as the arguments that spell it. ⚠ The
    ///     credential is a placeholder for a store nothing connects to.
    /// </summary>
    static readonly string[] ObjectStorageArguments = [
        "--CyberCloud:ObjectStorage:Endpoint=http://127.0.0.1:1",
        "--CyberCloud:ObjectStorage:Bucket=cybercloud-feeds",
        "--CyberCloud:ObjectStorage:AccessKeyId=composition-test",
        "--CyberCloud:ObjectStorage:SecretAccessKey=composition-test",
        "--CyberCloud:ObjectStorage:AllowInsecureTransport=true"
    ];

    /// <summary>
    ///     ⚠ The feeds host <b>starts</b> against a silo, for the reason the gateway assertion above
    ///     gives: composing is not starting, and this host loads a provider module the way the silo
    ///     does, which is the path that inserts <c>UseAuthorization</c> and throws on
    ///     <c>StartAsync</c> without <c>AddAuthorization</c>.
    /// </summary>
    [Fact]
    public async Task TheFeedsHostStartsAndNotOnlyComposes() {
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

        await using var feeds = await FeedsComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={gatewayPort}",
                FeedsIssuerArgument
            ]
        );

        feeds.MapFeeds();
        await feeds.StartAsync(TestContext.Current.CancellationToken);

        feeds.Services.GetRequiredService<IProviderRegistry>().Types.ShouldNotBeEmpty();

        await feeds.StopAsync(TestContext.Current.CancellationToken);
        await silo.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The identity host the feeds host is told to trust — the feeds section's spelling of <see cref="IssuerArgument" />.</summary>
    const string FeedsIssuerArgument = "--CyberCloud:Feeds:Identity:Issuer=http://127.0.0.1:1";

    /// <summary>Builds the real feeds host.</summary>
    static Task<WebApplication> BuildFeedsAsync() =>
        FeedsComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
                FeedsIssuerArgument
            ]
        );

    /// <summary>Asks the OS for a port nothing is listening on.</summary>
    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
