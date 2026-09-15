using CyberCloud.Authorization.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <c>ReBacScopeAuthorizer.ListReadableAsync</c> — the one <c>ListObjects</c> a scope
///     collection page asks for, and what the seam does with every way the engine can decline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The real seam over a scripted engine.</b> <see cref="ScriptedListObjectsGrain" />
///         is the only <c>IListObjectsGrain</c> this silo hosts, so what is under test is the
///         question the seam asks — which object type, within what, at what depth — and how it maps
///         the answer, never whether the walk itself is right. The walk is
///         <c>CyberCloud.Authorization.Tests</c>'; the two together, against
///         <c>CyberCloudSchema</c>, are <c>test/CyberCloud.Isolation</c>'s.
///     </para>
///     <para>
///         ⚠ <b>The cap case is the reason this class exists.</b> The real engine cannot be made to
///         hit <c>AuthorizationLimits.MaxListObjects</c> on demand in a unit test, and a seam that
///         turned that outcome into "nothing readable" would serve an empty page for a tenant whose
///         engine is busy — indistinguishable from a tenant with no subscriptions, and the one
///         answer a listing may never fake.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ReBacScopeAuthorizerTests(ResourceManagerCluster cluster) {
    static Guid Tenant => ResourceManagerCluster.Tenant;

    static readonly string[] GroupNames = ["dev", "prod"];

    ReBacScopeAuthorizer Authorizer => new(cluster.Grains, NullLogger<ReBacScopeAuthorizer>.Instance);

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The walk is scoped to the parent at depth 1, on the members' own object type, and
    ///         the answer is the candidates the walk named — matched on the ids the check side
    ///         spells.
    ///     </b>
    /// </summary>
    [Fact]
    public async Task ListReadableAsksWithinTheParent() {
        ResourceManagerCluster.ResetDoubles();

        var subscriptions = Enumerable.Range(0, 3).Select(_ => ScopeId.Subscription(Tenant, Guid.NewGuid())).ToList();

        // Two of the three, and one id no candidate carries — which must not appear either.
        ScriptedListObjectsGrain.Objects.Add(N(subscriptions[0].SubscriptionId));
        ScriptedListObjectsGrain.Objects.Add(N(subscriptions[2].SubscriptionId));
        ScriptedListObjectsGrain.Objects.Add(N(Guid.NewGuid()));

        var visibility = await Authorizer.ListReadableAsync(
            ScopeId.Tenant(Tenant),
            subscriptions,
            Permissions.Read,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        visibility.IsAnswered.ShouldBeTrue();
        visibility.Visible.ShouldBe([subscriptions[0], subscriptions[2]], ignoreOrder: true);

        var asked = ScriptedListObjectsGrain.Requests.ShouldHaveSingleItem();

        asked.ObjectType.ShouldBe(ReBacScopeAuthorizer.SubscriptionObjectType);
        asked.Permission.ShouldBe(Permissions.Read);
        asked.WithinDepth.ShouldBe(1);
        asked.PageSize.ShouldBe(ListObjectsRequest.MaxPageSize, "the walk is asked at its cap and paged to the end");

        // ⚠ Within is the parent as ReBacScopeAuthorizer.ObjectOf spells it — the same string the
        // tuple store keys and the check side asks on. A second spelling here would scope the walk
        // to an object no tuple names, which lists nothing while every check passes.
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Tenant(Tenant));
        asked.Within.ShouldNotBeNull();
        asked.Within.Type.ShouldBe(type);
        asked.Within.Id.ShouldBe(id);
    }

    /// <summary>
    ///     ⚠ <b>A resource-group page walks <c>resourceGroup</c> objects under the subscription,
    ///     and matches on <c>{subscriptionId:N}-{name}</c>.</b>
    /// </summary>
    [Fact]
    public async Task ListReadableUnderASubscriptionAsksForResourceGroups() {
        ResourceManagerCluster.ResetDoubles();

        var subscription = ScopeId.Subscription(Tenant, Guid.NewGuid());
        var groups = GroupNames.Select(x => ScopeId.Group(Tenant, subscription.SubscriptionId, x)).ToList();

        ScriptedListObjectsGrain.Objects.Add(ReBacScopeAuthorizer.ObjectOf(groups[1]).Id);

        var visibility = await Authorizer.ListReadableAsync(
            subscription,
            groups,
            Permissions.Read,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        visibility.IsAnswered.ShouldBeTrue();
        visibility.Visible.ShouldBe([groups[1]]);

        var asked = ScriptedListObjectsGrain.Requests.ShouldHaveSingleItem();

        asked.ObjectType.ShouldBe(ReBacScopeAuthorizer.ResourceGroupObjectType);
        asked.Within!.Type.ShouldBe(ReBacScopeAuthorizer.SubscriptionObjectType);
        asked.Within.Id.ShouldBe(N(subscription.SubscriptionId));
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         A cap, a depth cap and an outright failure are all "not answered" — never "nothing
    ///         readable".
    ///     </b>
    /// </summary>
    [Theory]
    [InlineData(ListObjectsOutcome.ObjectCapExceeded, null)]
    [InlineData(ListObjectsOutcome.DepthCapExceeded, null)]
    [InlineData(ListObjectsOutcome.Complete, "the store is down")]
    public async Task TheCapMapsToUnanswered(ListObjectsOutcome outcome, string? failure) {
        ResourceManagerCluster.ResetDoubles();

        var subscription = ScopeId.Subscription(Tenant, Guid.NewGuid());

        // The engine names the candidate — a seam that read the objects off a capped page would
        // have something to show, and must not.
        ScriptedListObjectsGrain.Objects.Add(N(subscription.SubscriptionId));
        ScriptedListObjectsGrain.Outcome = outcome;
        ScriptedListObjectsGrain.FailWith = failure;

        var visibility = await Authorizer.ListReadableAsync(
            ScopeId.Tenant(Tenant),
            [subscription],
            Permissions.Read,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        visibility.IsAnswered.ShouldBeFalse(
            "the engine declined and the seam answered anyway — an empty page for a busy engine is "
            + "indistinguishable from an empty tenant"
        );

        visibility.ShouldBeSameAs(ScopeCollectionVisibility.Unanswered);
    }

    /// <summary>
    ///     ⚠ <b>A parent that has no scope children is not answered either</b> — and no grain is asked.
    /// </summary>
    [Fact]
    public async Task AResourceGroupParentIsUnansweredWithoutAWalk() {
        ResourceManagerCluster.ResetDoubles();

        var visibility = await Authorizer.ListReadableAsync(
            ScopeId.Group(Tenant, Guid.NewGuid(), "prod"),
            [],
            Permissions.Read,
            ResourceManagerCluster.Caller(),
            TestContext.Current.CancellationToken
        );

        visibility.IsAnswered.ShouldBeFalse();
        ScriptedListObjectsGrain.Requests.ShouldBeEmpty();
    }

    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);
}
