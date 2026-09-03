using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IResourceGroupGrain" /> — Entity, Durable, key
///     <c>sub/{subscriptionId:N}/rg/{name}</c>.
/// </summary>
public sealed class ResourceGroupGrain(
    [PersistentState("resourceGroup", StorageTiers.Durable)] IPersistentState<ResourceGroupState> state,
    IClock clock,
    ILogger<ResourceGroupGrain> logger
)
    : Grain, IResourceGroupGrain, IRemindable {
    /// <summary>The reaper reminder's name. One per group, and only while there is anything to reap.</summary>
    public const string OrphanReminderName = "reap-orphans";

    /// <summary>How often the reminder looks. Never more often than Orleans' one-minute floor.</summary>
    public static TimeSpan OrphanSweepPeriod { get; } = TimeSpan.FromMinutes(15);

    Guid subscriptionId;
    Guid tenantId;
    string name = string.Empty;

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = TenancyGrainKeys.TenantOf(this);

        var key = TenancyGrainKeys.Decode(this, GrainKeyKind.ResourceGroup);
        subscriptionId = key.Id;
        name = key.Name;

        // ⚠ Re-armed on activation rather than trusted to survive. An Orleans reminder IS durable,
        // so this is normally a no-op — but a group whose members were recorded before this grain
        // had a reaper at all has candidates and no reminder, and that group would otherwise keep
        // its phantoms until somebody created a resource in it.
        await ArmOrDisarmAsync();
    }

    /// <inheritdoc />
    public async Task<Result<ResourceGroupDescriptor>> CreateAsync(Guid tenantId, string region) {
        if (tenantId != this.tenantId) {
            return Result<ResourceGroupDescriptor>.Failure(
                ErrorCode.AuthorizationFailed,
                $"This resource group is qualified to tenant {this.tenantId:D} and the call claims "
                + $"tenant {tenantId:D}."
            );
        }

        if (state.State.Descriptor is { } existing) {
            return Result<ResourceGroupDescriptor>.Success(existing);
        }

        state.State.Descriptor = new() {
            Name = name,
            SubscriptionId = subscriptionId,
            TenantId = tenantId,
            Region = region,
            State = ProvisioningState.Succeeded,
            CreatedAt = clock.UtcNow,
            Version = 1
        };

        await state.WriteStateAsync();
        return Result<ResourceGroupDescriptor>.Success(state.State.Descriptor);
    }

    /// <inheritdoc />
    public Task<Result<ResourceGroupDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.Descriptor is { } descriptor
                ? Result<ResourceGroupDescriptor>.Success(descriptor)
                : Result<ResourceGroupDescriptor>.Failure(
                    TenancyGrainKeys.NotCreated(ErrorCode.ResourceGroupNotFound, "Resource group", name)
                )
        );

    /// <inheritdoc />
    public async Task<Result> SetLockAsync(LockLevel level) {
        if (state.State.Descriptor is not { } descriptor) {
            return Result.Failure(TenancyGrainKeys.NotCreated(ErrorCode.ResourceGroupNotFound, "Resource group", name));
        }

        if (descriptor.Lock == level) {
            return Result.Success;
        }

        state.State.Descriptor = descriptor with { Lock = level, Version = descriptor.Version + 1 };
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> BeginCreateAsync(ResourceId address) {
        if (state.State.Descriptor is not { } group) {
            return Result.Failure(TenancyGrainKeys.NotCreated(ErrorCode.ResourceGroupNotFound, "Resource group", name));
        }

        // ⚠ THE SEAL, AND THIS IS THE HALF OF IT THAT DOES THE WORK. BeginGroupDeleteAsync setting a
        // flag would be decorative if the create path did not read it: a resource whose membership
        // is recorded after the group's namespace has been judged empty gets its objects destroyed
        // by a verdict that was true when it was reached. Because both run in this grain's single
        // turn, there is no window between the two — which is the whole reason the seal lives here
        // rather than in the caller.
        if (group.State == ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource group '{name}' is being deleted, so '{address.Path}' cannot be created in "
                + "it. docs/plan/06 § Two-phase create in reverse seals the group before anything "
                + "below it is touched, precisely so that a create cannot land inside a delete that "
                + "has already decided the group is empty."
            );
        }

        if (address.TenantId != tenantId
            || address.SubscriptionId != subscriptionId
            || !string.Equals(address.ResourceGroup, name, StringComparison.Ordinal)) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{address.Path}' does not address a resource in this group "
                + $"({tenantId:D}/{subscriptionId:D}/{name}). A membership record whose address "
                + "points elsewhere would make the group's delete a delete of somebody else's "
                + "resources."
            );
        }

        if (state.State.Members.ContainsKey(address.Id)) {
            // Idempotent re-drive of step 2 — the retried PUT of docs/plan/06 § Two-phase create.
            // ⚠ It returns without touching the member, which is the load-bearing half: re-running
            // step 2 against a resource that has already reached Succeeded must not put it back into
            // Creating, or a retry would make a live resource look like an orphan to the reaper.
            return Result.Success;
        }

        state.State.Members[address.Id] = new() {
            ResourceId = address.Id, CanonicalPath = address.CanonicalPath, State = ProvisioningState.Creating
        };
        state.State.CreatingSince[address.Id] = clock.UtcNow;

        await state.WriteStateAsync();

        // ⚠ The group now has an in-flight create, so the reaper has something to look for. Arming
        // it here rather than on a schedule is what keeps its cost proportional to creates rather
        // than to groups — see ArmOrDisarmAsync.
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> CompleteCreateAsync(Guid resourceId, ProvisioningState terminal) {
        if (terminal is not (ProvisioningState.Succeeded or ProvisioningState.Failed or ProvisioningState.Canceled)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"{terminal} is not a terminal provisioning state. CompleteCreateAsync takes "
                + "Succeeded, Failed or Canceled — docs/plan/06 § Tags, locks."
            );
        }

        if (!state.State.Members.TryGetValue(resourceId, out var member)) {
            return Result.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.ResourceNotFound, "Resource", resourceId.ToString("D"))
            );
        }

        state.State.Members[resourceId] = member with { State = terminal };
        state.State.CreatingSince.Remove(resourceId);

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> BeginDeleteAsync(Guid resourceId) {
        if (!state.State.Members.TryGetValue(resourceId, out var member)) {
            return Result.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.ResourceNotFound, "Resource", resourceId.ToString("D"))
            );
        }

        if (member.State == ProvisioningState.Deleting) {
            return Result.Success;
        }

        state.State.Members[resourceId] = member with { State = ProvisioningState.Deleting };
        state.State.CreatingSince.Remove(resourceId);

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> FailDeleteAsync(Guid resourceId, string failure) {
        if (!state.State.Members.TryGetValue(resourceId, out var member)) {
            return Result.Failure(
                TenancyGrainKeys.NotCreated(ErrorCode.ResourceNotFound, "Resource", resourceId.ToString("D"))
            );
        }

        if (member.State != ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource {resourceId:D} is {member.State}, not Deleting. A teardown failure can "
                + "only be recorded against a delete that had begun."
            );
        }

        // ⚠ Stays Deleting. Stays listed. docs/plan/06 § Two-phase create: "never silently gone
        // while its pods still run and its meter still ticks. That last clause is a billing-dispute
        // prevention measure as much as a correctness one."
        state.State.Members[resourceId] = member with {
            State = ProvisioningState.Deleting, LastFailure = failure, TeardownAttempts = member.TeardownAttempts + 1
        };

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> CompleteDeleteAsync(Guid resourceId) {
        if (!state.State.Members.TryGetValue(resourceId, out var member)) {
            return Result.Success;
        }

        if (member.State != ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource {resourceId:D} is {member.State}, not Deleting. Removing it from the "
                + "listing without a delete having begun would hide a live resource."
            );
        }

        state.State.Members.Remove(resourceId);
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ResourceGroupMember>>> ListAsync() =>
        Task.FromResult(
            Result<IReadOnlyList<ResourceGroupMember>>.Success(
                [.. state.State.Members.Values.OrderBy(x => x.CanonicalPath, StringComparer.Ordinal)]
            )
        );

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ResourceGroupMember>>> ListOrphansAsync(TimeSpan olderThan) {
        var cutoff = clock.UtcNow - olderThan;

        var orphans = state.State.CreatingSince
            .Where(x => x.Value <= cutoff)
            .Select(x => state.State.Members.TryGetValue(x.Key, out var member) ? member : null)
            .Where(x => x is { State: ProvisioningState.Creating })
            .Select(x => x!)
            .OrderBy(x => x.CanonicalPath, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(Result<IReadOnlyList<ResourceGroupMember>>.Success(orphans));
    }

    /// <inheritdoc />
    public async Task<Result> RecordClusterAsync(Guid clusterId) {
        if (state.State.Descriptor is null) {
            return Result.Failure(TenancyGrainKeys.NotCreated(ErrorCode.ResourceGroupNotFound, "Resource group", name));
        }

        if (clusterId == Guid.Empty) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "The empty GUID is not a cluster. Recording it would make the group's delete try to "
                + "reclaim a namespace on a cluster that does not exist, and report the refusal as "
                + "though a real one had refused."
            );
        }

        // ⚠ Not sealed-gated. A pass may still be finishing while a delete is being attempted, and
        // learning about one more cluster is strictly better for the reclaim that follows —
        // forgetting one is how a namespace outlives every record of itself.
        if (!state.State.Clusters.Add(clusterId)) {
            return Result.Success;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Guid>>> ListClustersAsync() =>
        Task.FromResult(Result<IReadOnlyList<Guid>>.Success([.. state.State.Clusters]));

    /// <inheritdoc />
    public async Task<Result> BeginGroupDeleteAsync() {
        if (state.State.Descriptor is not { } descriptor) {
            // Absence is the goal of the whole choreography, so a group that is already gone has
            // nothing to seal and the caller may proceed to the namespaces.
            return Result.Success;
        }

        // ⚠ THE CHECK AND THE SEAL ARE ONE TURN. A caller that listed the members and then sealed
        // would have left exactly the window this method exists to shut, because BeginCreateAsync
        // could run between the two.
        if (state.State.Members.Count > 0) {
            var deleting = state.State.Members.Values.Count(x => x.State == ProvisioningState.Deleting);

            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource group '{name}' still holds {state.State.Members.Count} resource(s), "
                + $"{deleting} of them already Deleting, so it cannot be deleted. Delete them first: "
                + "a group delete does not cascade — see IResourceGroupGrain.BeginGroupDeleteAsync "
                + "for why a cascade that skipped each resource's own lock and authorization would "
                + "be a lock that does not hold. "
                + string.Join(
                    ", ",
                    state.State.Members.Values
                        .Select(x => x.CanonicalPath)
                        .Order(StringComparer.Ordinal)
                        .Take(5)
                )
            );
        }

        if (descriptor.State == ProvisioningState.Deleting) {
            // Idempotent: a re-driven delete finds the seal it set.
            return Result.Success;
        }

        state.State.Descriptor = descriptor with {
            State = ProvisioningState.Deleting, Version = descriptor.Version + 1
        };

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> CompleteGroupDeleteAsync() {
        if (state.State.Descriptor is not { } descriptor) {
            return Result.Success;
        }

        if (descriptor.State != ProvisioningState.Deleting) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource group '{name}' is {descriptor.State}, not Deleting. Removing its record "
                + "without BeginGroupDeleteAsync having sealed it would skip the only step that "
                + "stops a resource being created inside a delete that has already judged the group "
                + "empty — docs/plan/06 § Two-phase create, in reverse."
            );
        }

        if (state.State.Members.Count > 0) {
            // ⚠ Re-checked rather than assumed from the seal. The seal refuses to be set while
            // members exist, but a member can be recorded by a call that was already in flight when
            // the seal was written, and a group record removed while a member is listed is a
            // resource whose pods run and whose meter ticks with nothing above it.
            return Result.Failure(
                ErrorCode.Conflict,
                $"Resource group '{name}' acquired {state.State.Members.Count} member(s) after it "
                + "was sealed, so its record stays. The group is still sealed and the delete can be "
                + "re-driven once they are gone."
            );
        }

        state.State.Descriptor = null;
        state.State.Members.Clear();
        state.State.CreatingSince.Clear();

        // ⚠ The clusters go LAST and they go with the record. Until this point they are what the
        // caller reclaims namespaces from; keeping them after the record is gone would leave a group
        // that does not exist still naming clusters.
        state.State.Clusters.Clear();

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ResourceGroupMember>>> ReapOrphansAsync(TimeSpan olderThan) {
        // ⚠ THE FLOOR IS THE INDEX LEASE, AND WITHOUT IT THE REAPER'S EVIDENCE IS CIRCULAR. The
        // proof below is "old, and the index does not name this member" — but a claim inside its
        // five-minute lease has not expired yet, so a threshold shorter than the lease would sweep a
        // create that is merely slow, on the strength of an index entry that was about to be
        // confirmed. Refused rather than clamped: a caller that asked for a two-minute sweep asked
        // for something this cannot answer, and quietly widening it would hide that.
        if (olderThan < IndexClaimMachine.LeaseDuration) {
            return Result<IReadOnlyList<ResourceGroupMember>>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A {olderThan.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)}-minute "
                + "orphan threshold is shorter than the index lease of "
                + IndexClaimMachine.LeaseDuration.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)
                + " minutes, so a create that is simply slow is indistinguishable from one that "
                + "died. IResourceGroupGrain.OrphanAge is the value the reaper uses."
            );
        }

        var candidates = (await ListOrphansAsync(olderThan)).GetValueOrThrow();

        if (candidates.Count == 0) {
            return Result<IReadOnlyList<ResourceGroupMember>>.Success([]);
        }

        var reaped = new List<ResourceGroupMember>();

        foreach (var candidate in candidates) {
            var address = ResourceId.ParsePath(candidate.CanonicalPath);

            if (address.TryGetError(out var pathError)) {
                // ⚠ A member whose own path will not parse is a bug on the write path, and it is not
                // this method's to guess at. Removing it would be removing a member on the strength
                // of not understanding it.
                logger.LogError(
                    "Resource group '{Group}' holds a member whose canonical path does not parse, so "
                    + "it cannot be swept: {Path} — {Reason}",
                    name,
                    candidate.CanonicalPath,
                    pathError.Message
                );

                continue;
            }

            var entry = await GrainFactory
                .ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address.GetValueOrThrow()))
                .GetAsync();

            if (entry.TryGetError(out var indexError)) {
                // ⚠ THE MEMBER STAYS. An index that did not answer is a shard that is unreachable,
                // and reaping on it would turn a storage outage into resources vanishing from every
                // listing while their pods keep running — docs/plan/06 § Two-phase create's
                // billing-dispute clause, reached from the sweeping side.
                logger.LogWarning(
                    "The index for '{Path}' could not be read, so the member stays listed: {Reason}",
                    candidate.CanonicalPath,
                    indexError.Message
                );

                continue;
            }

            var index = entry.GetValueOrThrow();

            // ⚠ THE PROOF. docs/plan/06 § Two-phase create's orphan is "durable state, NO CONFIRMED
            // INDEX". A Confirmed or SoftDeleted binding to THIS member's id means the create got
            // through step 3 and the resource is real, however long it has been Creating — the thing
            // that failed then is step 4's bookkeeping, and reaping it would delete a live resource
            // from its group's listing. A Claimed entry is a create still inside its lease.
            var bound = index.BoundTo == candidate.ResourceId
                && index.State is IndexEntryState.Confirmed
                    or IndexEntryState.SoftDeleted
                    or IndexEntryState.Claimed;

            if (bound) {
                continue;
            }

            state.State.Members.Remove(candidate.ResourceId);
            state.State.CreatingSince.Remove(candidate.ResourceId);
            reaped.Add(candidate);
        }

        if (reaped.Count == 0) {
            return Result<IReadOnlyList<ResourceGroupMember>>.Success([]);
        }

        await state.WriteStateAsync();
        await ArmOrDisarmAsync();

        logger.LogInformation(
            "Reaped {Count} orphaned member(s) from resource group '{Group}' in subscription "
            + "{Subscription}: {Paths}. docs/plan/06 § Two-phase create — claimed a name, never "
            + "confirmed it.",
            reaped.Count,
            name,
            subscriptionId,
            string.Join(", ", reaped.Select(x => x.CanonicalPath))
        );

        return Result<IReadOnlyList<ResourceGroupMember>>.Success(reaped);
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, OrphanReminderName, StringComparison.Ordinal)) {
            return;
        }

        _ = await ReapOrphansAsync(IResourceGroupGrain.OrphanAge);
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Registers the reaper reminder while the group has a member in
    ///     <see cref="ProvisioningState.Creating" />, and cancels it when it has none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only while there is something to reap, and that is a cost decision with a
    ///         correctness consequence.</b> A reminder per resource group platform-wide would be a
    ///         standing row per group in the reminder table and a tick per group per period, forever,
    ///         to look at a list that is empty for all but a few seconds of a group's life. Arming it
    ///         off <c>CreatingSince</c> makes the reaper's cost proportional to the number of
    ///         in-flight creates rather than to the number of groups — and the consequence is that
    ///         every path which adds to or removes from that dictionary has to come through here,
    ///         which is why it is one method rather than a call at each site.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A silo with no reminder service logs and carries on.</b>
    ///         <c>RegisterOrUpdateReminder</c> throws there, and letting that escape would make every
    ///         create in such a silo fail over a sweeper — turning an absent cleanup into an absent
    ///         platform. The reaper is still reachable by hand
    ///         (<see cref="ReapOrphansAsync" />), which is what
    ///         <c>ResourceGroupReclaimer</c> uses before it seals a group, so the group delete does
    ///         not depend on this either.
    ///     </para>
    /// </remarks>
    async Task ArmOrDisarmAsync() {
        try {
            if (state.State.CreatingSince.Count > 0) {
                _ = await this.RegisterOrUpdateReminder(
                    OrphanReminderName,
                    OrphanSweepPeriod,
                    OrphanSweepPeriod
                );

                return;
            }

            if (await this.GetReminder(OrphanReminderName) is { } existing) {
                await this.UnregisterReminder(existing);
            }
        }
        catch (InvalidOperationException error) {
            logger.LogWarning(
                "Resource group '{Group}' could not arm the two-phase-create reaper because this "
                + "silo has no reminder service: {Reason} Orphaned members will not be swept "
                + "automatically here; ReapOrphansAsync still works and the group delete calls it.",
                name,
                error.Message
            );
        }
    }
}
