using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.ResourceManager.Contracts;
using Shouldly;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.ClusterConformance;

/// <summary>
///     The five views of <c>CyberCloud.Monitor/workspaces/components</c>, over telemetry the test
///     emitted as OTLP, carried by the pinned collector's real <c>clickhouse</c> exporter into a real
///     ClickHouse, and read back through the real <c>ResourceManagerService</c>, the real action
///     dispatcher and the real handler.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing between the POST and the rows is a double.</b> The manager is the harness's
///         real one over real grains, so the workspace's GUID the view reads is the one the tenant's
///         index bound when the harness created the workspace — the <c>ActionContext.Parent</c> path,
///         end to end. The store is <c>ClickHouseTelemetryStore</c>, wrapped only to record which
///         database each query named. The rows were written by the exporter, into tables
///         <see cref="MonitorTelemetrySchema" /> created, so the spellings the statements depend on
///         (<c>Server</c>, <c>Error</c>, nanoseconds) are the exporter's and not this file's.
///     </para>
///     <para>
///         ⚠ <b>Every expected number is derived in <see cref="TelemetryFeed" />'s remarks</b>, and
///         the percentiles are ClickHouse's <c>quantileExact</c> — position ⌊level × n⌋ of the sorted
///         values, no interpolation — so p50 of 5‥100 ms in steps of 5 is 55.
///     </para>
/// </remarks>
/// <param name="cluster">The harness, with the telemetry store wired into its dispatcher.</param>
/// <param name="world">The ClickHouse and the collectors.</param>
public sealed class ComponentViewsAgainstClickHouseTests(
    ProviderTestCluster<ComponentViewsCase> cluster,
    TelemetryWorld world
) : IClassFixture<ProviderTestCluster<ComponentViewsCase>>, IClassFixture<TelemetryWorld>, IAsyncLifetime {
    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        if (world.Unavailable is { } reason) {
            Assert.Skip(
                ClusterInfrastructure.SkipMessage(
                    "CyberCloud.Monitor/workspaces/components",
                    "that each view counts what the real collector's exporter wrote into a real ClickHouse",
                    reason
                )
            );
        }

        await world.FeedAsync(cluster);

        foreach (var (tenant, subscription, name) in new[] {
                     (ConformanceIds.Tenant, ConformanceIds.Subscription, "shop"),
                     (ConformanceIds.Tenant, ConformanceIds.Subscription, "admin"),
                     (ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription, "shop")
                 }) {
            await EnsureComponentAsync(tenant, subscription, name);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── The tables ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePlatformsTablesAreTheExportersShape() {
        // ⚠ Column by column — name, type, default kind and expression, codec, comment — and the
        // tables' engines, keys and skipping indices, between the tables MonitorTelemetrySchema
        // created and the ones the exporter created for itself with create_schema on. A collector bump
        // that moved the shape fails here by column rather than as rows that never arrive.
        var ours = MonitorWorkspaces.Database(world.WorkspaceA);

        foreach (var (what, sql) in new[] {
                     ("columns", "SELECT table, name, type, default_kind, default_expression, compression_codec, comment FROM system.columns WHERE database = '{0}' ORDER BY table, position FORMAT TSV"),
                     ("tables", "SELECT name, engine, partition_key, sorting_key FROM system.tables WHERE database = '{0}' ORDER BY name FORMAT TSV"),
                     ("indices", "SELECT table, name, type_full, expr, granularity FROM system.data_skipping_indices WHERE database = '{0}' ORDER BY table, name FORMAT TSV")
                 }) {
            var platform = await world.SqlAsync(string.Format(CultureInfo.InvariantCulture, sql, ours));
            var exporter = await world.SqlAsync(string.Format(CultureInfo.InvariantCulture, sql, TelemetryWorld.ExporterOwned));

            exporter.ShouldNotBeNullOrWhiteSpace($"the exporter created no {what} in {TelemetryWorld.ExporterOwned}");
            platform.ShouldBe(exporter, $"the platform's {what} are not the exporter's");
        }
    }

    // ── The five views ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestsCountRateFailuresAndExactPercentilesByOperation() {
        var view = await ViewAsync("shop", MonitorComponents.RequestsAction, """{"timespanMinutes":60}""");

        view["timespanMinutes"]!.GetValue<int>().ShouldBe(60);
        view["total"]!.GetValue<long>().ShouldBe(40, "twenty frontend and twenty cart server spans");
        view["failed"]!.GetValue<long>().ShouldBe(6, "four frontend and two cart");

        Texts(view, "services").ShouldBe(["cart", "frontend"]);
        Texts(view, "operations").ShouldBe(["GET /items", "GET /cart"]);
        Numbers(view, "counts").ShouldBe([20, 20]);
        Numbers(view, "failures").ShouldBe([2, 4]);
        Numbers(view, "ratePerMinute").ShouldBe([0.3333, 0.3333]);
        Numbers(view, "failureRate").ShouldBe([0.1, 0.2]);
        Numbers(view, "p50Ms").ShouldBe([55, 110]);
        Numbers(view, "p95Ms").ShouldBe([100, 200]);
        Numbers(view, "p99Ms").ShouldBe([100, 200]);
    }

    [Fact]
    public async Task DependenciesByTypeAndTarget() {
        var view = await ViewAsync("shop", MonitorComponents.DependenciesAction, "{}");

        view["total"]!.GetValue<long>().ShouldBe(40);
        view["failed"]!.GetValue<long>().ShouldBe(3);

        Texts(view, "services").ShouldBe(["cart", "frontend"]);
        Texts(view, "types").ShouldBe(["db", "http"]);
        Texts(view, "targets").ShouldBe(["pg.local", "cart"]);
        Texts(view, "names").ShouldBe(["SELECT items", "GET /items"]);
        Numbers(view, "counts").ShouldBe([20, 20]);
        Numbers(view, "failures").ShouldBe([3, 0]);
        Numbers(view, "failureRate").ShouldBe([0.15, 0]);
        Numbers(view, "p50Ms").ShouldBe([2, 50]);
    }

    [Fact]
    public async Task ExceptionsFromSpanEventsAndFromLogsSplitAndNewestMessageFirst() {
        var view = await ViewAsync("shop", MonitorComponents.ExceptionsAction, "{}");

        view["total"]!.GetValue<long>().ShouldBe(7);
        Texts(view, "services").ShouldBe(["frontend", "frontend"]);
        Texts(view, "types").ShouldBe(["System.InvalidOperationException", "System.IO.IOException"]);
        Numbers(view, "counts").ShouldBe([4, 3]);
        Numbers(view, "fromSpans").ShouldBe([4, 0]);
        Texts(view, "messages").ShouldBe(["cart 3 gone", "disk 2"]);
        Texts(view, "lastSeen").ShouldAllBe(static x => DateTimeOffset.Parse(x, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task TheApplicationMapIsTheServiceGraphFromParentAndChildSpans() {
        var view = await ViewAsync("shop", MonitorComponents.ApplicationMapAction, "{}");

        Texts(view, "nodes").ShouldBe(["cart", "frontend"]);
        Numbers(view, "nodeRequests").ShouldBe([20, 20]);
        Numbers(view, "nodeFailures").ShouldBe([2, 4]);

        // ⚠ One edge: a frontend client span is the parent of each cart server span. The frontend's
        // own server→client and cart's server→db parents are inside one service and are not edges.
        Texts(view, "edgeSources").ShouldBe(["frontend"]);
        Texts(view, "edgeTargets").ShouldBe(["cart"]);
        Numbers(view, "edgeCalls").ShouldBe([20]);
        Numbers(view, "edgeFailures").ShouldBe([2]);
        Numbers(view, "edgeP95Ms").ShouldBe([100]);
    }

    [Fact]
    public async Task ATransactionIsOneTracesSpansAndLogsInOrder() {
        var trace = world.ShopTraces[0];
        var view = await ViewAsync(
            "shop",
            MonitorComponents.TransactionAction,
            new JsonObject { ["traceId"] = trace }.ToJsonString()
        );

        view["traceId"]!.GetValue<string>().ShouldBe(trace);
        view["truncated"]!.GetValue<bool>().ShouldBeFalse();
        view["spanCount"]!.GetValue<int>().ShouldBe(4);

        Texts(view, "services").ShouldBe(["frontend", "frontend", "cart", "cart"]);
        Texts(view, "names").ShouldBe(["GET /cart", "GET /items", "GET /items", "SELECT items"]);
        Texts(view, "kinds").ShouldBe(["Server", "Client", "Server", "Client"]);
        Texts(view, "statuses").ShouldBe(["Error", "Unset", "Unset", "Unset"]);
        Numbers(view, "durationsMs").ShouldBe([10, 50, 5, 2]);

        var spans = Texts(view, "spanIds");
        Texts(view, "parentSpanIds").ShouldBe(["", spans[0], spans[1], spans[2]], "each span's parent is the one before it");

        view["logCount"]!.GetValue<int>().ShouldBe(2);
        Texts(view, "logBodies").ShouldBe(["reading cart", "failed to read cart 0"]);
        Texts(view, "logSeverities").ShouldBe(["Info", "Error"]);
        Texts(view, "logSpanIds").ShouldBe([spans[0], spans[0]]);
    }

    // ── The boundaries ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AComponentSeesOnlyItsOwnNamespace() {
        var admin = await ViewAsync("admin", MonitorComponents.RequestsAction, "{}");

        admin["total"]!.GetValue<long>().ShouldBe(5);
        Texts(admin, "services").ShouldBe(["backoffice"]);
        Numbers(admin, "p50Ms").ShouldBe([30]);

        var shop = await ViewAsync("shop", MonitorComponents.ApplicationMapAction, "{}");
        Texts(shop, "nodes").ShouldNotContain("backoffice");
    }

    [Fact]
    public async Task AnotherTenantsCallerIsRefusedAndItsOwnComponentReadsOnlyItsOwnDatabase() {
        var databaseA = MonitorWorkspaces.Database(world.WorkspaceA);
        var databaseB = MonitorWorkspaces.Database(world.WorkspaceB);
        var before = TelemetryWorld.Asked.Count;

        // Tenant B's caller at tenant A's component: the manager's step 1, before any handler.
        var refused = await cluster.Manager.ActionAsync(
            Request(ConformanceIds.Tenant, ConformanceIds.Subscription, "shop", MonitorComponents.RequestsAction, "{}")
                with {
                    Caller = ProviderTestCluster<ComponentViewsCase>.Caller(ConformanceIds.OtherTenant, "mallory")
                },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(CyberCloud.Core.ErrorCode.ResourceNotFound);
        TelemetryWorld.Asked.Count.ShouldBe(before, "a refused caller's view reached the telemetry store");

        // Tenant B's own component of the same name reads tenant B's database and nothing of A's.
        var own = await ViewAsync(
            "shop",
            MonitorComponents.RequestsAction,
            "{}",
            ConformanceIds.OtherTenant,
            ConformanceIds.OtherSubscription
        );

        own["total"]!.GetValue<long>().ShouldBe(7);
        own["failed"]!.GetValue<long>().ShouldBe(1);
        Texts(own, "services").ShouldBe(["b-frontend"]);
        TelemetryWorld.Asked.Skip(before).ShouldAllBe(x => x == databaseB);

        // And the other way round: A's view never names b-frontend.
        var mine = await ViewAsync("shop", MonitorComponents.RequestsAction, "{}");
        Texts(mine, "services").ShouldNotContain("b-frontend");
        TelemetryWorld.Asked.Last().ShouldBe(databaseA);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    async Task<JsonObject> ViewAsync(
        string component,
        string view,
        string body,
        Guid? tenant = null,
        Guid? subscription = null
    ) {
        var answered = await cluster.Manager.ActionAsync(
            Request(tenant ?? ConformanceIds.Tenant, subscription ?? ConformanceIds.Subscription, component, view, body),
            TestContext.Current.CancellationToken
        );

        answered.IsSuccess.ShouldBeTrue(answered.Error?.Message);
        answered.GetValueOrThrow().Completed.ShouldBeTrue("a view answers directly");

        var response = JsonNode.Parse(answered.GetValueOrThrow().ActionResponse)!.AsObject();

        // Every answer is what the published schema says leaves the platform — the dispatcher checks
        // it too, and this keeps the claim visible where the numbers are.
        var schema = view switch {
            MonitorComponents.RequestsAction => MonitorComponents.RequestsResponse,
            MonitorComponents.DependenciesAction => MonitorComponents.DependenciesResponse,
            MonitorComponents.ExceptionsAction => MonitorComponents.ExceptionsResponse,
            MonitorComponents.ApplicationMapAction => MonitorComponents.ApplicationMapResponse,
            _ => MonitorComponents.TransactionResponse
        };

        schema.Validate(response.Deserialize<JsonElement>(), allowTags: false).IsSuccess.ShouldBeTrue(view);
        return response;
    }

    static WriteRequest Request(Guid tenant, Guid subscription, string component, string action, string body) =>
        new() {
            Path = ProviderTestCluster<ComponentViewsCase>.Address(component, tenant, subscription).Path,
            ApiVersion = MonitorWorkspaces.V2026,
            Verb = WriteVerb.Post,
            Action = action,
            Body = body,
            Caller = ProviderTestCluster<ComponentViewsCase>.Caller(tenant)
        };

    async Task EnsureComponentAsync(Guid tenant, Guid subscription, string name) {
        var address = ProviderTestCluster<ComponentViewsCase>.Address(name, tenant, subscription);

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = MonitorWorkspaces.V2026,
                Verb = WriteVerb.Put,
                Body = MonitorComponents.Body(ProviderTestCluster<ComponentViewsCase>.ClusterId),
                Caller = ProviderTestCluster<ComponentViewsCase>.Caller(tenant)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        if (accepted.GetValueOrThrow().OperationId == Guid.Empty) {
            return;
        }

        var operation = cluster.For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(accepted.GetValueOrThrow().OperationId));

        for (var i = 0; i < 10; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }

    static List<string> Texts(JsonObject view, string column) =>
        [.. view[column]!.AsArray().Select(static x => x!.GetValue<string>())];

    static List<double> Numbers(JsonObject view, string column) =>
        [.. view[column]!.AsArray().Select(static x => x!.GetValue<double>())];
}
