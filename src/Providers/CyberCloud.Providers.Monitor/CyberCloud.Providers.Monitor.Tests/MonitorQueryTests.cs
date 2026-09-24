using CyberCloud.Providers.Monitor.Query;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The explorers' three actions as the registry sees them, and the rules their handlers apply
///     before any store is asked (#41). The stores themselves are asked in
///     <c>MonitorQueryOverHttpTests</c>, over a real VictoriaMetrics cluster and a real ClickHouse.
/// </summary>
public sealed class MonitorQueryTests {
    static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheThreeActionsAreDeclaredWithReadTheirRequestsAndTheirHandlers() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorWorkspaces.Type, out var registration).ShouldBeTrue();

        (string Name, Type Handler, bool Declared)[] expected = [
            (MonitorQueries.QueryMetricsAction, typeof(MonitorWorkspaceQueryMetricsHandler), false),
            (MonitorQueries.ListMetricLabelsAction, typeof(MonitorWorkspaceListMetricLabelsHandler), true),
            (MonitorQueries.SearchLogsAction, typeof(MonitorWorkspaceSearchLogsHandler), false)
        ];

        foreach (var (name, handler, declared) in expected) {
            registration.TryGetAction(name, out var action).ShouldBeTrue(name);

            // ⚠ `read`, the type's own read permission — so the Reader role runs all three and nothing
            // narrower than reading the workspace is asked for. MonitorQueries' remarks carry the why.
            action.Permission.ShouldBe(registration.ReadPermission, name);
            action.Secret.ShouldBeFalse(name);
            action.LongRunning.ShouldBeFalse(name);
            action.Request.ShouldNotBeNull(name);
            action.HandlerType.ShouldBe(handler, name);

            // Two responses are arrays of objects the schema cannot say — conformance.yaml § owed,
            // query-responses-are-undeclared. This pins which, so declaring one is a decision.
            (action.Response is not null).ShouldBe(declared, name);
        }
    }

    // ── #41's review: the account is asked for only by the workspace that holds it ──────────────

    [Theory]
    [InlineData("unclaimed")]
    [InlineData("held by another workspace")]
    public async Task AWorkspaceThatDoesNotHoldItsAccountIsRefusedAndTheStoreIsNeverAsked(string ledgerState) {
        // ⚠ The two ways a workspace comes to not hold its account: its create hasn't converged, or it
        // folded onto an account another tenant's workspace claimed first. Both are a 409, and in both
        // the store is never asked — asking it is the cross-tenant read.
        var store = new CountingMetricsStore();
        var ledger = new DictionaryAccounts();
        var (context, account) = Context(MonitorQueries.QueryMetricsAction, """{"query":"up"}""");

        if (ledgerState != "unclaimed") {
            (await ledger.ClaimAsync(account, Guid.NewGuid(), TestContext.Current.CancellationToken)).ShouldBeTrue();
        }

        var queried = await new MonitorWorkspaceQueryMetricsHandler(store, ledger, new FixedClock())
            .InvokeAsync(context, TestContext.Current.CancellationToken);

        var (labelContext, _) = Context(MonitorQueries.ListMetricLabelsAction, """{"label":"__name__"}""");

        var listed = await new MonitorWorkspaceListMetricLabelsHandler(store, ledger, new FixedClock())
            .InvokeAsync(labelContext, TestContext.Current.CancellationToken);

        foreach (var answered in new[] { queried, listed }) {
            answered.Error!.Code.ShouldBe(ErrorCode.Conflict, ledgerState);
            answered.Error.Message.ShouldContain("doesn't hold its metrics account");
        }

        store.Calls.ShouldBe(0, $"the store was asked under an account the workspace doesn't hold ({ledgerState})");
    }

    [Fact]
    public async Task TheWorkspaceThatHoldsItsAccountIsAnsweredUnderIt() {
        var store = new CountingMetricsStore();
        var ledger = new DictionaryAccounts();
        var (context, account) = Context(MonitorQueries.QueryMetricsAction, """{"query":"up"}""");

        (await ledger.ClaimAsync(account, context.Id.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var answered = await new MonitorWorkspaceQueryMetricsHandler(store, ledger, new FixedClock())
            .InvokeAsync(context, TestContext.Current.CancellationToken);

        answered.IsSuccess.ShouldBeTrue(answered.Error?.Message);
        store.Calls.ShouldBe(1);
        store.LastAccount.ShouldBe(account);
    }

    static (ActionContext Context, uint Account) Context(string action, string body) {
        var id = new ResourceId(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            MonitorWorkspaces.Type,
            "prod",
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

        var context = new ActionContext(
            id,
            MonitorWorkspaces.V2026,
            action,
            JsonDocument.Parse(body).RootElement,
            JsonDocument.Parse(MonitorWorkspaces.Body(Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c"))).RootElement,
            "",
            null,
            new InMemorySecretVault()
        );

        return (context, MonitorWorkspaces.AccountId(id));
    }

    [Fact]
    public void ARangeQueryIsBoundedByTheWindowAndThePointCeiling() {
        Read("""{"query":"up","start":"2026-09-23T11:00:00Z","end":"2026-09-23T12:00:00Z"}""")
            .GetValueOrThrow()
            .StepSeconds.ShouldBe(15, "an hour cut into 240 points");

        Read("""{"query":"up","start":"2026-09-23T11:00:00Z"}""").Error!.Message.ShouldContain("both");
        Read("""{"query":"up","start":"2026-09-23T12:00:00Z","end":"2026-09-23T11:00:00Z"}""").Error!.Message.ShouldContain("not after");
        Read("""{"query":"up","start":"2025-08-01T00:00:00Z","end":"2026-09-23T00:00:00Z"}""").Error!.Message.ShouldContain("400");

        var fine = Read("""{"query":"up","start":"2026-09-01T00:00:00Z","end":"2026-09-23T00:00:00Z","stepSeconds":1}""");
        fine.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        fine.Error.Message.ShouldContain("at least 173 s", customMessage: "22 days over 11000 points, rounded up");

        var instant = Read("""{"query":"up"}""").GetValueOrThrow();
        instant.IsRange.ShouldBeFalse();
        instant.Time.ShouldBe(Now);

        static Result<MetricsQuery> Read(string json) {
            using var body = JsonDocument.Parse(json);
            return MonitorWorkspaceQueryMetricsHandler.ReadQuery(body.RootElement, Now);
        }
    }

    [Fact]
    public void ASearchIsBoundedByTheWindowTheFilterCountAndTheBucketCount() {
        var parsed = Read("""{"from":"2026-09-23T11:00:00Z","to":"2026-09-23T12:00:00Z","attributes":["k=v=w","empty="],"traceId":"ABCDEF0123456789ABCDEF0123456789"}""")
            .GetValueOrThrow();

        parsed.BucketSeconds.ShouldBe(60, "an hour cut into 60 buckets");
        parsed.Top.ShouldBe(MonitorQueries.DefaultLogRows);
        parsed.Attributes.ShouldBe([("k", "v=w"), ("empty", "")], "split at the FIRST '=', so a value may carry one");
        parsed.TraceId.ShouldBe("abcdef0123456789abcdef0123456789");

        Read("""{"from":"2026-06-01T00:00:00Z","to":"2026-09-23T00:00:00Z"}""").Error!.Message.ShouldContain("90");
        Read("""{"from":"2026-09-01T00:00:00Z","to":"2026-09-23T00:00:00Z","bucketSeconds":60}""").Error!.Message.ShouldContain("1000");

        var eleven = string.Join(',', Enumerable.Range(0, 11).Select(static x => $"\"k{x}=v\""));
        Read($$"""{"from":"2026-09-23T11:00:00Z","to":"2026-09-23T12:00:00Z","attributes":[{{eleven}}]}""").Error!.Message.ShouldContain("10");

        static Result<LogSearch> Read(string json) {
            using var body = JsonDocument.Parse(json);
            return MonitorWorkspaceSearchLogsHandler.ReadSearch(body.RootElement);
        }
    }

    [Fact]
    public void TheRequestSchemasRefuseWhatAHandlerMustNeverSee() {
        // A body member nobody declared — VictoriaMetrics' extra_label, which narrows or widens what an
        // account sees — is refused before any handler runs.
        Validate(MonitorQueries.QueryMetricsRequest, """{"query":"up","extra_label":"tenant=b"}""").IsFailure.ShouldBeTrue();

        // A label name is a path segment on the store's side.
        Validate(MonitorQueries.ListMetricLabelsRequest, """{"label":"../admin"}""").IsFailure.ShouldBeTrue();
        Validate(MonitorQueries.ListMetricLabelsRequest, """{"label":"__name__"}""").IsSuccess.ShouldBeTrue();

        Validate(MonitorQueries.SearchLogsRequest, """{"from":"2026-09-23T11:00:00Z","to":"2026-09-23T12:00:00Z","severities":["critical"]}""")
            .IsFailure.ShouldBeTrue("six severity classes, and critical is not one of them");
        Validate(MonitorQueries.SearchLogsRequest, """{"from":"2026-09-23T11:00:00Z","to":"2026-09-23T12:00:00Z","attributes":["=value"]}""")
            .IsFailure.ShouldBeTrue("a filter with no key");
        Validate(MonitorQueries.SearchLogsRequest, """{"from":"2026-09-23T11:00:00Z"}""").IsFailure.ShouldBeTrue("to is required");

        static Result Validate(ResourceSchema schema, string json) {
            using var body = JsonDocument.Parse(json);
            return schema.Validate(body.RootElement);
        }
    }

    [Fact]
    public void TheStoresAnswerIsParsedWithNonFiniteValuesAsGapsAndTheSeriesCapCounted() {
        var series = string.Join(
            ',',
            Enumerable.Range(0, MonitorQueries.MaxSeries + 3)
                .Select(static x => $$"""{"metric":{"__name__":"up","i":"{{x}}"},"values":[[1790000000,"1"],[1790000060,"NaN"],[1790000120,"+Inf"]]}""")
        );

        var answer = VictoriaMetricsQueryStore.ParseQuery($$$"""{"status":"success","data":{"resultType":"matrix","result":[{{{series}}}]}}""");

        answer.Series.Length.ShouldBe(MonitorQueries.MaxSeries);
        answer.SeriesTotal.ShouldBe(MonitorQueries.MaxSeries + 3);
        answer.Truncated.ShouldBeTrue();
        answer.Series[0].Points.Select(static x => x.Value).ShouldBe([1.0, null, null]);

        var scalar = VictoriaMetricsQueryStore.ParseQuery("""{"status":"success","data":{"resultType":"scalar","result":[1790000000,"42"]}}""");
        scalar.Series.ShouldHaveSingleItem().Points.ShouldHaveSingleItem().Value.ShouldBe(42);
    }

    [Fact]
    public void OnlyAWorkspaceDatabaseNameIsEverSpelledIntoAStatement() {
        var id = new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", MonitorWorkspaces.Type, "w", Guid.NewGuid());

        MonitorQueries.IsWorkspaceDatabase(MonitorWorkspaces.Database(id)).ShouldBeTrue();
        MonitorQueries.IsWorkspaceDatabase("ws_x`; DROP DATABASE system; --").ShouldBeFalse();
        MonitorQueries.IsWorkspaceDatabase("system").ShouldBeFalse();
        Should.Throw<ArgumentException>(() => MonitorLogsTable.CreateSql("default"));
    }

    [Fact]
    public void ALogSearchValueIsSpelledInClickHousesEscapedFormat() {
        // ⚠ ClickHouse reads param_* in the escaped format: sent raw, "\t" in a Windows path is a tab
        // and a lone backslash fails the search. The store's search tests find each over a real server.
        ClickHouseLogStore.ParameterValue(@"C:\temp\new").ShouldBe(@"C:\\temp\\new");
        ClickHouseLogStore.ParameterValue(@"\").ShouldBe(@"\\");
        ClickHouseLogStore.ParameterValue("a\tb\nc\rd\0e").ShouldBe(@"a\tb\nc\rd\0e");
        ClickHouseLogStore.ParameterValue("')) OR 1=1 --").ShouldBe("')) OR 1=1 --", "a quote means nothing in the escaped format");
    }

    [Fact]
    public void SeverityIsTheBandOfTheNumber() {
        MonitorQueries.SeverityOf(0).ShouldBe("unspecified");
        MonitorQueries.SeverityOf(9).ShouldBe("info");
        MonitorQueries.SeverityOf(17).ShouldBe("error");
        MonitorQueries.SeverityOf(20).ShouldBe("error");
        MonitorQueries.SeverityOf(24).ShouldBe("fatal");
        MonitorQueries.SeverityOf(25).ShouldBe("unspecified");
    }

    [Fact]
    public async Task AnUnconfiguredHostRefusesByNameAndAPlainHttpEndpointIsRefusedAtComposition() {
        var refusing = new UnavailableMonitorQueryStore();
        (await refusing.QueryAsync(new(1, "short"), new("up", null, null, Now, 0), TestContext.Current.CancellationToken)).Error!.Message
            .ShouldContain(MonitorQueryOptions.SectionName);

        Should.Throw<ArgumentException>(() => new MonitorQueryOptions { MetricsEndpoint = "http://vmselect:8481" }.Validate())
            .Message.ShouldContain("AllowInsecureTransport");

        new MonitorQueryOptions { MetricsEndpoint = "https://vmselect-telemetry-{tier}.cybercloud-telemetry.svc:8481" }
            .MetricsEndpointFor("extended")
            .Host.ShouldBe("vmselect-telemetry-extended.cybercloud-telemetry.svc");
    }

    [Theory]
    [InlineData(30, 2, "70")]
    [InlineData(100, 1, null)]
    public async Task ALogSearchsTwoStatementsShareOneRowBudget(long firstReads, int statements, string? secondBudget) {
        // ⚠ #41's review: the rows and the histogram each got the whole max_rows_to_read, so a search
        // could read twice what the refusal names. The histogram now gets what the rows left, and a
        // search the rows exhausted is refused before the histogram runs — max_rows_to_read=0 would be
        // ClickHouse's "unlimited".
        var clickHouse = new SummarizingClickHouse(firstReads);
        var store = new ClickHouseLogStore(
            new HttpClient(clickHouse),
            new MonitorQueryOptions { LogsEndpoint = "https://clickhouse:8443", LogsMaxRowsToRead = 100 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClickHouseLogStore>.Instance
        );

        using var body = JsonDocument.Parse("""{"from":"2026-09-23T11:00:00Z","to":"2026-09-23T12:00:00Z"}""");
        var search = MonitorWorkspaceSearchLogsHandler.ReadSearch(body.RootElement).GetValueOrThrow();

        var answered = await store.SearchAsync("ws_" + new string('a', 32), search, TestContext.Current.CancellationToken);

        clickHouse.Budgets.Count.ShouldBe(statements);
        clickHouse.Budgets[0].ShouldBe("100");

        if (secondBudget is null) {
            answered.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
            answered.Error.Message.ShouldContain("its rows and its histogram together");
        } else {
            answered.IsSuccess.ShouldBeTrue(answered.Error?.Message);
            clickHouse.Budgets[1].ShouldBe(secondBudget);
        }
    }

    [Theory]
    [InlineData("https://vmselect:8481", "https://vmselect:8481/select/7/prometheus/api/v1/query")]
    [InlineData("https://vmselect:8481/", "https://vmselect:8481/select/7/prometheus/api/v1/query")]
    [InlineData("https://gateway.example/vm", "https://gateway.example/vm/select/7/prometheus/api/v1/query")]
    [InlineData("https://gateway.example/vm/", "https://gateway.example/vm/select/7/prometheus/api/v1/query")]
    public void AnEndpointsPathPrefixIsKept(string endpoint, string expected) =>
        // ⚠ #41's review: vmselect behind an ingress, vmauth or a proxy path lost its prefix, because
        // both stores resolved an absolute path against the endpoint.
        MonitorQueryOptions.Resolve(new Uri(endpoint), "select/7/prometheus/api/v1/query").AbsoluteUri.ShouldBe(expected);

    [Fact]
    public void ClickHousesQueryStringKeepsTheEndpointsPathAndAnEndpointMayNotCarryItsOwn() {
        MonitorQueryOptions.Resolve(new Uri("https://gateway.example/clickhouse"), "?readonly=2&max_rows_to_read=5")
            .AbsoluteUri.ShouldBe("https://gateway.example/clickhouse/?readonly=2&max_rows_to_read=5");

        Should.Throw<ArgumentException>(() => new MonitorQueryOptions { LogsEndpoint = "https://clickhouse:8443/?user=admin" }.Validate())
            .Message.ShouldContain("query string");

        Should.Throw<ArgumentException>(() => new MonitorQueryOptions { MetricsEndpoint = "https://vmselect:8481/#fragment" }.Validate())
            .Message.ShouldContain("fragment");
    }
}

/// <summary>A metrics store that counts what it was asked and answers empty.</summary>
sealed class CountingMetricsStore : IMonitorMetricsStore {
    public int Calls { get; private set; }

    public uint LastAccount { get; private set; }

    public Task<Result<MetricsAnswer>> QueryAsync(MetricsTenancy tenancy, MetricsQuery query, CancellationToken cancellationToken = default) {
        Calls++;
        LastAccount = tenancy.AccountId;

        return Task.FromResult(Result<MetricsAnswer>.Success(new("vector", ImmutableArray<MetricSeries>.Empty, 0)));
    }

    public Task<Result<LabelAnswer>> LabelsAsync(MetricsTenancy tenancy, LabelQuery query, CancellationToken cancellationToken = default) {
        Calls++;
        LastAccount = tenancy.AccountId;

        return Task.FromResult(Result<LabelAnswer>.Success(new(ImmutableArray<string>.Empty, false)));
    }
}

/// <summary>
///     A ClickHouse HTTP interface that answers every statement with no rows and a summary saying the
///     first statement read <c>firstReads</c> rows, and records the <c>max_rows_to_read</c> each carried.
/// </summary>
sealed class SummarizingClickHouse(long firstReads) : HttpMessageHandler {
    public List<string> Budgets { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var budget = request.RequestUri!.Query.TrimStart('?')
            .Split('&')
            .Select(static x => x.Split('=', 2))
            .First(static x => x[0] == "max_rows_to_read")[1];

        Budgets.Add(budget);

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") };
        var read = Budgets.Count == 1 ? firstReads : 0;
        response.Headers.Add("X-ClickHouse-Summary", $$"""{"read_rows":"{{read}}","read_bytes":"0"}""");

        return Task.FromResult(response);
    }
}
