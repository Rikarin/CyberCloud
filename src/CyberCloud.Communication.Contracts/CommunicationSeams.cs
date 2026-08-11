namespace CyberCloud.Communication.Contracts;

/// <summary>
///     What the rest of the platform calls to send something.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the seam three built modules are currently stubbing.</b> docs/plan/11
///         § Credentials routes email, SMS and WhatsApp OTP through this module; docs/plan/16
///         § Alerts fans a firing alert out through it; docs/plan/22 § Invoicing sends every dunning
///         step through it, and is explicit that <i>"every step notified, with the timeline
///         stated"</i> — a suspension nobody was told about is the failure that turns a billing
///         problem into a churn problem.
///     </para>
///     <para>
///         ⚠ <b>Interface rather than a grain reference, for the reason
///         <c>IUsageEmitter</c> gives.</b> A caller in a gateway or a host is not a grain, so
///         <c>Orleans.Multitenant</c>'s call filter never sees it and every <c>GetGrain</c> has to
///         be qualified with <c>ForTenant</c> — CC1006. Handing callers an interface means exactly
///         one place in the tree gets that right, rather than one place per caller.
///     </para>
///     <para>
///         ⚠ <b>It reports failure and never succeeds quietly.</b> That is the same rule
///         <c>IOtpDeliverySeam</c> states from the identity side: <i>"an OTP factor that reports
///         delivery and sends nothing locks every user who enrols in it out of their account"</i>.
///     </para>
/// </remarks>
public interface IMessageSender {
    /// <summary>Sends one message, or returns the one already sent under this idempotency key.</summary>
    /// <param name="tenantId">Whose service to send through. Qualifies the grain call.</param>
    /// <param name="request">
    ///     What to send. ⚠ <see cref="SendRequest.IdempotencyKey" /> is required and is the caller's
    ///     to choose well: it must be a function of the thing being notified about, not of the
    ///     attempt. <c>Guid.NewGuid()</c> sends twice and is the mistake this parameter exists to
    ///     prevent.
    ///     <para>
    ///         ⚠ <b>A clock reading is not the answer either, and this parameter used to recommend
    ///         one.</b> The suggestion here was <c>$"otp-{userId:N}-{purpose}-{window:O}"</c>, and it
    ///         is wrong in both directions — <c>CyberCloud.Identity.Tests.OtpDeliveryTests</c> has
    ///         the evidence. Too coarse a window and two genuinely distinct codes compute one key, so
    ///         the second differs in content and comes back <see cref="ErrorCode.Conflict" />: not a
    ///         silent duplicate, a broken "resend". Too fine — or unrounded, which the <c>O</c>
    ///         round-trip format is, since it prints sub-second precision — and a retry landing on
    ///         the far side of a boundary computes a new key and sends twice, which is
    ///         <c>Guid.NewGuid()</c> by another route. Derive it from something that changes when,
    ///         and only when, the notification is genuinely a different one:
    ///         <c>CyberCloud.Identity.Seams.CommunicationOtpDelivery</c> uses a digest over the code
    ///         itself, and its remarks say what that costs.
    ///     </para>
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<MessageSnapshot>> SendAsync(
        Guid tenantId,
        SendRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Where a message got to. The answer to "did it arrive".</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service it was sent through.</param>
    /// <param name="idempotencyKey">The key it was sent under.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<MessageSnapshot>> GetStatusAsync(
        Guid tenantId,
        Guid serviceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Takes a carrier callback apart and routes what is in it — delivery receipts to their message
///     grains, inbound messages to suppression and onwards.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The parts that are actually the work makes both halves not-optional:
///         receipts because <i>"without them 'did it arrive' is unanswerable"</i>, and inbound
///         because <i><c>STOP</c> handling is legally required in most jurisdictions</i>.
///     </para>
///     <para>
///         ⚠ <b>The provider parses and the router correlates, and keeping those apart is what makes
///         a new carrier cheap.</b> A provider implementation knows one carrier's payload shape and
///         nothing about grains; this knows about grains and nothing about payloads. The alternative
///         — every provider reaching into the message grain — is where the correlation logic gets
///         written twenty times and gets the late-receipt case wrong nineteen.
///     </para>
/// </remarks>
public interface IWebhookRouter {
    /// <summary>Handles one carrier callback end to end.</summary>
    /// <param name="tenantId">The tenant whose service the callback arrived on.</param>
    /// <param name="envelope">The callback, verbatim.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     How many receipts and inbound messages were handled.
    ///     <para>
    ///         ⚠ <b>A receipt for an unknown provider message id is ignored, not fatal.</b> It is
    ///         counted separately so the number is observable, and the call still succeeds. Webhooks
    ///         arrive late, twice, and for messages past <see cref="IMessageGrain.Retention" />, and
    ///         a router that failed on those would page somebody every time a carrier retried.
    ///     </para>
    /// </returns>
    Task<Result<WebhookHandling>> HandleAsync(
        Guid tenantId,
        WebhookEnvelope envelope,
        CancellationToken cancellationToken = default
    );

    /// <summary>Handles one inbound message — a reply, or an opt-out.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="inbound">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     What was done with it. ⚠ Suppression happens <b>before</b> anything else, so a
    ///     misconfigured forwarding destination cannot cost an opt-out.
    /// </returns>
    Task<Result<InboundOutcome>> HandleInboundAsync(
        Guid tenantId,
        InboundMessage inbound,
        CancellationToken cancellationToken = default
    );

    /// <summary>Records one delivery receipt against its message.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service the receipt arrived on.</param>
    /// <param name="receipt">The carrier's statement.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     <c>true</c> when the receipt found a message. <c>false</c> — a success — when it did not.
    /// </returns>
    Task<Result<bool>> HandleReceiptAsync(
        Guid tenantId,
        Guid serviceId,
        DeliveryReceipt receipt,
        CancellationToken cancellationToken = default
    );
}

/// <summary>What handling one webhook did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.WebhookHandling")]
public sealed record WebhookHandling {
    /// <summary>Receipts that found their message.</summary>
    [Id(0)]
    public int ReceiptsApplied { get; init; }

    /// <summary>
    ///     Receipts whose provider message id matched nothing. ⚠ Not an error — see
    ///     <see cref="IWebhookRouter.HandleAsync" /> — but worth a metric, because a number that
    ///     climbs means the retention horizon is shorter than the carrier's receipt latency.
    /// </summary>
    [Id(1)]
    public int ReceiptsIgnored { get; init; }

    /// <summary>Inbound messages handled.</summary>
    [Id(2)]
    public int InboundHandled { get; init; }

    /// <summary>Inbound messages that were opt-outs and suppressed an address.</summary>
    [Id(3)]
    public int OptOuts { get; init; }
}

/// <summary>
///     Finds the <see cref="IChannelProvider" /> a channel's configuration names.
/// </summary>
/// <remarks>
///     ⚠ <b>A named lookup rather than one provider per channel, because BYO makes the mapping
///     many-to-one.</b> Two tenants on <see cref="ChannelKind.Sms" /> may be on Twilio and Vonage;
///     one tenant may move between them. Resolving by <see cref="ChannelConfiguration.Provider" />
///     means that is a configuration change rather than a deployment.
/// </remarks>
public interface IChannelProviderRegistry {
    /// <summary>The provider serving a channel under a name.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="name">
    ///     <see cref="ChannelConfiguration.Provider" />. ⚠ Empty resolves to the channel's registered
    ///     default, which in a build with no carrier client is the refusing seam — never a silent
    ///     success.
    /// </param>
    /// <returns>
    ///     <see cref="ErrorCode.InternalError" /> when nothing serves that channel at all, naming
    ///     what would have to be registered.
    /// </returns>
    Result<IChannelProvider> Resolve(ChannelKind channel, string name);
}
