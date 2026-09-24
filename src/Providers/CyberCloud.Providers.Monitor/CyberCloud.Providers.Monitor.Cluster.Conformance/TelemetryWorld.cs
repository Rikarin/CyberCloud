using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Providers.Monitor.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Providers.Monitor.Telemetry;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Shouldly;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The component case, over a harness whose action dispatcher reaches the ClickHouse
///     <see cref="TelemetryWorld" /> started.
/// </summary>
/// <remarks>
///     A separate source from <c>MonitorComponentCase</c> so its harness state — keyed by the source
///     type — is its own: the Docker-free suite keeps the refusing store, this one gets the real one.
/// </remarks>
public sealed class ComponentViewsCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => MonitorComponentCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors => MonitorComponentCase.Ancestors;

    /// <inheritdoc />
    public static void ConfigureSilo(ISiloBuilder silo) => MonitorComponentCase.ConfigureSilo(silo);

    /// <inheritdoc />
    /// <remarks>
    ///     Resolved lazily, at the first view: the harness builds this container before the world has
    ///     a ClickHouse to point at.
    /// </remarks>
    public static void ConfigureHandlers(IServiceCollection services) =>
        services.AddSingleton<ITelemetryStore>(static _ => TelemetryWorld.Store);
}

/// <summary>
///     A ClickHouse and the pinned OpenTelemetry collector on one Docker network, with the platform's
///     tables in each workspace's database and telemetry the test emitted flowing through the real
///     exporter into them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The collector runs the configuration <see cref="MonitorCollectors.CollectorConfig" />
///             renders, with one line changed.
///         </b> The rendered exporter points at <see cref="MonitorWorkspaces.QueryHost" /> over
///         HTTPS, a platform address nothing provisions; here it points at the container's native
///         port on the network. Everything else — <c>create_schema: false</c>, the database and the
///         key from the environment the kubelet would fill, the username the workspace's
///         <c>VMUser</c> name, <c>memory_limiter</c> and <c>batch</c> — is what a tenant's collector
///         runs.
///     </para>
///     <para>
///         ⚠ <b>One collector per workspace database</b>, because the database is an environment
///         variable read at start — the same one-collector-one-workspace shape the resource has.
///     </para>
/// </remarks>
public sealed class TelemetryWorld : IAsyncLifetime {
    /// <summary>The ClickHouse image — the one the resource graph's end-to-end suite runs.</summary>
    public const string ClickHouseImage = "clickhouse/clickhouse-server:25.3-alpine";

    const string Admin = "admin";
    const string AdminPassword = "telemetry-world-admin";

    /// <summary>The views' read user — what <c>CyberCloud:Monitor:Telemetry</c> names in a region.</summary>
    const string Reader = "views";

    const string ReaderPassword = "telemetry-world-views";

    /// <summary>The ingest key both collectors present, as the workspace's <c>Secret</c> would hold it.</summary>
    const string IngestKey = "telemetry-world-ingest-key";

    static ITelemetryStore? store;

    readonly List<IContainer> collectors = [];
    readonly SemaphoreSlim feeding = new(1, 1);
    readonly HttpClient admin = new() { Timeout = TimeSpan.FromSeconds(60) };
    INetwork? network;
    IContainer? clickHouse;
    bool fed;

    /// <summary>The store the handler reads through: the real ClickHouse one, counting what it is asked.</summary>
    /// <exception cref="InvalidOperationException">No world has started.</exception>
    public static ITelemetryStore Store =>
        store ?? throw new InvalidOperationException("TelemetryWorld has not started, so there is no store to read.");

    /// <summary>Every query the handler's store has been asked, by workspace database.</summary>
    public static ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>Why the world could not start, or <see langword="null" /> when it did.</summary>
    public Exception? Unavailable { get; private set; }

    /// <summary>The trace ids the feed wrote into tenant A's <c>shop</c>, in emission order.</summary>
    public ImmutableArray<string> ShopTraces { get; private set; } = [];

    /// <summary>Tenant A's workspace, GUID resolved.</summary>
    public ResourceId WorkspaceA { get; private set; }

    /// <summary>Tenant B's workspace, GUID resolved.</summary>
    public ResourceId WorkspaceB { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        try {
            network = new NetworkBuilder().WithName("cc32-telemetry-" + Guid.NewGuid().ToString("N")[..8]).Build();
            await network.CreateAsync();

            clickHouse = new ContainerBuilder(ClickHouseImage)
                .WithNetwork(network)
                .WithNetworkAliases("clickhouse")
                .WithPortBinding(8123, true)
                .WithEnvironment("CLICKHOUSE_USER", Admin)
                .WithEnvironment("CLICKHOUSE_PASSWORD", AdminPassword)
                .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
                .WithWaitStrategy(
                    Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8123).ForPath("/ping"))
                )
                .Build();

            await clickHouse.StartAsync();

            store = new CountingStore(
                new ClickHouseTelemetryStore(
                    new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                    new() {
                        ClickHouseEndpoint = Endpoint,
                        ClickHouseUser = Reader,
                        ClickHousePassword = ReaderPassword,
                        AllowInsecureTransport = true
                    }
                )
            );
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            Unavailable = exception;
        }
    }

    /// <summary>ClickHouse's HTTP interface as this process reaches it.</summary>
    string Endpoint => $"http://{clickHouse!.Hostname}:{clickHouse.GetMappedPublicPort(8123)}";

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        foreach (var collector in collectors) {
            await collector.DisposeAsync();
        }

        if (clickHouse is not null) {
            await clickHouse.DisposeAsync();
        }

        if (network is not null) {
            await network.DisposeAsync();
        }

        admin.Dispose();
        feeding.Dispose();
        store = null;
    }

    /// <summary>
    ///     Creates both tenants' workspace tables, starts a collector per workspace, emits the
    ///     telemetry every assertion reads, and waits for the exporter to land all of it. Once.
    /// </summary>
    /// <param name="cluster">The harness whose workspaces the databases belong to.</param>
    public async Task FeedAsync(ProviderTestCluster<ComponentViewsCase> cluster) {
        ArgumentNullException.ThrowIfNull(cluster);

        await feeding.WaitAsync();
        try {
            if (fed) {
                return;
            }

            WorkspaceA = await ResolveWorkspaceAsync(cluster, ConformanceIds.Tenant, ConformanceIds.Subscription);
            WorkspaceB = await ResolveWorkspaceAsync(cluster, ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription);

            var databaseA = MonitorWorkspaces.Database(WorkspaceA);
            var databaseB = MonitorWorkspaces.Database(WorkspaceB);

            // ⚠ The workspace's VMUser name is the exporter's username, and both tenants' workspaces
            // are called ancestor-0 — so one ClickHouse user serves both here. That is a real property
            // of the collector's rendering (charts/managed/monitor-collector/conformance.yaml § owed,
            // exporter-username-is-not-tenant-unique), not a shortcut this test takes.
            var exporterUser = MonitorWorkspaces.VmUserName(ConformanceIds.AncestorName(0));

            foreach (var database in new[] { databaseA, databaseB }) {
                foreach (var statement in MonitorTelemetrySchema.Statements(database)) {
                    await SqlAsync(statement);
                }
            }

            await SqlAsync($"CREATE USER IF NOT EXISTS `{exporterUser}` IDENTIFIED BY '{IngestKey}'");
            await SqlAsync($"CREATE USER IF NOT EXISTS {Reader} IDENTIFIED BY '{ReaderPassword}'");

            foreach (var database in new[] { databaseA, databaseB }) {
                await SqlAsync($"GRANT SELECT, INSERT ON {database}.* TO `{exporterUser}`");
                await SqlAsync($"GRANT SELECT ON {database}.* TO {Reader}");
            }

            // The exporter's own tables, for the shape comparison: the same rendered configuration with
            // create_schema switched on, into a database nothing of the platform's touches.
            await SqlAsync($"CREATE DATABASE IF NOT EXISTS {ExporterOwned}");
            await SqlAsync($"GRANT ALL ON {ExporterOwned}.* TO `{exporterUser}`");

            var collectorA = await StartCollectorAsync(databaseA, createSchema: false);
            var collectorB = await StartCollectorAsync(databaseB, createSchema: false);
            var exporterOwned = await StartCollectorAsync(ExporterOwned, createSchema: true);

            var feed = new TelemetryFeed(DateTimeOffset.UtcNow.AddMinutes(-10));
            ShopTraces = feed.Shop();
            feed.Admin();

            await PostAsync(collectorA, "/v1/traces", feed.TracesJson());
            await PostAsync(collectorA, "/v1/logs", feed.LogsJson());

            var other = new TelemetryFeed(DateTimeOffset.UtcNow.AddMinutes(-10));
            other.OtherTenant();
            await PostAsync(collectorB, "/v1/traces", other.TracesJson());

            var probe = new TelemetryFeed(DateTimeOffset.UtcNow.AddMinutes(-10));
            probe.OtherTenant();
            probe.Shop();
            await PostAsync(exporterOwned, "/v1/traces", probe.TracesJson());
            await PostAsync(exporterOwned, "/v1/logs", probe.LogsJson());

            await WaitForAsync(databaseA, MonitorTelemetrySchema.TracesTable, feed.SpanCount);
            await WaitForAsync(databaseA, MonitorTelemetrySchema.LogsTable, feed.LogCount);
            await WaitForAsync(databaseB, MonitorTelemetrySchema.TracesTable, other.SpanCount);
            await WaitForAsync(ExporterOwned, MonitorTelemetrySchema.LogsTable, probe.LogCount);

            fed = true;
        } finally {
            feeding.Release();
        }
    }

    /// <summary>The database the exporter created its own tables in.</summary>
    public const string ExporterOwned = "exporter_owned";

    /// <summary>Runs one statement as the administrator and returns the body.</summary>
    /// <param name="sql">The statement.</param>
    public async Task<string> SqlAsync(string sql) {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + "/");
        request.Headers.Add("X-ClickHouse-User", Admin);
        request.Headers.Add("X-ClickHouse-Key", AdminPassword);
        request.Content = new StringContent(sql, Encoding.UTF8);

        using var response = await admin.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException($"ClickHouse refused the fixture's statement: {body}\n{sql}");
        }

        return body;
    }

    async Task<IContainer> StartCollectorAsync(string database, bool createSchema) {
        // The collector's own address under the harness's workspace — its name is only in the
        // rendered text as the VMUser, which is the workspace's.
        var collectorId = new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            MonitorCollectors.Type,
            "gateway",
            Guid.Empty,
            ConformanceIds.AncestorName(0)
        );

        using var body = System.Text.Json.JsonDocument.Parse(MonitorCollectors.Body(ConformanceIds.Cluster));
        var rendered = MonitorCollectors.CollectorConfig(collectorId, body.RootElement);

        const string Rendered = "endpoint: https://" + MonitorWorkspaces.QueryHost;
        rendered.ShouldContain(Rendered, customMessage: "the rendered exporter no longer names the query host this fixture swaps");

        var config = rendered.Replace(Rendered, "endpoint: tcp://clickhouse:9000", StringComparison.Ordinal);

        if (createSchema) {
            config = config.Replace("create_schema: false", "create_schema: true", StringComparison.Ordinal);
        }

        var collector = new ContainerBuilder(MonitorCollectors.Image)
            .WithNetwork(network)
            .WithPortBinding(MonitorCollectors.OtlpHttpPort, true)
            .WithPortBinding(MonitorCollectors.HealthPort, true)
            .WithEnvironment(MonitorWorkspaces.EnvAccountId, "1")
            .WithEnvironment(MonitorWorkspaces.EnvDatabase, database)
            .WithEnvironment(MonitorWorkspaces.EnvIngestKey, IngestKey)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(config),
                MonitorCollectors.ConfigDirectory + "/" + MonitorCollectors.ConfigFile
            )
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(static x => x.ForPort(MonitorCollectors.HealthPort).ForPath("/"))
            )
            .Build();

        collectors.Add(collector);
        await collector.StartAsync();
        return collector;
    }

    static async Task PostAsync(IContainer collector, string path, string json) {
        using var http = new HttpClient();
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await http.PostAsync(
            new Uri($"http://{collector.Hostname}:{collector.GetMappedPublicPort(MonitorCollectors.OtlpHttpPort)}{path}"),
            content
        );

        response.IsSuccessStatusCode.ShouldBeTrue(
            $"the collector refused the export to {path}: {await response.Content.ReadAsStringAsync()}"
        );
    }

    async Task WaitForAsync(string database, string table, int rows) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var landed = 0;

        while (DateTimeOffset.UtcNow < deadline) {
            landed = int.Parse(
                (await SqlAsync($"SELECT count() FROM {database}.{table}")).Trim(),
                CultureInfo.InvariantCulture
            );

            if (landed >= rows) {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var logs = new StringBuilder();
        foreach (var collector in collectors) {
            var (stdout, stderr) = await collector.GetLogsAsync();
            logs.Append(stdout).Append(stderr);
        }

        throw new InvalidOperationException(
            $"{landed} of {rows} rows reached {database}.{table} through the exporter within 90 s. "
            + "The collectors said:\n"
            + logs
        );
    }

    static async Task<ResourceId> ResolveWorkspaceAsync(
        ProviderTestCluster<ComponentViewsCase> cluster,
        Guid tenant,
        Guid subscription
    ) {
        var address = new ResourceId(
            tenant,
            subscription,
            ConformanceIds.ResourceGroup,
            MonitorWorkspaces.Type,
            ConformanceIds.AncestorName(0),
            Guid.Empty
        );

        return address.WithId((await cluster.Index(address).ResolveAsync()).GetValueOrThrow());
    }

    /// <summary>The real store, recording which database each query was for.</summary>
    sealed class CountingStore(ITelemetryStore inner) : ITelemetryStore {
        public Task<Result<ImmutableArray<JsonObject>>> QueryAsync(
            TelemetryQuery query,
            CancellationToken cancellationToken = default
        ) {
            Asked.Enqueue(MonitorWorkspaces.Database(query.Workspace));
            return inner.QueryAsync(query, cancellationToken);
        }
    }
}
