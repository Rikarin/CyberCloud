using CyberCloud.Core.Contracts;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IDirectoryIndexGrain" /> — Index, Durable, key <c>idx/dir/{collection}</c>,
///     tenant-qualified. Issue #41.
/// </summary>
/// <remarks>
///     ⚠ <b>A set held as a list.</b> Adding an id that's already here writes nothing, so the
///     claim-first callers can re-drive a create without growing the list; the order is kept so a
///     listing reads oldest first, which is the order an administrator expects of a member list
///     that grows at the bottom.
/// </remarks>
public sealed class DirectoryIndexGrain(
    [PersistentState("directory", StorageTiers.Durable)]
    IPersistentState<DirectoryIndexGrainState> state
)
    : Grain, IDirectoryIndexGrain {
    string collection = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        // Both checks throw on a wrong key: a tenant-less activation, or another shape's key.
        _ = IdentityGrainKeys.TenantOf(this);
        collection = IdentityGrainKeys.Decode(this, GrainKeyKind.DirectoryIndex).Name;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> AddAsync(Guid id) {
        if (id == Guid.Empty) {
            return Result.Failure(ErrorCode.InvalidRequestBody, "A directory index records a real id, not the empty GUID.");
        }

        if (state.State.Ids.Contains(id)) {
            return Result.Success;
        }

        if (state.State.Ids.Count >= DirectoryIndexPolicy.MaxEntries) {
            return Result.Failure(
                ErrorCode.QuotaExceeded,
                $"This tenant's directory already holds {DirectoryIndexPolicy.MaxEntries} {collection}, "
                + "which is as many as one list keeps. Remove one before adding another."
            );
        }

        state.State.Ids.Add(id);
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(Guid id) {
        if (state.State.Ids.Remove(id)) {
            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Guid>>> ListAsync() =>
        Task.FromResult(Result<IReadOnlyList<Guid>>.Success([.. state.State.Ids]));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();

        return Task.CompletedTask;
    }
}
