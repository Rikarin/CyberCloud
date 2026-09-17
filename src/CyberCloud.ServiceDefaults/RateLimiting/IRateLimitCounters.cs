namespace CyberCloud.ServiceDefaults.RateLimiting;

/// <summary>
///     What one window's worth of counting produced.
/// </summary>
/// <param name="Count">How many requests are inside the window, this one included.</param>
/// <param name="RetryAfter">
///     How long until the oldest request in the window falls out of it — the honest
///     <c>Retry-After</c>. ⚠ Not the whole window: telling a caller to wait five minutes when the
///     budget frees in four seconds is how a well-behaved SDK is turned into a stalled one.
/// </param>
public readonly record struct WindowCount(long Count, TimeSpan RetryAfter);

/// <summary>
///     A sliding-window request counter — the gateway's stage 5 and the identity host's per-IP
///     buckets share it. docs/plan/10 § Request pipeline:
///     <i>
///         "Redis-backed sliding
///         window."
///     </i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOTHING BEHIND THIS INTERFACE MAY TOUCH A GRAIN.</b> docs/plan/10 § Request pipeline
///         states the reason in one line —
///         <i>
///             "a rate limiter that costs a grain call is a rate
///             limiter that amplifies an attack"
///         </i> — and it is worth spelling out, because the
///         implementation that gets this wrong looks perfectly reasonable. A per-subscription counter
///         held in a subscription grain would mean a flood against one subscription activates that
///         grain, serialises every request in the flood through its single-threaded turn, and holds
///         a silo's scheduler while doing it. The rate limiter would become the amplifier: the
///         cheapest possible request for the attacker, the most expensive possible for the platform.
///         <c>RateLimitingTests.AFloodPastTheLimitCostsNoGrainCall</c> floods past the limit against
///         a grain factory that fails the test if it is touched at all. The same sentence is why
///         docs/plan/11 § Credentials puts the sign-in lockout counter in the hot tier as a Redis
///         <c>INCR</c> — an authentication endpoint whose failure path costs a grain activation is
///         the same amplifier.
///     </para>
///     <para>
///         ⚠ <b>Here, in <c>CyberCloud.ServiceDefaults</c>, because two hosts count.</b> The gateway's
///         five buckets lived beside this interface in the gateway for as long as the gateway was
///         the only host with a limiter; the identity host's per-IP buckets on <c>/api/signup/begin</c>
///         and the code-verify endpoints (#94) count through the same counters, and a host cannot
///         reference another host. What each host keeps to itself is its <i>buckets</i> — which key,
///         which limit, which window — and what it answers when one trips; the window arithmetic and
///         the Redis script are the part that must not be written twice, because the second copy is
///         the one whose window resets on a boundary.
///     </para>
///     <para>
///         The Redis implementation is a sorted set per key and one Lua script per request:
///         <c>ZREMRANGEBYSCORE</c>, <c>ZADD</c>, <c>ZCARD</c>, <c>PEXPIRE</c>. One round trip, no
///         read-modify-write race, and the window really does slide rather than resetting on a clock
///         boundary — a fixed window lets a caller spend two windows' budget in the two seconds
///         either side of the boundary, which is exactly the burst the limit exists to stop.
///     </para>
/// </remarks>
public interface IRateLimitCounters {
    /// <summary>Records one request against a key and reports the window.</summary>
    /// <param name="key">
    ///     The bucket's key, already scoped — <c>rl:{bucket}:{subject}</c>. ⚠ Built from the token's
    ///     tenant or the connection's address, never from anything the caller wrote, or a caller
    ///     could spend somebody else's budget by naming their subscription.
    /// </param>
    /// <param name="window">The bucket's window.</param>
    /// <param name="cancellationToken">Cancels the round trip.</param>
    /// <returns>
    ///     The count and the honest retry delay. ⚠ On a Redis failure the implementation must
    ///     <b>fail open</b> — see <see cref="RedisRateLimitCounters" /> for why that is the right
    ///     direction and what it costs.
    /// </returns>
    Task<WindowCount> CountAsync(string key, TimeSpan window, CancellationToken cancellationToken = default);
}
