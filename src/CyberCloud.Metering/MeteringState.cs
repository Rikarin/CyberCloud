using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Metering;

/// <summary>
///     <c>UsageSamplerGrain</c>'s state — a cursor and a switch.
/// </summary>
/// <remarks>
///     ⚠ <b>Every collection and property here is <c>{ get; set; }</c> and that is not laziness.</b>
///     A real storage provider round-trips grain state through System.Text.Json, which does not
///     populate a get-only collection; the trap is documented on
///     <c>CyberCloud.ResourceManager.Tests</c>' <c>.csproj</c> and the whole tree follows the same
///     rule.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageSamplerState")]
public sealed class UsageSamplerState {
    /// <summary>Whether <c>StartAsync</c> has been called and <c>StopAsync</c> has not.</summary>
    [Id(0)]
    public bool Running { get; set; }

    /// <summary>
    ///     The start of the last window sampled. Diagnostic only — correctness comes from the
    ///     idempotency key, not from this. See <c>IUsageSamplerGrain</c> on why the tier is Hot.
    /// </summary>
    [Id(1)]
    public DateTimeOffset LastWindowStart { get; set; }

    /// <summary>How many passes this grain has run since it was last activated from empty.</summary>
    [Id(2)]
    public long Passes { get; set; }
}

/// <summary>
///     <c>UsageRollupGrain</c>'s state: raw records for hours that have not closed, and the
///     idempotency keys already seen.
/// </summary>
/// <remarks>
///     ⚠ <b>Durable. Neither half is rebuildable</b> — see <c>IUsageRollupGrain</c>, and
///     <c>durable-grains.txt</c>, which carries the reviewed reason.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageRollupState")]
public sealed class UsageRollupState {
    /// <summary>Whether the hourly close reminder is wanted.</summary>
    [Id(0)]
    public bool Running { get; set; }

    /// <summary>
    ///     Raw records whose hour has not closed. Dropped only once the hour's aggregate is in the
    ///     ledger <i>and</i> the sink has taken it.
    /// </summary>
    [Id(1)]
    public List<UsageEvent> Pending { get; set; } = [];

    /// <summary>
    ///     Every idempotency key seen inside the retention horizon, against the end of its window so
    ///     the horizon can be applied without re-reading the record.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the dedup and it is the reason this grain is durable.</b> Losing it turns
    ///     every redelivery in the horizon into double billing, which is worse than losing the
    ///     records themselves — a lost record is a figure that is too low and visible in a graph, a
    ///     double-counted one is a figure that is too high and looks like growth.
    /// </remarks>
    [Id(2)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "Not secrets. These are UsageEvent.IdempotencyKey values — sha256 digests of a resource "
            + "id, a meter name and two window boundaries, all of which are already in this grain's "
            + "own Pending list and in the ledger. See the justification on UsageEvent.IdempotencyKey."
    )]
    public Dictionary<string, DateTimeOffset> SeenKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The start of every hour already closed and settled, so re-closing one is refused rather
    ///     than appended twice to an append-only ledger.
    /// </summary>
    /// <remarks>
    ///     Pruned on the same horizon as <see cref="SeenKeys" />: an hour older than the horizon
    ///     cannot receive a late record from a transport that no longer holds one.
    /// </remarks>
    [Id(3)]
    public List<DateTimeOffset> ClosedHours { get; set; } = [];
}

/// <summary>
///     <c>UsageLedgerGrain</c>'s state — the entries, and the next sequence number.
/// </summary>
/// <remarks>
///     ⚠ <b>Durable and append-only.</b> Nothing in the grain removes from
///     <see cref="Entries" /> or rewrites one, and <c>IUsageLedgerGrain</c> exposes no method that
///     could. <see cref="NextSequence" /> only increases; a gap in the sequence is evidence, not a
///     bug, because nothing can create one.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageLedgerState")]
public sealed class UsageLedgerState {
    /// <summary>Every entry, in the order it was appended.</summary>
    [Id(0)]
    public List<UsageLedgerEntry> Entries { get; set; } = [];

    /// <summary>The sequence the next entry gets. Starts at 1.</summary>
    [Id(1)]
    public long NextSequence { get; set; } = 1;
}
