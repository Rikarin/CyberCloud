using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Query;

/// <summary>
///     <see cref="IMonitorLogStore" /> over ClickHouse's HTTP interface, reading the workspace's
///     <see cref="MonitorLogsTable" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>EVERY VALUE IS A PARAMETER AND THE ONE IDENTIFIER IS CHECKED.</b> The statement is
///         built from fixed text and <c>{name:Type}</c> placeholders, and the values travel as
///         <c>param_{name}</c> in the query string, which ClickHouse binds server-side — so a quote, a
///         backslash or a <c>')) OR 1=1 --</c> in the search text is a string the body is searched
///         for. The database is the one thing spelled into the SQL, and
///         <see cref="MonitorQueries.IsWorkspaceDatabase" /> refuses anything that is not
///         <c>ws_</c> and 32 hex digits before it is. <c>MonitorQueryOverHttpTests</c> sends the
///         injection and asserts it matches the one row whose body carries it.
///     </para>
///     <para>
///         ⚠ <b>Four settings ride on every statement and none is the caller's.</b>
///         <c>readonly=2</c>, so a defect in the builder cannot become a write;
///         <c>max_execution_time</c> and <c>max_rows_to_read</c>, the budget, whose breach is a
///         <c>400</c> naming the budget; and <c>timeout_overflow_mode=throw</c>, so a query that ran out
///         of time fails rather than answering the part it read — a histogram of a partial scan is a
///         chart of a quiet afternoon that was not quiet.
///     </para>
///     <para>
///         ⚠ <b>Time is compared as integers, never as a zoned value.</b> The bounds are Unix
///         nanoseconds (<c>fromUnixTimestamp64Nano</c>) against <c>Timestamp</c> and Unix seconds
///         against <c>TimestampTime</c> — the second for partition pruning, the first for the exact
///         edge — and rows come back as <c>toUnixTimestamp64Nano</c>. A <c>DateTime64</c> with no
///         zone is rendered in the <i>server's</i> zone, and a server that is not in UTC would shift
///         every row and every bucket by its offset; the search tests run ClickHouse in
///         <c>Europe/Prague</c> for that reason, as <c>ProjectionFixture</c> does.
///     </para>
///     <para>
///         <b>This is not <c>CyberCloud.ResourceGraph.ClickHouseClient</c>, and the duplication is a
///         module edge avoided.</b> That client takes the resource graph's options and lives in a module
///         this family has no line to in <c>module-layering.txt</c>; the forty lines it would save are
///         not worth a provider depending on the projection's module forever.
///     </para>
/// </remarks>
public sealed class ClickHouseLogStore : IMonitorLogStore {
    /// <summary>The one sentence a caller reads for a failure that is not theirs to fix.</summary>
    public const string FailedSentence =
        "The workspace's log store could not run the search. The store's reply is in the gateway's log; quote the response's request id when reporting this.";

    /// <summary>What an empty answer says when the table is not there.</summary>
    public const string NoTableNote =
        "No logs have arrived in this workspace yet: its logs table does not exist. It is created when the "
        + "first logs are written — charts/managed/monitor-collector/conformance.yaml § owed, "
        + "collector-clickhouse-tables-are-the-exporters-shape.";

    const int UnknownTable = 60;
    const int UnknownDatabase = 81;
    const int TimeoutExceeded = 159;
    const int TooManyRows = 158;
    const int TooManyRowsOrBytes = 396;

    readonly HttpClient http;
    readonly MonitorQueryOptions options;
    readonly ILogger<ClickHouseLogStore> logger;
    readonly Uri endpoint;

    /// <summary>Creates the store over one long-lived <see cref="HttpClient" /> it does not own.</summary>
    /// <param name="http">The client.</param>
    /// <param name="options">Where ClickHouse is, the credential and the budget.</param>
    /// <param name="logger">Where the store's own words go.</param>
    public ClickHouseLogStore(HttpClient http, MonitorQueryOptions options, ILogger<ClickHouseLogStore> logger) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        this.http = http;
        this.options = options;
        this.logger = logger;
        endpoint = options.LogsEndpointUri();
    }

    /// <inheritdoc />
    public async Task<Result<LogAnswer>> SearchAsync(
        string database,
        LogSearch search,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(search);

        if (!MonitorQueries.IsWorkspaceDatabase(database)) {
            return Result<LogAnswer>.Failure(ErrorCode.InternalError, $"'{database}' is not a workspace database, so nothing was run.");
        }

        var (where, parameters) = Filter(search);
        var table = $"`{database}`.`{MonitorLogsTable.Name}`";

        // ── The newest rows, one more than asked for so a longer result is known to be longer ────
        var rows = await ExecuteAsync(
            $"""
             SELECT toUnixTimestamp64Nano(Timestamp) AS t, SeverityNumber AS n, SeverityText AS s,
                    ServiceName AS svc, Body AS body, TraceId AS trace, SpanId AS span,
                    LogAttributes AS attributes, ResourceAttributes AS resource
             FROM {table}
             WHERE {where}
             ORDER BY Timestamp DESC
             LIMIT {search.Top + 1}
             FORMAT JSON
             """,
            parameters,
            database,
            cancellationToken
        );

        if (rows.TryGetError(out var rowsError)) {
            return Empty(rowsError, out var empty) ? Result<LogAnswer>.Success(empty) : Result<LogAnswer>.Failure(rowsError);
        }

        // ── The histogram, over the same filter, bucketed from `from` rather than from the epoch ──
        //
        // ⚠ Bucket INDEXES relative to `from`, computed on integers. toStartOfInterval aligns to the
        // server's zone for some widths, and a bucket that starts somewhere other than where the
        // chart's axis says it does is a histogram that lies about when things happened.
        var histogramParameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal) {
            ["bucket"] = search.BucketSeconds.ToString(CultureInfo.InvariantCulture)
        };

        var histogram = await ExecuteAsync(
            $"""
             SELECT intDiv(toUnixTimestamp(TimestampTime) - {"{fromS:Int64}"}, {"{bucket:Int64}"}) AS b,
                    if(SeverityNumber BETWEEN 1 AND 24, intDiv(SeverityNumber - 1, 4), -1) AS band,
                    count() AS c
             FROM {table}
             WHERE {where}
             GROUP BY b, band
             ORDER BY b, band
             FORMAT JSON
             """,
            histogramParameters,
            database,
            cancellationToken
        );

        if (histogram.TryGetError(out var histogramError)) {
            return Empty(histogramError, out var empty) ? Result<LogAnswer>.Success(empty) : Result<LogAnswer>.Failure(histogramError);
        }

        try {
            var (records, truncated) = ParseRows(rows.GetValueOrThrow().Body, search.Top);
            var buckets = ParseBuckets(histogram.GetValueOrThrow().Body);
            var read = rows.GetValueOrThrow().Statistics;
            var read2 = histogram.GetValueOrThrow().Statistics;

            return Result<LogAnswer>.Success(
                new(records, truncated, buckets, new(read.RowsRead + read2.RowsRead, read.BytesRead + read2.BytesRead), "")
            );
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            return Failed<LogAnswer>(database, "the store answered 200 with a body that is not the JSON format asked for", exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<LogEstimate>> EstimateAsync(
        string database,
        LogSearch search,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(search);

        if (!MonitorQueries.IsWorkspaceDatabase(database)) {
            return Result<LogEstimate>.Failure(ErrorCode.InternalError, $"'{database}' is not a workspace database, so nothing was run.");
        }

        var (where, parameters) = Filter(search);

        // ⚠ EXPLAIN ESTIMATE reads the primary index and the partition list and nothing else: it is
        // what docs/plan/20's "query cost preview" costs, which is a few index marks rather than the
        // scan it is warning about. The rows it names are the rows in the parts and granules the
        // WHERE could not prune — an upper bound on what the search reads, not a count of matches.
        var estimated = await ExecuteAsync(
            $"""
             EXPLAIN ESTIMATE
             SELECT count() FROM `{database}`.`{MonitorLogsTable.Name}`
             WHERE {where}
             FORMAT JSON
             """,
            parameters,
            database,
            cancellationToken
        );

        if (estimated.TryGetError(out var error)) {
            return ClickHouseCode(error) is UnknownTable or UnknownDatabase
                ? Result<LogEstimate>.Success(default)
                : Result<LogEstimate>.Failure(error);
        }

        try {
            using var document = JsonDocument.Parse(estimated.GetValueOrThrow().Body);
            long rows = 0, parts = 0, marks = 0;

            foreach (var row in document.RootElement.GetProperty("data").EnumerateArray()) {
                rows += Long(row, "rows");
                parts += Long(row, "parts");
                marks += Long(row, "marks");
            }

            return Result<LogEstimate>.Success(new(rows, parts, marks));
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            return Failed<LogEstimate>(database, "the store's estimate is not the JSON format asked for", exception.Message);
        }
    }

    /// <summary>
    ///     The <c>WHERE</c> clause for a search and the parameters it binds. Nothing in the text comes
    ///     from the search but the placeholders' names, which are fixed or numbered.
    /// </summary>
    static (string Where, Dictionary<string, string> Parameters) Filter(LogSearch search) {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["fromNs"] = Nanos(search.From),
            ["toNs"] = Nanos(search.To),
            ["fromS"] = search.From.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            // The partition bound is inclusive of the last second, because TimestampTime truncates
            // and the exact edge is Timestamp's to decide.
            ["toS"] = (search.To.ToUnixTimeSeconds() + 1).ToString(CultureInfo.InvariantCulture)
        };

        var clauses = new List<string> {
            "TimestampTime >= toDateTime({fromS:Int64})",
            "TimestampTime <= toDateTime({toS:Int64})",
            "Timestamp >= fromUnixTimestamp64Nano({fromNs:Int64})",
            "Timestamp < fromUnixTimestamp64Nano({toNs:Int64})"
        };

        if (search.Severities.Length > 0) {
            parameters["bands"] = "[" + string.Join(
                ",",
                search.Severities.Select(static x => MonitorQueries.SeverityBand[x].ToString(CultureInfo.InvariantCulture))
            ) + "]";
            clauses.Add("SeverityNumber BETWEEN 1 AND 24 AND has({bands:Array(UInt8)}, intDiv(SeverityNumber - 1, 4))");
        }

        if (search.Service.Length > 0) {
            parameters["service"] = search.Service;
            clauses.Add("ServiceName = {service:String}");
        }

        if (search.Text.Length > 0) {
            parameters["text"] = search.Text;
            clauses.Add("positionCaseInsensitiveUTF8(Body, {text:String}) > 0");
        }

        if (search.TraceId.Length > 0) {
            parameters["trace"] = search.TraceId;
            clauses.Add("lower(TraceId) = {trace:String}");
        }

        for (var i = 0; i < search.Attributes.Length; i++) {
            var (key, value) = search.Attributes[i];
            var k = "ak" + i.ToString(CultureInfo.InvariantCulture);
            var v = "av" + i.ToString(CultureInfo.InvariantCulture);
            parameters[k] = key;
            parameters[v] = value;
            clauses.Add($"(LogAttributes[{{{k}:String}}] = {{{v}:String}} OR ResourceAttributes[{{{k}:String}}] = {{{v}:String}})");
        }

        return (string.Join("\n  AND ", clauses), parameters);
    }

    /// <summary>A <c>200</c>'s body and what ClickHouse's summary header says it read.</summary>
    readonly record struct Executed(string Body, LogStatistics Statistics);

    async Task<Result<Executed>> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, string> parameters,
        string database,
        CancellationToken cancellationToken
    ) {
        var query = new StringBuilder("?readonly=2&timeout_overflow_mode=throw&output_format_json_quote_64bit_integers=0");
        query.Append("&max_execution_time=").Append(Seconds(options.QueryTimeout));
        query.Append("&max_rows_to_read=").Append(options.LogsMaxRowsToRead.ToString(CultureInfo.InvariantCulture));

        foreach (var (name, value) in parameters) {
            query.Append("&param_").Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.QueryTimeout + TimeSpan.FromSeconds(2));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/" + query));
        request.Content = new StringContent(sql, Encoding.UTF8, "text/plain");
        request.Headers.Add("X-ClickHouse-User", options.LogsUser);
        request.Headers.Add("X-ClickHouse-Key", options.LogsPassword);

        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            var body = await BoundedBody.ReadAsync(response.Content, options.MaxResponseBytes, budget.Token);

            if (body is null) {
                return Result<Executed>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "The search matched more data than one response may carry. Ask for fewer rows with top, or narrow the window."
                );
            }

            if (!response.IsSuccessStatusCode) {
                var code = response.Headers.TryGetValues("X-ClickHouse-Exception-Code", out var values)
                    && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0;

                if (code is TimeoutExceeded or TooManyRows or TooManyRowsOrBytes) {
                    return BudgetExceeded();
                }

                if (code is UnknownTable or UnknownDatabase) {
                    // Carried as the code alone; the caller turns it into an empty answer.
                    return Result<Executed>.Failure(ErrorCode.ResourceNotFound, CodeMarker + code.ToString(CultureInfo.InvariantCulture));
                }

                return Failed<Executed>(database, $"ClickHouse answered {(int)response.StatusCode} (code {code})", body.Trim());
            }

            return Result<Executed>.Success(new(body, Summary(response)));
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return BudgetExceeded();
        } catch (HttpRequestException exception) {
            return Failed<Executed>(database, "ClickHouse could not be reached", exception.Message);
        }
    }

    const string CodeMarker = "clickhouse-code:";

    static int ClickHouseCode(Error error) =>
        error.Message.StartsWith(CodeMarker, StringComparison.Ordinal)
        && int.TryParse(error.Message[CodeMarker.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code
            : 0;

    /// <summary>Whether a failure is the table's absence, with the empty answer that stands for it.</summary>
    static bool Empty(Error error, out LogAnswer empty) {
        empty = new([], false, [], default, NoTableNote);

        return ClickHouseCode(error) is UnknownTable or UnknownDatabase;
    }

    Result<Executed> BudgetExceeded() =>
        Result<Executed>.Failure(
            ErrorCode.InvalidRequestBody,
            $"The search ran past the log store's budget of {Seconds(options.QueryTimeout)} s or "
            + $"{options.LogsMaxRowsToRead.ToString("N0", CultureInfo.InvariantCulture)} rows read. Narrow the "
            + "window, or add a service, a severity or an attribute filter."
        );

    Result<T> Failed<T>(string database, string what, string detail) where T : notnull {
        logger.LogError("Log search in {Database} failed: {What}. Detail: {Detail}", database, what, detail);

        return Result<T>.Failure(ErrorCode.InternalError, FailedSentence);
    }

    /// <summary>The rows out of ClickHouse's <c>JSON</c> format, at most <paramref name="top" /> of them.</summary>
    static (ImmutableArray<LogRecord> Rows, bool Truncated) ParseRows(string body, int top) {
        using var document = JsonDocument.Parse(body);
        var rows = ImmutableArray.CreateBuilder<LogRecord>();
        var seen = 0;

        foreach (var row in document.RootElement.GetProperty("data").EnumerateArray()) {
            seen++;

            if (rows.Count >= top) {
                continue;
            }

            rows.Add(
                new(
                    Long(row, "t"),
                    (int)Long(row, "n"),
                    row.GetProperty("s").GetString() ?? "",
                    row.GetProperty("svc").GetString() ?? "",
                    row.GetProperty("body").GetString() ?? "",
                    row.GetProperty("trace").GetString() ?? "",
                    row.GetProperty("span").GetString() ?? "",
                    Map(row.GetProperty("attributes")),
                    Map(row.GetProperty("resource"))
                )
            );
        }

        return (rows.ToImmutable(), seen > top);
    }

    static ImmutableArray<LogBucket> ParseBuckets(string body) {
        using var document = JsonDocument.Parse(body);
        var buckets = new SortedDictionary<long, SortedDictionary<string, long>>();

        foreach (var row in document.RootElement.GetProperty("data").EnumerateArray()) {
            var index = Long(row, "b");
            var band = Long(row, "band");
            var name = band is >= 0 and < 6 ? MonitorQueries.Severities[(int)band] : "unspecified";

            if (!buckets.TryGetValue(index, out var counts)) {
                buckets[index] = counts = new(StringComparer.Ordinal);
            }

            counts[name] = counts.GetValueOrDefault(name) + Long(row, "c");
        }

        return [.. buckets.Select(static x => new LogBucket(x.Key, x.Value.ToImmutableSortedDictionary(StringComparer.Ordinal)))];
    }

    static ImmutableSortedDictionary<string, string> Map(JsonElement map) =>
        map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().ToImmutableSortedDictionary(static x => x.Name, static x => x.Value.GetString() ?? "", StringComparer.Ordinal)
            : ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>A number ClickHouse wrote as a number or, for a 64-bit one the setting missed, as a string.</summary>
    static long Long(JsonElement row, string name) {
        var value = row.GetProperty(name);

        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : value.GetInt64();
    }

    /// <summary>What <c>X-ClickHouse-Summary</c> says a statement read, or zeros.</summary>
    static LogStatistics Summary(HttpResponseMessage response) {
        if (!response.Headers.TryGetValues("X-ClickHouse-Summary", out var values)) {
            return default;
        }

        try {
            using var summary = JsonDocument.Parse(values.First());
            return new(Long(summary.RootElement, "read_rows"), Long(summary.RootElement, "read_bytes"));
        } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException) {
            return default;
        }
    }

    static string Nanos(DateTimeOffset instant) =>
        ((instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100).ToString(CultureInfo.InvariantCulture);

    static string Seconds(TimeSpan span) => Math.Max(1, (long)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
