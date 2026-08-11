using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="ISubscriptionGrain" /> — Entity, Durable, key <c>sub/{subscriptionId:N}</c>.
/// </summary>
public sealed class SubscriptionGrain(
    [PersistentState("subscription", StorageTiers.Durable)] IPersistentState<SubscriptionState> state,
    IClock clock
)
    : Grain, ISubscriptionGrain {
    Guid subscriptionId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = TenancyGrainKeys.TenantOf(this);
        subscriptionId = TenancyGrainKeys.Decode(this, GrainKeyKind.Subscription).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<SubscriptionDescriptor>> CreateAsync(string displayName) {
        if (state.State.Descriptor is { } existing) {
            return Result<SubscriptionDescriptor>.Success(Snapshot(existing));
        }

        state.State.Descriptor = new() {
            Id = subscriptionId,
            TenantId = tenantId,
            DisplayName = displayName,
            State = ProvisioningState.Succeeded,
            CreatedAt = clock.UtcNow,
            Version = 1
        };

        await state.WriteStateAsync();
        return Result<SubscriptionDescriptor>.Success(Snapshot(state.State.Descriptor));
    }

    /// <inheritdoc />
    public Task<Result<SubscriptionDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.Descriptor is { } descriptor
                ? Result<SubscriptionDescriptor>.Success(Snapshot(descriptor))
                : Result<SubscriptionDescriptor>.Failure(
                    TenancyGrainKeys.NotCreated(
                        ErrorCode.SubscriptionNotFound,
                        "Subscription",
                        subscriptionId.ToString("D")
                    )
                )
        );

    /// <inheritdoc />
    public async Task<Result<ResourceGroupDescriptor>> CreateResourceGroupAsync(string name, string region) {
        if (state.State.Descriptor is null) {
            return Result<ResourceGroupDescriptor>.Failure(
                TenancyGrainKeys.NotCreated(
                    ErrorCode.SubscriptionNotFound,
                    "Subscription",
                    subscriptionId.ToString("D")
                )
            );
        }

        var validated = ResourceNaming.Validate(name, "resource group name");
        if (validated.TryGetError(out var invalid)) {
            return Result<ResourceGroupDescriptor>.Failure(invalid);
        }

        // ⚠ The group grain is created FIRST and the listing entry is added after it succeeds. The
        // other order would list a group that does not exist, and a caller that then GETs it gets a
        // 404 for something the list just told them about.
        var group = GrainFactory.ForTenant(tenantId.ToString("D"))
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscriptionId, name));

        var created = await group.CreateAsync(tenantId, region);
        if (created.TryGetError(out var failure)) {
            return Result<ResourceGroupDescriptor>.Failure(failure);
        }

        if (!state.State.ResourceGroups.Contains(name, StringComparer.Ordinal)) {
            state.State.ResourceGroups.Add(name);
            state.State.ResourceGroups.Sort(StringComparer.Ordinal);

            state.State.Descriptor = state.State.Descriptor with { Version = state.State.Descriptor.Version + 1 };
            await state.WriteStateAsync();
        }

        return created;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<string>>> ListResourceGroupsAsync() =>
        Task.FromResult(Result<IReadOnlyList<string>>.Success([.. state.State.ResourceGroups]));

    /// <inheritdoc />
    public async Task<Result> RemoveResourceGroupAsync(string name) {
        if (!state.State.ResourceGroups.Remove(name)) {
            return Result.Success;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    SubscriptionDescriptor Snapshot(SubscriptionDescriptor descriptor) =>
        descriptor with { ResourceGroups = [.. state.State.ResourceGroups] };
}
