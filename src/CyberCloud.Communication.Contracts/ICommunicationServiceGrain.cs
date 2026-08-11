using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     A tenant's <c>CyberCloud.Communication/services/{name}</c> resource — which channels it has,
///     whose carrier account each one bills to, and what it may spend.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <b>Durable</b> · <b>Key</b> <c>res/{serviceId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Durable because losing it loses a paying customer's carrier wiring.</b> docs/plan/05
///         § Choosing a tier asks whether the state can be rebuilt. Nothing upstream holds a tenant's
///         choice of provider, their credential handles, or their spend caps — the tenant does, in
///         their head, and asking them to re-enter it is asking them to re-do the onboarding they
///         paid for. The caps are the sharper half: <see cref="ChannelLimits.None" /> is the default,
///         so a lost configuration does not overspend — it stops sending entirely, and every OTP in
///         the platform fails at once.
///     </para>
///     <para>
///         ⚠ <b>BYO credentials are handles and never values</b> — <see cref="CarrierSecretRef" />,
///         and CC1005 is what keeps it that way. docs/plan/17 § The channel abstraction offers BYO
///         from day one, so the steady state is that this durable, replicated, backed-up grain sits
///         next to a customer's Twilio contract. The right amount of credential material in it is
///         none.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.ICommunicationServiceGrain")]
public interface ICommunicationServiceGrain : IGrainWithStringKey {
    /// <summary>Creates the service, or reports the one that is already there.</summary>
    /// <param name="tenantId">The owning tenant, which must match the grain's tenant qualification.</param>
    /// <param name="name">The resource's name within its group.</param>
    Task<Result<CommunicationService>> CreateAsync(Guid tenantId, string name);

    /// <summary>Everything about the service. ⚠ Credential handles, never credential values.</summary>
    Task<Result<CommunicationService>> DescribeAsync();

    /// <summary>Configures one channel, replacing whatever was there.</summary>
    /// <param name="configuration">
    ///     The provider, the credential handles, the caps and the sender. ⚠ Rejected with
    ///     <see cref="ErrorCode.InvalidRequestBody" /> when
    ///     <see cref="CredentialMode.TenantAccount" /> is asked for with empty handles: a BYO channel
    ///     with nothing to authenticate as would fall back to the platform's account and bill the
    ///     tenant at the marked-up rate they chose BYO to avoid.
    /// </param>
    Task<Result<CommunicationService>> ConfigureChannelAsync(ChannelConfiguration configuration);

    /// <summary>One channel's configuration, as the send path reads it.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>
    ///     <see cref="ErrorCode.ResourceNotFound" /> when the channel is not configured, which is
    ///     distinct from configured-and-disabled: one is "this tenant does not use SMS" and the other
    ///     is "somebody turned it off", and a support case starts by telling them apart.
    /// </returns>
    Task<Result<ChannelConfiguration>> GetChannelAsync(ChannelKind channel);

    /// <summary>Turns a configured channel on or off without losing its configuration.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="enabled">Whether sending is allowed.</param>
    /// <remarks>
    ///     ⚠ The kill switch. When a tenant's sending is the incident — a loop, a compromised API
    ///     key, an abuse report — this is what an operator reaches for, and it must not require
    ///     destroying the configuration to use.
    /// </remarks>
    Task<Result> SetChannelEnabledAsync(ChannelKind channel, bool enabled);

    /// <summary>Every channel configured, in configuration order.</summary>
    Task<Result<ImmutableArray<ChannelConfiguration>>> ListChannelsAsync();

    /// <summary>Records that a template name belongs to a template resource.</summary>
    /// <param name="name">The name a send references.</param>
    /// <param name="templateId">The template child resource's GUID.</param>
    /// <remarks>
    ///     ⚠ <b>The service is the naming authority for its children, which is why this lives here
    ///     and not on the template.</b> A send names a template by name and the send path has to get
    ///     from that to a grain key in one hop. The alternative — a digest-keyed index grain per
    ///     name — is what the resource manager does for full resource paths, and it needs a key
    ///     shape and a two-phase claim for a set that is small, per-service, and already being read.
    /// </remarks>
    Task<Result> RegisterTemplateAsync(string name, Guid templateId);

    /// <summary>The template resource a name refers to.</summary>
    /// <param name="name">The name a send references.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when no template has that name.</returns>
    Task<Result<Guid>> ResolveTemplateAsync(string name);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     A sender a tenant registered with a carrier — docs/plan/17 § The channel abstraction's
///     compliance obligations, made a resource.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <b>Durable</b> · <b>Key</b> <c>res/{senderId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>A first-class flow, which is what docs/plan/17 § The channel abstraction asks for by
///         insisting the product say we are a broker and not a carrier.</b> Sender-id registration,
///         10DLC campaign approval in the US, and per-country content rules belong to the tenant.
///         Making that an afterthought — a text field on a send request — would mean the platform
///         quietly attempting sends the carrier will reject and the regulator will attribute to
///         somebody. Making it a resource means the tenant can see the status, the platform can
///         refuse before dispatch, and the record of who submitted what exists when it is asked for.
///     </para>
///     <para>
///         ⚠ <b>Durable because it is a compliance record.</b> It is evidence of what was submitted
///         and what the carrier answered, and the question it answers arrives months later from
///         somebody with the authority to ask.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.ISenderIdentityGrain")]
public interface ISenderIdentityGrain : IGrainWithStringKey {
    /// <summary>Declares a sender the tenant intends to use.</summary>
    /// <param name="channel">Which channel it sends on.</param>
    /// <param name="value">What recipients see — an alphanumeric sender id, an E.164 number, a <c>From</c> address.</param>
    /// <param name="countries">
    ///     The ISO 3166-1 alpha-2 countries it is intended for. ⚠ Declared by the tenant, confirmed
    ///     by the carrier, and never inferred: an alphanumeric sender id is legal in Germany and
    ///     rejected in the United States, and the platform is not the party that knows.
    /// </param>
    Task<Result<SenderIdentity>> RegisterAsync(ChannelKind channel, string value, ImmutableArray<string> countries);

    /// <summary>Records that the registration went to the carrier.</summary>
    /// <param name="carrierReference">Their reference — a 10DLC campaign id, a Meta WABA id.</param>
    Task<Result<SenderIdentity>> MarkSubmittedAsync(string carrierReference);

    /// <summary>Records what the carrier decided.</summary>
    /// <param name="status">
    ///     Their answer. ⚠ Only <see cref="SenderRegistrationStatus.Approved" />,
    ///     <see cref="SenderRegistrationStatus.Rejected" /> and
    ///     <see cref="SenderRegistrationStatus.Revoked" /> are decisions; anything else is refused,
    ///     because a caller that could write <see cref="SenderRegistrationStatus.Approved" /> by
    ///     writing <see cref="SenderRegistrationStatus.Draft" /> would be approving senders.
    /// </param>
    /// <param name="countries">Which countries the carrier cleared, which may be fewer than were asked for.</param>
    /// <param name="note">Their reason, verbatim. Required for a rejection or a revocation.</param>
    Task<Result<SenderIdentity>> RecordDecisionAsync(
        SenderRegistrationStatus status,
        ImmutableArray<string> countries,
        string note
    );

    /// <summary>The sender and its registration status.</summary>
    Task<Result<SenderIdentity>> GetAsync();

    /// <summary>
    ///     Whether a send may use this sender right now.
    /// </summary>
    /// <param name="channel">The channel the send is on.</param>
    /// <returns>
    ///     <para>
    ///         <see cref="ErrorCode.PolicyViolation" /> for anything but
    ///         <see cref="SenderRegistrationStatus.Approved" />, with a message naming the status and
    ///         whose obligation it is to move it on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An empty <see cref="SenderIdentity.Countries" /> means "no country is cleared",
    ///         not "every country is".</b> The other reading is the one that gets a tenant an SMS
    ///         into a jurisdiction their sender id is illegal in — and it is the reading a reasonable
    ///         person reaches for, which is why it is written down here.
    ///     </para>
    /// </returns>
    Task<Result<SenderIdentity>> CheckAsync(ChannelKind channel);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
