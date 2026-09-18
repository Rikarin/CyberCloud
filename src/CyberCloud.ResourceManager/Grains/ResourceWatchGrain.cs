using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     The resources in one subscription that asked to hear when resources of one type change —
///     <see cref="IResourceWatchGrain" />, keyed by <see cref="GrainKeys.WatchIndex" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The key is a digest, so this grain cannot say which subscription or type it is for.</b>
///         That is the same trade <c>ResourceIndexGrain</c> makes for <c>idx/path</c>: the type name
///         carries a <c>/</c> and the key grammar does not, so the pair is hashed. The consequence is
///         that this grain checks nothing about the watcher it is handed beyond its shape — the
///         subscription scope is enforced by whoever computes the key, and both callers
///         (<c>OwnedResourceWatch</c> for the owner's own subscription, <c>ResourceWatchFanout</c>
///         for the changed resource's) compute it from a fact the manager established rather than
///         from anything a provider said.
///     </para>
///     <para>
///         A dictionary rather than a list so that the every-pass <see cref="SubscribeAsync" /> is a
///         lookup, and so that a watcher registered twice is one watcher.
///     </para>
/// </remarks>
public sealed class ResourceWatchGrain(
    [PersistentState("resourceWatch", StorageTiers.Durable)]
    IPersistentState<ResourceWatchState> state
)
    : Grain, IResourceWatchGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = ResourceManagerGrainKeys.TenantOf(this);
        _ = ResourceManagerGrainKeys.Decode(this, GrainKeyKind.WatchIndex);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(ResourceWatcher watcher) {
        ArgumentNullException.ThrowIfNull(watcher);

        if (watcher.ResourceId == Guid.Empty) {
            return Result.Failure(
                ErrorCode.InvalidResourceId,
                $"'{watcher.Path}' carries no resource id, so it cannot watch anything. A watch is "
                + "registered from a reconcile pass, by which point the resource has one."
            );
        }

        // ⚠ A member is a success that writes nothing, because every pass of every watcher may call
        // this — IResourceWatch says why. Writing the same entry again would be a durable-tier write
        // per reconcile pass for a fact that has not changed.
        if (state.State.Watchers.ContainsKey(watcher.ResourceId)) {
            return Result.Success;
        }

        state.State.Watchers[watcher.ResourceId] = watcher;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result> UnsubscribeAsync(Guid resourceId) {
        if (!state.State.Watchers.Remove(resourceId)) {
            return Result.Success;
        }

        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<ResourceWatcher>>> ListAsync() =>
        Task.FromResult(
            Result<ImmutableArray<ResourceWatcher>>.Success(
                [.. state.State.Watchers.Values.OrderBy(static x => x.Since).ThenBy(static x => x.ResourceId)]
            )
        );

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
