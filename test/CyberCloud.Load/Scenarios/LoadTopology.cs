using CyberCloud.Authorization.Contracts;
using CyberCloud.Chaos.Topology;
using CyberCloud.Gateway.Host;
using CyberCloud.Providers.Sample.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace CyberCloud.Load.Scenarios;

/// <summary>One caller: a service principal with an owner grant on its tenant and a token the gateway accepts.</summary>
/// <param name="World">The tenant and subscription it acts in.</param>
/// <param name="SubjectId">The service principal's id.</param>
/// <param name="Token">A bearer token from <see cref="StandInIssuer" />.</param>
public sealed record Caller(TenantWorld World, string SubjectId, string Token);

/// <summary>
///     The chaos suite's cluster with the real gateway in front of it, populated for load: tenants,
///     subscriptions, callers, and converged widgets to read.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The shape of the population is dictated by the gateway's own rate limits</b>, which
///         are production behaviour and are not turned off: 600 requests a minute per subject,
///         12 000 reads and 1 200 writes per five minutes per subscription, 30 000 per five minutes
///         per tenant (<c>RateLimitBuckets</c>). 500 rps of reads therefore needs at least fifty
///         subjects across at least thirteen subscriptions across at least five tenants, and 50
///         writes a second needs the same thirteen subscriptions. Ten tenants, two subscriptions
///         each, eight callers each is enough headroom that a 429 in the results is a finding
///         rather than a driver mistake.
///     </para>
///     <para>
///         ⚠ <b>Tenants are split across both durable shards</b> by the same hash the silos use, so
///         the writes land on two PostgreSQLs rather than one, which is what a fifty-shard
///         deployment scaled to a tenth looks like.
///     </para>
/// </remarks>
public sealed class LoadTopology : IAsyncLifetime {
    /// <summary>How many tenants the population has.</summary>
    public const int Tenants = 10;

    /// <summary>Subscriptions per tenant.</summary>
    public const int SubscriptionsPerTenant = 2;

    /// <summary>Callers per tenant; each is a service principal with its own token and its own rate-limit bucket.</summary>
    public const int CallersPerTenant = 8;

    /// <summary>Converged widgets per subscription, for the read scenario to read.</summary>
    public const int WidgetsPerSubscription = 20;

    readonly StandInIssuer issuer = new();
    WebApplication gateway = null!;

    /// <summary>The cluster underneath.</summary>
    public ChaosTopology Platform { get; } = new();

    /// <summary>The numbers.</summary>
    public LoadReport Report { get; } = new();

    /// <summary>A client pointed at the gateway. Thread-safe; one for the whole run.</summary>
    public HttpClient Http { get; private set; } = null!;

    /// <summary>Every subscription, as a world addressed through it.</summary>
    public IReadOnlyList<TenantWorld> Subscriptions { get; private set; } = [];

    /// <summary>Every caller, across every tenant.</summary>
    public IReadOnlyList<Caller> Callers { get; private set; } = [];

    /// <summary>The seeded widgets' names, per subscription.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Widgets { get; private set; } =
        new Dictionary<Guid, IReadOnlyList<string>>();

    /// <summary>
    ///     <c>src/Hosts/CyberCloud.Gateway.Host</c> — the directory whose <c>appsettings.json</c> the
    ///     gateway runs on, walked up to from the test assembly through <c>CyberCloud.slnx</c>.
    /// </summary>
    static string GatewayHostDirectory {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
                directory = directory.Parent;
            }

            return Path.Combine(
                directory?.FullName
                ?? throw new InvalidOperationException(
                    "No CyberCloud.slnx above "
                    + AppContext.BaseDirectory
                    + ", so the gateway host's appsettings.json cannot be found."
                ),
                "src",
                "Hosts",
                "CyberCloud.Gateway.Host"
            );
        }
    }

    /// <summary>Facts about what the numbers were measured against.</summary>
    public IReadOnlyDictionary<string, string> Facts =>
        new Dictionary<string, string>(Platform.Facts, StringComparer.Ordinal) {
            ["tenants"] = Tenants.ToString(CultureInfo.InvariantCulture),
            ["subscriptions"] = (Tenants * SubscriptionsPerTenant).ToString(CultureInfo.InvariantCulture),
            ["callers"] = (Tenants * CallersPerTenant).ToString(CultureInfo.InvariantCulture),
            ["seededWidgets"] = (Tenants * SubscriptionsPerTenant * WidgetsPerSubscription).ToString(
                CultureInfo.InvariantCulture
            ),
            ["gateway"] =
                "CyberCloud.Gateway.Host over HTTP/1.1 on loopback, JWKS validation against a stand-in issuer",
            ["scale"] = LoadReport.Scale.ToString(CultureInfo.InvariantCulture)
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;
        await Platform.InitializeAsync();
        await issuer.StartAsync(token);

        Console.WriteLine(
            $"[CyberCloud.Load] silos: {string.Join(", ", Platform.Silos)}; primary gateway address "
            + $"{Platform.Cluster.Primary.GatewayAddress}; BaseGatewayPort {Platform.Cluster.Options.BaseGatewayPort}; "
            + $"the gateway will connect to 127.0.0.1:{Platform.GatewayPort}"
        );

        // ⚠ The content root is the gateway host's own directory, so the gateway runs on the
        // appsettings.json it ships with — Serilog's "Microsoft": "Warning" in particular. Without
        // it the content root is this test host's, no appsettings.json is found, and ASP.NET Core's
        // request logging writes two Information lines to the console per request: the third load
        // run measured a read p99 of 74.8 ms with 30 000 console lines competing for the same
        // stdout, which is a number about the console, not the gateway.
        gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--contentRoot", GatewayHostDirectory,
                "--urls", "http://127.0.0.1:0",
                "--CyberCloud:Cluster:LocalhostGatewayPort="
                + Platform.GatewayPort.ToString(CultureInfo.InvariantCulture),
                "--CyberCloud:Gateway:Identity:Issuer=" + issuer.Issuer
            ]
        );

        gateway.MapGateway();
        await gateway.StartAsync(token);

        var address = gateway.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        Http = new(
            new SocketsHttpHandler {
                MaxConnectionsPerServer = 256, PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            }
        ) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(60) };

        await PopulateAsync(token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        try {
            Report.Write(Facts);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.WriteLine($"[CyberCloud.Load] the results file could not be written: {ex.Message}");
        }

        Http?.Dispose();

        if (gateway is not null) {
            await gateway.StopAsync();
            await gateway.DisposeAsync();
        }

        await issuer.DisposeAsync();
        await Platform.DisposeAsync();
    }

    /// <summary>The path a widget has on the wire, with the api-version the gateway routes on.</summary>
    /// <param name="world">The subscription.</param>
    /// <param name="name">The widget's name.</param>
    public static string WidgetPath(TenantWorld world, string name) {
        ArgumentNullException.ThrowIfNull(world);
        return world.Widget(name).Path + "?api-version=" + SampleWidgets.V2026;
    }

    /// <summary>A GET of a widget through the gateway, as a tenant's client would send it.</summary>
    /// <param name="caller">Who asks.</param>
    /// <param name="world">The subscription the widget is in.</param>
    /// <param name="name">The widget's name.</param>
    public HttpRequestMessage Get(Caller caller, TenantWorld world, string name) {
        ArgumentNullException.ThrowIfNull(caller);
        var request = new HttpRequestMessage(HttpMethod.Get, WidgetPath(world, name));
        request.Headers.Authorization = new("Bearer", caller.Token);
        return request;
    }

    /// <summary>A PUT of a widget through the gateway.</summary>
    /// <param name="caller">Who asks.</param>
    /// <param name="world">The subscription the widget is in.</param>
    /// <param name="name">The widget's name.</param>
    /// <param name="message">What the ConfigMap will say.</param>
    public HttpRequestMessage Put(Caller caller, TenantWorld world, string name, string message) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(world);

        var request = new HttpRequestMessage(HttpMethod.Put, WidgetPath(world, name)) {
            Content = new StringContent(
                SampleWidgets.Body(world.Cluster, message),
                System.Text.Encoding.UTF8,
                "application/json"
            )
        };

        request.Headers.Authorization = new("Bearer", caller.Token);
        return request;
    }

    /// <summary>The callers whose tenant a subscription belongs to.</summary>
    /// <param name="world">The subscription.</param>
    public IReadOnlyList<Caller> CallersOf(TenantWorld world) {
        ArgumentNullException.ThrowIfNull(world);
        return [.. Callers.Where(x => x.World.Tenant == world.Tenant)];
    }

    async Task PopulateAsync(CancellationToken cancellationToken) {
        var subscriptions = new List<TenantWorld>();
        var callers = new List<Caller>();

        for (var t = 0; t < Tenants; t++) {
            var shard = t % 2 == 0 ? ChaosTopology.ShardA : ChaosTopology.ShardB;
            var first = await Platform.CreateTenantAsync(ChaosTopology.TenantOn(shard), $"load-{t}", cancellationToken);
            subscriptions.Add(first);

            for (var s = 1; s < SubscriptionsPerTenant; s++) {
                subscriptions.Add(await Platform.AddSubscriptionAsync(first, $"load-{t}-{s}", cancellationToken));
            }

            for (var c = 0; c < CallersPerTenant; c++) {
                var subject = $"load-{t}-caller-{c}";
                await Platform.GrantOwnerAsync(first, SubjectTypes.ServicePrincipal, subject);
                callers.Add(new(first, subject, issuer.Mint(first.Tenant, SubjectTypes.ServicePrincipal, subject)));
            }
        }

        Subscriptions = subscriptions;
        Callers = callers;

        // The widgets the read scenario reads, converged through the write path and the client-side
        // drive, sixteen at a time.
        var widgets = new Dictionary<Guid, IReadOnlyList<string>>();
        var names = Enumerable.Range(0, WidgetsPerSubscription).Select(static i => $"w{i}").ToList();

        foreach (var world in subscriptions) {
            widgets[world.Subscription] = names;
        }

        using var gate = new SemaphoreSlim(16);

        await Task.WhenAll(
            subscriptions.SelectMany(world => names.Select(name => (world, name)))
                .Select(async x => {
                        await gate.WaitAsync(cancellationToken);

                        try {
                            var accepted = (await Platform.PutWidgetAsync(
                                    x.world,
                                    x.name,
                                    "seed",
                                    cancellationToken
                                )).GetValueOrThrow();
                            var (last, _) = await Platform.DriveUntilTerminalAsync(
                                x.world.Tenant,
                                accepted.OperationId,
                                TimeSpan.FromMinutes(3),
                                cancellationToken
                            );

                            if (last?.State != OperationState.Succeeded) {
                                throw new InvalidOperationException(
                                    $"seeding {x.world.Group}/{x.name} ended {last?.State}: {last?.Error?.Message}"
                                );
                            }
                        } finally {
                            gate.Release();
                        }
                    }
                )
        );

        Widgets = widgets;

        // ⚠ The gateway resolves a tenant from its directory CACHE, which TenantDirectoryRefreshService
        // pulls every TenancyRefreshOptions.DirectoryInterval; a tenant created a second ago is a
        // 404 at the gateway until the next pull. Every subscription is read through the gateway
        // until it answers 200, so the first measured request is not the cache's warm-up.
        var probing = System.Diagnostics.Stopwatch.StartNew();

        foreach (var world in subscriptions) {
            var caller = callers.First(x => x.World.Tenant == world.Tenant);

            while (true) {
                using var response = await Http.SendAsync(Get(caller, world, names[0]), cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.OK) {
                    break;
                }

                if (probing.Elapsed > TimeSpan.FromMinutes(2)) {
                    throw new InvalidOperationException(
                        $"the gateway answered {(int)response.StatusCode} for {world.Group}/{names[0]} two minutes after the population was "
                        + $"created: {await response.Content.ReadAsStringAsync(cancellationToken)}"
                    );
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        Console.WriteLine(
            $"[CyberCloud.Load] populated {subscriptions.Count} subscriptions, {callers.Count} callers, "
            + $"{subscriptions.Count * names.Count} widgets; the gateway resolved every tenant within {probing.Elapsed.TotalSeconds:F0} s"
        );
    }
}

/// <summary>Binds <see cref="LoadTopology" /> to every scenario's class.</summary>
[CollectionDefinition(Name)]
public sealed class LoadSuite : ICollectionFixture<LoadTopology> {
    /// <summary>The collection's name.</summary>
    public const string Name = "load-topology";
}
