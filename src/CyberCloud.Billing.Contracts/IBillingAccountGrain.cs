using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>
///     A tenant's billing account: who it invoices, which subscriptions it carries, and every invoice
///     and credit note it has issued. docs/plan/22 § Invoicing and payment.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Coordinator · <b>Tier</b> Durable · <b>Key</b> <c>tenant/{tenantId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠
///         <b>
///             What a billing account IS, since docs/plan/22 never says: one per tenant, with
///             subscriptions attached to it.
///         </b> The tenant is the contracting party — it is what signs up, what docs/plan/06's
///         <c>Warned → Suspended → Disabled</c> ladder acts on, and what a VAT number belongs to. A
///         subscription is a unit of usage and quota, not of legal identity, so it is attached rather
///         than billed on its own; a subscription no account carries is usage nobody is invoiced for,
///         which <see cref="AttachSubscriptionAsync" /> exists to make an explicit decision. Several
///         accounts per tenant (a department paying for its own subscriptions) is a key change and a
///         routing rule, and nothing here assumes it away.
///     </para>
///     <para>
///         ⚠ <b>The invoice grain is the month, and the draft is not stored.</b>
///         <see cref="PreviewAsync" /> rates the attached subscriptions' usage ledgers for the month
///         on every call — the draft accrues because the ledger does. <see cref="FinalizeAsync" /> is
///         the only write, it is refused until the 48-hour late-usage window after the month has
///         passed, and what it stores is never changed again: this interface declares no method that
///         reaches a finalized invoice other than to read it or to answer it with a credit note.
///     </para>
///     <para>
///         ⚠ <b>Holding every invoice in one grain's state is an M2 size decision.</b> Twelve invoices
///         a year of a few dozen lines each is small for decades; a tenant with thousands of
///         subscriptions is the case that would move invoices into a grain per month, and that is a
///         new key shape (docs/plan/22 § What is owed).
///     </para>
/// </remarks>
[Alias("CyberCloud.Billing.IBillingAccountGrain")]
public interface IBillingAccountGrain : IGrainWithStringKey {
    /// <summary>The late-usage window — docs/plan/22 § Invoicing and payment, "a 48-hour late-usage window before finalisation".</summary>
    static readonly TimeSpan LateUsageWindow = TimeSpan.FromHours(48);

    /// <summary>Sets whom the account invoices.</summary>
    /// <param name="profile">
    ///     The customer. The country must be two upper-case letters, the currency one
    ///     <see cref="Currencies" /> knows, and a VAT number, when present, must start with the
    ///     country's VAT prefix.
    /// </param>
    /// <remarks>
    ///     A change applies to invoices finalized afterwards. A finalized invoice keeps the profile it
    ///     was issued to, which is what a customer who has since moved expects to see on last year's.
    /// </remarks>
    Task<Result<BillingAccountSnapshot>> ConfigureAsync(BillingProfile profile);

    /// <summary>
    ///     Attaches a subscription, so its usage lands on this account's invoices, and arms the month
    ///     close—<see cref="CloseMonthsAsync" />.
    /// </summary>
    /// <param name="subscriptionId">
    ///     A subscription in this tenant. Attaching twice changes nothing but re-arms a month close
    ///     that failed to arm the first time.
    /// </param>
    Task<Result<BillingAccountSnapshot>> AttachSubscriptionAsync(Guid subscriptionId);

    /// <summary>The account as it stands.</summary>
    Task<Result<BillingAccountSnapshot>> GetAsync();

    /// <summary>
    ///     The month's invoice as a draft, rated from the ledgers now — or the finalized invoice, if
    ///     the month has been finalized.
    /// </summary>
    /// <param name="periodStart">The first instant of the month, UTC. Any other instant is refused.</param>
    Task<Result<Invoice>> PreviewAsync(DateTimeOffset periodStart);

    /// <summary>Finalizes the month: rates it one last time, numbers it and stores it.</summary>
    /// <param name="periodStart">The first instant of the month, UTC.</param>
    /// <returns>
    ///     The finalized invoice. ⚠ Idempotent: finalizing a month that already is returns the stored
    ///     invoice unchanged, which is what makes a retry after a timeout safe — and a retry that
    ///     reaches the numbering grain again gets the number it was already given, so a crash between
    ///     allocation and write costs no gap. Refused with <see cref="ErrorCode.Conflict" /> before the
    ///     late-usage window has passed, and out of order—when a later month is already finalized, or
    ///     the month before, since the first attach, isn't—because numbers follow months.
    /// </returns>
    Task<Result<Invoice>> FinalizeAsync(DateTimeOffset periodStart);

    /// <summary>
    ///     Closes the months that are due: finalizes, oldest first, every month since the first
    ///     subscription was attached whose late-usage window has passed and that has no invoice yet.
    /// </summary>
    /// <returns>
    ///     The invoices this call finalized, oldest first, or an empty array when nothing was due. On
    ///     the first month that can't be finalized—no profile, no issuer, a currency no meter is priced
    ///     in—the call stops and returns that refusal. The months before it stay finalized and
    ///     <see cref="ListInvoicesAsync" /> lists them.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>This is the month close, and the account runs it itself.</b> Attaching a subscription
    ///     arms a reminder that calls this every <see cref="MonthCloseTick" />, so an account that
    ///     carries a subscription and has a profile is invoiced on the 3rd of each month with nobody
    ///     asking. Calling it directly is safe at any time: a finalized month answers its stored
    ///     invoice, and a month inside its window is left alone.
    /// </remarks>
    Task<Result<ImmutableArray<Invoice>>> CloseMonthsAsync();

    /// <summary>How often the month-close reminder runs <see cref="CloseMonthsAsync" />.</summary>
    static readonly TimeSpan MonthCloseTick = TimeSpan.FromHours(1);

    /// <summary>Every finalized invoice, in the order they were finalized.</summary>
    Task<Result<ImmutableArray<Invoice>>> ListInvoicesAsync();

    /// <summary>One finalized invoice, by number.</summary>
    /// <param name="number">The number printed on it.</param>
    Task<Result<Invoice>> GetInvoiceAsync(string number);

    /// <summary>
    ///     Re-rates a finalized month against the ledger as it is now and says what a correction
    ///     would credit — and what late usage it cannot.
    /// </summary>
    /// <param name="invoiceNumber">The finalized invoice.</param>
    Task<Result<CorrectionProposal>> ProposeCorrectionAsync(string invoiceNumber);

    /// <summary>Issues a numbered credit note against a finalized invoice.</summary>
    /// <param name="request">
    ///     What to credit, why, and who approved it. Each line may credit at most what its invoice
    ///     line still carries after every credit note already issued against it.
    /// </param>
    Task<Result<CreditNote>> IssueCreditNoteAsync(CreditNoteRequest request);

    /// <summary>Every credit note, in the order they were issued.</summary>
    Task<Result<ImmutableArray<CreditNote>>> ListCreditNotesAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     The gap-free document numbers, one sequence per issuer and series. docs/plan/22 § Invoicing
///     and payment; EU invoicing law asks for a sequential number that "uniquely identifies the
///     invoice" (Council Directive 2006/112/EC, Article 226(2)).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Platform singleton · <b>Tier</b> Durable · <b>Key</b>
///         <c>platform/invoice-numbering</c>, null-tenant. See
///         <c>GrainKeys.InvoiceNumberingSingleton</c> for why a singleton.
///     </para>
///     <para>
///         ⚠ <b>Gap-free is a property of three rules together, and dropping any one breaks it.</b>
///         A number is allocated only at finalization, never for a draft; an allocation is keyed by
///         the document it is for, so asking twice for one document answers the same number; and the
///         document confirms once it is written, so a number allocated to a finalization that never
///         completed is visible in <see cref="AuditAsync" /> rather than silently skipped. A counter
///         that incremented on every call would leave a gap at every retry.
///     </para>
///     <para>
///         ⚠ <b>This grain is null-tenant and reachable from every tenant</b>
///         (<c>PlatformCrossTenantAuthorizer</c>'s one allowed edge). What it holds is a counter and
///         document keys — a tenant id and a month — and no amounts, so a tenant that called it
///         directly could learn how many invoices the platform has issued and nothing about anyone's.
///     </para>
/// </remarks>
[Alias("CyberCloud.Billing.IInvoiceNumberingGrain")]
public interface IInvoiceNumberingGrain : IGrainWithStringKey {
    /// <summary>The number for one document, allocating the next one if the document has none.</summary>
    /// <param name="issuer">The issuer. Its <see cref="InvoiceIssuer.Code" /> keys the sequence and its prefix spells the number.</param>
    /// <param name="series">Invoice or credit note — two independent sequences per issuer.</param>
    /// <param name="documentKey">
    ///     What the number is for — <c>{tenant:N}/{yyyy-MM}</c> for an invoice, and
    ///     <c>{tenant:N}/{request id}</c> for a credit note, so a retried request gets the number its
    ///     first attempt was given. The same key answers the same number until the document confirms;
    ///     after that the account answers a retry from its own state and never asks again.
    /// </param>
    Task<Result<string>> AllocateAsync(InvoiceIssuer issuer, DocumentSeries series, string documentKey);

    /// <summary>Records that the document carrying a number has been written.</summary>
    /// <param name="issuerCode">The issuer's code.</param>
    /// <param name="series">The series.</param>
    /// <param name="number">A number <see cref="AllocateAsync" /> returned.</param>
    Task<Result> ConfirmAsync(string issuerCode, DocumentSeries series, string number);

    /// <summary>How many numbers a series has issued, and which are not yet on a written document.</summary>
    /// <param name="issuerCode">The issuer's code.</param>
    /// <param name="series">The series.</param>
    Task<Result<NumberingAudit>> AuditAsync(string issuerCode, DocumentSeries series);
}
