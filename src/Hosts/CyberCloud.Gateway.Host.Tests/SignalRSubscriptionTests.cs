using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway still owns of docs/plan/10 § SignalR: the four hub names and the handshake's
///     trip through the pipeline.
/// </summary>
/// <remarks>
///     ⚠ <b>The interest-set behaviour is no longer tested here, because it is no longer implemented
///     here.</b> <c>IConnectionGrain</c> and <c>ConnectionGrain</c> moved to
///     <c>CyberCloud.ResourceManager(.Contracts)</c> — a grain needs a silo to activate it and this
///     host is an Orleans client, so the type that shipped here could not be loaded by any silo and
///     was kept working only by the tests constructing it with <c>new</c>. Per-subscribe
///     authorization, the revoke re-check and the interest cap are asserted against a real
///     <c>TestCluster</c> in <c>CyberCloud.ResourceManager.Tests.ConnectionGrainTests</c>, which is
///     the first time any of them has run through grain infrastructure.
/// </remarks>
public sealed class SignalRSubscriptionTests {
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

    /// <summary>
    ///     ⚠ The connection id is a key segment, and a <c>/</c> in it would be a second segment.
    /// </summary>
    /// <remarks>
    ///     SignalR generates the id, so this cannot come from a caller — which is exactly why it
    ///     throws rather than escaping: reaching it means our own code built the key from the wrong
    ///     value, and docs/plan/00 § Coding standards puts that in the "page someone" class.
    /// </remarks>
    [Fact]
    public void TheConnectionKeyRefusesAnIdThatWouldAddASegment() {
        ConnectionGrainKeys.Connection("abc123").ShouldBe("conn/abc123");

        Should.Throw<ArgumentException>(() => ConnectionGrainKeys.Connection("abc/123"));
        Should.Throw<ArgumentException>(() => ConnectionGrainKeys.Connection(""));
    }
}
