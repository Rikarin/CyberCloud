using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;
using System.Collections.Immutable;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IResourceIndexGrain" /> — Index, Durable, key <c>idx/path/{digest}</c>.
/// </summary>
public sealed class ResourceIndexGrain(
    [PersistentState("index", StorageTiers.Durable)] IPersistentState<IndexState> state,
    IClock clock
)
    : Grain, IResourceIndexGrain {
    Guid tenantId;
    string digest = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = TenancyGrainKeys.TenantOf(this);
        digest = TenancyGrainKeys.Decode(this, GrainKeyKind.PathIndex).Digest;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> TryClaimAsync(ResourceId address, Guid resourceId) {
        if (resourceId == Guid.Empty) {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A claim binds a path to a resource GUID and Guid.Empty is not one. A path parsed "
                + "from a URL carries Guid.Empty until the index resolves it — docs/plan/06 "
                + "§ Identifiers — so passing it here means the caller has the question, not the "
                + "answer."
            );
        }

        if (address.TenantId != tenantId) {
            return Result<IndexEntry>.Failure(
                ErrorCode.AuthorizationFailed,
                $"This index grain belongs to tenant {tenantId:D} and '{address.Path}' addresses "
                + $"tenant {address.TenantId:D}."
            );
        }

        // ⚠ The key must be the one GrainKeys would mint for this address, or a claim would be
        // recorded against a grain that no lookup will ever reach. The check is cheap and it is the
        // only thing standing between "the digest is one-way" and "nobody ever notices it is wrong".
        var expected = GrainKeys.PathIndex(address);
        if (!string.Equals(expected, GrainKeys.PathIndexPrefix + digest, StringComparison.Ordinal)) {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidGrainKey,
                $"'{address.CanonicalPath}' hashes to '{expected}' and this grain is "
                + $"'{GrainKeys.PathIndexPrefix + digest}'. The caller built the key from a "
                + "different value than the one it is claiming — most likely Path rather than "
                + "CanonicalPath (docs/plan/06 § Identifiers)."
            );
        }

        var claimed = IndexClaimMachine.TryClaim(
            state.State.Entry,
            resourceId,
            address.CanonicalPath,
            clock.UtcNow,
            $"'{address.CanonicalPath}'"
        );

        return await PersistAsync(claimed);
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> ConfirmAsync(Guid resourceId) =>
        await PersistAsync(IndexClaimMachine.Confirm(state.State.Entry, resourceId, clock.UtcNow, Describe()));

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid resourceId) {
        var released = await PersistAsync(
            IndexClaimMachine.Release(state.State.Entry, resourceId, clock.UtcNow, Describe())
        );

        if (released.TryGetError(out var error)) {
            return Result.Failure(error);
        }

        // ⚠ THE COUNTS BELONG TO THE RESOURCE AND NOT TO THE ADDRESS, SO THEY GO WHEN THE BINDING DOES.
        //
        // The name is reusable the instant this returns — docs/plan/06 § Two-phase create, "release the
        // index first (so the name is immediately reusable)" — and the next create at the same path is
        // a DIFFERENT resource with a different GUID. A count that survived would be inherited by it,
        // and a brand-new resource that answers 409 to its own delete over children it never had is
        // unrecoverable without an operator.
        //
        // Nothing is normally cleared here: the manager's gate refused the delete unless every count
        // was already zero, so this is the second line of defence rather than the first. It is worth
        // having because the failure it prevents is permanent and the cost is one dictionary clear.
        if (state.State.Children.Count > 0) {
            state.State.Children.Clear();
            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> SoftDeleteAsync(Guid resourceId, TimeSpan retention) =>
        // ⚠ NOTHING IS CLEARED HERE, WHICH IS THE WHOLE DIFFERENCE FROM ReleaseAsync ABOVE.
        //
        // That method drops the child counts because the name becomes free and the next create at this
        // address is a different resource. This one holds the name for the same resource, which is
        // coming back with the same GUID — so the counts stay true and clearing them would leave a
        // restored parent deletable over children it still has.
        await PersistAsync(
            IndexClaimMachine.SoftDelete(
                state.State.Entry,
                resourceId,
                clock.UtcNow + retention,
                clock.UtcNow,
                Describe()
            )
        );

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> RestoreAsync(Guid resourceId) =>
        await PersistAsync(IndexClaimMachine.Restore(state.State.Entry, resourceId, clock.UtcNow, Describe()));

    /// <inheritdoc />
    public async Task<Result<int>> AddChildAsync(ResourceTypeName childType) {
        if (childType.IsEmpty) {
            return Result<int>.Failure(
                ErrorCode.InvalidResourceType,
                "A child was registered against this address with no type. The refusal a parent's "
                + "delete gives has to name what is holding it — docs/plan/08 § Deleting a parent "
                + "resource that has children — and an untyped count cannot."
            );
        }

        var key = Key(childType);
        var count = state.State.Children.GetValueOrDefault(key) + 1;

        state.State.Children[key] = count;
        await state.WriteStateAsync();

        return Result<int>.Success(count);
    }

    /// <inheritdoc />
    public async Task<Result<int>> RemoveChildAsync(ResourceTypeName childType) {
        if (childType.IsEmpty) {
            return Result<int>.Failure(
                ErrorCode.InvalidResourceType,
                "A child was deregistered from this address with no type, so there is no count to "
                + "decrement."
            );
        }

        var key = Key(childType);

        // ⚠ Clamped at zero rather than allowed to go negative, and a decrement for a type that is not
        // counted succeeds. OperationGrain calls this from a re-drivable delete, so "run twice" is the
        // normal case and not the exceptional one; a negative count would make the parent DELETABLE
        // while a child still existed, which is the whole failure this counter closes.
        if (!state.State.Children.TryGetValue(key, out var count) || count <= 1) {
            if (state.State.Children.Remove(key)) {
                await state.WriteStateAsync();
            }

            return Result<int>.Success(0);
        }

        state.State.Children[key] = count - 1;
        await state.WriteStateAsync();

        return Result<int>.Success(count - 1);
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<ChildTypeCount>>> ChildrenAsync() =>
        Task.FromResult(
            Result<ImmutableArray<ChildTypeCount>>.Success(
                [
                    .. state.State.Children
                        // ⚠ A key that no longer parses is dropped rather than surfaced. Only Key()
                        // writes this dictionary and it writes ToString()'s exact form, so this is
                        // unreachable; if it ever were reachable, a refusal naming a type nobody can
                        // act on is worse than one that undercounts, and the count would be visible in
                        // the resource-graph projection either way.
                        .Where(x => x.Value > 0 && ResourceTypeName.TryParse(x.Key, out _))
                        // Ordered so a refusal message is the same on every retry. An unordered
                        // dictionary would make "2 databases and 1 firewallRule" and the reverse the
                        // same refusal with two different texts, which reads as two different faults.
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => new ChildTypeCount { Type = Parse(x.Key), Count = x.Value })
                ]
            )
        );

    /// <inheritdoc />
    public Task<Result<IndexEntry>> GetAsync() =>
        Task.FromResult(Result<IndexEntry>.Success(IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow)));

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveAsync() {
        var entry = IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow);

        return Task.FromResult(
            entry.State == IndexEntryState.Confirmed
                ? Result<Guid>.Success(entry.BoundTo)
                : Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    // ⚠ ONE MESSAGE FOR EVERY NON-CONFIRMED STATE, SOFT-DELETED INCLUDED, AND THE
                    // STATE NAME IN IT IS NOT AN ORACLE BECAUSE NOTHING PROJECTS IT TO A CALLER.
                    // ResourceManagerService turns any failure from here into `'{path}' does not
                    // exist.` from its own NotFound helper — byte for byte the message a name that was
                    // never taken gets — so this text reaches a log and never a response body.
                    // docs/plan/08 § Soft delete forbids a 410 for exactly the reason that matters
                    // here: the caller must not be able to tell a held name from a free one.
                    $"{Describe()} resolves to nothing: it is {entry.State}. Only a confirmed binding "
                    + "is a resource — a claim under lease may never become one, and a soft-deleted "
                    + "one is recoverable rather than addressable."
                )
        );
    }

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveSoftDeletedAsync() {
        var entry = IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow);

        return Task.FromResult(
            entry.State == IndexEntryState.SoftDeleted
                ? Result<Guid>.Success(entry.BoundTo)
                : Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"{Describe()} holds no soft-deleted resource: it is {entry.State}."
                )
        );
    }

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveExpiredAsync() {
        var now = clock.UtcNow;
        var entry = IndexClaimMachine.Effective(state.State.Entry, now);

        if (entry.State != IndexEntryState.SoftDeleted) {
            return Task.FromResult(
                Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"{Describe()} holds no soft-deleted resource: it is {entry.State}."
                )
            );
        }

        // ⚠ `<=` and THIS GRAIN'S OWN CLOCK, which is the same comparison IndexClaimMachine.Restore
        // makes to refuse a restore. The two are complements by construction rather than by
        // agreement: an instant that is too late to restore is exactly an instant at which the window
        // has ended, and no second clock is involved in either answer.
        return Task.FromResult(
            entry.RecoverableUntil <= now
                ? Result<Guid>.Success(entry.BoundTo)
                : Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"{Describe()} is soft-deleted and its recovery window runs until "
                    + $"{entry.RecoverableUntil.ToString("u", System.Globalization.CultureInfo.InvariantCulture)}, so "
                    + "is still restorable and nothing may end it on the clock's account."
                )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>The dictionary key for a child type — canonical, for the reason IndexState gives.</summary>
    static string Key(ResourceTypeName childType) => childType.Canonical.ToString();

    /// <summary>The inverse of <see cref="Key" />, for a key the filter above has already accepted.</summary>
    static ResourceTypeName Parse(string key) {
        _ = ResourceTypeName.TryParse(key, out var type);
        return type;
    }

    string Describe() =>
        state.State.Entry.IndexedValue is { Length: > 0 } value
            ? $"'{value}'"
            : $"Index entry '{GrainKeys.PathIndexPrefix + digest}'";

    async Task<Result<IndexEntry>> PersistAsync(Result<IndexEntry> outcome) {
        if (outcome.TryGetError(out _)) {
            return outcome;
        }

        var entry = outcome.GetValueOrThrow();
        if (entry == state.State.Entry) {
            return outcome;
        }

        state.State.Entry = entry;
        await state.WriteStateAsync();
        return outcome;
    }
}

/// <summary>
///     <see cref="IEmailIndexGrain" /> — Index, Durable, key <c>idx/email/{digest}</c>.
/// </summary>
public sealed class EmailIndexGrain(
    [PersistentState("index", StorageTiers.Durable)] IPersistentState<IndexState> state,
    IClock clock
)
    : Grain, IEmailIndexGrain {
    Guid tenantId;
    string digest = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = TenancyGrainKeys.TenantOf(this);
        digest = TenancyGrainKeys.Decode(this, GrainKeyKind.EmailIndex).Digest;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> TryClaimAsync(string email, Guid userId) {
        if (userId == Guid.Empty) {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A claim binds an address to a user and Guid.Empty is not one."
            );
        }

        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out var invalid)) {
            return Result<IndexEntry>.Failure(invalid);
        }

        var address = normalized.GetValueOrThrow();
        var expected = GrainKeys.EmailIndex(tenantId, address);

        if (!string.Equals(expected, GrainKeys.EmailIndexPrefix + digest, StringComparison.Ordinal)) {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidGrainKey,
                $"'{address}' in tenant {tenantId:D} hashes to '{expected}' and this grain is "
                + $"'{GrainKeys.EmailIndexPrefix + digest}'. Email uniqueness is per tenant "
                + "(docs/plan/06 § Grain keys), so the tenant is part of the digest."
            );
        }

        var claimed = IndexClaimMachine.TryClaim(state.State.Entry, userId, address, clock.UtcNow, $"'{address}'");

        return await PersistAsync(claimed);
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> ConfirmAsync(Guid userId) =>
        await PersistAsync(IndexClaimMachine.Confirm(state.State.Entry, userId, clock.UtcNow, Describe()));

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid userId) =>
        (await PersistAsync(IndexClaimMachine.Release(state.State.Entry, userId, clock.UtcNow, Describe()))).ToResult();

    /// <inheritdoc />
    public Task<Result<IndexEntry>> GetAsync() =>
        Task.FromResult(Result<IndexEntry>.Success(IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow)));

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveAsync() {
        var entry = IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow);

        return Task.FromResult(
            entry.State == IndexEntryState.Confirmed
                ? Result<Guid>.Success(entry.BoundTo)
                : Result<Guid>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"{Describe()} resolves to nothing: it is {entry.State}."
                )
        );
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    string Describe() =>
        state.State.Entry.IndexedValue is { Length: > 0 } value
            ? $"'{value}'"
            : $"Index entry '{GrainKeys.EmailIndexPrefix + digest}'";

    async Task<Result<IndexEntry>> PersistAsync(Result<IndexEntry> outcome) {
        if (outcome.TryGetError(out _)) {
            return outcome;
        }

        var entry = outcome.GetValueOrThrow();
        if (entry == state.State.Entry) {
            return outcome;
        }

        state.State.Entry = entry;
        await state.WriteStateAsync();
        return outcome;
    }
}
