using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Time;
using CyberCloud.Providers.Monitor;
using CyberCloud.Providers.Monitor.Accounts;
using CyberCloud.Providers.Monitor.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Providers.Monitor.Query;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Actions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests.Infrastructure;

/// <summary>
///     Everything <c>MonitorQueryOverHttpTests</c> reads through: a VictoriaMetrics cluster and a
///     ClickHouse in Testcontainers, a test cluster with the real write path, and the gateway's
///     pipeline behind a real listener.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Production code from the socket to the store, except three things, and each is named.</b>
///         The pipeline is <see cref="GatewayHarness" />'s eight stages behind Kestrel; stage 8
///         dispatches to a real <see cref="ResourceManagerService" /> over a real Orleans test cluster
///         with the Monitor provider registered; the manager runs the real
///         <see cref="ActionDispatcher" />, which resolves the real handlers over the real
///         <see cref="VictoriaMetricsQueryStore" /> and <see cref="ClickHouseLogStore" />. What is not
///         production: the token issuer (the harness's, as in every gateway test), the authorizer
///         (<c>PermissiveAuthorizer</c>, which the Reader test restricts), and the cluster the
///         workspace converges onto (the conformance suite's fake; the action reads no object).
///     </para>
///     <para>
///         ⚠ <b>"A Reader may query" is two proofs joined, and neither is here.</b> The permissive
///         authorizer is told the string <c>read</c> is granted, so what this suite shows is that the
///         three actions need <c>read</c> and nothing more. That they declare the type's own read
///         permission is <c>MonitorQueryTests.TheThreeActionsAreDeclaredWithReadTheirRequestsAndTheirHandlers</c>;
///         that the real ReBAC schema grants <c>read</c> on a resource to Reader and withholds
///         <c>write</c> is <c>RoleAssignmentTests.AnOwnerGrantsReaderOnAGroupAndTheReaderCanReadButNotWriteAResourceInIt</c>,
///         through the real authorizers and the real <c>CyberCloudSchema</c>. The resource authorizer
///         checks an action's declared permission on the resource object as it checks <c>read</c>
///         for a <c>GET</c>, so nothing sits between the two.
///         #41's review asked for the join to be written down rather than implied.
///     </para>
///     <para>
///         ⚠ <b>The metrics store is VictoriaMetrics' CLUSTER version, three containers, because the
///         single-node version has no <c>accountID</c>.</b> The whole claim under test is that one
///         workspace's query cannot read another's account, and a single-node store would have made
///         every account the same account and every assertion vacuous.
///     </para>
///     <para>
///         ⚠ <b>Two workspaces with the SAME NAME in two tenants.</b> Their paths differ only in the
///         tenant segment, which is the segment the gateway rebuilds from the token — so if anything
///         between the socket and the store took the tenancy from the wrong place, tenant B would read
///         tenant A's data under B's own path, and that is the assertion that would turn.
///     </para>
///     <para>
///         ⚠ <b>Needs a Docker daemon, and fails without one</b>, like
///         <c>ResourceGraphQueryEndToEndTests</c>; the project file says why.
///     </para>
/// </remarks>
public sealed class MonitorQueryFixture : IAsyncLifetime {
    /// <summary>VictoriaMetrics' cluster images, one version for all three.</summary>
    public const string VictoriaMetricsVersion = "v1.152.0-cluster";

    /// <summary>The name both tenants give their workspace.</summary>
    public const string Workspace = "telemetry";

    /// <summary>A workspace in tenant A that nothing has written a log to.</summary>
    public const string EmptyWorkspace = "quiet";

    /// <summary>
    ///     A workspace in tenant B whose accountID the ledger says tenant A's workspace holds — what a
    ///     fold collision looks like from the platform. Its account carries tenant A's series.
    /// </summary>
    public const string CollidingWorkspace = "collider";

    /// <summary>
    ///     The body of tenant A's one record with a Windows path, a lone backslash and a tab in it —
    ///     the three things ClickHouse's escaped parameter format reads as something else.
    /// </summary>
    public const string BackslashBody = "copied C:\\temp\\new to\tshare \\ done";

    const string ClickHouseUser = "cybercloud";
    const string ClickHousePassword = "cyber-cloud-test-password";

    readonly INetwork network = new NetworkBuilder().Build();
    readonly IContainer storage;
    readonly IContainer insert;
    readonly IContainer select;
    readonly IContainer clickHouse;

    /// <summary>The conformance harness's test cluster, with the Monitor provider registered.</summary>
    public ProviderTestCluster<MonitorCase> Cluster { get; } = new();

    /// <summary>The listener.</summary>
    internal OverHttpGateway Gateway { get; private set; } = null!;

    /// <summary>Tenant A — the conformance harness's primary tenant.</summary>
    public static Guid TenantA => ConformanceIds.Tenant;

    /// <summary>Tenant B — the conformance harness's other tenant.</summary>
    public static Guid TenantB => ConformanceIds.OtherTenant;

    /// <summary>Tenant A's workspace GUID, as the write path resolved it.</summary>
    public Guid WorkspaceA { get; private set; }

    /// <summary>Tenant B's workspace GUID.</summary>
    public Guid WorkspaceB { get; private set; }

    /// <summary>How <see cref="CollidingWorkspace" />'s create ended — refused by its reconciler.</summary>
    public OperationStatus CollidingCreate { get; private set; } = null!;

    /// <summary><see cref="CollidingWorkspace" />'s accountID, which tenant A's workspace holds.</summary>
    public uint CollidingAccount { get; private set; }

    /// <summary>Where the seeded data starts: an hour before the fixture started, on the minute.</summary>
    public DateTimeOffset Origin { get; } = Minute(DateTimeOffset.UtcNow.AddHours(-1));

    /// <summary>Creates the containers; nothing starts until <see cref="InitializeAsync" />.</summary>
    public MonitorQueryFixture() {
        storage = new ContainerBuilder("victoriametrics/vmstorage:" + VictoriaMetricsVersion)
            .WithNetwork(network)
            .WithNetworkAliases("vmstorage")
            .WithCommand("-retentionPeriod=100y")
            .WithPortBinding(8482, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8482).ForPath("/health")))
            .Build();

        insert = new ContainerBuilder("victoriametrics/vminsert:" + VictoriaMetricsVersion)
            .WithNetwork(network)
            .WithCommand("-storageNode=vmstorage:8400")
            .WithPortBinding(8480, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8480).ForPath("/health")))
            .Build();

        // ⚠ -search.disableCache and a latency offset of one second: the rollup cache would otherwise
        // remember an answer computed before the seed became searchable, and the default offset of
        // 30 s hides the newest samples from every query — both correct in production, and both a
        // flaky assertion here.
        select = new ContainerBuilder("victoriametrics/vmselect:" + VictoriaMetricsVersion)
            .WithNetwork(network)
            .WithCommand("-storageNode=vmstorage:8401", "-search.disableCache", "-search.latencyOffset=1s")
            .WithPortBinding(8481, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8481).ForPath("/health")))
            .Build();

        clickHouse = new ContainerBuilder(ResourceGraphQueryEndToEndTests.Image)
            .WithPortBinding(8123, true)
            .WithEnvironment("CLICKHOUSE_USER", ClickHouseUser)
            .WithEnvironment("CLICKHOUSE_PASSWORD", ClickHousePassword)
            .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
            // Not UTC, so a search that compared zoned values would be wrong by an hour or two.
            .WithEnvironment("TZ", "Europe/Prague")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8123).ForPath("/ping")))
            .Build();
    }

    /// <summary>The metrics store's URL, for a test that asks it directly.</summary>
    public Uri SelectUri => new($"http://{select.Hostname}:{select.GetMappedPublicPort(8481)}");

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        await network.CreateAsync(token);
        await storage.StartAsync(token);
        await Task.WhenAll(insert.StartAsync(token), select.StartAsync(token), clickHouse.StartAsync(token));

        await Cluster.InitializeAsync();

        var options = new MonitorQueryOptions {
            MetricsEndpoint = SelectUri.ToString(),
            LogsEndpoint = $"http://{clickHouse.Hostname}:{clickHouse.GetMappedPublicPort(8123)}",
            LogsUser = ClickHouseUser,
            LogsPassword = ClickHousePassword,
            AllowInsecureTransport = true
        };

        // ── The real write path, over the harness's cluster, with the real handlers ──────────────
        //
        // ⚠ The ledger is the real grain, reached through the seam the gateway registers. The silo's
        // reconciler claims through its own instance of the same seam, so what a handler checks here
        // is what a reconcile pass claimed. The in-process cluster shares one type manifest, which is
        // why MonitorAccountOverTheRealHostsTests crosses a real process boundary as well.
        var accounts = new GrainMonitorAccounts(Cluster.Grains);

        var handlers = new ServiceCollection()
            .AddSingleton<IClock>(new SystemClock())
            .AddSingleton<IMonitorAccounts>(accounts)
            .AddSingleton<IMonitorMetricsStore>(
                new VictoriaMetricsQueryStore(new HttpClient(), options, NullLogger<VictoriaMetricsQueryStore>.Instance)
            )
            .AddSingleton<IMonitorLogStore>(
                new ClickHouseLogStore(new HttpClient(), options, NullLogger<ClickHouseLogStore>.Instance)
            )
            .AddSingleton<MonitorWorkspaceQueryMetricsHandler>()
            .AddSingleton<MonitorWorkspaceListMetricLabelsHandler>()
            .AddSingleton<MonitorWorkspaceSearchLogsHandler>()
            .BuildServiceProvider();

        var manager = new ResourceManagerService(
            Cluster.Registry,
            Cluster.Authorizer,
            Cluster.Relations,
            Cluster.Locks,
            new NotSupportedPolicyEvaluator(),
            Cluster.Changes,
            Cluster.Grains,
            new ActionDispatcher(handlers, new FakeClusterConnectionFactory(Cluster.World), Cluster.Vault),
            NullLogger<ResourceManagerService>.Instance
        );

        WorkspaceA = await CreateConvergedAsync(manager, TenantA, ConformanceIds.Subscription, Workspace);
        WorkspaceB = await CreateConvergedAsync(manager, TenantB, ConformanceIds.OtherSubscription, Workspace);
        await CreateConvergedAsync(manager, TenantA, ConformanceIds.Subscription, EmptyWorkspace);

        // ⚠ The collision, staged where the platform would meet it: the write is accepted, and before
        // its reconcile runs the ledger is told tenant A's workspace got the account first. Nothing
        // searches for two GUIDs that fold alike — the write path mints the GUID, so a real collision
        // can't be arranged, and to the reconciler and the handlers a staged one is the same thing.
        var (_, collided) = await CreateAsync(
            manager,
            TenantB,
            ConformanceIds.OtherSubscription,
            CollidingWorkspace,
            async id => {
                CollidingAccount = AccountOf(id, TenantB, ConformanceIds.OtherSubscription);
                (await accounts.ClaimAsync(CollidingAccount, WorkspaceA, token)).ShouldBeTrue();
            }
        );

        CollidingCreate = collided;

        await SeedMetricsAsync(token);
        await SeedLogsAsync(token);

        Gateway = await OverHttpGateway.StartAsync(new GatewayHarness(manager, Cluster.Registry, TenantA, TenantB));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Gateway is not null) {
            await Gateway.DisposeAsync();
        }

        await Cluster.DisposeAsync();
        await clickHouse.DisposeAsync();
        await select.DisposeAsync();
        await insert.DisposeAsync();
        await storage.DisposeAsync();
        await network.DisposeAsync();
    }

    /// <summary>A workspace's action path.</summary>
    /// <param name="tenant">Whose tenant segment.</param>
    /// <param name="action">The action.</param>
    /// <param name="name">The workspace.</param>
    public static string ActionPath(Guid tenant, string action, string name = Workspace) {
        var subscription = tenant == TenantB ? ConformanceIds.OtherSubscription : ConformanceIds.Subscription;

        return $"/tenants/{tenant:D}/subscriptions/{subscription:D}/resourceGroups/{ConformanceIds.ResourceGroup}"
            + $"/providers/{MonitorWorkspaces.ProviderNamespace}/{MonitorWorkspaces.TypePath}/{name}/{action}";
    }

    /// <summary>POSTs an action through the listener as a tenant's caller.</summary>
    /// <param name="tenant">The token's tenant.</param>
    /// <param name="path">The path — usually <see cref="ActionPath" />.</param>
    /// <param name="body">The body, serialized as JSON.</param>
    public async Task<(int Status, string Body)> PostAsync(Guid tenant, string path, object body) {
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "?api-version=" + MonitorWorkspaces.V2026);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Gateway.Harness.Token(tenant, "alice"));
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await Gateway.Http.SendAsync(request, TestContext.Current.CancellationToken);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Creates a workspace through the write path and drives its create to Succeeded.</summary>
    async Task<Guid> CreateConvergedAsync(ResourceManagerService manager, Guid tenant, Guid subscription, string name) {
        var (id, status) = await CreateAsync(manager, tenant, subscription, name, static _ => Task.CompletedTask);

        // ⚠ Driven, not merely accepted: the reconcile pass is what claims the accountID, and a
        // workspace whose create hasn't converged holds none, so its metrics reads are a 409.
        status.State.ShouldBe(OperationState.Succeeded, $"'{name}' in {tenant}: {status.Error?.Message}");

        return id;
    }

    /// <summary>Creates a workspace through the write path and drives its create to a terminal state.</summary>
    async Task<(Guid Id, OperationStatus Status)> CreateAsync(
        ResourceManagerService manager,
        Guid tenant,
        Guid subscription,
        string name,
        Func<Guid, Task> beforeReconcile
    ) {
        var address = new ResourceId(tenant, subscription, ConformanceIds.ResourceGroup, MonitorWorkspaces.Type, name, Guid.Empty);

        var accepted = await manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = MonitorWorkspaces.V2026,
                Verb = WriteVerb.Put,
                Body = MonitorWorkspaces.Body(ConformanceIds.Cluster),
                Caller = ProviderTestCluster<MonitorCase>.Caller(tenant)
            },
            CancellationToken.None
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var id = accepted.GetValueOrThrow().Resource.Id;
        id.ShouldNotBe(Guid.Empty);

        await beforeReconcile(id);

        var operation = Cluster.Operation(tenant, accepted.GetValueOrThrow().OperationId);
        OperationStatus? last = null;

        for (var drive = 0; drive < 8 && last is not { IsTerminal: true }; drive++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();
        }

        last.ShouldNotBeNull();
        last.IsTerminal.ShouldBeTrue($"'{name}' in {tenant} never finished creating: {last.State}");

        return (id, last);
    }

    // ── The seed ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The accountID the handler will derive for a workspace GUID.</summary>
    public static uint AccountOf(Guid workspace, Guid tenant, Guid subscription) =>
        MonitorWorkspaces.AccountId(
            new ResourceId(tenant, subscription, ConformanceIds.ResourceGroup, MonitorWorkspaces.Type, Workspace, workspace)
        );

    /// <summary>
    ///     Two series in A's account and one in B's, one sample a minute for the hour after
    ///     <see cref="Origin" />, each value its minute's index — so a sum or a last value is known.
    /// </summary>
    async Task SeedMetricsAsync(CancellationToken cancellationToken) {
        var accountA = AccountOf(WorkspaceA, TenantA, ConformanceIds.Subscription);
        var accountB = AccountOf(WorkspaceB, TenantB, ConformanceIds.OtherSubscription);

        accountA.ShouldNotBe(accountB, "the fold gave two workspaces one account, and every assertion would be vacuous");

        await ImportAsync(accountA, ["cc_requests_total{tenant=\"a\",route=\"/api\"}", "cc_requests_total{tenant=\"a\",route=\"/health\"}"], cancellationToken);
        await ImportAsync(accountB, ["cc_requests_total{tenant=\"b\",route=\"/api\"}"], cancellationToken);

        // Tenant A's series under the account the colliding workspace folds to — what its holder
        // would have written there. A read that reached the store would answer these.
        await ImportAsync(CollidingAccount, ["cc_requests_total{tenant=\"a\",route=\"/collided\"}"], cancellationToken);

        // Searchable is not the same as accepted: vminsert buffers and vmstorage publishes a new part
        // about a second later. Wait for both accounts rather than sleeping a guess.
        await UntilSeriesAsync(accountA, 2, cancellationToken);
        await UntilSeriesAsync(accountB, 1, cancellationToken);
        await UntilSeriesAsync(CollidingAccount, 1, cancellationToken);
    }

    async Task ImportAsync(uint account, string[] series, CancellationToken cancellationToken) {
        var lines = new StringBuilder();

        foreach (var name in series) {
            for (var minute = 0; minute <= 60; minute++) {
                var at = Origin.AddMinutes(minute).ToUnixTimeMilliseconds();
                lines.Append(CultureInfo.InvariantCulture, $"{name} {minute} {at}\n");
            }
        }

        using var http = new HttpClient();
        var uri = $"http://{insert.Hostname}:{insert.GetMappedPublicPort(8480)}/insert/{account}/prometheus/api/v1/import/prometheus";
        using var response = await http.PostAsync(uri, new StringContent(lines.ToString()), cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>Waits until a range query over the whole seed answers every series and its last sample.</summary>
    /// <remarks>
    ///     ⚠ <b>The series API is not the readiness signal, and the first cut used it.</b> vmstorage
    ///     registers a series in its index as the sample arrives and makes the sample itself searchable
    ///     only when the in-memory part is published, so <c>/api/v1/series</c> answered both series while
    ///     <c>query_range</c> still answered none — two tests failed with an empty matrix, and the store
    ///     was right. The wait asks the question the tests ask.
    /// </remarks>
    async Task UntilSeriesAsync(uint account, int expected, CancellationToken cancellationToken) {
        using var http = new HttpClient();
        var uri = new Uri(SelectUri, $"/select/{account}/prometheus/api/v1/query_range");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (DateTimeOffset.UtcNow < deadline) {
            using var form = new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["query"] = "cc_requests_total",
                    ["start"] = Origin.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    ["end"] = Origin.AddMinutes(60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    ["step"] = "60s"
                }
            );

            using var response = await http.PostAsync(uri, form, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var result = document.RootElement.GetProperty("data").GetProperty("result");

            if (result.GetArrayLength() >= expected
                && result.EnumerateArray().All(static x => x.GetProperty("values").GetArrayLength() == 61)) {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Account {account} never answered {expected} complete series.");
    }

    /// <summary>
    ///     Eight records in A's database and two in B's, all inside the hour after <see cref="Origin" />.
    /// </summary>
    async Task SeedLogsAsync(CancellationToken cancellationToken) {
        var databaseA = MonitorWorkspaces.Database(new(TenantA, ConformanceIds.Subscription, ConformanceIds.ResourceGroup, MonitorWorkspaces.Type, Workspace, WorkspaceA));
        var databaseB = MonitorWorkspaces.Database(new(TenantB, ConformanceIds.OtherSubscription, ConformanceIds.ResourceGroup, MonitorWorkspaces.Type, Workspace, WorkspaceB));

        foreach (var database in new[] { databaseA, databaseB }) {
            await ClickHouseAsync($"CREATE DATABASE IF NOT EXISTS `{database}`", cancellationToken);
            await ClickHouseAsync(MonitorLogsTable.CreateSql(database), cancellationToken);
        }

        await InsertAsync(
            databaseA,
            [
                Record(2, 9, "INFO", "api", "request served", ("http.method", "GET")),
                Record(5, 17, "ERROR", "api", "upstream timed out", ("http.method", "GET")),
                Record(12, 13, "WARN", "worker", "queue is deep", ("queue", "billing")),
                Record(25, 17, "Error", "worker", "job failed: TIMED OUT waiting", ("queue", "billing")),
                Record(33, 9, "info", "api", "user said ')) OR 1=1 -- and left", ("http.method", "POST")),
                Record(47, 21, "FATAL", "api", "process exiting", ("http.method", "GET")),
                Record(50, 9, "INFO", @"sync\agent", BackslashBody, ("file.path", @"C:\temp\new")),
                Record(58, 0, "", "cron", "tick", ("job", "nightly"))
            ],
            cancellationToken
        );

        await InsertAsync(
            databaseB,
            [
                Record(3, 17, "ERROR", "api", "tenant b secret failure", ("http.method", "GET")),
                Record(40, 9, "INFO", "api", "tenant b served", ("http.method", "GET"))
            ],
            cancellationToken
        );
    }

    Dictionary<string, object> Record(int minute, int severity, string text, string service, string body, (string Key, string Value) attribute) =>
        new Dictionary<string, object> {
            ["Timestamp"] = Origin.AddMinutes(minute).AddMilliseconds(123).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["TraceId"] = minute == 5 ? "0af7651916cd43dd8448eb211c80319c" : "",
            ["SpanId"] = "",
            ["SeverityText"] = text,
            ["SeverityNumber"] = severity,
            ["ServiceName"] = service,
            ["Body"] = body,
            ["ResourceAttributes"] = new Dictionary<string, string> { ["service.name"] = service, ["deployment.environment"] = "prod" },
            ["LogAttributes"] = new Dictionary<string, string> { [attribute.Key] = attribute.Value }
        };

    async Task InsertAsync(string database, Dictionary<string, object>[] rows, CancellationToken cancellationToken) {
        var body = new StringBuilder(
            $"INSERT INTO `{database}`.`{MonitorLogsTable.Name}` "
            + "(Timestamp, TraceId, SpanId, SeverityText, SeverityNumber, ServiceName, Body, ResourceAttributes, LogAttributes) "
            + "FORMAT JSONEachRow\n"
        );

        foreach (var row in rows) {
            body.Append(JsonSerializer.Serialize(row)).Append('\n');
        }

        await ClickHouseAsync(body.ToString(), cancellationToken);
    }

    async Task ClickHouseAsync(string sql, CancellationToken cancellationToken) {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://{clickHouse.Hostname}:{clickHouse.GetMappedPublicPort(8123)}/?date_time_input_format=best_effort"
        );
        request.Headers.Add("X-ClickHouse-User", ClickHouseUser);
        request.Headers.Add("X-ClickHouse-Key", ClickHousePassword);
        request.Content = new StringContent(sql, Encoding.UTF8, "text/plain");

        using var response = await http.SendAsync(request, cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    static DateTimeOffset Minute(DateTimeOffset instant) =>
        new(instant.Year, instant.Month, instant.Day, instant.Hour, instant.Minute, 0, TimeSpan.Zero);
}
