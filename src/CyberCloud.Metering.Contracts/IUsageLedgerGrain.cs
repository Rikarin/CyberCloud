using System.Collections.Immutable;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     What one ledger append says. The grain assigns everything a caller must not choose.
/// </summary>
/// <remarks>
///     ⚠ <b>No <c>Sequence</c>, no <c>EntryId</c>, no <c>RecordedAt</c> and no
///     <c>CorrectsEntryId</c> here, and their absence is the design.</b> A caller that could set a
///     sequence could write an entry into the middle of the history; one that could set an entry id
///     could overwrite an existing entry by naming it; one that could set <c>RecordedAt</c> could
///     backdate. Every field that makes the ledger a ledger is assigned by
///     <see cref="IUsageLedgerGrain" /> and is unreachable from the wire.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageLedgerAppend")]
public sealed record UsageLedgerAppend {
    /// <summary>The tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The resource.</summary>
    [Id(1)]
    public Guid ResourceId { get; init; }

    /// <summary>The resource's path, for the invoice line.</summary>
    [Id(2)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The meter.</summary>
    [Id(3)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>The region.</summary>
    [Id(4)]
    public string Region { get; init; } = string.Empty;

    /// <summary>The hour, inclusive.</summary>
    [Id(5)]
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>The hour, exclusive.</summary>
    [Id(6)]
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>The quantity. Non-negative — a reduction is a correction.</summary>
    [Id(7)]
    public decimal Quantity { get; init; }

    /// <summary>How many raw records the hour summed.</summary>
    [Id(8)]
    public int SampleCount { get; init; }
}

/// <summary>
///     The append-only usage ledger — docs/plan/22 § The pipeline's durable, per-subscription record.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b> <c>sub/{subscriptionId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>THIS INTERFACE DECLARES NO UPDATE, SET, EDIT, REPLACE OR DELETE, AND THAT ABSENCE IS
///         THE FEATURE.</b> docs/plan/22 § The pipeline: <i>"The ledger is durable-tier and
///         append-only. Corrections are new entries with a reason and a link to the original, never
///         edits. An adjustable ledger cannot be audited and cannot be defended in a dispute."</i>
///         The property is structural, not a convention a reviewer has to remember: there is no
///         method that reaches an existing entry, and <c>UsageLedgerEntry</c>'s members are all
///         <c>init</c>-only, so a caller holding one cannot mutate its copy either.
///         <c>LedgerImmutabilityTests</c> asserts both by reflection, so adding a mutator later
///         fails a test rather than passing review.
///     </para>
///     <para>
///         <b>What "M1 ledger" means, since docs/plan/22 § Effort puts the ledger grain in M2.</b>
///         The row that is M2 is <i>"Rating, price lists, tiers, free tier, ledger grain"</i> — a
///         ledger of <b>charges</b>, which needs prices, which M1 does not have. This is the ledger
///         of <b>usage</b>: quantities, not money. It is M1 because the same sentence that defers
///         rating demands this — <i>"the meters must be correct from the first resource because usage
///         that was never recorded cannot be recovered"</i> — and a record that is not durable and
///         not append-only is not a record. When rating lands, charges are a second ledger derived
///         from this one, and this one does not change.
///     </para>
///     <para>
///         <b>Entries are hourly aggregates, not raw samples,</b> and that is a size decision worth
///         seeing. One resource with one meter produces 288 five-minute samples a day and 24 hourly
///         entries; the raw records live in <see cref="IUsageRollupGrain" />'s durable state until
///         their hour closes and in ClickHouse afterwards, and the hour is what an invoice line and a
///         dispute are argued over. A ledger of raw samples would be 12× the rows for no additional
///         defensibility.
///     </para>
/// </remarks>
[Alias("CyberCloud.Metering.IUsageLedgerGrain")]
public interface IUsageLedgerGrain : IGrainWithStringKey {
    /// <summary>
    ///     Appends one entry. The only way anything enters this ledger.
    /// </summary>
    /// <param name="append">What to record. The grain assigns identity, sequence and time.</param>
    /// <returns>
    ///     The entry as written, or a failure.
    ///     <para>
    ///         ⚠ Appending an entry for a (resource, meter, window) that already has one is refused
    ///         with <see cref="ErrorCode.Conflict" />. It is the last line of defence behind the
    ///         rollup's idempotency-key dedup, and it is placed here because this is the only object
    ///         from which nothing can be removed — a duplicate that reaches it is permanent.
    ///     </para>
    /// </returns>
    Task<Result<UsageLedgerEntry>> AppendAsync(UsageLedgerAppend append);

    /// <summary>
    ///     Corrects an entry — by adding another one, which is the only kind of correction there is.
    /// </summary>
    /// <param name="correctsEntryId">
    ///     <see cref="UsageLedgerEntry.EntryId" /> of the entry being corrected. Must exist. The
    ///     original is not touched, not marked and not superseded; it stays exactly as written and
    ///     the correction points at it.
    /// </param>
    /// <param name="delta">
    ///     What to add. Signed, and typically negative. ⚠ A <i>delta</i> rather than a replacement
    ///     value: two independent corrections to one entry then sum, whereas two replacements race
    ///     and the later silently discards the earlier. It also makes
    ///     <see cref="NetQuantityAsync" /> a plain sum, which is a figure a dispute can be walked
    ///     through line by line.
    /// </param>
    /// <param name="reason">
    ///     Why. ⚠ Required and non-blank: docs/plan/22 § The pipeline says corrections carry "a
    ///     reason and a link to the original", and a correction with no reason is an edit wearing a
    ///     hat. A blank one is refused rather than stored empty.
    /// </param>
    /// <returns>The correction entry, or a failure.</returns>
    Task<Result<UsageLedgerEntry>> AppendCorrectionAsync(Guid correctsEntryId, decimal delta, string reason);

    /// <summary>Every entry, in sequence order. Originals and corrections together, as written.</summary>
    Task<Result<ImmutableArray<UsageLedgerEntry>>> ListAsync();

    /// <summary>The entries that correct one entry, in sequence order.</summary>
    /// <param name="entryId">The original's <see cref="UsageLedgerEntry.EntryId" />.</param>
    Task<Result<ImmutableArray<UsageLedgerEntry>>> ListCorrectionsAsync(Guid entryId);

    /// <summary>
    ///     One meter's net quantity — the plain sum of every entry for it, corrections included.
    /// </summary>
    /// <param name="meter">The meter.</param>
    /// <remarks>
    ///     Derived on every call rather than kept as a running total. A cached total is a second
    ///     representation of the ledger's contents, and a ledger with two representations has one
    ///     that can be wrong.
    /// </remarks>
    Task<Result<decimal>> NetQuantityAsync(BillingMeter meter);

    /// <summary>How many entries there are. The ledger's length, which only ever grows.</summary>
    Task<Result<long>> CountAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
