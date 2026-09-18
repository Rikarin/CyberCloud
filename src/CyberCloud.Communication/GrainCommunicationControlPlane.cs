using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Communication;

/// <summary>
///     The <see cref="ICommunicationControlPlane" /> that talks to grains. What the tenant-facing
///     provider's reconcilers and action handlers hold.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Every <c>GetGrain</c> here is qualified with <c>ForTenant</c>, and this class exists
///             so that is written once — CC1006.
///         </b> A reconciler and an action handler are plain singletons in a silo's or a gateway's
///         container, not grains, so the call filter never sees them. The same arrangement
///         <see cref="GrainMessageSender" /> has, for the same reason.
///     </para>
///     <para>
///         No caching and no retry. Each method is one or two grain calls, and the grain is where
///         the state and its invariants live; a memo here would be a second copy of a durable fact
///         that could go stale on a silo that stayed up.
///     </para>
/// </remarks>
public sealed class GrainCommunicationControlPlane(IGrainFactory grains) : ICommunicationControlPlane {
    /// <inheritdoc />
    public async Task<Result<CommunicationService>> EnsureServiceAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        string defaultLocale,
        CancellationToken cancellationToken = default
    ) {
        if (serviceId == Guid.Empty) {
            return Result<CommunicationService>.Failure(ErrorCode.InvalidResourceId, "A service needs an id.");
        }

        var service = Service(tenantId, serviceId);

        var created = await service.CreateAsync(tenantId, name);
        if (created.TryGetError(out var error)) {
            return Result<CommunicationService>.Failure(error);
        }

        return await service.SetDefaultLocaleAsync(defaultLocale ?? string.Empty);
    }

    /// <inheritdoc />
    public Task<Result<CommunicationService>> DescribeServiceAsync(
        Guid tenantId,
        Guid serviceId,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).DescribeAsync();

    /// <inheritdoc />
    public Task<Result> RetireServiceAsync(
        Guid tenantId,
        Guid serviceId,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).RetireAsync();

    /// <inheritdoc />
    public Task<Result<CommunicationService>> ConfigureChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelConfiguration configuration,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
        return Service(tenantId, serviceId).ConfigureChannelAsync(configuration);
    }

    /// <inheritdoc />
    public Task<Result<ChannelConfiguration>> GetChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).GetChannelAsync(channel);

    /// <inheritdoc />
    public Task<Result> RemoveChannelAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).RemoveChannelAsync(channel);

    /// <inheritdoc />
    public async Task<Result<MessageTemplateVersion>> EnsureTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        Guid templateId,
        string name,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ THE NAME IS CLAIMED BEFORE THE TEMPLATE IS CREATED, and the order is the safe one. A
        // registration that fails because another template holds the name leaves nothing behind;
        // the other order would leave a created template no send could ever reach, and a second
        // pass would find it "already there" and report converged over a name it does not own.
        var registered = await Service(tenantId, serviceId).RegisterTemplateAsync(name, templateId);
        if (registered.TryGetError(out var taken)) {
            return Result<MessageTemplateVersion>.Failure(taken);
        }

        return await Template(tenantId, templateId).CreateAsync(serviceId, name, channel);
    }

    /// <inheritdoc />
    public Task<Result<MessageTemplateVersion>> AddTemplateVersionAsync(
        Guid tenantId,
        Guid templateId,
        ImmutableArray<TemplateParameter> parameters,
        ImmutableArray<LocalizedBody> bodies,
        CancellationToken cancellationToken = default
    ) =>
        Template(tenantId, templateId).AddVersionAsync(parameters, bodies);

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).ResolveTemplateAsync(name);

    /// <inheritdoc />
    public Task<Result<ImmutableArray<MessageTemplateVersion>>> ListTemplateVersionsAsync(
        Guid tenantId,
        Guid templateId,
        CancellationToken cancellationToken = default
    ) =>
        Template(tenantId, templateId).ListVersionsAsync();

    /// <inheritdoc />
    public Task<Result> UnregisterTemplateAsync(
        Guid tenantId,
        Guid serviceId,
        string name,
        Guid templateId,
        CancellationToken cancellationToken = default
    ) =>
        Service(tenantId, serviceId).UnregisterTemplateAsync(name, templateId);

    /// <inheritdoc />
    public Task<Result<SuppressionEntry>> SuppressAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        SuppressionReason reason,
        string note,
        Guid ownerResourceId,
        CancellationToken cancellationToken = default
    ) =>
        Suppression(tenantId, serviceId).SuppressAsync(channel, destination, reason, note, ownerResourceId);

    /// <inheritdoc />
    public Task<Result<SuppressionCheck>> CheckSuppressionAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        CancellationToken cancellationToken = default
    ) =>
        Suppression(tenantId, serviceId).CheckAsync(channel, destination);

    /// <inheritdoc />
    public Task<Result> ReleaseSuppressionAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        string destination,
        string reason,
        CancellationToken cancellationToken = default
    ) =>
        Suppression(tenantId, serviceId).ReleaseAsync(channel, destination, reason);

    /// <inheritdoc />
    public Task<Result<ImmutableArray<SuppressionEntry>>> ListSuppressionsAsync(
        Guid tenantId,
        Guid serviceId,
        ChannelKind channel,
        CancellationToken cancellationToken = default
    ) =>
        Suppression(tenantId, serviceId).ListAsync(channel);

    ICommunicationServiceGrain Service(Guid tenantId, Guid serviceId) =>
        For(tenantId).GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(serviceId));

    ISuppressionListGrain Suppression(Guid tenantId, Guid serviceId) =>
        For(tenantId).GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(serviceId));

    IMessageTemplateGrain Template(Guid tenantId, Guid templateId) =>
        For(tenantId).GetGrain<IMessageTemplateGrain>(CommunicationGrainKeys.Template(templateId));

    /// <summary>
    ///     ⚠ <c>"D"</c> and <see cref="CultureInfo.InvariantCulture" />, matching every other
    ///     tenant-qualified call site in the tree. Two spellings of a tenant id are two tenants to
    ///     <c>Orleans.Multitenant</c>, which encodes the string it is given.
    /// </summary>
    TenantGrainFactory For(Guid tenantId) => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
}
