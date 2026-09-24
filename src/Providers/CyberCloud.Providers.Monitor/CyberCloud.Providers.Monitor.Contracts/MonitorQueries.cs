using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     The three read actions on <c>CyberCloud.Monitor/workspaces</c> that the portal's metrics
///     explorer and log search are built on: <c>queryMetrics</c>, <c>listMetricLabels</c> and
///     <c>searchLogs</c>. Issue #41, docs/plan/16 § Querying a workspace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ACTIONS ON THE WORKSPACE, NOT A FIFTH COMPONENT BEHIND THE GATEWAY, AND THE ADDRESS IS
///         WHAT DECIDES IT.</b> #54's resource graph got its own route kind because a graph query has
///         no resource to hang off: it reads every resource the caller may see. A metrics query has
///         exactly one — the workspace whose <c>accountID</c> it runs under — and the action path
///         already does, in this order, every step the query needs: resolve the address with the
///         <i>token's</i> tenant, refuse an address that does not exist, check a permission through
///         the one enforcement seam (docs/plan/07), and hand the handler the resource's GUID. The
///         GUID is where the <c>accountID</c> and the database come from, so the tenancy coordinate
///         the store is asked under is derived from what the platform resolved and never from
///         anything the request carries. A route of its own would have had to repeat all four, and
///         the gateway may not name the seam it would have had to call (<c>GatewayIsolationTests</c>).
///     </para>
///     <para>
///         ⚠ <b>The permission is <c>read</c>, which the Reader role grants.</b> Querying a workspace's
///         data is reading the workspace, and docs/plan/07 § Azure RBAC, expressed in it gives
///         <c>read</c> to Reader. <c>listKeys</c> is the contrast: it hands out the credential that
///         <i>writes</i>, so it has its own permission. A query hands out nothing a Reader could not
///         already see in the tenant's own Grafana.
///     </para>
///     <para>
///         ⚠ <b>Two of the three responses are undeclared, and that is the registry's limit rather
///         than a choice.</b> A range query is a list of series, each a label map and a list of
///         points; a log search is a list of rows, each with two attribute maps. <see cref="SchemaKind.Array" />
///         refuses an array of objects by design, and the house workaround — one text line per item,
///         as <c>listInstances</c> and <c>listSuppressions</c> do — is right for a line a person reads
///         and wrong for numbers a chart plots. So <c>queryMetrics</c> and <c>searchLogs</c> declare a
///         request and no response: the dispatcher does not check what it cannot describe, the
///         generated clients type the body <c>unknown</c>, and the shape is written down here and in
///         docs/plan/16 instead. <c>listMetricLabels</c> answers a list of strings, which the schema
///         can say, so it says it. The api-version with an array-of-objects kind is what closes it —
///         <c>charts/managed/monitor-workspace/conformance.yaml § owed</c>,
///         <c>query-responses-are-undeclared</c>.
///     </para>
/// </remarks>
public static class MonitorQueries {
    // ── The actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>PromQL or MetricsQL over the workspace's metrics, instant or range.</summary>
    public const string QueryMetricsAction = "queryMetrics";

    /// <summary>The metric names, the label names on a metric, or one label's values.</summary>
    public const string ListMetricLabelsAction = "listMetricLabels";

    /// <summary>The workspace's logs in a time window, filtered, newest first, with a histogram.</summary>
    public const string SearchLogsAction = "searchLogs";

    /// <summary>What all three check. See the remarks on this type.</summary>
    public const string Permission = "read";

    // ── The limits ────────────────────────────────────────────────────────────────────────────
    //
    // ⚠ Constants rather than options, because every one of them is part of what a caller may ask
    // for and the refusal names the number. A deployment that wanted a larger series cap would be
    // publishing a different API under the same api-version. What IS deployment configuration —
    // the store's own budget per query, rows read and wall clock — is MonitorQueryOptions'.

    /// <summary>The longest expression or search text accepted, in characters.</summary>
    public const int MaxQueryLength = 4000;

    /// <summary>The most points one series of a range query may have — Prometheus' own ceiling.</summary>
    /// <remarks>
    ///     ⚠ Prometheus refuses <c>(end - start) / step &gt; 11000</c> with <i>"exceeded maximum
    ///     resolution of 11,000 points per timeseries"</i>, and every PromQL client already chooses a
    ///     step inside it. The same number here means a query that works against Prometheus works
    ///     here, and a chart of 11 000 points is already wider than any screen.
    /// </remarks>
    public const int MaxPoints = 11_000;

    /// <summary>How many points a range query is given when it names no step.</summary>
    public const int DefaultPoints = 240;

    /// <summary>The most series a metrics response carries; the rest are counted and dropped.</summary>
    /// <remarks>
    ///     ⚠ <b>Truncated and said so, not refused.</b> The explorer's first query is often a bare
    ///     metric name, and a refusal would make the tenant guess a label filter before seeing a single
    ///     line. The response carries <c>seriesTotal</c> and <c>truncated</c>, so nothing is dropped
    ///     silently — docs/plan/16 § Cost and retention honesty's rule, applied to a read.
    /// </remarks>
    public const int MaxSeries = 500;

    /// <summary>The longest window a metrics query may cover: the <c>extended</c> tier's retention.</summary>
    public static TimeSpan MaxMetricsRange { get; } = TimeSpan.FromDays(400);

    /// <summary>The most label names or values <c>listMetricLabels</c> returns.</summary>
    public const int MaxLabelValues = 10_000;

    /// <summary>The longest window a log search may cover: the <c>extended</c> tier's log retention.</summary>
    public static TimeSpan MaxLogsRange { get; } = TimeSpan.FromDays(90);

    /// <summary>The most rows one log search returns.</summary>
    public const int MaxLogRows = 1000;

    /// <summary>The rows a log search returns when it names no <c>top</c>.</summary>
    public const int DefaultLogRows = 100;

    /// <summary>How many buckets the histogram is cut into when the search names no bucket width.</summary>
    public const int DefaultHistogramBuckets = 60;

    /// <summary>The most buckets a histogram may have, whatever width the search names.</summary>
    public const int MaxHistogramBuckets = 1000;

    /// <summary>The most <c>key=value</c> attribute filters one search may carry.</summary>
    public const int MaxAttributeFilters = 10;

    // ── Severities ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The six severity classes a search filters on, in OpenTelemetry's order, and the
    ///     <c>SeverityNumber</c> ranges they cover.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The NUMBER, not the text.</b> OpenTelemetry's log data model defines
    ///     <c>SeverityNumber</c> 1–24 in six bands of four and leaves <c>SeverityText</c> to the
    ///     source: <c>ERROR</c>, <c>Error</c>, <c>err</c> and <c>E</c> are all spellings a real
    ///     collector forwards. A filter on the text would miss every spelling it did not list, so the
    ///     search compares <c>intDiv(SeverityNumber - 1, 4)</c> against the band index and the text is
    ///     only ever displayed. A record with <c>SeverityNumber</c> 0 — unspecified — is in no band and
    ///     matches no severity filter; it still matches a search that names none.
    /// </remarks>
    public static ImmutableArray<string> Severities { get; } = ["trace", "debug", "info", "warn", "error", "fatal"];

    /// <summary>The band index of each severity, for the SQL.</summary>
    public static FrozenDictionary<string, int> SeverityBand { get; } =
        Severities.Select(static (name, index) => (name, index)).ToFrozenDictionary(static x => x.name, static x => x.index, StringComparer.Ordinal);

    /// <summary>The severity class a <c>SeverityNumber</c> falls in, or <c>"unspecified"</c>.</summary>
    /// <param name="severityNumber">OpenTelemetry's 0–24.</param>
    public static string SeverityOf(int severityNumber) =>
        severityNumber is >= 1 and <= 24 ? Severities[(severityNumber - 1) / 4] : "unspecified";

    // ── The requests ──────────────────────────────────────────────────────────────────────────

    /// <summary>The body of <c>queryMetrics</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Instant or range, decided by the body.</b> <c>start</c> and <c>end</c> together make a
    ///         range query; neither makes an instant query at <c>time</c>, or now. One without the
    ///         other is refused by the handler, which is the one rule the flat schema cannot state.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No accountID, no tenant, no URL, and no pass-through parameter.</b> VictoriaMetrics'
    ///         select API takes <c>extra_label</c> and <c>extra_filters[]</c>, which would let a caller
    ///         widen or narrow what the account sees, and a <c>nocache</c> that costs the store. None of
    ///         them is here and nothing forwards a body member the schema does not name — the schema
    ///         rejects unknown members before the handler runs.
    ///     </para>
    /// </remarks>
    public static ResourceSchema QueryMetricsRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/query",
                    SchemaKind.Text,
                    true,
                    Description: "A PromQL or MetricsQL expression, run under this workspace's metrics tenancy."
                ) { MinLength = 1, MaxLength = MaxQueryLength },
                new(
                    "/start",
                    SchemaKind.Text,
                    Description: "Where a range query starts. Give it with end, or neither for an instant query."
                ) { Format = SchemaFormat.DateTime },
                new("/end", SchemaKind.Text, Description: "Where a range query ends. Give it with start.")
                    { Format = SchemaFormat.DateTime },
                new(
                    "/time",
                    SchemaKind.Text,
                    Description: "The instant an instant query is evaluated at. Defaults to now."
                ) { Format = SchemaFormat.DateTime },
                new(
                    "/stepSeconds",
                    SchemaKind.WholeNumber,
                    Description: "A range query's resolution. Defaults to the window cut into 240 points; "
                    + "the window divided by it may not exceed 11000."
                ) { Minimum = 1, Maximum = 86_400 }
            ]
        );

    /// <summary>The body of <c>listMetricLabels</c>.</summary>
    public static ResourceSchema ListMetricLabelsRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/label",
                    SchemaKind.Text,
                    Description: "The label whose values to list — __name__ for the metric names. "
                    + "Leave it out to list the label names instead."
                ) { Pattern = LabelNamePattern, MaxLength = 128 },
                new(
                    "/match",
                    SchemaKind.Text,
                    Description: "A series selector the answer is narrowed to, for example http_requests_total."
                ) { MaxLength = 1000 },
                new("/start", SchemaKind.Text, Description: "The window's start. Defaults to a day before end.")
                    { Format = SchemaFormat.DateTime },
                new("/end", SchemaKind.Text, Description: "The window's end. Defaults to now.")
                    { Format = SchemaFormat.DateTime }
            ]
        );

    /// <summary>What <c>listMetricLabels</c> returns.</summary>
    public static ResourceSchema ListMetricLabelsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/values",
                    SchemaKind.Array,
                    true,
                    Description: "The label values, or the label names when no label was named, sorted."
                ) { ElementKind = SchemaKind.Text },
                new(
                    "/truncated",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether more than 10000 matched and the rest were left out."
                )
            ]
        );

    /// <summary>The body of <c>searchLogs</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A STRUCTURED FILTER AND NOT #54's KQL, AND THE DECISION IS RECORDED HERE BECAUSE IT
    ///         WAS A REAL ONE.</b> Reusing <c>KqlTranslator</c> would have given the log search a query
    ///         language for free. Three facts decided against it:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <b>The subset refuses the clock, and a log search is a question about the clock.</b>
    ///             <c>KqlSubset</c> leaves out <c>ago</c>, <c>now</c> and <c>bin</c> on purpose — each
    ///             is "a cost or a surface that wants its own decision" — and the histogram is
    ///             <c>bin</c>, the window is <c>ago</c>. Reuse means widening the subset exactly
    ///             where the cost is.
    ///         </item>
    ///         <item>
    ///             <b>The window has to be a bound, not a predicate the caller may omit.</b> A
    ///             ninety-day scan of a busy workspace is docs/plan/20's "someone will run a 400-day
    ///             scan". Here <c>from</c> and <c>to</c> are required and the span is capped before any
    ///             SQL exists; in KQL the window is a <c>where</c> clause the translator would have to
    ///             find, prove present and bound.
    ///         </item>
    ///         <item>
    ///             <b>The translator is built around a filter a log row does not have.</b> Every
    ///             statement it emits ANDs in <c>hasAny(access, …)</c> over the caller's usersets,
    ///             because a resource graph row is visible per resource. A log row is visible to
    ///             whoever may read the workspace — decided once, by the action's permission — so the
    ///             translator's central guarantee would be a no-op it insists on.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         What the structured filter costs is the power a query language has, and the portal's
    ///         query box recovers the useful part of it: <c>severity:error service:api "timed out"
    ///         http.method=GET</c> is parsed in the browser into this body. A KQL over the logs table —
    ///         the translator generalised to a declared schema, with the window made mandatory — is
    ///         <c>charts/managed/monitor-workspace/conformance.yaml § owed</c>, <c>logs-have-no-query-language</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every value is a ClickHouse query parameter.</b> The text, the service, each
    ///         attribute key and value, and the trace id reach the statement as <c>{name:Type}</c>
    ///         placeholders bound server-side; the only identifier in the SQL is the workspace's
    ///         database, derived from its GUID's hex digits and checked against
    ///         <see cref="DatabasePattern" /> before it is spelled.
    ///     </para>
    /// </remarks>
    public static ResourceSchema SearchLogsRequest { get; } =
        ResourceSchema.Of(
            [
                new("/from", SchemaKind.Text, true, Description: "The window's start, inclusive.")
                    { Format = SchemaFormat.DateTime },
                new("/to", SchemaKind.Text, true, Description: "The window's end, exclusive. At most 90 days after from.")
                    { Format = SchemaFormat.DateTime },
                new(
                    "/text",
                    SchemaKind.Text,
                    Description: "Text the log body must contain, compared without regard to case."
                ) { MaxLength = 512 },
                new(
                    "/severities",
                    SchemaKind.Array,
                    Description: "The severity classes to keep. Leave it out for every record, including "
                    + "those with no severity."
                ) { ElementKind = SchemaKind.Text, AllowedValues = Severities },
                new("/service", SchemaKind.Text, Description: "The service.name the record must come from.")
                    { MaxLength = 256 },
                new(
                    "/attributes",
                    SchemaKind.Array,
                    Description: "Up to 10 key=value filters, each matched against the record's own "
                    + "attributes and its resource's."
                ) { ElementKind = SchemaKind.Text, Pattern = AttributeFilterPattern, MaxLength = 641 },
                new("/traceId", SchemaKind.Text, Description: "The trace the record must belong to, 32 hex digits.")
                    { Pattern = "[0-9a-fA-F]{32}" },
                new(
                    "/top",
                    SchemaKind.WholeNumber,
                    Description: "How many records to return, newest first. Defaults to 100."
                ) { Minimum = 1, Maximum = MaxLogRows },
                new(
                    "/bucketSeconds",
                    SchemaKind.WholeNumber,
                    Description: "The histogram's bucket width. Defaults to the window cut into 60."
                ) { Minimum = 1, Maximum = 86_400 },
                new(
                    "/estimate",
                    SchemaKind.Boolean,
                    Description: "Answer how many rows the search would read, and run nothing else."
                )
            ]
        );

    // ── Grammar the handlers and the portal share ─────────────────────────────────────────────

    /// <summary>A Prometheus label name, <c>__name__</c> included.</summary>
    public const string LabelNamePattern = "[a-zA-Z_][a-zA-Z0-9_]*";

    /// <summary>One attribute filter: a key of 1–128 characters with no <c>=</c>, then <c>=</c>, then the value.</summary>
    public const string AttributeFilterPattern = "[^=]{1,128}=.{0,512}";

    /// <summary>What <see cref="MonitorWorkspaces.Database" /> produces, and the only identifier a log search spells.</summary>
    public const string DatabasePattern = "ws_[0-9a-f]{32}";

    static readonly Regex DatabaseShape = new(
        "^" + DatabasePattern + "$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
    );

    /// <summary>Whether a database name is one <see cref="MonitorWorkspaces.Database" /> could have produced.</summary>
    /// <param name="database">The name about to be spelled into a statement.</param>
    public static bool IsWorkspaceDatabase(string database) =>
        !string.IsNullOrEmpty(database) && DatabaseShape.IsMatch(database);

    /// <summary>Splits one attribute filter at its first <c>=</c>.</summary>
    /// <param name="filter">A value <see cref="AttributeFilterPattern" /> accepted.</param>
    /// <returns>The key and the value; the value may be empty.</returns>
    public static (string Key, string Value) SplitAttribute(string filter) {
        ArgumentNullException.ThrowIfNull(filter);

        var equals = filter.IndexOf('=', StringComparison.Ordinal);

        return equals <= 0 ? (filter, "") : (filter[..equals], filter[(equals + 1)..]);
    }

    /// <summary>The step a range query gets when it names none: the window over <see cref="DefaultPoints" />, at least a second.</summary>
    /// <param name="window">The range's length.</param>
    public static int DefaultStepSeconds(TimeSpan window) =>
        (int)Math.Max(1, Math.Ceiling(window.TotalSeconds / DefaultPoints));

    /// <summary>The bucket width a histogram gets when the search names none.</summary>
    /// <param name="window">The search's window.</param>
    public static int DefaultBucketSeconds(TimeSpan window) =>
        (int)Math.Max(1, Math.Ceiling(window.TotalSeconds / DefaultHistogramBuckets));

    /// <summary>An instant as the wire spells it: RFC 3339, UTC, millisecond precision.</summary>
    /// <param name="instant">The instant.</param>
    public static string Stamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
///     The ClickHouse table a workspace's logs are in, as the collector's exporter writes it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE EXPORTER'S SHAPE, BECAUSE THE EXPORTER IS THE WRITER.</b> A workspace's collector
///         runs upstream's <c>clickhouse</c> exporter with <c>create_schema: false</c> against
///         <c>otel_logs</c> in the workspace's database (<see cref="MonitorCollectors" />), so the table
///         a search reads is whatever that exporter inserts into. <see cref="CreateSql" /> is its logs
///         DDL as the exporter renders it, reproduced by hand; nothing in this repository runs it in
///         production — <c>charts/managed/monitor-collector/conformance.yaml § owed</c>,
///         <c>collector-clickhouse-tables-are-the-exporters-shape</c> — and the search tests run it to
///         seed. ⚠ No test compares it with the exporter's own source, which is why the search reads
///         nine columns that have kept their names and types across the exporter's releases and
///         nothing else: <c>Timestamp</c>, <c>TimestampTime</c>, <c>TraceId</c>, <c>SpanId</c>,
///         <c>SeverityText</c>, <c>SeverityNumber</c>, <c>ServiceName</c>, <c>Body</c>,
///         <c>ResourceAttributes</c> and <c>LogAttributes</c>.
///     </para>
///     <para>
///         ⚠ <b>A missing table is an empty answer and not an error.</b> A workspace nobody has sent
///         a log to has no table, and until the schema step exists that is every workspace; a search
///         there answers no rows and says why rather than a <c>500</c>.
///     </para>
/// </remarks>
public static class MonitorLogsTable {
    /// <summary>The table's name inside the workspace's database.</summary>
    public const string Name = "otel_logs";

    /// <summary>The exporter's logs DDL for one database. See the remarks on this type.</summary>
    /// <param name="database">A name <see cref="MonitorQueries.IsWorkspaceDatabase" /> accepts.</param>
    /// <exception cref="ArgumentException">The name is not a workspace database.</exception>
    public static string CreateSql(string database) {
        if (!MonitorQueries.IsWorkspaceDatabase(database)) {
            throw new ArgumentException($"'{database}' is not a workspace database name.", nameof(database));
        }

        return $"""
                CREATE TABLE IF NOT EXISTS `{database}`.`{Name}` (
                    Timestamp DateTime64(9) CODEC(Delta(8), ZSTD(1)),
                    TimestampTime DateTime DEFAULT toDateTime(Timestamp),
                    TraceId String CODEC(ZSTD(1)),
                    SpanId String CODEC(ZSTD(1)),
                    TraceFlags UInt8,
                    SeverityText LowCardinality(String) CODEC(ZSTD(1)),
                    SeverityNumber UInt8,
                    ServiceName LowCardinality(String) CODEC(ZSTD(1)),
                    Body String CODEC(ZSTD(1)),
                    ResourceSchemaUrl LowCardinality(String) CODEC(ZSTD(1)),
                    ResourceAttributes Map(LowCardinality(String), String) CODEC(ZSTD(1)),
                    ScopeSchemaUrl LowCardinality(String) CODEC(ZSTD(1)),
                    ScopeName String CODEC(ZSTD(1)),
                    ScopeVersion LowCardinality(String) CODEC(ZSTD(1)),
                    ScopeAttributes Map(LowCardinality(String), String) CODEC(ZSTD(1)),
                    LogAttributes Map(LowCardinality(String), String) CODEC(ZSTD(1)),
                    INDEX idx_trace_id TraceId TYPE bloom_filter(0.001) GRANULARITY 1,
                    INDEX idx_res_attr_key mapKeys(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                    INDEX idx_res_attr_value mapValues(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                    INDEX idx_log_attr_key mapKeys(LogAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                    INDEX idx_log_attr_value mapValues(LogAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                    INDEX idx_body Body TYPE tokenbf_v1(32768, 3, 0) GRANULARITY 8
                ) ENGINE = MergeTree()
                PARTITION BY toDate(TimestampTime)
                PRIMARY KEY (ServiceName, TimestampTime)
                ORDER BY (ServiceName, TimestampTime, Timestamp)
                SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
                """;
    }
}
