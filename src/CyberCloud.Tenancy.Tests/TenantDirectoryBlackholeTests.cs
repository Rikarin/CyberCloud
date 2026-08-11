using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/23's chaos-invariant 5, with real infrastructure: <b>"If the global cluster is
///     unreachable, no <i>new</i> tenants can be created and no directory changes propagate — but
///     every existing tenant keeps working from cache, in every region, indefinitely."</b>
///     (docs/plan/05 § The tenant directory.)
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The blackhole is a stopped PostgreSQL container, not a stubbed grain client.</b>
///         The fixture runs a dedicated <c>platform-00</c> shard that carries every null-tenant
///         grain — the tenant directory and the shard map — and this class stops it. That makes the
///         global directory genuinely unreachable while both tenant shards are genuinely fine, which
///         is the exact blast radius the document claims. A test that swapped in a throwing
///         <c>IGrainFactory</c> would be asserting against its own stub, and would keep passing if
///         the real failure looked different (it does: the real one is an
///         <c>OrleansException</c> wrapping an <c>NpgsqlException</c> raised inside
///         <c>OnActivateAsync</c>).
///     </para>
///     <para>
///         ⚠ <b>This class runs in its own collection</b> — <see cref="DestructiveTenancySuite" /> —
///         because stopping a container is not something a test may do to a fixture other tests are
///         still using. It is the only class in that collection and every test in it is ordered by
///         construction: everything before the blackhole is seeded in
///         <see cref="SeedThenBlackholeThenAssertAsync" />, one test, in order. Splitting it into
///         several <c>[Fact]</c>s would make it depend on xUnit's unspecified execution order, which
///         is exactly the kind of test that fails once a month for no reason.
///     </para>
/// </remarks>
[Collection(DestructiveTenancySuite.Name)]
public sealed class TenantDirectoryBlackholeTests(TenancyCluster cluster)
{
    static Guid Existing => TenancyCluster.Tenant(21_001);

    static Guid AlsoExisting => TenancyCluster.Tenant(21_002);

    static Guid BrandNew => TenancyCluster.Tenant(21_003);

    [Fact]
    public async Task SeedThenBlackholeThenAssertAsync()
    {
        var token = TestContext.Current.CancellationToken;

        // ── Before ────────────────────────────────────────────────────────────────────────────
        await RegisterAsync(Existing, "blackhole-a");
        await RegisterAsync(AlsoExisting, "blackhole-b");

        // The mirrors pull once, the way a silo does at start.
        await cluster.Directory.RefreshAsync(token);
        await cluster.ShardMapRefresher.RefreshAsync(token);

        cluster.Directory.Count.ShouldBeGreaterThanOrEqualTo(2);
        var mapVersionBefore = cluster.ShardMap.Version;
        var shardOfExisting = cluster.ShardMap.DurableShardFor(TenancyCluster.Id(Existing));

        // Two existing tenants with real state on the real tenant shards.
        (await cluster.TenantGrain(Existing).CreateAsync("blackhole-a", "A", "eu-central"))
            .IsSuccess.ShouldBeTrue();
        (await cluster.TenantGrain(AlsoExisting).CreateAsync("blackhole-b", "B", "eu-central"))
            .IsSuccess.ShouldBeTrue();

        // ── The blackhole ─────────────────────────────────────────────────────────────────────
        await cluster.BlackholeThePlatformShardAsync(token);

        // ⚠ And the two platform grains are deactivated, deliberately.
        //
        // Stopping the database alone is not enough to make the global cluster "unreachable" from
        // this silo: the directory grain is already ACTIVATED, its whole state is in memory, and it
        // will happily keep answering reads from there. That is a real and rather reassuring
        // property — but it is not the failure docs/plan/05 § The tenant directory is about, which
        // is a global cluster that is *gone*. Dropping the activations means the next call has to
        // re-read PostgreSQL, and PostgreSQL is not there. Without this the test would be asserting
        // that one activation survives, which nobody doubted.
        await cluster.DirectoryGrain().DeactivateAsync();
        await cluster.ShardMapGrain().DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);

        // 1. Reads for an existing tenant never leave the process, so they are unaffected. Not
        //    "eventually" and not "for a while" — the snapshot has no TTL at all.
        cluster.Directory.TryLookup(Existing, out var cached).ShouldBeTrue();
        cached!.Slug.ShouldBe("blackhole-a");

        (await cluster.Directory.LookupAsync(Existing)).GetValueOrThrow().Slug
            .ShouldBe("blackhole-a");
        (await cluster.Directory.LookupAsync(AlsoExisting)).GetValueOrThrow().Slug
            .ShouldBe("blackhole-b");

        // 2. The shard map still routes every tenant it knows about.
        cluster.ShardMap.DurableShardFor(TenancyCluster.Id(Existing)).ShouldBe(shardOfExisting);

        // 3. ⚠ THE POINT: existing tenants keep WORKING, not merely keep resolving. Real grain
        //    calls, real reads and real writes against the real tenant shards, with the global
        //    directory's database stopped.
        (await cluster.TenantGrain(Existing).GetAsync()).GetValueOrThrow().Slug
            .ShouldBe("blackhole-a");

        (await cluster.TenantGrain(Existing).AddSubscriptionAsync(Guid.NewGuid())).IsSuccess
            .ShouldBeTrue("a write for an existing tenant is unaffected by the global cluster.");

        var subscription = Guid.NewGuid();
        (await cluster.SubscriptionGrain(AlsoExisting, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(AlsoExisting, subscription)
            .CreateResourceGroupAsync("prod-rg", "eu-central")).IsSuccess.ShouldBeTrue();

        var address = new ResourceId(
            AlsoExisting, subscription, "prod-rg",
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"), "db-1", Guid.NewGuid());

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue("the whole two-phase create runs with the global cluster down.");
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        (await cluster.QuotaGrain(AlsoExisting, subscription)
            .TryReserveAsync(QuotaMeter.Vcpu, 2m, Guid.NewGuid())).IsSuccess.ShouldBeTrue();

        // 4. And what genuinely IS lost: no NEW tenant can be resolved, and no directory change
        //    propagates. Named, rather than glossed.
        var unknown = await cluster.Directory.LookupAsync(BrandNew);
        unknown.IsFailure.ShouldBeTrue(
            "a tenant that is not in the local snapshot cannot be resolved while the global "
            + "directory is unreachable — docs/plan/05 § The tenant directory says exactly this.");
        unknown.Error!.Code.ShouldBe(ErrorCode.TenantNotFound);
        unknown.Error.Message.ShouldContain("unreachable");

        // 5. A refresh fails, is counted, and does NOT damage the snapshot. This is the property
        //    that makes "indefinitely" true: a failing refresh must never clear the cache.
        var failuresBefore = cluster.Directory.FallbackFailures;
        (await cluster.Directory.RefreshAsync(token)).ShouldBeFalse();
        cluster.Directory.FallbackFailures.ShouldBeGreaterThan(failuresBefore);
        cluster.Directory.Count.ShouldBeGreaterThanOrEqualTo(2, "a failed refresh kept the snapshot.");

        (await cluster.ShardMapRefresher.RefreshAsync(token)).ShouldBeFalse();
        cluster.ShardMapRefresher.Failures.ShouldBeGreaterThan(0);
        cluster.ShardMap.Version.ShouldBe(mapVersionBefore, "a failed refresh did not roll back.");
        cluster.ShardMap.DurableShardFor(TenancyCluster.Id(Existing)).ShouldBe(shardOfExisting);

        // 6. Creating a new tenant in the directory fails, which is the documented consequence —
        //    "no new tenants can be created".
        await Should.ThrowAsync<Exception>(() => RegisterAsync(BrandNew, "blackhole-new"));

        // 7. …and after all that, the existing tenants are still fine. Asserted last so that the
        //    failures above are known not to have broken them.
        (await cluster.TenantGrain(Existing).GetAsync()).GetValueOrThrow().Slug
            .ShouldBe("blackhole-a");
        (await cluster.Directory.LookupAsync(AlsoExisting)).IsSuccess.ShouldBeTrue();
    }

    async Task RegisterAsync(Guid tenant, string slug)
    {
        var assignment = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        var registered = await cluster.DirectoryGrain().RegisterAsync(new TenantDirectoryEntry
        {
            TenantId = tenant,
            Slug = slug,
            HomeRegion = "eu-central",
            HotShard = assignment.HotHashTag,
            DurableShard = assignment.DurableShard,
            Status = TenantStatus.Active,
        });

        registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);
    }
}
