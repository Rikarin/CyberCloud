namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     Every header name the gateway reads or writes, in one place.
/// </summary>
/// <remarks>
///     ⚠ <b>The Azure spellings are deliberate and are not ours to improve.</b> docs/plan/10
///     § Request pipeline takes <c>x-ms-correlation-request-id</c> <i>"because tooling already sends
///     it"</i>, and docs/plan/10 § Rate limiting keeps <c>Retry-After</c> and
///     <c>x-ms-ratelimit-remaining-*</c> <i>"because every cloud SDK's retry policy already
///     understands those headers"</i>. A prettier name here costs every SDK its retry behaviour.
/// </remarks>
static class GatewayHeaders {
    /// <summary>The correlation id a caller supplies. Azure's name.</summary>
    public const string CorrelationRequestId = "x-ms-correlation-request-id";

    /// <summary>The id this gateway assigns, on every response including errors.</summary>
    public const string RequestId = "x-cybercloud-request-id";

    /// <summary>The <c>202</c> polling URL. docs/plan/10 § Long-running operations.</summary>
    public const string AsyncOperation = "Azure-AsyncOperation";

    /// <summary>Seconds before the next poll, and seconds until a rate limit clears.</summary>
    public const string RetryAfter = "Retry-After";

    /// <summary>
    ///     Sent as <c>no-store</c> on an action's response. docs/plan/08 § The provider registry makes
    ///     a <c>secret: true</c> action's response never cached.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sent on every synchronous action, not only the secret ones.</b> Which actions carry
    ///     secret material is a per-provider declaration, and a header applied per declaration is one
    ///     somebody forgets when they add the thirteenth action. An action does work or reads
    ///     something live; neither is worth caching, so there is nothing to trade away.
    /// </remarks>
    public const string CacheControl = "Cache-Control";

    /// <summary>How many reads the subscription has left in the window.</summary>
    public const string RemainingSubscriptionReads = "x-ms-ratelimit-remaining-subscription-reads";

    /// <summary>How many writes the subscription has left in the window.</summary>
    public const string RemainingSubscriptionWrites = "x-ms-ratelimit-remaining-subscription-writes";

    /// <summary>How many requests the tenant has left in the window.</summary>
    public const string RemainingTenantTotal = "x-ms-ratelimit-remaining-tenant-total";

    /// <summary>How many interactive requests the user has left in the window.</summary>
    public const string RemainingUserInteractive = "x-ms-ratelimit-remaining-user-interactive";

    /// <summary>
    ///     Stamped by a gateway that has already proxied this request once.
    /// </summary>
    /// <remarks>
    ///     ⚠ This header is what makes <i>"One hop, never two"</i> (docs/plan/10 § Request pipeline)
    ///     enforceable rather than aspirational. A directory that disagrees with itself across two
    ///     regions would otherwise bounce a request between them until something timed out, and the
    ///     symptom — latency, not an error — is the kind that gets diagnosed for a week.
    /// </remarks>
    public const string ForwardedByRegion = "x-cybercloud-forwarded-by-region";

    /// <summary>
    ///     A tenant id in a request header. ⚠ <b>Read only to refuse it.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/10 § Request pipeline: the tenant comes from the token's <c>tid</c> and
    ///     <i>"never from the path, the body or a header"</i>. The gateway looks at this header for
    ///     one purpose — to answer <c>404</c> when it disagrees with the token — and never to decide
    ///     anything. See <c>TenantSmugglingCheck</c>.
    /// </remarks>
    public const string TenantIdHint = "x-ms-tenant-id";
}
