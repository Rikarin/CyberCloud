using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Telemetry;

/// <summary>
///     The five views' statements, and how their rows become the columns
///     <see cref="MonitorComponents" /> publishes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every statement is a constant and every value is bound.</b> The only inputs are the
///         component's <c>service.namespace</c>, the window, the row limit and — for a transaction —
///         a trace id the schema has already held to 32 hex digits; each reaches ClickHouse as a
///         <c>{name:Type}</c> parameter. The database is not in any statement at all:
///         <see cref="ClickHouseTelemetryStore" /> sets it as the request's default.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The spellings are the exporter's, read off rows it wrote rather than off its source.
///         </b> <c>SpanKind</c> is <c>Server</c>, <c>Client</c>, <c>Internal</c>, <c>Producer</c> or
///         <c>Consumer</c>; <c>StatusCode</c> is <c>Unset</c>, <c>Ok</c> or <c>Error</c>;
///         <c>Duration</c> is nanoseconds; an exception recorded on a span is an event named
///         <c>exception</c> with <c>exception.type</c> and <c>exception.message</c> attributes — all
///         measured against the pinned collector on 2026-09-23, and
///         <c>ComponentViewsAgainstClickHouseTests</c> asserts every view's numbers over rows that
///         exporter wrote.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The percentiles are exact, and "exact" means ClickHouse's <c>quantileExact</c>: the
///             value at 0-based position ⌊level × n⌋ of the sorted durations, no interpolation.
///         </b> So p50 of 1‥100 ms is 51 and p95 of twenty values is the largest. Exact because a
///         tenant comparing two numbers from one view must not see them cross; bounded by the look-back
///         cap and the store's <c>max_memory_usage</c>, which is what exact costs.
///     </para>
/// </remarks>
public static class ComponentViews {
    const string Window =
        "Timestamp >= now64(9) - toIntervalMinute({minutes:UInt32}) "
        + "AND ResourceAttributes['service.namespace'] = {namespace:String}";

    /// <summary>Server and consumer spans, by service and operation.</summary>
    public const string RequestsSql =
        $"""
         SELECT
             ServiceName AS service,
             SpanName AS operation,
             count() AS calls,
             countIf(StatusCode = 'Error') AS failures,
             quantilesExact(0.5, 0.95, 0.99)(Duration) AS durations,
             sum(count()) OVER () AS total,
             sum(countIf(StatusCode = 'Error')) OVER () AS totalFailed
         FROM {MonitorTelemetrySchema.TracesTable}
         WHERE {Window}
           AND SpanKind IN ('Server', 'Consumer')
         GROUP BY service, operation
         ORDER BY calls DESC, service, operation
         LIMIT {"{top:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>Client and producer spans, by caller, kind of target, target and name.</summary>
    /// <remarks>
    ///     ⚠ Both generations of the semantic conventions are read — <c>db.system.name</c> beside
    ///     <c>db.system</c>, <c>http.request.method</c> beside <c>http.method</c> — because the SDKs a
    ///     tenant runs are not all one release.
    /// </remarks>
    public const string DependenciesSql =
        $"""
         SELECT
             ServiceName AS service,
             multiIf(
                 SpanAttributes['db.system.name'] != '' OR SpanAttributes['db.system'] != '', 'db',
                 SpanAttributes['messaging.system'] != '', 'messaging',
                 SpanAttributes['rpc.system'] != '', 'rpc',
                 SpanAttributes['http.request.method'] != '' OR SpanAttributes['http.method'] != '', 'http',
                 'other') AS type,
             multiIf(
                 SpanAttributes['peer.service'] != '', SpanAttributes['peer.service'],
                 SpanAttributes['server.address'] != '', SpanAttributes['server.address'],
                 SpanAttributes['db.system.name'] != '', SpanAttributes['db.system.name'],
                 SpanAttributes['db.system'] != '', SpanAttributes['db.system'],
                 SpanAttributes['messaging.system'] != '', SpanAttributes['messaging.system'],
                 '') AS target,
             SpanName AS name,
             count() AS calls,
             countIf(StatusCode = 'Error') AS failures,
             quantilesExact(0.5, 0.95, 0.99)(Duration) AS durations,
             sum(count()) OVER () AS total,
             sum(countIf(StatusCode = 'Error')) OVER () AS totalFailed
         FROM {MonitorTelemetrySchema.TracesTable}
         WHERE {Window}
           AND SpanKind IN ('Client', 'Producer')
         GROUP BY service, type, target, name
         ORDER BY calls DESC, service, type, target, name
         LIMIT {"{top:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>
    ///     Exceptions from both stores: <c>exception</c> events on spans, and log records carrying
    ///     <c>exception.type</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Counted from both, and an application that records an exception on its span and logs
    ///     it too is counted twice</b> — two telemetry items, the way Application Insights counts
    ///     them. <c>/fromSpans</c> says how the count splits, so the reader can tell.
    /// </remarks>
    public const string ExceptionsSql =
        $"""
         SELECT
             service,
             type,
             argMax(message, at) AS message,
             count() AS occurrences,
             countIf(fromSpan = 1) AS spans,
             toUnixTimestamp64Milli(max(at)) AS lastMs,
             sum(count()) OVER () AS total
         FROM
         (
             SELECT
                 ServiceName AS service,
                 attributes['exception.type'] AS type,
                 substringUTF8(attributes['exception.message'], 1, 512) AS message,
                 at,
                 toUInt8(1) AS fromSpan
             FROM {MonitorTelemetrySchema.TracesTable}
             ARRAY JOIN Events.Name AS event, Events.Attributes AS attributes, Events.Timestamp AS at
             WHERE {Window}
               AND event = 'exception'
             UNION ALL
             SELECT
                 ServiceName AS service,
                 LogAttributes['exception.type'] AS type,
                 substringUTF8(if(LogAttributes['exception.message'] != '', LogAttributes['exception.message'], Body), 1, 512) AS message,
                 Timestamp AS at,
                 toUInt8(0) AS fromSpan
             FROM {MonitorTelemetrySchema.LogsTable}
             WHERE {Window}
               AND LogAttributes['exception.type'] != ''
         )
         GROUP BY service, type
         ORDER BY occurrences DESC, service, type
         LIMIT {"{top:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>The map's nodes: every service in the component, with what it served.</summary>
    public const string MapNodesSql =
        $"""
         SELECT
             ServiceName AS service,
             countIf(SpanKind IN ('Server', 'Consumer')) AS requests,
             countIf(SpanKind IN ('Server', 'Consumer') AND StatusCode = 'Error') AS failures
         FROM {MonitorTelemetrySchema.TracesTable}
         WHERE {Window}
         GROUP BY service
         ORDER BY requests DESC, service
         LIMIT {"{top:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>
    ///     The map's edges: a span in one service whose parent span is in another is one call from
    ///     the parent's service to the child's.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both ends inside the component.</b> A call into a service another component owns is
    ///     that component's dependency and this one's client span — it shows under
    ///     <c>dependencies</c> here, not as an edge, because the map is read from spans this
    ///     component's namespace wrote on both sides.
    /// </remarks>
    public const string MapEdgesSql =
        $"""
         SELECT
             parent.service AS source,
             child.service AS target,
             count() AS calls,
             countIf(child.status = 'Error') AS failures,
             quantileExact(0.95)(child.duration) AS p95
         FROM
         (
             SELECT TraceId AS trace, SpanId AS span, ServiceName AS service
             FROM {MonitorTelemetrySchema.TracesTable}
             WHERE {Window}
         ) AS parent
         INNER JOIN
         (
             SELECT TraceId AS trace, ParentSpanId AS parentSpan, ServiceName AS service, StatusCode AS status, Duration AS duration
             FROM {MonitorTelemetrySchema.TracesTable}
             WHERE {Window}
               AND ParentSpanId != ''
         ) AS child
         ON child.trace = parent.trace AND child.parentSpan = parent.span
         WHERE parent.service != child.service
         GROUP BY source, target
         ORDER BY calls DESC, source, target
         LIMIT {"{top:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>One trace's spans, oldest first.</summary>
    public const string TransactionSpansSql =
        $"""
         SELECT
             SpanId AS spanId,
             ParentSpanId AS parentSpanId,
             ServiceName AS service,
             SpanName AS name,
             SpanKind AS kind,
             toUnixTimestamp64Milli(Timestamp) AS startMs,
             Duration AS duration,
             StatusCode AS status
         FROM {MonitorTelemetrySchema.TracesTable}
         WHERE TraceId = {"{trace:String}"}
           AND {Window}
         ORDER BY Timestamp, SpanId
         LIMIT {"{limit:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>One trace's log records, oldest first.</summary>
    public static string TransactionLogsSql { get; } =
        $"""
         SELECT
             toUnixTimestamp64Milli(Timestamp) AS atMs,
             SpanId AS spanId,
             SeverityText AS severity,
             substringUTF8(Body, 1, {MonitorComponents.MaxLogBodyLength.ToString(CultureInfo.InvariantCulture)}) AS body
         FROM {MonitorTelemetrySchema.LogsTable}
         WHERE TraceId = {"{trace:String}"}
           AND {Window}
         ORDER BY Timestamp
         LIMIT {"{limit:UInt32}"}
         FORMAT JSONEachRow
         """;

    /// <summary>Runs one view and returns its response body.</summary>
    /// <param name="store">The telemetry store.</param>
    /// <param name="view">Which view — one of <see cref="MonitorComponents.Views" />.</param>
    /// <param name="workspace">The workspace, GUID resolved.</param>
    /// <param name="component">The component's address.</param>
    /// <param name="body">The validated request.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The response JSON, or the store's caller-fit failure.</returns>
    public static async Task<Result<string>> RunAsync(
        ITelemetryStore store,
        string view,
        ResourceId workspace,
        ResourceId component,
        System.Text.Json.JsonElement body,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(store);

        var transaction = string.Equals(view, MonitorComponents.TransactionAction, StringComparison.OrdinalIgnoreCase);
        var minutes = MonitorComponents.TimespanMinutes(
            body,
            transaction ? MonitorComponents.MaxTimespanMinutes : MonitorComponents.DefaultTimespanMinutes
        );

        var common = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["minutes"] = minutes.ToString(CultureInfo.InvariantCulture),
            ["namespace"] = MonitorComponents.ServiceNamespace(component)
        };

        if (transaction) {
            return await TransactionAsync(store, workspace, common, MonitorComponents.TraceId(body), cancellationToken);
        }

        common["top"] = MonitorComponents.Top(body).ToString(CultureInfo.InvariantCulture);

        switch (view) {
            case MonitorComponents.RequestsAction: {
                var rows = await store.QueryAsync(new(workspace, RequestsSql, common), cancellationToken);
                return rows.TryGetError(out var error)
                    ? Result<string>.Failure(error)
                    : Result<string>.Success(Requests(minutes, rows.GetValueOrThrow()).ToJsonString());
            }
            case MonitorComponents.DependenciesAction: {
                var rows = await store.QueryAsync(new(workspace, DependenciesSql, common), cancellationToken);
                return rows.TryGetError(out var error)
                    ? Result<string>.Failure(error)
                    : Result<string>.Success(Dependencies(minutes, rows.GetValueOrThrow()).ToJsonString());
            }
            case MonitorComponents.ExceptionsAction: {
                var rows = await store.QueryAsync(new(workspace, ExceptionsSql, common), cancellationToken);
                return rows.TryGetError(out var error)
                    ? Result<string>.Failure(error)
                    : Result<string>.Success(Exceptions(minutes, rows.GetValueOrThrow()).ToJsonString());
            }
            case MonitorComponents.ApplicationMapAction: {
                var nodes = await store.QueryAsync(new(workspace, MapNodesSql, common), cancellationToken);
                if (nodes.TryGetError(out var nodesError)) {
                    return Result<string>.Failure(nodesError);
                }

                var edges = await store.QueryAsync(new(workspace, MapEdgesSql, common), cancellationToken);
                return edges.TryGetError(out var edgesError)
                    ? Result<string>.Failure(edgesError)
                    : Result<string>.Success(
                        Map(minutes, nodes.GetValueOrThrow(), edges.GetValueOrThrow()).ToJsonString()
                    );
            }
            default:
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{view}' is not a view of '{MonitorComponents.Type}'. The views are "
                    + $"[{string.Join(", ", MonitorComponents.Views)}]."
                );
        }
    }

    static async Task<Result<string>> TransactionAsync(
        ITelemetryStore store,
        ResourceId workspace,
        Dictionary<string, string> common,
        string traceId,
        CancellationToken cancellationToken
    ) {
        common["trace"] = traceId;

        // One more than is returned, so a longer trace can say it was cut rather than look complete.
        var spans = await store.QueryAsync(
            new(
                workspace,
                TransactionSpansSql,
                new Dictionary<string, string>(common, StringComparer.Ordinal) {
                    ["limit"] = (MonitorComponents.MaxTransactionSpans + 1).ToString(CultureInfo.InvariantCulture)
                }
            ),
            cancellationToken
        );

        if (spans.TryGetError(out var spansError)) {
            return Result<string>.Failure(spansError);
        }

        var logs = await store.QueryAsync(
            new(
                workspace,
                TransactionLogsSql,
                new Dictionary<string, string>(common, StringComparer.Ordinal) {
                    ["limit"] = (MonitorComponents.MaxTransactionLogs + 1).ToString(CultureInfo.InvariantCulture)
                }
            ),
            cancellationToken
        );

        if (logs.TryGetError(out var logsError)) {
            return Result<string>.Failure(logsError);
        }

        return Result<string>.Success(
            Transaction(traceId, spans.GetValueOrThrow(), logs.GetValueOrThrow()).ToJsonString()
        );
    }

    // ── Shaping ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>requests</c> response over its rows.</summary>
    /// <param name="minutes">The window read.</param>
    /// <param name="rows">The rows <see cref="RequestsSql" /> returned.</param>
    public static JsonObject Requests(int minutes, ImmutableArray<JsonObject> rows) {
        var response = Header(minutes, rows, includeFailed: true);
        response["services"] = Texts(rows, "service");
        response["operations"] = Texts(rows, "operation");
        AddCallColumns(response, minutes, rows, includeRate: true);
        return response;
    }

    /// <summary>The <c>dependencies</c> response over its rows.</summary>
    /// <param name="minutes">The window read.</param>
    /// <param name="rows">The rows <see cref="DependenciesSql" /> returned.</param>
    public static JsonObject Dependencies(int minutes, ImmutableArray<JsonObject> rows) {
        var response = Header(minutes, rows, includeFailed: true);
        response["services"] = Texts(rows, "service");
        response["types"] = Texts(rows, "type");
        response["targets"] = Texts(rows, "target");
        response["names"] = Texts(rows, "name");
        AddCallColumns(response, minutes, rows, includeRate: false);
        return response;
    }

    /// <summary>The <c>exceptions</c> response over its rows.</summary>
    /// <param name="minutes">The window read.</param>
    /// <param name="rows">The rows <see cref="ExceptionsSql" /> returned.</param>
    public static JsonObject Exceptions(int minutes, ImmutableArray<JsonObject> rows) {
        var response = Header(minutes, rows, includeFailed: false);
        response["services"] = Texts(rows, "service");
        response["types"] = Texts(rows, "type");
        response["messages"] = Texts(rows, "message");
        response["counts"] = Wholes(rows, "occurrences");
        response["fromSpans"] = Wholes(rows, "spans");
        response["lastSeen"] = new JsonArray([.. rows.Select(static x => (JsonNode?)Stamp(Long(x, "lastMs")))]);
        return response;
    }

    /// <summary>The <c>applicationMap</c> response over its two row sets.</summary>
    /// <param name="minutes">The window read.</param>
    /// <param name="nodes">The rows <see cref="MapNodesSql" /> returned.</param>
    /// <param name="edges">The rows <see cref="MapEdgesSql" /> returned.</param>
    public static JsonObject Map(int minutes, ImmutableArray<JsonObject> nodes, ImmutableArray<JsonObject> edges) =>
        new() {
            ["timespanMinutes"] = minutes,
            ["nodes"] = Texts(nodes, "service"),
            ["nodeRequests"] = Wholes(nodes, "requests"),
            ["nodeFailures"] = Wholes(nodes, "failures"),
            ["edgeSources"] = Texts(edges, "source"),
            ["edgeTargets"] = Texts(edges, "target"),
            ["edgeCalls"] = Wholes(edges, "calls"),
            ["edgeFailures"] = Wholes(edges, "failures"),
            ["edgeP95Ms"] = new JsonArray([.. edges.Select(static x => (JsonNode?)Milliseconds(Long(x, "p95")))])
        };

    /// <summary>The <c>transaction</c> response over its two row sets.</summary>
    /// <param name="traceId">The trace read.</param>
    /// <param name="spans">The rows <see cref="TransactionSpansSql" /> returned — one more than a response carries when the trace is longer.</param>
    /// <param name="logs">The rows <see cref="TransactionLogsSql" /> returned, likewise.</param>
    public static JsonObject Transaction(string traceId, ImmutableArray<JsonObject> spans, ImmutableArray<JsonObject> logs) {
        var truncated = spans.Length > MonitorComponents.MaxTransactionSpans
            || logs.Length > MonitorComponents.MaxTransactionLogs;

        var keptSpans = spans.Take(MonitorComponents.MaxTransactionSpans).ToImmutableArray();
        var keptLogs = logs.Take(MonitorComponents.MaxTransactionLogs).ToImmutableArray();

        return new() {
            ["traceId"] = traceId,
            ["spanCount"] = keptSpans.Length,
            ["truncated"] = truncated,
            ["spanIds"] = Texts(keptSpans, "spanId"),
            ["parentSpanIds"] = Texts(keptSpans, "parentSpanId"),
            ["services"] = Texts(keptSpans, "service"),
            ["names"] = Texts(keptSpans, "name"),
            ["kinds"] = Texts(keptSpans, "kind"),
            ["starts"] = new JsonArray([.. keptSpans.Select(static x => (JsonNode?)Stamp(Long(x, "startMs")))]),
            ["durationsMs"] = new JsonArray([.. keptSpans.Select(static x => (JsonNode?)Milliseconds(Long(x, "duration")))]),
            ["statuses"] = Texts(keptSpans, "status"),
            ["logCount"] = keptLogs.Length,
            ["logTimes"] = new JsonArray([.. keptLogs.Select(static x => (JsonNode?)Stamp(Long(x, "atMs")))]),
            ["logSpanIds"] = Texts(keptLogs, "spanId"),
            ["logSeverities"] = Texts(keptLogs, "severity"),
            ["logBodies"] = Texts(keptLogs, "body")
        };
    }

    static JsonObject Header(int minutes, ImmutableArray<JsonObject> rows, bool includeFailed) {
        // ⚠ The totals ride on every row as a window over the whole grouping, before the LIMIT, so
        // they count what the top rows left out; an empty result has no row to carry them and is zero.
        var first = rows.IsDefaultOrEmpty ? null : rows[0];

        var header = new JsonObject {
            ["timespanMinutes"] = minutes,
            ["total"] = first is null ? 0 : Long(first, "total")
        };

        if (includeFailed) {
            header["failed"] = first is null ? 0 : Long(first, "totalFailed");
        }

        return header;
    }

    static void AddCallColumns(JsonObject response, int minutes, ImmutableArray<JsonObject> rows, bool includeRate) {
        response["counts"] = Wholes(rows, "calls");
        response["failures"] = Wholes(rows, "failures");

        if (includeRate) {
            response["ratePerMinute"] = new JsonArray(
                [.. rows.Select(x => (JsonNode?)Math.Round((double)Long(x, "calls") / minutes, 4))]
            );
        }

        response["failureRate"] = new JsonArray(
            [
                .. rows.Select(static x => (JsonNode?)(Long(x, "calls") is var calls and > 0
                        ? Math.Round((double)Long(x, "failures") / calls, 4)
                        : 0d
                    )
                )
            ]
        );

        response["p50Ms"] = Quantile(rows, 0);
        response["p95Ms"] = Quantile(rows, 1);
        response["p99Ms"] = Quantile(rows, 2);
    }

    static JsonArray Quantile(ImmutableArray<JsonObject> rows, int index) =>
        new(
            [
                .. rows.Select(x => (JsonNode?)(x["durations"] is JsonArray durations && durations.Count > index
                        ? Milliseconds(durations[index]!.GetValue<long>())
                        : 0d
                    )
                )
            ]
        );

    static JsonArray Texts(ImmutableArray<JsonObject> rows, string column) =>
        new([.. rows.Select(x => (JsonNode?)(x[column]?.GetValue<string>() ?? string.Empty))]);

    static JsonArray Wholes(ImmutableArray<JsonObject> rows, string column) =>
        new([.. rows.Select(x => (JsonNode?)Long(x, column))]);

    static long Long(JsonObject row, string column) => row[column]?.GetValue<long>() ?? 0;

    /// <summary>Nanoseconds to milliseconds, to the microsecond.</summary>
    static double Milliseconds(long nanoseconds) => Math.Round(nanoseconds / 1_000_000d, 3);

    static string Stamp(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
