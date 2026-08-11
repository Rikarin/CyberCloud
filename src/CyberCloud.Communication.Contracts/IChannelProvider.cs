namespace CyberCloud.Communication.Contracts;

/// <summary>
///     What a carrier looks like from the inside — docs/plan/17 § The channel abstraction.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a real implementation owes.</b> Nothing in this repository implements this against
///         a carrier, and the list below is what the first one has to do beyond calling an HTTP API.
///         It is written here rather than in a ticket because the seam is where the next person
///         looks.
///     </para>
///     <list type="number">
///         <item>
///             <b>Authenticate from a handle, never a value.</b>
///             <see cref="ChannelConfiguration.Credentials" /> carries
///             <see cref="CarrierSecretRef" />s. Resolve them at dispatch, hold the resolved value
///             for the call and no longer, and never log it. A tenant's own Twilio token is the
///             tenant's, and BYO is offered from day one (docs/plan/17 § The channel abstraction).
///         </item>
///         <item>
///             <b>Return a provider message id, always.</b> It is the only handle a delivery receipt
///             arrives with, and a dispatch that reports success without one has made
///             <see cref="IWebhookRouter" /> unable to correlate anything the carrier says later.
///         </item>
///         <item>
///             <b>Verify webhook signatures before believing a receipt.</b>
///             <see cref="WebhookEnvelope.Body" /> is the bytes as received precisely so the
///             signature still checks. An unverified receipt is a stranger telling you a message
///             bounced — which, since a bounce suppresses an address, is a denial-of-service on the
///             tenant's own recipients.
///         </item>
///         <item>
///             <b>Classify failures, and say which ones suppress.</b>
///             <see cref="DeliveryReceipt.Suppresses" /> is the carrier's judgement rendered into
///             ours. Only the carrier knows whether its code means "this address does not exist" or
///             "try again in an hour", and guessing either way is expensive — see that member.
///         </item>
///         <item>
///             <b>Report cost in the carrier's currency.</b> The spend limit reserves an estimate and
///             settles the real figure (<see cref="SpendReservation" />). A provider that never
///             reports cost leaves every window settled at the estimate, which is a limit that
///             drifts.
///         </item>
///         <item>
///             <b>Be idempotent where the carrier lets you.</b> Most accept a client reference. Pass
///             <see cref="MessageSnapshot.MessageId" />. Our idempotency stops a duplicate send from
///             <i>us</i>; the carrier's stops a duplicate from a retried HTTP request inside the
///             provider.
///         </item>
///         <item>
///             <b>Enforce nothing about compliance, and surface everything.</b> We are a broker, not
///             a carrier (docs/plan/17 § The channel abstraction). Sender registration, 10DLC and
///             template approval are the tenant's, and a provider's job is to report the carrier's
///             answer into <see cref="SenderIdentity" /> and
///             <see cref="MessageTemplateVersion.Approval" /> rather than to have an opinion.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The signature deviates from docs/plan/17 § The channel abstraction in two places, and
///         both are deliberate.</b> The sketch there has
///         <c>ValueTask&lt;Result&gt; HandleWebhookAsync(HttpRequest request)</c> and
///         <c>GetStatusAsync</c> with no cancellation. <see cref="WebhookEnvelope" />'s own remarks
///         say why <c>HttpRequest</c> cannot appear in a contracts assembly; and returning
///         <see cref="WebhookOutcome" /> rather than a bare <c>Result</c> is what lets the router do
///         the correlating, so a provider parses its carrier's payload and nothing else. The
///         cancellation token is the repository-wide convention on every seam.
///     </para>
/// </remarks>
public interface IChannelProvider {
    /// <summary>Which channel this serves.</summary>
    ChannelKind Kind { get; }

    /// <summary>
    ///     The name a <see cref="ChannelConfiguration.Provider" /> selects it by — <c>twilio</c>,
    ///     <c>meta-cloud</c>, <c>ses</c>. Lower case, compared ordinally.
    /// </summary>
    string Name { get; }

    /// <summary>Hands one message to the carrier.</summary>
    /// <param name="message">
    ///     Everything needed to send: the destination, the rendered body, the sender, and the
    ///     credential handles. Already checked against the suppression list and the spend limit —
    ///     ⚠ a provider must not re-check either, because a provider that could decide to send is a
    ///     provider that can be wrong about a complaint.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     The carrier's acceptance, with the provider message id. A failure means the carrier
    ///     refused or could not be reached, and <see cref="IMessageGrain" /> releases the spend
    ///     reservation on it.
    /// </returns>
    Task<Result<DispatchReceipt>> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>Asks the carrier where a message got to.</summary>
    /// <param name="providerMessageId">The carrier's id, from <see cref="DispatchReceipt" />.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    ///     The pull half of delivery status. Receipts are the push half and are the one that scales;
    ///     this exists for the message whose receipt never arrived, which is the case an operator
    ///     asks about.
    /// </remarks>
    Task<Result<DeliveryStatus>> GetStatusAsync(string providerMessageId, CancellationToken cancellationToken = default);

    /// <summary>Parses a carrier callback into receipts and inbound messages.</summary>
    /// <param name="request">The callback, verbatim. Verify its signature here.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     What the payload contained. ⚠ <see cref="WebhookOutcome.Empty" /> for a payload this
    ///     provider does not recognise — a success, not a failure. Carriers send keep-alives, test
    ///     pings and event types added after we wrote the parser, and failing on those turns a
    ///     carrier's product decision into our alert.
    /// </returns>
    ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     A message as a carrier receives it — rendered, checked, and about to be sent.
/// </summary>
/// <remarks>
///     ⚠ <b>Distinct from <see cref="SendRequest" /> because the checks happen in between.</b> A
///     <see cref="SendRequest" /> names a template; this carries the rendered text. A
///     <see cref="SendRequest" /> may name a suppressed address; this one cannot, because
///     <see cref="IMessageGrain" /> will not build it. The type boundary is what makes "the provider
///     is never called for a suppressed address" a property of the code rather than of a code path.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.OutboundMessage")]
public sealed record OutboundMessage {
    /// <summary>Our id for it. Pass to the carrier as a client reference where one is accepted.</summary>
    [Id(0)]
    public Guid MessageId { get; init; }

    /// <summary>The tenant, for the provider's own logging and multi-account routing.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The channel.</summary>
    [Id(2)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>Where it goes, normalized.</summary>
    [Id(3)]
    public string Destination { get; init; } = string.Empty;

    /// <summary>What recipients see as the sender.</summary>
    [Id(4)]
    public string Sender { get; init; } = string.Empty;

    /// <summary>The rendered subject, or empty.</summary>
    [Id(5)]
    public string Subject { get; init; } = string.Empty;

    /// <summary>The rendered body.</summary>
    [Id(6)]
    public string Body { get; init; } = string.Empty;

    /// <summary>
    ///     The carrier's template name, for a channel that sends by reference rather than by body.
    ///     Empty for the rest.
    /// </summary>
    [Id(7)]
    public string ProviderTemplateName { get; init; } = string.Empty;

    /// <summary>The template arguments, in declaration order, for a template-by-reference send.</summary>
    [Id(8)]
    public System.Collections.Immutable.ImmutableArray<TemplateArgument> Arguments { get; init; } = [];

    /// <summary>The locale the body was rendered in.</summary>
    [Id(9)]
    public string Locale { get; init; } = string.Empty;

    /// <summary>Which account pays, and the handles that reach it.</summary>
    [Id(10)]
    public CarrierCredentials Credentials { get; init; } = new();
}
