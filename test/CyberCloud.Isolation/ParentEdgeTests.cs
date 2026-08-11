using CyberCloud.Authorization;
using CyberCloud.ResourceManager;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The edge that makes a created resource readable — written by the platform, or by nobody.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here runs against the real <c>ReBacResourceRelationWriter</c>, the real
///         <c>ReBacResourceAuthorizer</c> and the real <c>CyberCloudSchema</c>, and that is the whole
///         reason they are in this project.</b> Both halves of the seam are doubled in every other
///         suite, which is right for testing step ordering and useless here: a double writes whatever
///         tuple its author believed in and a double answers whatever its author believed. The defect
///         these tests exist for — <b>a create succeeded and its own creator then got 404</b> — is
///         invisible to any pair of doubles that agree with each other.
///     </para>
///     <para>
///         docs/plan/07 § The model: <c>From("parent", …)</c> is <i>"the whole of hierarchical
///         inheritance"</i>, and it follows a <c>parent</c> tuple. docs/plan/08 § The write path, end
///         to end's step 8 is what writes it.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class ParentEdgeTests(IsolationCluster cluster) {
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheCreatorCanReadWhatTheyJustCreated(IsolationTarget target) {
        // ⚠ THE DEFECT, AS ONE SENTENCE. Nothing in this test writes a tuple: the only grant in the
        // fixture is `resourceGroup:{sub}-prod#owner@user:victor`, and the only thing that can carry
        // that grant down to a resource is the parent edge the write path writes at step 8. Before
        // that step existed, this read answered 404 — for a resource the same caller had just been
        // told, with a 202, that they had created.
        var name = "creator-read-" + target.Name.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        var read = await cluster.Manager.ReadAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(
            "the creator cannot read the resource they just created — the write path did not write "
            + "the resource → resourceGroup parent edge, so CyberCloudSchema's From(parent, owner) "
            + "rewrite has nothing to follow: " + read.Error?.Message
        );

        read.GetValueOrThrow().Id.ShouldBe(id);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheWritePathWritesTheEdgeAndPointsItAtTheGroupRatherThanTheSubscription(
        IsolationTarget target
    ) {
        // ⚠ WHICH PARENT, ASSERTED RATHER THAN ASSUMED. CyberCloudSchema's chain is
        // resource → resourceGroup → subscription → tenant. A parent pointing straight at the
        // subscription would still make the resource readable to a SUBSCRIPTION owner — so the test
        // above would pass — while every `resourceGroup:…#contributor` assignment, which is the
        // second row of docs/plan/07 § Azure RBAC, expressed in it, would grant nothing. That is a
        // defect no read-back test can see, so the tuple itself is read.
        var name = "edge-shape-" + target.Name.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var parents = await cluster.ParentsOfAsync(IsolationCluster.Victim, id);

        parents.Count.ShouldBe(1, "a resource has exactly one parent scope");
        parents[0].Type.ShouldBe(ObjectTypes.ResourceGroup);
        parents[0].Id.ShouldBe(
            IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture)
            + "-"
            + IsolationCluster.Group
        );

        // ⚠ And the group's object id carries the SUBSCRIPTION, so the `prod` groups of two
        // subscriptions are two authorization objects rather than one — the cross-subscription hole
        // that ReBacResourceAuthorizer.GroupObjectId's remarks describe.
        parents[0].Id.ShouldNotBe(IsolationCluster.Group);
        parents[0].Relation.ShouldBeNullOrEmpty("the parent's subject is an object, not a userset");
    }

    [Fact]
    public void TheRelationTheWriterNamesIsTheOneTheSchemaRewritesThrough() {
        // ⚠ THE SAME GUARD THE resourcegroup/resourceGroup CASING BUG EARNED, ON THE OTHER STRING.
        //
        // ReBacResourceRelationWriter names the relation "parent" as a literal because
        // CyberCloud.Authorization.Relations lives in an assembly CyberCloud.ResourceManager does not
        // reference. A mismatch would be SILENT in a way the casing bug was not: the tuple would be
        // written successfully against a relation no rewrite follows, every create would report 202,
        // and every resource would be invisible with no error in any log.
        ReBacResourceRelationWriter.ParentRelation.ShouldBe(Relations.Parent);

        CyberCloudSchema.Instance
            .Member(ReBacResourceAuthorizer.ResourceObjectType, ReBacResourceRelationWriter.ParentRelation)
            .ShouldNotBeNull("the resource type has no 'parent' relation for the edge to be written on");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheEdgeExistsBeforeTheResourceDoesAndSoTheWindowHasNoWidth(IsolationTarget target) {
        // ⚠ "THE TUPLE SURVIVES THE SILO DYING BETWEEN THE RESOURCE WRITE AND THE TUPLE WRITE."
        //
        // It survives because that ordering does not exist. The edge is written at step 8 and the
        // durable resource at step 9, so there is no instant at which a resource is durable and
        // unlinked — a silo lost anywhere in the write path leaves either nothing, or a resource that
        // is already readable. The trace is where that ordering is observable from outside, and
        // WriteTraceBuilder throws rather than recording a step out of order, so this assertion and
        // the write path cannot drift apart.
        var name = "ordering-" + target.Name.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);

        var address = IsolationCluster.Address(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var reached = accepted.GetValueOrThrow().Trace.Reached;

        reached.IndexOf(WriteStep.LinkParent).ShouldBeGreaterThanOrEqualTo(0, "step 8 did not run");

        reached.IndexOf(WriteStep.LinkParent).ShouldBeLessThan(
            reached.IndexOf(WriteStep.SubmitDesired),
            "the parent edge was written AFTER the durable resource, which reopens the window in "
            + "which a resource exists and nobody can see it"
        );

        reached.IndexOf(WriteStep.IndexClaim).ShouldBeLessThan(
            reached.IndexOf(WriteStep.LinkParent),
            "the edge was written before the name was claimed, so a lost name race leaves a tuple "
            + "for a resource that was never created"
        );

        // And the edge really is there the moment the 202 comes back — before any reconcile pass has
        // run, which is the point at which a caller would first try to GET what they created.
        (await cluster.ParentsOfAsync(IsolationCluster.Victim, accepted.GetValueOrThrow().Resource.Id))
            .ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task DeleteLeavesNoDanglingTuple(IsolationTarget target) {
        // ⚠ A tuple pointing at an object that no longer exists is a slow leak in the tenant's tuple
        // store, and it is the kind that is never noticed: it grants nothing, costs nothing to read
        // and grows by one row per resource ever deleted. OperationGrain removes it after
        // CompleteDeleteAsync — when the resource is GONE rather than when the delete was asked for,
        // because a resource in Deleting is still visible and its owner still has to be able to see it.
        var name = "unlink-" + target.Name.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        (await cluster.ParentsOfAsync(IsolationCluster.Victim, id)).ShouldNotBeEmpty("the create wrote no edge");

        var address = IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        var caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser);

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        // ⚠ The edge is STILL THERE while the teardown runs. Asserted rather than skipped past,
        // because unlinking at the request would have been the easy implementation and would have
        // made a resource invisible to its owner for as long as the teardown took — indefinitely, if
        // it kept failing.
        (await cluster.ParentsOfAsync(IsolationCluster.Victim, id)).ShouldNotBeEmpty(
            "the edge was removed when the delete was ACCEPTED, so the owner cannot read a resource "
            + "that is still up and still billed"
        );

        var operation = cluster.For(IsolationCluster.Victim)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(deleted.GetValueOrThrow().OperationId));

        for (var i = 0; i < 6; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                break;
            }
        }

        (await operation.GetAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await cluster.ParentsOfAsync(IsolationCluster.Victim, id)).ShouldBeEmpty(
            "the resource is gone and its parent tuple is not"
        );
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;
}
