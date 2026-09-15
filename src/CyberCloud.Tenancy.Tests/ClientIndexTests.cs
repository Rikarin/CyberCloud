using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     <c>IClientIndexGrain</c> — the <c>client_id</c> → application index ADR-015's degraded mode
///     requires. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     ⚠ <b>The same state machine as the email index, so this suite pins what is different rather
///     than re-proving the machine.</b> What is different: the value is an opaque, case-sensitive
///     client id rather than a folded address; the bound value is an application GUID; and the whole
///     point is that a <i>second</i> application cannot take a live client id. The machine's own
///     interruption behaviour — lease expiry, idempotent re-claim, mismatched-id conflict — is
///     <c>TwoPhaseCreateTests</c>' and is exercised here only where the client index adds something.
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class ClientIndexTests(TenancyCluster cluster) {
    static readonly Guid PortalApp = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
    static readonly Guid OtherApp = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000002");

    [Fact]
    public async Task AClaimedAndConfirmedClientIdResolvesToItsApplication() {
        var tenant = TenancyCluster.Tenant(40);
        var index = cluster.ClientIndexGrain(tenant, "portal");

        var claimed = await index.TryClaimAsync("portal", PortalApp);
        claimed.GetValueOrThrow().State.ShouldBe(IndexEntryState.Claimed);

        // ⚠ Not resolvable under a lease — a claim that may never be confirmed must not hand out a
        // client id's application, exactly as the path index refuses a leased claim.
        (await index.ResolveAsync()).IsFailure.ShouldBeTrue();

        (await index.ConfirmAsync(PortalApp)).GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);
        (await index.ResolveAsync()).GetValueOrThrow().ShouldBe(PortalApp);
    }

    [Fact]
    public async Task ASecondApplicationCannotTakeALiveClientId() {
        // ⚠ THE PROPERTY THAT MATTERS. Two applications sharing one client_id is two registrations an
        // authorization request cannot tell apart — the single-threaded index activation is what
        // makes it a 409 rather than a race.
        var tenant = TenancyCluster.Tenant(41);
        var index = cluster.ClientIndexGrain(tenant, "portal");

        (await index.TryClaimAsync("portal", PortalApp)).IsSuccess.ShouldBeTrue();
        await index.ConfirmAsync(PortalApp);

        var refused = await index.TryClaimAsync("portal", OtherApp);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    [Fact]
    public async Task ReleasingAClientIdLetsAnotherApplicationTakeIt() {
        var tenant = TenancyCluster.Tenant(42);
        var index = cluster.ClientIndexGrain(tenant, "cyc");

        await index.TryClaimAsync("cyc", PortalApp);
        await index.ConfirmAsync(PortalApp);

        (await index.ReleaseAsync(PortalApp)).IsSuccess.ShouldBeTrue();
        (await index.ResolveAsync()).IsFailure.ShouldBeTrue();

        (await index.TryClaimAsync("cyc", OtherApp)).IsSuccess.ShouldBeTrue();
        await index.ConfirmAsync(OtherApp);
        (await index.ResolveAsync()).GetValueOrThrow().ShouldBe(OtherApp);
    }

    [Fact]
    public async Task AnExpiredLeaseFreesAClientIdThatWasNeverConfirmed() {
        var tenant = TenancyCluster.Tenant(43);
        var index = cluster.ClientIndexGrain(tenant, "abandoned");

        await index.TryClaimAsync("abandoned", PortalApp);

        // The create that claimed this id died before confirming. Five minutes later the name is
        // free — evaluated on read, so no timer had to fire.
        cluster.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (await index.TryClaimAsync("abandoned", OtherApp)).IsSuccess.ShouldBeTrue(
            "an expired lease should have freed the client id for a different application."
        );
    }

    [Fact]
    public async Task TheSameClientIdInTwoTenantsAreTwoDistinctActivations() {
        // docs/plan/11 § Protocol — a client_id is unique within a tenant, so `portal` in two tenants
        // is two entries and one tenant's registration says nothing about the other's.
        var one = TenancyCluster.Tenant(44);
        var two = TenancyCluster.Tenant(45);

        await cluster.ClientIndexGrain(one, "portal").TryClaimAsync("portal", PortalApp);
        await cluster.ClientIndexGrain(one, "portal").ConfirmAsync(PortalApp);

        // The other tenant's `portal` is untouched and free to claim for a different application.
        (await cluster.ClientIndexGrain(two, "portal").ResolveAsync()).IsFailure.ShouldBeTrue();
        (await cluster.ClientIndexGrain(two, "portal").TryClaimAsync("portal", OtherApp)).IsSuccess.ShouldBeTrue();

        cluster.ClientIndexGrain(one, "portal").GetGrainId()
            .ShouldNotBe(cluster.ClientIndexGrain(two, "portal").GetGrainId());
    }

    [Fact]
    public async Task AClientIdCarryingTheDigestSeparatorIsRefusedBeforeAnyBinding() {
        // ⚠ A newline is the digest separator, so a client id carrying one could be re-cut into a
        // different (tenant, client) pair. The grain re-validates rather than trusting the caller —
        // reaching it needs a well-formed key, so this asserts the grain's own guard on the value.
        var tenant = TenancyCluster.Tenant(46);
        var index = cluster.ClientIndexGrain(tenant, "safe");

        var refused = await index.TryClaimAsync("safe\nother", PortalApp);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }
}
