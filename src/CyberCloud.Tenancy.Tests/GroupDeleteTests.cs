using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     The group's own half of docs/plan/06 § Two-phase create in reverse: the seal, the refusal, and
///     the record of which clusters the group's namespaces are on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seal is the only thing that closes the create-during-delete race, and it closes
///         it because the check and the write are one grain turn.</b> A caller that listed the
///         members and then sealed would leave exactly the window this exists to shut — an Orleans
///         grain is single-threaded, so putting both inside one method is not a convenience, it is
///         the mechanism.
///     </para>
///     <para>
///         ⚠ <b>What is NOT here.</b> The namespace half — the inventory, the verdict and the
///         recursive delete — is <c>ResourceGroupReclaimer</c>'s and is asserted in the resource
///         manager's suites against a cluster this assembly cannot see.
///         <c>CyberCloud.Tenancy</c> holds no reference to <c>CyberCloud.Kubernetes.Contracts</c> at
///         all, which is the same assembly rule that keeps namespace creation out of this grain.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class GroupDeleteTests(TenancyCluster cluster) {
    static Guid Cluster { get; } = new("5c1a0000-0000-4000-8000-000000000001");

    // ── The seal ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGroupThatStillHoldsAMemberRefusesToBeSealedAndNamesIt() {
        // ⚠ REFUSE, NOT CASCADE. A cascade is a per-resource delete with each resource's own lock,
        // authorization, soft-delete window and failable teardown; one that skipped those would be a
        // way to delete a locked resource by deleting its group.
        var address = await ProvisionAsync(Tenant(1), "held", "web-01");
        var group = Group(address);

        var refused = await group.BeginGroupDeleteAsync();

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.Conflict);
        error.Message.ShouldContain(address.CanonicalPath);

        (await group.GetAsync()).GetValueOrThrow()
            .State.ShouldBe(
                ProvisioningState.Succeeded,
                "a refused seal must not half-seal the group: the tenant is being told to empty it "
                + "first, and a group they cannot then add to is a group they cannot empty either."
            );
    }

    [Fact]
    public async Task SealingAnEmptyGroupPutsItInDeletingAndIsIdempotent() {
        var address = await ProvisionAsync(Tenant(2), "sealable", "web-01");
        var group = Group(address);

        await EmptyAsync(group, address);

        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.GetAsync()).GetValueOrThrow().State.ShouldBe(ProvisioningState.Deleting);

        // ⚠ Idempotent, because the whole choreography is re-driven whenever a namespace reclaim
        // refuses — and a second seal that failed would turn every re-drive into a dead end.
        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.GetAsync()).GetValueOrThrow().State.ShouldBe(ProvisioningState.Deleting);
    }

    [Fact]
    public async Task ASealedGroupRefusesANewMember() {
        // ⚠ THE HALF OF THE SEAL THAT DOES THE WORK. BeginGroupDeleteAsync setting a state nobody
        // reads would be decorative: a resource whose membership is recorded after the namespace has
        // been judged empty gets its objects destroyed by a verdict that was true when it was
        // reached.
        var address = await ProvisionAsync(Tenant(3), "sealed-rg", "web-01");
        var group = Group(address);

        await EmptyAsync(group, address);
        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();

        var latecomer = address with { Name = "web-02", Id = Guid.NewGuid() };

        var refused = await group.BeginCreateAsync(latecomer);

        refused.TryGetError(out var error).ShouldBeTrue(
            "a create that lands inside a delete is the race the seal exists to close."
        );

        error.Code.ShouldBe(ErrorCode.Conflict);
        (await group.ListAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    // ── The last step ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRecordCannotBeRemovedWithoutTheSealHavingBeenSet() {
        // The ordering, as a refusal. A caller that reached the last step without the first has
        // skipped the only step that closes the race, and is refused rather than obeyed.
        var address = await ProvisionAsync(Tenant(4), "unsealed", "web-01");
        var group = Group(address);

        await EmptyAsync(group, address);

        var refused = await group.CompleteGroupDeleteAsync();

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.Conflict);
        (await group.GetAsync()).IsSuccess.ShouldBeTrue("the group is still there.");
    }

    [Fact]
    public async Task TheWholeSequenceRemovesTheGroupAndIsIdempotentAfterwards() {
        var address = await ProvisionAsync(Tenant(5), "gone", "web-01");
        var group = Group(address);

        await EmptyAsync(group, address);

        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.CompleteGroupDeleteAsync()).IsSuccess.ShouldBeTrue();

        (await group.GetAsync()).IsFailure.ShouldBeTrue();

        // ⚠ Absence is the goal, so a re-driven delete after a network timeout must not report a
        // failure for work that succeeded.
        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.CompleteGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
    }

    // ── Which clusters the namespaces are on ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheClustersAGroupPlacedObjectsOnAreRecordedAndSurviveUntilTheGroupGoes() {
        // ⚠ WITHOUT THIS THE GROUP DELETE HAS NOTHING TO ENUMERATE. A namespace is keyed by (group,
        // cluster) and a group may span clusters; by the time the delete runs every member is gone —
        // that is its precondition — so nothing else in the control plane can say which clusters
        // those were.
        var address = await ProvisionAsync(Tenant(6), "spanning", "web-01");
        var group = Group(address);
        var second = new Guid("5c1a0000-0000-4000-8000-000000000002");

        (await group.RecordClusterAsync(Cluster)).IsSuccess.ShouldBeTrue();
        (await group.RecordClusterAsync(second)).IsSuccess.ShouldBeTrue();

        // Idempotent: every silo records the same cluster whenever its own memo misses.
        (await group.RecordClusterAsync(Cluster)).IsSuccess.ShouldBeTrue();

        (await group.ListClustersAsync()).GetValueOrThrow()
            .Order()
            .ShouldBe([Cluster, second]);

        await EmptyAsync(group, address);
        (await group.BeginGroupDeleteAsync()).IsSuccess.ShouldBeTrue();

        (await group.ListClustersAsync()).GetValueOrThrow().Count.ShouldBe(
            2,
            "the seal must not forget them — they are what the reclaim that follows reads."
        );

        (await group.CompleteGroupDeleteAsync()).IsSuccess.ShouldBeTrue();
        (await group.ListClustersAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheEmptyGuidIsNotACluster() {
        // ⚠ Recording it would make the delete try to reclaim a namespace on a cluster that does not
        // exist and report that refusal as though a real cluster had refused — a group that can
        // never be deleted, for a reason that names nothing.
        var address = await ProvisionAsync(Tenant(7), "no-cluster", "web-01");

        var refused = await Group(address).RecordClusterAsync(Guid.Empty);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        (await Group(address).ListClustersAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task AGroupThatWasNeverCreatedRecordsNothing() {
        var group = cluster.ResourceGroupGrain(Tenant(8), Guid.NewGuid(), "never-made");

        (await group.RecordClusterAsync(Cluster)).IsFailure.ShouldBeTrue();
    }

    // ── The harness ──────────────────────────────────────────────────────────────────────────────

    static Guid Tenant(int n) => TenancyCluster.Tenant(4200 + n);

    IResourceGroupGrain Group(ResourceId address) =>
        cluster.ResourceGroupGrain(address.TenantId, address.SubscriptionId, address.ResourceGroup);

    static async Task EmptyAsync(IResourceGroupGrain group, ResourceId address) {
        (await group.BeginDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.CompleteDeleteAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await group.ListAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    async Task<ResourceId> ProvisionAsync(Guid tenant, string groupName, string resourceName) {
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("t" + tenant.ToString("N")[..8], "T", "eu-central"))
            .IsSuccess.ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess.ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync(groupName, "eu-central")).IsSuccess.ShouldBeTrue();

        var address = new ResourceId(
            tenant,
            subscription,
            groupName,
            new("CyberCloud.DBforPostgreSQL", "servers"),
            resourceName,
            Guid.NewGuid()
        );

        var group = Group(address);

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess.ShouldBeTrue();
        (await group.BeginCreateAsync(address)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(address).ConfirmAsync(address.Id)).IsSuccess.ShouldBeTrue();

        (await group.CompleteCreateAsync(address.Id, ProvisioningState.Succeeded)).IsSuccess.ShouldBeTrue();

        return address;
    }
}
