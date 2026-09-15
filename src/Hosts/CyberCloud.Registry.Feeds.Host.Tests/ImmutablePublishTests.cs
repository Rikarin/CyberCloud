using CyberCloud.Identity.Validation;
using CyberCloud.Registry.Feeds.Host.Feeds;
using CyberCloud.ResourceManager.Conformance;
using System.Collections.Immutable;
using System.Text;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The claim two racing pushes of one version meet, driven with the interleaving the host
///     cannot be made to produce on demand: the check passed for both, both stored, one claimed
///     first.
/// </summary>
/// <remarks>
///     ⚠ Over HTTP the race is real and not reproducible — <c>NuGetProtocolTests</c> and
///     <c>NpmProtocolTests</c> prove the serial half through the host. This class holds the catalogue
///     and the store in its hands and plays the loser's turn after the winner's, which is the one
///     ordering the grain's single-threadedness guarantees will happen to somebody.
/// </remarks>
public sealed class ImmutablePublishTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALosingClaimRemovesItsOwnBytesAndLeavesTheWinners() {
        var store = new InMemoryObjectStore();
        var context = Context(store);

        var winner = await Store(store, context, "first"u8.ToArray());
        (await context.Catalogue.PutAsync(winner, replace: false)).IsSuccess.ShouldBeTrue();

        var loser = await Store(store, context, "second"u8.ToArray());
        var claimed = await ImmutablePublish.ClaimAsync(context, store, loser, [loser.StoredAt], Token);

        claimed.IsFailure.ShouldBeTrue();
        claimed.TryGetError(out var refused).ShouldBeTrue();
        refused!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        // The entry still describes the bytes the store holds, and the loser's are gone.
        var surviving = (await context.Catalogue.GetAsync(winner.Path)).GetValueOrThrow();
        surviving.StoredAt.ShouldBe(winner.StoredAt);
        surviving.Sha256.ShouldBe(FeedResponses.Sha256Of("first"u8));
        (await store.GetAsync(context.StoragePrefix + winner.StoredAt, Token)).IsSuccess.ShouldBeTrue("the winner's bytes were removed");
        (await store.GetAsync(context.StoragePrefix + loser.StoredAt, Token)).IsFailure.ShouldBeTrue("the loser's bytes were left behind");
    }

    [Fact]
    public async Task ALosingClaimWithTheSameBytesRemovesNothing() {
        // Two runners publishing the identical artefact write the identical key; the second's
        // delete would be the first's bytes.
        var store = new InMemoryObjectStore();
        var context = Context(store);

        var winner = await Store(store, context, "same"u8.ToArray());
        (await context.Catalogue.PutAsync(winner, replace: false)).IsSuccess.ShouldBeTrue();

        var loser = await Store(store, context, "same"u8.ToArray());
        loser.StoredAt.ShouldBe(winner.StoredAt);

        var claimed = await ImmutablePublish.ClaimAsync(context, store, loser, [loser.StoredAt], Token);

        claimed.IsFailure.ShouldBeTrue();
        (await store.GetAsync(context.StoragePrefix + winner.StoredAt, Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AWinningClaimKeepsEverything() {
        var store = new InMemoryObjectStore();
        var context = Context(store);

        var entry = await Store(store, context, "only"u8.ToArray());
        var claimed = await ImmutablePublish.ClaimAsync(context, store, entry, [entry.StoredAt], Token);

        claimed.IsSuccess.ShouldBeTrue();
        (await store.GetAsync(context.StoragePrefix + entry.StoredAt, Token)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void TheKeyCarriesTheHashBetweenTheDirectoryAndTheFile() {
        var at = ImmutablePublish.StoredAt("nuget/x/1.0.0", "abc", "x.1.0.0.nupkg");

        at.ShouldBe("nuget/x/1.0.0/abc/x.1.0.0.nupkg");
        ImmutablePublish.DirectoryOf(at).ShouldBe("nuget/x/1.0.0/abc");
    }

    static async Task<FeedEntry> Store(InMemoryObjectStore store, FeedContext context, byte[] bytes) {
        var sha256 = FeedResponses.Sha256Of(bytes);
        var storedAt = ImmutablePublish.StoredAt("nuget/x/1.0.0", sha256, "x.1.0.0.nupkg");

        (await store.PutAsync(context.StoragePrefix + storedAt, bytes, "application/octet-stream", Token)).IsSuccess.ShouldBeTrue();

        return new() { Path = "nuget/x/1.0.0", StoredAt = storedAt, Size = bytes.Length, Sha256 = sha256, PublishedBy = "user:test" };
    }

    static FeedContext Context(InMemoryObjectStore store) {
        var tenant = Guid.NewGuid();
        var feed = Guid.NewGuid();
        var claims = new TokenClaims(tenant, "user", "test", "cyc.api", "", DateTimeOffset.UtcNow.AddHours(1));

        return new(
            claims,
            new() { TenantId = tenant, SubjectType = "user", SubjectId = "test" },
            new(tenant, Guid.NewGuid(), "build", ArtifactFeeds.Type, "packages", feed),
            FeedKind.NuGet,
            new Catalogue(),
            ArtifactFeeds.StoragePrefix(tenant, feed)
        );
    }

    /// <summary>The grain's put-without-replace rule and nothing else, in a dictionary.</summary>
    sealed class Catalogue : IFeedGrain {
        readonly Dictionary<string, FeedEntry> entries = new(StringComparer.Ordinal);

        public Task<Result<FeedEntry>> PutAsync(FeedEntry entry, bool replace) {
            if (!replace && entries.ContainsKey(entry.Path)) {
                return Task.FromResult(Result<FeedEntry>.Failure(ErrorCode.ResourceAlreadyExists, $"'{entry.Path}' is already in this feed."));
            }

            entries[entry.Path] = entry;
            return Task.FromResult(Result<FeedEntry>.Success(entry));
        }

        public Task<Result<FeedEntry>> GetAsync(string path) =>
            Task.FromResult(
                entries.TryGetValue(path, out var entry)
                    ? Result<FeedEntry>.Success(entry)
                    : Result<FeedEntry>.Failure(ErrorCode.ResourceNotFound, $"'{path}' is not in this feed.")
            );

        public Task<Result<ImmutableArray<FeedEntry>>> ListAsync(string pathPrefix) =>
            Task.FromResult(Result<ImmutableArray<FeedEntry>>.Success([.. entries.Values.Where(x => x.Path.StartsWith(pathPrefix, StringComparison.Ordinal))]));

        public Task<Result> RemoveAsync(string path) {
            entries.Remove(path);
            return Task.FromResult(Result.Success);
        }

        public Task<Result<FeedDescriptor>> OpenAsync(FeedKind kind) => throw new NotSupportedException();

        public Task<Result<FeedDescriptor>> DescribeAsync() => throw new NotSupportedException();

        public Task<Result<FeedClosure>> CloseAsync() => throw new NotSupportedException();
    }
}
