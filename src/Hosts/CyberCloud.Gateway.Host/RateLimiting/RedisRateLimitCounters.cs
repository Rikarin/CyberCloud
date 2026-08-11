using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Globalization;

namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     The sliding window of docs/plan/10 § Request pipeline, as one Lua script per request.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One script, not four commands.</b> Trim, add, count and expire have to be one atomic
///         unit or two concurrent requests interleave between the count and the add and both are
///         admitted — which at the limit is exactly when it matters. A <c>MULTI</c> would also be
///         atomic but cannot return the count to the same round trip, so the limiter would cost two.
///     </para>
///     <para>
///         ⚠ <b>Every key is hash-tagged, and on Redis Cluster that is not optional.</b> The script
///         touches one key, so it runs on whichever node owns it; the tag keeps a tenant's five
///         buckets on one node, which keeps the cross-slot surface at zero — docs/plan/05 § Hot.
///     </para>
///     <para>
///         ⚠ <b>It fails OPEN, and the direction is a decision rather than an oversight.</b> Redis
///         being unreachable is a platform fault, and failing closed converts it into a total outage
///         of the API for every tenant — a rate limiter is a protection against abuse, not a
///         correctness control, and nothing below it depends on it having run. Every failure is
///         logged at warning with the key, so "the limiter was down" is answerable after the fact
///         rather than inferred from a bill.
///     </para>
/// </remarks>
sealed class RedisRateLimitCounters(
    IConnectionMultiplexer redis,
    IClock clock,
    ILogger<RedisRateLimitCounters> logger
)
    : IRateLimitCounters {
    /// <summary>
    ///     Trim the window, add this request, count, and set the key to expire one window from now.
    /// </summary>
    /// <remarks>
    ///     <c>ARGV[1]</c> is now in milliseconds, <c>ARGV[2]</c> the window in milliseconds and
    ///     <c>ARGV[3]</c> a unique member. The member has to be unique per request — two requests in
    ///     the same millisecond would otherwise be one <c>ZADD</c> and the second would be free.
    ///     Returns the count and the oldest score still in the window, which is what makes
    ///     <c>Retry-After</c> honest.
    /// </remarks>
    const string SlidingWindowScript = """
        local now = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)
        redis.call('ZADD', KEYS[1], now, ARGV[3])
        redis.call('PEXPIRE', KEYS[1], window)
        local count = redis.call('ZCARD', KEYS[1])
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        local first = now
        if oldest[2] then first = tonumber(oldest[2]) end
        return { count, first }
        """;

    /// <inheritdoc />
    public async Task<WindowCount> CountAsync(
        string key,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var windowMilliseconds = (long)window.TotalMilliseconds;

        try {
            var result = (RedisResult[]?)await redis.GetDatabase().ScriptEvaluateAsync(
                SlidingWindowScript,
                [key],
                [
                    now.ToString(CultureInfo.InvariantCulture),
                    windowMilliseconds.ToString(CultureInfo.InvariantCulture),
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                ]
            );

            if (result is not [var count, var oldest]) {
                return new(1, TimeSpan.Zero);
            }

            var expiresIn = (long)oldest + windowMilliseconds - now;

            return new((long)count, TimeSpan.FromMilliseconds(Math.Max(expiresIn, 0)));
        }
        catch (RedisException exception) {
            logger.LogWarning(
                exception,
                "Rate-limit counter '{Key}' could not be updated; the request is admitted. "
                + "docs/plan/10 § Rate limiting — the limiter fails open, so a Redis outage costs "
                + "protection rather than availability.",
                key
            );

            return new(1, TimeSpan.Zero);
        }
    }
}
