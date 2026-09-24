using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Billing.Tax;

/// <summary>
///     The default <see cref="ITaxService" />: EU VAT on an electronically supplied service, from a
///     committed, dated rate table.
/// </summary>
/// <remarks>
///     <para>
///         <b>The decision, in the order it is made.</b>
///     </para>
///     <list type="number">
///         <item>
///             The issuer must be in the EU. This service knows EU VAT and nothing else, and refuses
///             rather than guessing for anyone else.
///         </item>
///         <item>
///             A customer outside the EU is <see cref="TaxTreatment.OutOfScope" />: the place of supply
///             of an electronically supplied service to them is outside the EU, and any local tax is
///             theirs to account for.
///         </item>
///         <item>
///             A business in <i>another</i> member state with a VAT number of the right shape is
///             <see cref="TaxTreatment.ReverseCharge" /> — Council Directive 2006/112/EC, Articles 44
///             and 196 — and the invoice carries the sentence that says so and both VAT numbers.
///         </item>
///         <item>
///             Everyone else in the EU is <see cref="TaxTreatment.Standard" /> at <b>the customer's</b>
///             country's rate: a consumer in another member state because of the place-of-supply rule
///             for electronic services (Article 58, the OSS scheme), and a customer in the issuer's own
///             country because that is the same rate.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             A VAT number is checked for its shape and never for its existence, and the quote says
///             so.
///         </b> Reverse-charging a number that VIES would reject leaves the issuer liable for the
///         VAT it did not charge. The check is the VIES <c>checkVatNumber</c> call, which is network
///         the tests here must not have; <see cref="TaxQuote.VatIdCheck" /> reads <c>format</c> until it
///         is wired, and docs/plan/22 § What is owed, <c>vies-validation</c>, says what it needs.
///     </para>
///     <para>
///         ⚠ <b>The rate in force is the one at the end of the period supplied.</b> A monthly service
///         is supplied over the month and invoiced after it; a member state that changes its rate on
///         the 1st of a month changes it for the month that starts then, which is the next invoice.
///     </para>
/// </remarks>
public sealed partial class EuVatTaxService : ITaxService {
    const string ResourceName = "CyberCloud.Billing.Tax.eu-vat-rates.json";

    /// <summary>The Article 196 sentence a reverse-charged invoice carries, in the words the directive's summary uses.</summary>
    public const string ReverseChargeNote =
        "Reverse charge: VAT to be accounted for by the recipient (Article 196, Council Directive 2006/112/EC).";

    /// <summary>What an invoice to a customer outside the EU says about tax.</summary>
    public const string OutOfScopeNote =
        "Outside the scope of EU VAT: the place of supply is outside the European Union.";

    static readonly Lazy<EuVatTaxService> CommittedService = new(LoadCommitted);

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly FrozenDictionary<string, ImmutableArray<(DateTimeOffset From, decimal Percent)>> rates;
    readonly FrozenDictionary<string, string> prefixes;

    EuVatTaxService(
        FrozenDictionary<string, ImmutableArray<(DateTimeOffset From, decimal Percent)>> rates,
        FrozenDictionary<string, string> prefixes
    ) {
        this.rates = rates;
        this.prefixes = prefixes;
    }

    /// <summary>The service over <c>Tax/eu-vat-rates.json</c>, embedded in this assembly.</summary>
    public static EuVatTaxService Committed => CommittedService.Value;

    /// <summary>The member states the table knows, ISO 3166-1 alpha-2, ordinally.</summary>
    public IEnumerable<string> MemberStates => rates.Keys.Order(StringComparer.Ordinal);

    /// <inheritdoc />
    public Result<TaxQuote> Quote(TaxQuoteRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        if (!rates.ContainsKey(request.Issuer.Country)) {
            return Result<TaxQuote>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The issuer '{request.Issuer.Code}' is in '{request.Issuer.Country}', which is not an EU member "
                + "state. EuVatTaxService quotes EU VAT for an EU issuer and nothing else; a non-EU issuer needs "
                + "the tax service docs/plan/22 § Invoicing and payment names."
            );
        }

        var customer = request.Customer;
        var rounded = MoneyRounding.Round(0m, request.Currency);
        if (rounded.TryGetError(out var currencyError)) {
            return Result<TaxQuote>.Failure(currencyError);
        }

        if (!rates.TryGetValue(customer.Country, out var ladder)) {
            return Result<TaxQuote>.Success(
                new() {
                    Treatment = TaxTreatment.OutOfScope,
                    RatePercent = 0m,
                    Base = request.TaxableAmount,
                    Amount = 0m,
                    Country = string.Empty,
                    Note = OutOfScopeNote,
                    VatIdCheck = string.Empty
                }
            );
        }

        if (customer.IsBusiness
            && !string.Equals(customer.Country, request.Issuer.Country, StringComparison.Ordinal)
            && IsWellFormedVatId(customer.Country, customer.VatId)) {
            return Result<TaxQuote>.Success(
                new() {
                    Treatment = TaxTreatment.ReverseCharge,
                    RatePercent = 0m,
                    Base = request.TaxableAmount,
                    Amount = 0m,
                    Country = customer.Country,
                    Note = $"{ReverseChargeNote} Customer VAT number {customer.VatId}; supplier VAT number {request.Issuer.VatId}.",
                    VatIdCheck = "format"
                }
            );
        }

        var percent = RateAt(ladder, request.SupplyDate);
        if (percent.TryGetError(out var noRate)) {
            return Result<TaxQuote>.Failure(noRate);
        }

        var tax = MoneyRounding.Round(request.TaxableAmount * percent.GetValueOrThrow() / 100m, request.Currency);

        return Result<TaxQuote>.Success(
            new() {
                Treatment = TaxTreatment.Standard,
                RatePercent = percent.GetValueOrThrow(),
                Base = request.TaxableAmount,
                Amount = tax.GetValueOrThrow(),
                Country = customer.Country,
                Note = string.Empty,
                VatIdCheck = customer.VatId.Length > 0 ? "format" : string.Empty
            }
        );
    }

    /// <inheritdoc />
    public Result CheckCustomer(BillingProfile customer) {
        ArgumentNullException.ThrowIfNull(customer);

        if (customer.VatId.Length > 0 && IsMemberState(customer.Country) && !IsWellFormedVatId(customer.Country, customer.VatId)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{customer.VatId}' is not the shape of a {customer.Country} VAT number: it starts with "
                + $"'{prefixes[customer.Country]}' and continues with two to thirteen letters and digits, no spaces.",
                "/vatId"
            );
        }

        return Result.Success;
    }

    /// <summary>
    ///     Whether a VAT number has the shape of one from a member state: that state's prefix, then
    ///     two to thirteen letters and digits. ⚠ The shape, not the existence — see the type's remarks.
    /// </summary>
    /// <param name="country">The member state, ISO 3166-1 alpha-2.</param>
    /// <param name="vatId">The number, prefix included.</param>
    public bool IsWellFormedVatId(string country, string? vatId) =>
        vatId is not null
        && prefixes.TryGetValue(country, out var prefix)
        && vatId.StartsWith(prefix, StringComparison.Ordinal)
        && VatBody().IsMatch(vatId[prefix.Length..]);

    /// <summary>Whether a country is an EU member state the table knows.</summary>
    /// <param name="country">ISO 3166-1 alpha-2.</param>
    public bool IsMemberState(string? country) => country is not null && rates.ContainsKey(country);

    /// <summary>Parses a rate table in <c>eu-vat-rates.json</c>'s shape.</summary>
    /// <param name="json">The table.</param>
    /// <returns>The service, or <see cref="ErrorCode.InvalidRequestBody" /> naming the first bad row.</returns>
    public static Result<EuVatTaxService> Parse(string json) {
        RateDocument? document;

        try {
            document = JsonSerializer.Deserialize<RateDocument>(json, JsonOptions);
        } catch (JsonException error) {
            return Invalid($"The VAT rate table is not valid JSON: {error.Message}");
        }

        if (document?.Rates is not { Count: > 0 } rows) {
            return Invalid("The VAT rate table has no rows.");
        }

        var byCountry = new Dictionary<string, List<(DateTimeOffset, decimal)>>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows) {
            if (row.Country is not { Length: 2 } country || !country.All(char.IsAsciiLetterUpper)) {
                return Invalid($"'{row.Country}' is not an ISO 3166-1 alpha-2 code.");
            }

            if (row.VatPrefix is not { Length: 2 } prefix) {
                return Invalid($"{country}'s VAT prefix '{row.VatPrefix}' is not two letters.");
            }

            if (row.Percent is < 0 or >= 100) {
                return Invalid($"{country}'s rate {row.Percent} is not a percentage.");
            }

            if (!DateTimeOffset.TryParseExact(
                    row.From,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var from
                )) {
                return Invalid($"{country}'s row date '{row.From}' is not yyyy-MM-dd.");
            }

            if (prefixes.TryGetValue(country, out var seen) && !string.Equals(seen, prefix, StringComparison.Ordinal)) {
                return Invalid($"{country} is given two VAT prefixes, '{seen}' and '{prefix}'.");
            }

            prefixes[country] = prefix;

            var ladder = byCountry.TryGetValue(country, out var existing) ? existing : byCountry[country] = [];
            if (ladder.Count > 0 && ladder[^1].Item1 >= from) {
                return Invalid($"{country}'s rows are not in date order at {row.From}.");
            }

            ladder.Add((from, row.Percent));
        }

        return Result<EuVatTaxService>.Success(
            new(
                byCountry.ToFrozenDictionary(static x => x.Key, static x => x.Value.ToImmutableArray(), StringComparer.Ordinal),
                prefixes.ToFrozenDictionary(StringComparer.Ordinal)
            )
        );
    }

    static Result<decimal> RateAt(ImmutableArray<(DateTimeOffset From, decimal Percent)> ladder, DateTimeOffset at) {
        decimal? found = null;

        foreach (var (from, percent) in ladder) {
            if (from <= at) {
                found = percent;
            }
        }

        return found is { } rate
            ? Result<decimal>.Success(rate)
            : Result<decimal>.Failure(
                ErrorCode.InvalidRequestBody,
                string.Create(CultureInfo.InvariantCulture, $"The VAT rate table has no rate in force at {at:O}.")
            );
    }

    static EuVatTaxService LoadCommitted() {
        using var stream = typeof(EuVatTaxService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded; the .csproj's EmbeddedResource line names it.");

        using var reader = new StreamReader(stream);
        var parsed = Parse(reader.ReadToEnd());

        return parsed.TryGetError(out var error)
            ? throw new InvalidOperationException($"The committed VAT rate table does not parse: {error.Message}")
            : parsed.GetValueOrThrow();
    }

    static Result<EuVatTaxService> Invalid(string message) =>
        Result<EuVatTaxService>.Failure(ErrorCode.InvalidRequestBody, message);

    [GeneratedRegex("^[0-9A-Z]{2,13}$", RegexOptions.CultureInvariant)]
    private static partial Regex VatBody();

    sealed class RateDocument {
        public List<RateRow>? Rates { get; set; }
    }

    sealed class RateRow {
        public string? Country { get; set; }

        public string? VatPrefix { get; set; }

        public string? From { get; set; }

        public decimal Percent { get; set; }
    }
}
