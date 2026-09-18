using CyberCloud.ResourceGraph.Query;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Net;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>
///     What a caller reads when the store does not answer the query — and what they never read.
///     docs/plan/08 § Errors, <i>"No exception details, ever"</i>, applied to a returned failure
///     rather than a thrown one (#54 review).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Against a scripted server, because the leak was in the shape of ClickHouse's
///         answer and not in any one query.</b> The real server refuses a statement with its
///         exception text, which quotes the whole statement back: the tenant database, every
///         column, <c>is_deleted</c>, <c>hasAny(access, ['user:alice'])</c> with the caller's usersets
///         in it, and the endpoint's URL; the first cut passed that text through as the <c>500</c>
///         body. The script here is that answer, taken from a real 25.3 for
///         <c>resources | where tags has 'prod'</c> before the translator was fixed, so the assertion
///         is about the sentence a caller gets and the line the log gets.
///         <c>ResourceGraphQueryServiceTests.AQueryPastItsBudgetIsA400ThatNamesTheBudgetAndNotTheStatement</c>
///         drives the budget half against the real server.
///     </para>
/// </remarks>
public sealed class ResourceGraphQueryFailureTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    const string ClickHouseAnswer =
        "Code: 43. DB::Exception: Illegal type Map(String, String) of argument of function match: In scope SELECT toString(resource_id) AS resourceId, "
        + "name AS name FROM tenant_11111111111141118111111111111111.resource_graph FINAL WHERE (is_deleted = 0) AND hasAny(access, _CAST(['user:alice'], "
        + "'Array(String)')) AND match(tags, _CAST('(?i)prod', 'String')) ORDER BY name ASC LIMIT 51. (ILLEGAL_TYPE_OF_ARGUMENT) (version 25.3.6.56 (official build))";

    static readonly string[] NeverInTheBody = ["SELECT", "tenant_", "hasAny", "user:alice", "is_deleted", "DB::Exception", "clickhouse.internal", "http://", "Code: "];

    [Theory]
    [InlineData(43, ClickHouseAnswer)]
    [InlineData(0, "<html><body>502 Bad Gateway from the proxy in front of http://clickhouse.internal:8123/</body></html>")]
    public async Task AStatementTheStoreRefusesIsA500WithOneSentenceAndTheDetailInTheLog(int code, string body) {
        var (service, log) = Service(new ScriptedHandler(code, body));

        var answered = await service.QueryAsync(
            new() { Query = "resources | where tags has 'prod'", Caller = new() { TenantId = Tenant, SubjectType = "user", SubjectId = "alice" } },
            TestContext.Current.CancellationToken
        );

        answered.IsFailure.ShouldBeTrue();
        answered.Error!.Code.ShouldBe(ErrorCode.InternalError);
        answered.Error.Message.ShouldBe(ResourceGraphQueryService.FailedSentence);

        foreach (var word in NeverInTheBody) {
            answered.Error.Message.ShouldNotContain(word);
        }

        // The detail went to the log, with the tenant and the query beside it, so the request id in
        // the response finds it.
        log.Lines.ShouldContain(x => x.Contains("the store refused the statement", StringComparison.Ordinal));
        var opening = body[..40];
        log.Lines.ShouldContain(x => x.Contains(opening, StringComparison.Ordinal));
        log.Lines.ShouldContain(x => x.Contains(Tenant.ToString("D"), StringComparison.Ordinal));
        log.Lines.ShouldContain(x => x.Contains("resources | where tags has 'prod'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ClickHouseClient.TimeoutExceeded, "Code: 159. DB::Exception: Timeout exceeded: elapsed 10.2 seconds, maximum: 10: While executing MergeTreeSelect(pool: ReadPool, algorithm: Thread). (TIMEOUT_EXCEEDED)")]
    [InlineData(ClickHouseClient.TooManyRows, "Code: 158. DB::Exception: Limit for rows (controlled by 'max_rows_to_read' setting) exceeded, max rows: 1.00 million, current rows: 1.05 million. (TOO_MANY_ROWS)")]
    [InlineData(ClickHouseClient.TooManyRowsOrBytes, "Code: 396. DB::Exception: Limit for result exceeded. (TOO_MANY_ROWS_OR_BYTES)")]
    public async Task AQueryPastItsBudgetIsA400ThatNamesTheBudgetAndHowToNarrowIt(int code, string body) {
        var (service, log) = Service(new ScriptedHandler(code, body + " In scope SELECT name FROM tenant_11111111111141118111111111111111.resource_graph"));

        var answered = await service.QueryAsync(
            new() { Query = "resources | project name", Caller = new() { TenantId = Tenant, SubjectType = "user", SubjectId = "alice" } },
            TestContext.Current.CancellationToken
        );

        answered.IsFailure.ShouldBeTrue();
        answered.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        answered.Error.Message.ShouldContain("budget of 10 s or 1,000,000 rows read");
        answered.Error.Message.ShouldContain("Narrow it");

        foreach (var word in NeverInTheBody) {
            answered.Error.Message.ShouldNotContain(word);
        }

        log.Lines.ShouldContain(x => x.Contains("exceeded its budget", StringComparison.Ordinal) && x.Contains($"code {code}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoreThatCannotBeReachedIsTheSameSentence() {
        var (service, log) = Service(new ThrowingHandler());

        var answered = await service.QueryAsync(
            new() { Query = "resources | project name", Caller = new() { TenantId = Tenant, SubjectType = "user", SubjectId = "alice" } },
            TestContext.Current.CancellationToken
        );

        answered.IsFailure.ShouldBeTrue();
        answered.Error!.Code.ShouldBe(ErrorCode.InternalError);
        answered.Error.Message.ShouldBe(ResourceGraphQueryService.FailedSentence);
        answered.Error.Message.ShouldNotContain("clickhouse.internal");
        log.Lines.ShouldContain(x => x.Contains("could not be reached", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExceptionCodeIsReadOffTheClientsOwnSpellingAndNotOffTheBody() {
        ClickHouseClient.ExceptionCode(new Error(ErrorCode.InternalError, "ClickHouse at http://x/ answered 500 Internal Server Error (X-ClickHouse-Exception-Code: 159): Code: 159. DB::Exception: …")).ShouldBe(159);
        ClickHouseClient.ExceptionCode(new Error(ErrorCode.InternalError, "ClickHouse at http://x/ could not be reached: refused")).ShouldBe(0);
        // A body that names the header does not count; only the client's parenthesis does.
        ClickHouseClient.ExceptionCode(new Error(ErrorCode.InternalError, "X-ClickHouse-Exception-Code: 159")).ShouldBe(0);
        ClickHouseClient.IsBudgetExceeded(new Error(ErrorCode.InternalError, "ClickHouse at http://x/ answered 500 Internal Server Error (X-ClickHouse-Exception-Code: 43): Code: 43.")).ShouldBeFalse();
    }

    static (ResourceGraphQueryService Service, CapturingLogger Log) Service(HttpMessageHandler handler) {
        var options = new ResourceGraphOptions {
            ClickHouseEndpoint = "http://clickhouse.internal:8123/",
            AllowInsecureTransport = true
        };
        var client = new ClickHouseClient(new HttpClient(handler), options);
        var log = new CapturingLogger();

        return (new ResourceGraphQueryService(client, new ClickHouseResourceGraphStore(client), new OneCaller(), options, log), log);
    }

    /// <summary>
    ///     Answers the DDL the store issues with an empty <c>200</c> and the <c>SELECT</c> with the
    ///     scripted failure, the way a real server answers a statement it will not run — the
    ///     exception text as the body and its number in the header.
    /// </summary>
    sealed class ScriptedHandler(int code, string body) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var sql = await request.Content!.ReadAsStringAsync(cancellationToken);

            if (!sql.StartsWith("SELECT", StringComparison.Ordinal)) {
                return new(HttpStatusCode.OK) { Content = new StringContent("") };
            }

            var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(body) };

            if (code != 0) {
                response.Headers.Add(ClickHouseClient.ExceptionCodeHeader, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return response;
        }
    }

    sealed class ThrowingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it (clickhouse.internal:8123)");
    }

    sealed class OneCaller : ICallerAccessResolver {
        public Task<Result<ImmutableArray<string>>> SubjectsOfAsync(CallerContext caller, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<string>>.Success(["user:" + caller.SubjectId]));
    }

    /// <summary>An <see cref="ILogger{T}" /> that keeps every formatted line, after <c>CyberCloud.Vault.Tests</c>'.</summary>
    sealed class CapturingLogger : ILogger<ResourceGraphQueryService> {
        readonly List<string> lines = [];

        public IReadOnlyList<string> Lines => lines;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            ArgumentNullException.ThrowIfNull(formatter);
            lines.Add(formatter(state, exception));
        }
    }
}
