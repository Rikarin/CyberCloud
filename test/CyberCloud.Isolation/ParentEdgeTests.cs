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
        //
        // ⚠ AND "WHICH PARENT" NOW DEPENDS ON THE TARGET'S DEPTH, WHICH IS WHY THIS SWEEP BRANCHES.
        // A top-level resource's parent is its resource group; a child's is its parent RESOURCE.
        // Asserting the group for every target would have made the child row fail on a correct
        // implementation, and asserting whichever one happened to be there would have asserted
        // nothing. The branch is on the address's own depth — the same pure function
        // ReBacResourceRelationWriter reads — so a writer that stopped distinguishing them fails one
        // row of this theory whichever way it stopped.
        ArgumentNullException.ThrowIfNull(target);

        var name = "edge-shape-" + target.Name.GetHashCode(StringComparison.Ordinal).ToString("x8", CultureInfo.InvariantCulture);

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster.Address(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var parents = await cluster.ParentsOfAsync(IsolationCluster.Victim, id);

        parents.Count.ShouldBe(1, "a resource has exactly one parent scope");
        parents[0].Relation.ShouldBeNullOrEmpty("the parent's subject is an object, not a userset");

        if (address.Parent is { } parentAddress) {
            var parentId = await cluster.ResolveAsync(IsolationCluster.Victim, parentAddress);

            parents[0].Type.ShouldBe(
                ObjectTypes.Resource,
                "a child's parent edge names an object type other than `resource`, so granting a role "
                + "on the parent grants nothing on its children"
            );

            parents[0].Id.ShouldBe(parentId.ToString("N", CultureInfo.InvariantCulture));
            return;
        }

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
                Body = Conformance.Reference.Probes.ChildBody(IsolationCluster.ClusterId),
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
    public async Task AChildCannotBeHungOffAnotherTenantsParentAndTheRefusalIsTheSame404() {
        // ⚠ THE HOLE THE PARENT HOP OPENED, AND IT IS NEW AS OF 2026-08-12.
        //
        // Before the hop, a resource's `parent` edge named its resource GROUP, and a group's object
        // id is {subscriptionId:N}-{name} — so the only cross-boundary question was whether a
        // subscription id could be borrowed, which BelowTheManagerTests already covers. A child's
        // edge now names its PARENT RESOURCE, whose GUID the write path resolves out of an index the
        // path itself points at. That is a second thing an attacker can aim: guess the victim's
        // parent NAME and the child hangs off it.
        //
        // It does not, and the reason is worth pinning rather than assuming: every grain on the
        // resolve path is reached through ForTenant(caller's tenant) — ADR-002 puts the tenant in the
        // key — so the victim's parent is not addressable from the attacker's tenant and reads as
        // absent. The refusal must therefore be indistinguishable from the one for a parent name
        // that was never used anywhere, or the attacker has an oracle for the victim's parent names:
        // one probe per guess, from inside their own tenant, with no access to the victim's at all.
        var parentName = "cross-tenant-parent";

        var victimParentId = await cluster.CreateAsync(
            Probes,
            parentName,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        victimParentId.ShouldNotBe(Guid.Empty);

        // The attacker, inside their OWN tenant and their OWN subscription, names the victim's parent.
        var borrowed = new ResourceId(
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription,
            IsolationCluster.Group,
            Conformance.Reference.Probes.ChildType,
            "borrowed",
            Guid.Empty,
            parentName
        );

        // …and, for comparison, a parent name that exists in neither tenant.
        var invented = borrowed with { Name = "invented", ParentNames = "no-such-parent-anywhere" };

        cluster.World.Reset();

        var onBorrowed = await WriteChildAsync(borrowed);
        var onInvented = await WriteChildAsync(invented);

        onBorrowed.IsFailure.ShouldBeTrue(
            "a child was created in the attacker's tenant under the victim's parent name. Its ReBAC "
            + "parent edge names a resource GUID resolved from an index the attacker does not own"
        );

        onBorrowed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        onBorrowed.Error.Code.ShouldNotBe(
            ErrorCode.AuthorizationFailed,
            "403 here confirms the victim's parent exists — docs/plan/07 § The enforcement seam"
        );

        onInvented.IsFailure.ShouldBeTrue();

        // ⚠ IDENTICAL, MODULO THE PATH THE CALLER THEMSELVES SUPPLIED. Two 404s whose wording differs
        // are still a way to ask "is this a real parent over there".
        onBorrowed.Error.Code.ShouldBe(onInvented.Error!.Code);
        onBorrowed.Error.Target.ShouldBe(onInvented.Error.Target);

        onBorrowed.Error.Message.Replace(borrowed.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(onInvented.Error.Message.Replace(invented.Path, "PATH", StringComparison.Ordinal));

        // And nothing reached a provider, and no tuple was written against the victim's parent.
        cluster.World.Applied.ShouldBeEmpty("a refused cross-tenant child create reached a provider");

        (await cluster.ParentsOfAsync(IsolationCluster.Attacker, victimParentId)).ShouldBeEmpty();
    }

    /// <summary>Writes a child as the attacker, at an address the attacker supplied.</summary>
    /// <param name="address">Where.</param>
    async Task<Result<WriteAccepted>> WriteChildAsync(ResourceId address) =>
        await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = Conformance.Reference.Probes.V2026,
                Verb = WriteVerb.Put,
                Body = Conformance.Reference.Probes.ChildBody(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser)
            },
            TestContext.Current.CancellationToken
        );

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

    /// <summary>
    ///     ⚠ <b>No tuple survives the resource, and "the resource is gone" is a different moment for
    ///     a type that declares a recovery window.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE BRANCH BELOW IS READ OFF THE REGISTRY RATHER THAN OFF A LIST OF TYPE NAMES,
    ///         AND THAT IS THE WHOLE OF WHY THIS STAYS ONE THEORY.</b> A test named "delete leaves no
    ///         dangling tuple" that quietly came to mean "except for the types with a window, where
    ///         there is one and that is fine" would be the exact failure this suite exists to catch. So
    ///         the property the name states is asserted for every target — after the resource stops
    ///         existing, nothing points at it — and the branch is only about <i>when</i> it stops
    ///         existing. For a hard-delete type that is when the delete converges. For a soft-delete
    ///         type, docs/plan/08 § Soft delete makes the delete a park: the resource still exists,
    ///         keeps its name, keeps its committed quota and can be handed back, so a tuple naming it is
    ///         not dangling. It stops existing at the purge, and the purge is where this asserts empty.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The parked state is asserted POSITIVELY, and a <c>ShouldNotBeEmpty</c> there
    ///         would have been the weaker assertion that lets the real leak through.</b> § Soft
    ///         delete's fourth decision re-parents the edge <i>to the subscription</i>, and the reason is
    ///         a lifetime one: a parked resource that kept pointing at its resource group would hold a
    ///         tuple naming an object its tenant may delete while the window runs — a dangling edge
    ///         that outlives the group, which is the shape this test's name is about. "Not empty" is
    ///         satisfied by exactly that bug, so the subject's type and id are both checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only <c>CyberCloud.Storage/accounts</c> takes the second branch today</b>,
    ///         because it is the only member of <see cref="IsolationCatalog.Targets" /> that declares a
    ///         window — the four other types that declare one are not in this catalogue, and
    ///         <c>Storage/accounts/buckets</c> deliberately declares none, since a bucket's bytes live in
    ///         its account's volumes and the account's window already holds them. That is coverage rather
    ///         than behaviour, and it is worth knowing before reading a green run as broad evidence.
    ///     </para>
    /// </remarks>
    /// <param name="target">The type under attack.</param>
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task DeleteLeavesNoDanglingTuple(IsolationTarget target) {
        ArgumentNullException.ThrowIfNull(target);

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

        // ⚠ The registry's own fact, which is the one the manager branches on. A list of type names
        // here would disagree with the platform the day a provider declares or withdraws a window, and
        // it would disagree silently — in the direction of asserting the hard-delete shape against a
        // type that no longer has it, which is how this test first went red.
        cluster.Registry.TryGetType(target.Type, out var registration).ShouldBeTrue(
            $"'{target.Type}' is a target of this suite and the registry does not know it"
        );

        if (registration.SoftDeleteDays == 0) {
            (await cluster.ParentsOfAsync(IsolationCluster.Victim, id)).ShouldBeEmpty(
                "the resource is gone and its parent tuple is not"
            );

            return;
        }

        // ── The window: the resource still exists, so the edge is not dangling — it moved ──
        var parked = await cluster.ParentsOfAsync(IsolationCluster.Victim, id);

        parked.Count.ShouldBe(
            1,
            "a parked resource has exactly one parent scope, as a live one does. docs/plan/08 \u00a7 Soft "
            + "delete MOVES the edge rather than adding to it, and two edges would leave the resource "
            + "readable through a group it has been told it left"
        );

        parked[0].Type.ShouldBe(
            ObjectTypes.Subscription,
            "the parked resource still points at its resource GROUP. A tenant may delete that group "
            + "while the window runs, which leaves a tuple naming an object that is gone \u2014 the "
            + "dangling edge this test is named for, one lifetime removed. docs/plan/08 \u00a7 Soft "
            + "delete's fourth decision re-parents to the subscription for exactly this reason"
        );

        parked[0].Id.ShouldBe(
            IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture),
            "the edge names a subscription other than the one the resource lives in"
        );

        parked[0].Relation.ShouldBeNullOrEmpty("the parent's subject is an object, not a userset");

        // ── The purge, which is where the resource actually stops existing ─────────────
        //
        // ⚠ Not by driving the delete further, because there is nothing further to drive: that
        // operation reached Succeeded above and the window is a state rather than an operation.
        //
        // ⚠ THIS IS THE ONLY PURGE IN THE REPOSITORY THAT MEETS THE REAL SCHEMA. Every other one
        // runs against a doubled authorizer, so "a caller who may purge can purge" was reasoning
        // rather than a measurement — the same gap ParentEdgeTests opens by describing.
        // ⚠ AND THE PERSON WHO DELETED IT CANNOT PURGE IT, WHICH IS THE EDGE MOVE HAVING AN EFFECT
        // RATHER THAN JUST A SHAPE.
        //
        // victor holds `owner` on the resource GROUP and on nothing above it. The assertions above say
        // the parked resource no longer hangs off that group; this says what that costs him, and it is
        // the canonical 404 — docs/plan/07 § The enforcement seam, because "you may not purge this"
        // would confirm the name is held. docs/plan/08 § Soft delete chose this deliberately: "the
        // people who can see a deleted resource become the people who hold subscription-scoped rights,
        // which is exactly who Azure gives deletedVaults/read and purge/action to."
        //
        // ⚠ It is asserted before the successful purge rather than after, and the order is what makes
        // the pair evidence. Run the other way round the resource would already be gone, and a 404
        // would be the honest answer to a caller with every right in the platform.
        var byTheGroupOwner = await cluster.Manager.PurgeAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        byTheGroupOwner.IsFailure.ShouldBeTrue(
            "the resource-group owner purged a resource that no longer hangs off his group. The "
            + "re-parent moved the edge and changed nothing about who can reach it, so the window is "
            + "visible to the same people it was before — and a compromised group owner can destroy "
            + "the recovery it exists to provide"
        );

        byTheGroupOwner.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "the refusal names something other than the canonical 404, so it tells a caller who may "
            + "not see this resource that there is one to see"
        );

        // ── The subscription role holder, who is who the window belongs to now ───────────────────
        //
        // ⚠ simone is granted here rather than in the fixture, so the grant is visibly the thing that
        // changes the answer between the two calls. She holds `owner` on the SUBSCRIPTION and nothing
        // below it — the same subject SoftDeleteEdgeTests uses, and for the same reason: reusing
        // victor would route the check through the group edge that has just been taken away.
        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.Subscription,
                IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture)
            ),
            Relations.Owner,
            SubjectRef.Of(ObjectTypes.User, SubscriptionOwner)
        );

        var purged = await cluster.Manager.PurgeAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, SubscriptionOwner)
            },
            TestContext.Current.CancellationToken
        );

        purged.IsSuccess.ShouldBeTrue(
            "the subscription owner cannot purge, so nothing can end the window: the name is held and "
            + "the committed quota is never returned, for the full seven days and then forever. "
            + purged.Error?.Code + " \u2014 " + purged.Error?.Message
        );

        var purge = cluster.For(IsolationCluster.Victim)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(purged.GetValueOrThrow().OperationId));

        for (var i = 0; i < 6; i++) {
            var status = await purge.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                break;
            }
        }

        (await purge.GetAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await cluster.ParentsOfAsync(IsolationCluster.Victim, id)).ShouldBeEmpty(
            "the resource is gone and its parent tuple is not. \u26a0 A purge unlinks a SUBSCRIPTION "
            + "edge rather than a group one \u2014 see IResourceRelationWriter.UnlinkFromSubscriptionAsync "
            + "\u2014 so a purge that built the ordinary subject would delete a tuple that is not there, "
            + "report success, and leave one inert row per purged resource forever"
        );
    }

    /// <summary>The user who holds a role on the subscription and on nothing below it.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>victor</c>, who owns the resource group. A parked resource has left
    ///     that group — docs/plan/08 § Soft delete's fourth decision — so a purge driven by him would
    ///     either fail for the right reason or pass for the wrong one, and only a subject whose only
    ///     grant is above the group can tell the two apart. <c>SoftDeleteEdgeTests</c> names the same
    ///     subject for the same reason.
    /// </remarks>
    const string SubscriptionOwner = "simone";

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
