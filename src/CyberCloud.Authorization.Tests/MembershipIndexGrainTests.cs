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
///         Tenant indexes 600–609 are this file's.
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
        alice.UsersetsOf(string.Empty).ShouldBe([Member("i1-g1"), Member("i1-g2"), Member("i1-g3")], ignoreOrder: true);
        alice.SchemaVersion.ShouldBe(CyberCloudSchema.SchemaVersion);

        // Down from the top: the nested usersets and the one concrete member.
        var top = await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g3"));
        top.MembersOf(Relations.Member).ShouldBe([Member("i1-g2"), Member("i1-g1"), Alice], ignoreOrder: true);

        // ⚠ THE DELETE, THROUGH THE STORE. Cutting g2 out of g3 has to recompute g3's members from
        // the tuples, not merely drop g2: alice and g1 reached g3 through g2 and go with it.
        await cluster.RevokeAsync(tenant, "group:i1-g3#member@group:i1-g2#member");

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBe([Member("i1-g1"), Member("i1-g2")], ignoreOrder: true);
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g3"))).MembersOf(Relations.Member).ShouldBeEmpty();
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i1-g2"))).MembersOf(Relations.Member).ShouldBe([Member("i1-g1"), Alice], ignoreOrder: true);
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
        var indexed = await cluster.Check(tenant, resource).CheckAsync(Permissions.Read, Alice, Consistency.MinimizeLatency);
        indexed.GetValueOrThrow().Allowed.ShouldBeTrue();
        indexed.GetValueOrThrow().FromCache.ShouldBeFalse();

        var walked = await cluster.Check(tenant, resource).CheckAsync(Permissions.Read, Alice, Consistency.FullyConsistent);
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

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(RelationTuple.Parse("group:i4-g#member@user:alice").GetValueOrThrow()));

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBeEmpty("the write died before step 6");

        var before = await cluster.Check(tenant, scope).CheckAsync(Permissions.Read, Alice, Consistency.MinimizeLatency);
        before.GetValueOrThrow().Allowed.ShouldBeFalse("the index is complete for i4-g and says alice is not in it — fail-closed");

        var swept = (await cluster.Store(tenant).SweepAsync()).GetValueOrThrow();
        swept.Repaired.ShouldBe(1);

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBe([Member("i4-g")]);

        var token = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();
        var after = await cluster.Check(tenant, scope).CheckAsync(Permissions.Read, Alice, Consistency.AtLeastAsFresh(token));
        after.GetValueOrThrow().Allowed.ShouldBeTrue("the sweep moved the version, so the cached deny is behind the token and the index now says yes");
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
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).DeleteAsync(RelationTuple.Parse("group:i5-g#member@user:alice").GetValueOrThrow()));

        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBeEmpty("step 2 ran before the interruption");
        (await ReadAsync(tenant, ObjectRef.Of(ObjectTypes.Group, "i5-g"))).MembersOf(Relations.Member).ShouldBeEmpty();
        (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow().ShouldNotBeEmpty("the reverse half is step 5 and never ran");

        var swept = (await cluster.Store(tenant).SweepAsync()).GetValueOrThrow();
        swept.Repaired.ShouldBe(1);

        (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow().ShouldBeEmpty();
        (await ReadAsync(tenant, Alice.Object)).UsersetsOf(string.Empty).ShouldBeEmpty("a replayed delete is the same delete");
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

        rebuilt.MembersOf(Relations.Member).ShouldBe([Member("i6-g1"), Alice], ignoreOrder: true);
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
        page.Objects.Select(x => x.Id).ShouldBe(["i7-a", "i7-b"]);
        page.IndexReads.ShouldBe(1, "alice's slice names all four groups; a group inside a closure is never read for its own");
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

    async Task<MembershipIndexSnapshot> ReadAsync(Guid tenant, ObjectRef subjectObject) {
        var read = await cluster.Index(tenant, subjectObject).ReadAsync();
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        return read.GetValueOrThrow();
    }
}
