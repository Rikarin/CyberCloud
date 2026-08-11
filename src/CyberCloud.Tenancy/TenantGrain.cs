using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="ITenantGrain" /> — Entity, Durable, key <c>tenant/{tenantId:N}</c>.
/// </summary>
public sealed class TenantGrain(
    [PersistentState("tenant", StorageTiers.Durable)] IPersistentState<TenantState> state,
    IClock clock)
    : Grain, ITenantGrain
{
    /// <summary>
    ///     The transitions docs/plan/06 § Tenant lifecycle allows, as a table rather than as a chain
    ///     of <c>if</c>s.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="TenantStatus.Purged" /> maps to the empty set and that is the point: it is
    ///     terminal, because the directory entry is "tombstoned forever (never reuse an id)".
    ///     Restoring a purged tenant would have to mint a new id, which makes it a new tenant.
    /// </remarks>
    static readonly Dictionary<TenantStatus, TenantStatus[]> Allowed = new()
    {
        [TenantStatus.Provisioning] = [TenantStatus.Active, TenantStatus.Disabled, TenantStatus.PendingDeletion],
        [TenantStatus.Active] = [TenantStatus.Warned, TenantStatus.Suspended, TenantStatus.Disabled, TenantStatus.PendingDeletion],
        [TenantStatus.Warned] = [TenantStatus.Active, TenantStatus.Suspended, TenantStatus.Disabled, TenantStatus.PendingDeletion],
        [TenantStatus.Suspended] = [TenantStatus.Active, TenantStatus.Warned, TenantStatus.Disabled, TenantStatus.PendingDeletion],
        [TenantStatus.Disabled] = [TenantStatus.Active, TenantStatus.PendingDeletion],
        [TenantStatus.PendingDeletion] = [TenantStatus.Active, TenantStatus.Purged],
        [TenantStatus.Purged] = [],
    };

    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The two halves of the physical key must agree. They can only disagree if something built
        // a key by hand — the grain would then be storing tenant A's record under tenant B's
        // qualification, which is the one thing ADR-002's key discipline exists to make impossible.
        var qualification = TenancyGrainKeys.TenantOf(this);
        var fromKey = TenancyGrainKeys.Decode(this, GrainKeyKind.Tenant).Id;

        if (qualification != fromKey)
        {
            throw new InvalidOperationException(
                $"ITenantGrain was activated as tenant {qualification:D} with the key "
                + $"'{GrainKeys.Tenant(fromKey)}'. The tenant qualification and the key disagree, so "
                + "one of them is forged. Build the key with GrainKeys.Tenant(id) and reach it with "
                + "IGrainFactory.ForTenant(id).");
        }

        tenantId = qualification;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<TenantDescriptor>> CreateAsync(string slug, string displayName, string homeRegion)
    {
        var name = ResourceNaming.Validate(slug, "tenant slug");
        if (name.TryGetError(out var invalid))
        {
            return Result<TenantDescriptor>.Failure(invalid);
        }

        if (string.IsNullOrWhiteSpace(homeRegion))
        {
            return Result<TenantDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                "A tenant is homed to exactly one region at creation (docs/plan/04 § The clusters, "
                + "plural), so homeRegion is required.");
        }

        if (state.State.Descriptor is { } existing)
        {
            // Idempotent re-drive — docs/plan/06 § Tenant lifecycle, "every step is idempotent and
            // re-drivable". Same arguments: the existing record. Different ones: a conflict, because
            // silently accepting them would turn a retry into a rename.
            return string.Equals(existing.Slug, slug, StringComparison.Ordinal)
                && string.Equals(existing.HomeRegion, homeRegion, StringComparison.Ordinal)
                    ? Result<TenantDescriptor>.Success(existing)
                    : Result<TenantDescriptor>.Failure(
                        ErrorCode.Conflict,
                        $"Tenant {tenantId:D} already exists as slug '{existing.Slug}' in "
                        + $"'{existing.HomeRegion}'. A re-drive of tenant creation must carry the "
                        + "same slug and region; this one asks for "
                        + $"'{slug}' in '{homeRegion}'.");
        }

        var now = clock.UtcNow;
        state.State.Descriptor = new TenantDescriptor
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = displayName,
            HomeRegion = homeRegion,
            Status = TenantStatus.Provisioning,
            CreatedAt = now,
            ModifiedAt = now,
            Version = 1,
        };

        await state.WriteStateAsync();
        return Result<TenantDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public Task<Result<TenantDescriptor>> GetAsync() =>
        Task.FromResult(state.State.Descriptor is { } descriptor
            ? Result<TenantDescriptor>.Success(descriptor)
            : Result<TenantDescriptor>.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.TenantNotFound, "Tenant", tenantId.ToString("D"))));

    /// <inheritdoc />
    public async Task<Result<TenantDescriptor>> SetStatusAsync(TenantStatus status, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result<TenantDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                "A status change needs a reason: it is what the audit log and the tenant "
                + "notification are written from.");
        }

        if (state.State.Descriptor is not { } descriptor)
        {
            return Result<TenantDescriptor>.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.TenantNotFound, "Tenant", tenantId.ToString("D")));
        }

        if (descriptor.Status == status)
        {
            return Result<TenantDescriptor>.Success(descriptor);
        }

        if (!Allowed.TryGetValue(descriptor.Status, out var next)
            || !next.Contains(status))
        {
            return Result<TenantDescriptor>.Failure(
                ErrorCode.Conflict,
                $"A tenant cannot move from {descriptor.Status} to {status}. The transitions from "
                + $"{descriptor.Status} are "
                + (next is null or [] ? "none — it is terminal" : string.Join(", ", next))
                + ". See docs/plan/06 § Tenant lifecycle.");
        }

        state.State.Descriptor = descriptor with
        {
            Status = status,
            ModifiedAt = clock.UtcNow,
            Version = descriptor.Version + 1,
        };
        state.State.LastStatusReason = reason;

        await state.WriteStateAsync();
        return Result<TenantDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public Task<Result<bool>> AreControlPlaneWritesAllowedAsync() =>
        Task.FromResult(state.State.Descriptor is { } descriptor
            ? Result<bool>.Success(descriptor.Status is TenantStatus.Active or TenantStatus.Warned)
            : Result<bool>.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.TenantNotFound, "Tenant", tenantId.ToString("D"))));

    /// <inheritdoc />
    public async Task<Result> AddSubscriptionAsync(Guid subscriptionId)
    {
        if (state.State.Descriptor is null)
        {
            return Result.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.TenantNotFound, "Tenant", tenantId.ToString("D")));
        }

        if (state.State.Subscriptions.Contains(subscriptionId))
        {
            return Result.Success;
        }

        state.State.Subscriptions.Add(subscriptionId);
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Guid>>> ListSubscriptionsAsync() =>
        Task.FromResult(Result<IReadOnlyList<Guid>>.Success([.. state.State.Subscriptions]));

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
