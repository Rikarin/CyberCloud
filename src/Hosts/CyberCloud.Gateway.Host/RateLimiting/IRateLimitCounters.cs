namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     What one window's worth of counting produced.
/// </summary>
/// <param name="Count">How many requests are inside the window, this one included.</param>
/// <param name="RetryAfter">
///     How long until the oldest request in the window falls out of it — the honest
///     <c>Retry-After</c>. ⚠ Not the whole window: telling a caller to wait five minutes when the
///     budget frees in four seconds is how a well-behaved SDK is turned into a stalled one.
/// </param>
readonly record struct WindowCount(long Count, TimeSpan RetryAfter);

/// <summary>
///     The counters behind stage 5. docs/plan/10 § Request pipeline: <i>"Redis-backed sliding
///     window."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOTHING BEHIND THIS INTERFACE MAY TOUCH A GRAIN.</b> docs/plan/10 § Request pipeline
///         states the reason in one line — <i>"a rate limiter that costs a grain call is a rate
///         limiter that amplifies an attack"</i> — and it is worth spelling out, because the
///         implementation that gets this wrong looks perfectly reasonable. A per-subscription counter
///         held in a subscription grain would mean a flood against one subscription activates that
///         grain, serialises every request in the flood through its single-threaded turn, and holds
///         a silo's scheduler while doing it. The rate limiter would become the amplifier: the
///         cheapest possible request for the attacker, the most expensive possible for the platform.
///         <c>RateLimitingTests.AFloodPastTheLimitCostsNoGrainCall</c> floods past the limit against
///         a grain factory that fails the test if it is touched at all.
///     </para>
///     <para>
///         The Redis implementation is a sorted set per key and one Lua script per request:
///         <c>ZREMRANGEBYSCORE</c>, <c>ZADD</c>, <c>ZCARD</c>, <c>PEXPIRE</c>. One round trip, no
///         read-modify-write race, and the window really does slide rather than resetting on a clock
///         boundary — a fixed window lets a caller spend two windows' budget in the two seconds
///         either side of the boundary, which is exactly the burst the limit exists to stop.
///     </para>
/// </remarks>
interface IRateLimitCounters {
    /// <summary>Records one request against a key and reports the window.</summary>
    /// <param name="key">
    ///     The bucket's key, already scoped — <c>rl:{bucket}:{subject}</c>. ⚠ Built from the token's
    ///     tenant, never from anything the caller wrote, or a caller could spend somebody else's
    ///     budget by naming their subscription.
    /// </param>
    /// <param name="window">The bucket's window.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>
    ///     The count and the honest retry delay. ⚠ On a Redis failure the implementation must
    ///     <b>fail open</b> — see <c>RedisRateLimitCounters</c> for why that is the right direction
    ///     and what it costs.
    /// </returns>
    Task<WindowCount> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);
}
