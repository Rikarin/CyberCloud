using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     ⚠
///     <b>
///         The two-grain write is not transactional, and the asymmetry is deliberate. This file
///         tests the claim directly.
///     </b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § Storage:
///         <i>
///             "The first two are written together on every tuple write — the
///             write is to two grains and is <b>not</b> transactional, so it is ordered (object first,
///             then subject) and reconciled by a sweeper. A subject index missing an entry costs a
///             <c>ListObjects</c> a miss, not a <c>Check</c> an incorrect answer, because <c>Check</c>
///             walks forward from the object.
///             <b>
///                 That asymmetry is deliberate: the direction that can be
///                 stale is the one where staleness is a performance bug, not a security bug.
///             </b>
///             "
///         </i>
///     </para>
///     <para>
///         So: interrupt the write between the two halves, deactivate everything, and assert
///         <c>Check</c> is still correct. If it were not, the claim would be a security bug rather
///         than a design note, and the whole non-transactional write would have to be reconsidered.
///         <b>The asymmetry holds.</b>
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class TwoGrainWriteTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    [Fact]
    public async Task AWriteInterruptedBetweenTheTwoGrainsLeavesCheckCorrect() {
        var tenant = AuthorizationCluster.Tenant(200);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "interrupt1");
        var tuple = RelationTuple.Parse("resourceGroup:interrupt1#owner@user:alice")
            .GetValueOrThrow();

        cluster.Interceptor.Armed = true;

        // The write dies exactly between the two halves. The caller sees the failure, so nothing
        // downstream believes the assignment succeeded — but the first half has landed.
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(tuple));
        cluster.Interceptor.Fired.ShouldBeGreaterThan(0);

        // The forward half — the one Check reads — is there.
        var forward = await cluster.Objects(tenant, scope).ReadAsync();
        forward.GetValueOrThrow().Subjects(Relations.Owner).ShouldContain(Alice);

        // The reverse half — the one only ListObjects reads — is not.
        var reverse = await cluster.SubjectIndex(tenant, Alice).ListAsync();
        reverse.GetValueOrThrow()
            .ShouldBeEmpty(
                "the write died before the subject index was touched; if this is not empty the "
                + "interruption did not land where the test believes it did"
            );

        // ⚠ AND CHECK IS STILL CORRECT. This is the claim.
        var check = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.FullyConsistent);

        check.IsSuccess.ShouldBeTrue(check.Error?.Message);
        check.GetValueOrThrow()
            .Allowed.ShouldBeTrue(
                "docs/plan/07 § Storage: a missing subject-index entry must never cost Check a wrong "
                + "answer, because Check walks forward from the object. If this fails, the asymmetry "
                + "does not hold and the non-transactional write is a security bug rather than a "
                + "performance trade."
            );
    }

    [Fact]
    public async Task CheckIsStillCorrectAfterEveryGrainInvolvedIsDeactivated() {
        // The interruption again, then the state is forced through a full round trip to PostgreSQL
        // and back — which is what a silo restart in the middle of the incident would do.
        var tenant = AuthorizationCluster.Tenant(201);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "interrupt2");
        var tuple = RelationTuple.Parse("resourceGroup:interrupt2#owner@user:alice")
            .GetValueOrThrow();

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(tuple));

        await cluster.Store(tenant).DeactivateAsync();
        await cluster.Objects(tenant, scope).DeactivateAsync();
        await cluster.SubjectIndex(tenant, Alice).DeactivateAsync();
        await cluster.Check(tenant, scope).DeactivateAsync();

        var check = await cluster.Check(tenant, scope)
            .CheckAsync(Permissions.Read, Alice, Consistency.FullyConsistent);

        check.GetValueOrThrow().Allowed.ShouldBeTrue();

        // And the journal survived the deactivation, which is what makes the sweeper possible at
        // all: a journal in memory would have gone with the silo that lost the write.
        (await cluster.Store(tenant).PendingCountAsync()).GetValueOrThrow().ShouldBe(1);
    }

    [Fact]
    public async Task TheSweeperReconcilesTheMissingReverseEntry() {
        var tenant = AuthorizationCluster.Tenant(202);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "sweep1");
        var tuple = RelationTuple.Parse("resourceGroup:sweep1#owner@user:alice").GetValueOrThrow();

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(tuple));

        (await cluster.SubjectIndex(tenant, Alice).ListAsync()).GetValueOrThrow().ShouldBeEmpty();

        var swept = await cluster.Store(tenant).SweepAsync();
        var report = swept.GetValueOrThrow();

        report.Pending.ShouldBe(1);
        report.Repaired.ShouldBe(1);
        report.Remaining.ShouldBe(0);

        var reverse = await cluster.SubjectIndex(tenant, Alice).ListAsync();
        reverse.GetValueOrThrow()
            .ShouldContain(x =>
                x.Object == scope && x.Relation == Relations.Owner
            );
    }

    [Fact]
    public async Task ASweepIsIdempotentAndASecondOneFindsNothing() {
        var tenant = AuthorizationCluster.Tenant(203);
        var tuple = RelationTuple.Parse("resourceGroup:sweep2#owner@user:alice").GetValueOrThrow();

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(tuple));

        (await cluster.Store(tenant).SweepAsync()).GetValueOrThrow().Repaired.ShouldBe(1);

        var second = await cluster.Store(tenant).SweepAsync();
        second.GetValueOrThrow().Pending.ShouldBe(0);
        second.GetValueOrThrow().Repaired.ShouldBe(0);
    }

    [Fact]
    public async Task ARepairedWriteMovesTheTenantRelationVersion() {
        // A repair can land the OBJECT half for the first time, which changes what Check answers —
        // so every cached answer in the tenant has to become stale. The version is how that is said.
        var tenant = AuthorizationCluster.Tenant(204);
        var tuple = RelationTuple.Parse("resourceGroup:sweep3#owner@user:alice").GetValueOrThrow();

        cluster.Interceptor.Armed = true;
        await Should.ThrowAsync<Exception>(() => cluster.Store(tenant).WriteAsync(tuple));

        var before = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();
        await cluster.Store(tenant).SweepAsync();
        var after = (await cluster.Store(tenant).GetTokenAsync()).GetValueOrThrow();

        after.Version.ShouldBeGreaterThan(before.Version);
    }

    [Fact]
    public async Task AnUninterruptedWriteLeavesNothingForTheSweeper() {
        var tenant = AuthorizationCluster.Tenant(205);

        await cluster.WriteAsync(tenant, "resourceGroup:clean1#owner@user:alice");

        (await cluster.Store(tenant).PendingCountAsync()).GetValueOrThrow().ShouldBe(0);

        var reverse = await cluster.SubjectIndex(tenant, Alice).ListAsync();
        reverse.GetValueOrThrow().ShouldContain(x => x.Relation == Relations.Owner);
    }

    [Fact]
    public async Task TheReverseIndexTellsAGroupApartFromItsUserset() {
        // `group:eng` and `group:eng#member` share a reverse-index grain and must not collapse:
        // one is "the group itself", the other is "everyone in it". The key carries no userset
        // relation, so the entry has to.
        var tenant = AuthorizationCluster.Tenant(206);

        await cluster.WriteAsync(tenant, "resourceGroup:rev1#owner@group:eng#member");
        await cluster.WriteAsync(tenant, "resourceGroup:rev1#parent@subscription:s1");
        await cluster.WriteAsync(tenant, "subscription:s1#owner@group:eng#member");

        var entries = (await cluster.SubjectIndex(tenant, SubjectRef.Of(ObjectTypes.Group, "eng"))
            .ListAsync()).GetValueOrThrow();

        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(x => x.SubjectRelation == Relations.Member);
    }
}
