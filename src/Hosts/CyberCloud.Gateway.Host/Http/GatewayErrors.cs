namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The errors the gateway itself produces, and the one canonical <c>404</c>.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="NotFound" /> is the enumeration-oracle defence and it is a single string on
///     purpose.</b> docs/plan/07 § The enforcement seam gets the status code right — <i>"404, never
///     403"</i> — and stops there. A status code that matches while the two bodies differ is the same
///     oracle wearing a different hat: <c>'…' does not exist</c> against <c>you may not read '…'</c>
///     tells a prober exactly what the 404 was meant to hide. Every <c>ResourceNotFound</c> leaving
///     this process is re-rendered through this method, whatever message the component below wrote —
///     see <c>ResultShaper.Shape</c>.
/// </remarks>
static class GatewayErrors {
    /// <summary>
    ///     The one <c>404</c> body. Identical for a resource that is absent, one the caller may not
    ///     read, one in another tenant, and one whose api-version retired the type.
    /// </summary>
    /// <param name="path">
    ///     The address the caller asked for, echoed back so the response is diagnosable. ⚠ The
    ///     caller's own input and nothing else — anything derived from what the gateway <i>found</i>
    ///     would leak the thing the status code hides.
    /// </param>
    public static Error NotFound(string path) =>
        new(ErrorCode.ResourceNotFound, $"'{path}' does not exist.");

    /// <summary>The <c>401</c>. No token, an expired one, or one this platform did not issue.</summary>
    /// <param name="reason">
    ///     Why, in terms of the <i>request</i> — "no Authorization header", "the token has expired".
    ///     ⚠ Never in terms of the token's contents: "tenant 9f3… is unknown" confirms a tenant id.
    /// </param>
    public static Error Unauthenticated(string reason) =>
        new(
            ErrorCode.AuthorizationFailed,
            $"The request is not authenticated: {reason}. docs/plan/10 § Authentication inputs — "
            + "every caller presents a bearer token scoped to one tenant, and tokens live 10 minutes."
        );

    /// <summary>The <c>400</c> for a missing or unknown <c>api-version</c>.</summary>
    /// <param name="supplied">What the caller sent, or empty when the parameter was absent.</param>
    /// <param name="current">
    ///     The current version, named in the message. docs/plan/10 § API versioning: <i>"Missing →
    ///     400 naming the current version."</i> Naming it is what turns a 400 into a one-line fix.
    /// </param>
    public static Error ApiVersionRequired(string supplied, string current) =>
        new(
            ErrorCode.InvalidApiVersion,
            supplied.Length == 0
                ? $"The 'api-version' query parameter is required on every request. The current "
                + $"version is '{current}'. docs/plan/10 § API versioning."
                : $"'{supplied}' is not an api-version this platform serves. The current version is "
                + $"'{current}'. Versions are dates and are immutable, so a version that once worked "
                + "keeps working — docs/plan/10 § API versioning.",
            null
        );

    /// <summary>The <c>429</c>. Names the bucket, the limit and the window.</summary>
    /// <param name="bucket">Which of docs/plan/10 § Rate limiting's five buckets ran out.</param>
    /// <param name="limit">The bucket's limit.</param>
    /// <param name="windowSeconds">Its window, in seconds.</param>
    /// <param name="retryAfterSeconds">When the caller may try again.</param>
    public static Error RateLimited(string bucket, int limit, int windowSeconds, int retryAfterSeconds) =>
        new(
            ErrorCode.QuotaExceeded,
            $"Rate limit '{bucket}' exceeded: {limit} requests per {windowSeconds} seconds. Retry "
            + $"after {retryAfterSeconds} seconds. docs/plan/10 § Rate limiting."
        );
}
