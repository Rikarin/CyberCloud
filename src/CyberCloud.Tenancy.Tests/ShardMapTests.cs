using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Shards;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/05 § The shard map: <b>"Assignment is at tenant creation and it is permanent …
///     There is no automatic rebalancing, and that is a decision rather than an omission."</b>
/// </summary>
/// <remarks>
///     <para>
///         <c>StaticShardMapCache</c> documents four stubbed things, and the second is the sharp
///         one: <i>"Adding a shard here re-places existing tenants. <c>hash mod n</c> moves roughly
///         <c>1 - 1/n</c> of tenants when <c>n</c> changes, and docs/plan/05 § The shard map says
///         flatly that a tenant's durable state is never moved."</i> These tests are the proof that
///         the real implementation does not.
///     </para>
///     <para>
///         ⚠ Two of them run against a bare <see cref="GrainBackedShardMapCache" /> rather than the
///         silo's, because the hazard is about what happens when the shard <i>list</i> changes and
///         the silo's list is fixed at start. The grain-level tests use the real grain in the real
///         cluster.
///     </para>
/// </remarks>
/// <remarks>
///     ⚠ <b>Its own cluster, and that is not fastidiousness.</b> Several tests here add shard ids to
///     the map that have no PostgreSQL server behind them, which is the only way to exercise "what
///     happens when the shard list grows". The refresher pushes that list into the silo's
///     <see cref="GrainBackedShardMapCache" /> within seconds, after which an <i>unassigned</i>
///     tenant may hash onto a shard with no connection string. In a shared cluster that would
///     corrupt every other test's tenants — and, notably, it would do so by exactly the mechanism
///     these tests exist to rule out. Isolating them is the honest way to keep the hazard testable
///     without making it real for everyone else.
/// </remarks>
[Collection(ShardMapSuite.Name)]
public sealed class ShardMapTests(TenancyCluster cluster)
{
    static Guid Tenant(int n) => TenancyCluster.Tenant(7000 + n);

    [Fact]
    public async Task ReAssigningATenantReturnsItsOriginalAssignment()
    {
        var map = cluster.ShardMapGrain();
        var tenant = Tenant(1);

        var first = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();
        var second = (await map.AssignAsync(tenant, "us-east")).GetValueOrThrow();

        second.ShouldBe(
            first,
            "assignment is permanent — even the region on the second call is ignored, because the "
            + "record is the record.");
    }

    [Fact]
    public async Task AddingAShardDoesNotMoveASingleAlreadyAssignedTenant()
    {
        // ⚠ THE HAZARD StaticShardMapCache DOCUMENTS, AIMED AT THE REAL IMPLEMENTATION.
        var map = cluster.ShardMapGrain();

        var before = new Dictionary<Guid, string>();
        for (var i = 100; i < 140; i++)
        {
            var tenant = Tenant(i);
            before[tenant] = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow()
                .DurableShard;
        }

        // They really did spread, or this test proves nothing about hashing.
        before.Values.Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThan(1);

        // Capacity is added at the front — docs/plan/05 § The shard map.
        await AddShardsAsync("durable-02", "durable-03");

        foreach (var (tenant, shard) in before)
        {
            (await map.GetAssignmentAsync(tenant)).GetValueOrThrow().DurableShard.ShouldBe(
                shard,
                $"tenant {tenant:D} moved when a shard was added. hash-mod-n would have moved about "
                + "half of them; a recorded assignment moves none.");

            (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow().DurableShard
                .ShouldBe(shard);
        }
    }

    [Fact]
    public async Task ANewTenantAfterTheShardListGrowsMayLandOnANewShard()
    {
        // The other side of the same coin: capacity added at the front is capacity that gets used.
        // Without this, "assignment is permanent" could be satisfied by never placing anywhere new.
        var map = cluster.ShardMapGrain();

        await AddShardsAsync("durable-02", "durable-03");

        var placements = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 200; i < 260; i++)
        {
            placements.Add((await map.AssignAsync(Tenant(i), "eu-central")).GetValueOrThrow()
                .DurableShard);
        }

        placements.ShouldContain("durable-02");
    }

    [Fact]
    public async Task AShardTakenOutOfTheRotationKeepsItsTenantsAndStopsTakingNewOnes()
    {
        // docs/plan/05 § The shard map: "at which point the answer is to stop assigning new tenants
        // to it, which costs nothing."
        var map = cluster.ShardMapGrain();

        await AddShardsAsync("durable-02", "durable-03");

        var resident = Tenant(300);
        var shard = (await map.AssignAsync(resident, "eu-central")).GetValueOrThrow().DurableShard;

        (await map.SetAcceptingNewTenantsAsync(shard, false)).IsSuccess.ShouldBeTrue();

        try
        {
            // The resident is untouched.
            (await map.GetAssignmentAsync(resident)).GetValueOrThrow().DurableShard.ShouldBe(shard);

            // And nothing new lands there.
            for (var i = 400; i < 440; i++)
            {
                (await map.AssignAsync(Tenant(i), "eu-central")).GetValueOrThrow().DurableShard
                    .ShouldNotBe(shard);
            }
        }
        finally
        {
            // Put it back: the tests in this class share one map grain and xUnit does not order
            // them, so a drained shard left behind would be a different test's flake.
            await map.SetAcceptingNewTenantsAsync(shard, true);
        }
    }

    [Fact]
    public async Task AShardCannotBeRemovedFromTheMap()
    {
        // Removing one would orphan every tenant recorded against it: their rows are in that
        // database and nothing would know to look there.
        var map = cluster.ShardMapGrain();

        // Built from the map's own current list, because the other tests in this class add shards
        // and xUnit does not order them. Asserting against a hard-coded list would make this test
        // pass or fail on execution order rather than on the property.
        await AddShardsAsync("durable-99");

        var refused = await map.ConfigureShardsAsync([TenancyCluster.ShardA]);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("SetAcceptingNewTenantsAsync");
    }

    [Fact]
    public async Task PinAsyncIsNotImplementedAndSaysSoWithTheDocumentInTheMessage()
    {
        // docs/plan/05 § The shard map budgets PinAsync at 0.5 EM in M2. The signature exists
        // because the document declares it; the body does not, because the map edit without the
        // quiesce/copy/flip/un-quiesce would repoint a live tenant at an empty database.
        var map = cluster.ShardMapGrain();

        var thrown = await Should.ThrowAsync<Exception>(
            () => map.PinAsync(Tenant(500), TenancyCluster.ShardB, null));

        thrown.ToString().ShouldContain("docs/plan/05");
        thrown.ToString().ShouldContain("quiesce");
    }

    [Fact]
    public async Task TheMapSurvivesItsOwnGrainDyingBecauseItIsDurable()
    {
        var map = cluster.ShardMapGrain();
        var tenant = Tenant(600);

        var assigned = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();

        await map.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        (await cluster.ShardMapGrain().GetAssignmentAsync(tenant)).GetValueOrThrow()
            .ShouldBe(assigned);
    }

    [Fact]
    public async Task TheCacheAgreesWithTheGrainOnceItHasRefreshed()
    {
        var tenant = Tenant(700);
        var assigned = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        await cluster.ShardMapRefresher.RefreshAsync(TestContext.Current.CancellationToken);

        cluster.ShardMap.DurableShardFor(TenancyCluster.Id(tenant))
            .ShouldBe(assigned.DurableShard);
    }

    [Fact]
    public async Task TheCacheAndTheGrainAgreeOnAnUnassignedTenantToo()
    {
        // ⚠ The window that would otherwise be a split brain: a tenant touched before its assignment
        // has reached this silo. The cache falls back to the deterministic hash and the grain
        // records the same shard, so there is no shard on which a write could land and a read could
        // miss.
        var tenant = Tenant(800);

        await cluster.ShardMapRefresher.RefreshAsync(TestContext.Current.CancellationToken);
        var predicted = cluster.ShardMap.DurableShardFor(TenancyCluster.Id(tenant));

        var recorded = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow().DurableShard;

        recorded.ShouldBe(predicted);
    }

    [Fact]
    public void TheCacheNeverRePlacesARecordedTenantWhenTheShardListGrows()
    {
        // The stub's limit 2, aimed at the cache rather than at the grain. A bare cache, given an
        // assignment and then a longer shard list, must still answer with the recorded shard.
        var map = BareCache();
        var tenant = Tenant(900);

        map.Apply(new ShardMapSnapshot
        {
            Version = 1,
            DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
            Assignments =
            [
                new ShardAssignment
                {
                    TenantId = tenant, DurableShard = TenancyCluster.ShardB, Version = 1,
                },
            ],
        });

        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(TenancyCluster.ShardB);

        // Four more shards arrive. hash mod n would move roughly 1 - 1/n of everything.
        map.Apply(new ShardMapSnapshot
        {
            Version = 2,
            DurableShards =
            [
                TenancyCluster.ShardA, TenancyCluster.ShardB,
                "durable-02", "durable-03", "durable-04", "durable-05",
            ],
        });

        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(
            TenancyCluster.ShardB, "a recorded assignment is never recomputed.");
    }

    [Fact]
    public void TheStaticStubDOESRePlaceATenantWhenAShardIsAddedWhichIsWhyItIsAStub()
    {
        // The control for the test above. If this ever stops being true, StaticShardMapCache has
        // been fixed and its remarks — and the test above — need rewriting rather than deleting.
        var moved = 0;
        var total = 0;

        for (var i = 0; i < 200; i++)
        {
            var tenant = TenancyCluster.Id(TenancyCluster.Tenant(9000 + i));
            total++;

            if (!string.Equals(
                    StaticOver(["a", "b"]).DurableShardFor(tenant),
                    StaticOver(["a", "b", "c"]).DurableShardFor(tenant),
                    StringComparison.Ordinal))
            {
                moved++;
            }
        }

        moved.ShouldBeGreaterThan(
            total / 4,
            "hash mod n moves roughly 1 - 1/n of tenants when n changes; the stub's own remarks say "
            + "so, and this is the measurement.");
    }

    [Fact]
    public void AConfiguredPinBeatsEverythingIncludingTheRecordedAssignment()
    {
        // The read-only half of PinAsync, which does work — DurableTierOptions.Pins.
        var options = new CyberCloudStorageOptions();
        options.Durable.Shards[TenancyCluster.ShardA] = "Host=unused";
        options.Durable.Shards[TenancyCluster.ShardB] = "Host=unused";

        var tenant = Tenant(1000);
        options.Durable.Pins[TenancyCluster.Id(tenant)] = TenancyCluster.ShardB;

        var map = new GrainBackedShardMapCache(options);

        map.Apply(new ShardMapSnapshot
        {
            Version = 1,
            DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
            Assignments =
            [
                new ShardAssignment
                {
                    TenantId = tenant, DurableShard = TenancyCluster.ShardA, Version = 1,
                },
            ],
        });

        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(
            TenancyCluster.ShardB, "an operator pin is the operator's word.");
    }

    [Fact]
    public void AnOlderSnapshotIsDiscardedRatherThanAppliedBackwards()
    {
        var map = BareCache();
        var tenant = Tenant(1100);

        map.Apply(new ShardMapSnapshot
        {
            Version = 5,
            DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
            Assignments =
            [
                new ShardAssignment { TenantId = tenant, DurableShard = TenancyCluster.ShardB, Version = 5 },
            ],
        });

        map.Apply(new ShardMapSnapshot { Version = 2, IsFullSnapshot = true }).ShouldBeFalse();

        map.Version.ShouldBe(5);
        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(TenancyCluster.ShardB);
    }

    [Fact]
    public void TheCacheVersionAdvancesWhichIsTheThirdThingTheStubStubbed()
    {
        var map = BareCache();

        map.Version.ShouldBe(0);
        map.Apply(new ShardMapSnapshot { Version = 11, DurableShards = [TenancyCluster.ShardA] });
        map.Version.ShouldBe(11);
    }

    /// <summary>
    ///     Adds shards to the map without assuming what is already in it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>ConfigureShardsAsync</c> is a whole-list operation and refuses a list that drops a
    ///     known shard — that refusal is the point of
    ///     <see cref="AShardCannotBeRemovedFromTheMap" />. The tests in this class each add
    ///     different shards and xUnit does not order them, so every caller has to union with what is
    ///     there rather than pass a literal.
    /// </remarks>
    async Task AddShardsAsync(params string[] shards)
    {
        var map = cluster.ShardMapGrain();
        var known = (await map.GetSnapshotAsync(0)).GetValueOrThrow().DurableShards;

        (await map.ConfigureShardsAsync([.. known.Union(shards, StringComparer.Ordinal)]))
            .IsSuccess.ShouldBeTrue();
    }

    static GrainBackedShardMapCache BareCache()
    {
        var options = new CyberCloudStorageOptions();
        options.Durable.Shards[TenancyCluster.ShardA] = "Host=unused";
        options.Durable.Shards[TenancyCluster.ShardB] = "Host=unused";

        return new GrainBackedShardMapCache(options);
    }

    static StaticShardMapCache StaticOver(IEnumerable<string> shards)
    {
        var options = new CyberCloudStorageOptions();
        foreach (var shard in shards)
        {
            options.Durable.Shards[shard] = "Host=unused";
        }

        return new StaticShardMapCache(options);
    }
}
