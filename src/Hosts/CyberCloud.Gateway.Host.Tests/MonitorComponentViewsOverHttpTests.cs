using CyberCloud.Conformance;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Providers.Monitor.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Providers.Monitor.Telemetry;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>The component case, with the dispatcher's store pointed at this suite's ClickHouse.</summary>
public sealed class ComponentViewsOverHttpCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => MonitorComponentCase.ProviderCase;

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors => MonitorComponentCase.Ancestors;

    /// <inheritdoc />
    public static void ConfigureSilo(ISiloBuilder silo) => MonitorComponentCase.ConfigureSilo(silo);

    /// <inheritdoc />
    public static void ConfigureHandlers(IServiceCollection services) =>
        services.AddSingleton<ITelemetryStore>(static _ => MonitorComponentViewsOverHttpTests.Store);
}

/// <summary>
///     A component's views <c>POST</c>ed at a real listener, through the gateway's eight stages, to the
///     real resource manager, the real handler and a real ClickHouse — and another tenant's caller
///     turned away before the store is asked.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The refusal is only worth something because the same request from the owner is
///             answered.
///         </b> A <c>404</c> from a pipeline that could not reach the manager at all would pass the
///         cross-tenant assertion alone, so the owner's view is asserted first, over the same socket,
///         with its numbers; and the other tenant's own component of the same name is asserted to read
///         its own workspace's database and nothing of the first's.
///     </para>
///     <para>
///         ⚠ <b>The rows are inserted, not exported.</b> What the exporter writes and each view's
///         numbers over it are <c>ComponentViewsAgainstClickHouseTests</c>' in the provider's
///         cluster-backed project; this suite is about who may read which database over HTTP, and the
///         tables are <see cref="MonitorTelemetrySchema" />'s either way.
///     </para>
///     <para>
///         ⚠ <b>Needs a Docker daemon, and fails without one</b> — this project's header says why.
///     </para>
/// </remarks>
/// <param name="cluster">The provider harness: real grains, a real manager, a fake cluster for the ConfigMap.</param>
public sealed class MonitorComponentViewsOverHttpTests(ProviderTestCluster<ComponentViewsOverHttpCase> cluster)
    : IClassFixture<ProviderTestCluster<ComponentViewsOverHttpCase>>, IAsyncLifetime {
    const string Admin = "admin";
    const string AdminPassword = "views-over-http-admin";
    const string Reader = "views";
    const string ReaderPassword = "views-over-http-reader";

    static ITelemetryStore? store;

    readonly IContainer clickHouse = new ContainerBuilder(ResourceGraphQueryEndToEndTests.Image)
        .WithPortBinding(8123, true)
        .WithEnvironment("CLICKHOUSE_USER", Admin)
        .WithEnvironment("CLICKHOUSE_PASSWORD", AdminPassword)
        .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8123).ForPath("/ping")))
        .Build();

    OverHttpGateway gateway = null!;

    /// <summary>The store the dispatcher's handler reads through, recording which database it named.</summary>
    public static ITelemetryStore Store => store ?? throw new InvalidOperationException("No ClickHouse has started.");

    /// <summary>Every database a view asked for, in order.</summary>
    static ConcurrentQueue<string> Asked { get; } = new();

    static Guid TenantA => ConformanceIds.Tenant;

    static Guid TenantB => ConformanceIds.OtherTenant;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        await clickHouse.StartAsync(TestContext.Current.CancellationToken);

        var endpoint = $"http://{clickHouse.Hostname}:{clickHouse.GetMappedPublicPort(8123)}";
        store = new AskedStore(
            new ClickHouseTelemetryStore(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                new() {
                    ClickHouseEndpoint = endpoint,
                    ClickHouseUser = Reader,
                    ClickHousePassword = ReaderPassword,
                    AllowInsecureTransport = true
                }
            )
        );

        using var admin = new HttpClient { BaseAddress = new(endpoint) };
        await SqlAsync(admin, $"CREATE USER IF NOT EXISTS {Reader} IDENTIFIED BY '{ReaderPassword}'");

        foreach (var (tenant, subscription, service, spans) in new[] {
                     (TenantA, ConformanceIds.Subscription, "a-frontend", 3),
                     (TenantB, ConformanceIds.OtherSubscription, "b-frontend", 5)
                 }) {
            var database = MonitorWorkspaces.Database(await WorkspaceAsync(tenant, subscription));

            foreach (var statement in MonitorTelemetrySchema.Statements(database)) {
                await SqlAsync(admin, statement);
            }

            await SqlAsync(admin, $"GRANT SELECT ON {database}.* TO {Reader}");
            await SqlAsync(
                admin,
                $"""
                 INSERT INTO {database}.otel_traces (Timestamp, TraceId, SpanId, SpanName, SpanKind, ServiceName, ResourceAttributes, Duration, StatusCode)
                 SELECT now64(9) - toIntervalSecond(number + 60), lower(hex(randomString(16))), lower(hex(randomString(8))),
                        'GET /', 'Server', '{service}', map('service.name', '{service}', 'service.namespace', 'shop'), 1000000, 'Unset'
                 FROM numbers({spans})
                 """
            );

            await CreateComponentAsync(tenant, subscription);
        }

        gateway = await OverHttpGateway.StartAsync(new GatewayHarness(cluster.Manager, cluster.Registry, TenantA, TenantB));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (gateway is not null) {
            await gateway.DisposeAsync();
        }

        await clickHouse.DisposeAsync();
        store = null;
    }

    [Fact]
    public async Task AnotherTenantsCallerIsA404AndTheStoreIsNeverAsked() {
        var databaseA = MonitorWorkspaces.Database(await WorkspaceAsync(TenantA, ConformanceIds.Subscription));
        var databaseB = MonitorWorkspaces.Database(await WorkspaceAsync(TenantB, ConformanceIds.OtherSubscription));

        // ── The owner, first, over the same socket: the pipeline reaches the manager and the store.
        var owner = await PostAsync(TenantA, ConformanceIds.Subscription, gateway.Harness.Token(TenantA, "alice"));

        owner.Status.ShouldBe(HttpStatusCode.OK, owner.Body);
        var answered = JsonNode.Parse(owner.Body)!;
        answered["total"]!.GetValue<long>().ShouldBe(3);
        answered["services"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["a-frontend"]);
        Asked.Last().ShouldBe(databaseA);

        // ── Tenant B's caller at tenant A's component.
        var before = Asked.Count;
        var intruder = await PostAsync(TenantA, ConformanceIds.Subscription, gateway.Harness.Token(TenantB, "mallory"));

        intruder.Status.ShouldBe(
            HttpStatusCode.NotFound,
            "another tenant's caller was not refused with the canonical 404 — docs/plan/07 § The enforcement seam. Body: "
            + intruder.Body
        );
        intruder.Body.ShouldNotContain("a-frontend");
        Asked.Count.ShouldBe(before, "a view refused to another tenant still reached the telemetry store");

        // ── Tenant B's own component of the same name reads B's database and nothing of A's.
        var own = await PostAsync(TenantB, ConformanceIds.OtherSubscription, gateway.Harness.Token(TenantB, "mallory"));

        own.Status.ShouldBe(HttpStatusCode.OK, own.Body);
        var theirs = JsonNode.Parse(own.Body)!;
        theirs["total"]!.GetValue<long>().ShouldBe(5);
        theirs["services"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["b-frontend"]);
        Asked.Last().ShouldBe(databaseB);
    }

    [Fact]
    public async Task ATraceIdThatIsNotHexIsRefusedBeforeTheStoreIsAsked() {
        // The one tenant-typed string a view takes, carrying SQL. The schema's pattern refuses it at
        // the manager's action-body step; and were it bound, it would be a parameter and not text.
        var before = Asked.Count;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ComponentPath(TenantA, ConformanceIds.Subscription) + "/" + MonitorComponents.TransactionAction
            + "?api-version=" + MonitorWorkspaces.V2026
        ) {
            Content = new StringContent("""{"traceId":"' OR 1=1 --"}""", Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Harness.Token(TenantA, "alice"));

        using var response = await gateway.Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Asked.Count.ShouldBe(before);
    }

    async Task<(HttpStatusCode Status, string Body)> PostAsync(Guid tenant, Guid subscription, string token) {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ComponentPath(tenant, subscription) + "/" + MonitorComponents.RequestsAction + "?api-version=" + MonitorWorkspaces.V2026
        ) {
            Content = new StringContent("""{"timespanMinutes":60}""", Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await gateway.Http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    static string ComponentPath(Guid tenant, Guid subscription) =>
        ProviderTestCluster<ComponentViewsOverHttpCase>.Address("shop", tenant, subscription).Path;

    async Task<ResourceId> WorkspaceAsync(Guid tenant, Guid subscription) {
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

    async Task CreateComponentAsync(Guid tenant, Guid subscription) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = ComponentPath(tenant, subscription),
                ApiVersion = MonitorWorkspaces.V2026,
                Verb = WriteVerb.Put,
                Body = MonitorComponents.Body(ProviderTestCluster<ComponentViewsOverHttpCase>.ClusterId),
                Caller = ProviderTestCluster<ComponentViewsOverHttpCase>.Caller(tenant)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        // The second test's PUT of the same body is a no-op with nothing to drive — the harness is
        // per class and the component is already there.
        if (accepted.GetValueOrThrow().OperationId == Guid.Empty) {
            return;
        }

        var operation = cluster.Operation(tenant, accepted.GetValueOrThrow().OperationId);

        for (var i = 0; i < 10; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }

    static async Task SqlAsync(HttpClient admin, string sql) {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/") { Content = new StringContent(sql, Encoding.UTF8) };
        request.Headers.Add("X-ClickHouse-User", Admin);
        request.Headers.Add("X-ClickHouse-Key", AdminPassword);

        using var response = await admin.SendAsync(request);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The real store, recording which database each view named.</summary>
    sealed class AskedStore(ITelemetryStore inner) : ITelemetryStore {
        public Task<Result<ImmutableArray<JsonObject>>> QueryAsync(
            TelemetryQuery query,
            CancellationToken cancellationToken = default
        ) {
            Asked.Enqueue(MonitorWorkspaces.Database(query.Workspace));
            return inner.QueryAsync(query, cancellationToken);
        }
    }
}
