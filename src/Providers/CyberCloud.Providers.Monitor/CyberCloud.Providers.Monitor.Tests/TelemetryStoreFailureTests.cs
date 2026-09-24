using CyberCloud.Providers.Monitor.Telemetry;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     What the store says to a caller when ClickHouse refuses, and what it sends when it asks —
///     against a stubbed HTTP server, because the point is the translation, not the store.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ClickHouse's own words never reach the caller.</b> Its exception text quotes the
///         statement and the workspace's database — the finding #54's review made about the resource
///         graph — so every refusal here is asserted not to carry either.
///     </para>
///     <para>
///         What a real ClickHouse answers over rows the real exporter wrote is
///         <c>ComponentViewsAgainstClickHouseTests</c>'.
///     </para>
/// </remarks>
public sealed class TelemetryStoreFailureTests {
    static readonly ResourceId Workspace = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        "prod",
        MonitorWorkspaces.Type,
        "prod",
        Guid.Parse("0a0b0c0d-0000-4000-8000-000000000001")
    );

    [Theory]
    [InlineData(159, "InvalidRequestBody", "timespanMinutes")]
    [InlineData(158, "InvalidRequestBody", "timespanMinutes")]
    [InlineData(241, "InvalidRequestBody", "timespanMinutes")]
    [InlineData(60, "Conflict", "collector-clickhouse-tables-are-the-exporters-shape")]
    [InlineData(81, "Conflict", "collector-clickhouse-tables-are-the-exporters-shape")]
    [InlineData(62, "InternalError", "did not answer")]
    public async Task ARefusalIsTranslatedAndNeverQuotesTheStatement(int code, string expected, string says) {
        var server = new StubServer(HttpStatusCode.BadRequest, $"Code: {code}. DB::Exception: SELECT secret FROM ws_0a0b0c0d000040008000000000000001.otel_traces", code);

        var refused = await Store(server).QueryAsync(Query(), TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.Value.ShouldBe(expected);
        refused.Error.Message.ShouldContain(says);
        refused.Error.Message.ShouldNotContain("SELECT");
        refused.Error.Message.ShouldNotContain("ws_");
    }

    [Fact]
    public async Task AnUnreachableStoreIsAnInternalErrorThatSaysSo() {
        var refused = await Store(new StubServer(throwOnSend: true)).QueryAsync(Query(), TestContext.Current.CancellationToken);

        refused.Error!.Code.ShouldBe(ErrorCode.InternalError);
        refused.Error.Message.ShouldContain("did not answer");
    }

    [Fact]
    public async Task TheRequestNamesTheWorkspacesDatabaseAndTheBudgetAndBindsEveryValue() {
        var server = new StubServer(HttpStatusCode.OK, """{"a":1}""" + "\n" + """{"a":2}""" + "\n");

        var rows = await Store(server).QueryAsync(Query(), TestContext.Current.CancellationToken);

        rows.GetValueOrThrow().Select(static x => x["a"]!.GetValue<int>()).ShouldBe([1, 2]);

        var query = server.LastUri!.Query;
        query.ShouldContain("database=" + MonitorWorkspaces.Database(Workspace));
        query.ShouldContain("readonly=2");
        query.ShouldContain("max_execution_time=10");
        query.ShouldContain("max_rows_to_read=");
        query.ShouldContain("max_memory_usage=");
        query.ShouldContain("param_trace=%27%20OR%201%3D1");
        server.LastBody.ShouldBe(ComponentViews.TransactionSpansSql);
        server.LastUser.ShouldBe("views");
    }

    [Fact]
    public async Task AWorkspaceWithNoGuidIsRefusedBeforeAnyRequest() {
        var server = new StubServer(HttpStatusCode.OK, "");

        var refused = await Store(server).QueryAsync(
            new(Workspace.WithId(Guid.Empty), ComponentViews.RequestsSql, new Dictionary<string, string>()),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        server.LastUri.ShouldBeNull("the store sent a request for a workspace it could not name a database for");
    }

    // ── Shaping ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestsShapeTotalsRatesAndMillisecondsFromTheRows() {
        var response = ComponentViews.Requests(
            60,
            [
                Row("""{"service":"cart","operation":"GET /items","calls":20,"failures":2,"durations":[55000000,100000000,100000000],"total":40,"totalFailed":6}"""),
                Row("""{"service":"frontend","operation":"GET /cart","calls":20,"failures":4,"durations":[110000000,200000000,200000000],"total":40,"totalFailed":6}""")
            ]
        );

        response["total"]!.GetValue<long>().ShouldBe(40);
        response["failed"]!.GetValue<long>().ShouldBe(6);
        response["ratePerMinute"]!.AsArray().Select(static x => x!.GetValue<double>()).ShouldBe([0.3333, 0.3333]);
        response["failureRate"]!.AsArray().Select(static x => x!.GetValue<double>()).ShouldBe([0.1, 0.2]);
        response["p50Ms"]!.AsArray().Select(static x => x!.GetValue<double>()).ShouldBe([55d, 110d]);
        MonitorComponents.RequestsResponse.Validate(response.Deserialize<JsonElement>(), allowTags: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyViewIsZeroAndStillTheDeclaredShape() {
        foreach (var (response, schema) in new[] {
                     (ComponentViews.Requests(60, []), MonitorComponents.RequestsResponse),
                     (ComponentViews.Dependencies(60, []), MonitorComponents.DependenciesResponse),
                     (ComponentViews.Exceptions(60, []), MonitorComponents.ExceptionsResponse),
                     (ComponentViews.Map(60, [], []), MonitorComponents.ApplicationMapResponse),
                     (ComponentViews.Transaction("4bf92f3577b34da6a3ce929d0e0e4736", [], []), MonitorComponents.TransactionResponse)
                 }) {
            schema.Validate(response.Deserialize<JsonElement>(), allowTags: false).IsSuccess.ShouldBeTrue(response.ToJsonString());
        }
    }

    [Fact]
    public void ATransactionOneSpanPastTheCapSaysItWasCut() {
        var spans = Enumerable.Range(0, MonitorComponents.MaxTransactionSpans + 1)
            .Select(static i => Row($$"""{"spanId":"{{i}}","parentSpanId":"","service":"s","name":"n","kind":"Server","startMs":1790000000000,"duration":1000000,"status":"Unset"}"""))
            .ToImmutableArray();

        var response = ComponentViews.Transaction("4bf92f3577b34da6a3ce929d0e0e4736", spans, []);

        response["truncated"]!.GetValue<bool>().ShouldBeTrue();
        response["spanCount"]!.GetValue<int>().ShouldBe(MonitorComponents.MaxTransactionSpans);
        response["starts"]![0]!.GetValue<string>().ShouldBe("2026-09-21T14:13:20.000Z");
    }

    static JsonObject Row(string json) => JsonNode.Parse(json)!.AsObject();

    static TelemetryQuery Query() =>
        new(
            Workspace,
            ComponentViews.TransactionSpansSql,
            new Dictionary<string, string> { ["trace"] = "' OR 1=1", ["minutes"] = "60", ["namespace"] = "shop", ["limit"] = "10" }
        );

    static ClickHouseTelemetryStore Store(StubServer server) =>
        new(
            new HttpClient(server),
            new() {
                ClickHouseEndpoint = "http://telemetry:8123",
                ClickHouseUser = "views",
                ClickHousePassword = "password",
                AllowInsecureTransport = true
            }
        );

    /// <summary>Answers every request with one status and body, and remembers what it was sent.</summary>
    sealed class StubServer(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "",
        int exceptionCode = 0,
        bool throwOnSend = false
    ) : HttpMessageHandler {
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";
        public string LastUser { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (throwOnSend) {
                throw new HttpRequestException("connection refused");
            }

            LastUri = request.RequestUri;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            LastUser = request.Headers.TryGetValues("X-ClickHouse-User", out var users) ? users.Single() : "";

            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };

            if (exceptionCode > 0) {
                response.Headers.Add(ClickHouseTelemetryStore.ExceptionCodeHeader, exceptionCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return response;
        }
    }
}
