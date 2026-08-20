using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The resource group's half of docs/plan/06 § Two-phase create, driven through the real write
///     path rather than against the grain on its own.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite exists because <c>TwoPhaseCreateTests</c> and <c>DeleteOrderingTests</c>
///         were both green while nothing in the platform called the methods they cover.</b>
///         <c>IResourceGroupGrain</c> has owned <c>BeginCreateAsync</c>, <c>CompleteCreateAsync</c>,
///         <c>BeginDeleteAsync</c>, <c>FailDeleteAsync</c>, <c>CompleteDeleteAsync</c>,
///         <c>ListAsync</c> and <c>ListOrphansAsync</c> since that document was written, and every
///         test of them called the grain directly. The write path never did — so every group's
///         membership was empty, and a listing built on it would have answered "no resources" for a
///         group full of them <i>and been right</i>, which is the worst shape a listing can have.
///     </para>
///     <para>
///         ⚠ <b>So every case here starts at <c>IResourceManager</c> and never at the grain.</b> The
///         two suites in <c>CyberCloud.Tenancy.Tests</c> already pin what the grain does when it is
///         called correctly; what was missing, and what is asserted below, is that anything calls it
///         at all and in the order docs/plan/06 fixes.
///     </para>
///     <para>
///         ⚠ <b><see cref="ResourceManagerCluster.IsolatedSubscription" /></b>, for the reason that
///         property's remarks give: several cases here create resources and soft-delete them without
///         purging, which holds committed quota for the rest of the run by design.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class GroupMembershipTests(ResourceManagerCluster cluster) {
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    // ── The create ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A create puts the resource into its group's membership, and it is <c>Creating</c>
    ///     until the operation converges.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Two-phase create, step 2, from the group's side. The two halves are asserted
    ///     separately and in that order, because a step that recorded the member only once the
    ///     operation had finished would pass a "the member is Succeeded at the end" assertion while
    ///     leaving every in-flight create invisible to a listing — which is the window a create
    ///     actually spends most of its life in.
    /// </remarks>
    [Fact]
    public async Task ACreateRecordsTheMemberInCreatingAndTheConvergedOperationStampsItSucceeded() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("joined");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var resourceId = created.GetValueOrThrow().Resource.Id;

        var whileCreating = await MemberOf(address, resourceId);
        whileCreating.ShouldNotBeNull("a create that is still running is already in its group's membership");
        whileCreating.State.ShouldBe(ProvisioningState.Creating);
        whileCreating.CanonicalPath.ShouldBe(address.WithId(resourceId).CanonicalPath);

        await Converge(created.GetValueOrThrow());

        var afterwards = await MemberOf(address, resourceId);
        afterwards.ShouldNotBeNull("a converged create is still a member");
        afterwards.State.ShouldBe(ProvisioningState.Succeeded);
    }

    /// <summary>
    ///     ⚠ <b>A write into a resource group that does not exist is refused, which it was not
    ///     before.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the new <i>failure mode</i> the membership record brings with it, and it is
    ///         Azure's behaviour: <c>ResolveAsync</c> validates the tenant, the subscription, the
    ///         type, the api-version and a child's parent <i>resource</i>, and never the group — so a
    ///         <c>PUT</c> into a group nobody had created used to succeed, leaving a resource that
    ///         belonged to nothing, inherited no lock and no role assignment from a group, and could
    ///         not be reached by anything walking the hierarchy downwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second half is the one worth defending: the refusal leaves nothing
    ///         behind.</b> The step sits after the index claim, so a refusal that left a confirmed
    ///         binding would make the name permanently unusable — the caller creates the group, retries
    ///         the identical <c>PUT</c>, and gets a <c>409</c> on a resource that does not exist. The
    ///         retry below is what proves the claim expired rather than stuck.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AWriteIntoAResourceGroupThatDoesNotExistIsRefusedAndLeavesTheNameUsable() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("homeless", "no-such-group");

        var refused = await Create(address);

        refused.IsFailure.ShouldBeTrue(
            "a resource group is a lifecycle unit and not a string in a path — a write into one that "
            + "does not exist has nowhere to record its membership"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceGroupNotFound);

        // ⚠ The index claim was never confirmed, so the name is not held by the refusal.
        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldNotBe(
            IndexEntryState.Confirmed,
            "a refused write must not leave a confirmed binding, or the retry that follows the group's "
            + "creation would answer 409 for a resource that does not exist"
        );

        // Create the group and the identical request goes through.
        var made = await cluster.Group(address).CreateAsync(ResourceManagerCluster.Tenant, "eu-west-1");
        made.IsSuccess.ShouldBeTrue(made.Error?.Message);

        var accepted = await Create(address);
        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        await Converge(accepted.GetValueOrThrow());

        var member = await MemberOf(address, accepted.GetValueOrThrow().Resource.Id);
        member.ShouldNotBeNull();
        member.State.ShouldBe(ProvisioningState.Succeeded);
    }

    /// <summary>
    ///     ⚠ <b>A create that fails terminally leaves the member <c>Failed</c> and not
    ///     <c>Creating</c>, which is what keeps the reaper off it.</b>
    /// </summary>
    /// <remarks>
    ///     <c>IResourceGroupGrain.ListOrphansAsync</c> enumerates members that have been
    ///     <c>Creating</c> for longer than a threshold, and docs/plan/06 § Two-phase create has a
    ///     per-subscription reaper reminder sweep them. A failed create whose member was never stamped
    ///     would sit in <c>Creating</c> forever and be swept — and a sweep is a delete, so the failure
    ///     is a reaper tearing down a resource whose owner is still looking at the error.
    /// </remarks>
    [Fact]
    public async Task ACreateThatFailsTerminallyLeavesTheMemberFailedRatherThanCreating() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("doomed");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailWith[resourceId] = "the manifest was rejected";

        var status = await cluster
            .Operation(ResourceManagerCluster.Tenant, created.GetValueOrThrow().OperationId)
            .DriveAsync();

        status.GetValueOrThrow().State.ShouldBe(OperationState.Failed);

        var member = await MemberOf(address, resourceId);
        member.ShouldNotBeNull("a failed create still exists and is still listed");
        member.State.ShouldBe(
            ProvisioningState.Failed,
            "a member left in Creating is what ListOrphansAsync hands to the reaper"
        );
    }

    /// <summary>
    ///     ⚠ <b>A member that never got its resource is what <c>ListOrphansAsync</c> returns, and
    ///     until the write path recorded membership there was nothing for it to return.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Two-phase create: an orphan is a resource grain with durable state and no
    ///     confirmed index, "swept by a per-subscription reaper reminder". The reaper itself is not
    ///     built — <see cref="IResourceGroupGrain.ListOrphansAsync" /> still has no production caller —
    ///     and this case does not pretend otherwise. What it pins is the half that <i>is</i> built:
    ///     the enumeration now has something in it, so the reaper has an inventory to read when it
    ///     arrives rather than an empty one that looks like a clean subscription.
    /// </remarks>
    [Fact]
    public async Task AnUnconvergedCreateIsEnumeratedAsAnOrphanOnceItIsOldEnough() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("never-finished");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var resourceId = created.GetValueOrThrow().Resource.Id;

        var young = await cluster.Group(address).ListOrphansAsync(TimeSpan.FromHours(1));
        young.GetValueOrThrow()
            .ShouldNotContain(
                x => x.ResourceId == resourceId,
                "a create that started a moment ago is not an orphan — the threshold is the whole guard"
            );

        TestClock.Instance.Advance(TimeSpan.FromHours(2));

        var old = await cluster.Group(address).ListOrphansAsync(TimeSpan.FromHours(1));
        old.GetValueOrThrow().ShouldContain(x => x.ResourceId == resourceId);

        // And converging it takes it back out, because it is no longer Creating.
        await Converge(created.GetValueOrThrow());

        var swept = await cluster.Group(address).ListOrphansAsync(TimeSpan.FromHours(1));
        swept.GetValueOrThrow()
            .ShouldNotContain(
                x => x.ResourceId == resourceId,
                "a converged create is not an orphan, whatever its age"
            );
    }

    // ── The delete, which is the reverse order and the harder half ──────────────────────────────

    /// <summary>
    ///     ⚠ <b>A delete marks the member <c>Deleting</c> and it <i>stays listed</i> until the
    ///     teardown converges — and a teardown that fails keeps it listed with the reason.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Two-phase create: "A resource whose data plane teardown fails is left in
    ///     <c>Deleting</c> with a retry reminder and is <i>visible</i> in listings with that state —
    ///     never silently gone while its pods still run and its meter still ticks. That last clause is
    ///     a billing-dispute prevention measure as much as a correctness one." This is the listing that
    ///     sentence is about, so the failing teardown is the case rather than a variant of it: removing
    ///     the member at the accept would pass a plain create-delete test and fail this one.
    /// </remarks>
    [Fact]
    public async Task ADeleteWhoseTeardownFailsKeepsTheMemberListedAsDeletingWithTheReason() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("stuck");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var atAccept = await MemberOf(address, resourceId);
        atAccept.ShouldNotBeNull("a delete that has been accepted has not happened yet");
        atAccept.State.ShouldBe(ProvisioningState.Deleting);

        // ⚠ Two retryable passes and NOT past the sixty-minute ceiling. An operation driven to its
        // timeout is terminally Failed, and the member's state after a terminal failure is a different
        // question from its state while the teardown is still being retried — which is the state
        // docs/plan/06 § Two-phase create's sentence is about and the one a stuck resource spends its
        // life in.
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, deleted.GetValueOrThrow().OperationId);

        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Running);
        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Running);

        var stuck = await MemberOf(address, resourceId);
        stuck.ShouldNotBeNull(
            "a resource whose pods are still running must never vanish from the listing — this is the "
            + "billing-dispute prevention measure"
        );

        stuck.State.ShouldBe(
            ProvisioningState.Deleting,
            "FailDeleteAsync deliberately cannot move the member to Failed: a Failed member reads as "
            + "'exists and is broken', which is what a resource that is still coming down is not"
        );

        stuck.LastFailure.ShouldContain("the API server refused the delete");

        stuck.TeardownAttempts.ShouldBeGreaterThan(
            1,
            "TeardownAttempts is a count, so it is recorded per failed pass and not once at the end"
        );

        // Now let it converge, from the same operation, and the member goes.
        FakeWorld.FailTeardownWith.TryRemove(resourceId, out _);
        var finished = await operation.DriveAsync();
        finished.GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await MemberOf(address, resourceId)).ShouldBeNull("the member goes when the resource does");
    }

    /// <summary>
    ///     ⚠ <b>The order is the reverse of the create's: the index is free while the member is still
    ///     listed.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Two-phase create: "release the index first (so the name is immediately
    ///     reusable), then tear down the data plane, then delete the grain state." Asserting the two
    ///     together is what makes the order observable — either one alone is satisfied by a delete that
    ///     did both at once, or by one that did neither.
    /// </remarks>
    [Fact]
    public async Task TheNameIsFreeBeforeTheMemberIsGone() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("reverse-order");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.FailTeardownWith[resourceId] = "the API server refused the delete";

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Free, "the name comes back immediately");

        var member = await MemberOf(address, resourceId);
        member.ShouldNotBeNull("and the resource is still listed, because it is still there");
        member.State.ShouldBe(ProvisioningState.Deleting);
    }

    // ── Soft delete, where "leaves the group" and "is deleted" are different questions ───────────

    /// <summary>
    ///     ⚠ <b>A soft delete takes the resource out of its group's membership, and a restore puts it
    ///     back.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete moves the resource out of the tree rather than flagging it in
    ///         place, and the group's listing is one of the read paths that decision exists to keep
    ///         clean: a member left behind would put a name into the listing whose every read is the
    ///         canonical <c>404</c>, handing a caller who may list the group but may not read the
    ///         resource exactly the "something is held here" signal that document refuses a
    ///         <c>410 Gone</c> over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It leaves at the <i>park</i> and not at the accept</b>, which is the same rule the
    ///         hard delete follows: until the teardown converges the pods are still up and the meter is
    ///         still ticking, so the resource is still in the group and still listed as
    ///         <c>Deleting</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASoftDeleteTakesTheResourceOutOfTheGroupAtTheParkAndARestorePutsItBack() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("recoverable");

        var created = await CreateVault(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        (await MemberOf(address, resourceId)).ShouldNotBeNull();

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var atAccept = await MemberOf(address, resourceId);
        atAccept.ShouldNotBeNull("a soft delete is accepted long before its data plane is down");
        atAccept.State.ShouldBe(ProvisioningState.Deleting);

        await Converge(deleted.GetValueOrThrow());

        (await MemberOf(address, resourceId)).ShouldBeNull(
            "a parked resource hangs off its SUBSCRIPTION, not its group — docs/plan/08 § Soft delete"
        );

        // ── And the restore is the exact reverse ────────────────────────────────────────────────
        var restored = await cluster.Manager.RestoreAsync(
            Request(address),
            TestContext.Current.CancellationToken
        );

        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        var rejoining = await MemberOf(address, resourceId);
        rejoining.ShouldNotBeNull("the restore puts it back into the group before it is back in service");
        rejoining.State.ShouldBe(
            ProvisioningState.Creating,
            "a restore re-applies the data plane, so the member is not Succeeded until it converges"
        );

        await Converge(restored.GetValueOrThrow());

        var back = await MemberOf(address, resourceId);
        back.ShouldNotBeNull();
        back.State.ShouldBe(ProvisioningState.Succeeded);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static ResourceId Address(string name, string group = "prod") =>
        new(
            ResourceManagerCluster.Tenant,
            Subscription,
            group,
            ConformingReconciler.TypeName,
            name,
            Guid.Empty
        );

    static ResourceId VaultAddress(string name, string group = "prod") =>
        new(
            ResourceManagerCluster.Tenant,
            Subscription,
            group,
            TestingProvider.VaultTypeName,
            name,
            Guid.Empty
        );

    static WriteRequest Request(ResourceId address) =>
        new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() };

    /// <summary>The group's membership record for one resource, or <c>null</c> when it holds none.</summary>
    /// <remarks>
    ///     ⚠ Read through <see cref="IResourceGroupGrain.ListAsync" /> rather than through a lookup,
    ///     because the listing is the thing under test: a member the grain holds but the listing does
    ///     not return is the failure a per-id read would not see.
    /// </remarks>
    async Task<ResourceGroupMember?> MemberOf(ResourceId address, Guid resourceId) {
        var listed = await cluster.Group(address).ListAsync();
        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);

        return listed.GetValueOrThrow().FirstOrDefault(x => x.ResourceId == resourceId);
    }

    Task<Result<WriteAccepted>> Create(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> CreateVault(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.VaultBody(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> Delete(ResourceId address) =>
        cluster.Manager.DeleteAsync(Request(address), TestContext.Current.CancellationToken);

    async Task Converge(WriteAccepted accepted) {
        if (accepted.OperationId == Guid.Empty) {
            return;
        }

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
