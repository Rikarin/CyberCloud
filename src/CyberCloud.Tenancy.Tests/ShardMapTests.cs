using CyberCloud.Core.Resources;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Shards;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/05 § The shard map:
///     <b>
///         "Assignment is at tenant creation and it is permanent …
///         There is no automatic rebalancing, and that is a decision rather than an omission."
///     </b>
/// </summary>
/// <remarks>
///     <para>
///         <c>StaticShardMapCache</c> documents four stubbed things, and the second is the sharp
///         one:
///         <i>
///             "Adding a shard here re-places existing tenants. <c>hash mod n</c> moves roughly
///             <c>1 - 1/n</c> of tenants when <c>n</c> changes, and docs/plan/05 § The shard map says
///             flatly that a tenant's durable state is never moved."
///         </i>
///         These tests are the proof that
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
public sealed class ShardMapTests(TenancyCluster cluster) {
    [Fact]
    public async Task ReAssigningATenantReturnsItsOriginalAssignment() {
        var map = cluster.ShardMapGrain();
        var tenant = Tenant(1);

        var first = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();
        var second = (await map.AssignAsync(tenant, "us-east")).GetValueOrThrow();

        second.ShouldBe(
            first,
            "assignment is permanent — even the region on the second call is ignored, because the "
            + "record is the record."
        );
    }

    [Fact]
    public async Task AddingAShardDoesNotMoveASingleAlreadyAssignedTenant() {
        // ⚠ THE HAZARD StaticShardMapCache DOCUMENTS, AIMED AT THE REAL IMPLEMENTATION.
        var map = cluster.ShardMapGrain();

        var before = new Dictionary<Guid, string>();
        for (var i = 100; i < 140; i++) {
            var tenant = Tenant(i);
            before[tenant] = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow()
                .DurableShard;
        }

        // They really did spread, or this test proves nothing about hashing.
        before.Values.Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThan(1);

        // Capacity is added at the front — docs/plan/05 § The shard map.
        await AddShardsAsync("durable-02", "durable-03");

        foreach (var (tenant, shard) in before) {
            (await map.GetAssignmentAsync(tenant)).GetValueOrThrow()
                .DurableShard.ShouldBe(
                    shard,
                    $"tenant {tenant:D} moved when a shard was added. hash-mod-n would have moved about "
                    + "half of them; a recorded assignment moves none."
                );

            (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow()
                .DurableShard
                    .ShouldBe(shard);
        }
    }

    [Fact]
    public async Task ANewTenantAfterTheShardListGrowsMayLandOnANewShard() {
        // The other side of the same coin: capacity added at the front is capacity that gets used.
        // Without this, "assignment is permanent" could be satisfied by never placing anywhere new.
        var map = cluster.ShardMapGrain();

        await AddShardsAsync("durable-02", "durable-03");

        var placements = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 200; i < 260; i++) {
            placements.Add(
                (await map.AssignAsync(Tenant(i), "eu-central")).GetValueOrThrow()
                    .DurableShard
            );
        }

        placements.ShouldContain("durable-02");
    }

    [Fact]
    public async Task AShardTakenOutOfTheRotationKeepsItsTenantsAndStopsTakingNewOnes() {
        // docs/plan/05 § The shard map: "at which point the answer is to stop assigning new tenants
        // to it, which costs nothing."
        var map = cluster.ShardMapGrain();

        await AddShardsAsync("durable-02", "durable-03");

        var resident = Tenant(300);
        var shard = (await map.AssignAsync(resident, "eu-central")).GetValueOrThrow().DurableShard;

        (await map.SetAcceptingNewTenantsAsync(shard, false)).IsSuccess.ShouldBeTrue();

        try {
            // The resident is untouched.
            (await map.GetAssignmentAsync(resident)).GetValueOrThrow().DurableShard.ShouldBe(shard);

            // And nothing new lands there.
            for (var i = 400; i < 440; i++) {
                (await map.AssignAsync(Tenant(i), "eu-central")).GetValueOrThrow()
                    .DurableShard
                        .ShouldNotBe(shard);
            }
        } finally {
            // Put it back: the tests in this class share one map grain and xUnit does not order
            // them, so a drained shard left behind would be a different test's flake.
            await map.SetAcceptingNewTenantsAsync(shard, true);
        }
    }

    [Fact]
    public async Task AShardCannotBeRemovedFromTheMap() {
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

    // ── Pinning — the placement half of docs/plan/05 § The shard map's PinAsync (issue #39) ────

    /// <summary>
    ///     ⚠ A pin placed before the tenant exists is the assignment <c>AssignAsync</c> then finds,
    ///     whatever the hash would have said — and the assign fills in the region the pin could not
    ///     carry.
    /// </summary>
    [Fact]
    public async Task APinBeforeCreationIsTheAssignmentTheCreateFinds() {
        var map = cluster.ShardMapGrain();
        await AddShardsAsync("durable-02", "durable-03");

        // Find a tenant the hash would NOT put on durable-03, so the pin is doing the placing.
        var tenant = Tenant(500);
        var hashed = (await map.AssignAsync(Tenant(501), "eu-central")).GetValueOrThrow().DurableShard;
        var chosen = hashed == "durable-03" ? "durable-02" : "durable-03";

        (await map.PinAsync(tenant, chosen, null)).IsSuccess.ShouldBeTrue();

        var assigned = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();

        assigned.DurableShard.ShouldBe(chosen, "the create placed the tenant somewhere other than its pin");
        assigned.Region.ShouldBe("eu-central", "the assign did not complete the region the pin could not carry");
        assigned.HotHashTag.ShouldBe(StaticShardMapCache.HotTagPrefix + TenancyCluster.Id(tenant).Replace("-", "", StringComparison.Ordinal));

        // Idempotent: the re-driven create carries the same pin and finds it.
        (await map.PinAsync(tenant, chosen, null)).IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>THE REFUSAL THAT IS THE POINT.</b> A pin that would move an assigned tenant is the
    ///     move docs/plan/05 § The shard map describes as quiesce, copy, flip, un-quiesce; only the
    ///     flip is a map edit and flipping alone repoints a live tenant at an empty database. The
    ///     move is M3 and the refusal names the four steps.
    /// </summary>
    [Fact]
    public async Task APinThatWouldMoveAnAssignedTenantIsRefusedByName() {
        var map = cluster.ShardMapGrain();
        await AddShardsAsync("durable-02", "durable-03");

        var tenant = Tenant(510);
        var resident = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();
        var elsewhere = resident.DurableShard == "durable-03" ? "durable-02" : "durable-03";

        var refused = await map.PinAsync(tenant, elsewhere, null);

        refused.IsFailure.ShouldBeTrue("a pin moved a tenant that already had durable state somewhere");
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("docs/plan/05");
        refused.Error.Message.ShouldContain("quiesce");
        refused.Error.Message.ShouldContain("M3");

        (await map.GetAssignmentAsync(tenant)).GetValueOrThrow()
            .DurableShard.ShouldBe(resident.DurableShard, "the refused pin moved the map anyway");
    }

    [Fact]
    public async Task APinToAnUnknownOrDrainedShardIsRefused() {
        var map = cluster.ShardMapGrain();
        await AddShardsAsync("durable-02", "durable-03");

        var unknown = await map.PinAsync(Tenant(520), "durable-nowhere", null);
        unknown.IsFailure.ShouldBeTrue();
        unknown.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await map.SetAcceptingNewTenantsAsync("durable-03", false)).IsSuccess.ShouldBeTrue();

        try {
            var drained = await map.PinAsync(Tenant(521), "durable-03", null);
            drained.IsFailure.ShouldBeTrue("a pin bypassed the placement rotation");
            drained.Error!.Code.ShouldBe(ErrorCode.Conflict);
            drained.Error.Message.ShouldContain("SetAcceptingNewTenantsAsync");
        } finally {
            (await map.SetAcceptingNewTenantsAsync("durable-03", true)).IsSuccess.ShouldBeTrue();
        }

        // Nothing was recorded for either.
        (await map.GetAssignmentAsync(Tenant(520))).IsFailure.ShouldBeTrue();
        (await map.GetAssignmentAsync(Tenant(521))).IsFailure.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ A hot hash-tag override is refused rather than recorded: the cache reads the configured
    ///     overrides and never the map, so a recorded one would be a fact nothing acts on.
    /// </summary>
    [Fact]
    public async Task AHotOverrideIsRefusedBecauseTheMapCannotDeliverIt() {
        var map = cluster.ShardMapGrain();

        var refused = await map.PinAsync(Tenant(530), TenancyCluster.ShardB, "cc:t:custom");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("HashTagOverrides");
        (await map.GetAssignmentAsync(Tenant(530))).IsFailure.ShouldBeTrue("a refused pin left an assignment");
    }

    /// <summary>
    ///     ⚠ <b>THE SPLIT THE REVIEW OF ISSUE #39 FOUND, AND THE STEP THAT CLOSES IT.</b> A pin is a
    ///     record in the map; the silo that activates the tenant's first grain routes its state
    ///     through its own mirror, which for a tenant it has not heard of falls back to the hash — the
    ///     shard the pin exists to disagree with. This drives the real storage layer: the mirror's
    ///     answer before and after <c>ShardMapPropagation.ConfirmAsync</c>, and then which PostgreSQL
    ///     server the tenant grain's row actually landed in.
    /// </summary>
    /// <remarks>
    ///     One silo, so "every silo" is this one; what the multi-silo case adds is Orleans'
    ///     <c>IManagementGrain</c> fan-out, which is the runtime's and not this repository's to
    ///     prove. The pin is to a REAL shard because the test reads rows back; the hash fallback may
    ///     name one of the shard ids other tests added to the map without a server behind it, which
    ///     is why the "not here" assertion is against the other real shard rather than the hashed one.
    /// </remarks>
    [Fact]
    public async Task APinnedTenantIsPlacedOnItsPinOnceTheMirrorHasConfirmedIt() {
        var token = TestContext.Current.CancellationToken;
        var map = cluster.ShardMapGrain();
        var tenant = Tenant(540);
        var id = TenancyCluster.Id(tenant);

        // What this silo's storage layer would do for the tenant right now, with no record: the hash.
        var hashed = cluster.ShardMap.DurableShardFor(id);
        var pinned = hashed == TenancyCluster.ShardA ? TenancyCluster.ShardB : TenancyCluster.ShardA;
        var other = pinned == TenancyCluster.ShardA ? TenancyCluster.ShardB : TenancyCluster.ShardA;

        (await map.PinAsync(tenant, pinned, null)).IsSuccess.ShouldBeTrue();
        var assigned = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();
        assigned.DurableShard.ShouldBe(pinned);

        // The record is in the map and NOT in this silo's mirror — the gap the timer would close in
        // fifteen seconds, and the gap a tenant grain activated now would write its first row into.
        cluster.ShardMap.DurableShardFor(id).ShouldBe(
            hashed,
            "the mirror learned the pin without a refresh, so this test no longer exercises the gap"
        );

        var before = cluster.ShardMapRefresher.Commands;
        var confirmed = await ShardMapPropagation.ConfirmAsync(cluster.Grains, assigned);

        confirmed.IsSuccess.ShouldBeTrue(confirmed.Error?.Message);
        cluster.ShardMapRefresher.Commands.ShouldBe(before + 1, "the fan-out did not reach this silo's mirror");
        cluster.ShardMap.DurableShardFor(id).ShouldBe(pinned, "the mirror was refreshed and still does not resolve the pin");

        // And the rows land on the pin — the storage provider for this tenant is built on this silo
        // now, for the first time, from the mirror that has the record.
        var grain = cluster.For(tenant).GetGrain<ITenantGrain>(GrainKeys.Tenant(tenant));
        var created = await grain.CreateAsync("pinned-540", "Pinned", "eu-central");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        // ⚠ The PHYSICAL key: Orleans.Multitenant prefixes the tenant, so GrainKeys.Tenant alone
        // matches no row — the same read CrossTenantAuthorizationTests makes.
        var physicalKey = grain.GetGrainId().Key.ToString()!;

        (await cluster.CountRowsAsync(pinned, physicalKey, token))
            .ShouldBe(1L, $"the pinned tenant's row is not on '{pinned}'");
        (await cluster.CountRowsAsync(other, physicalKey, token))
            .ShouldBe(0L, $"the pinned tenant has a row on '{other}' as well — the split");
    }

    [Fact]
    public async Task TheMapSurvivesItsOwnGrainDyingBecauseItIsDurable() {
        var map = cluster.ShardMapGrain();
        var tenant = Tenant(600);

        var assigned = (await map.AssignAsync(tenant, "eu-central")).GetValueOrThrow();

        await map.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        (await cluster.ShardMapGrain().GetAssignmentAsync(tenant)).GetValueOrThrow()
            .ShouldBe(assigned);
    }

    [Fact]
    public async Task TheCacheAgreesWithTheGrainOnceItHasRefreshed() {
        var tenant = Tenant(700);
        var assigned = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow();

        await cluster.ShardMapRefresher.RefreshAsync(TestContext.Current.CancellationToken);

        cluster.ShardMap.DurableShardFor(TenancyCluster.Id(tenant))
            .ShouldBe(assigned.DurableShard);
    }

    [Fact]
    public async Task TheCacheAndTheGrainAgreeOnAnUnassignedTenantToo() {
        // ⚠ The window that would otherwise be a split brain: a tenant touched before its assignment
        // has reached this silo. The cache falls back to the deterministic hash and the grain
        // records the same shard, so there is no shard on which a write could land and a read could
        // miss.
        var tenant = Tenant(800);

        await cluster.ShardMapRefresher.RefreshAsync(TestContext.Current.CancellationToken);
        var predicted = cluster.ShardMap.DurableShardFor(TenancyCluster.Id(tenant));

        var recorded = (await cluster.ShardMapGrain().AssignAsync(tenant, "eu-central"))
            .GetValueOrThrow()
            .DurableShard;

        recorded.ShouldBe(predicted);
    }

    [Fact]
    public void TheCacheNeverRePlacesARecordedTenantWhenTheShardListGrows() {
        // The stub's limit 2, aimed at the cache rather than at the grain. A bare cache, given an
        // assignment and then a longer shard list, must still answer with the recorded shard.
        var map = BareCache();
        var tenant = Tenant(900);

        map.Apply(
            new() {
                Version = 1,
                DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
                Assignments = [
                    new() { TenantId = tenant, DurableShard = TenancyCluster.ShardB, Version = 1 }
                ]
            }
        );

        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(TenancyCluster.ShardB);

        // Four more shards arrive. hash mod n would move roughly 1 - 1/n of everything.
        map.Apply(
            new() {
                Version = 2,
                DurableShards = [
                    TenancyCluster.ShardA, TenancyCluster.ShardB,
                    "durable-02", "durable-03", "durable-04", "durable-05"
                ]
            }
        );

        map.DurableShardFor(TenancyCluster.Id(tenant))
            .ShouldBe(TenancyCluster.ShardB, "a recorded assignment is never recomputed.");
    }

    [Fact]
    public void TheStaticStubDOESRePlaceATenantWhenAShardIsAddedWhichIsWhyItIsAStub() {
        // The control for the test above. If this ever stops being true, StaticShardMapCache has
        // been fixed and its remarks — and the test above — need rewriting rather than deleting.
        var moved = 0;
        var total = 0;

        for (var i = 0; i < 200; i++) {
            var tenant = TenancyCluster.Id(TenancyCluster.Tenant(9000 + i));
            total++;

            if (!string.Equals(
                    StaticOver(["a", "b"]).DurableShardFor(tenant),
                    StaticOver(["a", "b", "c"]).DurableShardFor(tenant),
                    StringComparison.Ordinal
                )) {
                moved++;
            }
        }

        moved.ShouldBeGreaterThan(
            total / 4,
            "hash mod n moves roughly 1 - 1/n of tenants when n changes; the stub's own remarks say "
            + "so, and this is the measurement."
        );
    }

    [Fact]
    public void AConfiguredPinBeatsEverythingIncludingTheRecordedAssignment() {
        // The read-only half of PinAsync, which does work — DurableTierOptions.Pins.
        var options = new CyberCloudStorageOptions();
        options.Durable.Shards[TenancyCluster.ShardA] = "Host=unused";
        options.Durable.Shards[TenancyCluster.ShardB] = "Host=unused";

        var tenant = Tenant(1000);
        options.Durable.Pins[TenancyCluster.Id(tenant)] = TenancyCluster.ShardB;

        var map = new GrainBackedShardMapCache(options);

        map.Apply(
            new() {
                Version = 1,
                DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
                Assignments = [
                    new() { TenantId = tenant, DurableShard = TenancyCluster.ShardA, Version = 1 }
                ]
            }
        );

        map.DurableShardFor(TenancyCluster.Id(tenant))
            .ShouldBe(TenancyCluster.ShardB, "an operator pin is the operator's word.");
    }

    [Fact]
    public void AnOlderSnapshotIsDiscardedRatherThanAppliedBackwards() {
        var map = BareCache();
        var tenant = Tenant(1100);

        map.Apply(
            new() {
                Version = 5,
                DurableShards = [TenancyCluster.ShardA, TenancyCluster.ShardB],
                Assignments = [
                    new() { TenantId = tenant, DurableShard = TenancyCluster.ShardB, Version = 5 }
                ]
            }
        );

        map.Apply(new() { Version = 2, IsFullSnapshot = true }).ShouldBeFalse();

        map.Version.ShouldBe(5);
        map.DurableShardFor(TenancyCluster.Id(tenant)).ShouldBe(TenancyCluster.ShardB);
    }

    [Fact]
    public void TheCacheVersionAdvancesWhichIsTheThirdThingTheStubStubbed() {
        var map = BareCache();

        map.Version.ShouldBe(0);
        map.Apply(new() { Version = 11, DurableShards = [TenancyCluster.ShardA] });
        map.Version.ShouldBe(11);
    }

    static Guid Tenant(int n) => TenancyCluster.Tenant(7000 + n);

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
    async Task AddShardsAsync(params string[] shards) {
        var map = cluster.ShardMapGrain();
        var known = (await map.GetSnapshotAsync(0)).GetValueOrThrow().DurableShards;

        (await map.ConfigureShardsAsync([.. known.Union(shards, StringComparer.Ordinal)]))
            .IsSuccess.ShouldBeTrue();
    }

    static GrainBackedShardMapCache BareCache() {
        var options = new CyberCloudStorageOptions();
        options.Durable.Shards[TenancyCluster.ShardA] = "Host=unused";
        options.Durable.Shards[TenancyCluster.ShardB] = "Host=unused";

        return new(options);
    }

    static StaticShardMapCache StaticOver(IEnumerable<string> shards) {
        var options = new CyberCloudStorageOptions();
        foreach (var shard in shards) {
            options.Durable.Shards[shard] = "Host=unused";
        }

        return new(options);
    }
}
