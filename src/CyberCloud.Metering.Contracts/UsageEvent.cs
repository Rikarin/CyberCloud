using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Metering.Contracts;

/// <summary>
///     A half-open sample window <c>[Start, End)</c>, snapped to a grid.
/// </summary>
/// <remarks>
///     ⚠ <b>The snap is what makes the idempotency key work, and it is easy to leave out.</b> The key
///     is <c>sha256(resourceId | meter | windowStart | windowEnd)</c>, so two samplers — or one
///     sampler run twice — produce the same key only if they produce the same window <i>to the
///     tick</i>. A sampler that used <c>clock.UtcNow</c> as the window start would produce a
///     different key on every run and docs/plan/22's dedup would never fire. Snapping to a grid
///     anchored at the Unix epoch means every silo, in every region, at every clock offset, agrees
///     on which window an instant belongs to without talking to anything.
/// </remarks>
/// <param name="Start">Inclusive start, UTC, on the grid.</param>
/// <param name="End">Exclusive end, UTC, on the grid.</param>
public readonly record struct UsageWindow(DateTimeOffset Start, DateTimeOffset End) {
    /// <summary>
    ///     The sampler's period — docs/plan/22 § Two kinds of meter, "a 5-minute sampler".
    /// </summary>
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The rollup's aggregation period — docs/plan/22 § The pipeline, <c>usage_hourly</c>.
    /// </summary>
    public static readonly TimeSpan RollupPeriod = TimeSpan.FromHours(1);

    /// <summary>How long the window is.</summary>
    public TimeSpan Length => End - Start;

    /// <summary>The window of the given length that contains an instant.</summary>
    /// <param name="instant">Any instant. Converted to UTC first — the grid is a UTC grid.</param>
    /// <param name="period">The grid spacing. Must be positive and must divide a day.</param>
    /// <returns>The half-open window containing <paramref name="instant" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period" /> is not positive.</exception>
    public static UsageWindow Containing(DateTimeOffset instant, TimeSpan period) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        var utc = instant.ToUniversalTime();
        var elapsed = utc.UtcTicks;
        var start = new DateTimeOffset(elapsed - (elapsed % period.Ticks), TimeSpan.Zero);

        return new(start, start + period);
    }

    /// <summary>The five-minute sample window containing an instant.</summary>
    /// <param name="instant">Any instant.</param>
    public static UsageWindow SampleAt(DateTimeOffset instant) => Containing(instant, SamplePeriod);

    /// <summary>The hour containing an instant.</summary>
    /// <param name="instant">Any instant.</param>
    public static UsageWindow HourAt(DateTimeOffset instant) => Containing(instant, RollupPeriod);

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Start.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}, {End.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ})";
}

/// <summary>
///     One usage record — docs/plan/22 § The pipeline's
///     <c>{ tenant, subscription, resourceId, meter, quantity, window, idempotencyKey }</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The property this type exists to make true</b> (docs/plan/22, first paragraph): <i>a
///         usage record, once emitted, is never lost and never double-counted.</i> The "never lost"
///         half is the durable rollup and ledger grains; the "never double-counted" half is
///         <see cref="IdempotencyKey" />, and it is a property of this type rather than of the
///         pipeline, because a key computed by the consumer would collapse two genuinely different
///         records that happened to look alike, and a key chosen at random by the producer would
///         collapse nothing.
///     </para>
///     <para>
///         ⚠ <b>Build one with <see cref="ForSample" /> or <see cref="ForEvent" />, never with an
///         object initialiser.</b> Both compute the key. A record constructed by hand with an empty
///         or invented key is refused by the rollup (<see cref="IsKeyConsistent" />) rather than
///         accepted, because a wrong key is worse than no key: it silently either swallows real
///         usage or duplicates it, and both are invisible until an invoice is disputed.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Metering.UsageEvent")]
public sealed record UsageEvent {
    /// <summary>The owning tenant. docs/plan/06 § The hierarchy.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The billing and quota boundary this accrues to.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>
    ///     The resource's GUID — docs/plan/06 § Identifiers' "stable across renames, used in tuples,
    ///     metering records and grain keys". ⚠ The GUID and not the path, deliberately: a rename
    ///     must not start a new meter.
    /// </summary>
    [Id(2)]
    public Guid ResourceId { get; init; }

    /// <summary>
    ///     The resource's path at the moment of the sample, for the invoice line and the support
    ///     ticket. Not an identifier — see <see cref="ResourceId" />.
    /// </summary>
    [Id(3)]
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>What is being metered.</summary>
    [Id(4)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>Whether this was sampled or emitted. Redundant with the catalogue, and carried anyway
    /// so a record read out of the sink years later explains itself without one.</summary>
    [Id(5)]
    public MeterKind Kind { get; init; } = MeterKind.Unknown;

    /// <summary>How much. Non-negative — a correction is a ledger entry, not a negative sample.</summary>
    [Id(6)]
    public decimal Quantity { get; init; }

    /// <summary>Inclusive start of the window, UTC, snapped to the grid.</summary>
    [Id(7)]
    public DateTimeOffset WindowStart { get; init; }

    /// <summary>Exclusive end of the window, UTC, snapped to the grid.</summary>
    [Id(8)]
    public DateTimeOffset WindowEnd { get; init; }

    /// <summary>
    ///     The region the usage happened in. Quota is per-region (docs/plan/06 § Quota) and rating is
    ///     per-region (docs/plan/22 § Rating), so a record without one cannot be priced.
    /// </summary>
    [Id(9)]
    public string Region { get; init; } = string.Empty;

    /// <summary>
    ///     For an event-based meter, the provider's own identifier for the occurrence. Empty for a
    ///     state-based meter.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This component is not in docs/plan/22 § The pipeline's key formula, and its absence
    ///     there is a defect in that document.</b> <c>sha256(resourceId | meter | windowStart |
    ///     windowEnd)</c> is exactly right for a state-based meter, where the sampler emits one event
    ///     per (resource, meter, window) <i>by construction</i> — that is what makes "a sampler that
    ///     runs twice produces one record" true. It is not a key for an event-based meter: two
    ///     requests to the same resource inside one window are two genuine records with identical
    ///     components, and the formula collapses them. That is the failure docs/plan/22 does not name
    ///     and the one that is worse than a duplicate — a dedup that swallows real usage. This field
    ///     is the fifth component, empty for state-based meters so their keys are bit-for-bit the
    ///     document's formula.
    /// </remarks>
    [Id(10)]
    public string EventId { get; init; } = string.Empty;

    /// <summary>
    ///     <c>sha256(resourceId | meter | windowStart | windowEnd)</c>, lower-case hex —
    ///     docs/plan/22 § The pipeline.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not a secret, and CC1005 is suppressed rather than the field renamed.</b> The rule
    ///     bans <c>[Id]</c>-annotated members ending in <c>Key</c> because a secret in grain state is
    ///     a secret in every backup. This one is a digest of five values that are themselves in the
    ///     same record — it carries no information that is not already beside it, and it is printed
    ///     in traces and compared across silos on purpose. Renaming it to dodge the analyzer would
    ///     lose the name docs/plan/22 § The pipeline uses, which is the name a reader will search for.
    /// </remarks>
    [Id(11)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "Not a secret. This is sha256 of ResourceId, Meter, WindowStart, WindowEnd and EventId — "
            + "every input is a sibling [Id] member of this same record, so the digest reveals nothing "
            + "the state does not already carry. It is deliberately logged, traced and compared across "
            + "silos, which is the opposite of a credential. The name is docs/plan/22 § The pipeline's."
    )]
    public string IdempotencyKey { get; init; } = string.Empty;

    /// <summary>When the record was produced. Diagnostic only — never a key component.</summary>
    /// <remarks>
    ///     ⚠ Keeping this out of the key is the point. A redelivery after a silo restart arrives with
    ///     a different emission time and the same window; if emission time were in the key it would
    ///     be a different record and docs/plan/22's "never double-counted" would be false.
    /// </remarks>
    [Id(12)]
    public DateTimeOffset EmittedAt { get; init; }

    /// <summary>The window, as a pair.</summary>
    public UsageWindow Window => new(WindowStart, WindowEnd);

    /// <summary>
    ///     The deterministic key of docs/plan/22 § The pipeline.
    /// </summary>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="meter">The meter. Its <i>name</i> is hashed — see <see cref="BillingMeter" />.</param>
    /// <param name="windowStart">Inclusive start.</param>
    /// <param name="windowEnd">Exclusive end.</param>
    /// <param name="eventId">
    ///     The provider's occurrence id for an event-based meter; <c>""</c> for a state-based one,
    ///     which reproduces the document's four-component formula exactly.
    /// </param>
    /// <returns>64 lower-case hexadecimal characters.</returns>
    /// <remarks>
    ///     <para>
    ///         Every component is rendered in a form with exactly one spelling: the GUID in the
    ///         32-digit lower-case <c>N</c> form, the instants as UTC ticks (an integer — no
    ///         formatting, no offset, no calendar), the meter as its member name. A component that
    ///         could be spelled two ways would be a key that could be spelled two ways, and a
    ///         redelivery whose serialiser rounded a millisecond differently would not collapse.
    ///     </para>
    ///     <para>
    ///         The separator is <c>|</c>, as the document writes it. No component can contain one:
    ///         GUIDs and tick counts cannot by construction, meter names are C# identifiers, and
    ///         <paramref name="eventId" /> is rejected below if it does — an event id carrying a
    ///         separator could otherwise forge another event's key.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="eventId" /> contains the separator.</exception>
    public static string KeyFor(
        Guid resourceId,
        BillingMeter meter,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string eventId = ""
    ) {
        eventId ??= string.Empty;

        if (eventId.Contains(Separator, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"An event id must not contain '{Separator}', which separates the key's components. "
                + $"'{eventId}' does, and an id that can inject a separator can forge another "
                + "event's idempotency key.",
                nameof(eventId)
            );
        }

        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{resourceId:N}{Separator}{meter}{Separator}{windowStart.ToUniversalTime().UtcTicks}"
            + $"{Separator}{windowEnd.ToUniversalTime().UtcTicks}{Separator}{eventId}"
        );

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    ///     Builds a state-based record — one per (resource, meter, window), which is what makes a
    ///     sampler that runs twice produce one record.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="resourcePath">The resource's path, for display.</param>
    /// <param name="meter">A state-based meter.</param>
    /// <param name="quantity">The accrued quantity — see <see cref="MeterCatalog.Accrue" />.</param>
    /// <param name="window">The snapped window.</param>
    /// <param name="region">The region.</param>
    /// <param name="emittedAt">When the sampler ran. Not a key component.</param>
    /// <returns>The record, or a failure describing what is wrong with the inputs.</returns>
    public static Result<UsageEvent> ForSample(
        Guid tenantId,
        Guid subscriptionId,
        Guid resourceId,
        string resourcePath,
        BillingMeter meter,
        decimal quantity,
        UsageWindow window,
        string region,
        DateTimeOffset emittedAt
    ) {
        if (MeterCatalog.KindOf(meter) != MeterKind.StateBased) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{meter}' is not a state-based meter and is not sampled. docs/plan/22 § Two kinds "
                + "of meter: an event-based meter is emitted by the provider at the moment, because "
                + "\"sampling would miss everything between samples\"."
            );
        }

        return Build(
            tenantId,
            subscriptionId,
            resourceId,
            resourcePath,
            meter,
            MeterKind.StateBased,
            quantity,
            window,
            region,
            string.Empty,
            emittedAt
        );
    }

    /// <summary>
    ///     Builds an event-based record — the seam a provider emits through at the moment something
    ///     happens.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <param name="resourcePath">The resource's path, for display.</param>
    /// <param name="meter">An event-based meter.</param>
    /// <param name="quantity">How much — a count, or a size.</param>
    /// <param name="window">
    ///     The window the occurrence falls in. A provider that batches a minute of requests reports
    ///     that minute; one reporting a single occurrence reports the window containing it.
    /// </param>
    /// <param name="region">The region.</param>
    /// <param name="eventId">
    ///     ⚠ <b>Required, and the provider owns its stability.</b> A redelivery of the same
    ///     occurrence must carry the same id or it will be counted twice; two different occurrences
    ///     must carry different ids or one will be lost. A batch's id is the batch's id; a single
    ///     occurrence's is whatever the provider already calls it — a request id, a message id.
    ///     Generating a fresh GUID per call defeats the whole mechanism.
    /// </param>
    /// <param name="emittedAt">When the provider emitted. Not a key component.</param>
    /// <returns>The record, or a failure describing what is wrong with the inputs.</returns>
    public static Result<UsageEvent> ForEvent(
        Guid tenantId,
        Guid subscriptionId,
        Guid resourceId,
        string resourcePath,
        BillingMeter meter,
        decimal quantity,
        UsageWindow window,
        string region,
        string eventId,
        DateTimeOffset emittedAt
    ) {
        if (MeterCatalog.KindOf(meter) != MeterKind.EventBased) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{meter}' is not an event-based meter and is not emitted. docs/plan/22 § Two kinds "
                + "of meter: a state-based meter comes from the sampler over the platform's own "
                + "record of what exists, because event-based \"would miss a resource that exists but "
                + "never changes\"."
            );
        }

        if (string.IsNullOrWhiteSpace(eventId)) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"An event-based emission of '{meter}' needs an event id. Two occurrences on the "
                + "same resource inside one window are indistinguishable without it, and the "
                + "idempotency key would collapse them — a dedup that swallows genuine usage is "
                + "worse than a duplicate. See UsageEvent.EventId."
            );
        }

        if (eventId.Contains(Separator, StringComparison.Ordinal)) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"An event id must not contain '{Separator}', which separates the idempotency key's "
                + $"components. '{eventId}' does."
            );
        }

        return Build(
            tenantId,
            subscriptionId,
            resourceId,
            resourcePath,
            meter,
            MeterKind.EventBased,
            quantity,
            window,
            region,
            eventId,
            emittedAt
        );
    }

    /// <summary>
    ///     Whether <see cref="IdempotencyKey" /> is the key this record's own components produce.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The rollup checks this on every ingest and it is not paranoia.</b> A record whose key
    ///     does not match its contents is either hand-built with an initialiser or damaged in
    ///     transit, and accepting it breaks dedup in whichever direction the wrong key happens to
    ///     point: a key that collides with an unrelated record silently discards this one, and a key
    ///     that collides with nothing lets a redelivery through as new usage.
    /// </remarks>
    public bool IsKeyConsistent() =>
        !EventId.Contains(Separator, StringComparison.Ordinal)
        && string.Equals(
            IdempotencyKey,
            KeyFor(ResourceId, Meter, WindowStart, WindowEnd, EventId),
            StringComparison.Ordinal
        );

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Meter} {Quantity} on {(ResourcePath.Length > 0 ? ResourcePath : ResourceId.ToString("N"))} "
            + $"over {Window} [{IdempotencyKey[..Math.Min(12, IdempotencyKey.Length)]}]"
        );

    /// <summary>docs/plan/22 § The pipeline writes the key's components separated by this.</summary>
    const char Separator = '|';

    static Result<UsageEvent> Build(
        Guid tenantId,
        Guid subscriptionId,
        Guid resourceId,
        string resourcePath,
        BillingMeter meter,
        MeterKind kind,
        decimal quantity,
        UsageWindow window,
        string region,
        string eventId,
        DateTimeOffset emittedAt
    ) {
        if (tenantId == Guid.Empty || subscriptionId == Guid.Empty) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                "A usage record names a tenant and a subscription; one of them is empty. An "
                + "unattributed record cannot be billed and cannot be deleted on request."
            );
        }

        if (resourceId == Guid.Empty) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidResourceId,
                "A usage record names a resource by GUID — docs/plan/06 § Identifiers. Guid.Empty is "
                + "what a path parses to before the index resolves it, so an empty one here means "
                + "the record was built from an address that was never resolved."
            );
        }

        if (quantity < 0) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A usage quantity cannot be negative; {quantity} is. Unwinding usage is a ledger "
                + "correction with a reason and a link to the original — docs/plan/22 § The pipeline "
                + "— never a negative sample, which nothing would explain."
            );
        }

        if (window.End <= window.Start) {
            return Result<UsageEvent>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A usage window must be positive and half-open; {window} is not."
            );
        }

        return Result<UsageEvent>.Success(
            new() {
                TenantId = tenantId,
                SubscriptionId = subscriptionId,
                ResourceId = resourceId,
                ResourcePath = resourcePath,
                Meter = meter,
                Kind = kind,
                Quantity = quantity,
                WindowStart = window.Start,
                WindowEnd = window.End,
                Region = region,
                EventId = eventId,
                EmittedAt = emittedAt,
                IdempotencyKey = KeyFor(resourceId, meter, window.Start, window.End, eventId)
            }
        );
    }
}
