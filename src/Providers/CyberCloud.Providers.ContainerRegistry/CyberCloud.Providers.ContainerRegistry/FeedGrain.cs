using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Collections.Immutable;

namespace CyberCloud.Providers.ContainerRegistry;

/// <summary>What <see cref="FeedGrain" /> holds. Durable — see <see cref="IFeedGrain" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ContainerRegistry.FeedState")]
public sealed class FeedState {
    /// <summary>The protocol, once opened.</summary>
    [Id(0)]
    public FeedKind Kind { get; set; } = FeedKind.Unknown;

    /// <summary>Whether <see cref="IFeedGrain.OpenAsync" /> ran.</summary>
    [Id(1)]
    public bool IsOpen { get; set; }

    /// <summary>Whether <see cref="IFeedGrain.CloseAsync" /> ran. Sticky: a closed feed never reopens.</summary>
    [Id(2)]
    public bool IsClosed { get; set; }

    /// <summary>When the feed was opened.</summary>
    [Id(3)]
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Every entry, keyed by path.</summary>
    /// <remarks>
    ///     ⚠ <c>{ get; set; }</c> rather than get-only, for the reason
    ///     <c>SuppressionListState</c> gives: System.Text.Json does not populate a get-only
    ///     collection, so a get-only property would deserialize to an empty dictionary and the whole
    ///     catalogue would silently vanish on the first reactivation.
    /// </remarks>
    [Id(4)]
    public Dictionary<string, FeedEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
///     One feed's catalogue. docs/plan/13 § Artifact feeds; the contract is <see cref="IFeedGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Tenant-scoped, and it checks.</b> Reached through <c>IGrainFactory.ForTenant</c> and
///         nothing else, like every grain in the tree (ADR-002); <see cref="OnActivateAsync" />
///         refuses an activation with no tenant qualification, so a host that reached for it with
///         the bare factory fails at activation with a message that names <c>ForTenant</c>, not at
///         the first cross-tenant listing with nothing in the log.
///     </para>
///     <para>
///         <b>The catalogue orders and stores; the feeds host interprets.</b> Nothing here knows what
///         a nuspec is. The one rule the grain enforces on the host's behalf is the immutable
///         version: a put without <c>replace</c> onto a taken path is
///         <see cref="ErrorCode.ResourceAlreadyExists" />, so no protocol handler can overwrite a
///         published version by forgetting to check.
///     </para>
/// </remarks>
/// <param name="state">The catalogue.</param>
/// <param name="clock">Stamps the open and the puts.</param>
public sealed class FeedGrain(
    [PersistentState("feed", StorageTiers.Durable)]
    IPersistentState<FeedState> state,
    IClock clock
)
    : Grain, IFeedGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = this.GetTenantId()
            ?? throw new InvalidOperationException(
                $"{nameof(FeedGrain)} is a tenant-scoped grain but was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<IFeedGrain>(…), "
                + "not with IGrainFactory.GetGrain<…>(…) — ADR-002."
            );

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<FeedDescriptor>> OpenAsync(FeedKind kind) {
        if (kind == FeedKind.Unknown) {
            return Result<FeedDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                "A feed needs a kind. FeedKind.Unknown is the zero value a default-constructed wire "
                + "type carries, and the kind decides which protocol the feeds host answers with."
            );
        }

        if (state.State.IsClosed) {
            return Result<FeedDescriptor>.Failure(
                ErrorCode.Conflict,
                "This feed was closed and a closed feed never reopens. A feed that was deleted and "
                + "created again is a different resource with a different GUID and a different grain."
            );
        }

        if (state.State.IsOpen) {
            // ⚠ Idempotent for the same kind — a reconciler's second pass — and refused for another:
            // the kind is immutable in the schema, so a different kind here means a caller went round
            // the resource manager.
            return state.State.Kind == kind
                ? Result<FeedDescriptor>.Success(Snapshot())
                : Result<FeedDescriptor>.Failure(
                    ErrorCode.Conflict,
                    $"This feed is open as {ArtifactFeeds.NameOf(state.State.Kind)} and cannot be reopened as "
                    + $"{ArtifactFeeds.NameOf(kind)}. A feed's kind is immutable."
                );
        }

        state.State.Kind = kind;
        state.State.IsOpen = true;
        state.State.OpenedAt = clock.UtcNow;
        await state.WriteStateAsync();

        return Result<FeedDescriptor>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<FeedDescriptor>> DescribeAsync() => Task.FromResult(Result<FeedDescriptor>.Success(Snapshot()));

    /// <inheritdoc />
    public async Task<Result<FeedEntry>> PutAsync(FeedEntry entry, bool replace) {
        ArgumentNullException.ThrowIfNull(entry);

        if (!state.State.IsOpen || state.State.IsClosed) {
            return Result<FeedEntry>.Failure(ErrorCode.Conflict, "This feed is not open, so nothing can be stored in it.");
        }

        if (string.IsNullOrWhiteSpace(entry.Path) || entry.Path.StartsWith('/') || entry.Path.Contains("//", StringComparison.Ordinal)) {
            return Result<FeedEntry>.Failure(ErrorCode.InvalidRequestBody, $"'{entry.Path}' is not a catalogue path.");
        }

        if (!replace && state.State.Entries.ContainsKey(entry.Path)) {
            return Result<FeedEntry>.Failure(
                ErrorCode.ResourceAlreadyExists,
                $"'{entry.Path}' is already in this feed. A published version is immutable; publish a new version."
            );
        }

        var stored = entry with { PublishedAt = entry.PublishedAt == default ? clock.UtcNow : entry.PublishedAt };
        state.State.Entries[stored.Path] = stored;
        await state.WriteStateAsync();

        return Result<FeedEntry>.Success(stored);
    }

    /// <inheritdoc />
    public Task<Result<FeedEntry>> GetAsync(string path) =>
        Task.FromResult(
            state.State.Entries.TryGetValue(path, out var entry)
                ? Result<FeedEntry>.Success(entry)
                : Result<FeedEntry>.Failure(ErrorCode.ResourceNotFound, $"'{path}' is not in this feed.")
        );

    /// <inheritdoc />
    public Task<Result<ImmutableArray<FeedEntry>>> ListAsync(string pathPrefix) {
        ArgumentNullException.ThrowIfNull(pathPrefix);

        return Task.FromResult(
            Result<ImmutableArray<FeedEntry>>.Success(
                [
                    .. state.State.Entries
                        .Where(x => x.Key.StartsWith(pathPrefix, StringComparison.Ordinal))
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Value)
                ]
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(string path) {
        if (state.State.Entries.Remove(path)) {
            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<FeedClosure>> CloseAsync() {
        var dropped = state.State.Entries.Count;

        state.State.Entries.Clear();
        state.State.IsOpen = false;
        state.State.IsClosed = true;
        await state.WriteStateAsync();

        return Result<FeedClosure>.Success(new() { EntriesDropped = dropped });
    }

    FeedDescriptor Snapshot() =>
        new() {
            Kind = state.State.Kind,
            IsOpen = state.State.IsOpen && !state.State.IsClosed,
            IsClosed = state.State.IsClosed,
            EntryCount = state.State.Entries.Count,
            OpenedAt = state.State.OpenedAt
        };
}
