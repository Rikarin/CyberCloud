using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Separation;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Shouldly;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     Route 7, closed and not closed, plus every tenancy grain type attacked across the tenant
///     boundary.
/// </summary>
/// <remarks>
///     <para>
///         <b>The seven original routes and where each lives.</b> Routes 1–6 are <i>storage</i>
///         claims and stay in <c>CyberCloud.ServiceDefaults.Tests.Storage.CrossTenantReachabilityTests</c>,
///         where the fixture is the storage fixture and where they still run unchanged: the same key
///         under tenant B is a different grain; tenant B's shard database does not contain tenant A's
///         bytes; tenant B's configured connection string points at a different PostgreSQL server;
///         forging the tenant inside the key does not change the tenant; tenant B's hot-key builder
///         refuses tenant A's grain id; tenant B's hash-tag namespace contains none of tenant A's
///         keys. None of the six is affected by communication separation, because none of them is
///         about a <i>call</i>.
///     </para>
///     <para>
///         <b>Route 7 is the one this file exists for, and the answer is: half closed.</b> It is
///         split into <see cref="Route7a_FromInsideAGrainTheRawKeyIsNowClosed" /> and
///         <see cref="Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN" /> because the two halves have
///         different answers and a single verdict would be wrong in whichever direction it was
///         written.
///     </para>
///     <para>
///         Every test uses its <b>own</b> pair of tenants. Sharing two tenants across the file would
///         make the tests order-dependent through the tenant grain's own state, which is the kind of
///         flake that gets a suite disabled.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class CrossTenantReachabilityTests(TenancyCluster cluster) {
    // ── Route 7 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Route7a_FromInsideAGrainTheRawKeyIsNowClosed() {
        // ⚠ THE INVERSION. CyberCloud.ServiceDefaults.Tests § Route7 asserts that a raw physical key
        // reaches the other tenant's state, and that no ICrossTenantAuthorizer is registered — which
        // is still true of THAT silo, because it wires storage only. With
        // AddMultitenantCommunicationSeparation wired, and with the CALLER being a grain, the same
        // move becomes the UnauthorizedAccessException docs/plan/04 § Silo composition promises,
        // "with both tenant ids in the message".
        var (a, b) = (A(7), B(7));
        await Seed(a, "route7a-victim");

        var rawKey = cluster.TenantGrain(a).GetGrainId().Key.ToString()!;
        rawKey.ShouldStartWith(TenancyCluster.Id(a));

        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route7a");
        (await attacker.MyTenantAsync()).ShouldBe(TenancyCluster.Id(b));

        var thrown =
            await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTenantByRawKeyAsync(rawKey));

        thrown.Message.ShouldContain(TenancyCluster.Id(a));
        thrown.Message.ShouldContain(TenancyCluster.Id(b));

        // And the state really was there to be reached — otherwise this test proves nothing.
        (await cluster.TenantGrain(a).GetAsync()).GetValueOrThrow().Slug.ShouldBe("route7a-victim");
    }

    [Fact]
    public async Task Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN() {
        // ⚠ THE LIMIT, AND IT IS NOT A DEFECT IN THE WIRING — IT IS WHAT THE MECHANISM IS.
        //
        // Orleans.Multitenant's TenantSeparatingCallFilter is an IIncomingGrainCallFilter whose
        // first act is:
        //
        //     var sourceId = context.SourceId;
        //     if (!sourceId.HasValue) return;   // "not coming from a grain … nothing to check"
        //     if (sourceType.IsClient() || sourceType.IsSystemTarget()) return;
        //
        // A caller that is not a grain has no tenant to attribute the call to, so the authorizer is
        // never consulted. Anything holding an IGrainFactory outside a grain context — a cluster
        // client, the gateway, a hosted service, this test — therefore still reaches any tenant's
        // grain by naming its physical key, exactly as before the separation was wired.
        //
        // docs/plan/04 § Silo composition's sentence remains true as written ("a bug in one
        // provider can read another tenant's grain" — a provider runs inside a grain), but it must
        // not be read as "no code can reach another tenant's grain". The boundary that closes THIS
        // half is the gateway, where a request's tenant is established from its token and ForTenant
        // is chosen (docs/plan/10). Until that exists, an IGrainFactory outside a grain is inside
        // the trust boundary, and that is a real limit on the tenancy guarantee.
        var a = A(70);
        await Seed(a, "route7b-victim");

        var rawKey = cluster.TenantGrain(a).GetGrainId().Key.ToString()!;

        var reached = await cluster.Grains.GetGrain<ITenantGrain>(rawKey).GetAsync();

        reached.IsSuccess.ShouldBeTrue(
            "a raw physical key from outside a grain context still reaches the state. If this ever "
            + "starts failing, communication separation has grown a client-side half — check "
            + "Orleans.Multitenant's release notes and rewrite this test to assert the new "
            + "behaviour; do not delete it."
        );
        reached.GetValueOrThrow().Slug.ShouldBe("route7b-victim");

        // The separation IS wired — this is not the old fixture with nothing registered.
        cluster.Services.GetServices<ICrossTenantAuthorizer>()
            .ShouldNotBeEmpty(
                "AddMultitenantCommunicationSeparation is wired; route 7b is open in spite of it, not "
                + "because it is missing."
            );

        // And the authorizer was genuinely never asked about this call.
        var before = cluster.Authorizer.Denied;
        await cluster.Grains.GetGrain<ITenantGrain>(rawKey).GetAsync();
        cluster.Authorizer.Denied.ShouldBe(
            before,
            "the filter returned before consulting the authorizer, because the source is a client."
        );
    }

    [Fact]
    public void TheSeparationIsRegisteredWithOurOwnAuthorizerAndSeparator() {
        // The registration assertion the old route-7 test made in the negative.
        cluster.Services.GetServices<ICrossTenantAuthorizer>()
            .ShouldContain(x => x is PlatformCrossTenantAuthorizer);

        cluster.Services.GetServices<IGrainCallTenantSeparator>()
            .ShouldContain(x => x is CyberCloudGrainCallTenantSeparator);
    }

    // ── Routes 8+: every tenancy grain type, tenant B with tenant A's ids ──────────────────────

    [Fact]
    public async Task Route8_TenantBCannotReachTenantAsSubscriptionFromInsideAGrain() {
        var (a, b) = (A(8), B(8));
        var subscription = Guid.NewGuid();

        await Seed(a, "route8");
        (await cluster.SubscriptionGrain(a, subscription).CreateAsync("A's prod")).IsSuccess
            .ShouldBeTrue();

        var rawKey = cluster.SubscriptionGrain(a, subscription).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route8");

        var thrown =
            await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachSubscriptionByRawKeyAsync(rawKey));

        thrown.Message.ShouldContain(TenancyCluster.Id(a));
        thrown.Message.ShouldContain(TenancyCluster.Id(b));
    }

    [Fact]
    public async Task Route9_TenantBUsingTenantAsSubscriptionIdInItsOwnQualificationSeesNothing() {
        // The other shape of the attack, and the one that is NOT an exception: tenant B builds a
        // well-formed key from tenant A's subscription id and reaches it under its own
        // qualification. That is allowed — and finds a different, empty grain, because the tenant is
        // in the physical key. This is route 1 in the tenancy domain.
        var (a, b) = (A(9), B(9));
        var subscription = Guid.NewGuid();

        await Seed(a, "route9");
        await cluster.SubscriptionGrain(a, subscription).CreateAsync("A's secret name");

        var asB = await cluster.SubscriptionGrain(b, subscription).GetAsync();

        asB.IsFailure.ShouldBeTrue();
        asB.Error!.Code.ShouldBe(ErrorCode.SubscriptionNotFound);
    }

    [Fact]
    public async Task Route10_TenantBCannotReachTenantAsResourceIndexFromInsideAGrain() {
        var (a, b) = (A(10), B(10));
        var address = Address(a, "route10");

        (await cluster.ResourceIndexGrain(address).TryClaimAsync(address, address.Id)).IsSuccess
            .ShouldBeTrue();

        var rawKey = cluster.ResourceIndexGrain(address).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route10");

        var thrown =
            await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachIndexByRawKeyAsync(rawKey));

        thrown.Message.ShouldContain(TenancyCluster.Id(a));
    }

    [Fact]
    public async Task Route11_TheSameResourcePathInTwoTenantsIsTwoIndexGrains() {
        // The index key hashes the CANONICAL PATH, which starts with the tenant id — so "the same
        // name" in two tenants is not the same name, and one tenant cannot deny another a name by
        // claiming it first.
        var (a, b) = (A(11), B(11));
        var inA = Address(a, "shared-name");
        var inB = Address(b, "shared-name");

        GrainKeys.PathIndex(inA).ShouldNotBe(GrainKeys.PathIndex(inB));

        (await cluster.ResourceIndexGrain(inA).TryClaimAsync(inA, inA.Id)).IsSuccess.ShouldBeTrue();
        (await cluster.ResourceIndexGrain(inB).TryClaimAsync(inB, inB.Id)).IsSuccess.ShouldBeTrue(
            "a tenant must not be able to burn another tenant's resource names."
        );
    }

    [Fact]
    public async Task Route12_TenantBCannotReachTenantAsQuotaFromInsideAGrain() {
        var (a, b) = (A(12), B(12));
        var subscription = Guid.NewGuid();

        (await cluster.QuotaGrain(a, subscription).SetLimitAsync(QuotaMeter.Vcpu, 8m))
            .IsSuccess.ShouldBeTrue();

        var rawKey = cluster.QuotaGrain(a, subscription).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route12");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachQuotaByRawKeyAsync(rawKey));
    }

    [Fact]
    public async Task Route13_TenantBCannotReachTenantAsResourceGroupFromInsideAGrain() {
        var (a, b) = (A(13), B(13));
        var subscription = Guid.NewGuid();

        await Seed(a, "route13");
        await cluster.SubscriptionGrain(a, subscription).CreateAsync("prod");
        (await cluster.SubscriptionGrain(a, subscription)
            .CreateResourceGroupAsync("secret-rg", "eu-central")).IsSuccess.ShouldBeTrue();

        var rawKey = cluster.ResourceGroupGrain(a, subscription, "secret-rg")
            .GetGrainId()
            .Key.ToString()!;

        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route13");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachResourceGroupByRawKeyAsync(rawKey));
    }

    [Fact]
    public async Task Route14_TenantBCannotReachTenantAsEmailIndexFromInsideAGrain() {
        var (a, b) = (A(14), B(14));
        var user = Guid.NewGuid();

        (await cluster.EmailIndexGrain(a, "alice@example.com")
            .TryClaimAsync("alice@example.com", user)).IsSuccess.ShouldBeTrue();

        var rawKey = cluster.EmailIndexGrain(a, "alice@example.com").GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route14");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachEmailIndexByRawKeyAsync(rawKey));
    }

    [Fact]
    public async Task Route15_TheSameEmailInTwoTenantsIsTwoClaimsBecauseUniquenessIsPerTenant() {
        // docs/plan/06 § Grain keys corrects an earlier table that specified a GLOBAL email index.
        // This is that correction as behaviour: the same address claimed in two tenants succeeds
        // twice, by two different users.
        var (a, b) = (A(15), B(15));
        var inA = Guid.NewGuid();
        var inB = Guid.NewGuid();

        (await cluster.EmailIndexGrain(a, "shared@example.com")
            .TryClaimAsync("shared@example.com", inA)).IsSuccess.ShouldBeTrue();

        (await cluster.EmailIndexGrain(b, "shared@example.com")
            .TryClaimAsync("shared@example.com", inB)).IsSuccess.ShouldBeTrue();

        (await cluster.EmailIndexGrain(a, "shared@example.com").GetAsync())
            .GetValueOrThrow()
            .BoundTo.ShouldBe(inA);

        (await cluster.EmailIndexGrain(b, "shared@example.com").GetAsync())
            .GetValueOrThrow()
            .BoundTo.ShouldBe(inB);
    }

    [Fact]
    public async Task Route16_ATenantScopedGrainMayReachTheNullTenantPlatformGrainsAndItIsCounted() {
        // ⚠ The edge that is deliberately ALLOWED, and the one place a key guarantee is traded for a
        // code guarantee — docs/plan/06 § Grain keys, the IClusterConnectionGrain ⚠. Asserted so
        // that "the authorizer allows this" is a decision on the record rather than a surprise.
        var reacher = cluster.For(A(16)).GetGrain<IReacherGrain>("res/route16");

        var before = cluster.Authorizer.AllowedNullTenantEdges;

        (await reacher.ReachTenantDirectoryAsync()).ShouldBeGreaterThanOrEqualTo(0);
        (await reacher.ReachShardMapAsync()).ShouldBeGreaterThanOrEqualTo(0);

        cluster.Authorizer.AllowedNullTenantEdges.ShouldBeGreaterThan(
            before,
            "the tenant → null-tenant edge must go through the authorizer, so that allowing it is "
            + "counted and logged rather than invisible."
        );
    }

    [Fact]
    public async Task Route17_ThePlatformTenantIsDeniedUntilAnOperatorRelationSaysOtherwise() {
        // docs/plan/06 § Platform administration, row 1: the platform → tenant edge is allowed only
        // when "the caller holds an active platform:root#operator relation". ReBAC is ADR-007's and
        // does not exist, so IPlatformOperatorAuthority's default DENIES — and the edge is closed.
        var a = A(17);
        await Seed(a, "route17");

        var rawKey = cluster.TenantGrain(a).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(Guid.Empty).GetGrain<IReacherGrain>("res/route17");

        cluster.Operators.Operator = null;

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTenantByRawKeyAsync(rawKey));

        // …and with an operator relation it is allowed, and counted.
        var before = cluster.Authorizer.AllowedPlatformEdges;
        cluster.Operators.Operator = "user:ops-1";

        try {
            (await attacker.ReachTenantByRawKeyAsync(rawKey)).ShouldBe("route17");
            cluster.Authorizer.AllowedPlatformEdges.ShouldBeGreaterThan(before);
        } finally {
            cluster.Operators.Operator = null;
        }
    }

    [Fact]
    public async Task Route18_ADelegationOpensTheTenantToTenantEdgeAndNothingElseDoes() {
        // docs/plan/06 § Platform administration, row 2 — "Lighthouse-shaped, P1". The store is
        // empty by default, so the edge is closed; this asserts the authorizer really does consult
        // it rather than hard-coding the denial.
        var (a, b) = (A(18), B(18));
        await Seed(a, "route18");

        var rawKey = cluster.TenantGrain(a).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route18");

        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTenantByRawKeyAsync(rawKey));

        cluster.Delegations.Delegations.Add((TenancyCluster.Id(b), TenancyCluster.Id(a)));

        try {
            (await attacker.ReachTenantByRawKeyAsync(rawKey)).ShouldBe("route18");
        } finally {
            cluster.Delegations.Delegations.Clear();
        }

        // Removing it closes the edge again — the delegation is consulted per call, not cached.
        await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTenantByRawKeyAsync(rawKey));
    }

    [Fact]
    public async Task Route19_ADelegationIsDirectionalSoBCannotBorrowAsGrant() {
        var (a, b) = (A(19), B(19));
        await Seed(a, "route19");

        var rawKey = cluster.TenantGrain(a).GetGrainId().Key.ToString()!;
        var attacker = cluster.For(b).GetGrain<IReacherGrain>("res/route19");

        // A → B is delegated, not B → A.
        cluster.Delegations.Delegations.Add((TenancyCluster.Id(a), TenancyCluster.Id(b)));

        try {
            await Should.ThrowAsync<UnauthorizedAccessException>(() => attacker.ReachTenantByRawKeyAsync(rawKey));
        } finally {
            cluster.Delegations.Delegations.Clear();
        }
    }

    [Fact]
    public async Task Route20_ATenantScopedGrainCannotBeReachedWithoutTenantQualificationAtAll() {
        // The mistake that is easiest to make and hardest to see: GetGrain instead of
        // ForTenant(...).GetGrain. It does not silently work against "some" tenant — the grain
        // refuses to activate, with a message naming the fix.
        var unqualified = cluster.Grains.GetGrain<ITenantGrain>(GrainKeys.Tenant(A(20)));

        var thrown = await Should.ThrowAsync<Exception>(() => unqualified.GetAsync());

        thrown.ToString().ShouldContain("ForTenant");
    }

    /// <summary>Tenant A for test number <paramref name="n" />.</summary>
    static Guid A(int n) => TenancyCluster.Tenant(1000 + n);

    /// <summary>Tenant B for test number <paramref name="n" />.</summary>
    static Guid B(int n) => TenancyCluster.Tenant(2000 + n);

    static ResourceId Address(Guid tenant, string name) =>
        new(
            tenant,
            TenancyCluster.Tenant(900),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            Guid.NewGuid()
        );

    async Task Seed(Guid tenant, string slug) {
        var created = await cluster.TenantGrain(tenant).CreateAsync(slug, slug, "eu-central");
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
    }
}
