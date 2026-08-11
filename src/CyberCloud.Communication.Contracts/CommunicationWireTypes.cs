using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     A handle to a carrier credential. The value lives in the vault and never in grain state.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row:
///         <i>"secrets are <c>SecretRef</c> handles resolved at the data plane"</i>. Every member here
///         is an address and there is deliberately no member a value could ride in — the same absence
///         argument <c>CyberCloud.ResourceManager.Contracts.SecretRef</c> and
///         <c>CyberCloud.Identity.Contracts.VaultSecretRef</c> make, in this module's vocabulary.
///     </para>
///     <para>
///         ⚠ <b>This matters more here than almost anywhere else in the platform.</b> BYO is offered
///         from day one (docs/plan/17 § The channel abstraction), so the steady state is that this
///         module holds a pointer to <i>a paying customer's</i> Twilio credential. A leak is not our
///         bill and not our incident to close — it is theirs, and we caused it. CC1005 is what keeps
///         the mistake from being made by accident; this type is what makes the right thing easy.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.CarrierSecretRef")]
public sealed record CarrierSecretRef {
    /// <summary>The vault path, for example <c>tenants/{tenantId}/communication/twilio</c>.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>Which field at that path, for example <c>authToken</c>.</summary>
    [Id(1)]
    public string Field { get; init; } = string.Empty;

    /// <summary>
    ///     The version to read, or empty for the current one. Pinning is what makes a dispatch
    ///     reproducible across a rotation.
    /// </summary>
    [Id(2)]
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether this handle names anything at all.</summary>
    public bool IsEmpty => Path.Length == 0 && Field.Length == 0;

    /// <inheritdoc />
    public override string ToString() =>
        Version.Length == 0 ? $"{Path}#{Field}" : $"{Path}#{Field}@{Version}";
}

/// <summary>
///     Which carrier account a channel bills to, and how to reach it.
/// </summary>
/// <remarks>
///     ⚠ <b>Three handles rather than one, because every carrier wants at least two values and they
///     rotate independently.</b> Twilio has an account SID and an auth token; Meta has a business
///     account id and a bearer; SES has an access id and a secret. Modelling one opaque credential
///     would force the provider implementations to stuff a JSON blob into a single vault field, which
///     rotates as a unit and is unreadable in an audit.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.CarrierCredentials")]
public sealed record CarrierCredentials {
    /// <summary>Whose account pays.</summary>
    [Id(0)]
    public CredentialMode Mode { get; init; } = CredentialMode.Unknown;

    /// <summary>The account identifier's handle — a Twilio account SID, a Meta business account id.</summary>
    [Id(1)]
    public CarrierSecretRef AccountRef { get; init; } = new();

    /// <summary>The authenticating value's handle — an auth token, a bearer, an access secret.</summary>
    [Id(2)]
    public CarrierSecretRef AuthRef { get; init; } = new();

    /// <summary>
    ///     The webhook-signing value's handle, when the carrier signs its callbacks. Empty when it
    ///     does not — ⚠ and an empty one is why <see cref="WebhookEnvelope" /> carries the raw body:
    ///     a receipt that cannot be verified is data from the internet.
    /// </summary>
    [Id(3)]
    public CarrierSecretRef SigningRef { get; init; } = new();
}

/// <summary>
///     What a tenant may spend and send on one channel in one window.
/// </summary>
/// <remarks>
///     ⚠ <b>docs/plan/17 § The parts that are actually the work states the stake plainly:</b>
///     <i>"An SMS loop is a five-figure incident within an hour, and the limit is the only thing
///     between a bug and that invoice."</i> Both limits are present because they fail differently: a
///     message count catches a loop sending cheap messages, and a spend cap catches a loop sending
///     expensive ones (a premium destination costs 30× a domestic one, so a count that looks sane can
///     still be a five-figure day).
///     <para>
///         ⚠ <b>This type lives on the durable service resource and never in the hot counter.</b> See
///         <see cref="ISendLimitGrain" /> for why, and for what a hot-tier loss actually costs.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.ChannelLimits")]
public sealed record ChannelLimits {
    /// <summary>The most messages this channel may dispatch in a window. Zero means none.</summary>
    [Id(0)]
    public long MaxMessagesPerWindow { get; init; }

    /// <summary>The most this channel may spend in a window, in <see cref="Currency" />.</summary>
    [Id(1)]
    public decimal MaxSpendPerWindow { get; init; }

    /// <summary>ISO 4217, for example <c>EUR</c>. Named so a refusal can print a figure a human recognises.</summary>
    [Id(2)]
    public string Currency { get; init; } = "EUR";

    /// <summary>
    ///     The window, which docs/plan/17 § The parts that are actually the work fixes at a day.
    /// </summary>
    /// <remarks>
    ///     ⚠ It is the <b>UTC calendar day</b> rather than a rolling 24 hours, and the choice is
    ///     deliberate: a rolling window needs the timestamp of every message in it, which is
    ///     unbounded state on the hot tier, while a calendar day needs two numbers and a date. The
    ///     cost is that a tenant can spend a day's budget at 23:59 and another at 00:01. That is a
    ///     2× overshoot with a floor, against a runaway loop whose damage is unbounded — and the
    ///     refusal names the window so the shape is visible rather than surprising.
    /// </remarks>
    public static TimeSpan Window => TimeSpan.FromDays(1);

    /// <summary>A channel with nothing allowed. The default a service starts from.</summary>
    /// <remarks>
    ///     ⚠ Zero rather than unlimited, and that is the safe direction: a tenant who has not set a
    ///     limit gets a refusal naming the limit, not an invoice naming a number.
    /// </remarks>
    public static ChannelLimits None { get; } = new();
}

/// <summary>How much of a window's allowance is gone, and how much is committed but unsettled.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.ChannelSpend")]
public sealed record ChannelSpend {
    /// <summary>The channel.</summary>
    [Id(0)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>The UTC date the window covers.</summary>
    [Id(1)]
    public DateOnly Window { get; init; }

    /// <summary>Messages dispatched in the window, settled and in flight.</summary>
    [Id(2)]
    public long Messages { get; init; }

    /// <summary>Money settled against actual carrier prices.</summary>
    [Id(3)]
    public decimal Settled { get; init; }

    /// <summary>Money reserved for sends that have not come back yet.</summary>
    [Id(4)]
    public decimal Reserved { get; init; }

    /// <summary>What the limit is measured against — settled plus in-flight.</summary>
    public decimal Committed => Settled + Reserved;
}

/// <summary>
///     A claim on a window's allowance, taken before the carrier is called and settled after.
/// </summary>
/// <remarks>
///     ⚠ <b>A reservation rather than a counter, which is docs/plan/22 § Quota's rule applied
///     here:</b> <i>"Reservation, not a counter — the lease expires if the operation dies."</i>
///     Counting after dispatch means a loop that dispatches faster than it settles never sees the
///     limit; counting before and settling to the real price means the limit binds on the way in and
///     the figure is still accurate on the way out.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SpendReservation")]
public sealed record SpendReservation {
    /// <summary>Identifies this claim, for <see cref="ISendLimitGrain.SettleAsync" />.</summary>
    [Id(0)]
    public Guid ReservationId { get; init; }

    /// <summary>The channel it was taken on.</summary>
    [Id(1)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>What was held.</summary>
    [Id(2)]
    public decimal Amount { get; init; }

    /// <summary>The UTC date whose allowance it came from.</summary>
    [Id(3)]
    public DateOnly Window { get; init; }

    /// <summary>
    ///     When an unsettled claim is released. ⚠ Without this, a silo that dies between reserve and
    ///     dispatch takes a slice of the day's budget with it, permanently.
    /// </summary>
    [Id(4)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>One channel's configuration on a tenant's communication service.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.ChannelConfiguration")]
public sealed record ChannelConfiguration {
    /// <summary>The channel.</summary>
    [Id(0)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>
    ///     Which <see cref="IChannelProvider" /> implementation serves it — <c>twilio</c>,
    ///     <c>meta-cloud</c>, <c>ses</c>, <c>in-memory</c>. Matched against
    ///     <see cref="IChannelProvider.Name" />, ordinally.
    /// </summary>
    [Id(1)]
    public string Provider { get; init; } = string.Empty;

    /// <summary>Whose account pays, and the handles that reach it.</summary>
    [Id(2)]
    public CarrierCredentials Credentials { get; init; } = new();

    /// <summary>What the tenant may send and spend per window. See <see cref="ChannelLimits" />.</summary>
    [Id(3)]
    public ChannelLimits Limits { get; init; } = ChannelLimits.None;

    /// <summary>
    ///     What one message is expected to cost, for the pre-dispatch reservation.
    /// </summary>
    /// <remarks>
    ///     ⚠ An estimate, and it has to be: the true price is per destination country and comes back
    ///     on <see cref="DispatchReceipt.Cost" />, which is after the point where a limit could still
    ///     stop anything. Set it to the most expensive destination the tenant sends to — an estimate
    ///     that is too low turns the spend cap into a suggestion.
    /// </remarks>
    [Id(4)]
    public decimal EstimatedUnitCost { get; init; }

    /// <summary>Whether sending on this channel is turned on at all.</summary>
    [Id(5)]
    public bool Enabled { get; init; }

    /// <summary>
    ///     The registered sender every send on this channel must name, or empty when the platform's
    ///     own sender is used.
    /// </summary>
    [Id(6)]
    public Guid SenderId { get; init; }
}

/// <summary>One argument for a template parameter.</summary>
/// <remarks>
///     ⚠ A pair in an array rather than a dictionary, so the wire form has one ordering and the
///     idempotency key computed over it is stable. A dictionary's enumeration order is not part of
///     its contract, and two callers sending the same message would then compute two keys.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.TemplateArgument")]
public sealed record TemplateArgument {
    /// <summary>The parameter's name, as <see cref="TemplateParameter.Name" /> spells it.</summary>
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The value substituted for <c>{name}</c>.</summary>
    [Id(1)]
    public string Value { get; init; } = string.Empty;
}

/// <summary>What a template version declares about one of its parameters.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.TemplateParameter")]
public sealed record TemplateParameter {
    /// <summary>The name, which appears in the body as <c>{name}</c>.</summary>
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///     Whether a render without it fails. ⚠ Required is the default a tenant should reach for:
    ///     docs/plan/17 § The parts that are actually the work wants <i>"typed parameters"</i>
    ///     precisely so that a missing one is caught here and not by a customer reading
    ///     <c>"Your code is {code}"</c>.
    /// </summary>
    [Id(1)]
    public bool Required { get; init; } = true;

    /// <summary>What goes in it, for the tenant's own documentation. Never validated against.</summary>
    [Id(2)]
    public string Description { get; init; } = string.Empty;
}

/// <summary>One version of a named template, in every locale it has been written in.</summary>
/// <remarks>
///     ⚠ <b>Versions are immutable and a change is a new version.</b> WhatsApp approves a template
///     <i>body</i>, so editing an approved body in place silently invalidates the approval and the
///     carrier starts rejecting sends that worked yesterday. Adding a version leaves the approved one
///     serving traffic while the new one waits for its own decision.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.MessageTemplateVersion")]
public sealed record MessageTemplateVersion {
    /// <summary>The version number, from 1, increasing by one.</summary>
    [Id(0)]
    public int Version { get; init; }

    /// <summary>Which channel it is written for. A WhatsApp body is not an email body.</summary>
    [Id(1)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>The parameters it declares.</summary>
    [Id(2)]
    public ImmutableArray<TemplateParameter> Parameters { get; init; } = [];

    /// <summary>The body per locale — <c>en-US</c>, <c>cs-CZ</c>. Keys compare ordinally.</summary>
    [Id(3)]
    public ImmutableArray<LocalizedBody> Bodies { get; init; } = [];

    /// <summary>
    ///     The carrier's own name for this template, once they have one.
    /// </summary>
    /// <remarks>
    ///     ⚠ WhatsApp does not accept a body at send time — it accepts a template name the carrier
    ///     has already approved, plus arguments. Ours is the name a tenant uses; this is the name the
    ///     carrier knows, and until it is filled a WhatsApp send has nothing to reference.
    /// </remarks>
    [Id(4)]
    public string ProviderTemplateName { get; init; } = string.Empty;

    /// <summary>
    ///     Where carrier pre-approval has got to. ⚠ The tenant's obligation, our record of it —
    ///     docs/plan/17 § The channel abstraction.
    /// </summary>
    [Id(5)]
    public SenderRegistrationStatus Approval { get; init; } = SenderRegistrationStatus.Unknown;

    /// <summary>When it was created.</summary>
    [Id(6)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A template body in one locale.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.LocalizedBody")]
public sealed record LocalizedBody {
    /// <summary>A BCP 47 tag, for example <c>en-US</c>. Compared ordinally.</summary>
    [Id(0)]
    public string Locale { get; init; } = string.Empty;

    /// <summary>The subject, for channels that have one. Empty elsewhere.</summary>
    [Id(1)]
    public string Subject { get; init; } = string.Empty;

    /// <summary>The body, with <c>{name}</c> placeholders.</summary>
    [Id(2)]
    public string Body { get; init; } = string.Empty;
}

/// <summary>A template body with its arguments substituted.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.RenderedMessage")]
public sealed record RenderedMessage {
    /// <summary>The subject, or empty.</summary>
    [Id(0)]
    public string Subject { get; init; } = string.Empty;

    /// <summary>The body.</summary>
    [Id(1)]
    public string Body { get; init; } = string.Empty;

    /// <summary>The locale actually used, which may not be the one asked for.</summary>
    [Id(2)]
    public string Locale { get; init; } = string.Empty;

    /// <summary>The template version rendered.</summary>
    [Id(3)]
    public int Version { get; init; }

    /// <summary>The carrier's template name, for channels that send by reference.</summary>
    [Id(4)]
    public string ProviderTemplateName { get; init; } = string.Empty;
}

/// <summary>
///     What a caller asks us to send. The grain assigns everything a caller must not choose.
/// </summary>
/// <remarks>
///     ⚠ <b>No status, no provider message id, no timestamps and no cost here, and their absence is
///     the design.</b> A caller that could set a status could claim a message was delivered; one that
///     could set a provider id could point a delivery receipt at somebody else's message. Every field
///     that makes a <see cref="MessageSnapshot" /> evidence is assigned by <see cref="IMessageGrain" />
///     and is unreachable from the wire — the same argument <c>UsageLedgerAppend</c> makes.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SendRequest")]
public sealed record SendRequest {
    /// <summary>The <c>CyberCloud.Communication/services</c> resource this sends through.</summary>
    [Id(0)]
    public Guid ServiceId { get; init; }

    /// <summary>Which channel.</summary>
    [Id(1)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>
    ///     Where it goes — an email address or an E.164 number, unredacted. This is the delivery path,
    ///     and it is normalized by <see cref="Destinations.Normalize" /> before anything compares it.
    /// </summary>
    [Id(2)]
    public string Destination { get; init; } = string.Empty;

    /// <summary>The template's name within the service, or empty for a channel that allows free text.</summary>
    [Id(3)]
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>Which version, or <c>0</c> for the newest approved one.</summary>
    [Id(4)]
    public int TemplateVersion { get; init; }

    /// <summary>The locale asked for, for example <c>cs-CZ</c>. Falls back per <see cref="TemplateRenderer" />.</summary>
    [Id(5)]
    public string Locale { get; init; } = string.Empty;

    /// <summary>The template arguments.</summary>
    [Id(6)]
    public ImmutableArray<TemplateArgument> Arguments { get; init; } = [];

    /// <summary>
    ///     Free-text body, for a channel and a tenant that allow one. ⚠ Ignored when
    ///     <see cref="TemplateName" /> is set, and refused for <see cref="ChannelKind.WhatsApp" />.
    /// </summary>
    [Id(7)]
    public string Body { get; init; } = string.Empty;

    /// <summary>
    ///     The caller's idempotency key — docs/plan/17 § The parts that are actually the work:
    ///     <i>"Every send carries a client-supplied key; a retry after a timeout must not send
    ///     twice."</i>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not a secret, and CC1005 is suppressed rather than the member renamed.</b> The rule
    ///     bans <c>[Id]</c>-annotated members ending in <c>Key</c> because a secret in grain state is
    ///     a secret in every backup. This one is chosen by the caller to be <i>repeatable</i> — it is
    ///     printed in traces, compared across silos and used as half a grain key on purpose, which is
    ///     the opposite of a credential. Renaming it would lose the term docs/plan/17 uses, which is
    ///     the term a reader will search for.
    ///     <para>
    ///         ⚠ It is also the reason a caller must not put anything sensitive in it: it reaches the
    ///         grain id, and <c>GrainKeys.Session</c>'s remarks say what that costs — the value ends
    ///         up in every log line and trace that prints a grain id.
    ///     </para>
    /// </remarks>
    [Id(8)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "Not a secret. This is the client-supplied idempotency key of docs/plan/17 § The parts "
            + "that are actually the work — a value the caller chooses so that a retry repeats it, "
            + "deliberately logged, traced and used as half a grain key. A credential is the thing "
            + "you must not repeat; this is the thing you must. The name is docs/plan/17's."
    )]
    public string IdempotencyKey { get; init; } = string.Empty;
}

/// <summary>Everything known about one message. What a status read returns.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.MessageSnapshot")]
public sealed record MessageSnapshot {
    /// <summary>The message's own id, assigned once and never reused.</summary>
    [Id(0)]
    public Guid MessageId { get; init; }

    /// <summary>The service it was sent through.</summary>
    [Id(1)]
    public Guid ServiceId { get; init; }

    /// <summary>The channel.</summary>
    [Id(2)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>Where it went, normalized.</summary>
    [Id(3)]
    public string Destination { get; init; } = string.Empty;

    /// <summary>Where it has got to.</summary>
    [Id(4)]
    public MessageStatus Status { get; init; } = MessageStatus.Unknown;

    /// <summary>The carrier's id for it, once one exists. The correlation key for a delivery receipt.</summary>
    [Id(5)]
    public string ProviderMessageId { get; init; } = string.Empty;

    /// <summary>Which <see cref="IChannelProvider" /> served it.</summary>
    [Id(6)]
    public string Provider { get; init; } = string.Empty;

    /// <summary>When the send was accepted.</summary>
    [Id(7)]
    public DateTimeOffset QueuedAt { get; init; }

    /// <summary>When a carrier took it, or <see langword="null" />.</summary>
    [Id(8)]
    public DateTimeOffset? DispatchedAt { get; init; }

    /// <summary>When a receipt settled it, or <see langword="null" />.</summary>
    [Id(9)]
    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>What the carrier charged, once a receipt says so.</summary>
    [Id(10)]
    public decimal Cost { get; init; }

    /// <summary>The currency of <see cref="Cost" />.</summary>
    [Id(11)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>Every receipt seen for this message, in arrival order.</summary>
    [Id(12)]
    public ImmutableArray<DeliveryReceipt> Receipts { get; init; } = [];

    /// <summary>
    ///     Why it was refused or failed, in the words the caller gets. Empty otherwise.
    /// </summary>
    /// <remarks>
    ///     ⚠ The body is deliberately <b>not</b> here. A snapshot is the answer to "did it arrive",
    ///     and an OTP or a password-reset link in a status object is a credential in a status object.
    /// </remarks>
    [Id(13)]
    public string Detail { get; init; } = string.Empty;
}

/// <summary>What a channel provider hands back when it accepts a message.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.DispatchReceipt")]
public sealed record DispatchReceipt {
    /// <summary>The carrier's id. ⚠ The only handle a delivery receipt arrives with.</summary>
    [Id(0)]
    public string ProviderMessageId { get; init; } = string.Empty;

    /// <summary>Where the carrier says it is, right now.</summary>
    [Id(1)]
    public MessageStatus Status { get; init; } = MessageStatus.Dispatched;

    /// <summary>What it cost, if the carrier says at accept time. Many do not.</summary>
    [Id(2)]
    public decimal Cost { get; init; }

    /// <summary>The currency of <see cref="Cost" />.</summary>
    [Id(3)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>When the carrier accepted it.</summary>
    [Id(4)]
    public DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>A carrier's statement about where a message got to.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.DeliveryReceipt")]
public sealed record DeliveryReceipt {
    /// <summary>Which message, in the carrier's terms.</summary>
    [Id(0)]
    public string ProviderMessageId { get; init; } = string.Empty;

    /// <summary>What the carrier says.</summary>
    [Id(1)]
    public MessageStatus Status { get; init; } = MessageStatus.Unknown;

    /// <summary>The carrier's own code, kept verbatim for support.</summary>
    [Id(2)]
    public string ProviderStatus { get; init; } = string.Empty;

    /// <summary>The carrier's explanation, kept verbatim.</summary>
    [Id(3)]
    public string Detail { get; init; } = string.Empty;

    /// <summary>What it cost, when the receipt is the first place the price appears.</summary>
    [Id(4)]
    public decimal Cost { get; init; }

    /// <summary>The currency of <see cref="Cost" />.</summary>
    [Id(5)]
    public string Currency { get; init; } = string.Empty;

    /// <summary>When the carrier says the event happened.</summary>
    [Id(6)]
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     Whether this receipt reports a permanent failure that should suppress the address.
    /// </summary>
    /// <remarks>
    ///     ⚠ Set by the provider implementation, not inferred here. Only the carrier knows whether
    ///     its code 21610 is a hard bounce or a transient one, and guessing in the wrong direction
    ///     either suppresses a good address or ignores a complaint.
    /// </remarks>
    [Id(7)]
    public SuppressionReason Suppresses { get; init; } = SuppressionReason.Unknown;
}

/// <summary>Where a carrier says a message is, when asked rather than told.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.DeliveryStatus")]
public sealed record DeliveryStatus {
    /// <summary>The carrier's id.</summary>
    [Id(0)]
    public string ProviderMessageId { get; init; } = string.Empty;

    /// <summary>What the carrier says.</summary>
    [Id(1)]
    public MessageStatus Status { get; init; } = MessageStatus.Unknown;

    /// <summary>The carrier's own code.</summary>
    [Id(2)]
    public string ProviderStatus { get; init; } = string.Empty;

    /// <summary>When it was checked.</summary>
    [Id(3)]
    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>A message from a recipient to the tenant. Replies, and <c>STOP</c>.</summary>
/// <remarks>
///     ⚠ <b>docs/plan/17 § The parts that are actually the work: <c>STOP</c> handling is legally
///     required in most jurisdictions.</b> Which is why inbound is not a nice-to-have surface that
///     forwards text somewhere — <see cref="IWebhookRouter.HandleInboundAsync" /> suppresses before
///     it forwards, so a router misconfiguration cannot cost an opt-out.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.InboundMessage")]
public sealed record InboundMessage {
    /// <summary>The service it arrived on.</summary>
    [Id(0)]
    public Guid ServiceId { get; init; }

    /// <summary>Which channel.</summary>
    [Id(1)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>Who sent it — the recipient of whatever we sent them.</summary>
    [Id(2)]
    public string From { get; init; } = string.Empty;

    /// <summary>Which of our senders they replied to.</summary>
    [Id(3)]
    public string To { get; init; } = string.Empty;

    /// <summary>What they wrote.</summary>
    [Id(4)]
    public string Body { get; init; } = string.Empty;

    /// <summary>The carrier's id for the inbound message.</summary>
    [Id(5)]
    public string ProviderMessageId { get; init; } = string.Empty;

    /// <summary>When the carrier says it arrived.</summary>
    [Id(6)]
    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>What handling one inbound message did.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.InboundOutcome")]
public sealed record InboundOutcome {
    /// <summary>Whether the body was a stop keyword.</summary>
    [Id(0)]
    public bool WasStop { get; init; }

    /// <summary>The keyword matched, for the audit trail.</summary>
    [Id(1)]
    public string Keyword { get; init; } = string.Empty;

    /// <summary>Whether the sender ended up suppressed as a result.</summary>
    [Id(2)]
    public bool Suppressed { get; init; }
}

/// <summary>One address a tenant may not send to, and why.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SuppressionEntry")]
public sealed record SuppressionEntry {
    /// <summary>The channel it applies to.</summary>
    [Id(0)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>The address, as <see cref="Destinations.Normalize" /> spells it.</summary>
    [Id(1)]
    public string Destination { get; init; } = string.Empty;

    /// <summary>Why.</summary>
    [Id(2)]
    public SuppressionReason Reason { get; init; } = SuppressionReason.Unknown;

    /// <summary>When it went on the list.</summary>
    [Id(3)]
    public DateTimeOffset SuppressedAt { get; init; }

    /// <summary>What happened, in the carrier's or the operator's words.</summary>
    [Id(4)]
    public string Note { get; init; } = string.Empty;
}

/// <summary>Whether an address is suppressed, and the entry if it is.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SuppressionCheck")]
public sealed record SuppressionCheck {
    /// <summary>Whether sending is blocked.</summary>
    [Id(0)]
    public bool IsSuppressed { get; init; }

    /// <summary>The entry, or <see langword="null" />.</summary>
    [Id(1)]
    public SuppressionEntry? Entry { get; init; }

    /// <summary>Nothing on the list matched.</summary>
    public static SuppressionCheck Clear { get; } = new();
}

/// <summary>
///     A sender a tenant has registered with a carrier, and how far the registration got.
/// </summary>
/// <remarks>
///     ⚠ <b>A first-class flow, not an afterthought</b> — docs/plan/17 § The channel abstraction.
///     Sender-id registration, 10DLC campaign approval and per-country content rules belong to the
///     tenant; this resource is the tooling. Nothing here decides anything: the platform records what
///     was submitted, records what the carrier answered, and refuses to send through a sender the
///     carrier has not approved.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.SenderIdentity")]
public sealed record SenderIdentity {
    /// <summary>The sender resource's id.</summary>
    [Id(0)]
    public Guid SenderId { get; init; }

    /// <summary>Which channel it sends on.</summary>
    [Id(1)]
    public ChannelKind Channel { get; init; } = ChannelKind.Unknown;

    /// <summary>
    ///     What recipients see — an alphanumeric sender id, an E.164 number, a <c>From</c> address.
    /// </summary>
    [Id(2)]
    public string Value { get; init; } = string.Empty;

    /// <summary>Where the carrier's registration stands.</summary>
    [Id(3)]
    public SenderRegistrationStatus Status { get; init; } = SenderRegistrationStatus.Unknown;

    /// <summary>
    ///     The ISO 3166-1 alpha-2 countries this sender is cleared for. ⚠ Empty is <b>not</b>
    ///     "everywhere" — see <see cref="ISenderIdentityGrain.CheckAsync" />.
    /// </summary>
    [Id(4)]
    public ImmutableArray<string> Countries { get; init; } = [];

    /// <summary>
    ///     The carrier's campaign or registration reference — a 10DLC campaign id, a Meta WABA id.
    ///     Ours to quote back at them, not ours to issue.
    /// </summary>
    [Id(5)]
    public string CarrierReference { get; init; } = string.Empty;

    /// <summary>When the status last changed.</summary>
    [Id(6)]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The carrier's reason, verbatim, when they rejected or revoked it.</summary>
    [Id(7)]
    public string Note { get; init; } = string.Empty;
}

/// <summary>
///     One tenant's communication service — <c>CyberCloud.Communication/services/{name}</c>.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.CommunicationService")]
public sealed record CommunicationService {
    /// <summary>The resource's id.</summary>
    [Id(0)]
    public Guid ServiceId { get; init; }

    /// <summary>The tenant that owns it.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The resource's name within its group.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Every channel configured on it.</summary>
    [Id(3)]
    public ImmutableArray<ChannelConfiguration> Channels { get; init; } = [];

    /// <summary>When it was created.</summary>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     A carrier callback, in a form that does not drag ASP.NET Core into a contracts assembly.
/// </summary>
/// <remarks>
///     ⚠ <b>docs/plan/17 § The channel abstraction sketches
///     <c>HandleWebhookAsync(HttpRequest request)</c>, and this deviates deliberately.</b> An
///     <c>HttpRequest</c> in <c>.Contracts</c> would make every assembly that references this one —
///     including <c>CyberCloud.Identity.Contracts</c>, which only wants to send an OTP — depend on
///     <c>Microsoft.AspNetCore.Http</c>. It also makes the seam untestable without a request pipeline
///     and unusable from a queue consumer, which is the other shape a carrier callback arrives in.
///     The gateway adapts one to the other in about ten lines; see <see cref="IChannelProvider" />.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Communication.WebhookEnvelope")]
public sealed record WebhookEnvelope {
    /// <summary>The service whose webhook path this arrived on. ⚠ This is where the tenant comes from.</summary>
    [Id(0)]
    public Guid ServiceId { get; init; }

    /// <summary>The HTTP method, upper case.</summary>
    [Id(1)]
    public string Method { get; init; } = "POST";

    /// <summary>The path it arrived on, for carriers that route by it.</summary>
    [Id(2)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The headers, as received. Carrier signatures live here.</summary>
    [Id(3)]
    public ImmutableArray<TemplateArgument> Headers { get; init; } = [];

    /// <summary>
    ///     The body, exactly as received and not re-serialized. ⚠ A signature is over the bytes the
    ///     carrier sent; a round trip through a JSON parser changes them and every verification fails.
    /// </summary>
    [Id(4)]
    public string Body { get; init; } = string.Empty;

    /// <summary>When it arrived.</summary>
    [Id(5)]
    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>What a provider made of a webhook.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Communication.WebhookOutcome")]
public sealed record WebhookOutcome {
    /// <summary>The delivery receipts it carried, if any.</summary>
    [Id(0)]
    public ImmutableArray<DeliveryReceipt> Receipts { get; init; } = [];

    /// <summary>The inbound messages it carried, if any.</summary>
    [Id(1)]
    public ImmutableArray<InboundMessage> Inbound { get; init; } = [];

    /// <summary>Nothing recognisable, and that is fine. See <see cref="IWebhookRouter" />.</summary>
    public static WebhookOutcome Empty { get; } = new();
}
