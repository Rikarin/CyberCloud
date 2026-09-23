using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>What the tax service is asked for one document.</summary>
/// <param name="Issuer">Who issues it — its country decides what "cross-border" means.</param>
/// <param name="Customer">Who it is addressed to.</param>
/// <param name="TaxableAmount">The subtotal, rounded. Negative for a credit note.</param>
/// <param name="Currency">Its currency.</param>
/// <param name="SupplyDate">The end of the period supplied, which picks the rate in force.</param>
public sealed record TaxQuoteRequest(
    InvoiceIssuer Issuer,
    BillingProfile Customer,
    decimal TaxableAmount,
    string Currency,
    DateTimeOffset SupplyDate
);

/// <summary>
///     The tax seam — docs/plan/22 § Invoicing and payment's "use a tax service (Stripe Tax /
///     Avalara)".
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             docs/plan/22 said "do not implement tax logic", and the default implementation in
///             <c>CyberCloud.Billing</c> does implement some — this is where the line was drawn.
///         </b> What ships is the EU VAT decision for one kind of supply (an electronically supplied
///         service): the customer country's standard rate from a committed, dated table, reverse
///         charge for an EU business in another member state with a VAT number, and "outside the
///         scope" for everybody else. No reduced rates, no US sales tax, no nexus, no OSS return.
///         That is enough for an EU issuer's first paying customers and small enough to be read in
///         one sitting, and it is behind this interface so that a Stripe Tax or Avalara
///         implementation replaces it rather than extends it — docs/plan/22 § Invoicing and payment
///         records the decision.
///     </para>
/// </remarks>
public interface ITaxService {
    /// <summary>Decides the tax on one document.</summary>
    /// <param name="request">The parties, the amount and the date.</param>
    /// <returns>The treatment, the rate, the rounded tax and the note the document must carry.</returns>
    Result<TaxQuote> Quote(TaxQuoteRequest request);

    /// <summary>
    ///     Whether this service can quote for a customer at all — what a billing account checks when its
    ///     profile is set, so that a malformed VAT number is refused on the day it is typed rather than
    ///     at the month's close.
    /// </summary>
    /// <param name="customer">The profile about to be stored.</param>
    /// <returns>Success, or <see cref="ErrorCode.InvalidRequestBody" /> naming the field and the shape it needs.</returns>
    Result CheckCustomer(BillingProfile customer);
}

/// <summary>A customer at the payment service provider — a token, never card data.</summary>
/// <param name="Id">The provider's customer id — <c>cus_…</c> at Stripe.</param>
/// <param name="Email">The address the provider holds for receipts.</param>
public sealed record PspCustomer(string Id, string Email);

/// <summary>
///     A setup intent: the provider's handle for collecting a payment method in the customer's
///     browser, so that card data never reaches this platform.
/// </summary>
/// <param name="Id">The provider's id — <c>seti_…</c>.</param>
/// <param name="Status">The provider's status word, verbatim — <c>requires_payment_method</c>, <c>succeeded</c>.</param>
/// <param name="ClientSecret">
///     What the browser's SDK confirms the intent with. ⚠ Handed to the customer's browser and to
///     nothing else — never logged, never stored in grain state.
/// </param>
/// <param name="PaymentMethodId">The method attached once the intent succeeded, or empty.</param>
public sealed record PspSetupIntent(string Id, string Status, string ClientSecret, string PaymentMethodId);

/// <summary>A stored payment method, as the provider describes it — a brand and four digits, never a number.</summary>
/// <param name="Id">The provider's id — <c>pm_…</c>.</param>
/// <param name="Type">The provider's type word — <c>card</c>, <c>sepa_debit</c>.</param>
/// <param name="Summary">What a person can recognise it by — <c>visa ending 4242</c>.</param>
public sealed record PspPaymentMethod(string Id, string Type, string Summary);

/// <summary>A charge for one invoice.</summary>
/// <param name="CustomerId">The provider's customer.</param>
/// <param name="PaymentMethodId">The stored method to charge.</param>
/// <param name="AmountMinor">The invoice total in minor units — <see cref="MoneyRounding.ToMinorUnits" />.</param>
/// <param name="Currency">The invoice's currency.</param>
/// <param name="InvoiceNumber">The invoice's number, carried as metadata so the provider's dashboard and ours agree.</param>
/// <param name="IdempotencyKey">
///     The key the provider collapses retries on. ⚠ A function of the invoice, never of the attempt:
///     a retry after a timeout must not charge twice.
/// </param>
public sealed record PspPaymentRequest(
    string CustomerId,
    string PaymentMethodId,
    long AmountMinor,
    string Currency,
    string InvoiceNumber,
    string IdempotencyKey
);

/// <summary>What the provider said about a charge.</summary>
/// <param name="Id">The provider's id — <c>pi_…</c>.</param>
/// <param name="Status">The provider's status word, verbatim — <c>succeeded</c>, <c>requires_action</c>, <c>processing</c>.</param>
/// <param name="AmountMinor">The amount the provider recorded.</param>
/// <param name="Currency">The currency the provider recorded, upper case.</param>
public sealed record PspPayment(string Id, string Status, long AmountMinor, string Currency);

/// <summary>A webhook event whose signature verified.</summary>
/// <param name="Id">The provider's event id — <c>evt_…</c>. What a consumer deduplicates on.</param>
/// <param name="Type">The event type — <c>payment_intent.succeeded</c>.</param>
/// <param name="ObjectId">The id of the object the event is about.</param>
/// <param name="Created">When the provider created it.</param>
public sealed record PspWebhookEvent(string Id, string Type, string ObjectId, DateTimeOffset Created);

/// <summary>
///     The payment service provider seam — docs/plan/22 § Invoicing and payment: "Stripe (or an
///     equivalent PSP) … We do not touch card data. PCI scope is the PSP's; ours is a token."
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Provider-neutral in its nouns, and the nouns are the PCI boundary.</b> A customer is
///         an id, a payment method is an id and a summary, and the only secret that crosses this
///         interface is a setup intent's client secret, which goes to the customer's browser. Nothing
///         here can carry a card number, because nothing here has a field for one.
///     </para>
///     <para>
///         ⚠ <b>A failure is a <see cref="Result" />, and a declined card is not a failure.</b> A
///         charge the provider accepted and the bank declined comes back as a
///         <see cref="PspPayment" /> with the provider's status word; a failure means the provider
///         could not be asked or refused the request itself. Dunning (docs/plan/22 § Invoicing and
///         payment, the Dunning row) reads the status word; it is owed.
///     </para>
/// </remarks>
public interface IPaymentServiceProvider {
    /// <summary>Creates a customer.</summary>
    /// <param name="tenantId">The billing account, carried as metadata.</param>
    /// <param name="legalName">The name the provider shows.</param>
    /// <param name="email">Where the provider sends receipts.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PspCustomer>> CreateCustomerAsync(
        Guid tenantId,
        string legalName,
        string email,
        CancellationToken cancellationToken = default
    );

    /// <summary>Starts collecting a payment method for a customer, off-session use allowed.</summary>
    /// <param name="customerId">The provider's customer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PspSetupIntent>> CreateSetupIntentAsync(string customerId, CancellationToken cancellationToken = default);

    /// <summary>Reads a setup intent back — what the platform does after the browser says it confirmed.</summary>
    /// <param name="setupIntentId">The intent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PspSetupIntent>> GetSetupIntentAsync(string setupIntentId, CancellationToken cancellationToken = default);

    /// <summary>A customer's stored card payment methods.</summary>
    /// <param name="customerId">The provider's customer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ImmutableArray<PspPaymentMethod>>> ListPaymentMethodsAsync(
        string customerId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Charges a stored method for one invoice, off-session.</summary>
    /// <param name="request">The charge.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<PspPayment>> PayInvoiceAsync(PspPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Verifies a webhook's signature and parses the event. Nothing in a webhook is believed until
    ///     this has answered success.
    /// </summary>
    /// <param name="payload">The request body, byte-for-byte as received, as UTF-8 text.</param>
    /// <param name="signatureHeader">The provider's signature header, verbatim.</param>
    /// <param name="now">The time to check the signature's age against.</param>
    Result<PspWebhookEvent> VerifyWebhook(string payload, string signatureHeader, DateTimeOffset now);
}
