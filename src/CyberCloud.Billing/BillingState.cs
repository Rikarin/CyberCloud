namespace CyberCloud.Billing;

/// <summary>What <c>BillingAccountGrain</c> persists.</summary>
/// <remarks>
///     ⚠ <b>Two lists that are only ever appended to.</b> <see cref="Invoices" /> and
///     <see cref="CreditNotes" /> are documents a customer has been sent; the grain adds to them and
///     never replaces or removes an element, and
///     <c>InvoicingTests.ALedgerCorrectionAfterFinalizationBecomesACreditNoteAndTheInvoiceIsUntouched</c>
///     asserts a finalized invoice reads back unchanged after a credit note is issued against it. Every collection is
///     <c>{ get; set; }</c> for the "STJ does not populate a get-only collection" trap
///     <c>UsageLedgerState</c> records.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BillingAccountState")]
public sealed class BillingAccountState {
    /// <summary>Whom the account invoices.</summary>
    [Id(0)]
    public BillingProfile Profile { get; set; } = new();

    /// <summary>Whether a profile has been set.</summary>
    [Id(1)]
    public bool Configured { get; set; }

    /// <summary>The attached subscriptions, in attach order.</summary>
    [Id(2)]
    public List<Guid> Subscriptions { get; set; } = [];

    /// <summary>Every finalized invoice, in finalization order. Append-only.</summary>
    [Id(3)]
    public List<Invoice> Invoices { get; set; } = [];

    /// <summary>Every credit note, in issue order. Append-only.</summary>
    [Id(4)]
    public List<CreditNote> CreditNotes { get; set; } = [];

    /// <summary>
    ///     The month the first subscription was attached in, and the first month the month close
    ///     finalizes. Null until a subscription is attached. An earlier month is finalized only when
    ///     someone asks for it by name.
    /// </summary>
    [Id(5)]
    public DateTimeOffset? FirstMonth { get; set; }
}

/// <summary>What <c>InvoiceNumberingGrain</c> persists: one counter per issuer and series.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.InvoiceNumberingState")]
public sealed class InvoiceNumberingState {
    /// <summary>The series, by <c>{issuerCode}|{series}</c>.</summary>
    [Id(0)]
    public Dictionary<string, NumberSeriesState> Series { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One gap-free sequence.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.NumberSeriesState")]
public sealed class NumberSeriesState {
    /// <summary>How many numbers have been handed out. Only grows.</summary>
    [Id(0)]
    public long Allocated { get; set; }

    /// <summary>
    ///     The number each unconfirmed document was given, by the document it was given to. Confirming
    ///     removes the entry, so this holds what's in flight and not every document ever numbered.
    /// </summary>
    [Id(1)]
    public Dictionary<string, string> ByDocument { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Numbers handed out whose document has not confirmed it was written, number → document.</summary>
    [Id(2)]
    public Dictionary<string, string> Unconfirmed { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What <c>BudgetGrain</c> persists.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Billing.BudgetState")]
public sealed class BudgetState {
    /// <summary>The spec, or <see langword="null" /> when no budget is held.</summary>
    [Id(0)]
    public BudgetSpec? Spec { get; set; }

    /// <summary>The currency of the last evaluation.</summary>
    [Id(1)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>The period of the last evaluation.</summary>
    [Id(2)]
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>The end of that period.</summary>
    [Id(3)]
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>The actual cost at the last evaluation.</summary>
    [Id(4)]
    public decimal Actual { get; set; }

    /// <summary>The forecast at the last evaluation.</summary>
    [Id(5)]
    public decimal Forecast { get; set; }

    /// <summary>When it was last evaluated.</summary>
    [Id(6)]
    public DateTimeOffset? LastEvaluatedAt { get; set; }

    /// <summary>Why the last evaluation could not run.</summary>
    [Id(7)]
    public string LastError { get; set; } = string.Empty;

    /// <summary>Every threshold that has fired, oldest first, capped at <c>IBudgetGrain.AlertsKept</c>.</summary>
    [Id(8)]
    public List<BudgetAlert> Alerts { get; set; } = [];
}
