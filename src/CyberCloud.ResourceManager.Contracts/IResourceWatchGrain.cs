using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The resources in one subscription that asked to hear when resources of one type change —
///     one grain per (subscription, type), keyed by <see cref="GrainKeys.WatchIndex" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Durable, and it has to be.</b> A watch is the whole memory of "the vault wants to know
///         about file shares": the reconciler that registered it holds nothing (clause 2 of
///         docs/plan/08 § The reconcile loop), and a fan-out that read an empty set after a silo
///         restart would deliver nothing and report nothing wrong. So this is a durable-tier grain
///         (ADR-003), listed in <c>durable-grains.txt</c>, and its set survives the silo.
///     </para>
///     <para>
///         ⚠ <b>Tenant-scoped, like every grain the manager owns.</b> Reached through
///         <c>ForTenant</c>, so a watcher can only ever be registered in its own tenant's index and a
///         fan-out reading the changed resource's tenant's index finds only that tenant's watchers.
///         The cross-tenant refusal is the key, not a check inside this grain.
///     </para>
///     <para>
///         ⚠ <b>The set is a set.</b> <see cref="SubscribeAsync" /> on a member is a success that
///         writes nothing, because every reconcile pass of every watcher may call it — see
///         <see cref="IResourceWatch" />.
///     </para>
/// </remarks>
[Alias("CyberCloud.ResourceManager.IResourceWatchGrain")]
public interface IResourceWatchGrain : IGrainWithStringKey {
    /// <summary>Adds a watcher, or leaves it if it is there.</summary>
    /// <param name="watcher">
    ///     The watching resource. Its type is recorded beside its id so a fan-out can name the
    ///     watching type in its log without a second grain read.
    /// </param>
    Task<Result> SubscribeAsync(ResourceWatcher watcher);

    /// <summary>Removes a watcher, or leaves the set if it was never there.</summary>
    /// <param name="resourceId">The watching resource's GUID.</param>
    Task<Result> UnsubscribeAsync(Guid resourceId);

    /// <summary>The current watchers, in registration order.</summary>
    Task<Result<ImmutableArray<ResourceWatcher>>> ListAsync();

    /// <summary>Lets a test or a repair tool release the activation.</summary>
    Task DeactivateAsync();
}

/// <summary>One resource that asked to hear about a type.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ResourceManager.ResourceWatcher")]
public sealed record ResourceWatcher {
    /// <summary>The watching resource's GUID — the key of its <see cref="IResourceGrain" />.</summary>
    [Id(0)]
    public Guid ResourceId { get; init; }

    /// <summary>The watching resource's path, for the fan-out's log and for a repair tool.</summary>
    [Id(1)]
    public string Path { get; init; } = string.Empty;

    /// <summary>When the watch was first registered.</summary>
    [Id(2)]
    public DateTimeOffset Since { get; init; }
}
