using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Telemetry;

/// <summary>
///     <see cref="ITelemetryStore" /> over ClickHouse's HTTP interface: the statement is the
///     <c>POST</c> body, the workspace's database and the budget are query-string settings, and every
///     value is a <c>param_</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The database is a setting, not text in the statement.</b> ClickHouse's HTTP interface
///         takes <c>database=</c> as the session's default, so the views' statements name bare tables
///         and the one identifier that decides whose telemetry is read never enters the SQL. It is
///         still checked against <see cref="MonitorTelemetrySchema.IsWorkspaceDatabase" /> before the
///         request leaves, because it came from a GUID and must look like one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Five settings ride on every request and none is the caller's:
///         </b> <c>readonly=2</c>, so a statement that is not a read is refused by the server;
///         <c>max_execution_time</c>, <c>max_rows_to_read</c> and <c>max_memory_usage</c>, the
///         budget; and <c>output_format_json_quote_64bit_integers=0</c>, so a <c>UInt64</c> count
///         comes back as a JSON number rather than the string ClickHouse quotes it as by default.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What the store says goes to the log, and the caller gets one of three sentences
///         </b> — the finding #54's review made about the resource graph, applied before it could be
///         made again. ClickHouse answers a refused statement with its exception text, which quotes
///         the statement, the database and the endpoint. A budget refusal is an
///         <see cref="ErrorCode.InvalidRequestBody" /> naming the budget and how to narrow the view; a
///         database or table that does not exist is a <see cref="ErrorCode.Conflict" /> saying the
///         workspace's store is not provisioned, which is the state every workspace is in until the
///         schema step exists; anything else is an <see cref="ErrorCode.InternalError" /> saying only
///         that the store did not answer.
///     </para>
/// </remarks>
public sealed class ClickHouseTelemetryStore : ITelemetryStore {
    /// <summary>The response header ClickHouse puts its exception's number in.</summary>
    public const string ExceptionCodeHeader = "X-ClickHouse-Exception-Code";

    /// <summary>
    ///     The exception numbers that mean "the statement ran past its budget":
    ///     <c>TOO_MANY_ROWS</c>, <c>TIMEOUT_EXCEEDED</c>, <c>MEMORY_LIMIT_EXCEEDED</c> and
    ///     <c>TOO_MANY_ROWS_OR_BYTES</c>.
    /// </summary>
    public static ImmutableHashSet<int> BudgetCodes { get; } = [158, 159, 241, 396];

    /// <summary>The exception numbers that mean "nothing has created this": <c>UNKNOWN_TABLE</c> and <c>UNKNOWN_DATABASE</c>.</summary>
    public static ImmutableHashSet<int> UnprovisionedCodes { get; } = [60, 81];

    readonly HttpClient http;
    readonly Uri endpoint;
    readonly MonitorTelemetryOptions options;
    readonly ILogger logger;

    /// <summary>Creates a store over one long-lived <see cref="HttpClient" /> it does not own.</summary>
    /// <param name="http">The client. One per process.</param>
    /// <param name="options">The endpoint, the credential and the budget.</param>
    /// <param name="logger">Where the store's own words go. Never to the caller.</param>
    /// <exception cref="ArgumentException">
    ///     The endpoint is not an absolute http(s) URI, or is plain http without
    ///     <see cref="MonitorTelemetryOptions.AllowInsecureTransport" />.
    /// </exception>
    public ClickHouseTelemetryStore(
        HttpClient http,
        MonitorTelemetryOptions options,
        ILogger<ClickHouseTelemetryStore>? logger = null
    ) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        this.http = http;
        this.options = options;
        this.logger = logger ?? NullLogger<ClickHouseTelemetryStore>.Instance;
        endpoint = ValidatedEndpoint(options);
    }

    /// <summary>The endpoint as an absolute URI, refusing plain HTTP unless the section opted in.</summary>
    /// <param name="options">The bound section.</param>
    /// <exception cref="ArgumentException">See the constructor.</exception>
    public static Uri ValidatedEndpoint(MonitorTelemetryOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.ClickHouseEndpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            throw new ArgumentException(
                $"{MonitorTelemetryOptions.SectionName}:ClickHouseEndpoint '{options.ClickHouseEndpoint}' is not "
                + "an absolute http(s) URI. ClickHouse's HTTP interface is https://host:8443.",
                nameof(options)
            );
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !options.AllowInsecureTransport) {
            throw new ArgumentException(
                $"{MonitorTelemetryOptions.SectionName}:ClickHouseEndpoint '{options.ClickHouseEndpoint}' is plain "
                + "HTTP and the platform's read credential would travel in clear. Set AllowInsecureTransport "
                + "for a container on a laptop; a region's endpoint has TLS.",
                nameof(options)
            );
        }

        return uri;
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<JsonObject>>> QueryAsync(
        TelemetryQuery query,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(query);

        // ⚠ Refused here as well as by the caller: a workspace with no GUID derives `ws_000…`, which
        // is a real name nothing owns — and the one database every unresolved caller would share.
        if (query.Workspace.Type != MonitorWorkspaces.Type || query.Workspace.Id == Guid.Empty) {
            return Result<ImmutableArray<JsonObject>>.Failure(
                ErrorCode.InternalError,
                $"'{query.Workspace.Path}' is not a workspace with a resolved GUID, so there is no database "
                + "to read. The view path resolves the workspace through the platform's index before it asks."
            );
        }

        var database = MonitorWorkspaces.Database(query.Workspace);
        MonitorTelemetrySchema.EnsureDatabase(database);

        var url = new StringBuilder("?database=").Append(database)
            .Append("&readonly=2")
            .Append("&output_format_json_quote_64bit_integers=0")
            .Append("&max_execution_time=")
            .Append(((int)Math.Ceiling(options.QueryTimeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture))
            .Append("&max_rows_to_read=")
            .Append(options.MaxRowsToRead.ToString(CultureInfo.InvariantCulture))
            .Append("&max_memory_usage=")
            .Append(options.MaxMemoryBytes.ToString(CultureInfo.InvariantCulture));

        foreach (var (name, value) in query.Parameters) {
            url.Append("&param_").Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, url.ToString()));
        request.Headers.Add("X-ClickHouse-User", options.ClickHouseUser);
        request.Headers.Add("X-ClickHouse-Key", options.ClickHousePassword);
        request.Content = new StringContent(query.Sql, Encoding.UTF8);
        request.Content.Headers.ContentType = new("text/plain") { CharSet = "utf-8" };

        string body;
        try {
            using var response = await http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode) {
                var code = response.Headers.TryGetValues(ExceptionCodeHeader, out var values)
                    && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0;

                logger.LogError(
                    "The telemetry store refused a view statement on {Database}: {Status} ({Code}) {Body}",
                    database,
                    (int)response.StatusCode,
                    code,
                    body.Trim()
                );

                return Refused(code);
            }
        } catch (HttpRequestException exception) {
            logger.LogError(exception, "The telemetry store at {Endpoint} could not be reached", endpoint);
            return Unanswered();
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            logger.LogError("The telemetry store at {Endpoint} did not answer within {Timeout}", endpoint, http.Timeout);
            return Unanswered();
        }

        try {
            return Result<ImmutableArray<JsonObject>>.Success(
                [
                    .. body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static line => JsonNode.Parse(line) as JsonObject
                            ?? throw new JsonException("a JSONEachRow line that is not an object")
                        )
                ]
            );
        } catch (JsonException exception) {
            logger.LogError(exception, "The telemetry store answered a view statement on {Database} with rows that are not JSON", database);
            return Unanswered();
        }
    }

    static Result<ImmutableArray<JsonObject>> Refused(int code) =>
        BudgetCodes.Contains(code)
            ? Result<ImmutableArray<JsonObject>>.Failure(
                ErrorCode.InvalidRequestBody,
                "The view read more of the workspace's telemetry than one request may — its time, row "
                + "or memory budget. Ask for a shorter timespanMinutes."
            )
            : UnprovisionedCodes.Contains(code)
                ? Result<ImmutableArray<JsonObject>>.Failure(
                    ErrorCode.Conflict,
                    "The workspace's traces and logs tables do not exist in the telemetry store yet, so "
                    + "there is nothing to read. Nothing in the platform creates them today — "
                    + "charts/managed/monitor-collector/conformance.yaml § owed, "
                    + "collector-clickhouse-tables-are-the-exporters-shape."
                )
                : Unanswered();

    static Result<ImmutableArray<JsonObject>> Unanswered() =>
        Result<ImmutableArray<JsonObject>>.Failure(
            ErrorCode.InternalError,
            "The telemetry store did not answer the view. The platform's log has the store's own words."
        );
}
