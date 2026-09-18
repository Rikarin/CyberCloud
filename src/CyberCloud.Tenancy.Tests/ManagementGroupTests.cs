using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     <see cref="IManagementGroupGrain" /> and the two records that point at it — docs/plan/06 § The
///     hierarchy's tree above the subscription, issue #39 — through the real grains in the real
///     cluster.
/// </summary>
/// <remarks>
///     ⚠ <b>What this class does not test is the tree's authorization</b>, which is the whole point
///     of a group and lives in the tuple store, not here: <c>test/CyberCloud.Isolation</c>'s
///     <c>ManagementGroupTests</c> drives a grant at a group down to a resource group through the
///     real <c>CyberCloudSchema</c>. This class is the record: what a group remembers, what it
///     refuses, and what survives a reactivation.
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class ManagementGroupTests(TenancyCluster cluster) {
    [Fact]
    public async Task CreationRecordsTheTreePositionAndIsIdempotentOnTheSamePosition() {
        var tenant = Tenant(1);
        var group = Group(tenant, "platform");

        var created = (await group.CreateAsync("Platform", "", 0)).GetValueOrThrow();

        created.Name.ShouldBe("platform");
        created.TenantId.ShouldBe(tenant);
        created.DisplayName.ShouldBe("Platform");
        created.Parent.ShouldBeEmpty("a root group hangs off the tenant, spelled by absence");
        created.Depth.ShouldBe(1);
        created.Version.ShouldBe(1);

        var again = (await group.CreateAsync("Platform", "", 0)).GetValueOrThrow();

        // The record, not a second one: same version, same position. (Record equality is not used
        // because the two list members are compared by reference.)
        again.Version.ShouldBe(created.Version, "a re-driven create on the same position wrote a new version");
        again.Parent.ShouldBe(created.Parent);
        again.Depth.ShouldBe(created.Depth);
        again.CreatedAt.ShouldBe(created.CreatedAt);
    }

    [Fact]
    public async Task TheDisplayNameDefaultsToTheName() {
        var created = (await Group(Tenant(2), "unnamed").CreateAsync("", "", 0)).GetValueOrThrow();

        created.DisplayName.ShouldBe("unnamed");
    }

    /// <summary>
    ///     ⚠ A re-drive naming a different parent is the move <see cref="IManagementGroupGrain" />'s
    ///     remarks refuse, and accepting it silently would turn a retry into one.
    /// </summary>
    [Fact]
    public async Task ARedriveNamingADifferentParentIsAConflictNotAMove() {
        var tenant = Tenant(3);
        (await Group(tenant, "root").CreateAsync("", "", 0)).IsSuccess.ShouldBeTrue();

        var child = Group(tenant, "child");
        (await child.CreateAsync("", "root", 1)).IsSuccess.ShouldBeTrue();

        var moved = await child.CreateAsync("", "", 0);

        moved.IsFailure.ShouldBeTrue("a group was moved by re-creating it under another parent");
        moved.Error!.Code.ShouldBe(ErrorCode.Conflict);
        moved.Error.Message.ShouldContain("move");

        (await child.GetAsync()).GetValueOrThrow().Parent.ShouldBe("root", "the refused move changed the record");
    }

    [Fact]
    public async Task AGroupCannotBeItsOwnParent() {
        var refused = await Group(Tenant(4), "loop").CreateAsync("", "loop", 1);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    /// <summary>
    ///     ⚠ Six levels, and the seventh is refused — the cap is what keeps a legal tree inside
    ///     docs/plan/07 § Check's twelve hops, and the grain enforces it off the depth its caller
    ///     read from the parent.
    /// </summary>
    [Fact]
    public async Task TheTreeIsCappedAtSixLevels() {
        var tenant = Tenant(5);

        var atTheCap = await Group(tenant, "deep-6").CreateAsync("", "deep-5", IManagementGroupGrain.MaxDepth - 1);
        atTheCap.IsSuccess.ShouldBeTrue(atTheCap.Error?.Message);
        atTheCap.GetValueOrThrow().Depth.ShouldBe(IManagementGroupGrain.MaxDepth);

        var pastTheCap = await Group(tenant, "deep-7").CreateAsync("", "deep-6", IManagementGroupGrain.MaxDepth);

        pastTheCap.IsFailure.ShouldBeTrue("a seventh level was created");
        pastTheCap.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        pastTheCap.Error.Message.ShouldContain("docs/plan/07");

        (await Group(tenant, "deep-7").GetAsync()).IsFailure.ShouldBeTrue("the refused group has a record");
    }

    [Fact]
    public async Task ChildrenAndSubscriptionsAreRecordedSortedAndForgottenIdempotently() {
        var tenant = Tenant(6);
        var group = Group(tenant, "members");
        (await group.CreateAsync("", "", 0)).IsSuccess.ShouldBeTrue();

        var later = Guid.Parse("ffffffff-0000-4000-8000-000000000001");
        var earlier = Guid.Parse("00000000-0000-4000-8000-000000000001");

        (await group.AddChildAsync("zeta")).IsSuccess.ShouldBeTrue();
        (await group.AddChildAsync("alpha")).IsSuccess.ShouldBeTrue();
        (await group.AddChildAsync("alpha")).IsSuccess.ShouldBeTrue();
        (await group.AddSubscriptionAsync(later)).IsSuccess.ShouldBeTrue();
        (await group.AddSubscriptionAsync(earlier)).IsSuccess.ShouldBeTrue();

        var record = (await group.GetAsync()).GetValueOrThrow();
        record.Children.ShouldBe(["alpha", "zeta"]);
        record.Subscriptions.ShouldBe([earlier, later]);

        (await group.RemoveChildAsync("never-listed")).IsSuccess.ShouldBeTrue("forgetting a stranger is a success");
        (await group.RemoveChildAsync("zeta")).IsSuccess.ShouldBeTrue();
        (await group.RemoveSubscriptionAsync(later)).IsSuccess.ShouldBeTrue();

        record = (await group.GetAsync()).GetValueOrThrow();
        record.Children.ShouldBe(["alpha"]);
        record.Subscriptions.ShouldBe([earlier]);
    }

    /// <summary>
    ///     ⚠ The check and the delete are one turn, and a group that holds anything is refused with
    ///     the members named — a group delete does not cascade.
    /// </summary>
    [Fact]
    public async Task DeleteRefusesWhileAnythingHangsOffTheGroupAndNamesIt() {
        var tenant = Tenant(7);
        var group = Group(tenant, "held");
        (await group.CreateAsync("", "", 0)).IsSuccess.ShouldBeTrue();
        (await group.AddChildAsync("held-child")).IsSuccess.ShouldBeTrue();

        var refused = await group.DeleteAsync();

        refused.IsFailure.ShouldBeTrue("a group holding a child was deleted");
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain("held-child");

        (await group.GetAsync()).IsSuccess.ShouldBeTrue("the refused delete removed the record");

        (await group.RemoveChildAsync("held-child")).IsSuccess.ShouldBeTrue();
        (await group.DeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.GetAsync()).IsFailure.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ A deleted group still answers its parent's name, so a re-driven <c>DELETE</c> after a
    ///     crash between the record and the sweep can still clear the parent's child list — the
    ///     reason <c>ManagementGroupState.LastParent</c> exists.
    /// </summary>
    [Fact]
    public async Task ADeletedGroupStillNamesTheParentItHad() {
        var tenant = Tenant(8);
        (await Group(tenant, "parent").CreateAsync("", "", 0)).IsSuccess.ShouldBeTrue();

        var child = Group(tenant, "gone");
        (await child.CreateAsync("", "parent", 1)).IsSuccess.ShouldBeTrue();

        (await child.DeleteAsync()).GetValueOrThrow().ShouldBe("parent");

        // Already gone — and the parent is still the answer, across a reactivation.
        await child.DeactivateAsync();
        (await child.DeleteAsync()).GetValueOrThrow().ShouldBe("parent");
        (await child.GetAsync()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task TheRecordSurvivesAReactivationBecauseItIsDurable() {
        var tenant = Tenant(9);
        var group = Group(tenant, "durable");
        (await group.CreateAsync("Durable", "", 0)).IsSuccess.ShouldBeTrue();
        (await group.AddChildAsync("kept")).IsSuccess.ShouldBeTrue();

        await group.DeactivateAsync();

        var record = (await Group(tenant, "durable").GetAsync()).GetValueOrThrow();
        record.DisplayName.ShouldBe("Durable");
        record.Children.ShouldBe(["kept"]);
    }

    // ── The two records that point at a group ──────────────────────────────────────────────────

    [Fact]
    public async Task TheTenantListsItsGroupsFlatAndSorted() {
        var tenant = Tenant(10);
        var grain = cluster.TenantGrain(tenant);
        (await grain.CreateAsync("mg-10", "MG 10", "eu-central")).IsSuccess.ShouldBeTrue();

        (await grain.AddManagementGroupAsync("zeta")).IsSuccess.ShouldBeTrue();
        (await grain.AddManagementGroupAsync("alpha")).IsSuccess.ShouldBeTrue();
        (await grain.AddManagementGroupAsync("alpha")).IsSuccess.ShouldBeTrue();

        (await grain.ListManagementGroupsAsync()).GetValueOrThrow().ShouldBe(["alpha", "zeta"]);

        (await grain.RemoveManagementGroupAsync("zeta")).IsSuccess.ShouldBeTrue();
        (await grain.RemoveManagementGroupAsync("zeta")).IsSuccess.ShouldBeTrue();
        (await grain.ListManagementGroupsAsync()).GetValueOrThrow().ShouldBe(["alpha"]);

        var refused = await grain.AddManagementGroupAsync("NOT-A-NAME");
        refused.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ASubscriptionRecordsTheGroupItHangsOffAndAnUnwrittenRecordReadsAsNone() {
        var tenant = Tenant(11);
        var subscription = cluster.SubscriptionGrain(tenant, Guid.Parse("11111111-0000-4000-8000-000000000039"));

        var created = (await subscription.CreateAsync("Grouped")).GetValueOrThrow();
        created.ManagementGroup.ShouldBeEmpty("a subscription hangs off the tenant until it is assigned");

        var assigned = (await subscription.SetManagementGroupAsync("platform")).GetValueOrThrow();
        assigned.ManagementGroup.ShouldBe("platform");
        assigned.Version.ShouldBe(created.Version + 1);

        var same = (await subscription.SetManagementGroupAsync("platform")).GetValueOrThrow();
        same.Version.ShouldBe(assigned.Version, "an idempotent re-stamp bumped the version");

        var cleared = (await subscription.SetManagementGroupAsync("")).GetValueOrThrow();
        cleared.ManagementGroup.ShouldBeEmpty();

        (await subscription.SetManagementGroupAsync("Not A Name")).IsFailure.ShouldBeTrue();
    }

    static Guid Tenant(int n) => TenancyCluster.Tenant(9100 + n);

    IManagementGroupGrain Group(Guid tenant, string name) =>
        cluster.For(tenant).GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(name));
}
