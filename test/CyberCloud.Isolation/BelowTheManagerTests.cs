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
    public async Task AnotherTenantsSubscriptionIdUnderYourOwnTenantIs404(IsolationTarget target) {
        // ⚠ THIS TEST USED TO DOCUMENT A GAP FROM THE SAFE SIDE AND NOW ASSERTS THAT IT IS CLOSED.
        //
        // The write path compared the PATH's tenant against the caller's and never looked at the
        // path's SUBSCRIPTION, so /tenants/{mine}/subscriptions/{theirs}/… was accepted. It leaked
        // nothing — the property at the bottom of this test is why: every grain is reached through
        // ForTenant(caller), so the quota, the index and the resource all landed in the CALLER's
        // tenant under a GUID that was merely somebody else's label. What it meant is that
        // subscription ids were never validated at all, and an unvalidated id stops being survivable
        // the moment billing or quota reporting keys a number on one.
        //
        // Step 1 now reads ISubscriptionGrain through ForTenant(caller), which makes "exists" and
        // "belongs to this tenant" the same question, and answers 404 to both.
        var confused = IsolationCluster.Address(
            target,
            "subscription-confusion",
            IsolationCluster.Attacker,
            IsolationCluster.VictimSubscription
        );

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

        attempt.IsFailure.ShouldBeTrue("a path naming another tenant's subscription was accepted");
        attempt.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "a subscription in another tenant must be indistinguishable from one that does not "
            + "exist — docs/plan/07 § The enforcement seam"
        );

        // ⚠ And it was refused BEFORE anything was claimed, in either tenant. A check that answered
        // 404 after taking the name would be a denial-of-service dressed as a refusal.
        var attackerIndex = await cluster.For(IsolationCluster.Attacker)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(confused))
            .GetAsync();

        attackerIndex.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);

        var victimIndex = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(confused))
            .GetAsync();

        victimIndex.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);

        var victimQuota = cluster.For(IsolationCluster.Victim)
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription));

        var attackerQuota = cluster.For(IsolationCluster.Attacker)
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription));

        // ⚠ The same subscription GUID, two tenants, two grains — kept as an assertion because it is
        // the second layer. The step-1 check is one line in one service; ADR-002's tenant-qualified
        // key is what holds if somebody ever removes it.
        (await victimQuota.ListLeasesAsync()).IsSuccess.ShouldBeTrue();
        (await attackerQuota.ListLeasesAsync()).IsSuccess.ShouldBeTrue();

        victimQuota.GetPrimaryKeyString().ShouldNotBe(attackerQuota.GetPrimaryKeyString());
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ASubscriptionInAnotherTenantAnswersByteIdenticallyToOneThatNeverExisted(
        IsolationTarget target
    ) {
        // ⚠ THE ORACLE THE STATUS CODE ALONE DOES NOT CLOSE, APPLIED TO SUBSCRIPTIONS. Two 404s whose
        // messages differ are still a way to ask "is this GUID a live subscription somewhere". A
        // subscription id is exactly as enumerable as a resource name and leaks more: it is the
        // billing boundary, so knowing one exists is knowing a customer does.
        var theirs = IsolationCluster.Address(
            target,
            "sub-oracle",
            IsolationCluster.Attacker,
            IsolationCluster.VictimSubscription
        );

        var invented = IsolationCluster.Address(
            target,
            "sub-oracle",
            IsolationCluster.Attacker,
            Guid.Parse("99999999-9999-4999-8999-999999999999")
        );

        var caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser);

        var onReal = await cluster.Manager.ReadAsync(
            new() { Path = theirs.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        var onInvented = await cluster.Manager.ReadAsync(
            new() { Path = invented.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        onReal.IsFailure.ShouldBeTrue();
        onInvented.IsFailure.ShouldBeTrue();

        onReal.Error!.Code.ShouldBe(onInvented.Error!.Code);
        onReal.Error.Target.ShouldBe(onInvented.Error.Target);
        onReal.Error.Details.ShouldBeEmpty();

        // The one legitimate difference: each message quotes the path it was given. Blank that out
        // and the two messages are the same bytes.
        onReal.Error.Message.Replace(theirs.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(onInvented.Error.Message.Replace(invented.Path, "PATH", StringComparison.Ordinal));

        // ⚠ AND IT IS THE SAME ANSWER AS A PLAIN MISSING RESOURCE, WHICH IS THE STRONGER CLAIM.
        // Byte-identical to each other would be satisfied by a "no such subscription" message that
        // was consistent with itself and still told the attacker which of the two things was wrong.
        // This third probe uses the attacker's OWN, perfectly valid subscription and a name nobody
        // took — so the only thing it has in common with the other two is that the answer must not
        // say which layer refused.
        var mine = IsolationCluster.Address(
            target,
            "sub-oracle",
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription
        );

        var onMine = await cluster.Manager.ReadAsync(
            new() { Path = mine.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        onMine.IsFailure.ShouldBeTrue();
        onMine.Error!.Code.ShouldBe(onReal.Error.Code);

        onReal.Error.Message.Replace(theirs.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(
                onMine.Error.Message.Replace(mine.Path, "PATH", StringComparison.Ordinal),
                "a bad subscription and a bad resource name give different answers, so the shape of "
                + "the message says which layer refused"
            );
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;
}
