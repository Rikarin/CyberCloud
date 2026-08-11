using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     <see cref="IResourceIndexGrain" /> — Index, Durable, key <c>idx/path/{digest}</c>.
/// </summary>
public sealed class ResourceIndexGrain(
    [PersistentState("index", StorageTiers.Durable)] IPersistentState<IndexState> state,
    IClock clock)
    : Grain, IResourceIndexGrain
{
    Guid tenantId;
    string digest = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        tenantId = TenancyGrainKeys.TenantOf(this);
        digest = TenancyGrainKeys.Decode(this, GrainKeyKind.PathIndex).Digest;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> TryClaimAsync(ResourceId address, Guid resourceId)
    {
        if (resourceId == Guid.Empty)
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidRequestBody,
                "A claim binds a path to a resource GUID and Guid.Empty is not one. A path parsed "
                + "from a URL carries Guid.Empty until the index resolves it — docs/plan/06 "
                + "§ Identifiers — so passing it here means the caller has the question, not the "
                + "answer.");
        }

        if (address.TenantId != tenantId)
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.AuthorizationFailed,
                $"This index grain belongs to tenant {tenantId:D} and '{address.Path}' addresses "
                + $"tenant {address.TenantId:D}.");
        }

        // ⚠ The key must be the one GrainKeys would mint for this address, or a claim would be
        // recorded against a grain that no lookup will ever reach. The check is cheap and it is the
        // only thing standing between "the digest is one-way" and "nobody ever notices it is wrong".
        var expected = GrainKeys.PathIndex(address);
        if (!string.Equals(expected, GrainKeys.PathIndexPrefix + digest, StringComparison.Ordinal))
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidGrainKey,
                $"'{address.CanonicalPath}' hashes to '{expected}' and this grain is "
                + $"'{GrainKeys.PathIndexPrefix + digest}'. The caller built the key from a "
                + "different value than the one it is claiming — most likely Path rather than "
                + "CanonicalPath (docs/plan/06 § Identifiers).");
        }

        var claimed = IndexClaimMachine.TryClaim(
            state.State.Entry, resourceId, address.CanonicalPath, clock.UtcNow,
            $"'{address.CanonicalPath}'");

        return await PersistAsync(claimed);
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> ConfirmAsync(Guid resourceId) =>
        await PersistAsync(IndexClaimMachine.Confirm(
            state.State.Entry, resourceId, clock.UtcNow, Describe()));

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid resourceId) =>
        (await PersistAsync(IndexClaimMachine.Release(
            state.State.Entry, resourceId, clock.UtcNow, Describe()))).ToResult();

    /// <inheritdoc />
    public Task<Result<IndexEntry>> GetAsync() =>
        Task.FromResult(Result<IndexEntry>.Success(
            IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow)));

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveAsync()
    {
        var entry = IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow);

        return Task.FromResult(entry.State == IndexEntryState.Confirmed
            ? Result<Guid>.Success(entry.BoundTo)
            : Result<Guid>.Failure(
                ErrorCode.ResourceNotFound,
                $"{Describe()} resolves to nothing: it is {entry.State}. Only a confirmed binding "
                + "is a resource — a claim under lease may never become one."));
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    string Describe() => state.State.Entry.IndexedValue is { Length: > 0 } value
        ? $"'{value}'"
        : $"Index entry '{GrainKeys.PathIndexPrefix + digest}'";

    async Task<Result<IndexEntry>> PersistAsync(Result<IndexEntry> outcome)
    {
        if (outcome.TryGetError(out _))
        {
            return outcome;
        }

        var entry = outcome.GetValueOrThrow();
        if (entry == state.State.Entry)
        {
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
    IClock clock)
    : Grain, IEmailIndexGrain
{
    Guid tenantId;
    string digest = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        tenantId = TenancyGrainKeys.TenantOf(this);
        digest = TenancyGrainKeys.Decode(this, GrainKeyKind.EmailIndex).Digest;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> TryClaimAsync(string email, Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidRequestBody, "A claim binds an address to a user and Guid.Empty is not one.");
        }

        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out var invalid))
        {
            return Result<IndexEntry>.Failure(invalid);
        }

        var address = normalized.GetValueOrThrow();
        var expected = GrainKeys.EmailIndex(tenantId, address);

        if (!string.Equals(expected, GrainKeys.EmailIndexPrefix + digest, StringComparison.Ordinal))
        {
            return Result<IndexEntry>.Failure(
                ErrorCode.InvalidGrainKey,
                $"'{address}' in tenant {tenantId:D} hashes to '{expected}' and this grain is "
                + $"'{GrainKeys.EmailIndexPrefix + digest}'. Email uniqueness is per tenant "
                + "(docs/plan/06 § Grain keys), so the tenant is part of the digest.");
        }

        var claimed = IndexClaimMachine.TryClaim(
            state.State.Entry, userId, address, clock.UtcNow, $"'{address}'");

        return await PersistAsync(claimed);
    }

    /// <inheritdoc />
    public async Task<Result<IndexEntry>> ConfirmAsync(Guid userId) =>
        await PersistAsync(IndexClaimMachine.Confirm(
            state.State.Entry, userId, clock.UtcNow, Describe()));

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid userId) =>
        (await PersistAsync(IndexClaimMachine.Release(
            state.State.Entry, userId, clock.UtcNow, Describe()))).ToResult();

    /// <inheritdoc />
    public Task<Result<IndexEntry>> GetAsync() =>
        Task.FromResult(Result<IndexEntry>.Success(
            IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow)));

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveAsync()
    {
        var entry = IndexClaimMachine.Effective(state.State.Entry, clock.UtcNow);

        return Task.FromResult(entry.State == IndexEntryState.Confirmed
            ? Result<Guid>.Success(entry.BoundTo)
            : Result<Guid>.Failure(
                ErrorCode.ResourceNotFound,
                $"{Describe()} resolves to nothing: it is {entry.State}."));
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        this.DeactivateOnIdle();
        return Task.CompletedTask;
    }

    string Describe() => state.State.Entry.IndexedValue is { Length: > 0 } value
        ? $"'{value}'"
        : $"Index entry '{GrainKeys.EmailIndexPrefix + digest}'";

    async Task<Result<IndexEntry>> PersistAsync(Result<IndexEntry> outcome)
    {
        if (outcome.TryGetError(out _))
        {
            return outcome;
        }

        var entry = outcome.GetValueOrThrow();
        if (entry == state.State.Entry)
        {
            return outcome;
        }

        state.State.Entry = entry;
        await state.WriteStateAsync();
        return outcome;
    }
}
