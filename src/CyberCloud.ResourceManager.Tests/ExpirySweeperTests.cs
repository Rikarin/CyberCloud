using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The clock behind <c>PurgeExpiredAsync</c> — <c>IExpirySweeperGrain</c>, issue #12.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What <see cref="SoftDeletePathTests" /> already covers, and what is left for this
///         file.</b> That suite drives the two <i>fronts</i> of purge:
///         <c>SoftDeletePathTests.AnExpiredWindowIsEndedWithoutACallerAndAnUnexpiredOneIsNot</c>
///         calls <c>PurgeExpiredAsync</c> by hand and asserts that the deadline is a deadline, and
///         <c>SoftDeletePathTests.TheMechanismRefusesEverythingThatIsNotAnExpiredWindow</c> asserts
///         that every other kind of nothing answers the same way. Neither one is driven by anything.
///         This file is about the driver: whether the right entries reach that mechanism, what
///         happens to the ones that do not, and what the sweep does to the registry it reads.
///     </para>
///     <para>
///         ⚠ <b>EVERY CASE GETS ITS OWN RESOURCE GROUP, AND THAT IS A CORRECTNESS RULE HERE RATHER
///         THAN TIDINESS.</b> A sweep is per resource group and acts on <i>everything</i> parked in
///         it, so two cases sharing a group would have the first one's leftovers purged by the
///         second one's sweep — and the second case would go green on a report it did not produce.
///         The shared <c>prod</c> group in particular is where <see cref="SoftDeletePathTests" />
///         leaves parked resources on purpose, so nothing here may touch it.
///     </para>
///     <para>
///         ⚠ <b>The reminder itself is not observable from a test, and
///         <c>ExpirySweep.Disarmed</c> is what stands in for it.</b> Orleans exposes
///         <c>GetReminder</c> to the grain and to nobody else, so "armed exactly while there is
///         something parked" cannot be asserted directly from out here. What can be asserted is the
///         decision the grain took — it reports whether the pass found an empty registry and stood
///         down — and both directions of that decision are driven below. What is <b>not</b> covered
///         is whether <c>RegisterOrUpdateReminder</c> was really called, which is
///         <c>ResourceGroupGrain</c>'s reaper's position too.
///     </para>
///     <para>
///         ⚠ <b>The other thing nothing here covers is the per-pass cap.</b>
///         <c>IExpirySweeperGrain.MaxPerSweep</c> is <c>ListRequest.MaxPageSize</c>, so reaching it
///         means parking a hundred and one resources in one group — a minute of cluster time to
///         assert an arithmetic split. What the cap does when it is <i>not</i> reached is covered —
///         <see cref="ASweepEndsAnExpiredWindowWithNobodyAuthorizingIt" /> and
///         <see cref="ASweepOfAGroupWithNothingParkedStandsDown" /> both assert <c>Deferred</c> is
///         zero, so a cap that mis-split a one-entry group would be caught — and what it does when it
///         is reached is not. The consequence of being wrong there is a pass that does less work than
///         it could, which the next tick repeats.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ExpirySweeperTests(ResourceManagerCluster cluster) {
    /// <summary>The same isolated subscription <see cref="SoftDeletePathTests" /> uses.</summary>
    /// <remarks>
    ///     ⚠ Its quota is lifted at cluster start, which matters more here than elsewhere: a
    ///     soft-deleted resource holds its committed amounts until the purge, so a suite that parks
    ///     several and purges them on a clock would otherwise spend the shared budget and fail an
    ///     unrelated class — the coupling that suite's own remarks describe.
    /// </remarks>
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    /// <summary>
    ///     ⚠ <b>A sweep ends an expired window with the authorizer denying everything and asked
    ///     nothing.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is issue #12's whole claim in one case: <i>nothing drives</i>
    ///         <c>PurgeExpiredAsync</c> <i>on a clock</i>, and now something does. The assertion that
    ///         makes it mean something is the negative one — <c>SwitchableAuthorizer</c> is set to
    ///         grant no permission at all and its <c>Asked</c> queue is empty afterwards. A sweeper
    ///         that had quietly grown a caller would fail here rather than pass with a comment.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The index is asserted <c>Free</c> and not merely "the entry is gone", because
    ///         those are different claims and only one of them is the point.</b> Clearing a registry
    ///         entry is something a sweeper could do all by itself; releasing the name is something
    ///         only the purge does, and it is what returns the committed quota and lets the tenant
    ///         re-create at that address.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASweepEndsAnExpiredWindowWithNobodyAuthorizingIt() {
        ResourceManagerCluster.ResetDoubles();

        var address = await ParkedVaultAsync("sweep-expired", "ends-on-the-clock");

        // ⚠ EIGHT DAYS INTO THE SEVEN DAYS `vaults` DECLARES, and past it rather than at its edge so
        // that what is being tested is the deadline and not a boundary condition.
        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        SwitchableAuthorizer.GrantOnly();
        SwitchableAuthorizer.Asked.Clear();

        var swept = await Sweep(address);

        swept.Examined.ShouldBe(1);
        swept.Purged.ShouldBe([address.CanonicalPath]);
        swept.Forgotten.ShouldBeEmpty();
        swept.Kept.ShouldBe(0);
        swept.Deferred.ShouldBe(0);

        SwitchableAuthorizer.Asked.ShouldBeEmpty(
            "an expiry is not a request, so there is nobody to authorize it — docs/plan/07 § Azure "
            + "RBAC took the split precisely so that no check is reached from here"
        );

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Free,
                "the sweep ran the purge and not a cleanup of its own: the name is released, which "
                + "is the half a registry write could never have done"
            );

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();

        swept.Disarmed.ShouldBeTrue(
            "the group has nothing parked left, so the sweeper stands down rather than ticking over "
            + "an empty registry every hour for ever"
        );
    }

    /// <summary>
    ///     ⚠ <b>A window that has not ended is left exactly as the sweep found it, entry and all.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the case that decides whether the sweeper owns the deadline, and it is
    ///         the one a plausible wrong implementation fails.</b> A sweep that read
    ///         <c>IndexEntry.RecoverableUntil</c> out of <c>GetAsync</c> and compared it against its
    ///         own process's clock would pass the case above and would be wrong in the one direction
    ///         that cannot be recovered from — destroying a resource somebody could still have
    ///         restored. Here the clock has not moved at all, so anything that purges is deciding
    ///         with something other than <c>IResourceIndexGrain.ResolveExpiredAsync</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>ParkedAt</c> is asserted unchanged</b>, which is what separates "left alone"
    ///         from "removed and re-added". A sweep that unparked and re-parked would satisfy every
    ///         other assertion here while restamping the answer to "when was this deleted" once an
    ///         hour for the whole window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the restore at the end is the property the recovery window exists for.</b> A
    ///         sweep is not supposed to be visible to a tenant who is still inside their window; the
    ///         only way to say that is to use the window afterwards.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AWindowThatHasNotEndedIsLeftExactlyWhereItWas() {
        ResourceManagerCluster.ResetDoubles();

        var address = await ParkedVaultAsync("sweep-early", "still-restorable");
        var before = (await cluster.Parked(address).ListAsync()).GetValueOrThrow();

        before.Count.ShouldBe(1, "the calibration: the delete parked it");

        var swept = await Sweep(address);

        swept.Examined.ShouldBe(1);
        swept.Purged.ShouldBeEmpty("nothing here is expired — the delete happened a moment ago");
        swept.Forgotten.ShouldBeEmpty("and the entry is true, so it is not a stale one either");
        swept.Kept.ShouldBe(1);

        swept.Disarmed.ShouldBeFalse(
            "the group still has a window running, so the sweeper stays armed to come back for it"
        );

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.SoftDeleted, "the refused pass changed nothing");

        var after = (await cluster.Parked(address).ListAsync()).GetValueOrThrow();

        after.Select(x => x.ResourceId).ShouldBe(before.Select(x => x.ResourceId));
        after[0].ParkedAt.ShouldBe(
            before[0].ParkedAt,
            "left alone rather than removed and re-added — a re-park would move 'when was this "
            + "deleted' on every tick of the window"
        );

        // ── And the window still works, which is the whole reason not to have purged ────────────
        var restored = await Restore(address);
        restored.IsSuccess.ShouldBeTrue($"a sweep must be invisible inside the window: {restored.Error?.Message}");
        await Converge(restored.GetValueOrThrow());

        (await Read(address)).IsSuccess.ShouldBeTrue("and the resource came back");
    }

    /// <summary>
    ///     ⚠ <b>A <c>CanNotDelete</c> lock stops the clock-driven purge exactly as it stops a typed
    ///     one, and the sweep comes back for the resource once the lock is gone.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/07 § Azure RBAC is explicit that this is the one refusal the mechanism
    ///         inherits: a lock is <i>"a tenant's standing, visible refusal of destruction, and a
    ///         clock that overruled it would make the lock mean 'until the platform disagrees'"</i>.
    ///         So a locked resource past its window stays parked — held past its window, which is
    ///         the thing being fixed, by a decision its owner made and can see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second half is the half worth having.</b> A sweeper that gave up on an entry
    ///         it could not purge — struck it off, or stopped asking — would leave a resource that
    ///         nothing ends after the lock is lifted, which is the state issue #12 exists to close
    ///         reached by a different road. The pass keeps the entry, and the next pass purges.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ALockOutranksTheClockAndTheSweepComesBackAfterwards() {
        ResourceManagerCluster.ResetDoubles();

        var address = await ParkedVaultAsync("sweep-locked", "locked-past-its-window");
        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        SwitchableLockResolver.Level = LockLevel.CanNotDelete;

        var refused = await Sweep(address);

        refused.Examined.ShouldBe(1);
        refused.Purged.ShouldBeEmpty("the clock does not outrank a CanNotDelete lock");
        refused.Forgotten.ShouldBeEmpty(
            "and a refusal is not evidence that the entry is false — the index still says "
            + "soft-deleted, so the entry is true and must survive to be retried"
        );

        refused.Kept.ShouldBe(1);
        refused.Disarmed.ShouldBeFalse();

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().Count.ShouldBe(1);

        // ── The lock is lifted, and the next pass is the one that ends it ───────────────────────
        SwitchableLockResolver.Reset();

        var swept = await Sweep(address);

        swept.Purged.ShouldBe([address.CanonicalPath]);

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    /// <summary>
    ///     ⚠ <b>Purge protection does not outlive the window on the clock's path either.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The flag's own two refusals say a resource <i>"cannot be purged <b>before</b> its
    ///         recovery window ends"</i> and <i>"wait for the recovery window to end"</i>, and
    ///         docs/plan/07 § Azure RBAC records that the condition was once the flag alone — so a
    ///         purge-protected resource became permanently undestroyable the moment its window
    ///         closed. That defect is fixed in <c>PurgeCoreAsync</c> and
    ///         <c>SoftDeletePathTests.PurgeProtectionRefusesInsideTheWindowAndStopsRefusingAfterIt</c>
    ///         pins it for the typed front.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is driven again here because the sweeper is what makes the flag's promise
    ///         automatic, and because the two are separable.</b> The mechanism inherits the lock and
    ///         does <i>not</i> inherit purge protection, which is an asymmetry a reader could easily
    ///         get backwards — and a sweeper written to "skip anything protected" would look
    ///         defensive, pass every other case in this file, and leave exactly the resources the
    ///         flag was supposed to protect only until their window ended sitting there for ever.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task PurgeProtectionDoesNotSurviveTheWindowOnTheClocksPathEither() {
        ResourceManagerCluster.ResetDoubles();

        var address = await ParkedVaultAsync("sweep-protected", "protected", purgeProtection: true);
        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        var swept = await Sweep(address);

        swept.Purged.ShouldBe(
            [address.CanonicalPath],
            "purge protection is opt-in protection against a caller, not against the deadline — its "
            + "own refusal says the window ends it"
        );

        (await cluster.Index(address).GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    /// <summary>
    ///     ⚠ <b>A registry entry the index does not agree with is forgotten, and the resource it
    ///     names is not touched.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE RECONCILE HALF, AND THE ORDER OF THE TWO ASSERTIONS IS THE POINT.</b> The
    ///         registry's invariant is <i>an entry exists only while the index says
    ///         <c>SoftDeleted</c></i>, so an entry naming a <b>live</b> resource is false. What a
    ///         sweep must do with it is remove it; what it must never do is act on it. A sweeper that
    ///         trusted the registry and handed every entry straight to <c>PurgeExpiredAsync</c> would
    ///         be one grain call away from destroying a resource nobody deleted — and the only thing
    ///         standing between those two behaviours is that the sweep asks the index first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The entry is written by hand because no ordinary path produces one</b>, which is
    ///         the same reason <c>ParkedResourceRegistryTests</c> writes directly to the grain: every
    ///         writer in the tree re-asks the index, so the state under test here is reachable only
    ///         through the interleaving <c>ResourceManagerService.RepairParkedRegistryAsync</c>'s
    ///         "NARROWED, NOT CLOSED" remarks describe. Reproducing that interleaving with real
    ///         concurrency would be a test that passes on a timing accident; writing the state it
    ///         produces is the same assertion without the flake.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASweepForgetsAnEntryForALiveResourceRatherThanPurgingIt() {
        ResourceManagerCluster.ResetDoubles();

        const string group = "sweep-live";
        var address = VaultAddress("very-much-alive", group);

        await CreateGroupAsync(address);
        await Converge((await Create(address)).GetValueOrThrow());

        var live = (await cluster.Index(address).ResolveAsync()).GetValueOrThrow();

        // The state the race produces, written directly: an entry for a resource the index calls
        // Confirmed. Nothing on any ordinary path can write this — see the remarks.
        (await cluster.Parked(address).ParkAsync(address.WithId(live))).IsSuccess.ShouldBeTrue();

        var swept = await Sweep(address);

        swept.Examined.ShouldBe(1);
        swept.Forgotten.ShouldBe([address.CanonicalPath]);
        swept.Purged.ShouldBeEmpty(
            "the entry is false, and a sweeper that acted on it would destroy a live resource that "
            + "nobody deleted"
        );

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Confirmed, "the resource is exactly where it was");

        (await Read(address)).IsSuccess.ShouldBeTrue("and it still answers at its own address");
    }

    /// <summary>
    ///     ⚠ <b>An entry resurrected over a name the purge has already freed lives one sweep, not
    ///     for ever — which is the half of the known race issue #12 was asked to close.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The race, stated as the code states it.</b>
    ///         <c>ResourceManagerService.RepairParkedRegistryAsync</c> reads the index and then
    ///         writes the registry, two grain calls, and a purge that unparks and releases in the gap
    ///         leaves an entry naming a name that is free. Its remarks used to end that such an entry
    ///         stands <i>permanently</i>, "since nothing can address that resource again" — and they
    ///         warned that this issue's sweeper would make the pair that produces it the likeliest in
    ///         the tree, because the moment a window closes is the moment a sweep purges and a
    ///         tenant's restore starts being refused.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves came true, and this is the second one.</b> The sweep does make the
    ///         pair ordinary. It also takes the permanence away, because something <i>can</i> address
    ///         the resource again: the sweep holds the entry's path and GUID and asks
    ///         <c>ResolveSoftDeletedAsync</c> directly. The race is not closed and this case does not
    ///         claim it is — what it pins is that the wrong entry survives at most one
    ///         <c>IExpirySweeperGrain.SweepPeriod</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The freed name is re-created afterwards, which is the assertion that would catch
    ///         a "fix" that only hid the entry.</b> A stale entry over a free name is not merely
    ///         untidy: while it stands, a listing of what is recoverable in the group offers a
    ///         restore of a resource that no longer exists, and it says the name is held to a caller
    ///         who may list the collection but may not read the resource — the enumeration oracle
    ///         docs/plan/08 § Soft delete refuses a <c>410 Gone</c> over.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task ASweepForgetsAnEntryResurrectedOverAFreedName() {
        ResourceManagerCluster.ResetDoubles();

        var address = await ParkedVaultAsync("sweep-resurrected", "gone-for-good");
        var parked = (await cluster.Parked(address).ListAsync()).GetValueOrThrow();
        var oldId = parked[0].ResourceId;

        TestClock.Instance.Advance(TimeSpan.FromDays(8));

        // ⚠ The purge's operation is deliberately not driven, and it does not have to be: the name
        // is released on the REQUEST path — PurgeCoreAsync unparks and calls ReleaseAsync before it
        // starts an operation at all, which is docs/plan/06 § Two-phase create's "release the index
        // first (so the name is immediately reusable), then tear down the data plane". Everything
        // this case asserts is on that side of the line, and a sweep hands back paths rather than
        // operation ids, so there is nothing here to poll.
        (await Sweep(address)).Purged.ShouldBe([address.CanonicalPath]);

        (await cluster.Index(address).GetAsync()).GetValueOrThrow()
            .State.ShouldBe(IndexEntryState.Free, "the calibration: the sweep released the name");

        // The interleaving's output, written directly: the repair's re-park landing after the
        // purge's release. Nothing on any ordinary path can write this — see the remarks on
        // ASweepForgetsAnEntryForALiveResourceRatherThanPurgingIt.
        (await cluster.Parked(address).ParkAsync(address.WithId(oldId))).IsSuccess.ShouldBeTrue();

        var swept = await Sweep(address);

        swept.Forgotten.ShouldBe(
            [address.CanonicalPath],
            "the index says the name holds nothing, so the entry is false and this pass is what "
            + "makes 'long' recoverable instead of permanent"
        );

        swept.Purged.ShouldBeEmpty();
        (await cluster.Parked(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();
        swept.Disarmed.ShouldBeTrue();

        // ── And the name really was free the whole time, which is what made the entry a lie ─────
        var recreated = await Create(address);
        recreated.IsSuccess.ShouldBeTrue($"the name was released and is re-usable: {recreated.Error?.Message}");
        await Converge(recreated.GetValueOrThrow());
    }

    /// <summary>
    ///     ⚠ <b>A sweep of a group with nothing parked stands down instead of ticking for ever.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ The cost half of <c>ResourceGroupGrain.ArmOrDisarmAsync</c>'s decision, reached from the
    ///     other side: a standing reminder per resource group platform-wide would be a row per group
    ///     and a tick per group per hour, for ever, to look at a registry that is empty for all but a
    ///     few days of a group's life. The arming rule that makes this safe is the mirror of it —
    ///     every writer that <i>adds</i> a registry entry arms the sweeper — and it is asserted from
    ///     the other end by <see cref="AWindowThatHasNotEndedIsLeftExactlyWhereItWas" />, whose pass
    ///     finds something and does not stand down.
    /// </remarks>
    [Fact]
    public async Task ASweepOfAGroupWithNothingParkedStandsDown() {
        ResourceManagerCluster.ResetDoubles();

        var address = VaultAddress("never-created", "sweep-empty");
        await CreateGroupAsync(address);

        var swept = await Sweep(address);

        swept.Examined.ShouldBe(0);
        swept.Purged.ShouldBeEmpty();
        swept.Forgotten.ShouldBeEmpty();
        swept.Deferred.ShouldBe(0);
        swept.Disarmed.ShouldBeTrue();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A vault's address in this suite's own subscription and its own resource group.</summary>
    /// <remarks>
    ///     ⚠ The group is a required argument rather than a default, which is the rule this file's
    ///     remarks give: a sweep acts on everything parked in one group, so a case that forgot to say
    ///     which group would be reading somebody else's leftovers.
    /// </remarks>
    static ResourceId VaultAddress(string name, string group) =>
        new(ResourceManagerCluster.Tenant, Subscription, group, TestingProvider.VaultTypeName, name, Guid.Empty);

    /// <summary>Creates a resource, deletes it, and drives both to convergence.</summary>
    /// <returns>The address, with a parked resource behind it.</returns>
    async Task<ResourceId> ParkedVaultAsync(string group, string name, bool? purgeProtection = null) {
        var address = VaultAddress(name, group);

        await CreateGroupAsync(address);

        var created = await Create(address, purgeProtection);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        await Converge(deleted.GetValueOrThrow());

        (await cluster.Parked(address).ListAsync()).GetValueOrThrow()
            .Count.ShouldBe(1, "the calibration: the soft delete parked exactly this one resource");

        return address;
    }

    async Task CreateGroupAsync(ResourceId address) {
        var made = await cluster.Group(address).CreateAsync(address.TenantId, "eu-west-1");
        made.IsSuccess.ShouldBeTrue(made.Error?.Message);
    }

    async Task<ExpirySweep> Sweep(ResourceId address) {
        var swept = await cluster.Sweeper(address).SweepAsync();
        swept.IsSuccess.ShouldBeTrue(swept.Error?.Message);

        return swept.GetValueOrThrow();
    }

    Task<Result<WriteAccepted>> Create(ResourceId address, bool? purgeProtection = null) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.VaultBody(2, purgeProtection),
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

    static WriteRequest Request(ResourceId address) =>
        new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() };

    /// <summary>Drives an accepted operation to a terminal state.</summary>
    async Task Converge(WriteAccepted accepted) {
        if (accepted is null || accepted.OperationId == Guid.Empty) {
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
