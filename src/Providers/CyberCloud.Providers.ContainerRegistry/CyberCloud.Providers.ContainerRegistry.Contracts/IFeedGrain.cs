using System.Collections.Immutable;

namespace CyberCloud.Providers.ContainerRegistry.Contracts;

/// <summary>Which protocol a feed speaks.</summary>
[Alias("CyberCloud.ContainerRegistry.FeedKind")]
public enum FeedKind {
    /// <summary>The zero value a default-constructed wire type carries. Never a real feed.</summary>
    Unknown = 0,

    /// <summary>The NuGet v3 API — service index, push, flat container, registration, search.</summary>
    NuGet = 1,

    /// <summary>The npm registry API — publish, packument, tarball, search.</summary>
    Npm = 2,

    /// <summary>Maven's repository layout — PUT and GET by path, <c>maven-metadata.xml</c>.</summary>
    Maven = 3
}

/// <summary>
///     One artefact in a feed's catalogue: where its bytes are, what they hash to, and what the
///     protocol recorded about them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The catalogue is protocol-agnostic and this record is why it can be.
///         </b> A NuGet package version, an npm tarball and a Maven file are each one entry:
///         <see cref="Path" /> is the protocol's own coordinates (<c>nuget/{id}/{version}</c>,
///         <c>npm/{name}/{version}</c>, <c>maven/{group}/{artifact}/{version}/{file}</c>), and
///         <see cref="Metadata" /> is whatever the protocol needs to answer its listings — a
///         nuspec's fields, an npm version manifest, nothing at all for Maven. The grain orders,
///         stores and lists; the feeds host interprets. That keeps three protocols' worth of rules
///         in the one process that speaks them, and the durable grain the size of a catalogue.
///     </para>
///     <para>
///         ⚠ <b>No member of this record ends in <c>Key</c></b>, and <see cref="StoredAt" /> is
///         spelled the way it is because of that. CC1005 refuses an <c>[Id]</c> member named
///         <c>*Key</c> anywhere outside <c>CyberCloud.Vault</c>, and an object key is not a secret
///         — but a rule with no exemptions is a rule nobody learns to work around.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.ContainerRegistry.FeedEntry")]
public sealed record FeedEntry {
    /// <summary>The entry's coordinates inside the feed — see the remarks on the class.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    ///     Where the bytes are in the platform's object store, relative to the feed's
    ///     <see cref="ArtifactFeeds.StoragePrefix" />. Usually equal to <see cref="Path" />.
    /// </summary>
    [Id(1)]
    public string StoredAt { get; init; } = string.Empty;

    /// <summary>How many bytes.</summary>
    [Id(2)]
    public long Size { get; init; }

    /// <summary>The bytes' SHA-256, lower-case hex. Every protocol hands a hash back; this is the one they derive from.</summary>
    [Id(3)]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>The media type the bytes are served back with.</summary>
    [Id(4)]
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>What the protocol recorded, as JSON. <c>{}</c> when it recorded nothing.</summary>
    [Id(5)]
    public string Metadata { get; init; } = "{}";

    /// <summary>
    ///     Whether the entry appears in listings and search. ⚠ NuGet's delete is an unlist: the
    ///     bytes stay, the version resolves by exact name, and it leaves every listing.
    /// </summary>
    [Id(6)]
    public bool Listed { get; init; } = true;

    /// <summary>When the entry was stored.</summary>
    [Id(7)]
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>The subject that stored it — <c>{subjectType}:{subjectId}</c>.</summary>
    [Id(8)]
    public string PublishedBy { get; init; } = string.Empty;
}

/// <summary>What a feed's grain says about itself.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ContainerRegistry.FeedDescriptor")]
public sealed record FeedDescriptor {
    /// <summary>The protocol. <see cref="FeedKind.Unknown" /> until <see cref="IFeedGrain.OpenAsync" /> ran.</summary>
    [Id(0)]
    public FeedKind Kind { get; init; } = FeedKind.Unknown;

    /// <summary>Whether the feed accepts and serves entries — opened and not since closed.</summary>
    [Id(1)]
    public bool IsOpen { get; init; }

    /// <summary>Whether <see cref="IFeedGrain.CloseAsync" /> ran. A closed feed never reopens.</summary>
    [Id(2)]
    public bool IsClosed { get; init; }

    /// <summary>How many entries the catalogue holds, listed or not.</summary>
    [Id(3)]
    public int EntryCount { get; init; }

    /// <summary>When the feed was opened.</summary>
    [Id(4)]
    public DateTimeOffset OpenedAt { get; init; }
}

/// <summary>What a close dropped.</summary>
[GenerateSerializer]
[Alias("CyberCloud.ContainerRegistry.FeedClosure")]
public sealed record FeedClosure {
    /// <summary>How many entries the catalogue held when it closed.</summary>
    [Id(0)]
    public int EntriesDropped { get; init; }
}

/// <summary>
///     One feed's catalogue — the durable metadata of every artefact the feeds host stored under it.
///     docs/plan/13 § Artifact feeds; keyed <c>GrainKeys.Resource(feedId)</c> within the tenant.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The first provider grain in the tree, and durable-grains.txt says why the resource
///             manager could not have held this instead.
///         </b> <c>ResourceGrain</c> holds the tenant's declared intent — a feed's kind and
///         description. What this grain holds is what tenants <i>put into</i> the feed afterwards:
///         which versions of which packages exist, which are listed, what each hashes to, which
///         dist-tag points where. That is data-plane content, like rows in a managed database, and
///         the manager holds no database's rows either. It is durable rather than hot because the
///         bytes in the object store do not carry it back: an npm dist-tag, a NuGet unlisting and a
///         publish timestamp exist nowhere but here.
///     </para>
///     <para>
///         <b>Every path operation is exact and every listing is a prefix</b>, ordinally, over the
///         protocol's own coordinates. The host spells the coordinates so that a prefix listing is
///         a protocol question: <c>nuget/{id}/</c> is every version of one package,
///         <c>npm/</c> is every package for search.
///     </para>
///     <para>
///         ⚠ <b>The catalogue is one grain and one state document.</b> A feed with ten thousand
///         entries is a state document of a few megabytes, read on activation and written on every
///         publish. That is fine at M2 and is the first thing to split — per package — when a feed
///         proves it is not; the split is a grain-key change the host never sees, because the host
///         addresses everything through this interface.
///     </para>
/// </remarks>
[Alias("CyberCloud.ContainerRegistry.IFeedGrain")]
public interface IFeedGrain : IGrainWithStringKey {
    /// <summary>Opens the feed for one protocol. Idempotent for the same kind; refused for another.</summary>
    /// <param name="kind">The protocol. <see cref="FeedKind.Unknown" /> is refused.</param>
    /// <returns>The feed as it now is, or <see cref="ErrorCode.Conflict" /> when it was opened as another kind or is closed.</returns>
    Task<Result<FeedDescriptor>> OpenAsync(FeedKind kind);

    /// <summary>What the feed is — never fails, so a reconciler can observe an unopened one.</summary>
    Task<Result<FeedDescriptor>> DescribeAsync();

    /// <summary>Stores one entry's metadata.</summary>
    /// <param name="entry">The entry. ⚠ The bytes are the caller's to have already stored.</param>
    /// <param name="replace">Whether an entry at the same path is overwritten. Off is the immutable-version rule.</param>
    /// <returns>
    ///     The stored entry, or <see cref="ErrorCode.ResourceAlreadyExists" /> when the path is taken
    ///     and <paramref name="replace" /> is off, or <see cref="ErrorCode.Conflict" /> when the feed
    ///     is not open.
    /// </returns>
    Task<Result<FeedEntry>> PutAsync(FeedEntry entry, bool replace);

    /// <summary>One entry by exact path.</summary>
    /// <param name="path">The coordinates.</param>
    /// <returns>The entry, or <see cref="ErrorCode.ResourceNotFound" />.</returns>
    Task<Result<FeedEntry>> GetAsync(string path);

    /// <summary>Every entry whose path starts with a prefix, in path order. Unlisted entries included.</summary>
    /// <param name="pathPrefix">The prefix, ordinal.</param>
    Task<Result<ImmutableArray<FeedEntry>>> ListAsync(string pathPrefix);

    /// <summary>Forgets one entry. Forgetting an absent path succeeds.</summary>
    /// <param name="path">The coordinates.</param>
    Task<Result> RemoveAsync(string path);

    /// <summary>
    ///     Closes the feed and empties the catalogue. A closed feed refuses every put and every open;
    ///     the bytes are the reconciler's to remove from the object store afterwards.
    /// </summary>
    Task<Result<FeedClosure>> CloseAsync();
}
