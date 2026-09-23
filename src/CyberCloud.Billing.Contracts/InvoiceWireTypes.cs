using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>
///     Who a billing account invoices — the customer half of an invoice, and everything the tax
///     service needs to decide how VAT applies.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BillingProfile")]
public sealed record BillingProfile {
    /// <summary>The legal name printed on the invoice.</summary>
    [Id(0)]
    public string LegalName { get; init; } = string.Empty;

    /// <summary>The customer's country, ISO 3166-1 alpha-2, upper case — <c>DE</c>, <c>CZ</c>, <c>US</c>.</summary>
    [Id(1)]
    public string Country { get; init; } = string.Empty;

    /// <summary>
    ///     The customer's VAT identification number, prefix included — <c>DE123456789</c> — or empty.
    ///     ⚠ Checked for shape only; VIES validation is owed (docs/plan/22 § What is owed).
    /// </summary>
    [Id(2)]
    public string VatId { get; init; } = string.Empty;

    /// <summary>Whether the customer buys as a business. A business with a VAT number can be reverse-charged.</summary>
    [Id(3)]
    public bool IsBusiness { get; init; }

    /// <summary>The currency invoices are issued in. Every meter the account uses must be priced in it.</summary>
    [Id(4)]
    public string Currency { get; init; } = Currencies.Euro;
}

/// <summary>The legal entity that issues invoices, and the prefix its numbers carry.</summary>
/// <remarks>
///     ⚠ <b>One issuer per deployment today</b>, bound from <c>CyberCloud:Billing:Issuer</c>. The
///     number sequence is keyed by <see cref="Code" /> anyway, so a second entity — an EU and a US
///     one, say — is configuration and a routing rule, not a renumbering.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Billing.InvoiceIssuer")]
public sealed record InvoiceIssuer {
    /// <summary>The stable code the number sequence is keyed by. Never reused for another entity.</summary>
    [Id(0)]
    public string Code { get; init; } = string.Empty;

    /// <summary>The legal name printed on the invoice.</summary>
    [Id(1)]
    public string LegalName { get; init; } = string.Empty;

    /// <summary>The issuer's country, ISO 3166-1 alpha-2. Reverse charge applies across a border from it.</summary>
    [Id(2)]
    public string Country { get; init; } = string.Empty;

    /// <summary>The issuer's own VAT number, printed on every invoice.</summary>
    [Id(3)]
    public string VatId { get; init; } = string.Empty;

    /// <summary>What every number from this issuer starts with — <c>CC</c> gives <c>CC-INV-00000001</c>.</summary>
    [Id(4)]
    public string NumberPrefix { get; init; } = string.Empty;
}

/// <summary>One line of an invoice: one meter in one subscription, for the period.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.InvoiceLine")]
public sealed record InvoiceLine {
    /// <summary>The subscription the usage was recorded in.</summary>
    [Id(0)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The meter.</summary>
    [Id(1)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>The unit, as <c>MeterCatalog</c> spells it — <c>vCPU-hour</c>, <c>GiB-month</c>.</summary>
    [Id(2)]
    public string Unit { get; init; } = string.Empty;

    /// <summary>The quantity, summed over the period. Unrounded; the meter reports to twelve places.</summary>
    [Id(3)]
    public decimal Quantity { get; init; }

    /// <summary>The amount, rounded once to the currency's minor unit — <see cref="MoneyRounding" />, rule 2.</summary>
    [Id(4)]
    public decimal Amount { get; init; }

    /// <summary>
    ///     Whether <see cref="Quantity" /> is the size a resource <i>declared</i> rather than one
    ///     anything measured. True for the storage meters today; the invoice prints why.
    /// </summary>
    /// <remarks>
    ///     ⚠ docs/plan/22 § What is owed, <c>storage-is-declared-not-observed</c>: a storage
    ///     quantity is the desired body's size, which differs from the provisioned volume while an
    ///     expansion is pending or has failed (billed high) and after a shrink Kubernetes refused
    ///     (billed low). The flag is how a line that carries that error says so.
    /// </remarks>
    [Id(5)]
    public bool DeclaredQuantity { get; init; }

    /// <summary>The line as a person reads it.</summary>
    [Id(6)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>What the tax service decided for one invoice or credit note.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.TaxQuote")]
public sealed record TaxQuote {
    /// <summary>Standard, reverse charge, or out of scope.</summary>
    [Id(0)]
    public TaxTreatment Treatment { get; init; } = TaxTreatment.Unknown;

    /// <summary>The rate, in percent — <c>21</c> for 21 %. Zero for reverse charge and out of scope.</summary>
    [Id(1)]
    public decimal RatePercent { get; init; }

    /// <summary>The amount the rate was applied to — the subtotal.</summary>
    [Id(2)]
    public decimal Base { get; init; }

    /// <summary>The tax, rounded once — <see cref="MoneyRounding" />, rule 4.</summary>
    [Id(3)]
    public decimal Amount { get; init; }

    /// <summary>Which country's rate, ISO 3166-1 alpha-2, or empty when none applied.</summary>
    [Id(4)]
    public string Country { get; init; } = string.Empty;

    /// <summary>
    ///     The sentence the invoice must carry — the reverse-charge wording, or the out-of-scope
    ///     statement. Empty for a standard-rated invoice.
    /// </summary>
    [Id(5)]
    public string Note { get; init; } = string.Empty;

    /// <summary>
    ///     How the customer's VAT number was checked: <c>format</c> today, <c>vies</c> once the VIES
    ///     call is wired. Printed on the invoice's audit trail, because a reverse charge on an
    ///     unverified number is a liability the issuer carries.
    /// </summary>
    [Id(6)]
    public string VatIdCheck { get; init; } = string.Empty;
}

/// <summary>A monthly invoice for one billing account — a draft, or a finalized document.</summary>
/// <remarks>
///     ⚠ <b>Every member is <c>init</c>-only, and a finalized one is never replaced.</b> The grain
///     holds finalized invoices in a list it only appends to, for the reason <c>UsageLedgerEntry</c>
///     gives: a document a customer has been sent cannot be edited, only answered with a credit note.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Billing.Invoice")]
public sealed record Invoice {
    /// <summary>This invoice's identity.</summary>
    [Id(0)]
    public Guid InvoiceId { get; init; }

    /// <summary>The gap-free number, or empty on a draft.</summary>
    [Id(1)]
    public string Number { get; init; } = string.Empty;

    /// <summary>Draft or finalized.</summary>
    [Id(2)]
    public InvoiceStatus Status { get; init; } = InvoiceStatus.Unknown;

    /// <summary>The billing account — the tenant.</summary>
    [Id(3)]
    public Guid TenantId { get; init; }

    /// <summary>The first instant of the month, UTC.</summary>
    [Id(4)]
    public DateTimeOffset PeriodStart { get; init; }

    /// <summary>The first instant of the next month, UTC.</summary>
    [Id(5)]
    public DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Who issued it.</summary>
    [Id(6)]
    public InvoiceIssuer Issuer { get; init; } = new();

    /// <summary>Who it is addressed to, as the profile read when it was computed.</summary>
    [Id(7)]
    public BillingProfile Customer { get; init; } = new();

    /// <summary>The currency.</summary>
    [Id(8)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>One line per subscription and meter, in subscription then meter order.</summary>
    [Id(9)]
    public ImmutableArray<InvoiceLine> Lines { get; init; } = [];

    /// <summary>The sum of the rounded lines.</summary>
    [Id(10)]
    public decimal Subtotal { get; init; }

    /// <summary>The tax decision and amount.</summary>
    [Id(11)]
    public TaxQuote Tax { get; init; } = new();

    /// <summary>Subtotal plus tax.</summary>
    [Id(12)]
    public decimal Total { get; init; }

    /// <summary>When it was finalized, or <see langword="null" /> on a draft.</summary>
    [Id(13)]
    public DateTimeOffset? FinalizedAt { get; init; }

    /// <summary>Every price sheet version a line was rated against, by effective date.</summary>
    [Id(14)]
    public ImmutableArray<DateTimeOffset> PriceSheetVersions { get; init; } = [];

    /// <summary>The notes the invoice prints — the tax note, and why a quantity was declared.</summary>
    [Id(15)]
    public ImmutableArray<string> Notes { get; init; } = [];
}

/// <summary>One line a credit note should credit, as a request names it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CreditNoteLineRequest")]
public sealed record CreditNoteLineRequest {
    /// <summary>The index of the invoice line being credited, from zero.</summary>
    [Id(0)]
    public int LineIndex { get; init; }

    /// <summary>How much of it to credit, before tax. Positive; the credit note carries it negated.</summary>
    [Id(1)]
    public decimal Amount { get; init; }
}

/// <summary>A request to correct a finalized invoice — the only kind of correction there is.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CreditNoteRequest")]
public sealed record CreditNoteRequest {
    /// <summary>The finalized invoice's number.</summary>
    [Id(0)]
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>What to credit, per line.</summary>
    [Id(1)]
    public ImmutableArray<CreditNoteLineRequest> Lines { get; init; } = [];

    /// <summary>
    ///     Why. ⚠ Required: docs/plan/22 § Invoicing and payment — "Credits and refunds: ledger entries
    ///     with a reason, an approver, and an audit trail".
    /// </summary>
    [Id(2)]
    public string Reason { get; init; } = string.Empty;

    /// <summary>Who approved it. ⚠ Required, for the same sentence.</summary>
    [Id(3)]
    public string ApprovedBy { get; init; } = string.Empty;

    /// <summary>
    ///     The caller's id for this request. ⚠ Required, and a retry must repeat it: a second request
    ///     with the same id answers the credit note the first one issued, so a timeout cannot credit
    ///     twice. A function of the decision being recorded, never of the attempt.
    /// </summary>
    [Id(4)]
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>One credited line — negative amounts, pointing at the invoice line they correct.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CreditNoteLine")]
public sealed record CreditNoteLine {
    /// <summary>The invoice line credited.</summary>
    [Id(0)]
    public int LineIndex { get; init; }

    /// <summary>Its subscription.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>Its meter.</summary>
    [Id(2)]
    public BillingMeter Meter { get; init; } = BillingMeter.Unknown;

    /// <summary>The credit, negative.</summary>
    [Id(3)]
    public decimal Amount { get; init; }

    /// <summary>The line as a person reads it.</summary>
    [Id(4)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>A numbered credit note against a finalized invoice.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CreditNote")]
public sealed record CreditNote {
    /// <summary>This credit note's identity — the key its number is allocated under.</summary>
    [Id(0)]
    public Guid CreditNoteId { get; init; }

    /// <summary>The gap-free number in the credit-note series.</summary>
    [Id(1)]
    public string Number { get; init; } = string.Empty;

    /// <summary>The invoice it corrects.</summary>
    [Id(2)]
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>The billing account.</summary>
    [Id(3)]
    public Guid TenantId { get; init; }

    /// <summary>The invoice's currency.</summary>
    [Id(4)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>The credited lines.</summary>
    [Id(5)]
    public ImmutableArray<CreditNoteLine> Lines { get; init; } = [];

    /// <summary>The sum of the lines, negative.</summary>
    [Id(6)]
    public decimal Subtotal { get; init; }

    /// <summary>The tax credited — the invoice's treatment and rate, on this subtotal.</summary>
    [Id(7)]
    public TaxQuote Tax { get; init; } = new();

    /// <summary>Subtotal plus tax, negative.</summary>
    [Id(8)]
    public decimal Total { get; init; }

    /// <summary>Why.</summary>
    [Id(9)]
    public string Reason { get; init; } = string.Empty;

    /// <summary>Who approved it.</summary>
    [Id(10)]
    public string ApprovedBy { get; init; } = string.Empty;

    /// <summary>When it was issued.</summary>
    [Id(11)]
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>The request it answered — see <see cref="CreditNoteRequest.RequestId" />.</summary>
    [Id(12)]
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>
///     What re-rating a finalized month says now, against what was invoiced — the input to a credit
///     note after a ledger correction.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.CorrectionProposal")]
public sealed record CorrectionProposal {
    /// <summary>The invoice compared.</summary>
    [Id(0)]
    public string InvoiceNumber { get; init; } = string.Empty;

    /// <summary>Lines that are now worth less than was invoiced, net of every credit already issued.</summary>
    [Id(1)]
    public ImmutableArray<CreditNoteLineRequest> Credits { get; init; } = [];

    /// <summary>
    ///     Usage that is now worth <i>more</i> than was invoiced — late usage past the 48-hour window.
    ///     ⚠ Not credited and not invoiced: a debit note is owed (docs/plan/22 § What is owed).
    /// </summary>
    [Id(2)]
    public ImmutableArray<InvoiceLine> Underbilled { get; init; } = [];
}

/// <summary>A billing account as it stands.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BillingAccountSnapshot")]
public sealed record BillingAccountSnapshot {
    /// <summary>The tenant — a billing account is one per tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>Whom it invoices. Empty until configured.</summary>
    [Id(1)]
    public BillingProfile Profile { get; init; } = new();

    /// <summary>The subscriptions whose usage its invoices carry, in the order they were attached.</summary>
    [Id(2)]
    public ImmutableArray<Guid> Subscriptions { get; init; } = [];

    /// <summary>Whether a profile has been set. An unconfigured account cannot finalize.</summary>
    [Id(3)]
    public bool Configured { get; init; }
}

/// <summary>How the numbering grain accounts for one series.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.NumberingAudit")]
public sealed record NumberingAudit {
    /// <summary>How many numbers the series has handed out. The next is this plus one.</summary>
    [Id(0)]
    public long Allocated { get; init; }

    /// <summary>
    ///     Numbers handed out whose document never confirmed it was written. ⚠ Each is a gap in the
    ///     printed sequence until its finalization is retried, and a retry gets the same number.
    /// </summary>
    [Id(1)]
    public ImmutableArray<string> Unconfirmed { get; init; } = [];
}
