using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Step 1's second ownership check: the path's <b>subscription</b> is one this tenant has.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The write path used to compare the path's tenant against the caller's and never look
///         at the subscription at all.</b> <c>/tenants/{mine}/subscriptions/{anything}/…</c> parsed,
///         passed, and ran the whole path against a GUID nobody had checked. It leaked nothing,
///         because every grain below step 1 is reached through <c>ForTenant(caller)</c> — so the
///         quota, the index and the resource all landed in the caller's own tenant under a
///         meaningless label. That is precisely why it survived: nothing failed, and the damage is
///         downstream, in the first component that reads a subscription id and believes it. Billing
///         and quota reporting are both that component.
///     </para>
///     <para>
///         ⚠ <b>This suite is where the check is discriminating, and the isolation suite is not.</b>
///         There, a foreign subscription is also refused by the ReBAC seam for want of a tuple on
///         <c>resourceGroup:{thatSubscription}-prod</c>, so a test written there passes with the
///         check removed. Here the authorizer is permissive by default, so the only thing that can
///         refuse an unknown subscription is the check itself.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class SubscriptionValidationTests(ResourceManagerCluster cluster) {
    /// <summary>A subscription GUID nobody ever created.</summary>
    static Guid Unknown { get; } = Guid.Parse("deadbeef-0000-4000-8000-000000000001");

    [Fact]
    public async Task ACreateUnderASubscriptionThisTenantDoesNotHaveIs404AndClaimsNothing() {
        ResourceManagerCluster.ResetDoubles();

        var address = new ResourceId(
            ResourceManagerCluster.Tenant,
            Unknown,
            "prod",
            ConformingReconciler.TypeName,
            "unowned-subscription",
            Guid.Empty
        );

        var quota = cluster.Quota(ResourceManagerCluster.Tenant, Unknown);
        var before = (await quota.ListLeasesAsync()).GetValueOrThrow().Count;

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue(
            "a resource was created under a subscription this tenant does not have — the id was "
            + "never validated, and a quota lease and an index entry now exist against it"
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ⚠ Refused at step 1, so nothing below it ran. The name is free and no quota moved — which
        // is what makes the check safe to add rather than a new denial-of-service of its own.
        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Free);

        (await quota.ListLeasesAsync()).GetValueOrThrow().Count.ShouldBe(before);
    }

    [Fact]
    public async Task ReadDeleteAndActionAreRefusedTheSameWay() {
        // One check, in ResolveAsync, which all four verbs go through. Asserted on all four rather
        // than on the write alone, because a check placed in ContinueWriteAsync would have covered
        // only the one — and a GET is the verb an attacker probes with.
        ResourceManagerCluster.ResetDoubles();

        var address = new ResourceId(
            ResourceManagerCluster.Tenant,
            Unknown,
            "prod",
            ConformingReconciler.TypeName,
            "unowned-verbs",
            Guid.Empty
        );

        var request = new WriteRequest {
            Path = address.Path,
            ApiVersion = TestingProvider.V2026,
            Verb = WriteVerb.Post,
            Action = "restart",
            Body = TestingProvider.Body(),
            Caller = ResourceManagerCluster.Caller()
        };

        (await cluster.Manager.ReadAsync(request, TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.Manager.DeleteAsync(request, TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.Manager.ActionAsync(request, TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AnUnknownSubscriptionAnswersByteIdenticallyToAnUnknownResourceName() {
        // ⚠ THE ORACLE THE STATUS CODE ALONE DOES NOT CLOSE. Two 404s whose messages differ are still
        // a way to ask which of the two things was wrong — and "that subscription is not yours" is a
        // more valuable answer than "that resource does not exist", because a subscription id is the
        // billing boundary: knowing one is live is knowing a customer is.
        // docs/plan/07 § The enforcement seam.
        ResourceManagerCluster.ResetDoubles();

        var unknownSubscription = new ResourceId(
            ResourceManagerCluster.Tenant,
            Unknown,
            "prod",
            ConformingReconciler.TypeName,
            "oracle-probe",
            Guid.Empty
        );

        var unknownName = ResourceManagerCluster.Address("oracle-probe-absent");

        var caller = ResourceManagerCluster.Caller();

        var onSubscription = await cluster.Manager.ReadAsync(
            new() { Path = unknownSubscription.Path, ApiVersion = TestingProvider.V2026, Caller = caller },
            TestContext.Current.CancellationToken
        );

        var onName = await cluster.Manager.ReadAsync(
            new() { Path = unknownName.Path, ApiVersion = TestingProvider.V2026, Caller = caller },
            TestContext.Current.CancellationToken
        );

        onSubscription.IsFailure.ShouldBeTrue();
        onName.IsFailure.ShouldBeTrue();

        onSubscription.Error!.Code.ShouldBe(onName.Error!.Code);
        onSubscription.Error.Target.ShouldBe(onName.Error.Target);
        onSubscription.Error.Details.ShouldBeEmpty();

        // The one legitimate difference is the path each caller supplied. Blank it out and the two
        // messages are the same bytes.
        onSubscription.Error.Message.Replace(unknownSubscription.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(
                onName.Error.Message.Replace(unknownName.Path, "PATH", StringComparison.Ordinal),
                "the refusal says which layer refused it"
            );
    }

    [Fact]
    public async Task TheCheckRunsBeforeTheRegistryIsConsulted() {
        // ⚠ THE ORDERING, FOR THE SAME REASON THE TENANT COMPARISON IS FIRST. The registry's refusal
        // NAMES the api-versions the platform serves, which is a description of the platform handed
        // out through an address the caller has not been shown to own. Both ownership checks come
        // before anything that describes anything.
        ResourceManagerCluster.ResetDoubles();

        var address = new ResourceId(
            ResourceManagerCluster.Tenant,
            Unknown,
            "prod",
            ConformingReconciler.TypeName,
            "version-probe",
            Guid.Empty
        );

        var refused = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = "2999-12-31", Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        refused.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "the api-version was validated before the subscription was, so an unknown subscription "
            + "answers with the list of versions the platform serves"
        );

        refused.Error.Message.ShouldNotContain(TestingProvider.V2026);
    }
}
