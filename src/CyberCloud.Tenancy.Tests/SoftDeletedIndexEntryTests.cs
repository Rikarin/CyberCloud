using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     The fourth <see cref="IndexEntryState" /> — docs/plan/08 § Soft delete's second decision.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Driven against the grain directly, and that is not merely convenient — it is the only
///         place two of these refusals are reachable at all.</b> The resource manager mints a fresh
///         GUID for every create, so a re-claim of a soft-deleted name <i>with the resource's own
///         GUID</i> cannot be produced through the API; the guard against it is defence against a
///         direct grain caller, and a test that went through the manager would pass with the guard
///         deleted. That was measured rather than assumed: sabotaging
///         <c>IndexClaimMachine.TryClaim</c> to accept a soft-deleted entry left the whole resource
///         manager suite green, because the ordinary path is caught one line later by the
///         already-bound check.
///     </para>
///     <para>
///         ⚠ <b>The state is one state and not a new mechanism</b>, which is what the document asks
///         for. Everything below is a transition of the machine <c>IndexClaimMachine</c> already ran:
///         <c>Confirmed → SoftDeleted → Confirmed</c>, and <c>SoftDeleted → Free</c>, which is the
///         purge and is the same <c>Release</c> a hard delete uses.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class SoftDeletedIndexEntryTests(TenancyCluster cluster) {
    [Fact]
    public async Task AConfirmedBindingBecomesSoftDeletedAndStopsResolving() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "parked", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ResolveAsync()).GetValueOrThrow().ShouldBe(address.Id);

        var parked = await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7));
        parked.IsSuccess.ShouldBeTrue(parked.Error?.Message);
        parked.GetValueOrThrow().State.ShouldBe(IndexEntryState.SoftDeleted);

        // ⚠ THE ONE FACT THE 404 IS BUILT ON. docs/plan/08 § Soft delete: ResolveAsync must refuse the
        // entry, "so the resource is not addressable, the 404 above is free". Every caller of this
        // method — the write path's step 1, the parent check, the child-delete gate — inherits that
        // answer without knowing soft delete exists.
        var resolved = await index.ResolveAsync();
        resolved.IsFailure.ShouldBeTrue("a soft-deleted binding must not resolve");
        resolved.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // …but the entitled question still has an answer.
        (await index.ResolveSoftDeletedAsync()).GetValueOrThrow().ShouldBe(address.Id);
    }

    /// <summary>
    ///     ⚠ <b>A re-claim with the soft-deleted resource's <i>own</i> GUID is refused, and this is the
    ///     case only a direct caller can reach.</b>
    /// </summary>
    /// <remarks>
    ///     <c>TryClaim</c> is idempotent for the same id against a <see cref="IndexEntryState.Claimed" />
    ///     or <see cref="IndexEntryState.Confirmed" /> entry — that idempotence is what makes a retried
    ///     <c>PUT</c> a no-op. Extending it to <see cref="IndexEntryState.SoftDeleted" /> would make a
    ///     create silently adopt a resource whose data plane is still running and whose direct role
    ///     assignments were dropped: a resurrection nobody asked for, reported as a create, bypassing
    ///     the restore that is the only sanctioned way back.
    /// </remarks>
    [Fact]
    public async Task ReclaimingASoftDeletedNameIsRefusedEvenWithTheOriginalResourcesOwnGuid() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "no-resurrect", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).IsSuccess.ShouldBeTrue();

        var sameGuid = await index.TryClaimAsync(address, address.Id);

        sameGuid.IsFailure.ShouldBeTrue(
            "the idempotence rule stops at SoftDeleted — a create must not be able to resurrect a "
            + "soft-deleted resource by re-claiming its own name with its own GUID"
        );

        sameGuid.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        // A different GUID — the ordinary create — is refused too, and this is the arm the resource
        // manager actually exercises.
        var rival = address.WithId(Guid.NewGuid());
        (await index.TryClaimAsync(rival, rival.Id)).IsFailure.ShouldBeTrue();

        // And the entry is untouched by either attempt.
        (await index.GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.SoftDeleted);
    }

    /// <summary>
    ///     ⚠ <b>A recovery window does not expire on read the way a lease does.</b>
    /// </summary>
    /// <remarks>
    ///     <c>IndexClaimMachine.Effective</c> collapses an expired <see cref="IndexEntryState.Claimed" />
    ///     entry to <see cref="IndexEntryState.Free" />, because a create whose silo died must not burn
    ///     the name. A window is the opposite promise: past its end a restore is refused, but the name
    ///     stays held until something explicitly purges it. Collapsing it would hand the name away at
    ///     the instant the resource became unrecoverable and leave that resource's data plane running
    ///     under a name somebody else now owns.
    /// </remarks>
    [Fact]
    public async Task PastTheWindowTheNameIsStillHeldButARestoreIsRefused() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "expired", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).IsSuccess.ShouldBeTrue();

        cluster.Clock.Advance(TimeSpan.FromDays(8));

        (await index.GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                IndexEntryState.SoftDeleted,
                "an expired window is not an expired lease — the name stays held until a purge"
            );

        var late = await index.RestoreAsync(address.Id);
        late.IsFailure.ShouldBeTrue("past the window there is nothing to restore");
        late.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var rival = address.WithId(Guid.NewGuid());
        (await index.TryClaimAsync(rival, rival.Id)).IsFailure.ShouldBeTrue("and the name is still not free");

        // ⚠ But it is still PURGEABLE, which is the direction that must stay open. Refusing here would
        // leave an expired resource that can be neither restored nor destroyed.
        (await index.ResolveSoftDeletedAsync()).IsSuccess.ShouldBeTrue();
        (await index.ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);
    }

    [Fact]
    public async Task ARestoreWithinTheWindowMakesTheNameResolveAgain() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "restored", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).IsSuccess.ShouldBeTrue();

        cluster.Clock.Advance(TimeSpan.FromDays(6));

        var restored = await index.RestoreAsync(address.Id);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);

        (await index.ResolveAsync()).GetValueOrThrow()
            .ShouldBe(address.Id, "the same resource is addressable at the same name again");

        // ⚠ And it is a real Confirmed binding rather than one carrying a stale deadline: the entry
        // must not expire out from under a live resource, which is the same reason Confirm does not
        // re-lease.
        (await index.GetAsync()).GetValueOrThrow().RecoverableUntil.ShouldBe(default);

        cluster.Clock.Advance(TimeSpan.FromDays(30));
        (await index.ResolveAsync()).IsSuccess.ShouldBeTrue("a restored binding is permanent");
    }

    /// <summary>
    ///     ⚠ <b>The park does not restamp the deadline, because the delete path is re-driven.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete makes retention immutable. A re-drive an hour later that reset the
    ///     deadline would silently extend every window whose delete failed once — a guarantee that
    ///     quietly becomes longer is as broken as one that quietly becomes shorter, because neither is
    ///     the number the platform published.
    /// </remarks>
    [Fact]
    public async Task ASecondParkKeepsTheOriginalDeadline() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "no-extend", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        var first = (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).GetValueOrThrow();

        cluster.Clock.Advance(TimeSpan.FromDays(3));

        var second = (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).GetValueOrThrow();

        second.RecoverableUntil.ShouldBe(
            first.RecoverableUntil,
            "a re-driven delete must not extend the window it is re-driving"
        );
    }

    [Fact]
    public async Task OnlyAConfirmedBindingCanBeParkedAndOnlyItsOwnerCanPark() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "guards", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        // ⚠ A name under a LEASE is a create that has not finished. Parking it would hold the name for
        // seven days on behalf of a resource that never existed, and nothing would ever purge it.
        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();

        var claimed = await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7));
        claimed.IsFailure.ShouldBeTrue("only a confirmed binding can be soft-deleted");
        claimed.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        // And somebody else's GUID cannot park somebody else's name.
        var impostor = await index.SoftDeleteAsync(Guid.NewGuid(), TimeSpan.FromDays(7));
        impostor.IsFailure.ShouldBeTrue();
        impostor.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await index.GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);
    }

    /// <summary>
    ///     ⚠ <b>A soft delete survives the activation dying, which is what "durable" has to mean here.</b>
    /// </summary>
    /// <remarks>
    ///     A recovery window is a promise measured in days and an activation lives for minutes. If the
    ///     state were not durable the name would come back the first time the silo idled the grain out,
    ///     and the resource would become unrestorable with nothing in any log — the same reason
    ///     docs/plan/06 § Two-phase create makes the claim itself durable.
    /// </remarks>
    [Fact]
    public async Task TheParkedStateSurvivesTheActivationBeingDestroyed() {
        var tenant = Guid.NewGuid();
        var address = await GroupAndAddress(tenant, "durable-park", "vault-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        var parked = (await index.SoftDeleteAsync(address.Id, TimeSpan.FromDays(7))).GetValueOrThrow();

        await index.DeactivateAsync();

        var revived = cluster.ResourceIndexGrain(address);
        var afterDeath = (await revived.GetAsync()).GetValueOrThrow();

        afterDeath.State.ShouldBe(IndexEntryState.SoftDeleted);
        afterDeath.RecoverableUntil.ShouldBe(parked.RecoverableUntil, "the deadline came back out of storage");
        afterDeath.BoundTo.ShouldBe(address.Id);

        (await revived.RestoreAsync(address.Id)).IsSuccess.ShouldBeTrue("and it is still restorable");
    }

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
}
