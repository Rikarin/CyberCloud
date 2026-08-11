namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     Zanzibar's zookie, adapted — the three rows of docs/plan/07 § Consistency, exactly.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             docs/plan/07 § Consistency and § Caching across requests contradict each other, and
///             this enum is where the contradiction had to be resolved.
///         </b>
///     </para>
///     <para>
///         § Caching across requests says the cache is <i>keyed</i> by
///         <c>(…, schemaVersion, tenantRelationVersion)</c> and that "a write invalidates the
///         tenant's whole check cache". If the version is part of the lookup key then after any
///         write <b>no</b> entry can be found by <b>any</b> mode — which makes
///         <see cref="MinimizeLatency" /> identical to <see cref="AtLeastAsFresh" />, makes the
///         token pointless, and makes the revoke-then-stale-read bug class § Consistency exists to
///         name <i>impossible</i>. § Consistency's own table says the opposite:
///         <see cref="MinimizeLatency" /> takes "any cached result".
///     </para>
///     <para>
///         <b>Resolved towards § Consistency</b>, because that is the section with the argument in
///         it. The relation version is <i>stamped on</i> a cached entry rather than being part of
///         the lookup key, and the mode decides which stamps are acceptable. The bug class is
///         therefore real, reproducible and tested (<c>RevokeThenStaleReadTests</c>), which is the
///         only honest state for a mode called "minimize latency" to be in.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.ConsistencyMode")]
public enum ConsistencyMode {
    /// <summary>
    ///     Any cached result, however stale. List views and portal navigation — docs/plan/07
    ///     § Consistency, row 1. <b>The default, and the one that can serve a revoked grant.</b>
    /// </summary>
    MinimizeLatency = 0,

    /// <summary>
    ///     Bypass cache entries older than the token. What the portal passes immediately after a
    ///     role assignment, using the token that write returned — row 2.
    /// </summary>
    AtLeastAsFresh = 1,

    /// <summary>
    ///     Bypass <b>all</b> caches and read durable. Deletion, key export, billing changes —
    ///     anything where a stale allow is a real incident — row 3.
    /// </summary>
    FullyConsistent = 2
}

/// <summary>
///     What a check actually concluded. ⚠ <b>Four outcomes, not two, and that is the point.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § Check sets a depth cap of 12 and a breadth cap of 1 000 and says nothing
///         about what happens when one is hit. Fail-closed is the only safe answer — a check that
///         ran out of budget must not allow — but "denied because the budget ran out" and "denied
///         because you genuinely have no path" are different events, and collapsing them means a
///         legitimate user is turned away silently and forever, with nothing to grep for.
///     </para>
///     <para>
///         So a capped check is <see cref="CheckResult.Allowed" /> = <see langword="false" /> (safe)
///         <i>and</i> carries <see cref="DepthCapExceeded" /> or <see cref="BreadthCapExceeded" />
///         (observable), <i>and</i> increments a counter on
///         <c>CyberCloud.Authorization.AuthorizationMetrics</c>, <i>and</i> is never written to the
///         cache — because caching "I gave up" would make one unlucky walk permanent.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.CheckOutcome")]
public enum CheckOutcome {
    /// <summary>Never assigned. Not an outcome — see <c>Result</c>'s <c>default(T)</c> argument.</summary>
    Unknown = 0,

    /// <summary>The walk found a path. <see cref="CheckResult.Allowed" /> is true.</summary>
    Allowed = 1,

    /// <summary>The walk completed within both caps and found no path. A genuine deny.</summary>
    Denied = 2,

    /// <summary>
    ///     The walk hit the depth cap before finding a path. Denied, and <b>possibly wrong</b>.
    /// </summary>
    DepthCapExceeded = 3,

    /// <summary>
    ///     The walk hit the breadth cap at some node before finding a path. Denied, and
    ///     <b>possibly wrong</b>.
    /// </summary>
    BreadthCapExceeded = 4
}
