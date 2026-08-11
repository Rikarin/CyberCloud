using CyberCloud.ResourceManager.Grains;
using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The SignalR connection grain — docs/plan/10 § SignalR, per-subscribe authorization re-checked
///     on relation changes, <b>driven through a real silo</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The "through a real silo" is the point of this file existing at all.</b>
///         <c>IConnectionGrain</c> and its implementation shipped in <c>CyberCloud.Gateway.Host</c>,
///         which docs/plan/03 § Hosts and docs/plan/10 § Shape make an Orleans <b>client</b>. A client
///         activates no grains, so no silo could load the type; the arrangement held together only
///         because the tests constructed the class with <c>new</c>, which in turn required the grain to
///         use no grain infrastructure — a constraint on future edits that the source recorded as if it
///         were a design.
///     </para>
///     <para>
///         Every test below reaches the grain the way <c>InterestHub</c> does: through
///         <c>IGrainFactory.ForTenant(tenant).GetGrain&lt;IConnectionGrain&gt;(conn/{id})</c>. So they
///         exercise activation, the key decode, the tenant qualification and the Orleans call path,
///         none of which had ever run.
///     </para>
///     <para>
///         ⚠ <b>No storage, and the tests prove that by killing the activation.</b> docs/plan/05 § Hot
///         puts the interest set in the class that "dies with the connection", and this grain is absent
///         from <c>durable-grains.txt</c> — satisfying the Storage tier gate by absence rather than by
///         exemption. <see cref="AnInterestSetDoesNotSurviveTheActivationBecauseItMustNot" /> is what
///         would fail the day somebody adds a <c>[PersistentState]</c> to "fix" a reconnect.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ConnectionGrainTests(ResourceManagerCluster cluster) {
    static readonly string TheResource = ResourceManagerCluster.Address("watched").Path;
    static readonly string AnotherResource = ResourceManagerCluster.Address("watched-too").Path;
    static readonly string AThirdResource = ResourceManagerCluster.Address("watched-thrice").Path;

    static CallerContext Caller(Guid? tenant = null) =>
        ResourceManagerCluster.Caller(tenant, "alice");

    IConnectionGrain Connection(string id) => cluster.Connection(ResourceManagerCluster.Tenant, id);

    [Fact]
    public async Task ConnectingAuthorizesNothing() {
        ResourceManagerCluster.ResetDoubles();
        var grain = Connection("connects-only");

        (await grain.AttachAsync(Caller(), "resources")).IsSuccess.ShouldBeTrue();

        // ⚠ docs/plan/10 § SignalR: "Subscription authorization is per-subscribe, not per-connect."
        ScriptedInterestAuthorizer.Asked.ShouldBe(0);
        (await grain.InterestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SubscribingAsksTheSeamAndAcceptsWhatItAllows() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);

        var grain = Connection("subscribes");
        await grain.AttachAsync(Caller(), "resources");

        var subscribed = await grain.SubscribeAsync(new("resources", TheResource));

        subscribed.IsSuccess.ShouldBeTrue(subscribed.Error?.Message);
        ScriptedInterestAuthorizer.Asked.ShouldBe(1);
        (await grain.InterestsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SubscribingToSomethingUnreadableIsRefusedWithTheSeamsOwn404() {
        ResourceManagerCluster.ResetDoubles();

        var grain = Connection("refused");
        await grain.AttachAsync(Caller(), "resources");

        var subscribed = await grain.SubscribeAsync(new("resources", TheResource));

        subscribed.IsFailure.ShouldBeTrue();
        subscribed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await grain.InterestsAsync()).ShouldBeEmpty();
        ScriptedInterestAuthorizer.Asked.ShouldBe(1);
    }

    /// <summary>
    ///     THE revoke path. Access is granted, subscribed, then taken away — and the interest goes.
    /// </summary>
    /// <remarks>
    ///     ⚠ docs/plan/10 § SignalR: <i>"A user who loses access to a resource group must stop
    ///     receiving its events — otherwise the live-update channel is an authorization bypass with a
    ///     nice UI."</i> The REST API would keep answering <c>404</c> correctly the whole time, which
    ///     is what makes the bypass invisible. In production the tenant's relation-version stream calls
    ///     <c>RecheckAsync</c>; that bridge is owed, and it is <i>buildable</i> now only because the
    ///     grain runs in a silo — a type declared in an Orleans client can subscribe to nothing.
    /// </remarks>
    [Fact]
    public async Task AUserWhoLosesAccessStopsReceivingEvents() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);
        ScriptedInterestAuthorizer.Grant(AnotherResource);

        var grain = Connection("revoked");
        await grain.AttachAsync(Caller(), "resources");
        await grain.SubscribeAsync(new("resources", TheResource));
        await grain.SubscribeAsync(new("resources", AnotherResource));

        (await grain.InterestsAsync()).Length.ShouldBe(2);

        ScriptedInterestAuthorizer.Revoke(TheResource);

        (await grain.RecheckAsync()).ShouldBe(1);

        var remaining = await grain.InterestsAsync();
        remaining.ShouldHaveSingleItem();
        remaining[0].ResourcePath.ShouldBe(AnotherResource);
    }

    [Fact]
    public async Task ARecheckThatChangesNothingDropsNothing() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);

        var grain = Connection("unchanged");
        await grain.AttachAsync(Caller(), "resources");
        await grain.SubscribeAsync(new("resources", TheResource));

        (await grain.RecheckAsync()).ShouldBe(0);
        (await grain.InterestsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SubscribingBeforeAttachingIsRefused() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);

        var grain = Connection("unattached");
        var subscribed = await grain.SubscribeAsync(new("resources", TheResource));

        subscribed.IsFailure.ShouldBeTrue();

        // ⚠ And the seam was never asked, because there is no caller to ask about.
        ScriptedInterestAuthorizer.Asked.ShouldBe(0);
    }

    [Fact]
    public async Task AnInterestSetIsCappedByTheConcurrencyLimit() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);
        ScriptedInterestAuthorizer.Grant(AnotherResource);
        ScriptedInterestAuthorizer.Grant(AThirdResource);

        var grain = Connection("capped");
        await grain.AttachAsync(Caller(), "resources");

        // The suite's silo configures ConnectionLimits.StreamsPerConnection = 2.
        (await grain.SubscribeAsync(new("resources", TheResource))).IsSuccess.ShouldBeTrue();
        (await grain.SubscribeAsync(new("resources", AnotherResource))).IsSuccess.ShouldBeTrue();

        var third = await grain.SubscribeAsync(new("resources", AThirdResource));

        third.IsFailure.ShouldBeTrue();
        third.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);

        // ⚠ Re-subscribing to one it already holds is not a new stream and is not refused.
        (await grain.SubscribeAsync(new("resources", TheResource))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UnsubscribingIsIdempotent() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);

        var grain = Connection("unsubscribes");
        await grain.AttachAsync(Caller(), "resources");
        await grain.SubscribeAsync(new("resources", TheResource));

        (await grain.UnsubscribeAsync(new("resources", TheResource))).IsSuccess.ShouldBeTrue();
        (await grain.UnsubscribeAsync(new("resources", TheResource))).IsSuccess.ShouldBeTrue();
        (await grain.InterestsAsync()).ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>The interest set does not survive the activation, and that is the tier decision.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/05 § Hot: the connection grain "dies with the connection". There is nothing to
    ///     rebuild — the socket is gone, and a reconnect is a new connection id and therefore a new
    ///     grain. A <c>[PersistentState]</c> added here to "survive a reconnect" would be a durable
    ///     record of a socket that no longer exists, and it would also put this type in
    ///     <c>durable-grains.txt</c>, which is reviewed like a schema migration. This test is what
    ///     fails first.
    /// </remarks>
    [Fact]
    public async Task AnInterestSetDoesNotSurviveTheActivationBecauseItMustNot() {
        ResourceManagerCluster.ResetDoubles();
        ScriptedInterestAuthorizer.Grant(TheResource);

        var grain = Connection("ephemeral");
        await grain.AttachAsync(Caller(), "resources");
        await grain.SubscribeAsync(new("resources", TheResource));
        (await grain.InterestsAsync()).ShouldHaveSingleItem();

        await grain.DeactivateAsync();

        (await grain.InterestsAsync()).ShouldBeEmpty("nothing is persisted, and nothing should be");

        // And the fresh activation has no caller either, so it authorizes nothing until re-attached.
        (await grain.SubscribeAsync(new("resources", TheResource))).IsFailure.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>The tenant in the key wins over the tenant in the caller.</b>
    /// </summary>
    /// <remarks>
    ///     The gateway reaches this grain through <c>ForTenant(token tenant)</c> and builds the
    ///     <see cref="CallerContext" /> from the same token, so the two can only disagree if something
    ///     composed one of them from somewhere else — a header, a path, a body. docs/plan/00 § The
    ///     tenant-separation row, corrected: the gateway is a client, so <c>Orleans.Multitenant</c>'s
    ///     filter never runs and the tenant in the key is the only boundary there is. Refusing the
    ///     mismatch means the grain is not a second place where a tenant can be established.
    /// </remarks>
    [Fact]
    public async Task ACallerFromAnotherTenantCannotAttachToThisTenantsConnection() {
        ResourceManagerCluster.ResetDoubles();

        var grain = Connection("wrong-tenant");
        var attached = await grain.AttachAsync(Caller(ResourceManagerCluster.OtherTenant), "resources");

        attached.IsFailure.ShouldBeTrue();
        attached.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    /// <summary>
    ///     ⚠ <b>A grain reached without <c>ForTenant</c> fails on activation.</b> ADR-002 puts the
    ///     tenant in the key; an activation with no tenant qualification is one no caller can be
    ///     attributed to, and serving it would be serving a connection belonging to nobody.
    /// </summary>
    [Fact]
    public async Task AnUnqualifiedActivationIsRefused() {
        var unqualified = cluster.Grains.GetGrain<IConnectionGrain>(
            ConnectionGrainKeys.Connection("no-tenant")
        );

        await Should.ThrowAsync<Exception>(async () => await unqualified.InterestsAsync());
    }
}
