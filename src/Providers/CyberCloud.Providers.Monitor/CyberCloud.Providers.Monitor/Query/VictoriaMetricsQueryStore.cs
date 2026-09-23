using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Providers.Monitor.Query;

/// <summary>
///     <see cref="IMonitorMetricsStore" /> over VictoriaMetrics' cluster select API:
///     <c>{vmselect}/select/{accountID}/prometheus/api/v1/…</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ACCOUNT IS A PATH SEGMENT THIS CLASS WRITES, AND IT IS THE WHOLE OF THE ISOLATION.</b>
///         VictoriaMetrics' cluster version separates tenants by the <c>accountID</c> in the URL and by
///         nothing else — there is no credential between vmselect and vmstorage. So the account comes
///         from <see cref="MetricsTenancy" />, which the handler derived from the workspace's resolved
///         GUID; the expression goes in the form body; and nothing a caller sends is appended to the
///         path or the query string. ⚠ MetricsQL does not let an expression name an account: a
///         <c>vm_account_id</c> label filter is meaningful only on the <c>/select/multitenant/</c>
///         path, which this class never builds, and <c>MonitorQueryOverHttpTests</c> sends one to
///         prove it reads nothing.
///     </para>
///     <para>
///         ⚠ <b>The budget is the store's as well as ours.</b> Each request carries <c>timeout</c>, so
///         vmselect stops the query itself; this side cancels a second later, so a store that ignored
///         the parameter still cannot hold the handler. What vmselect has no per-request knob for is a
///         series cap on a range query — it answers every series that matched — which is why the body
///         is read up to <see cref="MonitorQueryOptions.MaxResponseBytes" /> and the series beyond
///         <see cref="MonitorQueries.MaxSeries" /> are counted and dropped here.
///     </para>
///     <para>
///         ⚠ <b>The store's words reach the caller only when they are about the caller's query.</b>
///         vmselect answers a malformed expression with <c>422</c> and a sentence naming the token,
///         which is the tenant's to read and is returned as a <c>400</c>. Anything else — unreachable,
///         a <c>5xx</c>, a body that is not the API's JSON — is logged with the account and answered
///         with one sentence, docs/plan/08 § Errors' <i>"No exception details, ever"</i>.
///     </para>
/// </remarks>
public sealed class VictoriaMetricsQueryStore : IMonitorMetricsStore {
    /// <summary>The one sentence a caller reads for a failure that is not theirs to fix.</summary>
    public const string FailedSentence =
        "The workspace's metrics store could not answer. The store's reply is in the gateway's log; quote the response's request id when reporting this.";

    readonly HttpClient http;
    readonly MonitorQueryOptions options;
    readonly ILogger<VictoriaMetricsQueryStore> logger;

    /// <summary>Creates the store over one long-lived <see cref="HttpClient" /> it does not own.</summary>
    /// <param name="http">The client.</param>
    /// <param name="options">Where vmselect is, and the budget.</param>
    /// <param name="logger">Where the store's own words go.</param>
    public VictoriaMetricsQueryStore(HttpClient http, MonitorQueryOptions options, ILogger<VictoriaMetricsQueryStore> logger) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        this.http = http;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<MetricsAnswer>> QueryAsync(
        MetricsTenancy tenancy,
        MetricsQuery query,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(query);

        var form = new List<KeyValuePair<string, string>> {
            new("query", query.Expression),
            new("timeout", Seconds(options.QueryTimeout) + "s")
        };

        string path;

        if (query.IsRange) {
            path = "api/v1/query_range";
            form.Add(new("start", Epoch(query.Start!.Value)));
            form.Add(new("end", Epoch(query.End!.Value)));
            form.Add(new("step", query.StepSeconds.ToString(CultureInfo.InvariantCulture) + "s"));
        } else {
            path = "api/v1/query";
            form.Add(new("time", Epoch(query.Time)));
        }

        var answered = await SendAsync(tenancy, path, form, cancellationToken);

        if (answered.TryGetError(out var error)) {
            return Result<MetricsAnswer>.Failure(error);
        }

        try {
            return Result<MetricsAnswer>.Success(ParseQuery(answered.GetValueOrThrow()));
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) {
            return Failed<MetricsAnswer>(tenancy, "the store answered 200 with a body that is not the query API's JSON", exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<LabelAnswer>> LabelsAsync(
        MetricsTenancy tenancy,
        LabelQuery query,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(query);

        // ⚠ The label name is a PATH segment on this endpoint, so it is escaped here and was matched
        // against MonitorQueries.LabelNamePattern before it arrived — a name is [a-zA-Z0-9_] and
        // cannot carry a slash, a dot-dot or a query string into the URL.
        var path = query.Label.Length == 0
            ? "api/v1/labels"
            : "api/v1/label/" + Uri.EscapeDataString(query.Label) + "/values";

        var form = new List<KeyValuePair<string, string>> {
            new("start", Epoch(query.Start)),
            new("end", Epoch(query.End)),
            // One more than the cap, so a full answer and a truncated one can be told apart.
            new("limit", (MonitorQueries.MaxLabelValues + 1).ToString(CultureInfo.InvariantCulture)),
            new("timeout", Seconds(options.QueryTimeout) + "s")
        };

        if (query.Match.Length > 0) {
            form.Add(new("match[]", query.Match));
        }

        var answered = await SendAsync(tenancy, path, form, cancellationToken);

        if (answered.TryGetError(out var error)) {
            return Result<LabelAnswer>.Failure(error);
        }

        try {
            using var document = JsonDocument.Parse(answered.GetValueOrThrow());
            var values = document.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(static x => x.GetString() ?? "")
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();

            return Result<LabelAnswer>.Success(
                new(
                    values.Length > MonitorQueries.MaxLabelValues ? values[..MonitorQueries.MaxLabelValues] : values,
                    values.Length > MonitorQueries.MaxLabelValues
                )
            );
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) {
            return Failed<LabelAnswer>(tenancy, "the store answered 200 with a body that is not the label API's JSON", exception.Message);
        }
    }

    /// <summary>
    ///     Posts one form to one select endpoint under the tenancy's account, and returns the body of a
    ///     <c>2xx</c> or the failure the caller should read.
    /// </summary>
    async Task<Result<string>> SendAsync(
        MetricsTenancy tenancy,
        string path,
        List<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken
    ) {
        var account = tenancy.AccountId.ToString(CultureInfo.InvariantCulture);
        var baseUri = options.MetricsEndpointFor(tenancy.Tier);
        var uri = new Uri(baseUri, $"/select/{account}/prometheus/{path}");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.QueryTimeout + TimeSpan.FromSeconds(1));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new FormUrlEncodedContent(form);

        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            var body = await BoundedBody.ReadAsync(response.Content, options.MaxResponseBytes, budget.Token);

            if (body is null) {
                return Result<string>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "The query matched more data than one response may carry "
                    + $"({(options.MaxResponseBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB). "
                    + "Narrow it with a label filter, aggregate it with sum by (…), or widen the step."
                );
            }

            if (response.IsSuccessStatusCode) {
                return Result<string>.Success(body);
            }

            var said = StoreError(body);

            if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest && said.Length > 0) {
                // The tenant's own expression, refused with the store's reason — theirs to read.
                return Result<string>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "The metrics store refused the query: " + Truncate(said, 1000)
                );
            }

            if (said.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || said.Contains("deadline", StringComparison.OrdinalIgnoreCase)) {
                return BudgetExceeded();
            }

            return Failed<string>(tenancy, $"vmselect answered {(int)response.StatusCode}", Truncate(said.Length > 0 ? said : body, 2000));
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return BudgetExceeded();
        } catch (HttpRequestException exception) {
            return Failed<string>(tenancy, "vmselect could not be reached", exception.Message);
        }
    }

    Result<string> BudgetExceeded() =>
        Result<string>.Failure(
            ErrorCode.InvalidRequestBody,
            $"The query ran past the metrics store's budget of {Seconds(options.QueryTimeout)} s. Narrow it "
            + "with a label filter, shorten the window, or widen the step."
        );

    Result<T> Failed<T>(MetricsTenancy tenancy, string what, string detail) where T : notnull {
        logger.LogError(
            "Metrics query for account {AccountId} ({Tier}) failed: {What}. Detail: {Detail}",
            tenancy.AccountId,
            tenancy.Tier,
            what,
            detail
        );

        return Result<T>.Failure(ErrorCode.InternalError, FailedSentence);
    }

    /// <summary>
    ///     Reads the Prometheus query API's <c>data</c> into series, keeping the first
    ///     <see cref="MonitorQueries.MaxSeries" /> and counting the rest.
    /// </summary>
    /// <param name="body">The <c>200</c> body.</param>
    public static MetricsAnswer ParseQuery(string body) {
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var resultType = data.GetProperty("resultType").GetString() ?? "";
        var result = data.GetProperty("result");

        if (resultType is "scalar" or "string") {
            // [ <seconds>, "<value>" ] — one point with no labels, which is a series to a chart.
            var point = Point(result);
            return new(resultType, [new(ImmutableSortedDictionary<string, string>.Empty, [point])], 1);
        }

        var series = ImmutableArray.CreateBuilder<MetricSeries>();
        var total = 0;

        foreach (var item in result.EnumerateArray()) {
            total++;

            if (series.Count >= MonitorQueries.MaxSeries) {
                continue;
            }

            var labels = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

            if (item.TryGetProperty("metric", out var metric)) {
                foreach (var label in metric.EnumerateObject()) {
                    labels[label.Name] = label.Value.GetString() ?? "";
                }
            }

            var points = ImmutableArray.CreateBuilder<(double, double?)>();

            if (item.TryGetProperty("values", out var values)) {
                foreach (var value in values.EnumerateArray()) {
                    points.Add(Point(value));
                }
            } else if (item.TryGetProperty("value", out var single)) {
                points.Add(Point(single));
            }

            series.Add(new(labels.ToImmutable(), points.ToImmutable()));
        }

        return new(resultType, series.ToImmutable(), total);
    }

    /// <summary>One <c>[seconds, "value"]</c> pair; a non-finite or unparseable value is <see langword="null" />.</summary>
    static (double Seconds, double? Value) Point(JsonElement pair) {
        var seconds = pair[0].GetDouble();
        var text = pair[1].GetString();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
                ? (seconds, value)
                : (seconds, null);
    }

    /// <summary>The <c>error</c> member of the API's failure body, or empty.</summary>
    static string StoreError(string body) {
        try {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
        } catch (JsonException) {
            return "";
        }
    }

    static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    static string Epoch(DateTimeOffset instant) =>
        (instant.ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    static string Seconds(TimeSpan span) => Math.Max(1, (long)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads a response body up to a byte ceiling.</summary>
static class BoundedBody {
    /// <summary>The body as UTF-8 text, or <see langword="null" /> when it is longer than <paramref name="limit" />.</summary>
    /// <param name="content">The response content.</param>
    /// <param name="limit">The ceiling, in bytes.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<string?> ReadAsync(HttpContent content, long limit, CancellationToken cancellationToken) {
        if (content.Headers.ContentLength is { } declared && declared > limit) {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
            if (buffer.Length + read > limit) {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
