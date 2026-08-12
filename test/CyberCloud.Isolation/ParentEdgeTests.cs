using CyberCloud.Authorization;
// ⚠ ObjectTypes and Relations moved here from the implementation assembly — see
// CyberCloud.Authorization.Contracts/AuthorizationVocabulary.cs. `using CyberCloud.Authorization`
// above is still needed, for CyberCloudSchema. Safe to import unqualified in this one file because
// nothing in it names ObjectRef, which GlobalUsings.cs pins to the Kubernetes spelling.
using CyberCloud.Authorization.Contracts;
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
    public async Task AChildsEdgePointsAtItsParentResourceRatherThanAtTheGroup() {
        // ⚠ THE HOP THE INTERLEAVED GRAMMAR WAS CHOSEN TO MAKE EXPRESSIBLE, ASSERTED AT THE TUPLE.
        //
        // docs/plan/12 § Child resources picked `…/probes/{parent}/samples/{child}` over the
        // flattened `…/probes/samples/{child}` for one reason above the others: the flattened form
        // cannot say WHICH parent, so the edge could only ever point at the resource group.
        // ReBacResourceRelationWriter then went on doing exactly that for every resource regardless
        // of depth, which spends the decision and keeps the failure it was meant to remove — granting
        // somebody the parent would grant nothing on its children.
        //
        // The test above pins the top-level case, and it is not enough on its own: an edge aimed at
        // the group makes a child readable to a GROUP owner, so a read-back would pass while every
        // `resource:{parent}#contributor` assignment granted nothing. Same shape of defect as
        // parent-at-the-subscription, one level down. So the tuple itself is read.
        var parentName = "child-edge-parent";
        var childName = "child-edge-child";

        var parentId = await cluster.CreateAsync(
            Probes,
            parentName,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var childAddress = new ResourceId(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.Group,
            Conformance.Reference.Probes.ChildType,
            childName,
            Guid.Empty,
            parentName
        );

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = childAddress.Path,
                ApiVersion = Conformance.Reference.Probes.V2026,
                Verb = WriteVerb.Put,
                Body = Conformance.Reference.Probes.ChildBody(),
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(
            "the child could not be created at all: " + accepted.Error?.Message
        );

        var childId = accepted.GetValueOrThrow().Resource.Id;

        var parents = await cluster.ParentsOfAsync(IsolationCluster.Victim, childId);

        parents.Count.ShouldBe(1, "a resource has exactly one parent scope");

        parents[0].Type.ShouldBe(
            ObjectTypes.Resource,
            "the child's parent edge names an object type other than `resource`, so the hop "
            + "docs/plan/12 § Child resources bought does not exist"
        );

        // ⚠ "N", because that is what SubjectRef.Of(type, Guid) writes and therefore what the tuple
        // holds — the same spelling ReBacResourceAuthorizer.GroupObjectId uses for the subscription
        // half of a group's object id.
        parents[0].Id.ShouldBe(
            parentId.ToString("N", CultureInfo.InvariantCulture),
            "the child's parent edge does not point at the parent RESOURCE — granting a role on the "
            + "parent grants nothing on its children, which is the failure the interleaved address "
            + "was chosen to remove"
        );

        parents[0].Relation.ShouldBeNullOrEmpty("the parent's subject is an object, not a userset");
    }

    [Fact]
    public void TheRelationTheWriterNamesIsTheOneTheSchemaRewritesThrough() {
        // ⚠ THE SAME GUARD THE resourcegroup/resourceGroup CASING BUG EARNED, ON THE OTHER STRING —
        // AND THE HALF OF IT THAT SURVIVED THE VOCABULARY MOVE.
        //
        // ReBacResourceRelationWriter used to name the relation "parent" as a literal, because
        // Relations lived in CyberCloud.Authorization and CyberCloud.ResourceManager references only
        // its .Contracts. ParentRelation IS Relations.Parent now, so asserting the two strings agree
        // has become `x.ShouldBe(x)` and is gone; a misspelling is CS0117 at the call site.
        //
        // What is NOT a compile error is naming the wrong DEFINED relation, and that is what is left
        // here. Note which side of this is load-bearing: the schema half is not. Dropping
        // .Relation(Relations.Parent) from DefineType(ObjectTypes.Resource) fails 84 tests in
        // CyberCloud.Authorization.Tests on its own, because SchemaBuilder.Build() throws rather than
        // resolve the From(Relations.Parent, …) rewrites through an undeclared relation — so
        // `Member(ObjectTypes.Resource, Relations.Parent)` spelled with the constant directly would be
        // guarded elsewhere and would assert nothing new.
        //
        // The subject is ReBacResourceRelationWriter.ParentRelation: the relation THIS WRITER uses,
        // which nothing in CyberCloud.Authorization.Tests can see. Repointing it at any other defined
        // relation — Relations.Owner, say — compiles, names a relation the schema declares, and fails
        // only here. That failure is the silent one the casing bug was not: an object type the schema
        // does not define is REJECTED, but a tuple naming a relation the resource type does not
        // declare is written SUCCESSFULLY against a relation no rewrite follows, so every create
        // reports 202 and every resource is invisible with no error in any log.
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

    /// <summary>
    ///     The reference provider, which is the one with a child type. ⚠ Named rather than indexed
    ///     out of <see cref="IsolationCatalog.Targets" />: reordering that list must not silently
    ///     repoint the child test at a provider whose type has no children.
    /// </summary>
    static IsolationTarget Probes =>
        IsolationCatalog.Targets.Single(target => target.Type == Conformance.Reference.Probes.Type);
}
