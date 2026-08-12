using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     How much of a meter a body draws, when the amount is not a plain number sitting at a pointer.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The wall this exists to get past.</b> <see cref="MeterRegistration" /> reserved the
///         <i>number</i> found at a JSON pointer, and no managed service has one. A PostgreSQL server
///         spells its vCPU <c>500m</c>, its memory <c>2Gi</c> and its disk <c>20Gi</c> — strings — and
///         in the ordinary case spells none of them at all, because <c>sizing.preset</c> names them
///         indirectly and the explicit overrides default to <c>""</c>. So the provider could declare
///         <see cref="QuotaMeter.Resources" /> and nothing else, and quota was enforced over
///         everything except the two things a customer buys.
///     </para>
///     <para>
///         ⚠ <b>A unit on the pointer would not have been enough, and that is the finding.</b> Reading
///         <c>/properties/sizing/cpu</c> as a quantity still yields nothing for the body
///         <c>PostgresServers.Body</c> builds — the property is absent and the preset is what says
///         <c>500m</c> — and it still cannot express <c>replicas × per-instance</c>, which is what a
///         two-instance cluster actually consumes. A pointer addresses one value; the amounts here are
///         functions of several. <see cref="Quantity" /> keeps the cheap case cheap without needing a
///         second mechanism for it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Amount" /> must be a pure function of the body, and the delete path is why.</b>
///         <c>ResourceManagerService.CommittedBy</c> re-derives a resource's committed amounts from its
///         <i>stored superset</i> at delete time, through the same step the create reserved with, so
///         that a delete returns exactly the number the create committed. A derivation that consulted a
///         clock, configuration, a preset table that ships separately from the stored body, or anything
///         else outside its argument would break that symmetry — and the symptom is quota drifting
///         upward on every create/delete cycle until a subscription is refused creates it is entitled
///         to. That defect has happened here once already. <see cref="Reads" /> is the declaration of
///         what the function is allowed to touch, and a reviewer can check the lambda against it.
///     </para>
///     <para>
///         <b>Why a delegate is acceptable here when <c>IResourceTypeBuilder.Meter</c> argues against
///         one.</b> That argument is that a delegate "validates and reserves but cannot be
///         <i>generated</i> from". It is right, and the repair is not to drop the delegate but to make
///         it carry its own description: <see cref="Expression" /> and <see cref="Reads" /> are
///         required, they are what <c>OpenApiEmitter</c> publishes, and a derivation therefore says in
///         the generated document what it computes and from which properties. What is lost against a
///         bare pointer is that a generator cannot re-evaluate the formula; what is gained is a
///         published formula rather than a published number that was never right.
///     </para>
/// </remarks>
public sealed record MeterDerivation {
    /// <summary>
    ///     What this computes, in one line, for the humans and the generated document — for example
    ///     <c>replicas × sizing.cpu (from sizing.preset when unset)</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Required rather than optional. A meter whose amount is a lambda and whose document says
    ///     nothing is the state <c>IResourceTypeBuilder.Meter</c>'s remarks refuse; this member is the
    ///     price of the seam.
    /// </remarks>
    public required string Expression { get; init; }

    /// <summary>
    ///     Every RFC 6901 pointer <see cref="Amount" /> reads, so the declaration says what the
    ///     function depends on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not enforced at runtime, and still worth declaring.</b> Nothing sandboxes the delegate,
    ///     so this is a claim a reviewer checks rather than a constraint the platform applies. It earns
    ///     its place three ways: it is what a portal or CLI shows when asked "what moves this quota", it
    ///     is what an api-version bump has to be diffed against when a property is renamed, and it makes
    ///     the purity claim above concrete enough to argue with.
    /// </remarks>
    public required ImmutableArray<string> Reads { get; init; }

    /// <summary>
    ///     The function itself: a body in, an amount or a stated refusal out.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Returning a failure is the point, and it is why this is not <c>Func&lt;JsonElement,
    ///     decimal&gt;</c>.</b> A derivation that cannot resolve — a preset it does not know, a quantity
    ///     that does not parse, a property that was renamed out from under it — must be able to say so.
    ///     The alternative is reserving zero, and zero passes: the write succeeds, the resource
    ///     provisions, and nobody is charged. See <c>ResourceManagerService.AmountFor</c>, which refuses
    ///     the write on a failure here.
    /// </remarks>
    public required Func<JsonElement, Result<decimal>> Amount { get; init; }

    /// <summary>
    ///     A derivation that reads one Kubernetes quantity at one pointer — the cheap case, without a
    ///     second mechanism for it.
    /// </summary>
    /// <param name="amountPointer">The RFC 6901 pointer to the quantity string.</param>
    /// <param name="unit">What the parsed value should be expressed in.</param>
    /// <param name="fallback">
    ///     The quantity to use when the pointer is absent, or <see langword="null" /> to refuse. ⚠
    ///     <see langword="null" /> is the default on purpose — see
    ///     <c>ResourceManagerService.AmountFor</c>.
    /// </param>
    /// <returns>The derivation.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="amountPointer" /> is not a pointer, or <paramref name="fallback" /> is not a
    ///     quantity this platform accepts — both caught at silo start rather than at reserve time.
    /// </exception>
    public static MeterDerivation Quantity(string amountPointer, QuantityUnit unit, string? fallback = null) {
        ArgumentException.ThrowIfNullOrEmpty(amountPointer);

        if (amountPointer[0] != '/') {
            throw new ArgumentException(
                $"'{amountPointer}' is not a JSON Pointer. A meter's amount is addressed the same way an "
                + "error target is — docs/plan/08 § Errors — so it begins with '/'.",
                nameof(amountPointer)
            );
        }

        decimal? absent = null;
        if (fallback is not null) {
            if (!Convert(fallback, unit, out var parsed)) {
                throw new ArgumentException(
                    $"'{fallback}' is not a Kubernetes quantity, so it cannot be what '{amountPointer}' "
                    + "reserves when it is absent. The accepted form is "
                    + "PostgresServers.QuantityPattern — see KubeQuantity.",
                    nameof(fallback)
                );
            }

            absent = parsed;
        }

        var name = amountPointer + (unit is QuantityUnit.Gibibytes ? " as GiB" : string.Empty);

        return new() {
            Expression = absent is { } value
                ? $"{name} (or {value.ToString(System.Globalization.CultureInfo.InvariantCulture)} when absent)"
                : name,
            Reads = [amountPointer],
            Amount = body => Read(body, amountPointer, unit, absent)
        };
    }

    /// <summary>A derivation the provider writes itself, with the description it has to carry.</summary>
    /// <param name="expression">What it computes, in one line. See <see cref="Expression" />.</param>
    /// <param name="reads">Every pointer <paramref name="amount" /> reads. See <see cref="Reads" />.</param>
    /// <param name="amount">
    ///     The function. ⚠ Must be pure in its argument — see this type's remarks on why the delete path
    ///     depends on that.
    /// </param>
    /// <returns>The derivation.</returns>
    public static MeterDerivation Of(
        string expression,
        ImmutableArray<string> reads,
        Func<JsonElement, Result<decimal>> amount
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(amount);

        if (reads.IsDefaultOrEmpty) {
            throw new ArgumentException(
                $"The derivation '{expression}' declares no pointers. A derivation that reads nothing "
                + "from the body is a constant, and a constant amount is `Meters(meter)` or "
                + "`Meter(meter, \"\", fallback)` rather than a function.",
                nameof(reads)
            );
        }

        return new() { Expression = expression, Reads = reads, Amount = amount };
    }

    /// <summary>Resolves a pointer against a body. ⚠ Returns <see langword="null" /> for absent.</summary>
    /// <param name="body">The body.</param>
    /// <param name="amountPointer">The RFC 6901 pointer.</param>
    /// <returns>The element, or <see langword="null" /> when any segment is missing.</returns>
    /// <remarks>
    ///     ⚠ Shared with <c>ResourceManagerService.AmountFor</c> by being the only implementation a
    ///     derivation has to hand, so a provider does not write a fourth pointer walker. The escaping
    ///     rules of RFC 6901 (<c>~0</c>, <c>~1</c>) are not applied, which matches every other pointer
    ///     walk in the registry and is honest because no declared pointer contains <c>/</c> or
    ///     <c>~</c> in a segment.
    /// </remarks>
    public static JsonElement? Resolve(JsonElement body, string amountPointer) {
        ArgumentException.ThrowIfNullOrEmpty(amountPointer);

        var current = body;
        foreach (var token in amountPointer.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(token, out var next)) {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Reads a quantity-valued string at a pointer, in the unit asked for.</summary>
    /// <param name="body">The body.</param>
    /// <param name="amountPointer">The pointer.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="value">The value on success.</param>
    /// <returns><c>true</c> when the pointer holds a quantity this platform accepts.</returns>
    /// <remarks>
    ///     ⚠ An empty string is <b>not</b> a quantity and is not zero. Every optional quantity in this
    ///     platform's schemas defaults to <c>""</c> meaning "take it from somewhere else"
    ///     (<c>PostgresServers.OptionalQuantityPattern</c>), so reading it as zero would silently meter
    ///     a preset-sized server at nothing.
    /// </remarks>
    public static bool TryQuantityAt(JsonElement body, string amountPointer, QuantityUnit unit, out decimal value) {
        value = 0m;

        return Resolve(body, amountPointer) is { ValueKind: JsonValueKind.String } element
            && Convert(element.GetString(), unit, out value);
    }

    static bool Convert(string? text, QuantityUnit unit, out decimal value) =>
        unit is QuantityUnit.Gibibytes
            ? KubeQuantity.TryGibibytes(text, out value)
            : KubeQuantity.TryParse(text, out value);

    static Result<decimal> Read(JsonElement body, string amountPointer, QuantityUnit unit, decimal? absent) {
        if (TryQuantityAt(body, amountPointer, unit, out var value)) {
            return Result<decimal>.Success(value);
        }

        if (absent is { } fallback && Resolve(body, amountPointer) is null) {
            return Result<decimal>.Success(fallback);
        }

        return Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"'{amountPointer}' does not hold a Kubernetes quantity, so the amount this resource draws "
            + "cannot be determined and the write is refused. Reserving nothing would provision the "
            + "resource and charge for none of it — docs/plan/06 § Quota."
        );
    }
}

/// <summary>What a parsed Kubernetes quantity should be expressed in.</summary>
/// <remarks>
///     ⚠ There is no <c>Bytes</c>. <see cref="QuotaMeter.MemoryGb" /> and
///     <see cref="QuotaMeter.StorageGb" /> are gibibyte meters and <see cref="QuotaMeter.Vcpu" /> is a
///     core meter, so those are the two units a quota amount can be in; a byte-valued meter would be a
///     new <see cref="QuotaMeter" /> and would be declared with the rest of docs/plan/06 § Quota's
///     families rather than here.
/// </remarks>
// ⚠ Nothing serializes this — it is read at silo start and never crosses a wire — and it carries an
// [Alias] anyway, because ResourceManagerSerializationTests holds every public enum in this assembly
// to the rule rather than to a judgement about which ones travel. A judgement is what gets it wrong
// the day one of them starts travelling.
[Alias("CyberCloud.ResourceManager.QuantityUnit")]
public enum QuantityUnit {
    /// <summary>The quantity's own base unit — cores for a CPU quantity. <c>500m</c> is <c>0.5</c>.</summary>
    Base = 0,

    /// <summary>Gibibytes, for a byte-valued quantity. <c>4Gi</c> is <c>4</c> and <c>512Mi</c> is <c>0.5</c>.</summary>
    Gibibytes = 1
}
