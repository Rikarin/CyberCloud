using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/05 § The tenant directory — <b>the one global thing</b>, and the three claims it
///     makes about itself.
/// </summary>
[Collection(TenancySuite.Name)]
public sealed class TenantDirectoryTests(TenancyCluster cluster) {
    [Fact]
    public async Task ATenantIsRegisteredWithItsRegionAndBothShards() {
        // "Per tenant: id, slug, home region, hot-shard id, durable-shard id, status, directory
        // version. About 200 bytes."
        var tenant = Tenant(1);
        var assignment = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        var entry = (await cluster.DirectoryGrain()
            .RegisterAsync(
                new() {
                    TenantId = tenant,
                    Slug = "dir-1",
                    HomeRegion = "eu-central",
                    HotShard = assignment.HotHashTag,
                    DurableShard = assignment.DurableShard,
                    Status = TenantStatus.Provisioning
                }
            )).GetValueOrThrow();

        entry.DirectoryVersion.ShouldBeGreaterThan(0);
        entry.DurableShard.ShouldBe(assignment.DurableShard);

        (await cluster.DirectoryGrain().LookupAsync(tenant)).GetValueOrThrow().Slug.ShouldBe("dir-1");
        (await cluster.DirectoryGrain().LookupBySlugAsync("dir-1")).GetValueOrThrow()
            .TenantId
            .ShouldBe(tenant);
    }

    [Fact]
    public async Task SlugsAreGloballyUnique() {
        // docs/plan/04 § The clusters, plural puts "global uniqueness (email, tenant slug, DNS zone
        // apex)" in the global cluster. The directory grain's single activation IS that mutex.
        await Register(Tenant(2), "dir-unique");

        var stolen = await cluster.DirectoryGrain()
            .RegisterAsync(new() { TenantId = Tenant(3), Slug = "dir-unique", HomeRegion = "us-east" });

        stolen.IsFailure.ShouldBeTrue();
        stolen.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task APurgedTenantsSlugIsTombstonedForever() {
        // docs/plan/06 § Tenant lifecycle: "directory entry tombstoned forever (never reuse an id)".
        var tenant = Tenant(4);
        await Register(tenant, "dir-purged");

        (await cluster.DirectoryGrain().SetStatusAsync(tenant, TenantStatus.Purged)).IsSuccess
            .ShouldBeTrue();

        var reused = await cluster.DirectoryGrain()
            .RegisterAsync(new() { TenantId = Tenant(5), Slug = "dir-purged", HomeRegion = "eu-central" });

        reused.IsFailure.ShouldBeTrue();
        reused.Error!.Message.ShouldContain("purged");

        // The entry itself stays, so the id can never be reissued either.
        (await cluster.DirectoryGrain().LookupAsync(tenant)).GetValueOrThrow()
            .Status
            .ShouldBe(TenantStatus.Purged);
    }

    [Fact]
    public async Task ReadsNeverLeaveTheProcessOnceTheTenantIsResident() {
        // Claim 1: "Reads never leave the process." TryLookup is not even async — it cannot do I/O.
        var tenant = Tenant(6);
        await Register(tenant, "dir-resident");
        await cluster.Directory.RefreshAsync(TestContext.Current.CancellationToken);

        cluster.Directory.TryLookup(tenant, out var entry).ShouldBeTrue();
        entry!.Slug.ShouldBe("dir-resident");

        var missesBefore = cluster.Directory.Misses;
        (await cluster.Directory.LookupAsync(tenant)).GetValueOrThrow().Slug.ShouldBe("dir-resident");
        cluster.Directory.Misses.ShouldBe(missesBefore, "a resident tenant is not a cache miss.");
    }

    [Fact]
    public async Task ACacheMissFallsBackToAGrainCallAndIsCounted() {
        // Claim 2: "A cache miss (a tenant created 200 ms ago in another region) falls back to a
        // grain call — measured, alerted on, and expected to be a handful per second worldwide."
        var tenant = Tenant(7);
        await Register(tenant, "dir-miss");

        // Deliberately NOT refreshed: this is the "created 200 ms ago in another region" case.
        cluster.Directory.TryLookup(tenant, out _).ShouldBeFalse();

        var missesBefore = cluster.Directory.Misses;
        var found = await cluster.Directory.LookupAsync(tenant);

        found.GetValueOrThrow().Slug.ShouldBe("dir-miss");
        cluster.Directory.Misses.ShouldBe(missesBefore + 1, "the miss is measured.");

        // And the answer is absorbed, so the same miss does not happen twice.
        cluster.Directory.TryLookup(tenant, out var absorbed).ShouldBeTrue();
        absorbed!.Slug.ShouldBe("dir-miss");
    }

    [Fact]
    public async Task ADeltaCarriesOnlyWhatChangedAfterTheCallersCursor() {
        var directory = cluster.DirectoryGrain();

        // One write first, so the cursor below is genuinely non-zero: GetDeltaAsync(0) means "I
        // have nothing, send me everything" and would report a full snapshot however few entries
        // there are.
        await Register(Tenant(80), "dir-delta-seed");

        var before = (await directory.GetDeltaAsync(0)).GetValueOrThrow().Version;
        before.ShouldBeGreaterThan(0);

        await Register(Tenant(8), "dir-delta-a");
        await Register(Tenant(9), "dir-delta-b");

        var delta = (await directory.GetDeltaAsync(before)).GetValueOrThrow();

        delta.IsFullSnapshot.ShouldBeFalse();
        delta.Entries.Select(x => x.Slug).ShouldBe(["dir-delta-a", "dir-delta-b"], true);
        delta.Version.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task AFirstReadGetsTheWholeDirectory() {
        await Register(Tenant(10), "dir-full");

        var full = (await cluster.DirectoryGrain().GetDeltaAsync(0)).GetValueOrThrow();

        full.IsFullSnapshot.ShouldBeTrue();
        full.Entries.ShouldContain(x => x.Slug == "dir-full");
        full.Entries.Count.ShouldBe((await cluster.DirectoryGrain().CountAsync()).GetValueOrThrow());
    }

    [Fact]
    public async Task TheDirectorySurvivesItsOwnGrainDyingBecauseItIsDurable() {
        var tenant = Tenant(11);
        await Register(tenant, "dir-durable");

        await cluster.DirectoryGrain().DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        (await cluster.DirectoryGrain().LookupAsync(tenant)).GetValueOrThrow()
            .Slug
            .ShouldBe("dir-durable");
    }

    [Fact]
    public async Task AStatusChangeAdvancesTheVersionSoCachesSeeIt() {
        var tenant = Tenant(12);
        var registered = await Register(tenant, "dir-status");

        var suspended = (await cluster.DirectoryGrain()
            .SetStatusAsync(tenant, TenantStatus.Suspended)).GetValueOrThrow();

        suspended.Status.ShouldBe(TenantStatus.Suspended);
        suspended.DirectoryVersion.ShouldBeGreaterThan(registered.DirectoryVersion);

        var delta = (await cluster.DirectoryGrain().GetDeltaAsync(registered.DirectoryVersion))
            .GetValueOrThrow();

        delta.Entries.ShouldContain(x => x.TenantId == tenant && x.Status == TenantStatus.Suspended);
    }

    [Fact]
    public async Task AnUnknownTenantIsNotFoundRatherThanEmpty() {
        var missing = await cluster.DirectoryGrain().LookupAsync(Tenant(999));

        missing.IsFailure.ShouldBeTrue();
        missing.Error!.Code.ShouldBe(ErrorCode.TenantNotFound);

        await Task.CompletedTask;
    }

    static Guid Tenant(int n) => TenancyCluster.Tenant(11_000 + n);

    async Task<TenantDirectoryEntry> Register(Guid tenant, string slug) {
        var assignment = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        var registered = await cluster.DirectoryGrain()
            .RegisterAsync(
                new() {
                    TenantId = tenant,
                    Slug = slug,
                    HomeRegion = "eu-central",
                    HotShard = assignment.HotHashTag,
                    DurableShard = assignment.DurableShard,
                    Status = TenantStatus.Active
                }
            );

        registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);
        return registered.GetValueOrThrow();
    }
}
