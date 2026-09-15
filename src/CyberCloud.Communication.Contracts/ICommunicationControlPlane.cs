using System.Collections.Immutable;

namespace CyberCloud.Communication.Contracts;

/// <summary>
///     What the tenant-facing provider calls to shape a service — its channels, its templates and
///     its suppression list. The control plane; <see cref="IMessageSender" /> is the data plane.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the seam <c>CyberCloud.Providers.Communication</c> converges onto.</b> Every
///         one of its resource types — <c>services</c>, <c>services/channels</c>,
///         <c>services/templates</c>, <c>services/suppressions</c> — is desired state a reconciler
///         pushes into one of this module's grains and reads back, and the four reconcilers plus the
///         service's action handlers are the only callers.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An interface rather than grain references, for exactly the reason
///             <see cref="IMessageSender" /> gives.
///         </b> A reconciler is a plain singleton the reconcile driver resolves from the container;
///         it is not a grain, so <c>Orleans.Multitenant</c>'s call filter never sees it and every
///         <c>GetGrain</c> would have to be qualified with <c>ForTenant</c> by hand — CC1006. Handing
///         the provider this interface means the qualification is written once, in
///         <c>GrainCommunicationControlPlane</c>, and the provider's implementation assembly takes
///         no Orleans hosting package at all — which docs/plan/03 § Providers and every provider's
///         <c>.csproj</c> before this one say a provider should not.
///     </para>
///     <para>
///         ⚠ <b>Every mutating call here is idempotent, because a reconciler is its caller.</b>
///         docs/plan/08 § The reconcile loop's first clause is that a pass runs again after the silo
///         dies between the write and the bookkeeping. Ensuring a service that exists, removing a
///         channel that is gone, unregistering a name that was never registered — each succeeds and
///         changes nothing.
///     </para>
/// </remarks>
public interface ICommunicationControlPlane {
    // ── Services ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a service, or reports the one already there, and sets its default locale.</summary>
    /// <param name="tenantId">The owning tenant. Qualifies every grain call.</param>
    /// <param name="serviceId">The service, as <see cref="CommunicationGrainKeys.ResourceIdFor" /> derives it.</param>
    /// <param name="name">The resource's name within its group.</param>
    /// <param name="defaultLocale">
    ///     The locale a send falls back to when it names none, or empty for the renderer's own
    ///     chain. See <see cref="CommunicationService.DefaultLocale" />.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<CommunicationService>> EnsureServiceAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        string defaultLocale,
        CancellationToken cancellationToken = default
    );

    /// <summary>Everything about a service — channels as handles, never as values.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> for a service never created or since retired.</returns>
    Task<Result<CommunicationService>> DescribeServiceAsync(
        Guid tenantId,
        Guid serviceId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Takes a service out of use. See <see cref="ICommunicationServiceGrain.RetireAsync" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RetireServiceAsync(Guid tenantId, Guid serviceId, CancellationToken cancellationToken = default);

    // ── Channels ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Configures one channel, replacing whatever was there.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="configuration">
    ///     The channel. ⚠ <see cref="ChannelConfiguration.OwnerResourceId" /> is what lets two
    ///     resources declaring one <see cref="ChannelKind" /> be told apart; the provider refuses
    ///     the second by name rather than letting them alternate.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the service is not there yet.</returns>
    Task<Result<CommunicationService>> ConfigureChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelConfiguration configuration,
        CancellationToken cancellationToken = default
    );

    /// <summary>One channel's configuration, as the send path reads it.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when the channel is not configured.</returns>
    Task<Result<ChannelConfiguration>> GetChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes a channel. Succeeds when it was already gone.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RemoveChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    );

    // ── Templates ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a template and registers its name on the service, or reports the one already
    ///     there.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service the template belongs to.</param>
    /// <param name="templateId">The template, as <see cref="CommunicationGrainKeys.ResourceIdFor" /> derives it.</param>
    /// <param name="name">The name a send references.</param>
    /// <param name="channel">The channel it is written for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     The newest version, or the empty one for a template with no version yet.
    ///     <see cref="ErrorCode.ResourceAlreadyExists" /> when the name belongs to another template.
    /// </returns>
    Task<Result<MessageTemplateVersion>> EnsureTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        Guid templateId,
        string name,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    );

    /// <summary>Appends a version. See <see cref="IMessageTemplateGrain.AddVersionAsync" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="templateId">The template.</param>
    /// <param name="parameters">What the body expects.</param>
    /// <param name="bodies">The body per locale, at least one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<MessageTemplateVersion>> AddTemplateVersionAsync(
        Guid tenantId,
        Guid templateId,
        ImmutableArray<TemplateParameter> parameters,
        ImmutableArray<LocalizedBody> bodies,
        CancellationToken cancellationToken = default
    );

    /// <summary>The template a name points at on a service.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="name">The name a send references.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> when no template has that name.</returns>
    Task<Result<Guid>> ResolveTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every version of a template, oldest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="templateId">The template.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see cref="ErrorCode.ResourceNotFound" /> for a template never created.</returns>
    Task<Result<ImmutableArray<MessageTemplateVersion>>> ListTemplateVersionsAsync(
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Forgets a template's name on its service. The template's versions stay — see
    ///     <see cref="ICommunicationServiceGrain.UnregisterTemplateAsync" />.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="name">The name.</param>
    /// <param name="templateId">The template the name is expected to point at.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> UnregisterTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        Guid templateId,
        CancellationToken cancellationToken = default
    );

    // ── Suppression ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Adds an address to a service's list, or updates the reason on one already there.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">The channel the suppression applies to.</param>
    /// <param name="destination">The address, in any spelling.</param>
    /// <param name="reason">Why. See <see cref="SuppressionReason" />.</param>
    /// <param name="note">The words the support case will read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<SuppressionEntry>> SuppressAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        SuppressionReason reason,
        string note,
        CancellationToken cancellationToken = default
    );

    /// <summary>Whether an address is on a service's list.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="destination">The address, in any spelling.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<SuppressionCheck>> CheckSuppressionAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Takes an address off a service's list. ⚠ Refused with
    ///     <see cref="ErrorCode.PolicyViolation" /> for a complaint or an opt-out — see
    ///     <see cref="ISuppressionListGrain.ReleaseAsync" />.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="destination">The address.</param>
    /// <param name="reason">Who is asking and why. Recorded, and required.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> ReleaseSuppressionAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        string reason,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every entry on a service's list, in the order they were added.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="serviceId">The service.</param>
    /// <param name="channel">One channel, or <see cref="ChannelKind.Unknown" /> for all of them.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ImmutableArray<SuppressionEntry>>> ListSuppressionsAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    );
}
