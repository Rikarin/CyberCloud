using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Query;

/// <summary>
///     Serves <c>POST …/workspaces/{name}/queryMetrics</c>: one PromQL or MetricsQL expression under
///     the workspace's own <c>accountID</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The tenancy is read off <see cref="ActionContext.Id" /> and nothing else.</b> By the time
///         this runs, the manager has rebuilt the address with the token's tenant, resolved it to the
///         workspace's GUID and checked <c>read</c> on it; <see cref="MonitorWorkspaces.AccountId" />
///         over that GUID is the account, and the body has no member that could name another.
///     </para>
///     <para>
///         ⚠ <b>And the account is asked for only if this workspace holds it.</b> The account is a
///         32-bit fold of the GUID, so another tenant's workspace can fold onto it. The reconciler
///         claims the account at create and refuses a collision, and <see cref="IMonitorAccounts" />
///         answers whether this workspace is the one that claimed it; a workspace that isn't gets a
///         <c>409</c> and the store is never asked. Without the check, creating workspaces until one
///         collided was a cross-tenant read — #41's review.
///     </para>
///     <para>
///         The response — undeclared, for the reason <see cref="MonitorQueries" /> gives — is
///         <c>{ resultType, series: [{ labels: {…}, points: [[seconds, value|null], …] }], seriesTotal,
///         truncated }</c> plus <c>start</c>, <c>end</c> and <c>stepSeconds</c> for a range query or
///         <c>time</c> for an instant one.
///     </para>
/// </remarks>
/// <param name="store">The metrics store.</param>
/// <param name="accounts">The ledger that says whether this workspace holds its account.</param>
/// <param name="clock">What "now" is for an instant query that names no time.</param>
public sealed class MonitorWorkspaceQueryMetricsHandler(IMonitorMetricsStore store, IMonitorAccounts accounts, IClock clock)
    : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorWorkspaces.Type;

    /// <inheritdoc />
    public string Action => MonitorQueries.QueryMetricsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var query = ReadQuery(context.Body, clock.UtcNow);

        if (query.TryGetError(out var refusal)) {
            return Result<string>.Failure(refusal);
        }

        var parsed = query.GetValueOrThrow();
        var tenancy = await QueryBodies.TenancyAsync(context, accounts, cancellationToken);

        if (tenancy.TryGetError(out var unheld)) {
            return Result<string>.Failure(unheld);
        }

        var answered = await store.QueryAsync(tenancy.GetValueOrThrow(), parsed, cancellationToken);

        if (answered.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        return Result<string>.Success(Render(parsed, answered.GetValueOrThrow()));
    }

    /// <summary>
    ///     Reads and bounds a <c>queryMetrics</c> body — the rules the flat schema cannot state.
    /// </summary>
    /// <remarks>
    ///     ⚠ The body only. The expression's own look-behind isn't read here, and
    ///     <see cref="MonitorQueries.MaxMetricsRange" />'s remarks say why and what bounds it instead.
    /// </remarks>
    /// <param name="body">The body, already validated against <see cref="MonitorQueries.QueryMetricsRequest" />.</param>
    /// <param name="now">The instant an instant query with no <c>time</c> is evaluated at.</param>
    public static Result<MetricsQuery> ReadQuery(JsonElement body, DateTimeOffset now) {
        var expression = QueryBodies.Text(body, "query").Trim();

        if (expression.Length == 0) {
            return Result<MetricsQuery>.Failure(ErrorCode.InvalidRequestBody, "'/query' is empty. Name a metric, for example up.");
        }

        var start = QueryBodies.Instant(body, "start");
        var end = QueryBodies.Instant(body, "end");

        if (start is null != end is null) {
            return Result<MetricsQuery>.Failure(
                ErrorCode.InvalidRequestBody,
                "A range query names both '/start' and '/end', and an instant query names neither. This body names one."
            );
        }

        if (start is null) {
            return Result<MetricsQuery>.Success(new(expression, null, null, QueryBodies.Instant(body, "time") ?? now, 0));
        }

        var window = end!.Value - start.Value;

        if (window <= TimeSpan.Zero) {
            return Result<MetricsQuery>.Failure(ErrorCode.InvalidRequestBody, "'/end' is not after '/start'.");
        }

        if (window > MonitorQueries.MaxMetricsRange) {
            return Result<MetricsQuery>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The window is {window.TotalDays:0.#} days and a metrics query may cover at most "
                + $"{MonitorQueries.MaxMetricsRange.TotalDays:0} — the longest retention a workspace can have."
            );
        }

        var step = QueryBodies.Whole(body, "stepSeconds") ?? MonitorQueries.DefaultStepSeconds(window);
        var points = Math.Ceiling(window.TotalSeconds / step);

        if (points > MonitorQueries.MaxPoints) {
            var smallest = (long)Math.Ceiling(window.TotalSeconds / MonitorQueries.MaxPoints);

            return Result<MetricsQuery>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A step of {step} s over this window is {points:0} points per series, and the most is "
                + $"{MonitorQueries.MaxPoints}. Use a step of at least {smallest} s."
            );
        }

        return Result<MetricsQuery>.Success(new(expression, start, end, end.Value, step));
    }

    /// <summary>The response body for an answer. See the remarks on this type for its shape.</summary>
    /// <param name="query">What was asked.</param>
    /// <param name="answer">What the store said.</param>
    public static string Render(MetricsQuery query, MetricsAnswer answer) {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(answer);

        var series = new JsonArray();

        foreach (var item in answer.Series) {
            var labels = new JsonObject();
            foreach (var (name, value) in item.Labels) {
                labels[name] = value;
            }

            var points = new JsonArray();
            foreach (var (seconds, value) in item.Points) {
                points.Add(new JsonArray(seconds, value));
            }

            series.Add(new JsonObject { ["labels"] = labels, ["points"] = points });
        }

        var body = new JsonObject {
            ["resultType"] = answer.ResultType,
            ["seriesTotal"] = answer.SeriesTotal,
            ["truncated"] = answer.Truncated,
            ["series"] = series
        };

        if (query.IsRange) {
            body["start"] = MonitorQueries.Stamp(query.Start!.Value);
            body["end"] = MonitorQueries.Stamp(query.End!.Value);
            body["stepSeconds"] = query.StepSeconds;
        } else {
            body["time"] = MonitorQueries.Stamp(query.Time);
        }

        return body.ToJsonString();
    }
}

/// <summary>
///     Serves <c>POST …/workspaces/{name}/listMetricLabels</c> — the metric picker and the label
///     filters of the explorer.
/// </summary>
/// <remarks>
///     With <c>label</c> it lists that label's values — <c>__name__</c> is the metric names — and
///     without it the label names, both narrowed by <c>match</c> and a window that defaults to the
///     last day. Declared response: a list of strings is something the schema can say.
/// </remarks>
/// <param name="store">The metrics store.</param>
/// <param name="accounts">The ledger that says whether this workspace holds its account.</param>
/// <param name="clock">The default window's end.</param>
public sealed class MonitorWorkspaceListMetricLabelsHandler(IMonitorMetricsStore store, IMonitorAccounts accounts, IClock clock)
    : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorWorkspaces.Type;

    /// <inheritdoc />
    public string Action => MonitorQueries.ListMetricLabelsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var end = QueryBodies.Instant(context.Body, "end") ?? clock.UtcNow;
        var start = QueryBodies.Instant(context.Body, "start") ?? end - TimeSpan.FromDays(1);

        if (end <= start) {
            return Result<string>.Failure(ErrorCode.InvalidRequestBody, "'/end' is not after '/start'.");
        }

        if (end - start > MonitorQueries.MaxMetricsRange) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A label listing may cover at most {MonitorQueries.MaxMetricsRange.TotalDays:0} days."
            );
        }

        var tenancy = await QueryBodies.TenancyAsync(context, accounts, cancellationToken);

        if (tenancy.TryGetError(out var unheld)) {
            return Result<string>.Failure(unheld);
        }

        var answered = await store.LabelsAsync(
            tenancy.GetValueOrThrow(),
            new(QueryBodies.Text(context.Body, "label"), QueryBodies.Text(context.Body, "match").Trim(), start, end),
            cancellationToken
        );

        if (answered.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var answer = answered.GetValueOrThrow();

        return Result<string>.Success(
            new JsonObject {
                ["values"] = new JsonArray([.. answer.Values.Select(static x => (JsonNode?)x)]),
                ["truncated"] = answer.Truncated
            }.ToJsonString()
        );
    }
}

/// <summary>
///     Serves <c>POST …/workspaces/{name}/searchLogs</c>: the log search page's rows, histogram and
///     cost preview.
/// </summary>
/// <remarks>
///     The response — undeclared, for the reason <see cref="MonitorQueries" /> gives — is
///     <c>{ from, to, rows: [{ timestamp, severity, severityText, service, body, traceId, spanId,
///     attributes, resource }], truncated, histogram: { bucketSeconds, buckets: [{ start, total,
///     bySeverity }] }, statistics: { rowsRead, bytesRead }, note }</c>; with <c>estimate</c> it is
///     <c>{ from, to, estimate: { rows, parts, marks } }</c> and nothing is searched.
/// </remarks>
/// <param name="store">The log store.</param>
public sealed class MonitorWorkspaceSearchLogsHandler(IMonitorLogStore store) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorWorkspaces.Type;

    /// <inheritdoc />
    public string Action => MonitorQueries.SearchLogsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var search = ReadSearch(context.Body);

        if (search.TryGetError(out var refusal)) {
            return Result<string>.Failure(refusal);
        }

        var parsed = search.GetValueOrThrow();
        var database = MonitorWorkspaces.Database(context.Id);

        if (context.Body.TryGetProperty("estimate", out var estimate) && estimate.ValueKind == JsonValueKind.True) {
            var estimated = await store.EstimateAsync(database, parsed, cancellationToken);

            if (estimated.TryGetError(out var estimateError)) {
                return Result<string>.Failure(estimateError);
            }

            var value = estimated.GetValueOrThrow();

            return Result<string>.Success(
                new JsonObject {
                    ["from"] = MonitorQueries.Stamp(parsed.From),
                    ["to"] = MonitorQueries.Stamp(parsed.To),
                    ["estimate"] = new JsonObject { ["rows"] = value.Rows, ["parts"] = value.Parts, ["marks"] = value.Marks }
                }.ToJsonString()
            );
        }

        var answered = await store.SearchAsync(database, parsed, cancellationToken);

        if (answered.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        return Result<string>.Success(Render(parsed, answered.GetValueOrThrow()));
    }

    /// <summary>Reads and bounds a <c>searchLogs</c> body — the rules the flat schema cannot state.</summary>
    /// <param name="body">The body, already validated against <see cref="MonitorQueries.SearchLogsRequest" />.</param>
    public static Result<LogSearch> ReadSearch(JsonElement body) {
        if (QueryBodies.Instant(body, "from") is not { } from || QueryBodies.Instant(body, "to") is not { } to) {
            return Result<LogSearch>.Failure(ErrorCode.InvalidRequestBody, "A search names '/from' and '/to'.");
        }

        var window = to - from;

        if (window <= TimeSpan.Zero) {
            return Result<LogSearch>.Failure(ErrorCode.InvalidRequestBody, "'/to' is not after '/from'.");
        }

        if (window > MonitorQueries.MaxLogsRange) {
            return Result<LogSearch>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The window is {window.TotalDays:0.#} days and a log search may cover at most "
                + $"{MonitorQueries.MaxLogsRange.TotalDays:0} — the longest log retention a workspace can have."
            );
        }

        var attributes = ImmutableArray.CreateBuilder<(string, string)>();

        foreach (var filter in QueryBodies.Texts(body, "attributes")) {
            attributes.Add(MonitorQueries.SplitAttribute(filter));
        }

        if (attributes.Count > MonitorQueries.MaxAttributeFilters) {
            return Result<LogSearch>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The search carries {attributes.Count} attribute filters and the most is {MonitorQueries.MaxAttributeFilters}."
            );
        }

        var bucket = QueryBodies.Whole(body, "bucketSeconds") ?? MonitorQueries.DefaultBucketSeconds(window);
        var buckets = Math.Ceiling(window.TotalSeconds / bucket);

        if (buckets > MonitorQueries.MaxHistogramBuckets) {
            return Result<LogSearch>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A bucket of {bucket} s over this window is {buckets:0} buckets and the most is "
                + $"{MonitorQueries.MaxHistogramBuckets}. Use at least "
                + $"{(long)Math.Ceiling(window.TotalSeconds / MonitorQueries.MaxHistogramBuckets)} s."
            );
        }

        return Result<LogSearch>.Success(
            new(
                from,
                to,
                QueryBodies.Text(body, "text"),
                [.. QueryBodies.Texts(body, "severities").Distinct(StringComparer.Ordinal)],
                QueryBodies.Text(body, "service"),
                attributes.ToImmutable(),
                QueryBodies.Text(body, "traceId").ToLowerInvariant(),
                QueryBodies.Whole(body, "top") ?? MonitorQueries.DefaultLogRows,
                bucket
            )
        );
    }

    /// <summary>The response body for an answer. See the remarks on this type for its shape.</summary>
    /// <param name="search">What was asked.</param>
    /// <param name="answer">What the store said.</param>
    public static string Render(LogSearch search, LogAnswer answer) {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(answer);

        var rows = new JsonArray();

        foreach (var row in answer.Rows) {
            rows.Add(
                new JsonObject {
                    ["timestamp"] = NanoStamp(row.UnixNanos),
                    ["severity"] = MonitorQueries.SeverityOf(row.SeverityNumber),
                    ["severityText"] = row.SeverityText,
                    ["service"] = row.Service,
                    ["body"] = row.Body,
                    ["traceId"] = row.TraceId,
                    ["spanId"] = row.SpanId,
                    ["attributes"] = Object(row.Attributes),
                    ["resource"] = Object(row.Resource)
                }
            );
        }

        var buckets = new JsonArray();

        foreach (var bucket in answer.Buckets) {
            var counts = new JsonObject();
            foreach (var (severity, count) in bucket.BySeverity) {
                counts[severity] = count;
            }

            buckets.Add(
                new JsonObject {
                    ["start"] = MonitorQueries.Stamp(search.From.AddSeconds(bucket.Index * (double)search.BucketSeconds)),
                    ["total"] = bucket.BySeverity.Values.Sum(),
                    ["bySeverity"] = counts
                }
            );
        }

        return new JsonObject {
            ["from"] = MonitorQueries.Stamp(search.From),
            ["to"] = MonitorQueries.Stamp(search.To),
            ["rows"] = rows,
            ["truncated"] = answer.Truncated,
            ["histogram"] = new JsonObject { ["bucketSeconds"] = search.BucketSeconds, ["buckets"] = buckets },
            ["statistics"] = new JsonObject { ["rowsRead"] = answer.Statistics.RowsRead, ["bytesRead"] = answer.Statistics.BytesRead },
            ["note"] = answer.Note
        }.ToJsonString();
    }

    static JsonObject Object(ImmutableSortedDictionary<string, string> map) {
        var built = new JsonObject();
        foreach (var (key, value) in map) {
            built[key] = value;
        }

        return built;
    }

    /// <summary>RFC 3339 with the nanoseconds the store keeps, UTC.</summary>
    static string NanoStamp(long unixNanos) {
        var seconds = Math.DivRem(unixNanos, 1_000_000_000L, out var nanos);

        if (nanos < 0) {
            seconds--;
            nanos += 1_000_000_000L;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + "." + nanos.ToString("D9", CultureInfo.InvariantCulture) + "Z";
    }
}

/// <summary>Reading a validated action body, and the one tenancy derivation both metrics handlers share.</summary>
static class QueryBodies {
    /// <summary>
    ///     Returns the workspace's metrics tenancy — its <c>accountID</c> from the resolved GUID and its
    ///     tier from the stored body — if the workspace holds that account, and a <c>409</c> if it doesn't.
    /// </summary>
    public static async Task<Result<MetricsTenancy>> TenancyAsync(
        ActionContext context,
        IMonitorAccounts accounts,
        CancellationToken cancellationToken
    ) {
        var account = MonitorWorkspaces.AccountId(context.Id);

        if (!await accounts.IsHeldByAsync(account, context.Id.Id, cancellationToken)) {
            return Result<MetricsTenancy>.Failure(
                ErrorCode.Conflict,
                $"This workspace doesn't hold its metrics account ({account.ToString(CultureInfo.InvariantCulture)}), "
                + "so its metrics can't be read. Either it hasn't finished provisioning — try again once its "
                + "provisioningState is Succeeded — or it folds onto an account another workspace holds, and "
                + "its provisioning failed saying so; delete it and create it again."
            );
        }

        return Result<MetricsTenancy>.Success(
            new(account, MonitorWorkspaces.Tier(context.Desired, MonitorWorkspaces.Metrics))
        );
    }

    public static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static ImmutableArray<string> Texts(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.String).Select(static x => x.GetString() ?? "")]
            : [];

    public static int? Whole(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    public static DateTimeOffset? Instant(JsonElement body, string name) {
        var text = Text(body, name);

        return text.Length > 0
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                ? instant.ToUniversalTime()
                : null;
    }
}
