namespace CyberCloud.Providers.Monitor.Query;

/// <summary>
///     Where the region's telemetry stores answer reads, and the budget one query may spend there.
///     Bound from <see cref="SectionName" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The endpoints are the deployment's and never the request's.</b> docs/plan/16 routes a
///         workspace to coordinates <i>inside</i> one store per region, so the store is a fact about
///         the region and the coordinate is a fact about the workspace — the first comes from here,
///         the second from the workspace's GUID. No body member, header or query-string key reaches
///         either, which is what makes the upstream URL something a caller cannot choose.
///     </para>
///     <para>
///         ⚠ <b>Unconfigured is a supported shape, and it refuses by name.</b> A host with neither
///         endpoint keeps the refusing store, whose sentence names this section — the same contract
///         <c>UnavailableAlertQuerySeam</c> and the vault's resolver keep. An empty answer would read
///         as "your workspace has no data", which is the one lie a monitoring product may not tell.
///     </para>
/// </remarks>
public sealed class MonitorQueryOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Monitor:Query";

    /// <summary>The placeholder <see cref="MetricsEndpoint" /> may carry for the workspace's metrics tier.</summary>
    public const string TierPlaceholder = "{tier}";

    /// <summary>
    ///     vmselect's base URL — <c>http(s)://host:8481</c> — with <see cref="TierPlaceholder" />
    ///     where the tier's <c>VMCluster</c> name goes, or without it for a region with one cluster.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A template, because a workspace's metrics live in its tier's cluster.</b>
    ///     <see cref="MonitorWorkspaces.MetricsClusterName" /> puts each retention tier in its own
    ///     <c>VMCluster</c>, and the operator names that cluster's vmselect Service
    ///     <c>vmselect-{cluster}</c>, so <c>https://vmselect-telemetry-{tier}.cybercloud-telemetry.svc:8481</c>
    ///     reaches the right one for every tier. ⚠ A workspace that moved tier reads the new tier's
    ///     cluster only: the samples written before the move stay where they were written, which
    ///     <see cref="MonitorWorkspaces.MetricsClusterName" />'s remarks already say of the write half.
    ///     ⚠ <b>https, and the operator doesn't give vmselect TLS by default.</b> Its Service serves
    ///     plain http unless the <c>VMCluster</c> passes vmselect <c>-tls</c> with a certificate, and
    ///     <see cref="Validate" /> refuses a plain <c>http</c> endpoint without
    ///     <see cref="AllowInsecureTransport" />. So a region needs that certificate before this
    ///     endpoint works, and nothing in the bundle issues it yet —
    ///     <c>charts/managed/monitor-workspace/conformance.yaml § owed</c>,
    ///     <c>explorers-are-wired-on-a-laptop-only</c>.
    /// </remarks>
    public string MetricsEndpoint { get; set; } = string.Empty;

    /// <summary>ClickHouse's HTTP interface — <c>http(s)://host:8123</c> — for the logs.</summary>
    public string LogsEndpoint { get; set; } = string.Empty;

    /// <summary>The ClickHouse user a search runs as.</summary>
    /// <remarks>
    ///     <para>
    ///         It should hold <c>SELECT</c> on the workspace databases and nothing else:
    ///         <c>CREATE USER … SETTINGS readonly = 2</c> and <c>GRANT SELECT ON ws_*.* TO …</c>, which
    ///         ClickHouse 25.3 takes as a wildcard grant. <c>MonitorQueryFixture</c> runs every search in
    ///         <c>MonitorQueryOverHttpTests</c> as that user, so the grant is known to be enough.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>readonly = 2</c>, not the conventional read-only profile's <c>readonly = 1</c>.</b>
    ///         <see cref="ClickHouseLogStore" /> sets <c>readonly</c>, <c>max_execution_time</c> and
    ///         <c>max_rows_to_read</c> on every statement, and a <c>readonly = 1</c> user may change no
    ///         setting at all: ClickHouse answers code 164, <i>"Cannot modify 'readonly' setting in
    ///         readonly mode"</i>, and every search fails with <see cref="ClickHouseLogStore.FailedSentence" />.
    ///         <c>MonitorQueryOverHttpTests.AReadonlyOneUserCanRunNoSearchBecauseTheStoreSetsItsBudgetPerStatement</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ Nothing provisions that user yet. The AppHost hands the gateway the region's admin
    ///         credential — <c>charts/managed/monitor-workspace/conformance.yaml § owed</c>,
    ///         <c>log-search-runs-as-the-clickhouse-admin</c>.
    ///     </para>
    /// </remarks>
    public string LogsUser { get; set; } = "default";

    /// <summary>That user's password.</summary>
    public string LogsPassword { get; set; } = string.Empty;

    /// <summary>Whether plain <c>http</c> endpoints are accepted — for a container on a laptop, never a region.</summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>
    ///     How long one query may run before the store is told to stop and the caller is told why. A log
    ///     search's two statements share it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Under <c>ReconcileDriver.PassBudget</c>, which bounds every synchronous action, so the store's
    ///     own timeout fires first and the caller reads a sentence about their query rather than one
    ///     about a handler that did not return.
    /// </remarks>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The rows one log search may read, its rows statement and its histogram together — ClickHouse's
    ///     <c>max_rows_to_read</c>, split between the two by what the first one read.
    /// </summary>
    public long LogsMaxRowsToRead { get; set; } = 50_000_000;

    /// <summary>The largest response body read from either store, in bytes.</summary>
    /// <remarks>
    ///     ⚠ The series cap bounds what is <i>returned</i>; this bounds what is <i>read</i>, because
    ///     VictoriaMetrics has no per-request series limit on a range query and answers every series
    ///     that matched before the handler can count them.
    /// </remarks>
    public long MaxResponseBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Whether the metrics half is wired.</summary>
    public bool IsMetricsConfigured => MetricsEndpoint.Length > 0;

    /// <summary>Whether the logs half is wired.</summary>
    public bool IsLogsConfigured => LogsEndpoint.Length > 0;

    /// <summary>The vmselect base URL for one tier.</summary>
    /// <param name="tier">One of <see cref="MonitorWorkspaces.Tiers" />.</param>
    public Uri MetricsEndpointFor(string tier) {
        ArgumentException.ThrowIfNullOrEmpty(tier);

        return Validated(
            MetricsEndpoint.Replace(TierPlaceholder, tier, StringComparison.Ordinal),
            nameof(MetricsEndpoint)
        );
    }

    /// <summary>The ClickHouse base URL.</summary>
    public Uri LogsEndpointUri() => Validated(LogsEndpoint, nameof(LogsEndpoint));

    /// <summary>Appends a store path, or a query string, to an endpoint without dropping the endpoint's own path.</summary>
    /// <param name="endpoint">A validated endpoint: absolute, with no query string and no fragment.</param>
    /// <param name="relative">
    ///     A path with no leading slash, such as <c>select/7/prometheus/api/v1/query</c>, or a query
    ///     string starting with <c>?</c>.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Resolved against the endpoint as a DIRECTORY.</b> A store behind an ingress, vmauth or a
    ///     proxy path is configured as <c>https://gateway/vm</c>, and <c>new Uri(endpoint, "/select/…")</c>
    ///     — what both stores did until #41's review — replaced <c>/vm</c> rather than extending it,
    ///     so every query went to the proxy's root. The trailing slash is added when it's missing, and
    ///     the relative part never starts with one.
    /// </remarks>
    public static Uri Resolve(Uri endpoint, string relative) {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(relative);

        var directory = endpoint.AbsolutePath.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");

        return new(directory, relative.TrimStart('/'));
    }

    /// <summary>Checks every configured endpoint, so a bad one fails the host's start and not a query.</summary>
    /// <exception cref="ArgumentException">
    ///     An endpoint is not an absolute http(s) URI, carries a query string or a fragment, or is http
    ///     without <see cref="AllowInsecureTransport" />.
    /// </exception>
    public void Validate() {
        if (IsMetricsConfigured) {
            _ = MetricsEndpointFor(MonitorWorkspaces.DefaultTier);
        }

        if (IsLogsConfigured) {
            _ = LogsEndpointUri();
        }

        if (QueryTimeout <= TimeSpan.Zero) {
            throw new ArgumentException($"{SectionName}:QueryTimeout must be positive.", nameof(QueryTimeout));
        }
    }

    Uri Validated(string value, string key) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            throw new ArgumentException(
                $"{SectionName}:{key} '{value}' is not an absolute http(s) URI.",
                key
            );
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) {
            throw new ArgumentException(
                $"{SectionName}:{key} '{value}' carries a query string or a fragment, and the stores build "
                + "their own query strings. Configure a scheme, a host, a port and, behind a proxy, a path.",
                key
            );
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureTransport) {
            throw new ArgumentException(
                SectionName + ":" + key + " '" + value + "' is plain HTTP. Set " + SectionName
                + ":AllowInsecureTransport for a container on a laptop; a region's stores have TLS.",
                key
            );
        }

        return uri;
    }
}
