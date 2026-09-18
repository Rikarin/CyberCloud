using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     The <see cref="IResourceGraphQuery" /> over ClickHouse: the caller's KQL, translated, filtered
///     to what the caller may read, run under a budget, and paged.
/// </summary>
/// <remarks>
///     <para>
///         <b>Five steps, in an order that matters.</b> The continuation is checked against the
///         query text first, so a token from another query is a <c>400</c> before anything is read;
///         the caller's subjects are resolved next, so a caller the index cannot answer for gets a
///         <c>500</c> and not an empty page; the query is translated, which is where every refusal
///         happens; the tenant's table is ensured, so a tenant nothing has projected yet gets an
///         empty page rather than "database does not exist"; and the statement runs.
///     </para>
///     <para>
///         ⚠ <b>Four ClickHouse settings ride on every query and none is the caller's to change.</b>
///         <c>max_execution_time</c> and <c>max_rows_to_read</c> are the budget
///         (<see cref="ResourceGraphOptions.QueryTimeout" />,
///         <see cref="ResourceGraphOptions.QueryMaxRowsToRead" />); <c>readonly=2</c> makes the
///         connection refuse any statement that is not a read, so a defect in the translator cannot
///         become a write — it is belt to the translator's braces, since the SQL is generated and the
///         caller's text is bound as parameters; and <c>prefer_column_name_to_alias=1</c> is what
///         lets the translator alias an output column with a source column's name
///         (<c>extend name = toupper(name)</c>) without ClickHouse reading the alias inside its own
///         definition.
///     </para>
///     <para>
///         <b>One row more than the page.</b> The statement's <c>LIMIT</c> is the page size plus one;
///         a result longer than the page has a next page and the extra row is dropped. There is no
///         count of the whole result, for the reason <c>ResourceListPage</c> gives.
///     </para>
///     <para>
///         ⚠ <b>What the store says goes to the log, and the caller gets one of two sentences
///         (#54 review).</b> ClickHouse answers a statement it refuses with its exception text, and
///         that text quotes the whole statement: the tenant database, every storage column,
///         <c>is_deleted</c>, the <c>hasAny(access, […])</c> filter with the caller's usersets
///         substituted in, and the endpoint's URL. The first cut passed it through as the <c>500</c>
///         body — the gateway replaces the message of a thrown exception and not of a returned
///         failure — against docs/plan/08 § Errors, <i>"No exception details, ever"</i>. Now a
///         refusal for exceeding the budget (<c>TIMEOUT_EXCEEDED</c>, <c>TOO_MANY_ROWS</c>) is an
///         <see cref="ErrorCode.InvalidRequestBody" /> naming the budget and how to narrow the
///         query, and any other failure — the store unreachable, a statement it would not run, a
///         body that is not JSON, a caller the index could not resolve — is an
///         <see cref="ErrorCode.InternalError" /> whose message says only that; the detail is
///         logged at error with the tenant and the query.
///         <c>ResourceGraphQueryFailureTests</c> holds both against a stubbed server and
///         <c>ResourceGraphQueryServiceTests.AQueryPastItsBudgetIsA400ThatNamesTheBudgetAndNotTheStatement</c>
///         against the real one.
///     </para>
/// </remarks>
public sealed class ResourceGraphQueryService : IResourceGraphQuery {
    /// <summary>The one sentence a caller reads for a failure that is not theirs to fix.</summary>
    public const string FailedSentence =
        "The resource graph could not run the query. The store's answer is in the gateway's log; quote the response's request id when reporting this.";

    readonly ClickHouseClient clickHouse;
    readonly ClickHouseResourceGraphStore store;
    readonly ICallerAccessResolver access;
    readonly ResourceGraphOptions options;
    readonly ILogger<ResourceGraphQueryService> logger;

    /// <summary>Creates the service.</summary>
    /// <param name="clickHouse">The HTTP client for the platform's ClickHouse.</param>
    /// <param name="store">The projection's store, for ensuring a tenant's table exists before it is read.</param>
    /// <param name="access">Who the caller is, as the access column spells it.</param>
    /// <param name="options">The bound section; the query budget is read from it.</param>
    /// <param name="logger">Where the store's own words go, since the caller never sees them.</param>
    public ResourceGraphQueryService(
        ClickHouseClient clickHouse,
        ClickHouseResourceGraphStore store,
        ICallerAccessResolver access,
        ResourceGraphOptions options,
        ILogger<ResourceGraphQueryService> logger
    ) {
        ArgumentNullException.ThrowIfNull(clickHouse);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.clickHouse = clickHouse;
        this.store = store;
        this.access = access;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ResourceGraphQueryPage>> QueryAsync(ResourceGraphQueryRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Caller.TenantId == Guid.Empty) {
            return Result<ResourceGraphQueryPage>.Failure(ErrorCode.InvalidRequestBody, "The query names no tenant.");
        }

        var offset = ResourceGraphContinuation.Decode(request.Query, request.Continuation);

        if (offset.TryGetError(out var tokenError)) {
            return Result<ResourceGraphQueryPage>.Failure(tokenError);
        }

        var subjects = await access.SubjectsOfAsync(request.Caller, cancellationToken);

        if (subjects.TryGetError(out var accessError)) {
            return Failed(request, "the caller's access could not be resolved, so the query was not run", accessError.Message);
        }

        var translated = KqlTranslator.Translate(
            request.Query,
            new(request.Caller.TenantId, subjects.GetValueOrThrow(), request.PageSize, offset.GetValueOrThrow())
        );

        if (translated.TryGetError(out var translationError)) {
            return Result<ResourceGraphQueryPage>.Failure(translationError);
        }

        var ensured = await store.EnsureTenantAsync(request.Caller.TenantId, cancellationToken);

        if (ensured.TryGetError(out var ensureError)) {
            return Failed(request, "the tenant's table could not be ensured", ensureError.Message);
        }

        var query = translated.GetValueOrThrow();

        var executed = await clickHouse.ExecuteAsync(query.Sql + " FORMAT JSON", query.ParameterValues, Settings(), cancellationToken);

        if (executed.TryGetError(out var executionError)) {
            if (ClickHouseClient.IsBudgetExceeded(executionError)) {
                // The caller's to fix, so it is told which budget and how — and not what the
                // statement looked like.
                logger.LogInformation(
                    "Resource graph query for tenant {TenantId} exceeded its budget (ClickHouse code {Code}): {Query}",
                    request.Caller.TenantId,
                    ClickHouseClient.ExceptionCode(executionError),
                    request.Query
                );

                return Result<ResourceGraphQueryPage>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"The query ran past the resource graph's budget of {Settings()["max_execution_time"]} s or "
                    + $"{options.QueryMaxRowsToRead.ToString("N0", CultureInfo.InvariantCulture)} rows read. Narrow it with a where on "
                    + "name, type, resourceGroup or a tag, or ask for less with project and take."
                );
            }

            return Failed(request, "the store refused the statement", executionError.Message);
        }

        try {
            return Result<ResourceGraphQueryPage>.Success(Page(request, query, executed.GetValueOrThrow()));
        }
        catch (JsonException exception) {
            var body = executed.GetValueOrThrow();

            return Failed(
                request,
                "the store answered 200 with a body that is not the JSON format asked for",
                $"{exception.Message}. The body began: {(body.Length > 200 ? body[..200] + "…" : body).Trim()}"
            );
        }
    }

    /// <summary>
    ///     The failure a caller reads for anything that is not theirs to fix: the detail to the log,
    ///     <see cref="FailedSentence" /> to the caller.
    /// </summary>
    Result<ResourceGraphQueryPage> Failed(ResourceGraphQueryRequest request, string what, string detail) {
        logger.LogError(
            "Resource graph query for tenant {TenantId} failed: {What}. Detail: {Detail}. Query: {Query}",
            request.Caller.TenantId,
            what,
            detail,
            request.Query
        );

        return Result<ResourceGraphQueryPage>.Failure(ErrorCode.InternalError, FailedSentence);
    }

    /// <summary>The per-query settings — see the remarks on this type.</summary>
    Dictionary<string, string> Settings() =>
        new(StringComparer.Ordinal) {
            ["max_execution_time"] = Math.Max(1, (long)options.QueryTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["max_rows_to_read"] = options.QueryMaxRowsToRead.ToString(CultureInfo.InvariantCulture),
            ["readonly"] = "2",
            ["prefer_column_name_to_alias"] = "1"
        };

    /// <summary>
    ///     The page out of ClickHouse's <c>JSON</c> format — <c>{ "meta": […], "data": […], … }</c>:
    ///     up to a page of <c>data</c>'s rows as text, and a continuation when the extra row the
    ///     statement asked for came back.
    /// </summary>
    static ResourceGraphQueryPage Page(ResourceGraphQueryRequest request, TranslatedQuery query, string body) {
        using var document = JsonDocument.Parse(body);
        var rows = ImmutableArray.CreateBuilder<string>();
        var returned = 0;

        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
            foreach (var row in data.EnumerateArray()) {
                returned++;

                if (rows.Count < query.PageSize) {
                    rows.Add(row.GetRawText());
                }
            }
        }

        return new() {
            Columns = query.Columns,
            Rows = rows.ToImmutable(),
            Continuation = returned > query.PageSize
                ? ResourceGraphContinuation.Encode(request.Query, query.Offset + query.PageSize)
                : string.Empty
        };
    }
}
