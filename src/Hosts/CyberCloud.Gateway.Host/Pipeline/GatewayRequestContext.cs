using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     What one request has accumulated so far. Each stage reads what the ones before it wrote.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Caller" /> is the only tenant in here, and it is written exactly once, by
///     stage 3, from the token.</b> Nothing later re-derives it and nothing earlier can. That is the
///     property docs/plan/00 § The tenant-separation row, corrected leaves the gateway responsible
///     for: an Orleans client is outside <c>Orleans.Multitenant</c>'s call filter permanently, so
///     the value in this field <i>is</i> the tenancy boundary for every request that arrives over
///     HTTP.
/// </remarks>
sealed class GatewayRequestContext(HttpContext http) {
    /// <summary>The ASP.NET Core request. ⚠ Read for its inputs; never written to before stage 9.</summary>
    public HttpContext Http { get; } = http;

    /// <summary>The correlation id, from the caller or minted here. Stage 1.</summary>
    public string CorrelationId { get; set; } = "";

    /// <summary>This gateway's own id for the request, always minted here. Stage 1.</summary>
    public string RequestId { get; set; } = "";

    /// <summary>Who is asking, and for which tenant. Stage 3 — and only stage 3.</summary>
    public CallerContext Caller { get; set; } = new();

    /// <summary>The tenant's directory entry, for the region decision. Stage 3.</summary>
    public TenantDirectoryEntry? Tenant { get; set; }

    /// <summary>Which rate-limiting regime the request falls under. Stage 5.</summary>
    public RequestClass RequestClass { get; set; }

    /// <summary>The headers stage 5 produced, carried onto every response including successes.</summary>
    public IReadOnlyList<ResponseHeader> RateLimitHeaders { get; set; } = [];

    /// <summary>What the path names. Stage 6.</summary>
    public GatewayRoute Route { get; set; } = GatewayRoute.None;

    /// <summary>The resolved api-version. Stage 6.</summary>
    public ApiVersion ApiVersion { get; set; }

    /// <summary>The body, as text. Read once, at stage 3, and reused by 7 and 8.</summary>
    /// <remarks>
    ///     ⚠ Read at stage 3 rather than at 7 because the tenant check has to see it: <c>tenantId</c>
    ///     in a body is one of the surfaces a caller can put a tenant id into, and a check that ran
    ///     after routing would be checking a request that had already selected a provider.
    /// </remarks>
    public string Body { get; set; } = "";

    /// <summary>The stages entered, in order.</summary>
    public GatewayTraceBuilder Trace { get; } = new();

    /// <summary>The trace as a value, for a test and for the log line.</summary>
    public GatewayTrace Snapshot() => Trace.Build();
}
