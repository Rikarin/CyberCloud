using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Pricing;

/// <summary>
///     Turns a month of rated hours into an invoice's lines, subtotal, tax and total — the pure half
///     of <c>BillingAccountGrain</c>, so the arithmetic is testable without a silo.
/// </summary>
public static class InvoiceBuilder {
    /// <summary>
    ///     What an invoice says about a line whose quantity is a declared size — docs/plan/22 § What is
    ///     owed, <c>storage-is-declared-not-observed</c>.
    /// </summary>
    public const string DeclaredQuantityNote =
        "Storage quantities are the size each resource declares, not a measurement of the volume "
        + "provisioned. They differ while an expansion is pending or after one failed (billed high), and "
        + "after a shrink the cluster refused (billed low); a difference found later is corrected by credit note.";

    /// <summary>The meters rated on a declared size rather than an observed one.</summary>
    public static ImmutableHashSet<BillingMeter> DeclaredMeters { get; } =
        [BillingMeter.StorageGbMonths, BillingMeter.BackupGbMonths];

    /// <summary>One subscription's rated month, grouped into invoice lines.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="rated">Its rated hours for the month.</param>
    /// <param name="currency">The account's currency. Every hour must be priced in it.</param>
    /// <returns>
    ///     One line per meter with usage, in meter order, each rounded once. A failure when an hour is
    ///     priced in another currency — there is no conversion, and pretending there is would move money.
    /// </returns>
    public static Result<ImmutableArray<InvoiceLine>> Lines(
        Guid subscriptionId,
        IEnumerable<RatedHour> rated,
        string currency
    ) {
        ArgumentNullException.ThrowIfNull(rated);

        var lines = ImmutableArray.CreateBuilder<InvoiceLine>();

        foreach (var meter in rated.GroupBy(static x => x.Meter).OrderBy(static x => x.Key)) {
            if (meter.FirstOrDefault(x => !string.Equals(x.Currency, currency, StringComparison.Ordinal)) is { } foreign) {
                return Result<ImmutableArray<InvoiceLine>>.Failure(
                    ErrorCode.Conflict,
                    $"{meter.Key} is priced in {foreign.Currency} and the account invoices in {currency}. "
                    + "The platform does not convert currencies; price the meter in the account's currency or "
                    + "change the account's."
                );
            }

            var quantity = meter.Sum(static x => x.Quantity);
            var amount = MoneyRounding.Round(meter.Sum(static x => x.Amount), currency);
            if (amount.TryGetError(out var error)) {
                return Result<ImmutableArray<InvoiceLine>>.Failure(error);
            }

            var unit = MeterCatalog.Define(meter.Key).TryGetValue(out var definition) ? definition.Unit : meter.Key.ToString();
            var declared = DeclaredMeters.Contains(meter.Key);

            lines.Add(
                new() {
                    SubscriptionId = subscriptionId,
                    Meter = meter.Key,
                    Unit = unit,
                    Quantity = quantity,
                    Amount = amount.GetValueOrThrow(),
                    DeclaredQuantity = declared,
                    Description = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{meter.Key}: {quantity:0.######} {unit}{(declared ? " (declared)" : string.Empty)}, subscription {subscriptionId:D}"
                    )
                }
            );
        }

        return Result<ImmutableArray<InvoiceLine>>.Success(lines.ToImmutable());
    }

    /// <summary>Assembles the invoice around its lines: subtotal, tax, total and notes.</summary>
    /// <param name="tenantId">The billing account.</param>
    /// <param name="periodStart">The month.</param>
    /// <param name="issuer">Who issues it.</param>
    /// <param name="customer">Who it is to.</param>
    /// <param name="lines">Every line, already rounded.</param>
    /// <param name="priceVersions">The price sheet versions the lines were rated against.</param>
    /// <param name="tax">The tax seam.</param>
    /// <returns>A draft; the grain numbers and stamps it at finalization.</returns>
    public static Result<Invoice> Draft(
        Guid tenantId,
        DateTimeOffset periodStart,
        InvoiceIssuer issuer,
        BillingProfile customer,
        ImmutableArray<InvoiceLine> lines,
        ImmutableArray<DateTimeOffset> priceVersions,
        ITaxService tax
    ) {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(tax);

        var periodEnd = periodStart.AddMonths(1);
        var subtotal = lines.Sum(static x => x.Amount);

        // ⚠ The last instant of the period, not its exclusive end: a rate that changes on the 1st of
        // next month is next month's, and periodEnd IS the 1st of next month.
        var quote = tax.Quote(new(issuer, customer, subtotal, customer.Currency, periodEnd.AddTicks(-1)));
        if (quote.TryGetError(out var taxError)) {
            return Result<Invoice>.Failure(taxError);
        }

        var taxed = quote.GetValueOrThrow();
        var notes = ImmutableArray.CreateBuilder<string>();

        if (taxed.Note.Length > 0) {
            notes.Add(taxed.Note);
        }

        if (lines.Any(static x => x.DeclaredQuantity)) {
            notes.Add(DeclaredQuantityNote);
        }

        return Result<Invoice>.Success(
            new() {
                InvoiceId = Guid.Empty,
                Number = string.Empty,
                Status = InvoiceStatus.Draft,
                TenantId = tenantId,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                Issuer = issuer,
                Customer = customer,
                Currency = customer.Currency,
                Lines = lines,
                Subtotal = subtotal,
                Tax = taxed,
                Total = subtotal + taxed.Amount,
                PriceSheetVersions = priceVersions,
                Notes = notes.ToImmutable()
            }
        );
    }

    /// <summary>The tax a credit note carries: the invoice's treatment and rate, on the credit's subtotal.</summary>
    /// <param name="invoice">The invoice credited.</param>
    /// <param name="subtotal">The credit's subtotal, negative.</param>
    /// <param name="earlier">Every credit note already issued against <paramref name="invoice" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a fresh quote.</b> A credit note corrects a document; it takes the treatment
    ///         that document had, even if the customer's country or the rate has changed since.
    ///         Re-quoting would credit tax at a rate that was never charged.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Rounded on everything credited so far, not on this note alone—the first version did
    ///             the second, and it moved money.
    ///         </b> The invoice's tax is rounded once, on its subtotal. Rounding each note's share again
    ///         gains up to half a cent per note: two credits of 0.03 against a 0.06 subtotal at 21 %
    ///         returned 0.02 of tax on an invoice that charged 0.01. So this note's tax is the tax on
    ///         every credit so far, rounded once, less what the earlier notes already carried, and the
    ///         total never exceeds the invoice's own. Crediting a whole invoice in any number of notes
    ///         returns exactly the tax it charged.
    ///     </para>
    /// </remarks>
    public static Result<TaxQuote> CreditTax(Invoice invoice, decimal subtotal, IReadOnlyCollection<CreditNote> earlier) {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(earlier);

        var creditedBase = earlier.Sum(static x => x.Subtotal);
        var creditedTax = earlier.Sum(static x => x.Tax.Amount);

        var cumulative = MoneyRounding.Round((creditedBase + subtotal) * invoice.Tax.RatePercent / 100m, invoice.Currency);
        if (cumulative.TryGetError(out var error)) {
            return Result<TaxQuote>.Failure(error);
        }

        // Every figure here is negative, so Max is the smaller credit. The cap binds only when the
        // invoice's tax isn't this rate on its subtotal; the Min keeps a note from ever charging tax.
        var total = Math.Max(cumulative.GetValueOrThrow(), -invoice.Tax.Amount);
        var amount = Math.Min(total - creditedTax, 0m);

        return Result<TaxQuote>.Success(invoice.Tax with { Base = subtotal, Amount = amount });
    }
}
