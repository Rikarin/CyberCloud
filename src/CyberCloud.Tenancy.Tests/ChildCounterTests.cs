using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     The per-parent child counter <c>ResourceIndexGrain</c> carries — docs/plan/08 § Deleting a
///     parent resource that has children.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why the counter lives on the index grain at all.</b> docs/plan/08 recorded the refusal
///         as decided-but-unbuilt because <c>IResourceIndexGrain</c> is path→GUID and one-way, and the
///         only enumeration available was the resource-graph projection, which is eventually
///         consistent: <i>"a delete gate reading a stale index either orphans a child it did not see
///         or refuses over a child that is already gone"</i>. The two honest options it names are a
///         counter "maintained transactionally where the index claim and release already happen" or a
///         strongly-consistent child index keyed on the parent's address. Putting the counter on the
///         parent's own index grain is both at once: one activation per parent address, single
///         threaded, durable, and the same activation the parent's delete releases the name on — so
///         "is this name taken" and "does it still have children" cannot disagree.
///     </para>
///     <para>
///         ⚠ <b>The resource manager is the only production caller and is deliberately not here.</b>
///         <c>CyberCloud.ResourceManager.Tests.ChildDeleteRefusalTests</c> drives the whole write path
///         against it; this file is the grain's own arithmetic, including the two cases the manager
///         cannot reach — a decrement below zero and a release that still has counts on it.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class ChildCounterTests(TenancyCluster cluster) {
    static ResourceTypeName Databases { get; } = new("CyberCloud.DBforPostgreSQL", "servers/databases");

    static ResourceTypeName FirewallRules { get; } = new("CyberCloud.DBforPostgreSQL", "servers/firewallRules");

    [Fact]
    public async Task AFreshAddressHasNoChildren() {
        var index = cluster.ResourceIndexGrain(Address("counter-empty"));

        (await index.ChildrenAsync()).GetValueOrThrow().ShouldBeEmpty(
            "an address nobody has registered a child against must read as empty rather than as "
            + "absent — every delete in the platform goes through this read"
        );
    }

    [Fact]
    public async Task ChildrenAreCountedPerTypeAndReportedInAStableOrder() {
        var index = cluster.ResourceIndexGrain(Address("counter-per-type"));

        (await index.AddChildAsync(Databases)).GetValueOrThrow().ShouldBe(1);
        (await index.AddChildAsync(Databases)).GetValueOrThrow().ShouldBe(2);
        (await index.AddChildAsync(FirewallRules)).GetValueOrThrow().ShouldBe(1);

        var children = (await index.ChildrenAsync()).GetValueOrThrow();

        // ⚠ The refusal message is built from this, and a message that reordered itself between two
        // retries reads as two different faults rather than one unchanged one.
        children.Length.ShouldBe(2);
        children[0].Type.ShouldBe(Databases);
        children[0].Count.ShouldBe(2);
        children[1].Type.ShouldBe(FirewallRules);
        children[1].Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheCountIsCaseInsensitiveOnTheTypeTheWayAddressesAre() {
        // ⚠ ResourceTypeName compares case-insensitively and the path index hashes the CANONICAL form,
        // so a counter keyed on the raw spelling would count `servers/Databases` and
        // `servers/databases` separately — and a parent would then be held by a child nobody could
        // find under the name the refusal printed.
        var index = cluster.ResourceIndexGrain(Address("counter-casing"));

        await index.AddChildAsync(new("CyberCloud.DBforPostgreSQL", "servers/databases"));
        await index.AddChildAsync(new("CyberCloud.DBforPostgreSQL", "Servers/Databases"));

        var children = (await index.ChildrenAsync()).GetValueOrThrow();

        children.Length.ShouldBe(1, "one type was counted as two because the key was not canonicalised");
        children[0].Count.ShouldBe(2);
    }

    [Fact]
    public async Task RemovingTakesTheCountDownAndAZeroDisappears() {
        var index = cluster.ResourceIndexGrain(Address("counter-down"));

        await index.AddChildAsync(Databases);
        await index.AddChildAsync(Databases);

        (await index.RemoveChildAsync(Databases)).GetValueOrThrow().ShouldBe(1);
        (await index.ChildrenAsync()).GetValueOrThrow().Length.ShouldBe(1);

        (await index.RemoveChildAsync(Databases)).GetValueOrThrow().ShouldBe(0);

        // ⚠ Not "one type at zero" — empty. The delete gate refuses on a NON-EMPTY array, so a type
        // left in the dictionary at zero would hold its parent undeletable forever.
        (await index.ChildrenAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingMoreThanWasAddedIsClampedAtZeroRatherThanGoingNegative() {
        // ⚠ THE CALLER IS A REMINDER-DRIVEN OPERATION GRAIN, SO "RUN TWICE" IS THE NORMAL CASE.
        // OperationGrain decrements after CompleteDeleteAsync, on the same re-drivable pass as the
        // ReBAC unlink; a partially applied pass is re-driven from the start. A count that went
        // negative would make a parent DELETABLE while a sibling child was still live, which is this
        // counter's own failure with the sign flipped.
        var index = cluster.ResourceIndexGrain(Address("counter-clamped"));

        await index.AddChildAsync(Databases);

        (await index.RemoveChildAsync(Databases)).GetValueOrThrow().ShouldBe(0);
        (await index.RemoveChildAsync(Databases)).GetValueOrThrow().ShouldBe(0);
        (await index.RemoveChildAsync(Databases)).GetValueOrThrow().ShouldBe(0);

        await index.AddChildAsync(Databases);

        (await index.ChildrenAsync()).GetValueOrThrow()[0].Count.ShouldBe(
            1,
            "the count went negative under the repeated decrements, so one child now reads as none"
        );
    }

    [Fact]
    public async Task RemovingATypeThatWasNeverAddedSucceeds() {
        var index = cluster.ResourceIndexGrain(Address("counter-unknown"));

        (await index.RemoveChildAsync(FirewallRules)).IsSuccess.ShouldBeTrue(
            "a decrement for a type this address never held is an operation grain re-driving a "
            + "delete, not a fault — a failure here would stall that delete on a retry loop"
        );
    }

    [Fact]
    public async Task AnUntypedChildIsRefusedRatherThanCountedAnonymously() {
        // The refusal a parent's delete gives has to NAME what is holding it — docs/plan/08's ⚠ on
        // refusing: "a 409 WITH A COUNT rather than a bare refusal — the caller has to be able to see
        // what is holding it". A count under `default(ResourceTypeName)` could not be named.
        var index = cluster.ResourceIndexGrain(Address("counter-untyped"));

        (await index.AddChildAsync(default)).IsFailure.ShouldBeTrue();
        (await index.ChildrenAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleasingTheNameClearsTheCountsWithIt() {
        // ⚠ THE COUNTS BELONG TO THE RESOURCE, NOT TO THE ADDRESS.
        //
        // docs/plan/06 § Two-phase create releases the index first "so the name is immediately
        // reusable", and DeletePathTests.TheNameIsReusableWhileTheOldResourceIsStillTearingDown shows
        // a different GUID taking the same path a moment later. A count that survived the release
        // would be inherited by that new resource, which would then answer 409 to its own delete over
        // children it never had — and nothing would ever decrement it, so only an operator could
        // clear it.
        //
        // ⚠ Unreachable through the manager, which is why it is tested here: the gate refuses the
        // delete unless every count is already zero, so this is the second line of defence and the
        // manager cannot drive it.
        var address = Address("counter-released");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        await index.AddChildAsync(Databases);
        (await index.ChildrenAsync()).GetValueOrThrow().ShouldNotBeEmpty();

        (await index.ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();

        (await index.ChildrenAsync()).GetValueOrThrow().ShouldBeEmpty(
            "a released name carried its child counts into the next resource that takes the address"
        );
    }

    [Fact]
    public async Task AFailedReleaseLeavesTheCountsAlone() {
        // The other half: releasing with the wrong GUID is a Conflict and changes nothing, so the
        // clear above must not run on a release that did not happen.
        var address = Address("counter-not-released");
        var index = cluster.ResourceIndexGrain(address);

        (await index.TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        await index.AddChildAsync(Databases);

        (await index.ReleaseAsync(Guid.NewGuid())).IsFailure.ShouldBeTrue();

        (await index.ChildrenAsync()).GetValueOrThrow().ShouldNotBeEmpty(
            "a release that was refused cleared the counts anyway, so a caller who guessed a GUID "
            + "could strip a resource's protection against being deleted with live children"
        );
    }

    [Fact]
    public async Task TheCountsSurviveDeactivation() {
        // ⚠ Durable, not activation-scoped. A counter that lived in memory would read zero after any
        // silo restart, and every parent in the platform would become deletable with live children
        // exactly once per deployment.
        var address = Address("counter-durable");
        var index = cluster.ResourceIndexGrain(address);

        await index.AddChildAsync(Databases);
        await index.AddChildAsync(Databases);

        await index.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        var revived = cluster.ResourceIndexGrain(address);
        var children = (await revived.ChildrenAsync()).GetValueOrThrow();

        children.Length.ShouldBe(1, TwoPhaseCreateTests.StateSurvivedDeactivation);
        children[0].Count.ShouldBe(2);
    }

    /// <summary>An address in this suite's own tenant, distinct per test name.</summary>
    static ResourceId Address(string name) =>
        new(
            TenancyCluster.Tenant(3500),
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            Guid.NewGuid()
        );
}
