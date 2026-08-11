namespace CyberCloud.Tenancy;

/// <summary>
///     How often the two in-process mirrors — the shard map and the tenant directory — pull deltas
///     from their grains.
/// </summary>
/// <remarks>
///     <para>
///         Both are polling loops rather than stream subscriptions today. docs/plan/05 § The tenant
///         directory describes the production shape as "refreshed by subscribing to
///         <c>cc.platform.directory</c> and applying deltas", which is a NATS stream — and streams
///         are not wired yet (docs/plan/04 § Streams; <c>OrleansApplication.CreateSilo</c> leaves
///         <c>AddMultitenantStreams</c> as a seam). Polling a version-stamped delta has the same
///         convergence property with a worse latency, which on a feed whose write rate is 0.12/s is
///         a trade worth making until the stream provider lands.
///     </para>
///     <para>
///         ⚠ <b>These are timers, not deadlines.</b> Nothing expires if a refresh does not happen —
///         see <c>TenantDirectoryCache</c>, where "indefinitely" is load-bearing.
///     </para>
/// </remarks>
public sealed class TenancyRefreshOptions {
    /// <summary>How often to poll <c>IShardMapGrain.GetSnapshotAsync</c>.</summary>
    public TimeSpan ShardMapInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often to poll <c>ITenantDirectoryGrain.GetDeltaAsync</c>.</summary>
    public TimeSpan DirectoryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Whether the background loops run at all. Off is for tests that drive the refresh by hand.
    /// </summary>
    /// <remarks>
    ///     ⚠ A test that asserts on cache <i>misses</i> cannot share a process with a loop that is
    ///     quietly filling the cache — the assertion would pass or fail on timing. Turning the loop
    ///     off and calling <c>RefreshAsync</c> explicitly is what makes those tests deterministic,
    ///     and it exercises the same method the loop calls.
    /// </remarks>
    public bool RunBackgroundRefresh { get; set; } = true;
}
