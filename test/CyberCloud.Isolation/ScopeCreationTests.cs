using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     Scope creation, driven through the real <c>ScopeManagerService</c>, the real
///     <c>ReBacScopeAuthorizer</c>, the real <c>ReBacScopeRelationWriter</c> and the real
///     <c>CyberCloudSchema</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FINDING THIS SUITE EXISTS TO PIN: the inheritance chain
///         docs/plan/07 § Azure RBAC, expressed in it describes stopped one level above where the
///         document says it starts.</b> That table's first row is
///         <c>subscription:S#owner@user:U</c> and its third says inheritance sub → rg → resource is
///         "the <c>From("parent", …)</c> rewrites". Only the last hop of that chain was ever written:
///         <c>ReBacResourceRelationWriter</c> writes <c>resource#parent@resourceGroup</c> and nothing
///         in the platform wrote <c>resourceGroup#parent@subscription</c> or
///         <c>subscription#parent@tenant</c>. So a subscription owner held nothing on the groups or
///         resources inside their own subscription, on a real silo, permanently — and no test said
///         so, because every harness in the repository (including this one's own fixture, in
///         <c>GrantGroupOwnerAsync</c>) writes the resource group's <c>owner</c> tuple <i>directly</i>
///         and never asks the question one level up.
///     </para>
///     <para>
///         <see cref="AnOwnerAtTheTopOfTheChainReachesAResourceAtTheBottom" /> is the assertion that
///         it no longer does, and it is written as one grant and four hops rather than as a tuple
///         read: a tuple read would confirm the edges exist, and what has to be true is that the
///         evaluator <i>follows</i> them.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class ScopeCreationTests(IsolationCluster cluster) {
    /// <summary>A tenant the fixture does not touch, so these tests own its whole scope tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>IsolationCluster.Victim</c>, and the separation is load-bearing.</b> The
    ///     fixture grants <c>resourceGroup:{sub}-prod#owner@user:victor</c> directly, which is exactly
    ///     the shortcut that hid the missing edges — a test of the chain run inside that tenant would
    ///     pass through the shortcut and prove nothing.
    /// </remarks>
    static Guid Chain { get; } = Guid.Parse("66666666-0000-4000-8000-000000000006");

    /// <summary>The only user granted anything in <see cref="Chain" />, and only at the tenant.</summary>
    const string ChainOwner = "tessa";

    /// <summary>A user granted nothing anywhere.</summary>
    const string Nobody = "nemo";

    [Fact]
    public async Task AnOwnerAtTheTopOfTheChainReachesAResourceAtTheBottom() {
        var subscription = Guid.Parse("66666666-0000-4000-8000-0000000000a1");
        const string group = "chain-a";

        await SeedTenantAsync();

        // ── The scope path creates both scopes, over HTTP's shape and through the real engine ────
        var owner = IsolationCluster.Caller(Chain, ChainOwner);

        var madeSubscription = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Subscription(Chain, subscription).Path,
                Body = """{"displayName":"Chain"}""",
                Caller = owner
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ THE GRANT THAT MAKES THIS SUCCEED IS ON THE TENANT AND NOTHING ELSE. `write` on a
        // subscription's parent is `Rel(contributor)` on `tenant`, which `Role(contributor, This |
        // Rel(owner))` gives a tenant owner. If the tenant owner tuple were the only thing missing
        // this would be a 404 rather than a 403 — 404, never 403 — so the message matters.
        madeSubscription.IsSuccess.ShouldBeTrue(
            "a tenant owner could not create a subscription in their own tenant: "
            + madeSubscription.Error?.Message
        );

        madeSubscription.GetValueOrThrow().Created.ShouldBeTrue();

        var madeGroup = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(Chain, subscription, group).Path,
                Body = """{"location":"eu-west-1"}""",
                Caller = owner
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ AND THIS ONE IS THE HOP THAT DID NOT EXIST. `write` on the group's parent is checked on
        // `subscription:{s}`, and the only path from `user:tessa` to it is
        // subscription --parent--> tenant, which the subscription create above wrote. Delete
        // ReBacScopeRelationWriter.LinkToParentAsync and this line is where it shows.
        madeGroup.IsSuccess.ShouldBeTrue(
            "a tenant owner could not create a resource group in a subscription they had just "
            + "created — the subscription's parent edge to the tenant is missing, so "
            + "From(parent, owner) has nothing to follow: " + madeGroup.Error?.Message
        );

        // ── And a resource in it, through the twelve-step write path, read back by the same user ─
        // ⚠ A TOP-LEVEL TARGET, DELIBERATELY. A child's create resolves its parent's index binding
        // first, so a nested target would need an ancestor built in this tenant and would be
        // asserting the parent-resource hop rather than the four scope hops this test is about —
        // ParentEdgeTests already sweeps that.
        var target = IsolationCatalog.Targets[0];

        var address = new ResourceId(Chain, subscription, group, target.Type, "chain-res", Guid.Empty, target.ParentNames);

        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = owner
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ FOUR HOPS: resource → resourceGroup → subscription → tenant, against CheckLimits' depth.
        // The write path checks `write` on the group (a create has no object of its own), so this
        // line alone exercises the two edges the scope path wrote plus the tenant grant.
        created.IsSuccess.ShouldBeTrue(
            "a tenant owner could not create a resource in a group they created in a subscription "
            + "they created. The chain resource → resourceGroup → subscription → tenant is what "
            + "docs/plan/07 § Azure RBAC, expressed in it promises: " + created.Error?.Message
        );

        var read = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = owner },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(
            "the resource is not readable by the tenant owner: " + read.Error?.Message
        );
    }

    [Fact]
    public async Task TheEdgeTheScopePathWritesIsTheOneTheSchemaRewritesThrough() {
        // ⚠ THE SECOND HALF OF THE ASSERTION ABOVE, AND IT IS NOT REDUNDANT. A `parent` tuple aimed
        // at the WRONG object — say a subscription's parent pointing at its own resource group — would
        // still let the test above pass by a different route while every `tenant:#reader` assignment
        // granted nothing. The tuple is therefore read, and its subject compared to the object
        // ReBacScopeAuthorizer would check the parent on.
        var subscription = Guid.Parse("66666666-0000-4000-8000-0000000000a2");
        const string group = "chain-b";

        await SeedTenantAsync();

        var owner = IsolationCluster.Caller(Chain, ChainOwner);

        (await cluster.Scopes.CreateAsync(
            new() { Path = ScopeId.Subscription(Chain, subscription).Path, Body = """{"displayName":"B"}""", Caller = owner },
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        (await cluster.Scopes.CreateAsync(
            new() { Path = ScopeId.Group(Chain, subscription, group).Path, Body = """{"location":"eu-west-1"}""", Caller = owner },
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        var subscriptionParents = await ParentsOfAsync(
            ObjectTypes.Subscription,
            subscription.ToString("N", CultureInfo.InvariantCulture)
        );

        subscriptionParents.Count.ShouldBe(1, "a subscription has exactly one parent scope");
        subscriptionParents[0].Type.ShouldBe(ObjectTypes.Tenant);
        subscriptionParents[0].Id.ShouldBe(Chain.ToString("N", CultureInfo.InvariantCulture));
        subscriptionParents[0].Relation.ShouldBeNullOrEmpty("the parent's subject is an object, not a userset");

        var groupParents = await ParentsOfAsync(
            ObjectTypes.ResourceGroup,
            ReBacScopeAuthorizer.ObjectOf(ScopeId.Group(Chain, subscription, group)).Id
        );

        groupParents.Count.ShouldBe(1, "a resource group has exactly one parent scope");
        groupParents[0].Type.ShouldBe(ObjectTypes.Subscription);
        groupParents[0].Id.ShouldBe(subscription.ToString("N", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AGroupObjectIdIsTheSameStringBothSeamsBuild() {
        // ⚠ TWO ASSEMBLIES' WORTH OF THE SAME STRING, COMPARED RATHER THAN PINNED — the failure class
        // this repository has shipped twice. ReBacResourceAuthorizer.GroupObjectId builds a group's
        // ReBAC id from a ResourceId; ReBacScopeAuthorizer.ObjectOf builds it from a ScopeId. Nothing
        // in the compiler says they agree, and a disagreement would put a resource's parent edge on
        // one object and the group's own role assignments on another — so a group owner could not see
        // the resources in their own group, while every test of either half passed.
        var subscription = Guid.Parse("66666666-0000-4000-8000-0000000000a3");
        const string group = "agreement";

        var target = IsolationCatalog.Targets[0];

        var asResource = ReBacResourceAuthorizer.GroupObjectId(
            new(Chain, subscription, group, target.Type, "x", Guid.Empty, target.ParentNames)
        );

        var asScope = ReBacScopeAuthorizer.ObjectOf(ScopeId.Group(Chain, subscription, group));

        asScope.Type.ShouldBe(ReBacResourceAuthorizer.ResourceGroupObjectType);
        asScope.Id.ShouldBe(asResource);

        // The same comparison for the subscription, whose id a soft delete's re-parent also builds.
        var subscriptionAsResource = ReBacResourceAuthorizer.SubscriptionObjectId(
            new(Chain, subscription, group, target.Type, "x", Guid.Empty, target.ParentNames)
        );

        ReBacScopeAuthorizer.ObjectOf(ScopeId.Subscription(Chain, subscription))
            .Id.ShouldBe(subscriptionAsResource);

        await Task.CompletedTask;
    }

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACallerWithNoGrantCannotCreateASubscriptionAndIsToldNothingExists() {
        await SeedTenantAsync();

        var refused = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Subscription(Chain, Guid.Parse("66666666-0000-4000-8000-0000000000b1")).Path,
                Body = """{"displayName":"Sneaky"}""",
                Caller = IsolationCluster.Caller(Chain, Nobody)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a caller with no tenant grant created a subscription");

        // ⚠ 404 AND NOT 403 — docs/plan/07 § The enforcement seam. A 403 would confirm that the
        // tenant is real, which for a caller holding a token for it is not news; but the same helper
        // answers for a scope in ANOTHER tenant, and there the confirmation is the oracle. One
        // answer, so the two cannot be told apart.
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task OneTenantCannotCreateAResourceGroupInsideAnother() {
        // ⚠ THE FAILURE THIS SUITE EXISTS FOR, ASKED OF THE NEW ROUTE. The scope path is reached
        // through a gateway that has already resolved the caller's tenant, so a request naming
        // another tenant's subscription is a cross-tenant write attempt — and the manager's own
        // Resolve compares the path's tenant to the caller's before anything else, which is the
        // second of the two defences.
        var refused = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(IsolationCluster.Victim, IsolationCluster.VictimSubscription, "stolen").Path,
                Body = """{"location":"eu-west-1"}""",
                Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("the attacker created a resource group in the victim's subscription");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // And nothing was written: the victim's subscription still lists only the fixture's group.
        var groups = await cluster.For(IsolationCluster.Victim)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription))
            .ListResourceGroupsAsync();

        groups.GetValueOrThrow().ShouldNotContain("stolen");
    }

    [Fact]
    public async Task AGroupInASubscriptionThatDoesNotExistIsTheCanonicalNotFound() {
        // ⚠ The same question step 1 of the resource write path asks, and it must answer the same
        // way: a subscription is exactly as enumerable as a resource name and leaks more, because it
        // is the billing boundary.
        await SeedTenantAsync();

        var refused = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(Chain, Guid.Parse("66666666-0000-4000-8000-0000000000c1"), "orphan").Path,
                Body = """{"location":"eu-west-1"}""",
                Caller = IsolationCluster.Caller(Chain, ChainOwner)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task ATenantIsNotCreatableThroughTheScopePath() {
        // ⚠ THE DECISION, ASSERTED. IScopeManager.CreateTenantAsync carries the argument: stage 3 of
        // the gateway reads the tenant out of the '/tenants/{id}' prefix and refuses any request
        // whose token names a different one, so a tenant-create ROUTE cannot exist without breaching
        // the one boundary an HTTP request has. This refusal is what stops the route being added by
        // somebody who only reads the router.
        await SeedTenantAsync();

        var refused = await cluster.Scopes.CreateAsync(
            new() {
                Path = ScopeId.Tenant(Chain).Path,
                Body = "{}",
                Caller = IsolationCluster.Caller(Chain, ChainOwner)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a tenant was created through the scope path");

        // ⚠ 400 and not the canonical 404, deliberately: the caller holds a token for this very
        // tenant, so its existence is not news to them, and a 404 would send them looking for a
        // missing tenant instead of at the door they should be using.
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public async Task CreatingATenantWithoutTheOperatorGrantIsRefused() {
        // ⚠ THE ONLY CALLER OF platform:root#operator IN THE PLATFORM, AND THE FIRST ONE EVER.
        // docs/plan/06 § Platform administration names the relation, CyberCloudSchema has defined it
        // and `administer` over it since it was written, and nothing checked either. A permission the
        // schema does not follow can only evaluate false — which is how `purge` came to answer "does
        // not exist" to everybody, permanently — so this asserts the refusal is a REFUSAL and not an
        // unanswerable check.
        var refused = await cluster.Scopes.CreateTenantAsync(
            new() {
                TenantId = Guid.Parse("77777777-0000-4000-8000-000000000007"),
                Slug = "not-allowed",
                DisplayName = "Not Allowed",
                HomeRegion = "eu-west-1",
                OwnerSubjectId = ChainOwner
            },
            IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a caller with no platform:root#operator grant created a tenant");

        // ⚠ 403 and NOT the canonical 404, which is the one place the scope path departs from
        // "404, never 403". platform:root is a documented singleton, so there is nothing to leak, and
        // answering "that does not exist" to an operator whose grant lapsed sends them looking for a
        // missing tenant instead of at their own permissions.
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public async Task ATenantCreatedWithNoOwnerIsRefusedBeforeAnythingIsWritten() {
        // ⚠ `tenant` is the one type CyberCloudSchema gives no `parent` relation, so nothing above it
        // can grant on it and a tenant with no direct '#owner' tuple is permanently invisible to
        // everyone. Refusing here rather than defaulting the owner to the operator is what stops
        // platform staff becoming the standing owner of every customer tenant.
        var refused = await cluster.Scopes.CreateTenantAsync(
            new() {
                TenantId = Guid.Parse("77777777-0000-4000-8000-000000000008"),
                Slug = "ownerless",
                DisplayName = "Ownerless",
                HomeRegion = "eu-west-1"
            },
            IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();

        // ⚠ Before the platform check, and the order is the point: the request is malformed whoever
        // sends it, and reporting "you are not an operator" to somebody who is one would send them
        // to fix the wrong thing.
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates <see cref="Chain" />'s tenant record and grants <see cref="ChainOwner" /> owner on
    ///     it. Idempotent.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tenant grain is created directly and the directory is not touched, which is the
    ///     honest boundary of this suite.</b> <c>IScopeManager.CreateTenantAsync</c> also assigns a
    ///     shard and registers a directory entry, and those are what make a tenant <i>reachable</i>
    ///     from a gateway rather than what make its scopes work. Driving them needs the shard map and
    ///     the directory, which this cluster does not configure — so what is owed, and is said here
    ///     rather than left to be discovered, is that <b>no test drives CreateTenantAsync's happy
    ///     path end to end</b>. Its two refusals are covered above; its ordering is not.
    /// </remarks>
    async Task SeedTenantAsync() {
        var created = await cluster.For(Chain)
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(Chain))
            .CreateAsync("chain-tenant", "Chain Tenant", "eu-west-1");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        await cluster.GrantTenantOwnerAsync(Chain, ChainOwner);
    }

    async Task<IReadOnlyList<SubjectRef>> ParentsOfAsync(string type, string id) {
        var snapshot = await cluster.For(Chain)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(type, id))
            .ReadDurableAsync();

        return snapshot.IsSuccess
            && snapshot.GetValueOrThrow().ByRelation.TryGetValue(Relations.Parent, out var parents)
                ? parents
                : [];
    }
}
