using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     The catalogue grain's own rules, against the real grain in an in-memory silo.
/// </summary>
/// <remarks>
///     ⚠ In-memory grain storage, which is the deviation <c>ProviderTestCluster</c> records and
///     for the same reason. What it cannot prove is that <c>FeedState</c> serializes; what it does
///     prove is every rule below, and that the grain activates from THIS provider's assembly through
///     <c>ForTenant</c> — the first provider grain in the tree, and the first time a provider
///     assembly is on a silo's manifest for a reason other than a reconciler.
/// </remarks>
public sealed class FeedGrainTests : IAsyncLifetime {
    static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    TestCluster cluster = null!;

    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    IFeedGrain Feed(Guid tenant, Guid feed) =>
        cluster.GrainFactory
            .ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IFeedGrain>(GrainKeys.Resource(feed));

    static FeedEntry Entry(string path, string sha = "00") =>
        new() { Path = path, StoredAt = path, Size = 3, Sha256 = sha, ContentType = "application/octet-stream" };

    [Fact]
    public async Task OpenIsIdempotentForTheSameKindAndRefusedForAnother() {
        var feed = Feed(Tenant, Guid.NewGuid());

        (await feed.DescribeAsync()).GetValueOrThrow().IsOpen.ShouldBeFalse();

        var first = (await feed.OpenAsync(FeedKind.NuGet)).GetValueOrThrow();
        var second = (await feed.OpenAsync(FeedKind.NuGet)).GetValueOrThrow();

        first.IsOpen.ShouldBeTrue();
        first.Kind.ShouldBe(FeedKind.NuGet);
        second.OpenedAt.ShouldBe(first.OpenedAt);

        var refused = await feed.OpenAsync(FeedKind.Npm);
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task AnUnknownKindIsRefused() =>
        (await Feed(Tenant, Guid.NewGuid()).OpenAsync(FeedKind.Unknown)).Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

    [Fact]
    public async Task APublishedVersionIsImmutableUnlessTheCallerSaysReplace() {
        var feed = Feed(Tenant, Guid.NewGuid());
        await feed.OpenAsync(FeedKind.NuGet);

        var stored = (await feed.PutAsync(Entry("nuget/pkg/1.0.0", "aa"), replace: false)).GetValueOrThrow();
        stored.PublishedAt.ShouldNotBe(default);

        var again = await feed.PutAsync(Entry("nuget/pkg/1.0.0", "bb"), replace: false);
        again.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        (await feed.GetAsync("nuget/pkg/1.0.0")).GetValueOrThrow().Sha256.ShouldBe("aa");

        // Maven metadata and npm dist-tags are the callers that replace.
        (await feed.PutAsync(Entry("nuget/pkg/1.0.0", "cc"), replace: true)).IsSuccess.ShouldBeTrue();
        (await feed.GetAsync("nuget/pkg/1.0.0")).GetValueOrThrow().Sha256.ShouldBe("cc");
    }

    [Fact]
    public async Task AListingIsAPrefixInPathOrderAndAnAbsentPathIsNotFound() {
        var feed = Feed(Tenant, Guid.NewGuid());
        await feed.OpenAsync(FeedKind.Npm);

        await feed.PutAsync(Entry("npm/b/2.0.0"), false);
        await feed.PutAsync(Entry("npm/a/1.0.0"), false);
        await feed.PutAsync(Entry("npm/a/1.1.0"), false);
        await feed.PutAsync(Entry("npm/ab/1.0.0"), false);

        (await feed.ListAsync("npm/a/")).GetValueOrThrow().Select(x => x.Path).ShouldBe(["npm/a/1.0.0", "npm/a/1.1.0"]);
        (await feed.ListAsync("npm/")).GetValueOrThrow().Length.ShouldBe(4);
        (await feed.GetAsync("npm/zzz/1.0.0")).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await feed.RemoveAsync("npm/a/1.0.0")).IsSuccess.ShouldBeTrue();
        (await feed.RemoveAsync("npm/never")).IsSuccess.ShouldBeTrue();
        (await feed.ListAsync("npm/a/")).GetValueOrThrow().Select(x => x.Path).ShouldBe(["npm/a/1.1.0"]);
    }

    [Fact]
    public async Task ACloseEmptiesTheCatalogueAndIsSticky() {
        var feed = Feed(Tenant, Guid.NewGuid());
        await feed.OpenAsync(FeedKind.Maven);
        await feed.PutAsync(Entry("maven/org/x/y/1.0/y-1.0.jar"), false);
        await feed.PutAsync(Entry("maven/org/x/y/1.0/y-1.0.pom"), false);

        var closed = (await feed.CloseAsync()).GetValueOrThrow();
        closed.EntriesDropped.ShouldBe(2);

        var described = (await feed.DescribeAsync()).GetValueOrThrow();
        described.IsOpen.ShouldBeFalse();
        described.IsClosed.ShouldBeTrue();
        described.EntryCount.ShouldBe(0);

        (await feed.PutAsync(Entry("maven/org/x/y/1.1/y-1.1.jar"), false)).Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await feed.OpenAsync(FeedKind.Maven)).Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task APutOnAFeedThatWasNeverOpenedIsRefused() =>
        (await Feed(Tenant, Guid.NewGuid()).PutAsync(Entry("nuget/x/1"), false)).Error!.Code.ShouldBe(ErrorCode.Conflict);

    [Theory]
    [InlineData("")]
    [InlineData("/nuget/x/1")]
    [InlineData("nuget//x")]
    public async Task APathThatIsNotACataloguePathIsRefused(string path) {
        var feed = Feed(Tenant, Guid.NewGuid());
        await feed.OpenAsync(FeedKind.NuGet);

        (await feed.PutAsync(Entry(path), false)).Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task TwoTenantsWithTheSameFeedIdAreTwoCatalogues() {
        // The same GUID under two tenants is two grains, because the key carries the tenant —
        // ADR-002, and the reason every reference goes through ForTenant.
        var id = Guid.NewGuid();
        var mine = Feed(Tenant, id);
        var theirs = Feed(OtherTenant, id);

        await mine.OpenAsync(FeedKind.NuGet);
        await mine.PutAsync(Entry("nuget/secret/1.0.0"), false);

        (await theirs.DescribeAsync()).GetValueOrThrow().IsOpen.ShouldBeFalse();
        (await theirs.ListAsync("")).GetValueOrThrow().ShouldBeEmpty();
    }

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.ConfigureServices(services => services.AddSingleton<IClock, SystemClock>());
        }
    }
}
