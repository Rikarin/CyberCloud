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
/// </remarks>
public sealed class ResourceGraphQueryService : IResourceGraphQuery {
    readonly ClickHouseClient clickHouse;
    readonly ClickHouseResourceGraphStore store;
    readonly ICallerAccessResolver access;
    readonly ResourceGraphOptions options;

    /// <summary>Creates the service.</summary>
    /// <param name="clickHouse">The HTTP client for the platform's ClickHouse.</param>
    /// <param name="store">The projection's store, for ensuring a tenant's table exists before it is read.</param>
    /// <param name="access">Who the caller is, as the access column spells it.</param>
    /// <param name="options">The bound section; the query budget is read from it.</param>
    public ResourceGraphQueryService(
        ClickHouseClient clickHouse,
        ClickHouseResourceGraphStore store,
        ICallerAccessResolver access,
        ResourceGraphOptions options
    ) {
        ArgumentNullException.ThrowIfNull(clickHouse);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);

        this.clickHouse = clickHouse;
        this.store = store;
        this.access = access;
        this.options = options;
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
            return Result<ResourceGraphQueryPage>.Failure(
                ErrorCode.InternalError,
                $"The caller's access could not be resolved, so the query was not run: {accessError.Message}"
            );
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
            return Result<ResourceGraphQueryPage>.Failure(ensureError);
        }

        var query = translated.GetValueOrThrow();

        var executed = await clickHouse.ExecuteAsync(query.Sql + " FORMAT JSON", query.ParameterValues, Settings(), cancellationToken);

        if (executed.TryGetError(out var executionError)) {
            return Result<ResourceGraphQueryPage>.Failure(executionError);
        }

        try {
            return Result<ResourceGraphQueryPage>.Success(Page(request, query, executed.GetValueOrThrow()));
        }
        catch (JsonException exception) {
            var body = executed.GetValueOrThrow();

            return Result<ResourceGraphQueryPage>.Failure(
                ErrorCode.InternalError,
                $"ClickHouse answered 200 with a body that is not the JSON format asked for: {exception.Message}. "
                + $"The body began: {(body.Length > 200 ? body[..200] + "…" : body).Trim()}"
            );
        }
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
