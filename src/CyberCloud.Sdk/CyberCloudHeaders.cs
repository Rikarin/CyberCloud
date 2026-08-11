namespace CyberCloud.Sdk;

/// <summary>
///     The header names the gateway's request pipeline reads and writes — docs/plan/10 § Request
///     pipeline, stage 1.
/// </summary>
/// <remarks>
///     The two correlation names are not symmetrical and the asymmetry is the point.
///     <see cref="CorrelationRequestId" /> is Azure's spelling and travels <b>in</b>, because tooling
///     already sends it; <see cref="RequestId" /> is ours and comes <b>out</b>, because a support
///     conversation needs an identifier the platform minted rather than one the caller chose.
/// </remarks>
public static class CyberCloudHeaders {
    /// <summary>
    ///     The caller's correlation id, sent on every request by
    ///     <see cref="CorrelationRequestIdHandler" />. docs/plan/10 § Request pipeline: <i>"Azure's
    ///     header, because tooling already sends it"</i>.
    /// </summary>
    public const string CorrelationRequestId = "x-ms-correlation-request-id";

    /// <summary>
    ///     The platform's own request id, on every response and surfaced as
    ///     <see cref="Response.ServiceRequestId" />. docs/plan/08 § Errors puts it in a header
    ///     precisely so the error body never has to carry exception detail.
    /// </summary>
    public const string RequestId = "x-cybercloud-request-id";

    /// <summary>
    ///     The absolute URL of the operation to poll, on a <c>202</c>. docs/plan/10 § Long-running
    ///     operations, over HTTP.
    /// </summary>
    public const string AsyncOperation = "Azure-AsyncOperation";

    /// <summary>Seconds to wait before the next poll, or before retrying a <c>429</c>.</summary>
    public const string RetryAfter = "Retry-After";
}
