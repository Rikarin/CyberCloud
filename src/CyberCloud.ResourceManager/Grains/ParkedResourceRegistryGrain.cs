using CyberCloud.Core.Time;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     <see cref="IParkedResourceRegistryGrain" /> — Entity, Durable, key
///     <c>parked/{subscriptionId:N}/rg/{name}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This grain records and answers, and it decides nothing.</b> It does not know what a
///         recovery window is, when one ends, or who may see an entry: the window belongs to
///         <c>IResourceIndexGrain</c>, which stamps and reads it with one clock, and the visibility
///         belongs to docs/plan/07 § The enforcement seam, which is a <c>Check</c> per entry made by
///         whatever serves a listing. Putting either here would be a second, weaker copy of a rule
///         that already has one place.
///     </para>
///     <para>
///         ⚠ <b>It never reaches another grain, which is what makes it safe to call from the middle
///         of three other choreographies.</b> <c>OperationGrain.ParkAsync</c> calls it between two
///         writes it is retrying, and <c>ResourceManagerService</c> calls it on the request path with
///         a caller waiting. A registry that read the index to "verify" an entry would put a second
///         grain hop inside both, and would still be reading a value that can change the moment it
///         answers.
///     </para>
/// </remarks>
public sealed class ParkedResourceRegistryGrain(
    [PersistentState("parkedResources", StorageTiers.Durable)] IPersistentState<ParkedResourceRegistryState> state,
    IClock clock
)
    : Grain, IParkedResourceRegistryGrain {
    Guid tenantId;
    Guid subscriptionId;
    string group = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = ResourceManagerGrainKeys.TenantOf(this);

        var key = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.ParkedResourceRegistry);
        subscriptionId = key.Id;
        group = key.Name;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> ParkAsync(ResourceId address) {
        // ⚠ AN UNRESOLVED ADDRESS IS REFUSED RATHER THAN RECORDED, and the reason is that both ways
        // out of this registry take a GUID. docs/plan/06 § Identifiers keeps GUIDs out of paths, so
        // an address that came from a path carries Guid.Empty; an entry holding one would be a name
        // nothing could unpark, and Guid.Empty is also default(ResourceId)'s id, so a second such
        // entry would silently overwrite the first.
        if (address.Id == Guid.Empty) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{address.Path}' carries no resource id, so it cannot be recorded as parked. The "
                + "registry is keyed by GUID because a restore and a purge both address the resource "
                + "by one — resolve the address through IResourceIndexGrain first."
            );
        }

        if (Elsewhere(address.TenantId, address.SubscriptionId, address.ResourceGroup)) {
            return Result.Failure(ErrorCode.InvalidResourceId, NotThisGroup($"'{address.Path}'"));
        }

        // ⚠ IDEMPOTENT, AND THE EARLY RETURN IS THE LOAD-BEARING HALF. OperationGrain.ParkAsync is
        // re-driven from a durable reminder, so a second call is the ordinary path. Rewriting the
        // entry would move ParkedAt on every retry, which would make "when was this deleted" a
        // function of how many times the teardown was interrupted — the same rule, and the same
        // reason, as IResourceIndexGrain.SoftDeleteAsync not restamping its deadline on a re-drive.
        if (state.State.Entries.ContainsKey(address.Id)) {
            return Result.Success;
        }

        state.State.Entries[address.Id] = new() {
            Path = address.Path, ResourceId = address.Id, ParkedAt = clock.UtcNow
        };

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> UnparkAsync(Guid resourceId) {
        // ⚠ ABSENCE IS THE GOAL, so removing what is not there succeeds. Both callers are retried —
        // a restore by its caller, a purge by whichever of the two fronts drove it — and a refusal
        // here would turn an operation whose work landed into one that never converges.
        if (!state.State.Entries.Remove(resourceId)) {
            return Result.Success;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ParkedResource>>> ListAsync() =>
        Task.FromResult(Result<IReadOnlyList<ParkedResource>>.Success(Ordered(state.State.Entries.Values)));

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ParkedResource>>> ListOfTypeAsync(ResourceCollectionId collection) {
        if (Elsewhere(collection.TenantId, collection.SubscriptionId, collection.ResourceGroup)) {
            return Task.FromResult(
                Result<IReadOnlyList<ParkedResource>>.Failure(
                    ErrorCode.InvalidResourceId,
                    NotThisGroup($"The collection '{collection.Path}'")
                )
            );
        }

        // ⚠ THE ANCESTOR NAMES ARE PART OF THE FILTER AND NOT AN AFTERTHOUGHT — see IsIn. A
        // collection of `servers/databases` is addressed `…/servers/{serverName}/databases`, so two
        // servers in one group have two database collections; comparing the type alone would answer
        // both at once and hand a caller the parked databases of a server they did not ask about.
        var matching = state.State.Entries.Values.Where(entry => IsIn(entry, collection));

        return Task.FromResult(Result<IReadOnlyList<ParkedResource>>.Success(Ordered(matching)));
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether an address or a collection names a different resource group than this one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Checked rather than trusted, exactly as <c>IResourceGroupGrain.BeginCreateAsync</c>
    ///     checks it.</b> Every caller builds this grain's key from the same address it then passes
    ///     in, so a mismatch means our own code composed a key from one address and an argument from
    ///     another — and the quiet version of that is a resource recorded as recoverable in somebody
    ///     else's group, where the restore that would find it is a restore nobody is entitled to
    ///     make.
    /// </remarks>
    bool Elsewhere(Guid addressTenant, Guid addressSubscription, string? addressGroup) =>
        addressTenant != tenantId
        || addressSubscription != subscriptionId
        || !string.Equals(addressGroup, group, StringComparison.Ordinal);

    /// <summary>Whether one entry belongs to <paramref name="collection" />.</summary>
    /// <remarks>
    ///     <see cref="ResourceTypeName" /> compares case-insensitively, which is what makes the
    ///     provider namespace's case-preserving spelling on the wire irrelevant here; the ancestor
    ///     names are ordinal, because <see cref="ResourceNaming" /> already forbids upper case in them.
    /// </remarks>
    static bool IsIn(ParkedResource entry, ResourceCollectionId collection) {
        var address = entry.AddressOf();

        return address.Type == collection.Type
            && string.Equals(address.ParentNames, collection.ParentNames, StringComparison.Ordinal);
    }

    string NotThisGroup(string subject) =>
        $"{subject} does not name a resource in this group "
        + $"({tenantId:D}/{subscriptionId:D}/{group}). A parked-resource entry whose address points "
        + "elsewhere would offer a restore of somebody else's resource.";

    /// <summary>
    ///     The entries in the order a listing reads them — by canonical path, ordinally.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same ordering <c>IResourceManager.ListAsync</c> pages on</b>, so that a listing
    ///     built over this registry can carry the same continuation shape — "the next entry whose
    ///     canonical path sorts after this one" — without a second definition of what "next" means.
    ///     A <see cref="Dictionary{TKey,TValue}" />'s own order is not one.
    /// </remarks>
    static IReadOnlyList<ParkedResource> Ordered(IEnumerable<ParkedResource> entries) =>
        [.. entries.OrderBy(entry => entry.AddressOf().CanonicalPath, StringComparer.Ordinal)];
}
