using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     Whether a request may proceed, and what to tell the caller either way.
/// </summary>
/// <param name="Allowed">Whether the request is inside every bucket that applies to it.</param>
/// <param name="Bucket">The bucket that refused, when <paramref name="Allowed" /> is false.</param>
/// <param name="RetryAfterSeconds">The <c>Retry-After</c>, rounded up to at least one second.</param>
/// <param name="Remaining">
///     The <c>x-ms-ratelimit-remaining-*</c> headers, on <b>every</b> response and not only on a
///     <c>429</c>. A client that only learns its budget after exceeding it cannot pace itself, which
///     is what those headers are for.
/// </param>
readonly record struct RateLimitDecision(
    bool Allowed,
    RateLimitBucket Bucket,
    int RetryAfterSeconds,
    ImmutableArray<ResponseHeader> Remaining
);

/// <summary>
///     Stage 5. docs/plan/10 § Rate limiting, all five buckets, over Redis counters.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Which buckets apply depends on the request class, and the exemption is the part that
///         is routinely got wrong.</b> docs/plan/10 § Rate limiting: <i>"Long-poll and SignalR are
///         exempt from the request-count limits and get a concurrency limit instead (connections per
///         tenant, streams per connection). Counting a 30-second long-poll as one request against a
///         5-minute window is how you accidentally rate-limit your own portal."</i> A portal tab
///         holding one long-poll open for the whole window spends its budget doing nothing, and the
///         symptom — the portal throttling itself while idle — reads as a platform fault.
///     </para>
///     <para>
///         ⚠ <b>Every key is built from the token.</b> The subscription id comes from the path, but
///         the tenant does not, and the key carries both — so a caller cannot spend another tenant's
///         budget, and cannot dodge their own by rewriting a segment.
///     </para>
///     <para>
///         ⚠ <b>DEFECT IN docs/plan/10 § Rate limiting, recorded here because it changes what this
///         bucket can do.</b> The table lists <i>per IP, unauthenticated — 60/min</i> for
///         <i>"sign-in, token, discovery"</i>, but docs/plan/10 § Request pipeline puts
///         authentication at stage 2 and rate limiting at stage 5. A request with a bad token is
///         therefore refused before it is ever counted, so on any route that requires a token the
///         bucket is unreachable by construction. It is reachable here only for the two anonymous
///         routes of docs/plan/10 § Shape (see <c>AuthenticateStage.IsAnonymous</c>) — and the
///         bucket's own rationale names sign-in and token, which docs/plan/10 § Request pipeline
///         puts on the <i>identity host</i>, not here. The row probably belongs to that host and to
///         Envoy's per-IP shed rather than to this table.
///     </para>
/// </remarks>
sealed class GatewayRateLimiter(IRateLimitCounters counters) {
    /// <summary>Counts a request against every bucket that applies and decides.</summary>
    /// <param name="requestClass">Which regime the request falls under.</param>
    /// <param name="tenantId">The tenant, from the token.</param>
    /// <param name="subjectId">The subject, from the token. Empty for an unauthenticated request.</param>
    /// <param name="subscriptionId">The subscription from the path, or <see cref="Guid.Empty" />.</param>
    /// <param name="clientIp">The caller's address, for the unauthenticated bucket.</param>
    /// <param name="cancellationToken">Cancels the round trips.</param>
    /// <remarks>
    ///     ⚠ <b>Named <c>EvaluateAsync</c> rather than <c>CheckAsync</c> on purpose.</b> "Check" is
    ///     the authorization engine's word (docs/plan/07 § Check), and
    ///     <c>GatewayIsolationTests.NoGatewaySourceFileCallsAnAuthorizationEngine</c> reads this
    ///     project's source for it. Keeping the rate limiter off that word costs nothing and keeps
    ///     the isolation assertion exact instead of approximate.
    /// </remarks>
    public async Task<RateLimitDecision> EvaluateAsync(
        RequestClass requestClass,
        Guid tenantId,
        string subjectId,
        Guid subscriptionId,
        string clientIp,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Unauthenticated first and alone. An anonymous caller has no tenant to charge, and
        // charging them to a tenant they named would let anyone drain a stranger's budget.
        if (tenantId == Guid.Empty) {
            var perIp = await CountAsync(
                RateLimitBuckets.IpUnauthenticated,
                $"rl:{{ip:{clientIp}}}:unauth",
                cancellationToken
            );

            return Decide([perIp]);
        }

        // docs/plan/10 § Rate limiting — the two exempt classes get a concurrency limit instead, and
        // that limit lives on the connection rather than on the request; see IConcurrencyLimiter.
        if (requestClass is RequestClass.LongPoll or RequestClass.Hub) {
            return new(true, default, 0, []);
        }

        var tag = $"{{t:{tenantId:N}}}";
        var counted = new List<(RateLimitBucket Bucket, WindowCount Window)>(3);

        if (subscriptionId != Guid.Empty) {
            var bucket = requestClass == RequestClass.Write
                ? RateLimitBuckets.SubscriptionWrites
                : RateLimitBuckets.SubscriptionReads;

            counted.Add(await CountAsync(
                bucket,
                $"rl:{tag}:sub:{subscriptionId:N}:{(requestClass == RequestClass.Write ? "w" : "r")}",
                cancellationToken
            ));
        }

        counted.Add(await CountAsync(RateLimitBuckets.TenantTotal, $"rl:{tag}:total", cancellationToken));

        if (subjectId.Length > 0) {
            counted.Add(await CountAsync(
                RateLimitBuckets.UserInteractive,
                $"rl:{tag}:user:{subjectId}",
                cancellationToken
            ));
        }

        return Decide(counted);
    }

    async Task<(RateLimitBucket Bucket, WindowCount Window)> CountAsync(
        RateLimitBucket bucket,
        string key,
        CancellationToken cancellationToken
    ) =>
        (bucket, await counters.CountAsync(key, bucket.Window, cancellationToken));

    static RateLimitDecision Decide(List<(RateLimitBucket Bucket, WindowCount Window)> counted) {
        var headers = ImmutableArray.CreateBuilder<ResponseHeader>(counted.Count);

        foreach (var (bucket, window) in counted) {
            if (bucket.RemainingHeader.Length == 0) {
                continue;
            }

            headers.Add(new(
                bucket.RemainingHeader,
                Math.Max(bucket.Limit - window.Count, 0).ToString(CultureInfo.InvariantCulture)
            ));
        }

        foreach (var (bucket, window) in counted) {
            if (window.Count <= bucket.Limit) {
                continue;
            }

            // At least one second: a Retry-After of 0 is an invitation to retry immediately, which
            // is how a throttled client turns into a busier one.
            var seconds = Math.Max((int)Math.Ceiling(window.RetryAfter.TotalSeconds), 1);

            return new(false, bucket, seconds, headers.ToImmutable());
        }

        return new(true, default, 0, headers.ToImmutable());
    }

    /// <summary>Builds the <c>429</c> outcome for a refused request.</summary>
    /// <param name="decision">The refusal.</param>
    public static GatewayOutcome TooManyRequests(RateLimitDecision decision) {
        var outcome = GatewayOutcome.Failure(
            StatusCodes.Status429TooManyRequests,
            GatewayErrors.RateLimited(
                decision.Bucket.Name,
                decision.Bucket.Limit,
                (int)decision.Bucket.Window.TotalSeconds,
                decision.RetryAfterSeconds
            )
        );

        return outcome with {
            Headers = decision.Remaining.Add(new(
                GatewayHeaders.RetryAfter,
                decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture)
            ))
        };
    }
}
