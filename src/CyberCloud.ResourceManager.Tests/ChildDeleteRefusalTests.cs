using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The other end of <see cref="ParentExistenceTests" />: a delete is refused while the resource
///     still has children.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/08 § Deleting a parent resource that has children decided this and it went
///         unbuilt because the platform could not enumerate children.</b>
///         <i>
///             "a delete is refused while the resource still has children — 409, not a cascade, and
///             not a silent orphan"
///         </i>
///         . Until the counter behind these tests existed, deleting a
///         <c>widgets</c> left every <c>gadgets</c> under it addressable, writable, still drawing
///         <c>QuotaMeter.Resources</c>, and carrying a ReBAC <c>parent</c> edge aimed at a GUID that
///         no longer resolves.
///     </para>
///     <para>
///         ⚠ <b>Why refusal and not a cascade, restated because the tests only show the behaviour.</b>
///         A resource group is a declared lifecycle boundary and a parent resource is not:
///         <c>DELETE …/widgets/w</c> is a single-resource URL and nobody typing it has said anything
///         about the gadgets on it. A cascade would tear down an unknown number of resources with the
///         data in them, return their quota under an operation that names something else, and produce
///         a <c>202</c> indistinguishable from the harmless case.
///     </para>
///     <para>
///         ⚠ <b>This suite addresses <see cref="ResourceManagerCluster.IsolatedSubscription" /></b>,
///         for the reason <see cref="ParentExistenceTests" /> gives at length: every case needs a real
///         parent, each parent is a converged create whose <c>Vcpu</c> lease is committed for the rest
///         of the run, and the shared subscription's budget is a hidden coupling between classes.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ChildDeleteRefusalTests(ResourceManagerCluster cluster) {
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    /// <summary>A widget's address in this suite's own subscription.</summary>
    static ResourceId Parent(string name) =>
        new(
            ResourceManagerCluster.Tenant,
            Subscription,
            "prod",
            ConformingReconciler.TypeName,
            name,
            Guid.Empty
        );

    /// <summary>A gadget's address under a widget — the interleaved shape.</summary>
    static ResourceId Child(string parentName, string name) =>
        new(
            ResourceManagerCluster.Tenant,
            Subscription,
            "prod",
            TestingProvider.ChildTypeName,
            name,
            Guid.Empty,
            parentName
        );

    static WriteRequest Request(ResourceId address) =>
        new() {
            Path = address.Path,
            ApiVersion = TestingProvider.V2026,
            Caller = ResourceManagerCluster.Caller()
        };

    async Task ConvergeAsync(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }

    async Task<ResourceId> CreateParentAsync(string name) {
        var address = Parent(name);

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message ?? "the fixture could not create the parent");
        await ConvergeAsync(created.GetValueOrThrow());
        return address;
    }

    async Task<WriteAccepted> CreateChildAsync(ResourceId child) {
        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = child.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.ChildBody(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message ?? "the fixture could not create the child");
        await ConvergeAsync(created.GetValueOrThrow());
        return created.GetValueOrThrow();
    }

    Task<Result<WriteAccepted>> DeleteAsync(ResourceId address) =>
        cluster.Manager.DeleteAsync(Request(address), TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeletingAParentThatHasAChildIsRefusedAndTheRefusalNamesTheCountAndTheType() {
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("occupied-widget");
        await CreateChildAsync(Child("occupied-widget", "occupant"));

        var refused = await DeleteAsync(parent);

        refused.IsFailure.ShouldBeTrue(
            "a parent with a live child was deleted — every child under it is now addressable, "
            + "writable, still drawing quota, and pointing at a resource that is gone"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceHasChildren);

        // ⚠ THE COUNT AND THE TYPE, because a bare refusal is not recoverable. docs/plan/08's own ⚠:
        // refusing "creates a real failure mode: a child whose own delete is stuck holds its parent
        // undeletable. That is why the answer is a 409 WITH A COUNT rather than a bare refusal — the
        // caller has to be able to see what is holding it."
        refused.Error.Message.ShouldContain("1 of type");
        refused.Error.Message.ShouldContain(TestingProvider.ChildTypeName.ToString());
    }

    [Fact]
    public async Task ARefusedDeleteReleasesNothingAndTheParentIsStillThere() {
        // ⚠ THE HALF-RUN DELETE IS THE FAILURE WORTH GUARDING. The gate sits BEFORE the index release,
        // and the index release is irreversible — docs/plan/06 § Two-phase create makes the name
        // "immediately reusable" the moment it happens. A refusal that had already released would have
        // handed the parent's name to whoever asked next while the parent was still running.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("intact-widget");
        await CreateChildAsync(Child("intact-widget", "intact-gadget"));

        (await DeleteAsync(parent)).IsFailure.ShouldBeTrue();

        (await cluster.Index(parent).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Confirmed, "a refused delete released the parent's name");

        var readBack = await cluster.Manager.ReadAsync(Request(parent), TestContext.Current.CancellationToken);

        readBack.IsSuccess.ShouldBeTrue("a refused delete left the parent unreadable");
        readBack.GetValueOrThrow().ProvisioningState.ShouldBe(
            ProvisioningState.Succeeded,
            "a refused delete moved the parent towards Deleting anyway"
        );
    }

    [Fact]
    public async Task TheChildGateRunsBeforeTheLockCheck() {
        // ⚠ docs/plan/08 puts the refusal "one step before the lock check", and the order is what the
        // caller experiences: someone who must delete three databases first should learn that on the
        // first call rather than after removing a CanNotDelete lock they never needed to touch.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("locked-and-occupied");
        await CreateChildAsync(Child("locked-and-occupied", "held-gadget"));

        SwitchableLockResolver.Level = LockLevel.CanNotDelete;

        var refused = await DeleteAsync(parent);

        SwitchableLockResolver.Reset();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(
            ErrorCode.ResourceHasChildren,
            "the lock check ran first, so a caller who removes the lock still cannot delete and has "
            + "not been told why"
        );
    }

    [Fact]
    public async Task ACallerWhoCannotReadTheParentStillGetsA404AndNotAChildCount() {
        // ⚠ THE CHILD COUNT MUST NOT BECOME AN ENUMERATION ORACLE. docs/plan/07 § The enforcement
        // seam: "404, never 403, on a resource the caller cannot read." The gate sits AFTER the seam
        // for exactly this reason — a 409 that said "this has 3 children" would confirm the resource
        // exists to someone who is not allowed to know that.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("secret-widget");
        await CreateChildAsync(Child("secret-widget", "secret-gadget"));

        SwitchableAuthorizer.GrantOnly("nothing-at-all");

        var refused = await DeleteAsync(parent);

        SwitchableAuthorizer.Reset();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Message.ShouldNotContain("child");
    }

    [Fact]
    public async Task DeletingTheChildFirstLetsTheParentGo() {
        // The recovery the refusal promises. A refusal nobody can act on is a resource nobody can
        // delete, which would be a worse defect than the orphan.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("releasable-widget");
        var child = Child("releasable-widget", "leaving-gadget");
        await CreateChildAsync(child);

        (await DeleteAsync(parent)).IsFailure.ShouldBeTrue("the fixture needs the parent held first");

        var childGone = await DeleteAsync(child);
        childGone.IsSuccess.ShouldBeTrue(childGone.Error?.Message);
        await ConvergeAsync(childGone.GetValueOrThrow());

        var parentGone = await DeleteAsync(parent);

        parentGone.IsSuccess.ShouldBeTrue(
            parentGone.Error?.Message
            ?? "the count never came back down, so the parent is undeletable forever — which is the "
            + "failure mode docs/plan/08's ⚠ on refusing warns about"
        );
    }

    [Fact]
    public async Task AChildWhoseDeleteHasNotConvergedStillHoldsItsParent() {
        // ⚠ THE DECREMENT IS AT CONVERGENCE AND NOT AT THE ACCEPT, AND THIS IS WHAT PINS IT.
        //
        // A delete is accepted long before it finishes, and docs/plan/06 § Two-phase create keeps the
        // resource VISIBLE in Deleting the whole time — "never silently gone while its pods still run
        // and its meter still ticks", which it calls "a billing-dispute prevention measure as much as
        // a correctness one". A child in that state EXISTS: it is readable, it is metered, and its data
        // plane may still be up. A counter that came down when the delete was merely accepted would let
        // the parent be torn down on top of it, which is the orphan this whole gate closes,
        // reintroduced in exactly the window where teardowns get stuck.
        //
        // ⚠ The child's operation is deliberately NOT driven. `gadgets` declares no reconciler and
        // ReconcileDriver converges a reconciler-less type on the first pass, so driving it once is
        // enough to finish the teardown — the accepted-but-unconverged state is reached by leaving it
        // alone, not by making a fake world fail.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("stuck-child-widget");
        var child = Child("stuck-child-widget", "stuck-gadget");
        var created = await CreateChildAsync(child);

        var accepted = await DeleteAsync(child);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        accepted.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Deleting);

        // The child still exists — this is the premise, asserted rather than assumed.
        var stillThere = await cluster.Resource(ResourceManagerCluster.Tenant, created.Resource.Id)
            .GetAsync(TestingProvider.V2026, []);

        stillThere.IsSuccess.ShouldBeTrue("the fixture's premise is a child that is mid-teardown");
        stillThere.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Deleting);

        var refused = await DeleteAsync(parent);

        refused.IsFailure.ShouldBeTrue(
            "the parent was deleted while a child was still tearing down — that child is still "
            + "readable, still metered and possibly still running, and it is now an orphan"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceHasChildren);

        // And when the child's teardown converges, the hold is released — the count comes down from
        // OperationGrain, next to the ReBAC unlink, on the same re-drivable pass.
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.GetValueOrThrow().OperationId);
        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await DeleteAsync(parent)).IsSuccess.ShouldBeTrue(
            "the child converged and the parent is still held, so the count is stuck high and the "
            + "parent is undeletable forever"
        );
    }

    [Fact]
    public async Task AParentThatNeverHadChildrenIsUnaffected() {
        // The regression guard. The gate runs on EVERY delete, including every top-level resource in
        // every other suite, and a gate that refused when the counter was merely absent would fail all
        // of them — loudly, but somewhere else.
        ResourceManagerCluster.ResetDoubles();

        var parent = await CreateParentAsync("childless-widget");

        (await DeleteAsync(parent)).IsSuccess.ShouldBeTrue("a resource with no children was refused");
    }

    [Fact]
    public async Task DeletingAChildDoesNotConsultAChildCounterOfItsOwn() {
        // A leaf's own counter is empty, so the gate is invisible to it. Worth its own case because
        // `gadgets` is the deepest type the fixture has: if the gate mis-read the CHILD's counter as
        // the PARENT's, every child delete would be refused and nothing else here would notice.
        ResourceManagerCluster.ResetDoubles();

        await CreateParentAsync("leaf-host-widget");
        var child = Child("leaf-host-widget", "leaf-gadget");
        await CreateChildAsync(child);

        (await DeleteAsync(child)).IsSuccess.ShouldBeTrue("a childless child was refused its own delete");
    }
}
