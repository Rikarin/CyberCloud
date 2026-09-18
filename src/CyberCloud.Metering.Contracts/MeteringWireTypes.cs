using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     How much of one quota family a resource holds, right now.
/// </summary>
/// <remarks>
///     ⚠ <b>Provisioned, not consumed.</b> This is the stock the write path reserved through
///     <c>IQuotaGrain.TryReserveAsync</c> and committed — the platform's own record of what exists.
///     It is deliberately the same <see cref="QuotaMeter" /> vocabulary the registry declares and the
///     write path reserves, so a resource is metered in the units it was admitted in. See
///     <see cref="MeterCatalog" /> for why there is only one vocabulary.
/// </remarks>
/// <remarks>
///     ⚠ <b>A sealed record and not a <c>readonly record struct</c>, which is what it wants to be.</b>
///     It is an <c>[Id]</c> member of <see cref="MeteredResource" />, so Orleans generates a
///     serializer for it, and the generator emits a copier that assigns through a <c>ref</c> — which
///     a positional readonly struct's <c>init</c>-only members refuse (<c>CS1620</c>). The tree's
///     existing answer for a struct that must go on the wire is a surrogate
///     (<c>CyberCloud.Core.Contracts.Serialization.ResourceIdSurrogate</c>); a surrogate for a pair
///     of scalars would be more machinery than the allocation it saves.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.MeteredQuantity")]
public sealed record MeteredQuantity {
    /// <summary>Which family. <see cref="QuotaMeter.Unknown" /> is not one and is refused.</summary>
    [Id(0)]
    public QuotaMeter Family { get; init; } = QuotaMeter.Unknown;

    /// <summary>How much exists. Non-negative.</summary>
    [Id(1)]
    public decimal Amount { get; init; }
}

/// <summary>
///     What the platform is observed to be doing with a resource, for display and for the abuse
///     signals of docs/plan/22 § Abuse.
/// </summary>
/// <remarks>
///     ⚠ <b>NOT AN INPUT TO METERING, AND THAT IS THE POINT.</b> docs/plan/22 § Two kinds of meter:
///     "A stopped VM still has a disk; a <c>Deployment</c> scaled to zero still has a
///     <c>PersistentVolumeClaim</c>. Metrics know about running pods; the resource graph knows what
///     exists. Getting this backwards under-bills storage." <c>UsageSamplerGrain</c> never reads this
///     field, and <c>StateBasedMetersIgnoreRunState</c> in <c>CyberCloud.Metering.Tests</c> asserts
///     that a resource in every value below produces byte-identical usage. It is carried because a
///     support engineer looking at an unexpected bill asks "was it even running", and the honest
///     answer — "yes it was stopped, and a stopped disk is still a disk" — needs the field to exist.
/// </remarks>
[Alias("CyberCloud.Metering.ObservedRunState")]
public enum ObservedRunState {
    /// <summary>Nothing observed it. The default, and the only one a resource graph alone can give.</summary>
    Unknown = 0,

    /// <summary>Running.</summary>
    Running = 1,

    /// <summary>Deliberately stopped. ⚠ Still metered for everything it still holds.</summary>
    Stopped = 2,

    /// <summary>Scaled to zero replicas. ⚠ Still metered for its volumes and its addresses.</summary>
    ScaledToZero = 3
}

/// <summary>
///     One resource as the sampler sees it — the platform's own record of a thing that exists.
/// </summary>
/// <remarks>
///     ⚠ <b>This is a projection of the resource graph, not of a metrics pipeline</b>
///     (docs/plan/22 § Two kinds of meter). Everything a state-based meter needs is
///     <see cref="Quantities" />, and everything a metrics pipeline would have contributed is
///     absent, except <see cref="RunState" />, which exists so it can be visibly ignored.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.MeteredResource")]
public sealed record MeteredResource {
    /// <summary>The resource's GUID. ⚠ Never <see cref="Guid.Empty" /> — see <see cref="UsageEvent.ResourceId" />.</summary>
    [Id(0)]
    public Guid ResourceId { get; init; }

    /// <summary>The path, for the invoice line.</summary>
    [Id(1)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The provider namespace and resource type, for cost-by-service.</summary>
    [Id(2)]
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>The region. Quota and rating are both per-region.</summary>
    [Id(3)]
    public string Region { get; init; } = string.Empty;

    /// <summary>
    ///     What exists, in quota families — the same units the write path reserved.
    /// </summary>
    [Id(4)]
    public ImmutableArray<MeteredQuantity> Quantities { get; init; } = [];

    /// <summary>⚠ Diagnostic only. See <see cref="ObservedRunState" />.</summary>
    [Id(5)]
    public ObservedRunState RunState { get; init; } = ObservedRunState.Unknown;

    /// <summary>
    ///     Tags, carried so cost-by-tag (docs/plan/22 § Cost visibility, and why tags are M1 in
    ///     docs/plan/06) works without a second join later.
    /// </summary>
    [Id(6)]
    public ImmutableDictionary<string, string> Tags { get; init; } =
        ImmutableDictionary<string, string>.Empty;
}

/// <summary>
///     One hour of one meter on one resource — docs/plan/22 § The pipeline's <c>usage_hourly</c>.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageAggregate")]
public sealed record UsageAggregate {
    /// <summary>The tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource.</summary>
    [Id(2)]
    public Guid ResourceId { get; init; }

    /// <summary>The resource's path, as of the last record in the hour.</summary>
    [Id(3)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The meter.</summary>
    [Id(4)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>The region.</summary>
    [Id(5)]
    public string Region { get; init; } = string.Empty;

    /// <summary>Inclusive start of the hour.</summary>
    [Id(6)]
    public DateTimeOffset HourStart { get; init; }

    /// <summary>Exclusive end of the hour.</summary>
    [Id(7)]
    public DateTimeOffset HourEnd { get; init; }

    /// <summary>The sum of every accepted record's quantity in the hour.</summary>
    [Id(8)]
    public decimal Quantity { get; init; }

    /// <summary>
    ///     How many records were summed. Carried because "12 samples" and "1 sample" over one hour
    ///     have very different explanations for the same quantity, and the difference is the first
    ///     thing anyone asks when a figure looks wrong.
    /// </summary>
    [Id(9)]
    public int SampleCount { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Meter} {Quantity} on {ResourcePath} for {HourStart:yyyy-MM-ddTHH:mm:ssZ} ({SampleCount} samples)"
        );
}

/// <summary>
///     One entry in the append-only usage ledger — docs/plan/22 § The pipeline's durable,
///     per-subscription, append-only record.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every member is <c>init</c>-only and the grain exposes no way to replace one.</b>
///         docs/plan/22 § The pipeline: "Corrections are new entries with a reason and a link to the
///         original, never edits. An adjustable ledger cannot be audited and cannot be defended in a
///         dispute." The immutability is structural rather than a convention — see
///         <c>IUsageLedgerGrain</c>, which declares no update, set, edit, replace or delete method,
///         and <c>LedgerImmutabilityTests</c>, which asserts by reflection that it never will
///         without somebody noticing.
///     </para>
///     <para>
///         <b>A correction carries a delta, not a replacement.</b> <see cref="Quantity" /> on a
///         correction is what to add — negative to reduce. That is chosen over "the corrected total"
///         because addition composes: two independent corrections to one entry sum, whereas two
///         replacements race and the later one silently discards the earlier. The net figure is
///         therefore always the plain sum of every entry, which is a property a dispute can be
///         walked through line by line.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageLedgerEntry")]
public sealed record UsageLedgerEntry {
    /// <summary>
    ///     Position in the ledger, from 1. Dense and gapless — a gap means an entry was removed,
    ///     which nothing can do, so a gap is evidence of tampering rather than of a bug.
    /// </summary>
    [Id(0)]
    public long Sequence { get; init; }

    /// <summary>This entry's identity. What a correction points at.</summary>
    [Id(1)]
    public Guid EntryId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(2)]
    public Guid TenantId { get; init; }

    /// <summary>The subscription. Always this grain's own.</summary>
    [Id(3)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The resource.</summary>
    [Id(4)]
    public Guid ResourceId { get; init; }

    /// <summary>The resource's path, as it was.</summary>
    [Id(5)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>The meter.</summary>
    [Id(6)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>The region.</summary>
    [Id(7)]
    public string Region { get; init; } = string.Empty;

    /// <summary>The hour this entry accounts for, inclusive.</summary>
    [Id(8)]
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>The hour this entry accounts for, exclusive.</summary>
    [Id(9)]
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>
    ///     The quantity. Non-negative on a usage entry; signed on a correction, where it is a delta.
    /// </summary>
    [Id(10)]
    public decimal Quantity { get; init; }

    /// <summary>How many raw records the hour's aggregate summed. Zero on a correction.</summary>
    [Id(11)]
    public int SampleCount { get; init; }

    /// <summary>When this entry was written. Not the window — see <see cref="WindowStart" />.</summary>
    [Id(12)]
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>
    ///     The entry this one corrects, or <see langword="null" /> for an original.
    /// </summary>
    /// <remarks>
    ///     ⚠ The link, not a mutation. An auditor reading this ledger sees the original figure, the
    ///     correction, the reason and the order — which is the whole of what "defensible in a
    ///     dispute" means.
    /// </remarks>
    [Id(13)]
    public Guid? CorrectsEntryId { get; init; }

    /// <summary>
    ///     Why. ⚠ Required on a correction and refused on an original: a correction with no reason
    ///     is an edit wearing a hat.
    /// </summary>
    [Id(14)]
    public string Reason { get; init; } = string.Empty;

    /// <summary>Whether this entry corrects another.</summary>
    public bool IsCorrection => CorrectsEntryId.HasValue;

    /// <inheritdoc />
    public override string ToString() {
        // ⚠ Not one string.Create with a `+` in it. Concatenating a conditional onto an interpolated
        // string literal loses the DefaultInterpolatedStringHandler conversion and the call binds to
        // string.Create(IFormatProvider, ref handler) with a plain string — CS1620, and a confusing
        // one. Two calls, each over a literal.
        var head = string.Create(CultureInfo.InvariantCulture, $"#{Sequence} {Meter} {Quantity}");

        return IsCorrection
            ? head
            + string.Create(CultureInfo.InvariantCulture, $" correcting {CorrectsEntryId:N} ({Reason})")
            : head;
    }
}

/// <summary>What one sampler pass did.</summary>
/// <remarks>
///     ⚠ The window is carried as two instants rather than as a <see cref="UsageWindow" />, and
///     <see cref="Window" /> recomposes it. <see cref="UsageWindow" /> is a positional readonly
///     record struct, which Orleans' generated copier cannot assign through — see
///     <see cref="MeteredQuantity" /> for the same trap and the same reasoning.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageSampleReport")]
public sealed record UsageSampleReport {
    /// <summary>The window sampled, inclusive. Snapped, so a second pass in the same window repeats it.</summary>
    [Id(0)]
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>The window sampled, exclusive.</summary>
    [Id(1)]
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>How many resources the source reported.</summary>
    [Id(2)]
    public int ResourcesSeen { get; init; }

    /// <summary>How many records were built and handed to the emitter.</summary>
    [Id(3)]
    public int Emitted { get; init; }

    /// <summary>How many the rollup took as new.</summary>
    [Id(4)]
    public int Accepted { get; init; }

    /// <summary>
    ///     How many collapsed. ⚠ A steady non-zero figure here is normal and is the mechanism
    ///     working — it is what a re-run over an unfinished window, or a redelivery after a silo
    ///     restart, looks like.
    /// </summary>
    [Id(5)]
    public int Duplicates { get; init; }

    /// <summary>The window, as a pair.</summary>
    public UsageWindow Window => new(WindowStart, WindowEnd);
}

/// <summary>What one ingest did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageIngestReceipt")]
public sealed record UsageIngestReceipt {
    /// <summary>Accepted or duplicate. ⚠ Both are success — see <see cref="UsageIngestOutcome" />.</summary>
    [Id(0)]
    public UsageIngestOutcome Outcome { get; init; } = UsageIngestOutcome.Unknown;

    /// <summary>The key that decided it, so a log line can be correlated with the emitter's.</summary>
    [Id(1)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "Not a secret — it is the echo of UsageEvent.IdempotencyKey, a sha256 of values that "
            + "travel beside it. See the justification on UsageEvent.IdempotencyKey."
    )]
    public string IdempotencyKey { get; init; } = string.Empty;
}
