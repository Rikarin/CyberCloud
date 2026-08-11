namespace CyberCloud.Communication.Contracts;

/// <summary>
///     Which carrier surface a message travels over. docs/plan/17 § The channel abstraction's
///     <c>Sms | WhatsApp | Email | Push | Voice</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Chat is absent and its absence is docs/plan/17 § Chat, not an omission.</b> Chat is M3 and
///     is a different shape entirely — threads, participants, read receipts, typing, over SignalR —
///     rather than a fifth thing you dispatch one of. Adding it here would make every switch in this
///     assembly wrong in a way the compiler could not see.
/// </remarks>
[Alias("CyberCloud.Communication.ChannelKind")]
public enum ChannelKind {
    /// <summary>The zero value a default-constructed wire type carries. Never a channel.</summary>
    Unknown = 0,

    /// <summary>Text messaging. ⚠ Sender-id registration and US 10DLC campaign approval apply.</summary>
    Sms,

    /// <summary>
    ///     WhatsApp, through the Meta Cloud API. ⚠ Business-initiated messages <b>require</b> a
    ///     pre-approved template — see <see cref="IMessageTemplateGrain" />.
    /// </summary>
    WhatsApp,

    /// <summary>Transactional email. Deliverability is SPF, DKIM and DMARC on the tenant's domain.</summary>
    Email,

    /// <summary>Mobile push, through APNs or FCM.</summary>
    Push,

    /// <summary>Voice. ⚠ Per-country content and consent rules are the strictest of the five.</summary>
    Voice
}

/// <summary>
///     Where one message is in its life. docs/plan/17 § The parts that are actually the work:
///     <i>"queued → dispatched → delivered/failed"</i>.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Refused" /> is a sixth state the sentence above does not name, and it is here
///     because collapsing it into <see cref="Failed" /> loses the only distinction that matters
///     operationally.</b> A refusal means no carrier was called: the address is suppressed, the
///     template is missing a parameter, the sender is unregistered, or the day's spend is gone.
///     Nothing was sent, nothing will be charged, and a retry with the same content will refuse
///     again. <see cref="Failed" /> means the carrier was called and said no, which is a different
///     conversation with a different owner.
/// </remarks>
[Alias("CyberCloud.Communication.MessageStatus")]
public enum MessageStatus {
    /// <summary>No message has been recorded under this idempotency key.</summary>
    Unknown = 0,

    /// <summary>Accepted and recorded, not yet handed to a carrier.</summary>
    Queued,

    /// <summary>A carrier accepted it and returned a provider message id.</summary>
    Dispatched,

    /// <summary>A delivery receipt said it arrived.</summary>
    Delivered,

    /// <summary>A carrier or a receipt said it did not arrive.</summary>
    Failed,

    /// <summary>⚠ Stopped before dispatch. No carrier was called. See the remarks on this type.</summary>
    Refused
}

/// <summary>
///     Why an address is on a tenant's suppression list. docs/plan/17 § The parts that are actually
///     the work: <i>"Bounces, complaints, opt-outs"</i>.
/// </summary>
/// <remarks>
///     ⚠ <b>The reason decides whether the tenant may lift it, and that is the whole point of
///     recording one.</b> A <see cref="HardBounce" /> is a fact about an address and a tenant
///     correcting a typo may clear it. A <see cref="Complaint" /> and an <see cref="OptOut" /> are
///     statements by the recipient: clearing either on the tenant's say-so is how a sending domain
///     gets blocked, and for <see cref="OptOut" /> it is unlawful in most jurisdictions.
///     <see cref="ISuppressionListGrain.ReleaseAsync" /> enforces the split.
/// </remarks>
[Alias("CyberCloud.Communication.SuppressionReason")]
public enum SuppressionReason {
    /// <summary>The zero value. Never a reason.</summary>
    Unknown = 0,

    /// <summary>The address does not exist. A permanent failure from the carrier.</summary>
    HardBounce,

    /// <summary>The recipient marked it as spam. ⚠ Only the recipient can undo this.</summary>
    Complaint,

    /// <summary>The recipient sent <c>STOP</c> or unsubscribed. ⚠ Only the recipient can undo this.</summary>
    OptOut,

    /// <summary>An operator or the tenant blocked the address deliberately.</summary>
    ManualBlock
}

/// <summary>
///     Whose carrier account pays for a channel. docs/plan/17 § The channel abstraction: the
///     platform's account is <i>"marked-up, no setup"</i> and the tenant's own is <i>"BYO, cheaper"</i>.
/// </summary>
/// <remarks>
///     ⚠ <b>Both are M1 of this module and <see cref="TenantAccount" /> is not a later addition.</b>
///     docs/plan/17 § The channel abstraction: <i>"BYO is offered from day one, because a tenant with
///     an existing Twilio contract will not move it and refusing them is refusing the customer."</i>
///     The design consequence is <see cref="CarrierCredentials" />: the credential is a handle, per
///     channel, resolved at dispatch, so the two modes differ by which handle is read and not by
///     which code path runs.
/// </remarks>
[Alias("CyberCloud.Communication.CredentialMode")]
public enum CredentialMode {
    /// <summary>The zero value. Never a mode.</summary>
    Unknown = 0,

    /// <summary>The platform's carrier account. Marked up, nothing for the tenant to set up.</summary>
    PlatformAccount,

    /// <summary>The tenant's own carrier contract, reached through their own credential handles.</summary>
    TenantAccount
}

/// <summary>
///     How far a tenant's own sender identity has got through the carrier's registration.
/// </summary>
/// <remarks>
///     ⚠ <b>We are a broker, not a carrier, and this enum is where the product says so.</b>
///     docs/plan/17 § The channel abstraction: <i>"Sender-id registration, 10DLC campaign approval in
///     the US, WhatsApp template pre-approval, and per-country content rules are the tenant's
///     compliance obligations with our tooling, not obligations we assume."</i> The platform records
///     and reports this status; it does not grant it, and
///     <see cref="ISenderIdentityGrain.RecordDecisionAsync" /> takes the carrier's answer rather than
///     making one.
/// </remarks>
[Alias("CyberCloud.Communication.SenderRegistrationStatus")]
public enum SenderRegistrationStatus {
    /// <summary>The zero value. Nothing has been submitted.</summary>
    Unknown = 0,

    /// <summary>Declared to us, not yet submitted to the carrier.</summary>
    Draft,

    /// <summary>Submitted. The carrier has not answered.</summary>
    Pending,

    /// <summary>The carrier approved it. The only status a send may use.</summary>
    Approved,

    /// <summary>The carrier rejected it. <see cref="SenderIdentity.Note" /> carries their reason.</summary>
    Rejected,

    /// <summary>Approved once and withdrawn since. Sending stops immediately.</summary>
    Revoked
}
