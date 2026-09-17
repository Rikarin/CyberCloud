using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using System.Globalization;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IManagementGroupGrain" /> — Entity, Durable, key <c>mg/{name}</c>
///     (docs/plan/06 § Grain keys, issue #39).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Durable, on the argument <c>SubscriptionGrain</c> makes for itself.</b> A group is
///         what role assignments and, one day, policies are scoped to; losing one to a Redis
///         eviction would detach every grant made at it from the subscriptions it covered without
///         any tuple changing — the tuples would still name a group whose record was gone.
///         docs/plan/05 § The two tiers puts "anything a role is assigned to" on the durable tier.
///     </para>
///     <para>
///         ⚠ <b>The tree is redundant on purpose.</b> A parent lists its children and a child names
///         its parent; a group lists its subscriptions and a subscription names its group. Two
///         grains, two writes, no transaction — so <c>ScopeManagerService</c> writes the leaf's own
///         record last and treats the listing as what a re-driven <c>PUT</c> repairs, exactly as it
///         does for <c>ITenantGrain.AddSubscriptionAsync</c>. A listing that names a member whose own
///         record disagrees is filtered by the read every page element goes through.
///     </para>
/// </remarks>
public sealed class ManagementGroupGrain(
    [PersistentState("managementGroup", StorageTiers.Durable)]
    IPersistentState<ManagementGroupState> state,
    IClock clock
)
    : Grain, IManagementGroupGrain {
    Guid tenantId;
    string name = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = TenancyGrainKeys.TenantOf(this);
        name = TenancyGrainKeys.Decode(this, GrainKeyKind.ManagementGroup).Name;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ManagementGroupDescriptor>> CreateAsync(string displayName, string parent, int parentDepth) {
        if (parent.Length > 0) {
            var parentName = ResourceNaming.Validate(parent, "management group name");
            if (parentName.TryGetError(out var invalidParent)) {
                return Result<ManagementGroupDescriptor>.Failure(invalidParent);
            }

            if (string.Equals(parent, name, StringComparison.Ordinal)) {
                return Result<ManagementGroupDescriptor>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"Management group '{name}' cannot be its own parent. A group hangs off another "
                    + "group or off the tenant — docs/plan/06 § The hierarchy."
                );
            }
        }

        if (parentDepth < 0) {
            return Result<ManagementGroupDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A parent depth of {parentDepth.ToString(CultureInfo.InvariantCulture)} is not a "
                + "position in the tree. The tenant is depth 0 and every group below it is one more."
            );
        }

        var depth = parentDepth + 1;

        if (depth > IManagementGroupGrain.MaxDepth) {
            return Result<ManagementGroupDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                $"Management group '{name}' would sit at depth "
                + depth.ToString(CultureInfo.InvariantCulture)
                + " and the tree is capped at "
                + IManagementGroupGrain.MaxDepth.ToString(CultureInfo.InvariantCulture)
                + " levels. Every level is a hop in every permission check beneath it, and "
                + "docs/plan/07 § Check caps the walk at twelve — see IManagementGroupGrain."
            );
        }

        var shown = displayName.Length == 0 ? name : displayName;

        if (state.State.Descriptor is { } existing) {
            // Idempotent re-drive — same tree position, the existing record. A different parent is
            // the move IManagementGroupGrain's remarks refuse, and silently accepting it would turn
            // a retry into one.
            return string.Equals(existing.Parent, parent, StringComparison.Ordinal)
                ? Result<ManagementGroupDescriptor>.Success(Snapshot(existing))
                : Result<ManagementGroupDescriptor>.Failure(
                    ErrorCode.Conflict,
                    $"Management group '{name}' already exists under "
                    + (existing.Parent.Length == 0 ? "the tenant" : $"'{existing.Parent}'")
                    + " and this request names "
                    + (parent.Length == 0 ? "the tenant" : $"'{parent}'")
                    + " as its parent. A group's parent is set at creation and a move is not built — "
                    + "IManagementGroupGrain says what a safe move would need, and docs/plan/06 "
                    + "§ The hierarchy records it as owed. Delete the group and re-create it, or "
                    + "create a new one."
                );
        }

        state.State.Descriptor = new() {
            Name = name,
            TenantId = tenantId,
            DisplayName = shown,
            Parent = parent,
            Depth = depth,
            CreatedAt = clock.UtcNow,
            Version = 1
        };

        await state.WriteStateAsync();
        return Result<ManagementGroupDescriptor>.Success(Snapshot(state.State.Descriptor));
    }

    /// <inheritdoc />
    public Task<Result<ManagementGroupDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.Descriptor is { } descriptor
                ? Result<ManagementGroupDescriptor>.Success(Snapshot(descriptor))
                : Result<ManagementGroupDescriptor>.Failure(NotCreated())
        );

    /// <inheritdoc />
    public async Task<Result> AddChildAsync(string child) {
        if (state.State.Descriptor is null) {
            return Result.Failure(NotCreated());
        }

        var validated = ResourceNaming.Validate(child, "management group name");
        if (validated.TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        if (state.State.Children.Contains(child, StringComparer.Ordinal)) {
            return Result.Success;
        }

        state.State.Children.Add(child);
        state.State.Children.Sort(StringComparer.Ordinal);
        await BumpAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> RemoveChildAsync(string child) {
        if (!state.State.Children.Remove(child)) {
            return Result.Success;
        }

        await BumpAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> AddSubscriptionAsync(Guid subscriptionId) {
        if (state.State.Descriptor is null) {
            return Result.Failure(NotCreated());
        }

        if (state.State.Subscriptions.Contains(subscriptionId)) {
            return Result.Success;
        }

        state.State.Subscriptions.Add(subscriptionId);
        state.State.Subscriptions.Sort();
        await BumpAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> RemoveSubscriptionAsync(Guid subscriptionId) {
        if (!state.State.Subscriptions.Remove(subscriptionId)) {
            return Result.Success;
        }

        await BumpAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<string>> DeleteAsync() {
        if (state.State.Descriptor is not { } descriptor) {
            // Absence is the goal, so a group that is already gone has reached it — and the parent
            // it had is what the caller sweeps with. ManagementGroupState.LastParent says why.
            return Result<string>.Success(state.State.LastParent);
        }

        // ⚠ THE CHECK AND THE DELETE ARE ONE TURN — the same argument as
        // ResourceGroupGrain.BeginGroupDeleteAsync. A caller that listed the members and then
        // deleted would leave a window in which AddChildAsync or AddSubscriptionAsync could run
        // between the two, and the record would be gone with a member still naming it.
        if (state.State.Children.Count > 0 || state.State.Subscriptions.Count > 0) {
            return Result<string>.Failure(
                ErrorCode.Conflict,
                $"Management group '{name}' still holds "
                + state.State.Children.Count.ToString(CultureInfo.InvariantCulture)
                + " child group(s) and "
                + state.State.Subscriptions.Count.ToString(CultureInfo.InvariantCulture)
                + " subscription(s), so it cannot be deleted. Move or delete them first: a group "
                + "delete does not cascade, for the reason a resource group's does not "
                + "(IScopeManager.DeleteAsync). "
                + string.Join(
                    ", ",
                    state.State.Children.Select(x => "group " + x)
                        .Concat(
                            state.State.Subscriptions.Select(x =>
                                "subscription " + x.ToString("D", CultureInfo.InvariantCulture)
                            )
                        )
                        .Take(5)
                )
            );
        }

        state.State.Descriptor = null;
        state.State.LastParent = descriptor.Parent;
        await state.WriteStateAsync();
        return Result<string>.Success(descriptor.Parent);
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    async Task BumpAsync() {
        if (state.State.Descriptor is { } descriptor) {
            state.State.Descriptor = descriptor with { Version = descriptor.Version + 1 };
        }

        await state.WriteStateAsync();
    }

    ManagementGroupDescriptor Snapshot(ManagementGroupDescriptor descriptor) =>
        descriptor with { Children = [.. state.State.Children], Subscriptions = [.. state.State.Subscriptions] };

    Error NotCreated() => TenancyGrainKeys.NotCreated(ErrorCode.ResourceNotFound, "Management group", name);
}
