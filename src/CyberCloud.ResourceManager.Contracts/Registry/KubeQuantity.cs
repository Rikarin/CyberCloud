using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>
///     The one place a Kubernetes quantity string becomes a number the quota ledger can hold.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every amount a managed service actually meters is one of these, and none of them is a
///         JSON number.</b> <c>CyberCloud.DBforPostgreSQL/servers</c> spells its vCPU as <c>500m</c>,
///         its memory as <c>2Gi</c> and its data volume as <c>20Gi</c>, so a meter that could only read
///         a number could declare nothing but <see cref="QuotaMeter.Resources" /> — a count of one —
///         while the two things a customer buys stayed outside quota entirely. This type is the
///         conversion that closes that, and <see cref="MeterDerivation" /> is the seam it is reached
///         through.
///     </para>
///     <para>
///         ⚠ <b>There is exactly one of these in the platform and there must stay exactly one.</b> Two
///         quantity parsers that disagree by a rounding rule is a billing defect that shows up as a
///         subscription refused a create it was entitled to, or granted one it was not. A provider that
///         needs a quantity calls <see cref="TryParse" /> or <see cref="TryGibibytes" /> rather than
///         writing its own.
///     </para>
///     <para>
///         ⚠ <b>That paragraph used to end "so there is no second implementation to reconcile with",
///         and a second implementation was written anyway.</b>
///         <c>ValkeyCaches.QuantityBytes</c> parsed the same grammar in <see langword="double" />,
///         truncated, and returned <see langword="null" /> where this refuses — written by an author
///         who could not see this file, because the Valkey one lived in a provider's
///         <c>.Contracts</c> assembly. Consolidated 2026-08-12; the two agreed on every verdict and
///         disagreed on <i>value</i>, which is the quieter half: <c>8.7T</c> came back as
///         8699999999999 rather than 8700000000000, and <c>8Ei</c> came back as
///         <see cref="long.MaxValue" /> rather than being refused, because a
///         <see langword="double" /> compares equal to <c>(double)long.MaxValue</c> one byte past it.
///         The lesson is in <see cref="Pattern" />: the grammar and the parser live together now, so
///         a provider that needs the one finds the other.
///     </para>
///     <para>
///         <b>The conversion is exact, and that is a decision rather than an accident.</b> Everything
///         downstream — <c>IQuotaGrain.TryReserveAsync</c>, <see cref="QuotaLease.Amount" />,
///         <see cref="QuotaUsage.Committed" />, <c>UsageEvent.Quantity</c> — is
///         <see langword="decimal" />, so nothing forces an integer and nothing has to be rounded.
///         <c>500m</c> reserves <c>0.5</c> vCPU, not <c>0</c> (free CPU) and not <c>1</c> (an
///         overcharge). Every suffix this accepts is a power of ten or a power of two, and both have
///         terminating decimal expansions inside <see langword="decimal" />'s 28 significant digits, so
///         no value this parses is approximated. ⚠ The moment something downstream takes an
///         <see langword="int" />, that statement stops being true and the rounding direction becomes a
///         decision somebody has to make and write down.
///     </para>
///     <para>
///         ⚠ <b>The accepted grammar is the platform's, not apimachinery's.</b> It is
///         <see cref="Pattern" />, deliberately narrower than upstream: no sign, no exponent form, no
///         fractional binary suffix. A body reaches this only after <c>ResourceSchema.Validate</c> has
///         checked that pattern, so anything this refuses is a declaration bug rather than a caller's;
///         refusing it loudly is the point.
///     </para>
/// </remarks>
public static class KubeQuantity {
    /// <summary>
    ///     The quantity form this platform accepts — <c>500m</c>, <c>2</c>, <c>4Gi</c> — for a schema
    ///     property's <c>Pattern</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The grammar lives beside the parser because separating them is how the platform got a
    ///     second parser.</b> Each provider declared its own copy of this string, so
    ///     <c>ValkeyCaches.QuantityPattern</c> was reachable from a provider that had never heard of
    ///     <see cref="KubeQuantity" /> — and the author who needed to turn one of these into a number
    ///     wrote the conversion where the pattern was. Anything that validates a quantity and anything
    ///     that reads one now start in the same file.
    ///     <para>
    ///         ⚠ It is narrower than apimachinery's on purpose: no sign, no exponent, no fractional
    ///         binary suffix. A negative or exponent-form CPU request is legal to the API server and is
    ///         never what a tenant meant.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>PostgresServers</c>, <c>KafkaClusters</c> and <c>TestProvider</c> still declare
    ///         their own byte-identical copies. Those are three assemblies this change does not own;
    ///         pointing them here is the remaining half of the consolidation, and
    ///         <c>QuantityParserTests</c> is what notices if one of them drifts into a parser instead.
    ///     </para>
    /// </remarks>
    public const string Pattern = @"\d+(\.\d+)?(m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?";

    /// <summary>A quantity or the empty string, for a field whose default is "take it from elsewhere".</summary>
    /// <remarks>
    ///     ⚠ The empty alternative is required rather than cosmetic: <see cref="SchemaProperty" />
    ///     checks its own <see cref="SchemaProperty.DefaultJson" /> against its own constraints at
    ///     construction, so a pattern that refused <c>""</c> on a property defaulting to <c>""</c> would
    ///     throw at silo start. ⚠ <c>""</c> is still not a quantity to <see cref="TryParse" /> and is
    ///     not zero — see <c>MeterDerivation.TryQuantityAt</c>.
    /// </remarks>
    public const string OptionalPattern = "(" + Pattern + ")?";

    /// <summary>One gibibyte, in bytes. The divisor <see cref="TryGibibytes" /> applies.</summary>
    /// <remarks>
    ///     ⚠ <c>2^30</c>, not <c>10^9</c>. <c>4Gi</c> is 4294967296 bytes and <c>4G</c> is 4000000000;
    ///     a meter that treated them alike would be wrong by 7% in the direction of undercharging on
    ///     every binary-suffixed value, which is every value a real body carries.
    /// </remarks>
    public const decimal BytesPerGibibyte = 1073741824m;

    /// <summary>
    ///     Parses a quantity into its base unit — cores for a CPU quantity, bytes for a memory or
    ///     storage one.
    /// </summary>
    /// <param name="text">The quantity, for example <c>500m</c>, <c>2</c>, <c>4Gi</c>.</param>
    /// <param name="value">The value in base units, exact, on success.</param>
    /// <returns><c>true</c> when <paramref name="text" /> is a quantity this platform accepts.</returns>
    /// <remarks>
    ///     ⚠ <b>The suffix is what a quantity means, and the two families do not overlap.</b> <c>k</c>
    ///     is 1000 and <c>Ki</c> is 1024; <c>m</c> is a <i>milli</i> and is the only suffix below one.
    ///     A parser that folded <c>k</c> and <c>Ki</c> together would silently undercount by 2.4%, and
    ///     one that read <c>m</c> as <c>M</c> would overcount by a factor of a billion — which is why
    ///     the comparison below is ordinal and case-sensitive rather than convenient.
    /// </remarks>
    public static bool TryParse(string? text, out decimal value) {
        value = 0m;

        if (string.IsNullOrEmpty(text)) {
            return false;
        }

        var digits = 0;
        while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] == '.')) {
            digits++;
        }

        if (digits == 0
            || !decimal.TryParse(
                text.AsSpan(0, digits),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var mantissa
            )) {
            return false;
        }

        var suffix = text.AsSpan(digits);

        // ⚠ Multiplication rather than division for every suffix but `m`, so no intermediate is
        // rounded. `0.5Gi` is 0.5 × 2^30 and lands on 536870912 exactly; computing it as a fraction of
        // a gibibyte first would be the same number here and would not be for a suffix whose factor is
        // not a power of two.
        var factor = suffix switch {
            "" => 1m,
            "m" => 0.001m,
            "k" => 1_000m,
            "M" => 1_000_000m,
            "G" => 1_000_000_000m,
            "T" => 1_000_000_000_000m,
            "P" => 1_000_000_000_000_000m,
            "E" => 1_000_000_000_000_000_000m,
            "Ki" => 1_024m,
            "Mi" => 1_048_576m,
            "Gi" => BytesPerGibibyte,
            "Ti" => 1_099_511_627_776m,
            "Pi" => 1_125_899_906_842_624m,
            "Ei" => 1_152_921_504_606_846_976m,
            _ => 0m
        };

        if (factor == 0m) {
            return false;
        }

        try {
            value = mantissa * factor;
        } catch (OverflowException) {
            // A mantissa long enough to overflow decimal at Ei is not a body anybody sent; it is a
            // declaration or a fuzz case. Refusing beats reserving a wrapped number.
            return false;
        }

        return true;
    }

    /// <summary>Parses a byte-valued quantity and converts it to gibibytes.</summary>
    /// <param name="text">The quantity, for example <c>4Gi</c>, <c>512Mi</c>, <c>20Gi</c>.</param>
    /// <param name="value">The size in GiB, exact, on success.</param>
    /// <returns><c>true</c> when <paramref name="text" /> is a quantity this platform accepts.</returns>
    /// <remarks>
    ///     ⚠ <see cref="QuotaMeter.MemoryGb" /> and <see cref="QuotaMeter.StorageGb" /> are named
    ///     "Gb" and are <b>gibibytes</b> — docs/plan/06 § Quota's families, and
    ///     <c>MeterCatalog</c>'s <c>GiB-hour</c> and <c>GiB-month</c> units, which is the vocabulary
    ///     the billing side already speaks. A conversion that used 10^9 here would put quota and
    ///     billing 7% apart on the same resource.
    /// </remarks>
    public static bool TryGibibytes(string? text, out decimal value) {
        if (!TryParse(text, out var bytes)) {
            value = 0m;
            return false;
        }

        value = bytes / BytesPerGibibyte;
        return true;
    }
}
