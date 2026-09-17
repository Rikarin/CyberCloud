using Microsoft.Extensions.Configuration;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The <c>CyberCloud:ResourceGraph</c> section: where the <c>resource-changed</c> stream is, and
///     where the projection lands.
/// </summary>
/// <remarks>
///     <para>
///         Two halves, and a host may configure one without the other. The <b>publisher</b> half is
///         the NATS URL, and it is what the gateway needs: <c>ResourceManagerService</c> emits from
///         the gateway's process (docs/plan/08 § The write path, end to end) and a gateway with no
///         URL keeps the resource manager's logging sink. The <b>projector</b> half adds the
///         ClickHouse endpoint, and only a silo consumes: <c>OrleansApplication.CreateSilo</c>'s
///         hosts run <see cref="ResourceGraphProjector" />, the gateway never does, because the
///         projector reads a tenant's <c>ICheckGrain</c> and reaching a grain from a client on every
///         event is a hop the silo does not pay.
///     </para>
///     <para>
///         ⚠ <b><see cref="NatsUrl" /> falls back to <c>ConnectionStrings:nats</c>, because that is
///         the key Aspire writes.</b> The AppHost hands the silos and the gateway the NATS resource
///         with <c>WithReference(nats)</c>, which renders as <c>ConnectionStrings__nats</c> and
///         nothing else; a section that had to be spelled a second time in the AppHost would be the
///         drift <c>CyberCloudResourceExtensions</c> exists to prevent. An explicit
///         <c>CyberCloud:ResourceGraph:NatsUrl</c> wins when both are present, which is what a chart
///         sets.
///     </para>
/// </remarks>
public sealed class ResourceGraphOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:ResourceGraph";

    /// <summary>The connection-string name Aspire's <c>WithReference(nats)</c> writes.</summary>
    public const string NatsConnectionStringName = "nats";

    /// <summary>The NATS server, <c>nats://host:4222</c>. Empty leaves the logging sink in place.</summary>
    public string NatsUrl { get; set; } = string.Empty;

    /// <summary>
    ///     The JetStream stream that holds <c>resource-changed</c>. One per cluster; the tenant is a
    ///     subject token, not a stream.
    /// </summary>
    public string Stream { get; set; } = "cc-resource-changed";

    /// <summary>
    ///     The durable consumer the projector pulls from. One name for the whole silo fleet, so a
    ///     message is projected once however many silos run.
    /// </summary>
    public string Consumer { get; set; } = "resource-graph";

    /// <summary>
    ///     How long the stream keeps an event. The projection is a materialized view, so the stream
    ///     is a replay buffer rather than the record: long enough to re-project after a ClickHouse
    ///     restore, not forever.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     The most a publish may hold the write path. ⚠ The connection buffers a publish while
    ///     disconnected and retries its first connect without limit, both right for the event and
    ///     wrong for the request waiting on it — this is the ceiling that keeps a NATS outage from
    ///     holding every <c>PUT</c> open.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     ClickHouse's HTTP interface, <c>http://host:8123</c>. Empty means this host does not
    ///     project.
    /// </summary>
    public string ClickHouseEndpoint { get; set; } = string.Empty;

    /// <summary>The ClickHouse user the projector connects as.</summary>
    public string ClickHouseUser { get; set; } = "default";

    /// <summary>Its password.</summary>
    public string ClickHousePassword { get; set; } = string.Empty;

    /// <summary>
    ///     Whether a plain-HTTP ClickHouse endpoint is acceptable. Off by default, following
    ///     <c>ObjectStorageOptions</c>: a production endpoint without TLS is a refusal, not a warning.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>The timeout on one ClickHouse request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether this host can publish — the gateway's question.</summary>
    public bool IsPublisherConfigured => NatsUrl.Length > 0;

    /// <summary>Whether this host can project — the silo's question.</summary>
    public bool IsProjectorConfigured => IsPublisherConfigured && ClickHouseEndpoint.Length > 0;

    /// <summary>
    ///     Binds the section, then fills an empty <see cref="NatsUrl" /> from
    ///     <c>ConnectionStrings:nats</c>.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    public static ResourceGraphOptions Bind(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ResourceGraphOptions();
        configuration.GetSection(SectionName).Bind(options);

        if (options.NatsUrl.Length == 0) {
            options.NatsUrl = configuration.GetConnectionString(NatsConnectionStringName) ?? string.Empty;
        }

        return options;
    }
}
