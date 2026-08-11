using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     One named template and every version of it — docs/plan/17 § The parts that are actually the
///     work: <i>"Named, versioned, localised, with typed parameters. Because WhatsApp requires
///     pre-approved templates, and because the alternative is string concatenation in twenty
///     providers."</i>
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <b>Durable</b> · <b>Key</b> <c>res/{templateId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Durable, and the WhatsApp sentence is the argument.</b> A template version that a
///         carrier has approved is not a copy of something we hold elsewhere — the approval is
///         attached to that exact body, and losing our copy means resubmitting and waiting days
///         while business-initiated WhatsApp sending is down. docs/plan/05 § Choosing a tier's "can
///         this be rebuilt" answers no, and the recovery is a queue at Meta rather than a warm-up.
///     </para>
///     <para>
///         ⚠ <b>Versions are append-only and a body is never edited in place.</b> The carrier
///         approved a body; changing it silently invalidates the approval and sends start failing on
///         a template nobody touched today. <see cref="AddVersionAsync" /> is the only way content
///         enters, and there is deliberately no method that reaches an existing version's body — the
///         same structural argument <c>IUsageLedgerGrain</c> makes about corrections.
///     </para>
///     <para>
///         <b>One grain per template rather than one per service</b>, because a template carries
///         every locale of every version and a busy tenant has dozens. Per-service would make one
///         durable read on the send path carry all of them.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.IMessageTemplateGrain")]
public interface IMessageTemplateGrain : IGrainWithStringKey {
    /// <summary>Creates the template, or reports the one already there.</summary>
    /// <param name="serviceId">The service it belongs to.</param>
    /// <param name="name">Its name within the service, which is what a send references.</param>
    /// <param name="channel">Which channel it is written for.</param>
    Task<Result<MessageTemplateVersion>> CreateAsync(Guid serviceId, string name, ChannelKind channel);

    /// <summary>Adds a version. The only way a body enters this grain.</summary>
    /// <param name="parameters">
    ///     What the body expects. ⚠ A parameter marked required is one whose absence fails the render
    ///     before dispatch — see <see cref="TemplateRenderer" />.
    /// </param>
    /// <param name="bodies">
    ///     The body per locale, at least one. ⚠ Refused empty: a version with no body is a template
    ///     that renders to nothing, and the failure would appear at a carrier rather than here.
    /// </param>
    /// <returns>
    ///     The version as stored, numbered by the grain.
    ///     <para>
    ///         ⚠ It starts at <see cref="SenderRegistrationStatus.Draft" /> and is not sendable on
    ///         <see cref="ChannelKind.WhatsApp" /> until a carrier decision says otherwise — the
    ///         broker rule again. On channels that do not pre-approve, approval is not consulted.
    ///     </para>
    /// </returns>
    Task<Result<MessageTemplateVersion>> AddVersionAsync(
        ImmutableArray<TemplateParameter> parameters,
        ImmutableArray<LocalizedBody> bodies
    );

    /// <summary>Records the carrier's decision on one version's pre-approval.</summary>
    /// <param name="version">Which version.</param>
    /// <param name="status">Their answer. Same rule as <see cref="ISenderIdentityGrain.RecordDecisionAsync" />.</param>
    /// <param name="providerTemplateName">
    ///     The carrier's own name for it, which is what a template-by-reference send quotes. ⚠
    ///     Required for an approval: an approved WhatsApp template with no carrier name cannot be
    ///     sent, and the send would fail at Meta with a message nobody can act on.
    /// </param>
    Task<Result<MessageTemplateVersion>> RecordApprovalAsync(
        int version,
        SenderRegistrationStatus status,
        string providerTemplateName
    );

    /// <summary>One version, by number.</summary>
    /// <param name="version">
    ///     The number, or <c>0</c> for the one a send should use. ⚠ <c>0</c> resolves to the newest
    ///     <b>approved</b> version on a channel that pre-approves and the newest version otherwise —
    ///     so adding a draft to a WhatsApp template cannot take production sending down.
    /// </param>
    Task<Result<MessageTemplateVersion>> GetVersionAsync(int version);

    /// <summary>Every version, oldest first.</summary>
    Task<Result<ImmutableArray<MessageTemplateVersion>>> ListVersionsAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     Maps a carrier's message id back to ours, so a delivery receipt can find its message grain.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Hot, TTL'd · <b>Key</b>
///         <c>res/{derived(serviceId, providerMessageId):N}</c>, tenant-qualified.
///     </para>
///     <para>
///         <b>Why an index at all.</b> A webhook carries the carrier's id and nothing of ours. The
///         message grain is keyed by the caller's idempotency key, which the carrier has never seen,
///         so something has to hold the one mapping. It is derived rather than looked up for the same
///         reason the message key is: a receipt finds its entry in one grain call with no scan.
///     </para>
///     <para>
///         ⚠ <b>Hot and TTL'd to exactly the same horizon as <see cref="IMessageGrain.Retention" />,
///         and it must not outlive it.</b> An index entry pointing at an expired message grain would
///         resurrect an empty activation on every late webhook.
///     </para>
///     <para>
///         ⚠ <b>An unknown id activates an empty grain, which is the cost of this design and is
///         bounded deliberately.</b> Webhooks arrive late, twice, and for messages we have forgotten
///         (docs/plan/17 § The parts that are actually the work), so an empty activation on an
///         unrecognised id is the expected path rather than an error. It is also, in principle, an
///         activation-per-request amplifier — which is why the webhook endpoint is per service and
///         authenticated, and why <see cref="LookupAsync" /> deactivates an activation it found
///         nothing in rather than leaving it in the collection cycle.
///     </para>
/// </remarks>
[Alias("CyberCloud.Communication.IProviderMessageIndexGrain")]
public interface IProviderMessageIndexGrain : IGrainWithStringKey {
    /// <summary>Points a carrier id at the idempotency key that addresses the message grain.</summary>
    /// <param name="idempotencyKey">The caller's key, which is half the message grain's key.</param>
    /// <param name="expiresAt">When the entry stops being valid. Match <see cref="IMessageGrain.Retention" />.</param>
    Task<Result> BindAsync(string idempotencyKey, DateTimeOffset expiresAt);

    /// <summary>The idempotency key this carrier id belongs to.</summary>
    /// <returns>
    ///     ⚠ <see cref="ErrorCode.ResourceNotFound" /> for an id nothing bound, and the caller's job
    ///     is to drop the receipt rather than to alert. See this type's remarks.
    /// </returns>
    Task<Result<string>> LookupAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
