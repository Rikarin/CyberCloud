using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/06 § Two-phase create, at every interruption point the document enumerates.
/// </summary>
/// <remarks>
///     <para>
///         The four steps: <b>1</b> claim the name (durable, 5-minute lease); <b>2</b> create the
///         resource grain and write durable desired state; <b>3</b> confirm the claim; <b>4</b>
///         return <c>202</c>. The two named failures:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Die between 1 and 3</b> → "the claim expires and the name is free again, and the
///             orphaned resource grain (durable state, no confirmed index) is swept by a
///             per-subscription reaper reminder".
///         </item>
///         <item>
///             <b>Die between 3 and 4</b> → "the resource exists and the caller retries the
///             <c>PUT</c> — which is idempotent because <c>PUT</c> with the same body on an existing
///             resource is a no-op".
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             Interruption here means the activation is destroyed, not that a boolean was
///             flipped.
///         </b>
///         Every test below calls <c>DeactivateAsync</c> on the real grain mid-flow, so
///         the in-memory state is gone and everything the grain knows afterwards comes back out of
///         PostgreSQL. A test that set a "pretend we died" flag would be asserting against its own
///         mock of the failure — which is precisely what ADR-018 forbids.
///     </para>
///     <para>
///         The orchestrator that drives these four steps in production is the resource manager
///         (docs/plan/08), which is deliberately not built here. These tests <i>are</i> that
///         orchestrator, one step at a time, so that the interruption can be placed between any two
///         of them.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class TwoPhaseCreateTests(TenancyCluster cluster) {
    /// <summary>Documented for the reader of <see cref="WaitForDeactivation" />.</summary>
    internal const string StateSurvivedDeactivation =
        "every post-deactivation assertion in this file reads a value that can only have come from "
        + "durable storage, because the value was written before the activation was dropped.";

    [Fact]
    public async Task TheHappyPathClaimsThenCreatesThenConfirms() {
        var tenant = Tenant(1);
        var address = await GroupAndAddress(tenant, "happy-path", "web-01");
        var index = cluster.ResourceIndexGrain(address);
        var group = Group(address);

        // 1. Claim.
        var claim = await index.TryClaimAsync(address, address.Id);
        claim.GetValueOrThrow().State.ShouldBe(IndexEntryState.Claimed);
        claim.GetValueOrThrow().BoundTo.ShouldBe(address.Id);

        // ⚠ Not yet resolvable. A claim under lease may never become a resource, so handing its
        // GUID out would let a caller address something that does not exist.
        (await index.ResolveAsync()).IsFailure.ShouldBeTrue();

        // 2. Create.
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        // 3. Confirm.
        (await index.ConfirmAsync(address.Id)).GetValueOrThrow()
            .State
            .ShouldBe(IndexEntryState.Confirmed);

        (await index.ResolveAsync()).GetValueOrThrow().ShouldBe(address.Id);

        // 4. …and the operation completes.
        (await group.CompleteCreateAsync(address.Id, ProvisioningState.Succeeded)).IsSuccess
            .ShouldBeTrue();

        var members = (await group.ListAsync()).GetValueOrThrow();
        members.ShouldContain(x => x.ResourceId == address.Id && x.State == ProvisioningState.Succeeded);
    }

    [Fact]
    public async Task ASecondCreateOfTheSameNameByADifferentResourceIsAConflict() {
        var tenant = Tenant(2);
        var address = await GroupAndAddress(tenant, "conflict", "web-01");

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();

        var rival = address with { Id = Guid.NewGuid() };
        var rejected = await cluster.ResourceIndexGrain(rival).TryClaimAsync(rival, rival.Id);

        rejected.IsFailure.ShouldBeTrue();
        rejected.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    // ── Interruption 1: die between claim and confirm ──────────────────────────────────────────

    [Fact]
    public async Task DyingBetweenClaimAndConfirmLeavesTheClaimDurableAndTheNameStillTaken() {
        // The first half of the guarantee, and the reason the claim is durable rather than in
        // memory: a claim that vanished with the activation would let two creates both win.
        var tenant = Tenant(3);
        var address = await GroupAndAddress(tenant, "interrupt-1", "web-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();

        // The silo dies here. The activation is destroyed; the next call re-reads PostgreSQL.
        await index.DeactivateAsync();
        await WaitForDeactivation();

        var afterDeath = await cluster.ResourceIndexGrain(address).GetAsync();
        afterDeath.GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Claimed,
                "the claim is durable — docs/plan/06 § Two-phase create, step 1."
            );
        afterDeath.GetValueOrThrow().BoundTo.ShouldBe(address.Id);

        var rival = address with { Id = Guid.NewGuid() };
        (await cluster.ResourceIndexGrain(rival).TryClaimAsync(rival, rival.Id)).IsFailure
            .ShouldBeTrue("the name is still taken while the lease is live.");
    }

    [Fact]
    public async Task DyingBetweenClaimAndConfirmFreesTheNameWhenTheLeaseExpires() {
        // ⚠ THE INTERRUPTION THE DOCUMENT NAMES: "If the silo dies between 1 and 3, the claim
        // expires and the name is free again."
        var tenant = Tenant(4);
        var address = await GroupAndAddress(tenant, "interrupt-2", "web-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await Group(address).BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        // Die. Nothing confirms.
        await index.DeactivateAsync();
        await WaitForDeactivation();

        // Time passes — the whole five minutes, plus a second.
        cluster.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var freed = cluster.ResourceIndexGrain(address);
        (await freed.GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Free,
                "the lease expired, so the name is free — evaluated on read, because a timer that has "
                + "to fire for correctness is a timer whose silo can also die."
            );

        // And somebody else can now have it.
        var rival = address with { Id = Guid.NewGuid() };
        var reclaimed = await cluster.ResourceIndexGrain(rival).TryClaimAsync(rival, rival.Id);

        reclaimed.IsSuccess.ShouldBeTrue();
        reclaimed.GetValueOrThrow().BoundTo.ShouldBe(rival.Id);
    }

    [Fact]
    public async Task ConfirmingAnExpiredClaimFailsRatherThanResurrectingIt() {
        // The nastiest ordering: the original create wakes up after its lease expired and somebody
        // else took the name. Confirming must not silently steal it back.
        var tenant = Tenant(5);
        var address = await GroupAndAddress(tenant, "interrupt-3", "web-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        await index.DeactivateAsync();
        await WaitForDeactivation();

        cluster.Clock.Advance(TimeSpan.FromMinutes(6));

        var rival = address with { Id = Guid.NewGuid() };
        (await cluster.ResourceIndexGrain(rival).TryClaimAsync(rival, rival.Id)).IsSuccess
            .ShouldBeTrue();
        (await cluster.ResourceIndexGrain(rival).ConfirmAsync(rival.Id)).IsSuccess.ShouldBeTrue();

        var late = await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id);

        late.IsFailure.ShouldBeTrue();
        late.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await cluster.ResourceIndexGrain(address).ResolveAsync()).GetValueOrThrow()
            .ShouldBe(rival.Id, "the name belongs to whoever claimed it after it was freed.");
    }

    [Fact]
    public async Task TheOrphanedResourceGrainIsVisibleToTheReaper() {
        // "the orphaned resource grain (durable state, no confirmed index) is swept by a
        // per-subscription reaper reminder". The reaper itself is the resource manager's; what
        // tenancy owes it is the list — and the list has to survive the group grain dying too.
        var tenant = Tenant(6);
        var address = await GroupAndAddress(tenant, "orphans", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        // Nothing confirms, and both grains die.
        await cluster.ResourceIndexGrain(address).DeactivateAsync();
        await group.DeactivateAsync();
        await WaitForDeactivation();

        cluster.Clock.Advance(TimeSpan.FromMinutes(30));

        var orphans = (await Group(address).ListOrphansAsync(TimeSpan.FromMinutes(15)))
            .GetValueOrThrow();

        orphans.ShouldContain(x => x.ResourceId == address.Id);
        orphans.Single(x => x.ResourceId == address.Id).State.ShouldBe(ProvisioningState.Creating);

        // And a member that DID reach a terminal state is not an orphan.
        var healthy = address with { Name = "web-02", Id = Guid.NewGuid() };
        (await Group(healthy).BeginCreateAsync(healthy)).IsSuccess.ShouldBeTrue();
        (await Group(healthy).CompleteCreateAsync(healthy.Id, ProvisioningState.Succeeded))
            .IsSuccess.ShouldBeTrue();

        cluster.Clock.Advance(TimeSpan.FromMinutes(30));

        (await Group(healthy).ListOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow()
            .ShouldNotContain(x => x.ResourceId == healthy.Id);
    }

    [Fact]
    public async Task TheReaperRemovesAMemberWhoseNameWasClaimedAndNeverConfirmed() {
        // ⚠ THE SWEEP, WHICH IS A DIFFERENT ACT FROM THE LISTING. Age says a member has been
        // Creating for a while; only the index says the resource does not exist. This is the case
        // the document names — "die between 1 and 3" — with the claim's lease long expired.
        var tenant = Tenant(7);
        var address = await GroupAndAddress(tenant, "reaped", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        // The claim expires; the name is free again. Nothing confirmed it.
        (await cluster.ResourceIndexGrain(address).ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        cluster.Clock.Advance(TimeSpan.FromMinutes(30));

        var reaped = (await group.ReapOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow();

        reaped.ShouldContain(x => x.ResourceId == address.Id);
        (await group.ListAsync()).GetValueOrThrow().ShouldNotContain(x => x.ResourceId == address.Id);
    }

    [Fact]
    public async Task TheReaperLeavesAMemberWhoseIndexIsConfirmedHoweverOldItIs() {
        // ⚠ THE SABOTAGE, AND IT IS THE ONE WITH MONEY ATTACHED. "Die between 3 and 4" leaves a
        // member Creating forever with a CONFIRMED index — the resource is real and only step 4's
        // bookkeeping was lost. A reaper that swept on age alone would remove a live resource from
        // its group's listing while its pods ran and its meter ticked, which is exactly what
        // docs/plan/06 § Two-phase create calls a billing-dispute prevention measure.
        var tenant = Tenant(8);
        var address = await GroupAndAddress(tenant, "confirmed-orphan", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        // …and nothing ever calls CompleteCreateAsync.
        cluster.Clock.Advance(TimeSpan.FromDays(30));

        (await group.ListOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow()
            .ShouldContain(
                x => x.ResourceId == address.Id,
                "by age alone it looks exactly like an orphan, which is why age alone is not enough."
            );

        (await group.ReapOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow().ShouldBeEmpty();

        (await group.ListAsync()).GetValueOrThrow()
            .ShouldContain(x => x.ResourceId == address.Id, "the resource exists — its name is bound to it.");
    }

    [Fact]
    public async Task TheReaperLeavesAMemberWhoseNameWasTakenBySomethingElse() {
        // A claim that expired and was then won by a different resource. The old member is still an
        // orphan — the index is bound, but not to it.
        var tenant = Tenant(9);
        var address = await GroupAndAddress(tenant, "renamed", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();

        var winner = address with { Id = Guid.NewGuid() };
        (await cluster.ResourceIndexGrain(winner).TryClaimAsync(winner, winner.Id)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(winner).ConfirmAsync(winner.Id)).IsSuccess.ShouldBeTrue();

        cluster.Clock.Advance(TimeSpan.FromMinutes(30));

        (await group.ReapOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow()
            .ShouldContain(x => x.ResourceId == address.Id);
    }

    [Fact]
    public async Task TheReaperRefusesAThresholdShorterThanTheIndexLease() {
        // ⚠ THE SABOTAGE ON THE REAPER'S OWN EVIDENCE. Its proof is "old, and the index does not
        // name this member" — but a claim inside its five-minute lease has not expired yet, so a
        // two-minute threshold would sweep a create that is merely slow on the strength of an index
        // entry that was about to be confirmed. Refused rather than clamped: quietly widening it
        // would hide that the caller asked for something this cannot answer.
        var tenant = Tenant(11);
        var address = await GroupAndAddress(tenant, "too-eager", "web-01");

        var refused = await Group(address).ReapOrphansAsync(TimeSpan.FromMinutes(2));

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        error.Message.ShouldContain("index lease");

        IResourceGroupGrain.OrphanAge.ShouldBeGreaterThan(
            TimeSpan.FromMinutes(5),
            "the value both callers use has to clear the floor, or the reaper is refused every time "
            + "it runs and nothing says so."
        );
    }

    [Fact]
    public async Task TheReaperLeavesAMemberThatIsSimplyYoung() {
        var tenant = Tenant(10);
        var address = await GroupAndAddress(tenant, "in-flight", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();

        // A create that is thirty seconds old and has not confirmed yet is a create in progress,
        // and its index claim is still inside its five-minute lease.
        cluster.Clock.Advance(TimeSpan.FromSeconds(30));

        (await group.ReapOrphansAsync(TimeSpan.FromMinutes(15))).GetValueOrThrow().ShouldBeEmpty();
        (await group.ListAsync()).GetValueOrThrow().ShouldContain(x => x.ResourceId == address.Id);
    }

    // ── Interruption 2: die between confirm and the 202 ────────────────────────────────────────

    [Fact]
    public async Task DyingBetweenConfirmAndTheResponseMakesTheRetriedPutANoOp() {
        // ⚠ THE SECOND INTERRUPTION THE DOCUMENT NAMES: "If it dies between 3 and 4, the resource
        // exists and the caller retries the PUT — which is idempotent because PUT with the same body
        // on an existing resource is a no-op, which is exactly why the API is PUT and not POST."
        var tenant = Tenant(7);
        var address = await GroupAndAddress(tenant, "interrupt-4", "web-01");
        var index = cluster.ResourceIndexGrain(address);
        var group = Group(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.CompleteCreateAsync(address.Id, ProvisioningState.Succeeded)).IsSuccess
            .ShouldBeTrue();

        // The 202 never reaches the caller. Everything dies.
        await index.DeactivateAsync();
        await group.DeactivateAsync();
        await WaitForDeactivation();

        var before = (await Group(address).ListAsync()).GetValueOrThrow();

        // The caller retries the whole PUT: claim, create, confirm, complete. Every step again.
        var retryClaim = await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id);
        retryClaim.IsSuccess.ShouldBeTrue("the same GUID re-claiming its own confirmed binding.");
        retryClaim.GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.Confirmed,
                "⚠ and it is STILL Confirmed — a retry must not put a live binding back under a lease, "
                + "or the lease could expire out from under a resource that exists."
            );

        (await Group(address).BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        var after = (await Group(address).ListAsync()).GetValueOrThrow();

        after.Count.ShouldBe(before.Count, "the retry created nothing new.");
        after.Single(x => x.ResourceId == address.Id)
            .State.ShouldBe(
                ProvisioningState.Succeeded,
                "⚠ and it did not reset a live resource to Creating, which would make the reaper treat "
                + "it as an orphan."
            );
    }

    [Fact]
    public async Task ARetriedPutWithADifferentResourceIdIsStillAConflict() {
        // The other half of idempotence: "same body" is what is idempotent. A different GUID for the
        // same path is a second resource claiming a taken name.
        var tenant = Tenant(8);
        var address = await GroupAndAddress(tenant, "interrupt-5", "web-01");

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        var rival = address with { Id = Guid.NewGuid() };
        var rejected = await cluster.ResourceIndexGrain(rival).TryClaimAsync(rival, rival.Id);

        rejected.IsFailure.ShouldBeTrue();
        rejected.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    [Fact]
    public async Task AConfirmedBindingDoesNotExpireHoweverLongTheSiloIsAway() {
        var tenant = Tenant(9);
        var address = await GroupAndAddress(tenant, "no-expiry", "web-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        await index.DeactivateAsync();
        await WaitForDeactivation();

        cluster.Clock.Advance(TimeSpan.FromDays(400));

        (await cluster.ResourceIndexGrain(address).ResolveAsync()).GetValueOrThrow()
            .ShouldBe(address.Id);
    }

    [Fact]
    public async Task AClaimBuiltFromPathRatherThanCanonicalPathIsRefusedRatherThanMisfiled() {
        // docs/plan/06 § Identifiers: the index key must hash CanonicalPath. If a caller hashes Path
        // instead, the two spellings of one resource become two grains and the two-phase create is
        // defeated. GrainKeys.PathIndex makes the mistake unrepresentable at the key; this asserts
        // the grain refuses it too, so a hand-built key cannot smuggle it in.
        var tenant = Tenant(10);
        var address = await GroupAndAddress(tenant, "canonical", "web-01");

        var mixedCase = address with { Type = new("CYBERCLOUD.DBFORPOSTGRESQL", "SERVERS") };

        // Same canonical path, so the SAME index grain — which is the property being relied on.
        GrainKeys.PathIndex(mixedCase).ShouldBe(GrainKeys.PathIndex(address));

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();

        var second = mixedCase with { Id = Guid.NewGuid() };
        (await cluster.ResourceIndexGrain(second).TryClaimAsync(second, second.Id)).IsFailure
            .ShouldBeTrue("two case spellings of one path must not both claim the name.");

        // And a claim addressed at the WRONG grain is refused rather than recorded where no lookup
        // will find it.
        var elsewhere = address with { Name = "web-99", Id = Guid.NewGuid() };
        var misfiled = await cluster.ResourceIndexGrain(address)
            .TryClaimAsync(elsewhere, elsewhere.Id);

        misfiled.IsFailure.ShouldBeTrue();
        misfiled.Error!.Code.ShouldBe(ErrorCode.InvalidGrainKey);
    }

    static Guid Tenant(int n) => TenancyCluster.Tenant(3000 + n);

    IResourceGroupGrain Group(ResourceId address) =>
        cluster.ResourceGroupGrain(address.TenantId, address.SubscriptionId, address.ResourceGroup);

    async Task<ResourceId> GroupAndAddress(Guid tenant, string groupName, string resourceName) {
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("t" + tenant.ToString("N")[..8], "T", "eu-central")).IsSuccess
            .ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync(groupName, "eu-central")).IsSuccess.ShouldBeTrue();

        return new(
            tenant,
            subscription,
            groupName,
            new("CyberCloud.DBforPostgreSQL", "servers"),
            resourceName,
            Guid.NewGuid()
        );
    }

    /// <summary>
    ///     Gives Orleans a moment to finish the deactivation started by <c>DeactivateOnIdle</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>DeactivateOnIdle</c> schedules the deactivation and returns; the call that requested
    ///     it must complete first. Without this pause the next call can arrive at the <i>same</i>
    ///     activation and the test would assert against memory rather than against PostgreSQL —
    ///     which is the one thing these tests must not do. The state assertions still hold if the
    ///     pause is too short (the grain has not died yet), so this is a sharpener, not a
    ///     correctness crutch: <see cref="StateSurvivedDeactivation" /> is the check that it worked.
    /// </remarks>
    static Task WaitForDeactivation() =>
        Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
}
