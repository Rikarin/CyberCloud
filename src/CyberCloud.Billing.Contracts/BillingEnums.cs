namespace CyberCloud.Billing.Contracts;

/// <summary>Where an invoice is in its life.</summary>
/// <remarks>
///     ⚠ <b>Two states and one direction.</b> A draft is not stored — it is the month's usage rated
///     on the day it is asked for, so it accrues as usage lands. A finalized invoice is stored, carries
///     a number, and nothing in <c>IBillingAccountGrain</c> can change it again; a correction is a
///     credit note, docs/plan/22 § Invoicing and payment.
/// </remarks>
[Alias("CyberCloud.Billing.InvoiceStatus")]
public enum InvoiceStatus {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Computed from the ledger on request. No number, no permanence.</summary>
    Draft = 1,

    /// <summary>Numbered, stored, immutable.</summary>
    Finalized = 2
}

/// <summary>Which numbered series a document belongs to. Each series is gap-free per issuer.</summary>
[Alias("CyberCloud.Billing.DocumentSeries")]
public enum DocumentSeries {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Invoices — <c>{prefix}-INV-00000001</c>.</summary>
    Invoice = 1,

    /// <summary>Credit notes — <c>{prefix}-CN-00000001</c>.</summary>
    CreditNote = 2
}

/// <summary>How tax applies to one invoice, which decides what the invoice has to say.</summary>
[Alias("CyberCloud.Billing.TaxTreatment")]
public enum TaxTreatment {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>VAT charged at the customer country's standard rate.</summary>
    Standard = 1,

    /// <summary>
    ///     An EU business in another member state, with a VAT number: no VAT charged, and the invoice
    ///     must say the customer accounts for it — Council Directive 2006/112/EC, Article 196.
    /// </summary>
    ReverseCharge = 2,

    /// <summary>A customer outside the EU. No EU VAT applies; local tax is the customer's to account for.</summary>
    OutOfScope = 3
}

/// <summary>How long a budget's period is. Periods are calendar-aligned in UTC.</summary>
[Alias("CyberCloud.Billing.BudgetPeriod")]
public enum BudgetPeriod {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>A calendar month — the invoice's period.</summary>
    Monthly = 1,

    /// <summary>A calendar quarter, starting January, April, July and October.</summary>
    Quarterly = 2,

    /// <summary>A calendar year.</summary>
    Annually = 3
}

/// <summary>What a budget's figure covers.</summary>
[Alias("CyberCloud.Billing.BudgetScope")]
public enum BudgetScope {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>The resource group the budget resource lives in.</summary>
    ResourceGroup = 1,

    /// <summary>
    ///     The whole subscription. ⚠ Evaluated only once the budget itself has been granted
    ///     <c>reader</c> on the subscription — see <c>IBudgetGrain</c>.
    /// </summary>
    Subscription = 2
}

/// <summary>What a budget threshold is compared with.</summary>
[Alias("CyberCloud.Billing.ThresholdKind")]
public enum ThresholdKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>What the period has cost so far.</summary>
    Actual = 1,

    /// <summary>
    ///     What the period will cost at the trailing seven days' rate — docs/plan/22 § Cost visibility,
    ///     "linear on the trailing 7 days … labelled an estimate".
    /// </summary>
    Forecast = 2
}

/// <summary>How a cost query's rows are keyed.</summary>
[Alias("CyberCloud.Billing.CostGrouping")]
public enum CostGrouping {
    /// <summary>Never assigned, and refused.</summary>
    Unknown = 0,

    /// <summary>One row per resource, keyed by its path.</summary>
    Resource = 1,

    /// <summary>One row per resource group, keyed by its name.</summary>
    ResourceGroup = 2,

    /// <summary>One row per resource type, keyed <c>{namespace}/{type}</c>.</summary>
    ResourceType = 3,

    /// <summary>One row per meter, keyed by the meter's name.</summary>
    Meter = 4,

    /// <summary>One row per UTC day, keyed <c>yyyy-MM-dd</c>.</summary>
    Day = 5
}
