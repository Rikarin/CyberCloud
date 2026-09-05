using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Soft delete — docs/plan/08 § Soft delete, all four decisions.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every case here runs against <c>CyberCloud.Testing/vaults</c> and every case in
///         <see cref="DeletePathTests" /> runs against <c>widgets</c>, and the pair is the design.</b>
///         The two types declare the same meters, the same reconciler behaviour and the same body
///         shape; they differ in one registry fact. So "a delete tears the resource down" and "a delete
///         parks it" are the same test over the same arithmetic with one declaration changed, which is
///         what makes the branch the manager takes on <c>SoftDeleteDays</c> observable rather than
///         assumed.
///     </para>
///     <para>
///         ⚠ <b>The sharpest failure this feature has is a soft-deleted resource that is still readable
///         at its old address</b>, and docs/plan/08 chose to move the resource out of the tree rather
///         than flag it in place precisely to make that unreachable by construction.
///         <see cref="TheOldAddressAnswersTheCanonical404OnReadOnDeleteAndOnTheIndexClaim" /> is the
///         assertion, and it checks all three doors.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class SoftDeletePathTests(ResourceManagerCluster cluster) {
    /// <summary>This suite's own subscription.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="ResourceManagerCluster.IsolatedSubscription" />, and this suite has a
    ///     stronger claim on it than the two that already address it.</b> Its remarks describe the
    ///     shared subscription's <c>Vcpu</c> budget as a hidden coupling in which a class that adds a
    ///     couple of creates pushes an unrelated class into <c>QuotaExceeded</c>. That is not
    ///     hypothetical here: this file and <c>ActionDispatchTests</c> were written on two branches at
    ///     once, each green alone, and the merge put eleven tests red across
    ///     <c>OperationTests</c>, <c>ParentEdgeStepTests</c> and <c>ActionBodyTests</c> — none of them
    ///     the ones that spent the budget.
    ///     <para>
    ///         ⚠ And the coupling is worse for a soft-delete suite than for any other, because
    ///         docs/plan/08 § Soft delete makes a soft delete hold its committed quota until the purge.
    ///         A create here that is soft-deleted and not purged keeps its lease by design, so this
    ///         file spends the shared budget in a way no ordinary create/delete pair does.
    ///     </para>
    /// </remarks>
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    /// <summary>A vault's address in this suite's own subscription.</summary>
    /// <remarks>
    ///     ⚠ A local helper rather than a subscription parameter on
    ///     <c>ResourceManagerCluster.VaultAddress</c>, for the reason that helper's own remarks give
    ///     about <c>Address</c>: an argument that can be forgotten is a test that passes for the wrong
    ///     reason. The type stays baked in, so a soft-delete case still cannot be written against the
    ///     hard-delete type by accident.
    /// </remarks>
    static ResourceId VaultAddress(string name, string group = "prod") =>
        new(ResourceManagerCluster.Tenant, Subscription, group, TestingProvider.VaultTypeName, name, Guid.Empty);

    // ── (a) and (b): the resource leaves, and the 404 is the canonical one ──────────────────────

    /// <summary>
    ///     ⚠ <b>The old address is gone on every door, and the <c>404</c> is byte for byte the one a
    ///     name that was never taken gets.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete rejects the "stay in place with a flag" design because it
    ///         <i>"puts an 'unless deleted' clause on every read path, every list, every ReBAC check
    ///         and the index claim, and the feature is then only as good as the least-remembered of
    ///         them"</i>. The three doors below are that list: a read, a second delete, and the index
    ///         claim a create goes through.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The byte-for-byte comparison is the second half and it is the one worth
    ///         defending.</b> A <c>404</c> that differed in shape, body or wording from a genuine
    ///         absence is an oracle just as surely as the <c>410 Gone</c> the document forbids — it
    ///         would let a caller who may not read the resource tell "this name is held by something I
    ///         cannot see" from "this name is free". So the message is compared against a real absence
    ///         at a name nothing ever claimed, rather than merely asserted to be
    ///         <see cref="ErrorCode.ResourceNotFound" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheOldAddressAnswersTheCanonical404OnReadOnDeleteAndOnTheIndexClaim() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("gone-from-here");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        (await Read(address)).IsSuccess.ShouldBeTrue("the resource is readable before the delete");

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        // The 404 a name nothing ever claimed gets, at an address of the same type in the same group.
        var absent = await Read(VaultAddress("never-existed"));
        absent.IsFailure.ShouldBeTrue();

        // ── Door 1: a read ──────────────────────────────────────────────────────────────────────
        var read = await Read(address);
        read.IsFailure.ShouldBeTrue("a soft-deleted resource is not readable at its old address");
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        read.Error.Message.ShouldBe(
            absent.Error!.Message.Replace("never-existed", "gone-from-here", StringComparison.Ordinal),
            "the 404 differs from a genuine absence, which makes it an oracle — docs/plan/08 § Soft "
            + "delete forbids a 410 Gone for exactly this reason and a distinguishable 404 is the same "
            + "leak wearing the right status code"
        );

        read.Error.Target.ShouldBe(absent.Error.Target, "even the target must not differ");

        // ── Door 2: a second delete ─────────────────────────────────────────────────────────────
        var again = await Delete(address);
        again.IsFailure.ShouldBeTrue("a soft-deleted resource cannot be deleted again — it is not there");
        again.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ── Door 3: the index claim ─────────────────────────────────────────────────────────────
        //
        // ⚠ A create at the held name is refused, and it is refused as a CONFLICT rather than as an
        // absence — which is not a contradiction of the two doors above. The caller reaching this point
        // has already passed the enforcement seam on the resource GROUP, and for them a live resource
        // answers 409 too. What must not differ is the answer a caller who cannot read gets, and that
        // is what door 1 pins.
        var recreated = await Create(address);
        recreated.IsFailure.ShouldBeTrue("the name is held for the whole window");
        recreated.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    // ── (e): the name is held, and the entry is not Free ────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The name is held for the whole window, and a restore has somewhere to go because of
    ///     it.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete: Azure holds it — <i>"You can't reuse the name of a key vault
    ///     that was soft-deleted, until the retention period expires"</i> — and <i>"releasing it is the
    ///     cheaper-sounding option and it breaks restore: a name taken by somebody else leaves a
    ///     restore with nowhere to go, so it would have to fail or overwrite, and both are worse than
    ///     making the tenant wait"</i>. This is the exact inverse of
    ///     <c>DeletePathTests.TheIndexIsReleasedFirstSoTheNameIsImmediatelyReusable</c>, which is
    ///     correct for the type that declares no window.
    /// </remarks>
    [Fact]
    public async Task TheNameIsHeldForTheWholeWindowAndTheEntryIsNotFree() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("name-held");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());
        await Delete(address);

        var entry = (await cluster.Index(address).GetAsync()).GetValueOrThrow();

        entry.State.ShouldBe(
            IndexEntryState.SoftDeleted,
            "the name must not come back — a restore would have nowhere to go"
        );

        entry.State.ShouldNotBe(IndexEntryState.Free);
        entry.BoundTo.ShouldBe(created.GetValueOrThrow().Resource.Id);

        // ⚠ Late in the window, not merely immediately after. The claim machine collapses an expired
        // LEASE to Free on read, and a recovery window that shared that behaviour would hand the name
        // away at the instant the resource became unrecoverable — see IndexEntryState.SoftDeleted.
        TestClock.Instance.Advance(TimeSpan.FromDays(6));

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.SoftDeleted, "six days into a seven-day window");

        var stolen = await Create(address);
        stolen.IsFailure.ShouldBeTrue("somebody else must not be able to take the name mid-window");
        stolen.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    // ── (c): the quota moves to the purge, and is returned exactly once ─────────────────────────

    /// <summary>
    ///     ⚠ <b>A delete of a soft-deletable type returns nothing; the purge returns exactly what the
    ///     create committed, on every meter.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete's third decision, and the document calls it <i>"the decision
    ///         most easily got wrong from Azure by analogy"</i>. A soft-deleted Key Vault is free only
    ///         because a vault reserves no capacity; where the deleted thing does hold capacity Azure
    ///         holds both — Managed HSM bills <i>"at their full hourly rate until they're purged"</i>.
    ///         A CyberCloud resource in its window consumes plenty, <i>"because handing the data back
    ///         is the entire feature: the volumes, the PVCs and the memory are all still
    ///         allocated"</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both meters, and <c>QuotaMeter.Resources</c> is the one that matters.</b> The
    ///         document moves <i>the whole of it</i> — <c>Resources</c> too, "even though a
    ///         soft-deleted resource is not one anybody can use, because a per-meter split reintroduces
    ///         the partial restore". A fix that returned the count meter at the delete and the capacity
    ///         meters at the purge would pass a one-meter test and be exactly the defect.
    ///     </para>
    ///     <para>
    ///         This is <c>DeletePathTests.ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter</c>
    ///         with the arithmetic unchanged and the moment moved, which is what the document asks for.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ThePurgeReturnsExactlyWhatTheCreateCommittedOnEveryMeterAndTheDeleteReturnsNothing() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("returns-at-purge");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Subscription);
        var vcpuBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;
        var countBefore = (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed;

        var created = await Create(address, size: 5);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore + 5);
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore + 1);

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        // ── The delete gives back NOTHING ───────────────────────────────────────────────────────
        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(
                vcpuBefore + 5,
                "the volumes, the PVCs and the memory are all still allocated, so the quota stays "
                + "committed — docs/plan/08 § Soft delete"
            );

        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow()
            .Committed.ShouldBe(
                countBefore + 1,
                "QuotaMeter.Resources moves with the rest: a per-meter split reintroduces the partial "
                + "restore"
            );

        // ── The purge gives back everything, once ───────────────────────────────────────────────
        var purged = await Purge(address);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, purged.GetValueOrThrow().OperationId);
        (await operation.DriveAsync()).GetValueOrThrow().State.ShouldBe(OperationState.Succeeded);

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore);
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore);

        // ⚠ NOT TWICE. The operation is re-drivable from a reminder and is already terminal.
        await operation.DriveAsync();

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore);
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore);
    }

    /// <summary>
    ///     ⚠ <b>Ten create/soft-delete/purge cycles leave every meter where they started.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>MeteredAmountTests.TenCreateDeleteCyclesLeaveTheMetersWhereTheyStarted</c> for a
    ///         soft-deletable type, which is what docs/plan/08 § Soft delete asks for — the arithmetic
    ///         unchanged, the moment moved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It catches the mirror of the bug that test was written for.</b> That one caught
    ///         quota drifting <i>up</i>, because a delete returned nothing. This catches quota drifting
    ///         <i>down</i>, because a soft delete that also returned would credit the meter twice — once
    ///         at the delete and again at the purge — and a single cycle hides it inside the
    ///         create/return symmetry. Ten cycles of a double credit is ten resources' worth of free
    ///         allowance, which is the shape a limit fails silently in.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TenCreateSoftDeletePurgeCyclesLeaveTheMetersWhereTheyStarted() {
        ResourceManagerCluster.ResetDoubles();

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Subscription);
        var vcpuBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;
        var countBefore = (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed;

        for (var i = 0; i < 10; i++) {
            // ⚠ The same NAME every time, which is only possible because the purge released it. A
            // cycle that had to invent a new name each round would not notice a purge that left the
            // index parked.
            var address = VaultAddress("cycled");

            var created = await Create(address, size: 3);
            created.IsSuccess.ShouldBeTrue($"cycle {i}: {created.Error?.Message}");
            await Converge(created.GetValueOrThrow());

            var deleted = await Delete(address);
            deleted.IsSuccess.ShouldBeTrue($"cycle {i}: {deleted.Error?.Message}");
            await Converge(deleted.GetValueOrThrow());

            var purged = await Purge(address);
            purged.IsSuccess.ShouldBeTrue($"cycle {i}: {purged.Error?.Message}");
            await Converge(purged.GetValueOrThrow());
        }

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(
                vcpuBefore,
                "ten cycles moved the vcpu meter — a soft delete that returned quota AND a purge that "
                + "returned it again drifts a subscription's allowance downward, which is the mirror "
                + "of the defect MeteredAmountTests was written for"
            );

        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow().Committed.ShouldBe(countBefore);
    }

    // ── The data plane comes down, and the restore puts it back ────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A soft delete tears the data plane down, and a restore applies it again from the body
    ///     the delete did not throw away.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS CASE ASSERTED THE OPPOSITE UNTIL 2026-08-18, AND IT IS THE DEFECT TWO
    ///         PROVIDERS WITHDREW THEIR RECOVERY WINDOWS OVER.</b> It read
    ///         <c>ASoftDeleteLeavesTheDataPlaneUpAndOnlyThePurgeTakesItDown</c> and pinned
    ///         <c>OperationGrain.DriveAsync</c> returning before it ran a pass, on the argument that a
    ///         resource in its window <i>"consumes plenty, because handing the data back is the entire
    ///         feature: the volumes, the PVCs and the memory are all still allocated"</i>. The premise
    ///         is true and the conclusion did not follow: what a restore has to hand back is the DATA,
    ///         and a Kubernetes teardown does not remove data — deleting a <c>StatefulSet</c> leaves
    ///         the claims its <c>volumeClaimTemplate</c> made. What the teardown removes is the running
    ///         half, and leaving THAT standing is a resource "silently gone while its pods still run
    ///         and its meter still ticks", which docs/plan/06 § Two-phase create forbids by name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The quota decision survives the inversion, and it is worth saying why, because the
    ///         old case claimed the two were one fact.</b> docs/plan/08 § Soft delete's rule is that
    ///         soft delete is free exactly when the deleted thing consumes no reserved capacity — and a
    ///         parked resource still does: its volumes are allocated and its name is held. The second
    ///         reason is the one that never depended on the data plane at all: quota held is what makes
    ///         restore total, because a restore that re-reserved would fail against an allowance the
    ///         tenant has spent in the meantime.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The restore half is what makes this a recovery window rather than a slow delete.</b>
    ///         A teardown with no way back is not soft delete under another name; it is the same
    ///         destruction with a wait in front of it. So the objects coming back is asserted here
    ///         rather than left to <see cref="ARestoreBringsTheResourceBackAtItsOldAddressWithWhatWasWritten" />,
    ///         which asks about the address rather than about the data plane.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASoftDeleteTakesTheDataPlaneDownAndARestorePutsItBack() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("still-running");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.Applied.ShouldContainKey(resourceId);

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        FakeWorld.Applied.ShouldNotContainKey(
            resourceId,
            "a delete the tenant was told converged must not leave the workload running — "
            + "docs/plan/06 § Two-phase create, 'never silently gone while its pods still run and its "
            + "meter still ticks'"
        );

        // ⚠ And the teardown was DRIVEN rather than the objects merely being absent. A pass that was
        // never asked for and a pass that ran are indistinguishable from the applied set alone on a
        // resource that had converged, so the reconciler's own delete is what is asserted.
        FakeWorld.Deletes.ShouldContainKey(
            resourceId,
            "a soft delete runs the reconciler's DeleteAsync, exactly as a hard delete does"
        );

        // ⚠ AND THE QUOTA IS STILL COMMITTED, WHICH IS WHAT SEPARATES THIS FROM A HARD DELETE.
        // TenCreateSoftDeletePurgeCyclesLeaveTheMetersWhereTheyStarted pins the arithmetic; this pins
        // that the teardown did not drag the return forward with it.
        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Subscription);
        (await quota.GetUsageAsync(QuotaMeter.Resources)).GetValueOrThrow()
            .Committed.ShouldBeGreaterThan(
                0,
                "the window holds the committed quota until the purge, and tearing the data plane down "
                + "does not end the window"
            );

        // ── And back it comes, from the desired state the park kept ──────────────────────────────
        var restored = await RestoreAndConverge(address);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        FakeWorld.Applied.ShouldContainKey(
            resourceId,
            "a design that tears the data plane down and cannot put it back has not implemented soft "
            + "delete, it has implemented a slower delete"
        );

        // ⚠ NO PURGE TAIL, AND ITS ABSENCE IS THE POINT. A restored resource is Confirmed, so a purge
        // here answers the canonical 404 — there is nothing parked to end the window of. The purge's
        // own behaviour is pinned by ThePurgeReturnsExactlyWhatTheCreateCommittedOnEveryMeterAndTheDeleteReturnsNothing
        // and by the ten-cycle arithmetic; what this case owns is the round trip.
        (await Read(address)).IsSuccess.ShouldBeTrue("and the old address answers again");
    }

    // ── (d): the resource is never invisible ───────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The parent edge moves to the subscription while deleted and back to the resource group
    ///     on restore, and there is no moment with no edge at all.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete: the group tuple <i>"asserts a containment that is no longer
    ///         true. Preserving it is not the conservative choice, it is the wrong one."</i> And the
    ///         reason re-parenting beats dropping: <i>"The resource is never parentless, so the failure
    ///         that made the parent tuple necessary in the first place — a resource nobody can see, and
    ///         a silo lost in that window leaving it that way — cannot happen during the recovery
    ///         window either."</i>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This bug already happened once, on create, before the parent tuple was written</b>
    ///         — see the write path's step 8. Asserting the edge is <i>present and pointing at the
    ///         subscription</i> rather than merely "still present" is what tells a re-parent from a
    ///         no-op.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheParentEdgeMovesToTheSubscriptionWhileDeletedAndBackOnRestore() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("reparented");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        var group = "resourceGroup:" + Subscription.ToString("N") + "-prod";
        var subscription = "subscription:" + Subscription.ToString("N");

        RecordingRelationWriter.Edges[resourceId].ShouldBe(group, "a live resource hangs off its group");

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        RecordingRelationWriter.Edges.ShouldContainKey(
            resourceId,
            "the resource is never parentless — dropping the edge is the option docs/plan/08 § Soft "
            + "delete rejected, because a resource nobody can see is the failure the edge exists to "
            + "prevent"
        );

        RecordingRelationWriter.Edges[resourceId].ShouldBe(
            subscription,
            "who can see a deleted resource becomes who holds subscription-scoped rights, which is "
            + "exactly who Azure gives deletedVaults/read and purge/action to"
        );

        var restored = await RestoreAndConverge(address);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        RecordingRelationWriter.Edges[resourceId].ShouldBe(
            group,
            "the edge moves back on restore — the resource is in its group again"
        );

        // ── And a purge takes the edge with it, whichever one the resource is holding ────────────
        //
        // ⚠ THIS THIRD PHASE EXISTS BECAUSE SABOTAGE FOUND NOTHING WITHOUT IT. Making the purge call
        // UnlinkFromParentAsync — which builds the resource group's subject — instead of
        // UnlinkFromSubscriptionAsync left every test in this file green, because a soft-deleted
        // resource's edge names the subscription and the unlink would have deleted a tuple that was
        // not there, reported success, and left one inert row per purged resource forever with nothing
        // to notice it.
        var deletedAgain = await Delete(address);
        await Converge(deletedAgain.GetValueOrThrow());

        RecordingRelationWriter.Edges[resourceId].ShouldBe(subscription);

        var purged = await Purge(address);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);
        await Converge(purged.GetValueOrThrow());

        RecordingRelationWriter.Edges.ShouldNotContainKey(
            resourceId,
            "the resource is destroyed and its parent tuple is not — the purge has to unlink the edge "
            + "the resource ACTUALLY holds, which by then names the subscription and not the group"
        );
    }

    /// <summary>
    ///     ⚠ <b>A role assignment written directly on the resource is absent after a restore.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete, and it is a security answer rather than a modelling one:
    ///     <i>"The recovery window is used after a compromise or after a decommission somebody wants to
    ///     undo, and those are the cases that decide it. Silently restoring a grant an administrator
    ///     deliberately removed is an error nobody observes. Making somebody re-grant after a restore is
    ///     an error everybody observes and can fix in a minute. Take the visible failure."</i> Both this
    ///     and the edge above are data rather than schema and so are cheap to reverse — but only if a
    ///     test pins the intent now, which is why the document asks for these two by description.
    /// </remarks>
    [Fact]
    public async Task ARoleAssignmentWrittenDirectlyOnTheResourceIsGoneAfterARestore() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("regrant-me");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;

        // Somebody was granted a role on this resource specifically, rather than inheriting one.
        RecordingRelationWriter.Assignments[resourceId] = ["owner@user:bob"];

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        RecordingRelationWriter.Assignments.ShouldNotContainKey(
            resourceId,
            "direct assignments go with the resource and must be recreated on recovery — Azure's "
            + "behaviour, and docs/plan/08 § Soft delete takes it"
        );

        var restored = await RestoreAndConverge(address);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        RecordingRelationWriter.Assignments.ShouldNotContainKey(
            resourceId,
            "and the restore does not put them back — a grant an administrator deliberately removed "
            + "must not come back silently"
        );

        // ⚠ The INHERITED edge is untouched, which is the other half. A drop that took the parent
        // tuple with it would leave the restored resource invisible to its group's role holders — the
        // resource-nobody-can-see failure, arriving through the security fix rather than the modelling
        // one.
        RecordingRelationWriter.Edges.ShouldContainKey(resourceId);
    }

    // ── The restore itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARestoreBringsTheResourceBackAtItsOldAddressWithWhatWasWritten() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("comes-back");

        var created = await Create(address, size: 4);
        await Converge(created.GetValueOrThrow());

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        (await Read(address)).IsFailure.ShouldBeTrue();

        var restored = await RestoreAndConverge(address);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        // ⚠ THE ACCEPT IS `Updating`, NOT `Succeeded`, AND THAT IS THE CONTRACT CHANGING RATHER THAN
        // A WEAKER ASSERTION. A restore re-applies the resource's stored desired state, so it is a
        // long-running operation like every other write and its 202 carries the resource mid-flight.
        // `Succeeded` is asserted below, off the READ, once the operation has converged.
        restored.GetValueOrThrow().Resource.ProvisioningState.ShouldBe(ProvisioningState.Updating);

        var read = await Read(address);
        read.GetValueOrThrow().ProvisioningState.ShouldBe(
            ProvisioningState.Succeeded,
            "the restore's reconcile pass converged"
        );
        read.IsSuccess.ShouldBeTrue("the old address answers again");
        read.GetValueOrThrow().Id.ShouldBe(
            created.GetValueOrThrow().Resource.Id,
            "the same resource came back, not a new one — the GUID is the identity"
        );

        read.GetValueOrThrow().Properties.ShouldContain("\"size\":4");

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Confirmed);
    }

    /// <summary>
    ///     ⚠ <b>Past the window, a restore is refused — and refused with the same <c>404</c> a name
    ///     that holds nothing gets.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A window that can be exceeded and then honoured anyway is not a window.</b> The
    ///     deadline is stamped on the index entry from the type's declared retention and read back
    ///     against the grain's own clock, so this is the one assertion that the number in the registry
    ///     reaches the behaviour at all.
    /// </remarks>
    [Fact]
    public async Task ARestoreAfterTheWindowHasPassedIsRefused() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("too-late");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        // ⚠ SIX DAYS IN IT STILL WORKS, AND THAT HALF IS WHAT MAKES THE REFUSAL BELOW MEAN THE
        // DEADLINE RATHER THAN A RESTORE THAT NEVER WORKS.
        TestClock.Instance.Advance(TimeSpan.FromDays(6));
        var early = await RestoreAndConverge(address);
        early.IsSuccess.ShouldBeTrue($"six days into a seven-day window: {early.Error?.Message}");

        // Park it again, and let the whole window pass this time.
        var reDeleted = await Delete(address);
        reDeleted.IsSuccess.ShouldBeTrue(reDeleted.Error?.Message);
        await Converge(reDeleted.GetValueOrThrow());

        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        var late = await Restore(address);
        late.IsFailure.ShouldBeTrue("eight days into a seven-day window");

        // ⚠ The same ResourceNotFound a name that holds nothing gets, rather than a code that says
        // "expired". "It was there and you are too late" is still an answer about a name the caller may
        // not be entitled to know about.
        late.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    /// <summary>
    ///     ⚠ <b>Restoring something that was never soft-deleted is the same <c>404</c> as everything
    ///     else.</b>
    /// </summary>
    /// <remarks>
    ///     A live resource, a name nobody ever claimed and a type with no recovery window all answer
    ///     identically. Three different answers would let a caller enumerate names through the verb
    ///     that knows soft delete exists — which is the oracle every other refusal on this path is
    ///     shaped to close.
    /// </remarks>
    [Fact]
    public async Task RestoringALiveResourceAnUnknownNameAndAHardDeleteTypeAllAnswerTheSame404() {
        ResourceManagerCluster.ResetDoubles();

        var live = VaultAddress("alive");
        var created = await Create(live);
        await Converge(created.GetValueOrThrow());

        var onLive = await Restore(live);
        var onUnknown = await Restore(VaultAddress("no-such-vault"));

        // ⚠ The hard-delete type, soft-deleted-shaped request. `widgets` declares no window, so there
        // is no recovery to ask about — and the answer must not say so.
        var hard = ResourceManagerCluster.Address("hard-type");
        var hardCreated = await cluster.Manager.WriteAsync(
            new() {
                Path = hard.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        await Converge(hardCreated.GetValueOrThrow());

        var onHardType = await cluster.Manager.RestoreAsync(
            new() { Path = hard.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        onLive.IsFailure.ShouldBeTrue();
        onUnknown.IsFailure.ShouldBeTrue();
        onHardType.IsFailure.ShouldBeTrue();

        onLive.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        onUnknown.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        onHardType.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "a type with no recovery window must not be distinguishable from a name that holds nothing "
            + "— that pair is how a name gets enumerated"
        );
    }

    // ── (f): retention is immutable and purge protection is irreversible ────────────────────────

    /// <summary>
    ///     ⚠ <b>A re-driven delete does not extend the window it is re-driving.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete: <i>"retention is set at creation and immutable afterwards —
    ///         a window a caller can shorten under their own resource is not a recovery window"</i>.
    ///         The platform satisfies that more strongly than the document asks: retention is declared
    ///         on the <i>type</i>, so there is no per-resource property to set at creation and none to
    ///         shorten later, and the delete path stamps the deadline from the registration and never
    ///         from the body.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is left to get wrong is the re-stamp</b>, and it is the direction that matters:
    ///         the delete is re-drivable from a reminder, so a second park an hour later that reset the
    ///         deadline would silently extend every window that ever failed once — a guarantee that
    ///         quietly becomes longer is as broken as one that quietly becomes shorter, because neither
    ///         is the number the platform published.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheRecoveryWindowIsStampedOnceAndARedrivenDeleteDoesNotExtendIt() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("window-fixed");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;

        await Delete(address);

        var stamped = (await cluster.Index(address).GetAsync()).GetValueOrThrow().RecoverableUntil;
        stamped.ShouldBe(TestClock.Instance.UtcNow + TimeSpan.FromDays(7));

        // Three days pass and the park runs again, exactly as a re-driven delete would.
        TestClock.Instance.Advance(TimeSpan.FromDays(3));
        (await cluster.Index(address).SoftDeleteAsync(resourceId, TimeSpan.FromDays(7))).IsSuccess.ShouldBeTrue();

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .RecoverableUntil.ShouldBe(
                stamped,
                "a re-drive must not extend the window it is re-driving — the deadline was promised at "
                + "the delete"
            );
    }

    /// <summary>
    ///     ⚠ <b>Purge protection cannot be turned off, by <c>PUT</c> or by <c>PATCH</c>, and a
    ///     protected resource cannot be purged.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete: <i>"Purge protection is a further opt-in flag that cannot be
    ///     turned off once on, which is the only version of it that is worth anything."</i> Both halves
    ///     are asserted here because either alone is worthless: a purge refusal one <c>PATCH</c> away
    ///     from being bypassed protects against nobody who can write, and a caller who can write is a
    ///     caller who can delete.
    /// </remarks>
    [Fact]
    public async Task PurgeProtectionCannotBeTurnedOffAndAProtectedResourceCannotBePurged() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("protected");

        var created = await Create(address, purgeProtection: true);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        // ── A PUT that omits the flag is a PUT that clears it ───────────────────────────────────
        var cleared = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.VaultBody(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        cleared.IsFailure.ShouldBeTrue("a full PUT omitting the flag asks for it to be cleared");
        cleared.Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ── And a PATCH that names it false is refused too ──────────────────────────────────────
        var patched = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Patch,
                Body = TestingProvider.VaultBody(purgeProtection: false),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        patched.IsFailure.ShouldBeTrue();
        patched.Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ── The purge itself is refused, which is what the flag is for ──────────────────────────
        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        var purged = await Purge(address);
        purged.IsFailure.ShouldBeTrue("a protected resource cannot be purged before its window ends");
        purged.Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ⚠ And the refusal left the resource recoverable rather than half-purged. A purge that
        // released the index before checking protection would leave the name free and the resource
        // unrestorable, which is the worst of both.
        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.SoftDeleted);
        (await RestoreAndConverge(address)).IsSuccess.ShouldBeTrue("the refused purge changed nothing");
    }

    /// <summary>
    ///     ⚠ <b>A resource with purge protection off is purgeable, so the refusal above is the flag and
    ///     not the verb.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the calibration for the test above, and without it that test passes for a
    ///     platform where nothing can ever be purged.</b> Omitting the flag entirely is the case that
    ///     matters: <c>IsPurgeProtected</c> reads an absent pointer as off, which is the fail-open
    ///     direction, and the only thing making that safe is that the builder refuses a type whose
    ///     schema does not declare the property.
    /// </remarks>
    [Fact]
    public async Task AResourceWithoutPurgeProtectionIsPurgeable() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("unprotected");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());
        await Converge((await Delete(address)).GetValueOrThrow());

        var purged = await Purge(address);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        await Converge(purged.GetValueOrThrow());

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Free, "the purge is what finally releases the name");
    }

    // ── The purge permission is separable from the delete permission ────────────────────────────

    /// <summary>
    ///     ⚠ <b>A caller who may delete but may not purge gets a <c>404</c> from the purge and can
    ///     still delete.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete: Azure puts
    ///     <c>Microsoft.KeyVault/locations/deletedVaults/purge/action</c> in Key Vault Contributor's
    ///     <c>notActions</c>, <i>"so 'may delete' and 'may destroy permanently' are genuinely separable
    ///     rights and a role can hold the first without the second"</i>. If the purge checked the delete
    ///     permission the window would protect against nobody who could already delete — which is
    ///     everybody it exists to protect against, since they are the one whose delete put the resource
    ///     there.
    ///     <para>
    ///         ⚠ <b>The refusal is a <c>404</c> and not a <c>403</c>, because the caller is refused the
    ///         READ permission's question too</b> — <c>SwitchableAuthorizer</c> denies the named
    ///         permission and docs/plan/07 § The enforcement seam answers absence when the read is what
    ///         failed. What matters here is that the purge asked a different question from the delete.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task APurgeChecksThePurgePermissionAndNotTheDeletePermission() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("may-delete-only");

        var created = await Create(address);
        await Converge(created.GetValueOrThrow());

        // The delete still works, so the caller plainly holds "may delete".
        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        await Converge(deleted.GetValueOrThrow());

        // ⚠ EVERYTHING EXCEPT PURGE. This is Key Vault Contributor: read, write and delete, with
        // purge/action in notActions.
        SwitchableAuthorizer.GrantOnly("read", "write", "delete");

        var purged = await Purge(address);
        purged.IsFailure.ShouldBeTrue(
            "the purge must consult its own permission — a purge that checked 'delete' would let "
            + "anybody who could delete also destroy, and the recovery window would protect against "
            + "nobody"
        );

        // ⚠ 403 and not 404, and that is the enforcement seam working rather than an inconsistency
        // with every other refusal in this file. docs/plan/07 § The enforcement seam: "403 is returned
        // only when the caller can read the object but not perform the action." This caller holds
        // `read`, so they already know the resource is there; hiding it from them would be the one
        // case the seam does not ask for.
        purged.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        SwitchableAuthorizer.Reset();

        // And with the purge permission it goes through, so the refusal was the permission and not the
        // verb being broken.
        (await Purge(address)).IsSuccess.ShouldBeTrue();
    }

    // ── The way in, which is the half docs/plan/08 recorded as owed ────────────────────────────

    /// <summary>
    ///     ⚠ <b>The two verbs reached through <c>ActionAsync</c> — the method the gateway calls —
    ///     rather than through <c>RestoreAsync</c> and <c>PurgeAsync</c> directly.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete: <i>"<c>RestoreAsync</c> and <c>PurgeAsync</c> exist, are
    ///         implemented on <c>ResourceManagerService</c>, and are covered by
    ///         <c>SoftDeletePathTests</c> — and neither has an HTTP route."</i> Every other case in this
    ///         file calls the two methods directly, which is exactly why that stayed true while the file
    ///         stayed green: a suite that only calls the method cannot notice that nothing else does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the join, and it is the one assertion here that a gateway test cannot
    ///         make.</b> <c>ActionRoutingTests.SoftDeletesTwoVerbsAreReachableOnPost</c> proves the
    ///         gateway dispatches <c>POST …/restore</c> to <c>IResourceManager.ActionAsync</c> against a
    ///         <i>substituted</i> manager; this proves the real one answers that call by restoring.
    ///         Neither half is worth anything alone — a route to a method that refuses, or a method
    ///         nothing routes to, is what was already shipped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What would have to break for this to go red:</b> the fork in <c>ActionAsync</c>
    ///         going away, which sends the request down the ordinary action path — where step 1's index
    ///         resolve refuses a parked binding and the answer is the canonical <c>404</c>. So the
    ///         assertion is the <i>success</i>, and the resource being addressable again afterwards; a
    ///         status-only assertion would pass for a manager that answered <c>202</c> and did nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheRestoreAndPurgeActionsReachTheSoftDeletePath() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("through-the-front-door");

        var created = await Create(address, size: 6);
        await Converge(created.GetValueOrThrow());

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        (await Read(address)).IsFailure.ShouldBeTrue("the parked resource is not addressable");

        var restored = await Action(address, SoftDeletePolicy.RestoreAction);
        restored.IsSuccess.ShouldBeTrue(
            $"POST …/{SoftDeletePolicy.RestoreAction} did not reach RestoreAsync: "
            + $"{restored.Error?.Code} — {restored.Error?.Message}. A ResourceNotFound here is the "
            + "ordinary action path answering for a resource whose index binding is parked, which is "
            + "what happens when ActionAsync stops forking."
        );

        await Converge(restored.GetValueOrThrow());

        var read = await Read(address);
        read.IsSuccess.ShouldBeTrue("the restore put the resource back at its old address");
        read.GetValueOrThrow().Id.ShouldBe(
            created.GetValueOrThrow().Resource.Id,
            "the same resource came back — the GUID is the identity"
        );

        // ⚠ The size the CREATE wrote, not one the action supplied — the restore re-applies the stored
        // body, so the POST carried no desired state of its own and could not have.
        read.GetValueOrThrow().Properties.ShouldContain("\"size\":6");

        // ── And the other verb, on the same resource, from the same door ────────────────────────
        var deletedAgain = await Delete(address);
        await Converge(deletedAgain.GetValueOrThrow());

        var purged = await Action(address, SoftDeletePolicy.PurgeAction);
        purged.IsSuccess.ShouldBeTrue(
            $"POST …/{SoftDeletePolicy.PurgeAction} did not reach PurgeAsync: {purged.Error?.Message}"
        );

        await Converge(purged.GetValueOrThrow());

        // ⚠ The name is free again, which is the only observable that separates a purge from a
        // second soft delete. The index is asked rather than the resource, because a purged resource
        // answers 404 either way.
        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Free,
                "the purge ended the window, so the name is reusable — docs/plan/08 § Soft delete."
            );
    }

    /// <summary>
    ///     ⚠ <b>A type with no recovery window declares neither verb, so neither has a route.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The registry is what gates the route, and this is the assertion that says so.</b>
    ///     <c>RouteStage</c> answers the canonical <c>404</c> to an action
    ///     <c>ResourceTypeRegistration.TryGetAction</c> does not know, so the two names being absent
    ///     here is the whole reason <c>POST …/restore</c> on a hard-delete type is refused at the
    ///     gateway. Asserting it on the registration rather than over HTTP is deliberate: this is the
    ///     fact the gateway reads, and it is the one that changes if <c>ProviderBuilder</c> ever
    ///     synthesises the pair unconditionally.
    /// </remarks>
    [Fact]
    public void AHardDeleteTypeDeclaresNeitherReservedActionAndASoftDeleteTypeDeclaresBoth() {
        cluster.Registry.TryGetType(ConformingReconciler.TypeName, out var hardDelete).ShouldBeTrue();

        hardDelete.SoftDeleteDays.ShouldBe(0, "the widget is the hard-delete half of this file's pair");
        hardDelete.TryGetAction(SoftDeletePolicy.RestoreAction, out _).ShouldBeFalse();
        hardDelete.TryGetAction(SoftDeletePolicy.PurgeAction, out _).ShouldBeFalse();

        cluster.Registry.TryGetType(TestingProvider.VaultTypeName, out var softDelete).ShouldBeTrue();

        softDelete.TryGetAction(SoftDeletePolicy.RestoreAction, out var restore).ShouldBeTrue();
        softDelete.TryGetAction(SoftDeletePolicy.PurgeAction, out var purge).ShouldBeTrue();

        // ⚠ DIFFERENT PERMISSIONS, WHICH IS THE SEPARATION THE WINDOW IS MADE OF. A purge published
        // under `delete` would advertise a right every deleter already holds — the caller the window
        // exists to protect against, because they are the one whose delete parked the resource.
        restore.Permission.ShouldBe(softDelete.WritePermission);
        purge.Permission.ShouldBe(softDelete.PurgePermission);
        purge.Permission.ShouldNotBe(softDelete.DeletePermission);

        // Both answer 202, so the generated document pairs Azure-AsyncOperation with Retry-After.
        restore.LongRunning.ShouldBeTrue();
        purge.LongRunning.ShouldBeTrue();

        // ⚠ And neither names a handler, which ProviderBuilder.Action refuses alongside longRunning.
        // A handler here would be one nothing invokes: ActionAsync forks before it resolves anything.
        restore.HandlerType.ShouldBeNull();
        purge.HandlerType.ShouldBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Invokes an action the way <c>DispatchStage</c> does.</summary>
    /// <param name="address">The resource.</param>
    /// <param name="action">The action name, as the URL's last segment spells it.</param>
    Task<Result<WriteAccepted>> Action(ResourceId address, string action) =>
        cluster.Manager.ActionAsync(
            Request(address) with { Verb = WriteVerb.Post, Action = action },
            TestContext.Current.CancellationToken
        );

    /// <summary>
    ///     ⚠ <b>A purge that cannot remove the volumes its teardown kept does not converge, and does
    ///     not hand the quota back.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete's owed item — <i>"a purge still leaves the volumes … so a
    ///         purged resource returns its quota and leaves its disks"</i> — is two failures in one
    ///         sentence, and this pins the second. The disks are the harder half and are proved
    ///         against a real API server; the ORDERING is provable here, and it is the half that
    ///         decides what an operator is left with when the removal fails. A purge that returned the
    ///         allowance and then failed to reach the claims would let the tenant spend a budget their
    ///         own storage is still occupying.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The failure is produced by the harness having no cluster to reach rather than by a
    ///         stub outcome</b>, which is the state <c>NoClusterConnectionFactory</c> puts every
    ///         resource here in. <c>VolumeReclaimer</c> refuses to converge on it precisely because
    ///         converging would report disks destroyed that were never reached — so what this asserts
    ///         is the refusal itself, not a mock's opinion of one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the same resource with no volumes declared purges cleanly</b>, in the same
    ///         test, so the refusal is attributable to the volumes rather than to anything else this
    ///         harness cannot do.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task APurgeThatCannotRemoveTheVolumesItKeptDoesNotReturnTheQuota() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("keeps-a-disk");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Subscription);
        var vcpuBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;

        var created = await Create(address, size: 3);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        var resourceId = created.GetValueOrThrow().Resource.Id;
        FakeWorld.KeepsVolume[resourceId] = "data";

        var deleted = await Delete(address);
        await Converge(deleted.GetValueOrThrow());

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore + 3);

        var purged = await Purge(address);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, purged.GetValueOrThrow().OperationId);

        OperationStatus status = default!;
        for (var i = 0; i < 5; i++) {
            status = (await operation.DriveAsync()).GetValueOrThrow();
        }

        status.State.ShouldNotBe(
            OperationState.Succeeded,
            "the purge reported success while the claim it named was never reached — that is the "
            + "defect docs/plan/08 § Soft delete records, reported as a green operation"
        );

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow()
            .Committed.ShouldBe(
                vcpuBefore + 3,
                "the reclaim runs BEFORE the committed quota goes back, so a purge that could not "
                + "remove the disks leaves the allowance they occupy held"
            );

        // ── And the same resource, with nothing kept, purges ────────────────────────────────────
        FakeWorld.KeepsVolume.TryRemove(resourceId, out _);

        for (var i = 0; i < 5; i++) {
            status = (await operation.DriveAsync()).GetValueOrThrow();
            if (status.IsTerminal) {
                break;
            }
        }

        status.State.ShouldBe(
            OperationState.Succeeded,
            $"the purge did not finish once there was nothing left to reclaim: {status.Error?.Message}"
        );

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(vcpuBefore);
    }

    // ── The clock-driven half of purge — docs/plan/07 § Azure RBAC ──────────────────────────────

    /// <summary>
    ///     ⚠ <b>An expired window is ended by a mechanism that asks the authorizer nothing, and the
    ///     same call one day earlier is refused.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/08 § Soft delete: <i>"an expiry is not a request, so there is nobody to
    ///         authorize it"</i>. This is the shape docs/plan/07 § Azure RBAC chose over a system
    ///         principal, and the assertion that makes it mean something is <b>negative</b>: the
    ///         authorizer is set to deny every permission and is asked nothing at all.
    ///         <c>SwitchableAuthorizer.Asked</c> is empty afterwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal before the deadline is the half that makes this a deadline rather
    ///         than a door.</b> Without it the test would pass for a mechanism that purged any parked
    ///         resource on request — which is a purge with the permission removed and nothing put in
    ///         its place, and it would look identical from the outside on the day the window ends.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AnExpiredWindowIsEndedWithoutACallerAndAnUnexpiredOneIsNot() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("expired");

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Subscription);
        var vcpuBefore = (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed;

        var created = await Create(address, size: 3);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await Converge((await Delete(address)).GetValueOrThrow());

        // ── Six days into a seven-day window: the clock may not end it ──────────────────────────
        TestClock.Instance.Advance(TimeSpan.FromDays(6));

        var early = await PurgeExpired(address);
        early.IsFailure.ShouldBeTrue("a window that has not ended is not an expiry");

        // ⚠ The canonical absence, which is the same answer a path holding nothing parked gets. A
        // distinct code here would let whatever drives this tell "not yet" from "not there", and its
        // retry interval would then encode how much window is left.
        early.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.SoftDeleted, "the refused mechanism changed nothing");

        // ── Past the deadline, with the authorizer denying everything ───────────────────────────
        TestClock.Instance.Advance(TimeSpan.FromDays(2));

        SwitchableAuthorizer.GrantOnly();
        SwitchableAuthorizer.Asked.Clear();

        var purged = await PurgeExpired(address);
        purged.IsSuccess.ShouldBeTrue(purged.Error?.Message);

        SwitchableAuthorizer.Asked.ShouldBeEmpty(
            "the clock-driven purge reached the authorizer. It carries no subject, so anything it "
            + "asked would be answered for a caller the platform invented in order to pass its own "
            + "check — which is the system principal docs/plan/07 § Azure RBAC declined."
        );

        await Converge(purged.GetValueOrThrow());

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Free, "ending the window is what finally releases the name");

        (await quota.GetUsageAsync(QuotaMeter.Vcpu)).GetValueOrThrow().Committed.ShouldBe(
            vcpuBefore,
            "the committed quota a soft delete kept is returned by the purge, whoever drove it"
        );
    }

    /// <summary>
    ///     ⚠ <b>Purge protection ends when the window ends, which is what both of its own messages
    ///     always said and what the code did not do.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>PurgeAsync</c>'s refusal says the resource <i>"cannot be purged <b>before</b> its
    ///         recovery window ends"</i> and <c>PurgeProtectionRefusalAsync</c>'s says <i>"wait for
    ///         the recovery window to end"</i>, while the condition was the flag alone. So a
    ///         protected resource became <b>permanently undestroyable</b> the moment its window
    ///         closed: unrestorable past the deadline, unpurgeable by anybody, holding its name and
    ///         its committed quota, with — as the message itself said — no request that changes the
    ///         answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted here and the first is what keeps this honest.</b> The
    ///         flag must still refuse inside the window, or this test passes for a platform where
    ///         purge protection does nothing at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task PurgeProtectionRefusesInsideTheWindowAndStopsRefusingAfterIt() {
        ResourceManagerCluster.ResetDoubles();
        var address = VaultAddress("protected-then-expired");

        var created = await Create(address, purgeProtection: true);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        await Converge((await Delete(address)).GetValueOrThrow());

        var inside = await Purge(address);
        inside.IsFailure.ShouldBeTrue("the flag refuses a purge inside the window");
        inside.Error!.Code.ShouldBe(ErrorCode.Conflict);

        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        // ⚠ THE AUTHORIZED FRONT, not the mechanism, and deliberately so: the fix is to the shared
        // rule rather than to the clock-driven path, so a tenant who protected a resource and then
        // waited out its window can destroy it themselves.
        var after = await Purge(address);
        after.IsSuccess.ShouldBeTrue(
            "a purge-protected resource past its window was refused for ever, by everybody: "
            + after.Error?.Message
        );

        await Converge(after.GetValueOrThrow());

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    /// <summary>
    ///     ⚠ <b>The mechanism answers a live resource, an unknown name and a type with no window the
    ///     same way it answers an unexpired one.</b>
    /// </summary>
    /// <remarks>
    ///     The same identity <c>RestoringALiveResourceAnUnknownNameAndAHardDeleteTypeAllAnswerTheSame404</c>
    ///     asserts for the restore verb, for a related reason rather than the same one. Here the
    ///     caller is a mechanism rather than a subject, so there is no oracle to close; what four
    ///     distinguishable answers <i>would</i> give is a driver whose behaviour depends on which
    ///     kind of nothing it found, and every one of those branches is a way to purge something
    ///     that is not expired.
    /// </remarks>
    [Fact]
    public async Task TheMechanismRefusesEverythingThatIsNotAnExpiredWindow() {
        ResourceManagerCluster.ResetDoubles();

        var live = VaultAddress("still-alive");
        await Converge((await Create(live)).GetValueOrThrow());

        var onLive = await PurgeExpired(live);
        var onUnknown = await PurgeExpired(VaultAddress("no-such-vault"));

        var hard = ResourceManagerCluster.Address("no-window");
        await Converge(
            (await cluster.Manager.WriteAsync(
                new() {
                    Path = hard.Path,
                    ApiVersion = TestingProvider.V2026,
                    Verb = WriteVerb.Put,
                    Body = TestingProvider.Body(),
                    Caller = ResourceManagerCluster.Caller()
                },
                TestContext.Current.CancellationToken
            )).GetValueOrThrow()
        );

        var onHard = await PurgeExpired(hard);

        foreach (var refusal in new[] { onLive, onUnknown, onHard }) {
            refusal.IsFailure.ShouldBeTrue();
            refusal.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        }

        (await Read(live)).IsSuccess.ShouldBeTrue("the live resource is untouched");
    }

    /// <summary>
    ///     ⚠ <b>A parked resource is in no listing and in no membership, so there is nothing for a
    ///     "what is recoverable" filter to filter.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This pins the fact that refutes the obvious design for issue #13.</b> That issue
    ///         reads as <i>"one filter over the collection endpoint"</i>, and
    ///         <c>IResourceManager.ListAsync</c> exists with a per-member <c>Check</c>, a page cap
    ///         and a continuation. What it enumerates is <c>IResourceGroupGrain.ListAsync</c>, and a
    ///         parked resource has left that membership: <c>OperationGrain.ParkAsync</c> calls the
    ///         <b>group's</b> <c>CompleteDeleteAsync</c> deliberately, because a member left behind
    ///         would put a name into a listing whose every read is the canonical <c>404</c> —
    ///         handing a caller who may list the group but may not read the resource the
    ///         "something is held here" signal docs/plan/08 § Soft delete refuses a <c>410 Gone</c>
    ///         over. So the filter has an empty input, and a listing of what is recoverable needs an
    ///         enumeration source rather than a predicate over one that does exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE ENUMERATION SOURCE NOW EXISTS AND THIS CASE IS UNCHANGED, WHICH IS THE POINT
    ///         OF SAYING SO.</b> Issue #71 built <c>IParkedResourceRegistryGrain</c> — a second
    ///         collection, keyed <c>parked/{subscriptionId:N}/rg/{name}</c>, written where
    ///         <c>OperationGrain.ParkAsync</c> unlists the member. What it did <b>not</b> do is put
    ///         the member back, because that is the decision docs/plan/08 § Soft delete calls right
    ///         and not the one to reverse. Every assertion below therefore still holds, and
    ///         <see cref="AParkedResourceIsInItsGroupsRegistryOfWhatIsRecoverable" /> is the other
    ///         half: absent from the membership <i>and</i> present somewhere. A change that
    ///         "fixed" #13 by relisting the member would turn this case red, which is what it is for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both doors are asserted, and the second is the one that matters.</b> The listing
    ///         could have been empty because the <c>Check</c> filtered the member out, which would be
    ///         a different platform with the same symptom — so the group's own membership is read
    ///         directly, underneath the filter.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASoftDeletedResourceIsInNoListingBecauseItLeftItsGroupsMembership() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "listing-parked";
        var live = VaultAddress("still-here", group);
        var parked = VaultAddress("gone-away", group);

        // ⚠ A group of its own rather than `prod`, because this case counts what is in the listing
        // and the shared group carries whatever every other case in this class left there.
        (await cluster.Group(live).CreateAsync(live.TenantId, "eu-west-1")).IsSuccess.ShouldBeTrue();

        await Converge((await Create(live)).GetValueOrThrow());
        await Converge((await Create(parked)).GetValueOrThrow());

        var before = await cluster.Manager.ListAsync(
            new() {
                Path = ResourceCollectionId.Of(live).Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        before.GetValueOrThrow().Resources.Length.ShouldBe(2, "both are live and listable");

        await Converge((await Delete(parked)).GetValueOrThrow());

        var after = await cluster.Manager.ListAsync(
            new() {
                Path = ResourceCollectionId.Of(live).Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        after.GetValueOrThrow().Resources.Select(x => x.Name).ShouldBe(["still-here"]);

        // ── Underneath the filter, which is where the finding actually is ───────────────────────
        var members = (await cluster.Group(parked).ListAsync())
            .GetValueOrThrow();

        // ⚠ The calibration, and without it the assertion below passes for an empty membership — a
        // group grain that answered nothing at all would look exactly like the finding.
        members.Select(x => x.CanonicalPath).ShouldContain(
            live.CanonicalPath,
            "the group's membership does not hold the LIVE resource either, so the assertion below "
            + "would be measuring an empty list rather than the park"
        );

        members.Select(x => x.CanonicalPath).ShouldNotContain(
            parked.CanonicalPath,
            "the parked resource is still a member of its group, so ListAsync's page was short for "
            + "the FILTER's reason rather than the membership's — which would make 'list what is "
            + "recoverable' a predicate over an input that exists"
        );

        // And it is still recoverable, so the absence above is a listing gap and not a lost resource.
        (await cluster.Index(parked).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.SoftDeleted);
        (await RestoreAndConverge(parked)).IsSuccess.ShouldBeTrue();
    }

    // ── The second place to look — docs/plan/08 § Soft delete, issue #71 ───────────────────────
    //
    // ⚠ THE THREE CASES BELOW ARE THE OTHER HALF OF THE ONE ABOVE, AND THEY ARE HERE RATHER THAN IN
    // ParkedResourceRegistryTests BECAUSE THE FINDING WAS NEVER ABOUT THE GRAIN. A registry with a
    // correct ParkAsync that nothing calls is exactly the state IResourceGroupGrain was in when
    // docs/plan/08 § Soft delete recorded that "nothing in production calls any of them" — fully
    // implemented, covered, and answering an empty collection for every group in the platform. So
    // each case drives the real verb end to end and reads the registry afterwards.

    /// <summary>
    ///     ⚠ <b>A parked resource is in the group's registry of what is recoverable, which is where
    ///     it went when it left the membership.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The calibration is the same one
    ///         <see cref="ASoftDeletedResourceIsInNoListingBecauseItLeftItsGroupsMembership" /> carries
    ///         and pointing the other way: the live resource must be in the membership and
    ///         <b>not</b> in the registry, or the two assertions below would pass for a platform that
    ///         had simply copied one collection into the other — which is the merge docs/plan/08
    ///         § Soft delete refuses, since it would put a name whose every read is the canonical
    ///         <c>404</c> back into a listing a caller may enumerate.
    ///     </para>
    ///     <para>
    ///         The type filter is asserted against a collection of the <i>other</i> type in the same
    ///         group. That type declares no window, so nothing can ever be parked in it and the
    ///         correct answer is empty — which is only meaningful beside a non-empty answer from the
    ///         collection that does have one, on the same grain, in the same call sequence.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AParkedResourceIsInItsGroupsRegistryOfWhatIsRecoverable() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "registry-parked";
        var live = VaultAddress("stays-live", group);
        var parked = VaultAddress("goes-away", group);

        (await cluster.Group(live).CreateAsync(live.TenantId, "eu-west-1")).IsSuccess.ShouldBeTrue();

        await Converge((await Create(live)).GetValueOrThrow());
        await Converge((await Create(parked)).GetValueOrThrow());

        (await cluster.Parked(live).ListAsync())
            .GetValueOrThrow()
            .ShouldBeEmpty("nothing is parked before the delete");

        await Converge((await Delete(parked)).GetValueOrThrow());

        // ── The membership, which is where it is NOT ─────────────────────────────────────────
        var members = (await cluster.Group(parked).ListAsync()).GetValueOrThrow();

        members.Select(x => x.CanonicalPath).ShouldContain(live.CanonicalPath);
        members.Select(x => x.CanonicalPath).ShouldNotContain(parked.CanonicalPath);

        // ── The registry, which is where it IS ───────────────────────────────────────────────
        var recoverable = (await cluster.Parked(parked).ListAsync()).GetValueOrThrow();

        recoverable.Select(x => x.AddressOf().Name).ShouldBe(["goes-away"]);
        recoverable[0].ResourceId.ShouldBe(
            (await cluster.Index(parked).ResolveSoftDeletedAsync()).GetValueOrThrow(),
            "the entry has to carry the GUID a restore and a purge address the resource by"
        );

        // ── "What is recoverable in this group, of this type" ────────────────────────────────
        var ofVaults = (await cluster.Parked(parked).ListOfTypeAsync(ResourceCollectionId.Of(parked)))
            .GetValueOrThrow();

        var ofWidgets = (await cluster.Parked(parked)
                .ListOfTypeAsync(
                    new(parked.TenantId, parked.SubscriptionId, group, ConformingReconciler.TypeName)
                ))
            .GetValueOrThrow();

        ofVaults.Select(x => x.AddressOf().Name).ShouldBe(["goes-away"]);
        ofWidgets.ShouldBeEmpty("widgets declare no recovery window, so nothing can be parked in one");
    }

    /// <summary>
    ///     ⚠ <b>A restore takes the resource out of the registry and puts it back into the
    ///     membership, so it is in exactly one collection at every ending.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The registry entry going is the half that would be silently missed.</b> A restore
    ///     that relisted the member and left the entry standing would still return <c>200</c>, still
    ///     restore the data plane and still pass every case in this file that predates this one — and
    ///     the registry would then offer a restore of a live resource, which answers <c>404</c> to
    ///     whoever accepts it and tells a caller who may list this collection but may not read the
    ///     resource that the name is held. That is the enumeration oracle docs/plan/07 § The
    ///     enforcement seam closes.
    /// </remarks>
    [Fact]
    public async Task ARestoreTakesTheResourceOutOfTheRegistryAndPutsItBackIntoTheMembership() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "registry-restored";
        var address = VaultAddress("comes-back", group);

        (await cluster.Group(address).CreateAsync(address.TenantId, "eu-west-1")).IsSuccess.ShouldBeTrue();

        await Converge((await Create(address)).GetValueOrThrow());
        await Converge((await Delete(address)).GetValueOrThrow());

        (await cluster.Parked(address).ListAsync())
            .GetValueOrThrow()
            .Select(x => x.AddressOf().Name)
            .ShouldBe(["comes-back"], "the calibration: it was in the registry before the restore");

        (await RestoreAndConverge(address)).IsSuccess.ShouldBeTrue();

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();

        (await cluster.Group(address).ListAsync())
            .GetValueOrThrow()
            .Select(x => x.CanonicalPath)
            .ShouldContain(address.CanonicalPath);

        (await Read(address)).IsSuccess.ShouldBeTrue("and it is readable at its own address again");
    }

    /// <summary>
    ///     ⚠ <b>A purge takes the resource out of the registry, and it is in no collection at all
    ///     afterwards — which is what "there is nothing to recover" means.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An entry left behind by a purge cannot be cleared by anything.</b> Once the name is
    ///     released, <c>RestorableAsync</c> answers the canonical <c>404</c> for that address, so no
    ///     request reaches the registry again; the entry would stand for the life of the group,
    ///     offering a restore of a resource whose grain state, quota and name are all gone. That is
    ///     why the clear runs before the release rather than after it, and why this case reads the
    ///     registry after a purge that converged rather than only after the accept.
    /// </remarks>
    [Fact]
    public async Task APurgeTakesTheResourceOutOfTheRegistryForGood() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "registry-purged";
        var address = VaultAddress("goes-for-good", group);

        (await cluster.Group(address).CreateAsync(address.TenantId, "eu-west-1")).IsSuccess.ShouldBeTrue();

        await Converge((await Create(address)).GetValueOrThrow());
        await Converge((await Delete(address)).GetValueOrThrow());

        (await cluster.Parked(address).ListAsync())
            .GetValueOrThrow()
            .Select(x => x.AddressOf().Name)
            .ShouldBe(["goes-for-good"], "the calibration: it was in the registry before the purge");

        await Converge((await Purge(address)).GetValueOrThrow());

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();

        (await cluster.Group(address).ListAsync())
            .GetValueOrThrow()
            .Select(x => x.CanonicalPath)
            .ShouldNotContain(address.CanonicalPath, "and it did not come back to the membership either");

        (await Restore(address)).Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "there is nothing to recover, and the refusal is the canonical 404"
        );
    }

    /// <summary>
    ///     ⚠ <b>A restore that is refused because the window has passed leaves the registry entry
    ///     exactly where it was — the case the ordering bug destroyed.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the #71 review's blocker (2026-09-05), and it was a real data-loss
    ///         path.</b> <c>RestoreAsync</c> cleared the registry entry and only then met the window
    ///         check inside <c>IndexClaimMachine.Restore</c>. Nothing upstream filtered an expired
    ///         entry — <c>ResolveSoftDeletedAsync</c> answers for any binding the index calls
    ///         <c>SoftDeleted</c> and never reads <c>RecoverableUntil</c> — so the caller got a
    ///         <c>404</c> and the entry was gone permanently: its only writer is
    ///         <c>OperationGrain.ParkAsync</c>, whose delete operation terminated when the resource
    ///         was parked. The resource kept its name and its committed quota, stayed
    ///         <c>SoftDeleted</c>, and appeared in no collection anywhere, which is precisely what
    ///         issue #71 exists to end.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Expired-but-unpurged is the ordinary long-term state and not a corner, which is
    ///         why this case matters more than its rarity suggests.</b> Issue #12's expiry sweeper
    ///         does not exist yet, so nothing ends a window on the clock's account; every parked
    ///         resource that nobody restores or purges arrives here and stays. The other three
    ///         registry cases in this section all use unexpired windows, so none of them would have
    ///         gone red — <see cref="ARestoreAfterTheWindowHasPassedIsRefused" /> drives this exact
    ///         sequence and passes because it never reads the registry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>ParkedAt</c> is asserted unchanged, and that is the assertion that separates
    ///         the fix from a plausible wrong one.</b> A repair that cleared the entry and wrote a
    ///         fresh one would satisfy every other assertion here while restamping the answer to
    ///         "when was this deleted" with the time of the failed restore — a listing of what is
    ///         recoverable would then say a resource seven days past its window had been parked a
    ///         moment ago. The refusal must not touch the entry at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ARestoreRefusedPastTheWindowLeavesTheRegistryEntryUntouched() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "registry-expired";
        var address = VaultAddress("too-late-to-restore", group);

        (await cluster.Group(address).CreateAsync(address.TenantId, "eu-west-1")).IsSuccess.ShouldBeTrue();

        await Converge((await Create(address)).GetValueOrThrow());
        await Converge((await Delete(address)).GetValueOrThrow());

        var before = (await cluster.Parked(address).ListAsync()).GetValueOrThrow();

        before.Select(x => x.AddressOf().Name)
            .ShouldBe(["too-late-to-restore"], "the calibration: it was in the registry before the refusal");

        // ⚠ EIGHT DAYS INTO THE SEVEN-DAY WINDOW `vaults` DECLARES, and past it rather than at its
        // edge so that the refusal below is the deadline and not a boundary condition.
        // ARestoreAfterTheWindowHasPassedIsRefused establishes that six days in still works, which
        // is what makes eight mean the window.
        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        var late = await Restore(address);

        late.IsFailure.ShouldBeTrue("eight days into a seven-day window");
        late.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ── The finding: what the refusal left behind ───────────────────────────────────────────
        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.SoftDeleted,
                "the refused restore did not move the index either, so the entry is still TRUE — "
                + "which is what makes its absence a loss rather than a correct clear"
            );

        var after = (await cluster.Parked(address).ListAsync()).GetValueOrThrow();

        after.Select(x => x.AddressOf().Name).ShouldBe(
            ["too-late-to-restore"],
            "a refused restore must not unlist the resource: nothing re-parks it, so the entry would "
            + "be gone for good and the resource would hold its name and its committed quota in no "
            + "collection at all"
        );

        after[0].ParkedAt.ShouldBe(
            before[0].ParkedAt,
            "the entry is the one the park wrote and not a fresh one — a re-park would restamp "
            + "'when was this deleted' with the time of the failed restore"
        );

        after[0].ResourceId.ShouldBe(before[0].ResourceId);

        // ⚠ AND THE SECOND ATTEMPT IS THE ONE THAT WOULD CATCH A FIX THAT ONLY REORDERED. A repair
        // reached through a path that a retry no longer takes would pass everything above and lose
        // the entry on any later attempt.
        (await Restore(address)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow()
            .Select(x => x.AddressOf().Name)
            .ShouldBe(["too-late-to-restore"], "and the second refusal did not lose it either");

        // ── And the purge, which is what SHOULD end this, is unaffected ─────────────────────────
        //
        // ⚠ THE PURGE SIDE WAS NEVER BROKEN and this proves the fix did not break it: Index's
        // ReleaseAsync has no permanent refusal, so its unpark has nothing that can fail after it.
        // Asserting it here rather than trusting it keeps the expired resource from being one this
        // file can only park.
        await Converge((await Purge(address)).GetValueOrThrow());

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty(
            "the purge is the ending an expired resource has, and it clears the entry"
        );
    }

    Task<Result<WriteAccepted>> PurgeExpired(ResourceId address) =>
        cluster.Manager.PurgeExpiredAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026 },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> Create(ResourceId address, int size = 2, bool? purgeProtection = null) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.VaultBody(size, purgeProtection),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<ResourceSnapshot>> Read(ResourceId address) =>
        cluster.Manager.ReadAsync(Request(address), TestContext.Current.CancellationToken);

    Task<Result<WriteAccepted>> Delete(ResourceId address) =>
        cluster.Manager.DeleteAsync(Request(address), TestContext.Current.CancellationToken);

    Task<Result<WriteAccepted>> Restore(ResourceId address) =>
        cluster.Manager.RestoreAsync(Request(address), TestContext.Current.CancellationToken);

    /// <summary>Restores and drives the restore's operation to a terminal state.</summary>
    /// <remarks>
    ///     ⚠ A restore is a long-running operation now that a soft delete tears the data plane down,
    ///     so a case that only called <c>Restore</c> would assert against a resource still in
    ///     <c>Updating</c> with nothing applied — green for the wrong reason on every one of them.
    /// </remarks>
    async Task<Result<WriteAccepted>> RestoreAndConverge(ResourceId address) {
        var restored = await Restore(address);
        if (restored.IsSuccess) {
            await Converge(restored.GetValueOrThrow());
        }

        return restored;
    }

    Task<Result<WriteAccepted>> Purge(ResourceId address) =>
        cluster.Manager.PurgeAsync(Request(address), TestContext.Current.CancellationToken);

    static WriteRequest Request(ResourceId address) =>
        new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() };

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
