using CyberCloud.Providers.Monitor.Query;
using CyberCloud.ResourceManager.Registry;
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
}
