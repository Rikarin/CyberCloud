using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     The ClickHouse tables a workspace's database holds: the shape upstream's <c>clickhouse</c>
///     exporter writes, owned by the platform.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Copied from what the exporter itself creates, and a test keeps it that way rather than
///             a comment.
///         </b> The collector runs with <c>create_schema: false</c> —
///         <c>MonitorCollectors.CollectorConfig</c> says why a tenant's credential must not decide
///         the schema of the platform's store — so the tables have to be there, in exactly the shape
///         the exporter at <see cref="MonitorCollectors.ImageTag" /> inserts into. These statements are
///         the exporter's own <c>SHOW CREATE TABLE</c> output with the database substituted, read on
///         2026-09-23 from the pinned image against <c>clickhouse/clickhouse-server:25.3-alpine</c>.
///         <c>ComponentViewsAgainstClickHouseTests.ThePlatformsTablesAreTheExportersShape</c> lets the
///         real exporter create its own tables beside these and compares every column's name, type,
///         default and codec; a collector image bump that moved the shape fails there, by column.
///     </para>
///     <para>
///         ⚠ <b>Nothing in production runs these yet.</b> The workspace's reconciler applies
///         Kubernetes objects and has no seam to the store, and the ingest host's schema step
///         (docs/plan/16 § Ingest) does not exist —
///         <c>charts/managed/monitor-collector/conformance.yaml § owed</c>,
///         <c>collector-clickhouse-tables-are-the-exporters-shape</c>. The statements are here so that
///         step has one spelling to run, and so the views' SQL is written against a shape that is
///         checked rather than remembered.
///     </para>
///     <para>
///         ⚠ <b>Four statements, and the last two are not optional.</b> <c>otel_traces_trace_id_ts</c>
///         and the materialized view that fills it are part of what the exporter creates; a trace
///         lookup that wants to bound its scan by a trace's first and last timestamp reads them.
///     </para>
/// </remarks>
public static partial class MonitorTelemetrySchema {
    /// <summary>The spans table.</summary>
    public const string TracesTable = "otel_traces";

    /// <summary>The log records table.</summary>
    public const string LogsTable = "otel_logs";

    /// <summary>A trace id's first and last timestamp, filled by <see cref="TraceIdViewName" />.</summary>
    public const string TraceIdTable = "otel_traces_trace_id_ts";

    /// <summary>The materialized view over <see cref="TracesTable" /> that fills <see cref="TraceIdTable" />.</summary>
    public const string TraceIdViewName = "otel_traces_trace_id_ts_mv";

    /// <summary>
    ///     Every statement that makes <paramref name="database" /> hold a workspace's tables, in order.
    /// </summary>
    /// <param name="database">
    ///     The workspace's database, <c>ws_{guid:N}</c> — <see cref="MonitorWorkspaces.Database" />.
    ///     ⚠ An identifier is interpolated, not bound, so anything else is refused.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="database" /> is not a workspace database's name.</exception>
    /// <remarks>
    ///     Every statement is <c>IF NOT EXISTS</c>, so running the list again is a no-op.
    /// </remarks>
    public static ImmutableArray<string> Statements(string database) {
        EnsureDatabase(database);

        return [
            $"CREATE DATABASE IF NOT EXISTS {database}",
            $"""
             CREATE TABLE IF NOT EXISTS {database}.{TracesTable}
             (
                 `Timestamp` DateTime64(9) CODEC(Delta(8), ZSTD(1)),
                 `TraceId` String CODEC(ZSTD(1)),
                 `SpanId` String CODEC(ZSTD(1)),
                 `ParentSpanId` String CODEC(ZSTD(1)),
                 `TraceState` String CODEC(ZSTD(1)),
                 `SpanName` LowCardinality(String) CODEC(ZSTD(1)),
                 `SpanKind` LowCardinality(String) CODEC(ZSTD(1)),
                 `ServiceName` LowCardinality(String) CODEC(ZSTD(1)),
                 `ResourceAttributes` Map(LowCardinality(String), String) CODEC(ZSTD(1)),
                 `ScopeName` String CODEC(ZSTD(1)),
                 `ScopeVersion` String CODEC(ZSTD(1)),
                 `SpanAttributes` Map(LowCardinality(String), String) CODEC(ZSTD(1)),
                 `Duration` UInt64 CODEC(ZSTD(1)),
                 `StatusCode` LowCardinality(String) CODEC(ZSTD(1)),
                 `StatusMessage` String CODEC(ZSTD(1)),
                 `Events.Timestamp` Array(DateTime64(9)) CODEC(ZSTD(1)),
                 `Events.Name` Array(LowCardinality(String)) CODEC(ZSTD(1)),
                 `Events.Attributes` Array(Map(LowCardinality(String), String)) CODEC(ZSTD(1)),
                 `Links.TraceId` Array(String) CODEC(ZSTD(1)),
                 `Links.SpanId` Array(String) CODEC(ZSTD(1)),
                 `Links.TraceState` Array(String) CODEC(ZSTD(1)),
                 `Links.Attributes` Array(Map(LowCardinality(String), String)) CODEC(ZSTD(1)),
                 INDEX idx_trace_id TraceId TYPE bloom_filter(0.001) GRANULARITY 1,
                 INDEX idx_res_attr_key mapKeys(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_res_attr_value mapValues(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_span_attr_key mapKeys(SpanAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_span_attr_value mapValues(SpanAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_duration Duration TYPE minmax GRANULARITY 1
             )
             ENGINE = MergeTree
             PARTITION BY toDate(Timestamp)
             ORDER BY (ServiceName, SpanName, toDateTime(Timestamp))
             SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
             """,
            $"""
             CREATE TABLE IF NOT EXISTS {database}.{TraceIdTable}
             (
                 `TraceId` String CODEC(ZSTD(1)),
                 `Start` DateTime CODEC(Delta(4), ZSTD(1)),
                 `End` DateTime CODEC(Delta(4), ZSTD(1)),
                 INDEX idx_trace_id TraceId TYPE bloom_filter(0.01) GRANULARITY 1
             )
             ENGINE = MergeTree
             PARTITION BY toDate(Start)
             ORDER BY (TraceId, Start)
             SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
             """,
            $"""
             CREATE MATERIALIZED VIEW IF NOT EXISTS {database}.{TraceIdViewName} TO {database}.{TraceIdTable}
             (
                 `TraceId` String,
                 `Start` DateTime64(9),
                 `End` DateTime64(9)
             )
             AS SELECT
                 TraceId,
                 min(Timestamp) AS Start,
                 max(Timestamp) AS End
             FROM {database}.{TracesTable}
             WHERE TraceId != ''
             GROUP BY TraceId
             """,
            $"""
             CREATE TABLE IF NOT EXISTS {database}.{LogsTable}
             (
                 `Timestamp` DateTime64(9) COMMENT 'Event timestamp with nanosecond precision' CODEC(Delta(8), ZSTD(1)),
                 `TraceId` String COMMENT 'W3C trace identifier' CODEC(ZSTD(1)),
                 `SpanId` String COMMENT 'W3C span identifier' CODEC(ZSTD(1)),
                 `TraceFlags` UInt8 COMMENT 'W3C trace flags',
                 `SeverityText` LowCardinality(String) COMMENT 'Log severity as text' CODEC(ZSTD(1)),
                 `SeverityNumber` UInt8 COMMENT 'Log severity as number (1-24)',
                 `ServiceName` LowCardinality(String) COMMENT 'Service that emitted the log' CODEC(ZSTD(1)),
                 `Body` String COMMENT 'Log message body' CODEC(ZSTD(1)),
                 `ResourceSchemaUrl` LowCardinality(String) COMMENT 'Schema URL for the resource' CODEC(ZSTD(1)),
                 `ResourceAttributes` Map(LowCardinality(String), String) COMMENT 'Resource attributes as key-value pairs' CODEC(ZSTD(1)),
                 `ScopeSchemaUrl` LowCardinality(String) COMMENT 'Schema URL for the instrumentation scope' CODEC(ZSTD(1)),
                 `ScopeName` String COMMENT 'Instrumentation scope name' CODEC(ZSTD(1)),
                 `ScopeVersion` LowCardinality(String) COMMENT 'Instrumentation scope version' CODEC(ZSTD(1)),
                 `ScopeAttributes` Map(LowCardinality(String), String) COMMENT 'Instrumentation scope attributes' CODEC(ZSTD(1)),
                 `LogAttributes` Map(LowCardinality(String), String) COMMENT 'Log record attributes' CODEC(ZSTD(1)),
                 `EventName` String COMMENT 'Event name for log records representing events' CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.cluster.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.cluster.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.container.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.container.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.deployment.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.deployment.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.namespace.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.namespace.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.node.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.node.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.pod.name` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.pod.name'] CODEC(ZSTD(1)),
                 `__otel_materialized_k8s.pod.uid` LowCardinality(String) MATERIALIZED ResourceAttributes['k8s.pod.uid'] CODEC(ZSTD(1)),
                 `__otel_materialized_deployment.environment.name` LowCardinality(String) MATERIALIZED ResourceAttributes['deployment.environment.name'] CODEC(ZSTD(1)),
                 INDEX idx_trace_id TraceId TYPE bloom_filter(0.001) GRANULARITY 1,
                 INDEX idx_res_attr_key mapKeys(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_res_attr_value mapValues(ResourceAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_scope_attr_key mapKeys(ScopeAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_scope_attr_value mapValues(ScopeAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_log_attr_key mapKeys(LogAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_log_attr_value mapValues(LogAttributes) TYPE bloom_filter(0.01) GRANULARITY 1,
                 INDEX idx_lower_body lower(Body) TYPE tokenbf_v1(32768, 3, 0) GRANULARITY 8
             )
             ENGINE = MergeTree
             PARTITION BY toDate(Timestamp)
             ORDER BY (toStartOfFiveMinutes(Timestamp), ServiceName, Timestamp)
             SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
             """
        ];
    }

    /// <summary>Whether a name is a workspace database's — <c>ws_</c> and 32 lower-case hex digits.</summary>
    /// <param name="database">The name.</param>
    public static bool IsWorkspaceDatabase(string database) =>
        database is not null && WorkspaceDatabase().IsMatch(database);

    /// <summary>Refuses anything that is not a workspace database's name.</summary>
    /// <param name="database">The name.</param>
    /// <exception cref="ArgumentException">It is not <c>ws_</c> and 32 lower-case hex digits.</exception>
    public static void EnsureDatabase(string database) {
        if (!IsWorkspaceDatabase(database)) {
            throw new ArgumentException(
                $"'{database}' is not a workspace database. A workspace's database is 'ws_' and the "
                + "workspace's GUID as 32 lower-case hex digits (MonitorWorkspaces.Database), and it is "
                + "interpolated into a statement as an identifier, so nothing else is accepted.",
                nameof(database)
            );
        }
    }

    [GeneratedRegex("^ws_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceDatabase();
}
