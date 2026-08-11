using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/06 § Two-phase create, the second half: <b>"Deletion is the same in reverse and it
///     is the harder half"</b>.
/// </summary>
/// <remarks>
///     <para>
///         The order is fixed: <b>release the index first</b> (so the name is immediately reusable),
///         <b>then</b> tear down the data plane, <b>then</b> delete the grain state.
///     </para>
///     <para>
///         ⚠ <b>And the failure clause is the one with money attached.</b> "A resource whose data
///         plane teardown fails is left in <c>Deleting</c> with a retry reminder and is
///         <i>visible</i> in listings with that state — never silently gone while its pods still run
///         and its meter still ticks. That last clause is a billing-dispute prevention measure as
///         much as a correctness one."
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class DeleteOrderingTests(TenancyCluster cluster)
{
    static Guid Tenant(int n) => TenancyCluster.Tenant(4000 + n);

    [Fact]
    public async Task TheIndexIsReleasedFirstSoTheNameIsImmediatelyReusable()
    {
        var address = await Provision(Tenant(1), "delete-order", "web-01");
        var index = cluster.ResourceIndexGrain(address);
        var group = Group(address);

        // Delete, step 1: release the index. The teardown has not even started.
        (await index.ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();

        // The name is free RIGHT NOW, while the old resource is still being torn down.
        var replacement = address with { Id = Guid.NewGuid() };
        var reclaimed = await cluster.ResourceIndexGrain(replacement)
            .TryClaimAsync(replacement, replacement.Id);

        reclaimed.IsSuccess.ShouldBeTrue(
            "docs/plan/06 § Two-phase create: 'release the index first (so the name is immediately "
            + "reusable)'.");
        reclaimed.GetValueOrThrow().BoundTo.ShouldBe(replacement.Id);

        // …and the old resource is still listed, in Deleting.
        (await group.ListAsync()).GetValueOrThrow()
            .Single(x => x.ResourceId == address.Id).State.ShouldBe(ProvisioningState.Deleting);
    }

    [Fact]
    public async Task AResourceWhoseTeardownFailsStaysDeletingAndStaysVisibleInListings()
    {
        // ⚠ THE BILLING-DISPUTE CLAUSE, as a test.
        var address = await Provision(Tenant(2), "teardown-fails", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();

        // The data plane refuses. Three times.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await group.FailDeleteAsync(address.Id, "the cluster refused: finalizer stuck"))
                .IsSuccess.ShouldBeTrue();
        }

        var listed = (await group.ListAsync()).GetValueOrThrow();
        var member = listed.Single(x => x.ResourceId == address.Id);

        member.State.ShouldBe(
            ProvisioningState.Deleting,
            "never Failed and never removed: 'never silently gone while its pods still run and its "
            + "meter still ticks'.");
        member.TeardownAttempts.ShouldBe(3);
        member.LastFailure.ShouldContain("finalizer stuck");
    }

    [Fact]
    public async Task AFailedTeardownSurvivesTheGroupGrainDyingBecauseTheReminderHasToFindIt()
    {
        // A retry reminder re-drives the teardown after a silo restart. If the Deleting state were
        // in memory only, the restart would look like a completed delete.
        var address = await Provision(Tenant(3), "teardown-survives", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.FailDeleteAsync(address.Id, "cluster unreachable")).IsSuccess.ShouldBeTrue();

        await group.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        var afterDeath = (await Group(address).ListAsync()).GetValueOrThrow()
            .Single(x => x.ResourceId == address.Id);

        afterDeath.State.ShouldBe(ProvisioningState.Deleting);
        afterDeath.LastFailure.ShouldContain("cluster unreachable");
        afterDeath.TeardownAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task OnlyASucceededTeardownRemovesTheMember()
    {
        var address = await Provision(Tenant(4), "teardown-succeeds", "web-01");
        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.FailDeleteAsync(address.Id, "first attempt failed")).IsSuccess.ShouldBeTrue();

        (await group.ListAsync()).GetValueOrThrow().ShouldContain(x => x.ResourceId == address.Id);

        (await group.CompleteDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();

        (await group.ListAsync()).GetValueOrThrow().ShouldNotContain(x => x.ResourceId == address.Id);
    }

    [Fact]
    public async Task AMemberCannotBeRemovedWithoutADeleteHavingBegun()
    {
        // The direction that would hide a live resource: CompleteDelete on something that is still
        // Succeeded. Refused, because a listing that drops a running resource is the failure this
        // whole ordering exists to prevent.
        var address = await Provision(Tenant(5), "no-shortcut", "web-01");
        var group = Group(address);

        var refused = await group.CompleteDeleteAsync(address.Id);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await group.ListAsync()).GetValueOrThrow().ShouldContain(x => x.ResourceId == address.Id);
    }

    [Fact]
    public async Task ATeardownFailureCannotBeRecordedAgainstAResourceThatIsNotBeingDeleted()
    {
        var address = await Provision(Tenant(6), "no-fabrication", "web-01");
        var group = Group(address);

        var refused = await group.FailDeleteAsync(address.Id, "made up");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task ReleasingAnIndexBoundToSomebodyElseIsRefused()
    {
        // The delete path's own cross-resource guard: releasing by the wrong GUID would hand a live
        // resource's name away.
        var address = await Provision(Tenant(7), "wrong-owner", "web-01");

        var refused = await cluster.ResourceIndexGrain(address).ReleaseAsync(Guid.NewGuid());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await cluster.ResourceIndexGrain(address).ResolveAsync()).GetValueOrThrow()
            .ShouldBe(address.Id);
    }

    [Fact]
    public async Task ReleasingTwiceIsANoOpBecauseTheDeleteIsReDrivenFromAReminder()
    {
        var address = await Provision(Tenant(8), "idempotent-release", "web-01");
        var index = cluster.ResourceIndexGrain(address);

        (await index.ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await index.ReleaseAsync(address.Id)).IsSuccess.ShouldBeTrue(
            "delete is re-driven from a reminder (docs/plan/06 § Two-phase create), so every step "
            + "must be safe to run twice.");
    }

    [Fact]
    public async Task DeletingTheGroupIsNotDeletingItsMembersHere()
    {
        // A resource group IS a lifecycle unit — "delete it, delete its contents, in dependency
        // order, as one operation" (docs/plan/06 § The hierarchy). That orchestration is the
        // resource manager's (docs/plan/08) and is deliberately not built here. What this asserts is
        // the boundary: removing the group from its subscription's listing does NOT quietly drop its
        // members, so nothing can look deleted while its contents still run.
        var address = await Provision(Tenant(9), "group-lifecycle", "web-01");
        var subscription = address.SubscriptionId;

        (await cluster.SubscriptionGrain(address.TenantId, subscription)
            .RemoveResourceGroupAsync(address.ResourceGroup)).IsSuccess.ShouldBeTrue();

        (await cluster.SubscriptionGrain(address.TenantId, subscription).ListResourceGroupsAsync())
            .GetValueOrThrow().ShouldNotContain(address.ResourceGroup);

        (await Group(address).ListAsync()).GetValueOrThrow()
            .ShouldContain(
                x => x.ResourceId == address.Id,
                "the group's members are still there — the resource manager has to tear them down, "
                + "and until it does the resource is not gone.");
    }

    IResourceGroupGrain Group(ResourceId address) =>
        cluster.ResourceGroupGrain(address.TenantId, address.SubscriptionId, address.ResourceGroup);

    async Task<ResourceId> Provision(Guid tenant, string groupName, string resourceName)
    {
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync(
            "t" + tenant.ToString("N")[..8], "T", "eu-central")).IsSuccess.ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync(groupName, "eu-central")).IsSuccess.ShouldBeTrue();

        var address = new ResourceId(
            tenant,
            subscription,
            groupName,
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
            resourceName,
            Guid.NewGuid());

        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.CompleteCreateAsync(address.Id, ProvisioningState.Succeeded)).IsSuccess
            .ShouldBeTrue();

        return address;
    }
}
