using System.Diagnostics.Metrics;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     The counters that make a capped check <b>observable</b> rather than indistinguishable from a
///     genuine deny.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type is half the answer to "what does a check that hits a cap return".</b> The
///         other half is <c>CheckOutcome</c>. Fail-closed is the only safe behaviour — a walk that
///         ran out of budget must not allow — but a fail-closed deny that looks exactly like a real
///         one turns a legitimate user away permanently and silently, and nobody finds out. So a
///         capped check is denied, <i>and</i> carries a distinguishable outcome, <i>and</i>
///         increments one of these, <i>and</i> is never cached.
///     </para>
///     <para>
///         Both a <see cref="Counter{T}" /> (for the OpenTelemetry pipeline of docs/plan/16) and a
///         plain readable total (for tests, which assert a delta across a call). They are
///         incremented together; the readable total is not a substitute for the instrument, it is a
///         way to hold the instrument to its word.
///     </para>
/// </remarks>
public static class AuthorizationMetrics
{
    /// <summary>The meter name, for the OpenTelemetry registration.</summary>
    public const string MeterName = "CyberCloud.Authorization";

    static readonly Meter Source = new(MeterName);

    static readonly Counter<long> ChecksCounter =
        Source.CreateCounter<long>("cybercloud.authz.checks", "{check}", "Checks evaluated.");

    static readonly Counter<long> CacheHitsCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.cache_hits", "{check}", "Checks served from the hot-tier cache.");

    static readonly Counter<long> DepthCapCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.depth_cap_exceeded",
            "{check}",
            "Checks denied because the depth cap was reached. ⚠ Each one may be a wrong deny.");

    static readonly Counter<long> BreadthCapCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.breadth_cap_exceeded",
            "{check}",
            "Checks denied because the breadth cap was reached. ⚠ Each one may be a wrong deny.");

    static long checks;
    static long cacheHits;
    static long depthCapExceeded;
    static long breadthCapExceeded;

    /// <summary>How many checks have been evaluated in this process.</summary>
    public static long Checks => Interlocked.Read(ref checks);

    /// <summary>How many were served from the check cache.</summary>
    public static long CacheHits => Interlocked.Read(ref cacheHits);

    /// <summary>
    ///     How many were denied because the walk reached <c>AuthorizationLimits.MaxDepth</c>. ⚠ Not
    ///     zero is not automatically an incident, but a rising one is: every increment is a subject
    ///     who may in fact have had access.
    /// </summary>
    public static long DepthCapExceeded => Interlocked.Read(ref depthCapExceeded);

    /// <summary>How many were denied because a node reached <c>AuthorizationLimits.MaxBreadth</c>.</summary>
    public static long BreadthCapExceeded => Interlocked.Read(ref breadthCapExceeded);

    internal static void RecordCheck()
    {
        Interlocked.Increment(ref checks);
        ChecksCounter.Add(1);
    }

    internal static void RecordCacheHit()
    {
        Interlocked.Increment(ref cacheHits);
        CacheHitsCounter.Add(1);
    }

    internal static void RecordDepthCap()
    {
        Interlocked.Increment(ref depthCapExceeded);
        DepthCapCounter.Add(1);
    }

    internal static void RecordBreadthCap()
    {
        Interlocked.Increment(ref breadthCapExceeded);
        BreadthCapCounter.Add(1);
    }
}
