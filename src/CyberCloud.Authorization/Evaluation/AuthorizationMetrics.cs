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
public static class AuthorizationMetrics {
    /// <summary>The meter name, for the OpenTelemetry registration.</summary>
    public const string MeterName = "CyberCloud.Authorization";

    static readonly Meter Source = new(MeterName);

    static readonly Counter<long> ChecksCounter =
        Source.CreateCounter<long>("cybercloud.authz.checks", "{check}", "Checks evaluated.");

    static readonly Counter<long> CacheHitsCounter =
        Source.CreateCounter<long>("cybercloud.authz.cache_hits", "{check}", "Checks served from the hot-tier cache.");

    static readonly Counter<long> DepthCapCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.depth_cap_exceeded",
            "{check}",
            "Checks denied because the depth cap was reached. ⚠ Each one may be a wrong deny."
        );

    static readonly Counter<long> BreadthCapCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.breadth_cap_exceeded",
            "{check}",
            "Checks denied because the breadth cap was reached. ⚠ Each one may be a wrong deny."
        );

    static readonly Counter<long> ListObjectsCounter =
        Source.CreateCounter<long>("cybercloud.authz.list_objects", "{walk}", "ListObjects walks evaluated.");

    static readonly Counter<long> ListObjectsCapCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.list_objects_cap_exceeded",
            "{walk}",
            "ListObjects walks that reached the object cap and returned nothing. ⚠ Each one is a "
            + "caller whose listing fell back to a Check per member, or to nothing."
        );

    static readonly Counter<long> IndexAnswersCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.index_answers",
            "{membership}",
            "Userset memberships the Leopard index answered without a walk — docs/plan/07 § The Leopard index."
        );

    static readonly Counter<long> IndexWritesCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.index_writes",
            "{slice}",
            "Leopard index slices written by tuple writes and deletes. The fan-out the threshold "
            + "paragraph of docs/plan/07 § The Leopard index exists to cap, measured before it is."
        );

    static readonly Counter<long> IndexRebuildsCounter =
        Source.CreateCounter<long>(
            "cybercloud.authz.index_rebuilds",
            "{slice}",
            "Leopard index slices recomputed from the tuples because they were stamped with another schema version."
        );

    static long checks;
    static long cacheHits;
    static long depthCapExceeded;
    static long breadthCapExceeded;
    static long listObjects;
    static long listObjectsCapExceeded;
    static long indexAnswers;
    static long indexWrites;
    static long indexRebuilds;

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

    /// <summary>How many <c>ListObjects</c> walks have run in this process.</summary>
    public static long ListObjects => Interlocked.Read(ref listObjects);

    /// <summary>
    ///     How many of them reached <c>AuthorizationLimits.MaxListObjects</c> and answered nothing.
    ///     ⚠ A rising one is a subject the projection should be serving — docs/plan/07 § ListObjects.
    /// </summary>
    public static long ListObjectsCapExceeded => Interlocked.Read(ref listObjectsCapExceeded);

    /// <summary>
    ///     How many userset memberships the Leopard index answered in place of a walk. The number
    ///     that says the index is doing the work docs/plan/07 § The Leopard index gives it.
    /// </summary>
    public static long IndexAnswers => Interlocked.Read(ref indexAnswers);

    /// <summary>
    ///     How many index slices tuple writes and deletes have written. Divided by the writes, it is
    ///     the fan-out per tuple, which is the cost the document's threshold exists to bound.
    /// </summary>
    public static long IndexWrites => Interlocked.Read(ref indexWrites);

    /// <summary>How many slices were recomputed because their schema version was behind.</summary>
    public static long IndexRebuilds => Interlocked.Read(ref indexRebuilds);

    internal static void RecordIndexAnswer() {
        Interlocked.Increment(ref indexAnswers);
        IndexAnswersCounter.Add(1);
    }

    internal static void RecordIndexWrites(int slices) {
        Interlocked.Add(ref indexWrites, slices);
        IndexWritesCounter.Add(slices);
    }

    internal static void RecordIndexRebuild() {
        Interlocked.Increment(ref indexRebuilds);
        IndexRebuildsCounter.Add(1);
    }

    internal static void RecordListObjects() {
        Interlocked.Increment(ref listObjects);
        ListObjectsCounter.Add(1);
    }

    internal static void RecordListObjectsCap() {
        Interlocked.Increment(ref listObjectsCapExceeded);
        ListObjectsCapCounter.Add(1);
    }

    internal static void RecordCheck() {
        Interlocked.Increment(ref checks);
        ChecksCounter.Add(1);
    }

    internal static void RecordCacheHit() {
        Interlocked.Increment(ref cacheHits);
        CacheHitsCounter.Add(1);
    }

    internal static void RecordDepthCap() {
        Interlocked.Increment(ref depthCapExceeded);
        DepthCapCounter.Add(1);
    }

    internal static void RecordBreadthCap() {
        Interlocked.Increment(ref breadthCapExceeded);
        BreadthCapCounter.Add(1);
    }
}
