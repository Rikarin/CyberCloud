using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     docs/plan/10 § SignalR — per-subscribe authorization, re-checked on relation changes.
/// </summary>
/// <remarks>
///     ⚠ <b>The revoke test is the one that matters.</b> A per-connect check passes at 09:00 and is
///     still delivering a resource group's events at 17:00 to somebody removed at 09:05, while the
///     REST API correctly answers <c>404</c> the whole time. That is an authorization bypass with a
///     nice UI, and nothing except <c>RecheckAsync</c> closes it.
/// </remarks>
public sealed class SignalRSubscriptionTests {
    static readonly string TheResource = GatewayHarness.ResourcePath(GatewayHarness.TenantA);
    static readonly string AnotherResource = GatewayHarness.ResourcePath(GatewayHarness.TenantA, "other");

    static (ConnectionGrain Grain, ScriptedInterestAuthorizer Seam) Connection(
        IConcurrencyLimiter? limiter = null
    ) {
        var seam = new ScriptedInterestAuthorizer();

        var grain = new ConnectionGrain(
            seam,
            limiter ?? new ProcessConcurrencyLimiter(new()),
            NullLogger<ConnectionGrain>.Instance
        );

        return (grain, seam);
    }

    static CallerContext Caller() =>
        new() { TenantId = GatewayHarness.TenantA, SubjectType = "user", SubjectId = "user-1" };

    [Fact]
    public async Task ConnectingAuthorizesNothing() {
        var (grain, seam) = Connection();

        await grain.AttachAsync(Caller(), HubNames.Resources);

        // ⚠ docs/plan/10 § SignalR: "Subscription authorization is per-subscribe, not per-connect."
        seam.Asked.ShouldBe(0);
        (await grain.InterestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SubscribingAsksTheSeamAndAcceptsWhatItAllows() {
        var (grain, seam) = Connection();
        seam.Readable.Add(TheResource);

        await grain.AttachAsync(Caller(), HubNames.Resources);

        var subscribed = await grain.SubscribeAsync(new(HubNames.Resources, TheResource));

        subscribed.IsSuccess.ShouldBeTrue();
        seam.Asked.ShouldBe(1);
        (await grain.InterestsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SubscribingToSomethingUnreadableIsRefusedWithTheSeamsOwn404() {
        var (grain, seam) = Connection();

        await grain.AttachAsync(Caller(), HubNames.Resources);

        var subscribed = await grain.SubscribeAsync(new(HubNames.Resources, TheResource));

        subscribed.IsFailure.ShouldBeTrue();
        subscribed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await grain.InterestsAsync()).ShouldBeEmpty();
        seam.Asked.ShouldBe(1);
    }

    /// <summary>
    ///     THE revoke path. Access is granted, subscribed, then taken away — and the interest goes.
    /// </summary>
    [Fact]
    public async Task AUserWhoLosesAccessStopsReceivingEvents() {
        var (grain, seam) = Connection();
        seam.Readable.Add(TheResource);
        seam.Readable.Add(AnotherResource);

        await grain.AttachAsync(Caller(), HubNames.Resources);
        await grain.SubscribeAsync(new(HubNames.Resources, TheResource));
        await grain.SubscribeAsync(new(HubNames.Resources, AnotherResource));

        (await grain.InterestsAsync()).Length.ShouldBe(2);

        // The relation change: one grant is revoked. In production the tenant's relation-version
        // stream calls RecheckAsync; here the test calls it, which is the same entry point.
        seam.Readable.Remove(TheResource);

        (await grain.RecheckAsync()).ShouldBe(1);

        var remaining = await grain.InterestsAsync();
        remaining.ShouldHaveSingleItem();
        remaining[0].ResourcePath.ShouldBe(AnotherResource);
    }

    [Fact]
    public async Task ARecheckThatChangesNothingDropsNothing() {
        var (grain, seam) = Connection();
        seam.Readable.Add(TheResource);

        await grain.AttachAsync(Caller(), HubNames.Resources);
        await grain.SubscribeAsync(new(HubNames.Resources, TheResource));

        (await grain.RecheckAsync()).ShouldBe(0);
        (await grain.InterestsAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SubscribingBeforeAttachingIsRefused() {
        var (grain, seam) = Connection();

        var subscribed = await grain.SubscribeAsync(new(HubNames.Resources, TheResource));

        subscribed.IsFailure.ShouldBeTrue();
        // ⚠ And the seam was never asked, because there is no caller to ask about.
        seam.Asked.ShouldBe(0);
    }

    [Fact]
    public async Task AnInterestSetIsCappedByTheConcurrencyLimit() {
        var (grain, seam) = Connection(new ProcessConcurrencyLimiter(new() { StreamsPerConnection = 1 }));
        seam.Readable.Add(TheResource);
        seam.Readable.Add(AnotherResource);

        await grain.AttachAsync(Caller(), HubNames.Resources);

        (await grain.SubscribeAsync(new(HubNames.Resources, TheResource))).IsSuccess.ShouldBeTrue();

        var second = await grain.SubscribeAsync(new(HubNames.Resources, AnotherResource));
        second.IsFailure.ShouldBeTrue();
        second.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
    }

    [Fact]
    public async Task UnsubscribingIsIdempotent() {
        var (grain, seam) = Connection();
        seam.Readable.Add(TheResource);

        await grain.AttachAsync(Caller(), HubNames.Resources);
        await grain.SubscribeAsync(new(HubNames.Resources, TheResource));

        (await grain.UnsubscribeAsync(new(HubNames.Resources, TheResource))).IsSuccess.ShouldBeTrue();
        (await grain.UnsubscribeAsync(new(HubNames.Resources, TheResource))).IsSuccess.ShouldBeTrue();
        (await grain.InterestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void TheFourHubsAreTheFourTheDocumentGives() {
        HubNames.IsKnown(HubNames.Resources).ShouldBeTrue();
        HubNames.IsKnown(HubNames.Operations).ShouldBeTrue();
        HubNames.IsKnown(HubNames.Terminal).ShouldBeTrue();
        HubNames.IsKnown(HubNames.Metrics).ShouldBeTrue();
        HubNames.IsKnown("anything-else").ShouldBeFalse();
    }

    [Fact]
    public async Task AHubHandshakeGoesThroughTheTenantCheckLikeAnyOtherRequest() {
        var gateway = new GatewayHarness();

        var anonymous = await gateway.SendAsync("POST", "/hubs/resources/negotiate", null);
        anonymous.Status.ShouldBe(StatusCodes.Status401Unauthorized);

        var unknown = await gateway.SendAsync("GET", "/hubs/nope", gateway.Token(GatewayHarness.TenantA));
        unknown.Status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
