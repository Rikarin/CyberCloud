using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     Going under the write path and asking the grains directly.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this is worth testing at all, when the gateway is the only public door.</b>
///         docs/plan/07 § The enforcement seam is one check in one place, which is the right design
///         and is also a single point of failure: everything below it is protected by the <i>grain
///         key</i> rather than by a check. ADR-002 puts the tenant id in that key and
///         <c>Orleans.Multitenant</c> refuses a crossing — so the question this class asks is whether
///         the second layer is really there, or whether the platform has one layer and a belief.
///     </para>
///     <para>
///         The calls here go through the <b>client</b> grain factory, which is the harshest position:
///         <c>Orleans.Multitenant</c>'s call filter never sees a caller that is not a grain (see
///         <c>CyberCloud.Tenancy/TenancySiloBuilderExtensions.cs</c> § the residue), so nothing but
///         the key itself separates these calls. That is exactly the position the gateway is in.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class BelowTheManagerTests(IsolationCluster cluster) {
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnotherTenantsResourceGrainIsADifferentActivationAndIsEmpty(IsolationTarget target) {
        // The attacker has the victim's resource GUID — from a log, a support ticket, a leaked trace —
        // and asks for the grain under their own tenant qualification.
        var id = await cluster.CreateAsync(
            target,
            "grain-probe",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var theirs = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceGrain>(GrainKeys.Resource(id))
            .GetAsync(target.ApiVersion, []);

        theirs.IsSuccess.ShouldBeTrue("the harness cannot read its own resource, so this proves nothing");

        var mine = await cluster.For(IsolationCluster.Attacker)
            .GetGrain<IResourceGrain>(GrainKeys.Resource(id))
            .GetAsync(target.ApiVersion, []);

        mine.IsFailure.ShouldBeTrue("the same resource GUID under another tenant reached the same state");
        mine.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnotherTenantsOperationCannotBePolled(IsolationTarget target) {
        // An operation id is the one identifier the platform hands out in a response header —
        // Azure-Async-Operation — so it is the identifier most likely to leak into a log or a bug
        // report. Polling one from the wrong tenant must find nothing.
        var address = IsolationCluster.Address(
            target,
            "operation-probe",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        var operationId = accepted.GetValueOrThrow().OperationId;

        var theirs = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId))
            .GetAsync();

        theirs.IsSuccess.ShouldBeTrue("the harness cannot poll its own operation, so this proves nothing");

        var mine = await cluster.For(IsolationCluster.Attacker)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId))
            .GetAsync();

        mine.IsFailure.ShouldBeTrue("another tenant's operation was pollable");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnotherTenantsIndexEntryIsInvisibleEvenWithTheirExactPath(IsolationTarget target) {
        // ⚠ DOUBLE PROTECTION, AND THIS ASSERTS BOTH HALVES SEPARATELY. The index key is a hash of the
        // resource's CANONICAL PATH, which already contains the victim's tenant GUID, and the grain
        // reference is tenant-qualified on top of that. Either alone would do; the test is that both
        // are there, because the day somebody "simplifies" the key to drop the tenant, the
        // qualification is what is left.
        await cluster.CreateAsync(
            target,
            "index-probe",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster.Address(
            target,
            "index-probe",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var theirs = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address))
            .GetAsync();

        theirs.GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);

        // Same key — the victim's path — under the attacker's qualification.
        var mine = await cluster.For(IsolationCluster.Attacker)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address))
            .GetAsync();

        mine.GetValueOrThrow().State.ShouldBe(
            IndexEntryState.Free,
            "the victim's index entry was readable under the attacker's tenant qualification"
        );

        mine.GetValueOrThrow().BoundTo.ShouldBe(Guid.Empty);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheIndexKeyItselfDiffersBetweenTenantsForTheSameNameAndGroup(IsolationTarget target) {
        // The other half of the same property, asserted on the key rather than on the grain: two
        // tenants using the same subscription-shaped path must not collide, so one cannot squat the
        // other's name.
        await Task.CompletedTask;

        var victim = IsolationCluster.Address(
            target,
            "same-name",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var attacker = IsolationCluster.Address(
            target,
            "same-name",
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription
        );

        GrainKeys.PathIndex(victim).ShouldNotBe(GrainKeys.PathIndex(attacker));

        // And the canonical path the key hashes carries the tenant, so the difference is not accidental.
        victim.CanonicalPath.ShouldContain(IsolationCluster.Victim.ToString("D", CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnotherTenantsSubscriptionIdUnderYourOwnTenantReachesNoneOfTheirState(IsolationTarget target) {
        // ⚠ A GAP, ASSERTED FROM THE SAFE SIDE. The write path compares the PATH's tenant against the
        // caller's and never checks that the path's SUBSCRIPTION belongs to that tenant — nothing in
        // docs/plan/08 § The write path, end to end's eleven steps does. So an attacker may address
        // /tenants/{their own}/subscriptions/{somebody else's}/... and the request is accepted.
        //
        // It leaks nothing, and this test is what says so: every grain the request touches is reached
        // through ForTenant(caller), so the quota, the index and the resource all land in the
        // ATTACKER's tenant under a subscription GUID that happens to be somebody else's label. What
        // it does mean is that subscription ids are not validated, which is worth knowing before
        // billing reads one.
        var confused = IsolationCluster.Address(
            target,
            "subscription-confusion",
            IsolationCluster.Attacker,
            IsolationCluster.VictimSubscription
        );

        // The attacker owns `prod` in their OWN subscription, not in the victim's, so the create is
        // refused for want of a tuple rather than accepted — which is a happier answer than the one
        // this test was written expecting, and is recorded rather than assumed.
        var attempt = await cluster.Manager.WriteAsync(
            new() {
                Path = confused.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser)
            },
            TestContext.Current.CancellationToken
        );

        if (attempt.IsSuccess) {
            // Accepted: then every touched grain must be in the attacker's tenant and none of the
            // victim's state may have moved.
            var victimIndex = await cluster.For(IsolationCluster.Victim)
                .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(confused))
                .GetAsync();

            victimIndex.GetValueOrThrow().State.ShouldBe(
                IndexEntryState.Free,
                "a create under the attacker's tenant claimed a name in the victim's index"
            );
        }
        else {
            attempt.Error!.Code.ShouldBe(
                ErrorCode.ResourceNotFound,
                "a refusal here must still be the invisible answer, not an authorization one"
            );
        }

        var victimQuota = cluster.For(IsolationCluster.Victim)
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription));

        var attackerQuota = cluster.For(IsolationCluster.Attacker)
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription));

        // ⚠ The same subscription GUID, two tenants, two grains. That is the property that makes the
        // missing subscription-ownership check survivable.
        (await victimQuota.ListLeasesAsync()).IsSuccess.ShouldBeTrue();
        (await attackerQuota.ListLeasesAsync()).IsSuccess.ShouldBeTrue();

        victimQuota.GetPrimaryKeyString().ShouldNotBe(attackerQuota.GetPrimaryKeyString());
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;
}
