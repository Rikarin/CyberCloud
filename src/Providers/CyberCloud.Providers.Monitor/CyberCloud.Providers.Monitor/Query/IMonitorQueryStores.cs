using System.Collections.Immutable;

namespace CyberCloud.Providers.Monitor.Query;

/// <summary>Where one workspace's metrics are: its <c>accountID</c> and the tier whose cluster holds them.</summary>
/// <param name="AccountId">From <see cref="MonitorWorkspaces.AccountId" /> over the resolved GUID — never from a request.</param>
/// <param name="Tier">The workspace's metrics retention tier, which picks the <c>VMCluster</c>.</param>
public readonly record struct MetricsTenancy(uint AccountId, string Tier);

/// <summary>One validated metrics query.</summary>
/// <param name="Expression">The PromQL or MetricsQL text.</param>
/// <param name="Start">A range query's start, or <see langword="null" /> for an instant query.</param>
/// <param name="End">A range query's end.</param>
/// <param name="Time">An instant query's evaluation time.</param>
/// <param name="StepSeconds">A range query's step.</param>
public sealed record MetricsQuery(
    string Expression,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    DateTimeOffset Time,
    int StepSeconds
) {
    /// <summary>Whether this is a range query.</summary>
    public bool IsRange => Start is not null;
}

/// <summary>One series of a metrics answer.</summary>
/// <param name="Labels">The label set, <c>__name__</c> included when the store kept it.</param>
/// <param name="Points">
///     Epoch seconds and value, ascending. ⚠ A <see langword="null" /> value is a sample the store
///     answered as <c>NaN</c> or an infinity, which JSON cannot carry — never a missing sample, which
///     is simply absent.
/// </param>
public sealed record MetricSeries(
    ImmutableSortedDictionary<string, string> Labels,
    ImmutableArray<(double Seconds, double? Value)> Points
);

/// <summary>What a metrics query matched.</summary>
/// <param name="ResultType"><c>matrix</c>, <c>vector</c>, <c>scalar</c> or <c>string</c>, as the store said.</param>
/// <param name="Series">At most <see cref="MonitorQueries.MaxSeries" /> of them.</param>
/// <param name="SeriesTotal">How many the store answered, before the cap.</param>
public sealed record MetricsAnswer(string ResultType, ImmutableArray<MetricSeries> Series, int SeriesTotal) {
    /// <summary>Whether series were dropped.</summary>
    public bool Truncated => SeriesTotal > Series.Length;
}

/// <summary>One validated label listing.</summary>
/// <param name="Label">The label whose values to list, or empty for the label names.</param>
/// <param name="Match">A series selector, or empty.</param>
/// <param name="Start">The window's start.</param>
/// <param name="End">The window's end.</param>
public sealed record LabelQuery(string Label, string Match, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A label listing's answer.</summary>
/// <param name="Values">Sorted, at most <see cref="MonitorQueries.MaxLabelValues" />.</param>
/// <param name="Truncated">Whether more matched.</param>
public sealed record LabelAnswer(ImmutableArray<string> Values, bool Truncated);

/// <summary>One validated log search.</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
/// <param name="Text">Body text, or empty.</param>
/// <param name="Severities">Severity classes, or empty for every record.</param>
/// <param name="Service">A service name, or empty.</param>
/// <param name="Attributes">Key and value pairs, each matched against record and resource attributes.</param>
/// <param name="TraceId">A trace id, lower-case hex, or empty.</param>
/// <param name="Top">Rows to return.</param>
/// <param name="BucketSeconds">The histogram's bucket width.</param>
public sealed record LogSearch(
    DateTimeOffset From,
    DateTimeOffset To,
    string Text,
    ImmutableArray<string> Severities,
    string Service,
    ImmutableArray<(string Key, string Value)> Attributes,
    string TraceId,
    int Top,
    int BucketSeconds
);

/// <summary>One log record as a search returns it.</summary>
/// <param name="UnixNanos">When, in nanoseconds since the epoch.</param>
/// <param name="SeverityNumber">OpenTelemetry's 0–24.</param>
/// <param name="SeverityText">The source's own spelling.</param>
/// <param name="Service">The <c>service.name</c>.</param>
/// <param name="Body">The message.</param>
/// <param name="TraceId">The trace, or empty.</param>
/// <param name="SpanId">The span, or empty.</param>
/// <param name="Attributes">The record's own attributes.</param>
/// <param name="Resource">Its resource's attributes.</param>
public sealed record LogRecord(
    long UnixNanos,
    int SeverityNumber,
    string SeverityText,
    string Service,
    string Body,
    string TraceId,
    string SpanId,
    ImmutableSortedDictionary<string, string> Attributes,
    ImmutableSortedDictionary<string, string> Resource
);

/// <summary>One histogram bucket.</summary>
/// <param name="Index">Buckets from <see cref="LogSearch.From" />, zero-based.</param>
/// <param name="BySeverity">Counts per severity class, <c>unspecified</c> included.</param>
public sealed record LogBucket(long Index, ImmutableSortedDictionary<string, long> BySeverity);

/// <summary>What the store reports it spent on a search.</summary>
/// <param name="RowsRead">Rows read, across both statements.</param>
/// <param name="BytesRead">Bytes read, across both statements.</param>
public readonly record struct LogStatistics(long RowsRead, long BytesRead);

/// <summary>A search's answer.</summary>
/// <param name="Rows">At most <see cref="LogSearch.Top" />, newest first.</param>
/// <param name="Truncated">Whether more matched.</param>
/// <param name="Buckets">The non-empty buckets, in order.</param>
/// <param name="Statistics">What the store read.</param>
/// <param name="Note">Why the answer is empty when that is not the filter's doing, or empty.</param>
public sealed record LogAnswer(
    ImmutableArray<LogRecord> Rows,
    bool Truncated,
    ImmutableArray<LogBucket> Buckets,
    LogStatistics Statistics,
    string Note
);

/// <summary>What a search would read, from ClickHouse's own estimate.</summary>
/// <param name="Rows">Rows in the parts the search cannot prune.</param>
/// <param name="Parts">How many parts.</param>
/// <param name="Marks">How many index marks.</param>
public readonly record struct LogEstimate(long Rows, long Parts, long Marks);

/// <summary>
///     Reads one workspace's metrics. The seam between the action handlers and VictoriaMetrics.
/// </summary>
/// <remarks>
///     ⚠ <b>The tenancy is a parameter the handler computes, and there is no overload without it.</b>
///     An implementation spells <see cref="MetricsTenancy.AccountId" /> into the one path segment
///     VictoriaMetrics' cluster grammar reserves for it, and nothing else in the request can name an
///     account.
/// </remarks>
public interface IMonitorMetricsStore {
    /// <summary>Runs an instant or range query.</summary>
    /// <param name="tenancy">Whose metrics.</param>
    /// <param name="query">The validated query.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<Result<MetricsAnswer>> QueryAsync(MetricsTenancy tenancy, MetricsQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lists label names or one label's values.</summary>
    /// <param name="tenancy">Whose metrics.</param>
    /// <param name="query">The validated listing.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    Task<Result<LabelAnswer>> LabelsAsync(MetricsTenancy tenancy, LabelQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
///     Searches one workspace's logs. The seam between the action handler and ClickHouse.
/// </summary>
/// <remarks>
///     ⚠ <b>The database is a parameter the handler computes</b>, from
///     <see cref="MonitorWorkspaces.Database" /> over the resolved GUID, and an implementation refuses
///     a name <see cref="MonitorQueries.IsWorkspaceDatabase" /> does not accept before spelling it.
/// </remarks>
public interface IMonitorLogStore {
    /// <summary>Runs a search: the newest rows and the histogram.</summary>
    /// <param name="database">The workspace's database.</param>
    /// <param name="search">The validated search.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<Result<LogAnswer>> SearchAsync(string database, LogSearch search, CancellationToken cancellationToken = default);

    /// <summary>Asks the store how much a search would read, and runs nothing.</summary>
    /// <param name="database">The workspace's database.</param>
    /// <param name="search">The validated search.</param>
    /// <param name="cancellationToken">Cancels the estimate.</param>
    Task<Result<LogEstimate>> EstimateAsync(string database, LogSearch search, CancellationToken cancellationToken = default);
}

/// <summary>
///     The store a host with no <see cref="MonitorQueryOptions.SectionName" /> gets: every query
///     refused, by name.
/// </summary>
public sealed class UnavailableMonitorQueryStore : IMonitorMetricsStore, IMonitorLogStore {
    /// <summary>The sentence for the metrics half.</summary>
    public const string MetricsSentence =
        "This host has no metrics store to query: set " + MonitorQueryOptions.SectionName
        + ":MetricsEndpoint to the region's vmselect. Nothing was run, and the workspace's data is untouched.";

    /// <summary>The sentence for the logs half.</summary>
    public const string LogsSentence =
        "This host has no logs store to query: set " + MonitorQueryOptions.SectionName
        + ":LogsEndpoint to the region's ClickHouse. Nothing was run, and the workspace's data is untouched.";

    /// <inheritdoc />
    public Task<Result<MetricsAnswer>> QueryAsync(MetricsTenancy tenancy, MetricsQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<MetricsAnswer>.Failure(ErrorCode.InternalError, MetricsSentence));

    /// <inheritdoc />
    public Task<Result<LabelAnswer>> LabelsAsync(MetricsTenancy tenancy, LabelQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LabelAnswer>.Failure(ErrorCode.InternalError, MetricsSentence));

    /// <inheritdoc />
    public Task<Result<LogAnswer>> SearchAsync(string database, LogSearch search, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LogAnswer>.Failure(ErrorCode.InternalError, LogsSentence));

    /// <inheritdoc />
    public Task<Result<LogEstimate>> EstimateAsync(string database, LogSearch search, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LogEstimate>.Failure(ErrorCode.InternalError, LogsSentence));
}
