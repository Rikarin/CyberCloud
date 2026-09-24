namespace CyberCloud.Providers.Monitor.Telemetry;

/// <summary>
///     Where the region's telemetry ClickHouse answers the views, and how much one view may spend.
///     Bound from <see cref="SectionName" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             One platform credential for every workspace, and the tenancy is the database the
///             view chooses rather than the user it connects as.
///         </b> The view path derives the database from the workspace's GUID in the platform's
///         index (<c>ActionContext.Parent</c>) and nothing a caller sends can name another. What the
///         credential should be allowed is <c>SELECT</c> on <c>ws_*</c> and nothing else; the store
///         also sets <c>readonly=2</c> on every request, so a defect in a statement cannot become a
///         write. Per-workspace users, so the store itself refuses a wrong database, are
///         <c>charts/managed/monitor-component/conformance.yaml § owed</c>, <c>one-read-credential-for-every-workspace</c>.
///     </para>
///     <para>
///         ⚠ <b>Empty <see cref="ClickHouseEndpoint" /> keeps the refusing default</b>, which is
///         every host until a deployment sets the section — the same arrangement
///         <c>CyberCloud:ResourceGraph</c> has.
///     </para>
/// </remarks>
public sealed class MonitorTelemetryOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Monitor:Telemetry";

    /// <summary>ClickHouse's HTTP interface, <c>https://host:8443</c>. Empty for none.</summary>
    public string ClickHouseEndpoint { get; set; } = string.Empty;

    /// <summary>The platform's read user.</summary>
    public string ClickHouseUser { get; set; } = string.Empty;

    /// <summary>Its password.</summary>
    public string ClickHousePassword { get; set; } = string.Empty;

    /// <summary>Whether a plain <c>http</c> endpoint is accepted — a container on a laptop, never a region.</summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>How long one view's statement may run — ClickHouse's <c>max_execution_time</c>.</summary>
    /// <remarks>
    ///     ⚠ Under the action path's own budget (<c>ReconcileDriver.PassBudget</c>), with room for the
    ///     application map's two statements, so a slow view is ClickHouse's refusal naming the budget
    ///     rather than the dispatcher abandoning the request with nothing to say.
    /// </remarks>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most rows one statement may read — <c>max_rows_to_read</c>.</summary>
    public long MaxRowsToRead { get; set; } = 50_000_000;

    /// <summary>The most memory one statement may hold — <c>max_memory_usage</c>, in bytes.</summary>
    /// <remarks>
    ///     The percentiles are exact, so they hold each group's durations; this is what bounds that,
    ///     and the look-back cap is what keeps an ordinary view well inside it.
    /// </remarks>
    public long MaxMemoryBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Whether an endpoint is set.</summary>
    public bool IsConfigured => ClickHouseEndpoint.Length > 0;
}
