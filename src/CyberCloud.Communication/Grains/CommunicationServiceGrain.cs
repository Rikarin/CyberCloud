using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="ICommunicationServiceGrain" /> — Entity, Durable, key <c>res/{serviceId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Nothing here holds a credential value, and CC1005 is what keeps it that way.</b>
///     <see cref="CarrierCredentials" /> carries three <see cref="CarrierSecretRef" /> handles;
///     resolution happens at the data plane, in whatever implements
///     <see cref="IChannelProvider" />. docs/plan/00 § Non-negotiables.
/// </remarks>
public sealed class CommunicationServiceGrain(
    [PersistentState("communication-service", StorageTiers.Durable)]
    IPersistentState<CommunicationServiceState> state,
    IClock clock
)
    : Grain, ICommunicationServiceGrain {
    Guid owningTenant;
    Guid serviceId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        owningTenant = CommunicationGrainDecoder.TenantOf(this);
        serviceId = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<CommunicationService>> CreateAsync(Guid tenantId, string name) {
        if (tenantId != Guid.Empty && tenantId != owningTenant) {
            return Result<CommunicationService>.Failure(
                ErrorCode.AuthorizationFailed,
                $"This grain is qualified for tenant {owningTenant:D} and the create names "
                + $"{tenantId:D}. A service created under the wrong tenant would send on "
                + "somebody else's carrier account."
            );
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return Result<CommunicationService>.Failure(
                ErrorCode.InvalidResourceName,
                "A communication service needs a name — docs/plan/06 § Identifiers."
            );
        }

        if (state.State.Created) {
            return Result<CommunicationService>.Success(Snapshot());
        }

        state.State.Created = true;
        state.State.TenantId = owningTenant;
        state.State.Name = name;
        state.State.CreatedAt = clock.UtcNow;
        await state.WriteStateAsync();

        return Result<CommunicationService>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<CommunicationService>> DescribeAsync() =>
        Task.FromResult(
            state.State.Created
                ? Result<CommunicationService>.Success(Snapshot())
                : NotFound<CommunicationService>()
        );

    /// <inheritdoc />
    public async Task<Result<CommunicationService>> ConfigureChannelAsync(ChannelConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!state.State.Created) {
            return NotFound<CommunicationService>();
        }

        if (configuration.Channel == ChannelKind.Unknown) {
            return Result<CommunicationService>.Failure(
                ErrorCode.InvalidRequestBody,
                "A channel configuration names a channel. ChannelKind.Unknown is the zero value a "
                + "default-constructed wire type carries."
            );
        }

        if (configuration.Credentials.Mode == CredentialMode.Unknown) {
            return Result<CommunicationService>.Failure(
                ErrorCode.InvalidRequestBody,
                "A channel configuration says whose carrier account pays — CredentialMode."
                + "PlatformAccount is marked up with no setup, CredentialMode.TenantAccount is BYO "
                + "and cheaper. docs/plan/17 § The channel abstraction offers both from day one."
            );
        }

        // ⚠ A BYO channel with empty handles would silently fall back to the platform's account and
        // bill the tenant at the marked-up rate they chose BYO to avoid — a wrong invoice that looks
        // like a working system, which is the failure mode that survives a launch.
        if (configuration.Credentials.Mode == CredentialMode.TenantAccount
            && (configuration.Credentials.AccountRef.IsEmpty || configuration.Credentials.AuthRef.IsEmpty)) {
            return Result<CommunicationService>.Failure(
                ErrorCode.InvalidRequestBody,
                "CredentialMode.TenantAccount needs both AccountRef and AuthRef to point at vault "
                + "entries. Write the credential to the vault first and configure the handles — a "
                + "BYO channel with nothing to authenticate as would send on the platform's account "
                + "and bill at the marked-up rate."
            );
        }

        if (configuration.Limits.MaxSpendPerWindow < 0 || configuration.Limits.MaxMessagesPerWindow < 0) {
            return Result<CommunicationService>.Failure(
                ErrorCode.InvalidRequestBody,
                "A limit cannot be negative. Zero means nothing is allowed, which is the safe "
                + "default a service starts from — see ChannelLimits.None."
            );
        }

        var index = state.State.Channels.FindIndex(x => x.Channel == configuration.Channel);
        if (index >= 0) {
            state.State.Channels[index] = configuration;
        } else {
            state.State.Channels.Add(configuration);
        }

        await state.WriteStateAsync();

        return Result<CommunicationService>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<ChannelConfiguration>> GetChannelAsync(ChannelKind channel) {
        if (!state.State.Created) {
            return Task.FromResult(NotFound<ChannelConfiguration>());
        }

        var found = state.State.Channels.FirstOrDefault(x => x.Channel == channel);

        return Task.FromResult(
            found is null
                ? Result<ChannelConfiguration>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"Service {serviceId:D} has no {channel} channel configured. That is different "
                    + "from having one that is turned off: this tenant does not use the channel at "
                    + "all, so there is nothing to enable."
                )
                : Result<ChannelConfiguration>.Success(found)
        );
    }

    /// <inheritdoc />
    public async Task<Result> SetChannelEnabledAsync(ChannelKind channel, bool enabled) {
        var index = state.State.Channels.FindIndex(x => x.Channel == channel);
        if (index < 0) {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Service {serviceId:D} has no {channel} channel configured."
            );
        }

        state.State.Channels[index] = state.State.Channels[index] with { Enabled = enabled };
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<ChannelConfiguration>>> ListChannelsAsync() =>
        Task.FromResult(Result<ImmutableArray<ChannelConfiguration>>.Success([.. state.State.Channels]));

    /// <inheritdoc />
    public async Task<Result> RegisterTemplateAsync(string name, Guid templateId) {
        if (string.IsNullOrWhiteSpace(name)) {
            return Result.Failure(ErrorCode.InvalidResourceName, "A template needs a name.");
        }

        if (templateId == Guid.Empty) {
            return Result.Failure(ErrorCode.InvalidResourceId, "A template registration needs its resource id.");
        }

        if (state.State.Templates.TryGetValue(name, out var existing) && existing != templateId) {
            return Result.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"'{name}' already names template {existing:D} on this service. A second resource "
                + "claiming the name would make which template a send reaches depend on which "
                + "registration ran last."
            );
        }

        state.State.Templates[name] = templateId;
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveTemplateAsync(string name) =>
        Task.FromResult(
            state.State.Templates.TryGetValue(name ?? string.Empty, out var templateId)
                ? Result<Guid>.Success(templateId)
                : Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"Service {serviceId:D} has no template named '{name}'. Create it and register "
                    + "the name before sending against it — refused here rather than at the carrier."
                )
        );

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    CommunicationService Snapshot() =>
        new() {
            ServiceId = serviceId,
            TenantId = state.State.TenantId,
            Name = state.State.Name,
            Channels = [.. state.State.Channels],
            CreatedAt = state.State.CreatedAt
        };

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            $"There is no communication service {serviceId:D} in tenant {owningTenant:D}."
        );
}
