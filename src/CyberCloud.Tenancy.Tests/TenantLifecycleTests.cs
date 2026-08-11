using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/06 § Tenant lifecycle, as behaviour rather than as a table.
/// </summary>
[Collection(TenancySuite.Name)]
public sealed class TenantLifecycleTests(TenancyCluster cluster)
{
    static Guid Tenant(int n) => TenancyCluster.Tenant(51_000 + n);

    [Fact]
    public async Task ATenantIsCreatedInProvisioningWithNoApiAccessYet()
    {
        var grain = cluster.TenantGrain(Tenant(1));

        var created = (await grain.CreateAsync("life-1", "Life 1", "eu-central")).GetValueOrThrow();

        created.Status.ShouldBe(TenantStatus.Provisioning);
        (await grain.AreControlPlaneWritesAllowedAsync()).GetValueOrThrow().ShouldBeFalse(
            "Provisioning means 'no API access yet'.");
    }

    [Fact]
    public async Task CreationIsIdempotentBecauseEveryStepIsReDrivable()
    {
        // docs/plan/06 § Tenant lifecycle: "Every step is idempotent and re-drivable."
        var grain = cluster.TenantGrain(Tenant(2));

        var first = (await grain.CreateAsync("life-2", "Life 2", "eu-central")).GetValueOrThrow();
        var again = (await grain.CreateAsync("life-2", "Life 2", "eu-central")).GetValueOrThrow();

        again.ShouldBe(first);
    }

    [Fact]
    public async Task ARedriveWithDifferentArgumentsIsAConflictNotARename()
    {
        var grain = cluster.TenantGrain(Tenant(3));

        (await grain.CreateAsync("life-3", "Life 3", "eu-central")).IsSuccess.ShouldBeTrue();

        var renamed = await grain.CreateAsync("life-3-renamed", "Life 3", "eu-central");

        renamed.IsFailure.ShouldBeTrue();
        renamed.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task SuspendedRejectsControlPlaneWritesAndSaysNothingAboutTheDataPlane()
    {
        // ⚠ The row with the most consequence: "Data plane keeps running, control-plane writes
        // rejected 403. Deliberate: suspending a tenant should not take their production down
        // without notice."
        var grain = cluster.TenantGrain(Tenant(4));

        (await grain.CreateAsync("life-4", "Life 4", "eu-central")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Active, "bootstrap done")).IsSuccess.ShouldBeTrue();

        (await grain.AreControlPlaneWritesAllowedAsync()).GetValueOrThrow().ShouldBeTrue();

        (await grain.SetStatusAsync(TenantStatus.Suspended, "overdue past grace")).IsSuccess
            .ShouldBeTrue();

        (await grain.AreControlPlaneWritesAllowedAsync()).GetValueOrThrow().ShouldBeFalse();

        // …and the tenant's existing state is still readable and its grains still work, which is
        // the closest a control-plane test can come to "the data plane keeps running".
        (await grain.GetAsync()).GetValueOrThrow().Status.ShouldBe(TenantStatus.Suspended);
        (await grain.ListSubscriptionsAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task WarnedStillAllowsWritesBecauseItIsABannerNotABlock()
    {
        var grain = cluster.TenantGrain(Tenant(5));

        (await grain.CreateAsync("life-5", "Life 5", "eu-central")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Active, "ok")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Warned, "payment overdue")).IsSuccess.ShouldBeTrue();

        (await grain.AreControlPlaneWritesAllowedAsync()).GetValueOrThrow().ShouldBeTrue(
            "Warned is 'Portal banner; writes allowed'.");
    }

    [Fact]
    public async Task PurgedIsTerminalAndNothingComesBackFromIt()
    {
        var grain = cluster.TenantGrain(Tenant(6));

        (await grain.CreateAsync("life-6", "Life 6", "eu-central")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Active, "ok")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.PendingDeletion, "customer asked")).IsSuccess
            .ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Purged, "30 days elapsed")).IsSuccess.ShouldBeTrue();

        var revived = await grain.SetStatusAsync(TenantStatus.Active, "oops");

        revived.IsFailure.ShouldBeTrue();
        revived.Error!.Code.ShouldBe(ErrorCode.Conflict);
        revived.Error.Message.ShouldContain("terminal");
    }

    [Fact]
    public async Task APendingDeletionTenantCanBeRestoredBecauseTheTombstoneIsThirtyDays()
    {
        // "PendingDeletion | 30-day tombstone | Nothing runs, nothing is billed, everything is
        // restorable."
        var grain = cluster.TenantGrain(Tenant(7));

        (await grain.CreateAsync("life-7", "Life 7", "eu-central")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.Active, "ok")).IsSuccess.ShouldBeTrue();
        (await grain.SetStatusAsync(TenantStatus.PendingDeletion, "customer asked")).IsSuccess
            .ShouldBeTrue();

        (await grain.SetStatusAsync(TenantStatus.Active, "customer changed their mind")).IsSuccess
            .ShouldBeTrue("everything is restorable.");
    }

    [Fact]
    public async Task AnIllegalTransitionNamesTheOnesThatAreLegal()
    {
        var grain = cluster.TenantGrain(Tenant(8));

        (await grain.CreateAsync("life-8", "Life 8", "eu-central")).IsSuccess.ShouldBeTrue();

        // Provisioning → Warned is not in the table.
        var refused = await grain.SetStatusAsync(TenantStatus.Warned, "no");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("Active");
        refused.Error.Message.ShouldContain("docs/plan/06");
    }

    [Fact]
    public async Task AStatusChangeNeedsAReasonBecauseTheAuditLogIsWrittenFromIt()
    {
        var grain = cluster.TenantGrain(Tenant(9));

        (await grain.CreateAsync("life-9", "Life 9", "eu-central")).IsSuccess.ShouldBeTrue();

        var refused = await grain.SetStatusAsync(TenantStatus.Active, "  ");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task TheEtagAdvancesOnEveryChangeSoIfMatchCanWork()
    {
        // docs/plan/06 § Tags, locks: "etag enables If-Match and is the only way to make concurrent
        // portal edits safe."
        var grain = cluster.TenantGrain(Tenant(10));

        var created = (await grain.CreateAsync("life-10", "Life 10", "eu-central")).GetValueOrThrow();
        var active = (await grain.SetStatusAsync(TenantStatus.Active, "ok")).GetValueOrThrow();

        active.Version.ShouldBeGreaterThan(created.Version);
        active.ModifiedAt.ShouldBeGreaterThanOrEqualTo(created.ModifiedAt);
    }

    [Fact]
    public async Task ASlugMustBeADnsLabelBecauseItBecomesAKubernetesObjectName()
    {
        // docs/plan/06 § Identifiers: DNS-1123, "chosen because these names end up as Kubernetes
        // object names and the alternative is a mangling function nobody can invert".
        var grain = cluster.TenantGrain(Tenant(11));

        var refused = await grain.CreateAsync("Not A Slug", "x", "eu-central");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceName);
    }

    [Fact]
    public async Task ATenantMustBeHomedToARegion()
    {
        // docs/plan/04 § The clusters, plural: "A tenant is homed to exactly one region at
        // creation." Not optional, and not defaulted — a default would silently home every tenant
        // in whichever region the first silo happened to be.
        var grain = cluster.TenantGrain(Tenant(12));

        var refused = await grain.CreateAsync("life-12", "Life 12", "   ");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task TheTenantGrainsStateSurvivesADeactivation()
    {
        var grain = cluster.TenantGrain(Tenant(13));

        (await grain.CreateAsync("life-13", "Life 13", "us-east")).IsSuccess.ShouldBeTrue();
        (await grain.AddSubscriptionAsync(Guid.NewGuid())).IsSuccess.ShouldBeTrue();

        await grain.DeactivateAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        var revived = cluster.TenantGrain(Tenant(13));
        (await revived.GetAsync()).GetValueOrThrow().HomeRegion.ShouldBe("us-east");
        (await revived.ListSubscriptionsAsync()).GetValueOrThrow().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ASubscriptionOwnsItsResourceGroupsAndTheNameIsUniqueWithinIt()
    {
        // docs/plan/06 § The hierarchy: a resource group name is unique within its subscription, not
        // within the tenant — so the same name in two subscriptions is two groups.
        var tenant = Tenant(14);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("life-14", "L", "eu-central")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, first).CreateAsync("prod")).IsSuccess.ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, second).CreateAsync("staging")).IsSuccess
            .ShouldBeTrue();

        (await cluster.SubscriptionGrain(tenant, first).CreateResourceGroupAsync("app", "eu-central"))
            .IsSuccess.ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, second).CreateResourceGroupAsync("app", "eu-central"))
            .IsSuccess.ShouldBeTrue("the same group name in a different subscription is fine.");

        (await cluster.SubscriptionGrain(tenant, first).ListResourceGroupsAsync()).GetValueOrThrow()
            .ShouldBe(["app"]);

        cluster.ResourceGroupGrain(tenant, first, "app").GetGrainId()
            .ShouldNotBe(cluster.ResourceGroupGrain(tenant, second, "app").GetGrainId());
    }

    [Fact]
    public async Task AResourceGroupCannotBeCreatedInASubscriptionThatDoesNotExist()
    {
        var tenant = Tenant(15);
        (await cluster.TenantGrain(tenant).CreateAsync("life-15", "L", "eu-central")).IsSuccess
            .ShouldBeTrue();

        var refused = await cluster.SubscriptionGrain(tenant, Guid.NewGuid())
            .CreateResourceGroupAsync("app", "eu-central");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.SubscriptionNotFound);
    }

    [Fact]
    public async Task AResourceGroupNameMustBeADnsLabelToo()
    {
        var tenant = Tenant(16);
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("life-16", "L", "eu-central")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();

        var refused = await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync("Prod RG", "eu-central");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceName);
    }

    [Fact]
    public async Task AMembershipRecordPointingOutsideTheGroupIsRefused()
    {
        // The guard that keeps "delete the group, delete its contents" from deleting somebody
        // else's contents.
        var tenant = Tenant(17);
        var subscription = Guid.NewGuid();

        (await cluster.TenantGrain(tenant).CreateAsync("life-17", "L", "eu-central")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription).CreateAsync("prod")).IsSuccess
            .ShouldBeTrue();
        (await cluster.SubscriptionGrain(tenant, subscription)
            .CreateResourceGroupAsync("mine", "eu-central")).IsSuccess.ShouldBeTrue();

        var elsewhere = new Core.Resources.ResourceId(
            tenant,
            subscription,
            "somebody-elses",
            new Core.Resources.ResourceTypeName("CyberCloud.Compute", "virtualMachines"),
            "vm-1",
            Guid.NewGuid());

        var refused = await cluster.ResourceGroupGrain(tenant, subscription, "mine")
            .BeginCreateAsync(elsewhere);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }
}
