using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     <see cref="IMembershipIndexGrain" /> against real grains: the tuple store closes both
///     directions on every write and delete, a check reads the closure instead of walking it, a
///     crash on either side of the index leaves it no more permissive than the tuples, and the
///     sweeper finishes what the crash interrupted.
/// </summary>
/// <remarks>
///     <para>
///         <c>MembershipIndexPropertyTests</c> holds the closure algorithm to a brute force over
///         thousands of graphs in memory; this file pins what the in-memory store cannot — that
///         <c>TupleStoreGrain</c> actually calls the maintainer at the step its remarks say, that
///         <c>MembershipIndexGrain.ApplyAsync</c> applies a change the way the in-memory store does
///         and keeps it across a deactivation, that <c>CheckGrain</c> hands the evaluator a reader
///         over these grains, and that the ordering docs/plan/07 § The Leopard index now relies on
///         — index last on a write, first on a delete — holds when the write actually dies.
///     </para>
///     <para>
///         ⚠ <b>Each test owns a tenant</b>, for the reason every other class in this suite does.
///         Tenant indexes 600–609 are this file's; 600–608 are taken.
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class MembershipIndexGrainTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    static SubjectRef Member(string group) => SubjectRef.Userset(ObjectTypes.Group, group, Relations.Member);

    [Fact]
    public async Task AWriteClosesBothDirectionsAndADeleteRecomputesThem() {
        var tenant = AuthorizationCluster.Tenant(600);
        await NestAsync(tenant, "i1", 3);

        // Up from alice: every group on the chain, in one slice.
        var alice = await ReadAsync(tenant, Alice.Object);
        alice.UsersetsOf(string.Empty).ShouldBe([Member("i1-g1"), Member("i1-g2"), Member("i1-g3")], true);
        alice.SchemaVersion.ShouldBe(CyberCloudSchema.SchemaVersion);

        // Down from the top: the nested usersets and the one concrete member.
        var top = await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g3"));
        top.MembersOf(Relations.Member).ShouldBe([Member("i1-g2"), Member("i1-g1"), Alice], true);

        // ⚠ THE DELETE, THROUGH THE STORE. Cutting g2 out of g3 has to recompute g3's members from
        // the tuples, not merely drop g2: alice and g1 reached g3 through g2 and go with it.
        await cluster.RevokeAsync(tenant, "group:i1-g3#member@group:i1-g2#member");

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty)
            .ShouldBe([Member("i1-g1"), Member("i1-g2")], true);
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g3"))).MembersOf(Relations.Member).ShouldBeEmpty();
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g2"))).MembersOf(Relations.Member)
            .ShouldBe([Member("i1-g1"), Alice], true);
    }

    [Fact]
    public async Task TheClosureSurvivesADeactivationOfEverySliceInvolved() {
        var tenant = AuthorizationCluster.Tenant(601);
        await NestAsync(tenant, "i2", 3);

        foreach (var group in new[] { "i2-g1", "i2-g2", "i2-g3" }) {
            await cluster.Index(tenant, ObjectRef.Of(ObjectTypes.Group, group)).DeactivateAsync();
        }

        await cluster.Index(tenant, Alice.Object).DeactivateAsync();

        // The row came back from PostgreSQL — every collection on MembershipIndexState is
        // { get; set; }, which AuthorizationStateContractTests is the gate for and this is the proof.
        var alice = await ReadAsync(tenant, Alice.Object);
        alice.UsersetsOf(string.Empty).Count.ShouldBe(3);

        var top = await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i2-g3"));
        top.MembersOf(Relations.Member).Count.ShouldBe(3);
    }

    [Fact]
    public async Task ACheckAnswersANestedGroupFromTheIndexAndAFullyConsistentCheckStillWalks() {
        var tenant = AuthorizationCluster.Tenant(602);
        await NestAsync(tenant, "i3", 3);
        await cluster.WriteAsync(tenant, "resourceGroup:i3-rg#parent@subscription:i3-sub");
        await cluster.WriteAsync(tenant, "resource:i3-res#parent@resourceGroup:i3-rg");
        await cluster.WriteAsync(tenant, "resourceGroup:i3-rg#reader@group:i3-g3#member");

        var resource = ObjectRef.Of(ObjectTypes.Resource, "i3-res");

        // Indexed first: nothing is cached yet, so this is a walk over the index. FullyConsistent
        // second, because it never reads the cache the first one filled.
        var indexed = await cluster.Check(tenant, resource)
            .CheckAsync(Permissions.Read, Alice, Consistency.MinimizeLatency);
        indexed.GetValueOrThrow().Allowed.ShouldBeTrue();
        indexed.GetValueOrThrow().FromCache.ShouldBeFalse();

        var walked = await cluster.Check(tenant, resource)
            .CheckAsync(Permissions.Read, Alice, Consistency.FullyConsistent);
        walked.GetValueOrThrow().Allowed.ShouldBeTrue();
        walked.GetValueOrThrow().FromCache.ShouldBeFalse();

        // ⚠ THE INDEX DID THE WORK. The walk visits resource#read, resource#reader, and the
        // group#member triples for g3, g2 and g1 on the way to alice; the indexed check stops at
        // the first userset it meets, because alice's slice already says she is in it.
        indexed.GetValueOrThrow()
            .TriplesVisited.ShouldBeLessThan(
                walked.GetValueOrThrow().TriplesVisited,
                "FullyConsistent walks the nested groups and MinimizeLatency reads the closure — CheckGrain's remarks"
            );
    }

    [Fact]
    public async Task AWriteThatDiesBeforeTheIndexLeavesADenyUntilTheSweeperReplaysIt() {
        // docs/plan/07 § The Leopard index and TupleStoreGrain's remarks: a write lands the index
        // LAST, so a crash between the two halves leaves a grant the forward walk sees and the
        // index does not. That is a deny, not a leak — and the sweeper turns it into the grant.
        var tenant = AuthorizationCluster.Tenant(603);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "i4-rg");
        await cluster.WriteAsync(tenant, "resourceGroup:i4-rg#reader@group:i4-g#member");

        // ⚠ bob first, so that i4-g's slice is a written, complete closure. A slice no write has
        // touched is "walk it", not "empty" — and the walk would find alice in the forward half
        // the interrupted write landed. The deny this test pins is the closure's, and it takes a
        // closure to make it.
        await cluster.WriteAsync(tenant, "group:i4-g#member@user:bob");

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant)
                .WriteAsync(RelationTuple.Parse("group:i4-g#member@user:alice").GetValueOrThrow())
        );

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBeEmpty("the write died before step 6");
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i4-g"))).MembersOf(Relations.Member)
            .ShouldBe([SubjectRef.Of(ObjectTypes.User, "bob")]);

        var before = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.MinimizeLatency);
        before.GetValueOrThrow()
            .Allowed.ShouldBeFalse("i4-g's closure is complete and does not hold alice — fail-closed");

        var swept = (await cluster.Store(tenant).SweepAsync()).GetValueOrThrow();
        swept.Repaired.ShouldBe(1);

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBe([Member("i4-g")]);

        var token = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();
        var after = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.AtLeastAsFresh(token));
        after.GetValueOrThrow()
            .Allowed.ShouldBeTrue(
                "the sweep moved the version, so the cached deny is behind the token and the index now says yes"
            );
    }

    [Fact]
    public async Task ADeleteThatDiesAfterTheIndexLeavesTheRevokeHonouredAndTheSweeperFinishesIt() {
        // The other order: a delete lands the index FIRST. A crash after it leaves the reverse
        // half still naming the group, the forward half already without it, and the index saying
        // no — every reader that could answer answers the revoke.
        var tenant = AuthorizationCluster.Tenant(604);
        await cluster.WriteAsync(tenant, "group:i5-g#member@user:alice");
        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBe([Member("i5-g")]);

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant)
                .DeleteAsync(RelationTuple.Parse("group:i5-g#member@user:alice").GetValueOrThrow())
        );

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty)
            .ShouldBeEmpty("step 2 ran before the interruption");
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i5-g"))).MembersOf(Relations.Member).ShouldBeEmpty();
        (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow()
            .ShouldNotBeEmpty("the reverse half is step 5 and never ran");

        var swept = (await cluster.Store(tenant).SweepAsync()).GetValueOrThrow();
        swept.Repaired.ShouldBe(1);

        (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty)
            .ShouldBeEmpty("a replayed delete is the same delete");
    }

    [Fact]
    public async Task RebuildRecomputesASliceFromTheTuplesAndDropsWhatTheyDoNotSupport() {
        var tenant = AuthorizationCluster.Tenant(605);
        await NestAsync(tenant, "i6", 2);

        var top = ObjectRef.Of(ObjectTypes.Group, "i6-g2");
        var mallory = SubjectRef.Of(ObjectTypes.User, "mallory");

        // A slice damaged by hand — the shape a restore or a repair tool could leave.
        var damaged = await cluster.Index(tenant, top)
            .ApplyAsync(
                new() {
                    SchemaVersion = CyberCloudSchema.SchemaVersion,
                    AddMembers = new Dictionary<string, IReadOnlyList<SubjectRef>> { [Relations.Member] = [mallory] }
                }
            );

        damaged.GetValueOrThrow().ShouldBeTrue();
        (await ReadAsync(tenant, top)).MembersOf(Relations.Member).ShouldContain(mallory);

        var rebuilt = (await cluster.Index(tenant, top).RebuildAsync()).GetValueOrThrow();

        rebuilt.MembersOf(Relations.Member).ShouldBe([Member("i6-g1"), Alice], true);
        rebuilt.UsersetsOf(Relations.Member).ShouldBeEmpty("nothing is above the top group");
        (await ReadAsync(tenant, top)).MembersOf(Relations.Member).ShouldNotContain(mallory);
    }

    [Fact]
    public async Task AListingReadsTheSubjectsClosureOnceAndFindsTheGrantMadeToTheOutermostGroup() {
        var tenant = AuthorizationCluster.Tenant(606);
        await NestAsync(tenant, "i7", 4);
        await cluster.WriteAsync(tenant, "resourceGroup:i7-rg#parent@subscription:i7-sub");
        await cluster.WriteAsync(tenant, "resource:i7-a#parent@resourceGroup:i7-rg");
        await cluster.WriteAsync(tenant, "resource:i7-b#parent@resourceGroup:i7-rg");
        await cluster.WriteAsync(tenant, "resourceGroup:i7-rg#reader@group:i7-g4#member");

        var listed = await cluster.For(tenant)
            .GetGrain<IListObjectsGrain>(GrainKeys.ListObjects(Alice.Type, Alice.Id))
            .ListObjectsAsync(new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read });

        var page = listed.GetValueOrThrow();
        page.Objects.Select(static x => x.Id).ShouldBe(["i7-a", "i7-b"]);
        page.IndexReads.ShouldBe(
            1,
            "alice's slice names all four groups; a group inside a closure is never read for its own"
        );
    }

    [Fact]
    public async Task RowsThatPredateTheIndexAreWalkedAndThenBackfilledByTheFirstWriteThatTouchesThem() {
        // ⚠ THE REVIEW FINDING ON ISSUE #37, against the real grains. The forward and reverse
        // halves are written straight to their grains — the rows an upgrade to the index, or a
        // restore without its rows, leaves behind — and no slice exists. Then a check must walk,
        // not take an empty slice as a complete "no"; and the first write through the store that
        // touches those objects must rebuild their slices from the rows rather than close the new
        // edge over nothing and stamp the result current.
        var tenant = AuthorizationCluster.Tenant(608);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "i9-rg");
        var eng = ObjectRef.Of(ObjectTypes.Group, "i9-eng");
        var top = ObjectRef.Of(ObjectTypes.Group, "i9-top");

        await WriteHalvesAsync(tenant, "resourceGroup:i9-rg#reader@group:i9-eng#member");
        await WriteHalvesAsync(tenant, "group:i9-eng#member@user:alice");

        (await ReadAsync(tenant, eng)).SchemaVersion.ShouldBe(0, "no write through the store has touched the group");
        (await ReadAsync(tenant, Alice.Object)).SchemaVersion.ShouldBe(0);

        var walked = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.MinimizeLatency);
        walked.GetValueOrThrow()
            .Allowed.ShouldBeTrue("an unwritten slice is 'walk it', and the walk finds alice in the rows");

        // The first write that touches eng: a nesting edge, through the store.
        var token = await cluster.WriteAsync(tenant, "group:i9-top#member@group:i9-eng#member");

        var topSlice = await ReadAsync(tenant, top);
        topSlice.SchemaVersion.ShouldBe(CyberCloudSchema.SchemaVersion);
        topSlice.MembersOf(Relations.Member)
            .ShouldBe(
                [Member("i9-eng"), Alice],
                true,
                "the pre-existing member is in the new closure, not only the edge"
            );

        var engSlice = await ReadAsync(tenant, eng);
        engSlice.SchemaVersion.ShouldBe(CyberCloudSchema.SchemaVersion);
        engSlice.MembersOf(Relations.Member).ShouldBe([Alice]);
        engSlice.UsersetsOf(Relations.Member).ShouldBe([Member("i9-top")]);

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty)
            .ShouldBe(
                [Member("i9-eng"), Member("i9-top")],
                true,
                "alice's slice was unwritten when the union reached it and was rebuilt first"
            );

        var after = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.AtLeastAsFresh(token));
        after.GetValueOrThrow().Allowed.ShouldBeTrue("the backfilled closures say what the rows say");
    }

    [Fact]
    public async Task AChangeComputedUnderAnotherSchemaVersionIsRefused() {
        var tenant = AuthorizationCluster.Tenant(607);

        var refused = await cluster.Index(tenant, Alice.Object)
            .ApplyAsync(new() { SchemaVersion = CyberCloudSchema.SchemaVersion + 1 });

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
    }

    /// <summary>alice ∈ g1 ⊂ g2 ⊂ … ⊂ g<paramref name="depth" />, through the store.</summary>
    async Task NestAsync(Guid tenant, string prefix, int depth) {
        await cluster.WriteAsync(tenant, $"group:{prefix}-g1#member@user:alice");

        for (var i = 1; i < depth; i++) {
            await cluster.WriteAsync(tenant, $"group:{prefix}-g{i + 1}#member@group:{prefix}-g{i}#member");
        }
    }

    /// <summary>
    ///     Writes a tuple's forward and reverse halves straight to their grains, past the store —
    ///     the rows a tenant has before the index exists for it.
    /// </summary>
    async Task WriteHalvesAsync(Guid tenant, string text) {
        var tuple = RelationTuple.Parse(text).GetValueOrThrow();

        (await cluster.Objects(tenant, tuple.Object)
                .WriteAsync(tuple.Relation, tuple.Subject, tuple.ExpiresOn)).IsSuccess.ShouldBeTrue();
        (await cluster.SubjectIndex(tenant, tuple.Subject)
                .AddAsync(
                    new() { Object = tuple.Object, Relation = tuple.Relation, SubjectRelation = tuple.Subject.Relation }
                ))
            .IsSuccess.ShouldBeTrue();
    }

    async Task<MembershipIndexSnapshot> ReadAsync(Guid tenant, ObjectRef subjectObject) {
        var read = await cluster.Index(tenant, subjectObject).ReadAsync();
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        return read.GetValueOrThrow();
    }
}
