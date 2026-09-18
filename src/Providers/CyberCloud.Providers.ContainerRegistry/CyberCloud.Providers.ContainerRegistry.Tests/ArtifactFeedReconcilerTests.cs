using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using NSubstitute;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     The feed reconciler's four clauses, against a substituted catalogue and the in-memory object
///     store, so each branch can be reached one at a time.
/// </summary>
/// <remarks>
///     The real grain is <c>FeedGrainTests</c>' subject and the real lifecycle is the shared
///     suite's; what this file reaches is what neither can steer — a catalogue that reads back
///     wrong, a store that still lists an artefact after the deletes, a body with no kind.
/// </remarks>
public sealed class ArtifactFeedReconcilerTests {
    static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    static readonly Guid Subscription = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    static readonly Guid FeedId = Guid.Parse("dddddddd-0000-4000-8000-00000000000d");

    static ResourceId Address { get; } =
        new(Tenant, Subscription, "prod", ArtifactFeeds.Type, "packages", FeedId);

    static (ArtifactFeedReconciler Reconciler, IFeedGrain Feed, InMemoryObjectStore Objects) Build() {
        var feed = Substitute.For<IFeedGrain>();
        var grains = Substitute.For<IGrainFactory>();

        // ⚠ Any key, and the assertion that it is the RIGHT key is below. ForTenant encodes the
        // tenant into the string key, so matching the exact spelling here would pin an
        // Orleans.Multitenant detail rather than the reconciler's behaviour.
        grains.GetGrain<IFeedGrain>(Arg.Any<string>(), Arg.Any<string>()).Returns(feed);

        return (new ArtifactFeedReconciler(grains, new SystemClock()), feed, new InMemoryObjectStore());
    }

    static ReconcileContext Context(InMemoryObjectStore objects, string body) {
        using var document = JsonDocument.Parse(body);

        return new(
            Address,
            ArtifactFeeds.V2026,
            document.RootElement.Clone(),
            null,
            ReconcileDriver.NamespaceFor(Address),
            null,
            new InMemorySecretVault(),
            new NullLog()
        ) { Objects = objects };
    }

    static FeedDescriptor Open(FeedKind kind) => new() { Kind = kind, IsOpen = true };

    [Fact]
    public async Task ACreateOpensTheCatalogueAndConvergesOnlyOnceItReadsBackOpen() {
        var (reconciler, feed, objects) = Build();
        feed.OpenAsync(FeedKind.NuGet).Returns(Result<FeedDescriptor>.Success(Open(FeedKind.NuGet)));
        feed.DescribeAsync().Returns(Result<FeedDescriptor>.Success(Open(FeedKind.NuGet)));

        var outcome = await reconciler.ReconcileAsync(
            Context(objects, ArtifactFeeds.Body()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        await feed.Received(1).OpenAsync(FeedKind.NuGet);
        objects.Count.ShouldBe(0, "a create writes nothing to the object store; the host does");
    }

    [Fact]
    public async Task ACatalogueThatDoesNotReadBackOpenIsInProgressNotConverged() {
        // ⚠ Clause 4. The open said yes; the reading says no; the reading wins.
        var (reconciler, feed, objects) = Build();
        feed.OpenAsync(FeedKind.Npm).Returns(Result<FeedDescriptor>.Success(Open(FeedKind.Npm)));
        feed.DescribeAsync().Returns(Result<FeedDescriptor>.Success(new() { Kind = FeedKind.Npm, IsOpen = false }));

        var outcome = await reconciler.ReconcileAsync(
            Context(objects, ArtifactFeeds.Body("npm")),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task ACatalogueOpenAsAnotherKindIsARefusalFromTheGrain() {
        var (reconciler, feed, objects) = Build();
        feed.OpenAsync(FeedKind.Maven).Returns(Result<FeedDescriptor>.Failure(ErrorCode.Conflict, "open as nuget"));

        var outcome = await reconciler.ReconcileAsync(
            Context(objects, ArtifactFeeds.Body("maven")),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task ABodyWithNoKindFailsByNameRatherThanOpeningAnUnknownFeed() {
        var (reconciler, feed, objects) = Build();

        var outcome = await reconciler.ReconcileAsync(
            Context(objects, """{"location":"eu-central","properties":{}}"""),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain(ArtifactFeeds.KindPointer);
        await feed.DidNotReceive().OpenAsync(Arg.Any<FeedKind>());
    }

    [Fact]
    public async Task ADeleteClosesTheCatalogueEmptiesThePrefixAndLeavesOtherPrefixesAlone() {
        var (reconciler, feed, objects) = Build();
        feed.CloseAsync().Returns(Result<FeedClosure>.Success(new() { EntriesDropped = 2 }));

        var prefix = ArtifactFeeds.StoragePrefix(Tenant, FeedId);
        var token = TestContext.Current.CancellationToken;
        await objects.PutAsync(prefix + "nuget/a/1.0.0/a.nupkg", "a"u8.ToArray(), "application/octet-stream", token);
        await objects.PutAsync(prefix + "nuget/b/1.0.0/b.nupkg", "b"u8.ToArray(), "application/octet-stream", token);
        await objects.PutAsync(
            ArtifactFeeds.StoragePrefix(Tenant, Guid.NewGuid()) + "nuget/c/1.0.0/c.nupkg",
            "c"u8.ToArray(),
            "application/octet-stream",
            token
        );

        var outcome = await reconciler.DeleteAsync(Context(objects, ArtifactFeeds.Body()), token);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        await feed.Received(1).CloseAsync();
        (await objects.ListAsync(prefix, token)).GetValueOrThrow().ShouldBeEmpty();
        objects.Count.ShouldBe(1, "another feed's artefact under the same tenant was removed by this feed's teardown");
    }

    [Fact]
    public async Task ADeleteAgainstAStoreThatCannotListIsAFailureNotAConvergence() {
        // ⚠ The refusing default's message, and the reason it refuses: an empty answer would let
        // the teardown converge over artefacts it never saw.
        var (reconciler, feed, _) = Build();
        feed.CloseAsync().Returns(Result<FeedClosure>.Success(new()));

        var context = Context(new InMemoryObjectStore(), ArtifactFeeds.Body()) with {
            Objects = new RefusingObjectStore()
        };

        var outcome = await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("ReconcileContext.Objects");
    }

    [Fact]
    public async Task ADeleteWhoseDeletesDoNotEmptyThePrefixIsInProgress() {
        // A store that lists a key its delete does not remove — the shape of an eventually
        // consistent listing — must not converge on the first pass.
        var (reconciler, feed, _) = Build();
        feed.CloseAsync().Returns(Result<FeedClosure>.Success(new()));

        var prefix = ArtifactFeeds.StoragePrefix(Tenant, FeedId);
        var sticky = new StickyListingStore(prefix + "nuget/a/1.0.0/a.nupkg");
        var context = Context(new InMemoryObjectStore(), ArtifactFeeds.Body()) with { Objects = sticky };

        var outcome = await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        sticky.Deletes.ShouldBe(1);
    }

    [Fact]
    public async Task ObserveReportsTheCatalogueAndNeverThrows() {
        var (reconciler, feed, _) = Build();
        feed.DescribeAsync()
            .Returns(Result<FeedDescriptor>.Success(new() { Kind = FeedKind.NuGet, IsOpen = true, EntryCount = 3 }));

        using var body = JsonDocument.Parse(ArtifactFeeds.Body());
        var observed = await reconciler.ObserveAsync(
            new(Address, ArtifactFeeds.V2026, body.RootElement, "", null),
            TestContext.Current.CancellationToken
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("3 entries");

        feed.DescribeAsync().Returns(Result<FeedDescriptor>.Success(new() { IsClosed = true }));
        (await reconciler.ObserveAsync(
                new(Address, ArtifactFeeds.V2026, body.RootElement, "", null),
                TestContext.Current.CancellationToken
            ))
            .Exists.ShouldBeFalse();
    }

    [Fact]
    public void TheReconcilerSatisfiesTheFourClauseContract() {
        var (reconciler, _, _) = Build();

        ReconcilerConformance.CheckNoHiddenState(reconciler).ShouldBeEmpty();
    }

    /// <summary>A store whose listing keeps naming a key its delete accepted.</summary>
    sealed class StickyListingStore(string key) : IObjectStore {
        public int Deletes { get; private set; }

        public Task<Result> PutAsync(
            string k,
            ReadOnlyMemory<byte> content,
            string contentType,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result.Success);

        public Task<Result<StoredObject>> GetAsync(string k, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<StoredObject>.Failure(ErrorCode.ResourceNotFound, "no"));

        public Task<Result> DeleteAsync(string k, CancellationToken cancellationToken = default) {
            Deletes++;
            return Task.FromResult(Result.Success);
        }

        public Task<Result<ImmutableArray<string>>> ListAsync(
            string prefix,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<ImmutableArray<string>>.Success([key]));
    }
}
