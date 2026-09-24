using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Billing.Pricing;

/// <summary>One tier of a meter's price: up to a cumulative monthly quantity, at a unit price.</summary>
/// <param name="UpTo">
///     The cumulative quantity in the month this tier ends at, exclusive of what follows; <see langword="null" />
///     on the last tier, which has no end.
/// </param>
/// <param name="UnitPrice">The price of one unit inside the tier. Zero is a free tier.</param>
public sealed record PriceTier(decimal? UpTo, decimal UnitPrice);

/// <summary>What one meter costs in one price sheet version.</summary>
/// <param name="Meter">The meter.</param>
/// <param name="Unit">The unit, which must be <c>MeterCatalog</c>'s spelling.</param>
/// <param name="Currency">The currency the prices are in.</param>
/// <param name="Tiers">The tiers, in ascending order of <see cref="PriceTier.UpTo" />.</param>
public sealed record MeterPrice(BillingMeter Meter, string Unit, string Currency, ImmutableArray<PriceTier> Tiers);

/// <summary>A price sheet version: every meter's price, in force from a month's first instant.</summary>
/// <param name="EffectiveFrom">The first instant it is in force, UTC — always the first of a month.</param>
/// <param name="Meters">Every meter's price.</param>
public sealed record PriceSheetVersion(DateTimeOffset EffectiveFrom, ImmutableDictionary<BillingMeter, MeterPrice> Meters);

/// <summary>
///     The platform's prices, versioned by effective date — docs/plan/22 § Rating: "Price lists are
///     versioned … with an effective date. A price change never applies retroactively; the rating
///     engine picks the list in effect at the usage window."
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Committed data and not a <c>CyberCloud.Billing/priceLists</c> resource, which is what
///             docs/plan/22 § Rating's "everything is a resource" line asks for.
///         </b> A price is the platform's decision, not a tenant's desired state: no tenant writes one,
///         and a resource type nobody may PUT would be a read-only document with a write path to
///         guard. So the sheet is <c>Pricing/price-sheet.json</c>, reviewed like code, and the doc's
///         sentence is corrected in docs/plan/22. Per-tenant negotiated prices — the enterprise
///         agreement row, M3 — are where a resource starts to make sense.
///     </para>
///     <para>
///         ⚠ <b>A version starts a month, and <see cref="Parse" /> refuses one that does not.</b>
///         Tiers are per calendar month (docs/plan/22: "per meter per month"); a version starting on
///         the 17th would put two prices under one tier ladder and one invoice line, with nothing to
///         say which hours were at which price. A month boundary means every month is rated against
///         exactly one version, and an invoice line is one price.
///     </para>
///     <para>
///         ⚠ <b>Total over <see cref="BillingMeter" />.</b> Every declared meter is priced in every
///         version, at zero when it is free, because a meter without a price is usage nobody is
///         charged for — the same argument <see cref="MeterCatalog" /> makes for being total over
///         <c>QuotaMeter</c>. <c>PriceSheetTests</c> reads the committed file and holds it to
///         that.
///     </para>
/// </remarks>
public sealed class PriceSheet {
    const string ResourceName = "CyberCloud.Billing.Pricing.price-sheet.json";

    static readonly Lazy<PriceSheet> CommittedSheet = new(LoadCommitted);

    PriceSheet(ImmutableArray<PriceSheetVersion> versions) => Versions = versions;

    /// <summary>The sheet in <c>Pricing/price-sheet.json</c>, embedded in this assembly.</summary>
    /// <exception cref="InvalidOperationException">The committed file does not parse — a bug, caught by a test before it ships.</exception>
    public static PriceSheet Committed => CommittedSheet.Value;

    /// <summary>Every version, oldest first.</summary>
    public ImmutableArray<PriceSheetVersion> Versions { get; }

    /// <summary>The version in force at an instant.</summary>
    /// <param name="instant">Any instant. A usage window is priced by its start.</param>
    /// <returns>The version, or <see cref="ErrorCode.InvalidRequestBody" /> for an instant before the first one.</returns>
    public Result<PriceSheetVersion> At(DateTimeOffset instant) {
        PriceSheetVersion? found = null;

        foreach (var version in Versions) {
            if (version.EffectiveFrom <= instant) {
                found = version;
            }
        }

        return found is not null
            ? Result<PriceSheetVersion>.Success(found)
            : Result<PriceSheetVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No price sheet version is in force at {instant:O}; the first is in force from "
                    + $"{Versions[0].EffectiveFrom:O}. Usage from before the platform had prices is not rated."
                )
            );
    }

    /// <summary>Parses and validates a price sheet document.</summary>
    /// <param name="json">The document, in <c>price-sheet.json</c>'s shape.</param>
    /// <returns>The sheet, or <see cref="ErrorCode.InvalidRequestBody" /> naming the first rule a version breaks.</returns>
    public static Result<PriceSheet> Parse(string json) {
        Document? document;

        try {
            document = JsonSerializer.Deserialize<Document>(json, JsonOptions);
        } catch (JsonException error) {
            return Invalid($"The price sheet is not valid JSON: {error.Message}");
        }

        if (document?.Versions is not { Count: > 0 } raw) {
            return Invalid("The price sheet has no versions. Nothing could be rated.");
        }

        var versions = ImmutableArray.CreateBuilder<PriceSheetVersion>(raw.Count);
        DateTimeOffset? previous = null;

        foreach (var version in raw) {
            var at = version.EffectiveFrom.ToUniversalTime();

            if (at != new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero)) {
                return Invalid(
                    $"Version {version.EffectiveFrom:O} does not start a month. A version is in force from the "
                    + "first instant of a month in UTC — a mid-month price would split one tier ladder and one "
                    + "invoice line between two prices."
                );
            }

            if (previous is { } last && at <= last) {
                return Invalid($"Version {at:O} is not later than the version before it ({last:O}). Versions are in date order.");
            }

            previous = at;

            var meters = ImmutableDictionary.CreateBuilder<BillingMeter, MeterPrice>();

            foreach (var price in version.Meters ?? []) {
                var parsed = ParseMeter(at, price);
                if (parsed.TryGetError(out var error)) {
                    return Result<PriceSheet>.Failure(error);
                }

                var meter = parsed.GetValueOrThrow();
                if (!meters.TryAdd(meter.Meter, meter)) {
                    return Invalid($"Version {at:O} prices {meter.Meter} twice.");
                }
            }

            foreach (var definition in MeterCatalog.Definitions) {
                if (!meters.ContainsKey(definition.Meter)) {
                    return Invalid(
                        $"Version {at:O} does not price {definition.Meter}. Every meter is priced in every "
                        + "version, at 0.0 when it is free — an unpriced meter is usage nobody is charged for."
                    );
                }
            }

            versions.Add(new(at, meters.ToImmutable()));
        }

        return Result<PriceSheet>.Success(new(versions.ToImmutable()));
    }

    static Result<MeterPrice> ParseMeter(DateTimeOffset version, MeterDocument price) {
        if (!Enum.TryParse<BillingMeter>(price.Meter, false, out var meter)
            || meter == BillingMeter.Unknown
            || !Enum.IsDefined(meter)) {
            return InvalidMeter($"Version {version:O} prices '{price.Meter}', which is not a BillingMeter.");
        }

        var definition = MeterCatalog.Define(meter);
        if (definition.TryGetError(out var undefined)) {
            return Result<MeterPrice>.Failure(undefined);
        }

        if (!string.Equals(price.Unit, definition.GetValueOrThrow().Unit, StringComparison.Ordinal)) {
            return InvalidMeter(
                $"Version {version:O} prices {meter} per '{price.Unit}', and the meter counts "
                + $"'{definition.GetValueOrThrow().Unit}'. A unit that disagrees with MeterCatalog prices a "
                + "quantity the meter never reports."
            );
        }

        if (!Currencies.IsKnown(price.Currency)) {
            return InvalidMeter($"Version {version:O} prices {meter} in '{price.Currency}', which Currencies does not list.");
        }

        if (price.Tiers is not { Count: > 0 } tiers) {
            return InvalidMeter($"Version {version:O} gives {meter} no tiers.");
        }

        decimal? floor = 0m;

        for (var i = 0; i < tiers.Count; i++) {
            var tier = tiers[i];
            var last = i == tiers.Count - 1;

            if (tier.UnitPrice < 0) {
                return InvalidMeter($"Version {version:O} prices a tier of {meter} below zero. A credit is a credit note.");
            }

            if (last != tier.UpTo is null) {
                return InvalidMeter(
                    $"Version {version:O}'s tiers for {meter} are not closed: every tier but the last ends at an "
                    + "upTo, and the last has none — a ladder that ends leaves usage past it unpriced."
                );
            }

            if (tier.UpTo is { } upTo && upTo <= floor) {
                return InvalidMeter($"Version {version:O}'s tiers for {meter} do not ascend: {upTo} follows {floor}.");
            }

            floor = tier.UpTo;
        }

        return Result<MeterPrice>.Success(
            new(meter, price.Unit!, price.Currency!, [.. tiers.Select(static x => new PriceTier(x.UpTo, x.UnitPrice))])
        );
    }

    static PriceSheet LoadCommitted() {
        using var stream = typeof(PriceSheet).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"{ResourceName} is not embedded in {typeof(PriceSheet).Assembly.GetName().Name}. The .csproj's "
                + "EmbeddedResource line names it."
            );

        using var reader = new StreamReader(stream);
        var parsed = Parse(reader.ReadToEnd());

        return parsed.TryGetError(out var error)
            ? throw new InvalidOperationException($"The committed price sheet does not parse: {error.Message}")
            : parsed.GetValueOrThrow();
    }

    static Result<PriceSheet> Invalid(string message) => Result<PriceSheet>.Failure(ErrorCode.InvalidRequestBody, message);

    static Result<MeterPrice> InvalidMeter(string message) => Result<MeterPrice>.Failure(ErrorCode.InvalidRequestBody, message);

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed class Document {
        public List<VersionDocument>? Versions { get; set; }
    }

    sealed class VersionDocument {
        public DateTimeOffset EffectiveFrom { get; set; }

        public List<MeterDocument>? Meters { get; set; }
    }

    sealed class MeterDocument {
        public string? Meter { get; set; }

        public string? Unit { get; set; }

        public string? Currency { get; set; }

        public List<TierDocument>? Tiers { get; set; }
    }

    sealed class TierDocument {
        public decimal? UpTo { get; set; }

        public decimal UnitPrice { get; set; }
    }
}
