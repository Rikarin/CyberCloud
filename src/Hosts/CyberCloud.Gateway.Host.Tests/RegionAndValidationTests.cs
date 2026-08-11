using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>docs/plan/10 § Request pipeline, stage 4 — one hop, never two.</summary>
public sealed class RegionRoutingTests {
    [Fact]
    public void TheHomeRegionIsServedLocally() {
        RegionRouting.Decide("eu-central", "eu-central", "").Action.ShouldBe(RegionAction.Serve);
        RegionRouting.Decide("EU-Central", "eu-central", "").Action.ShouldBe(RegionAction.Serve);
    }

    [Fact]
    public void AnUnplacedTenantIsServedLocallyRatherThanRefused() {
        // ⚠ An empty home region means "not placed yet". Refusing would make tenant creation depend
        // on a field that is written after it.
        RegionRouting.Decide("", "eu-central", "").Action.ShouldBe(RegionAction.Serve);
    }

    [Fact]
    public void AForeignRegionIsProxiedOnce() {
        RegionRouting.Decide("us-east", "eu-central", "").Action.ShouldBe(RegionAction.Proxy);
    }

    /// <summary>
    ///     ⚠ The loop guard. Two regions with disagreeing directory snapshots each conclude the other
    ///     is home; with no hop counter a request bounces until something times out, and the symptom
    ///     is latency rather than an error.
    /// </summary>
    [Fact]
    public void ASecondHopIsRefusedRatherThanTaken() {
        RegionRouting.Decide("us-east", "eu-central", "ap-south")
            .Action
            .ShouldBe(RegionAction.RefuseSecondHop);
    }

    [Fact]
    public async Task AnAlreadyForwardedRequestInTheWrongRegionIs502NamingBothRegions() {
        var gateway = new GatewayHarness("eu-central", "us-east");

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (GatewayHeaders.ForwardedByRegion, "ap-south")
        );

        response.Status.ShouldBe(StatusCodes.Status502BadGateway);
        response.Body.ShouldContain("us-east");
        response.Body.ShouldContain("eu-central");
        response.Body.ShouldContain("one hop, never two");
    }

    [Fact]
    public async Task WithNoProxyConfiguredAForeignTenantIsRefusedRatherThanServedLocally() {
        var gateway = new GatewayHarness("eu-central", "us-east");

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status500InternalServerError);
        response.Body.ShouldContain("us-east");

        // ⚠ And nothing was dispatched. Serving another region's tenant from here would read its
        // grains across the boundary the placement exists to avoid, silently.
        gateway.Manager.Paths.ShouldBeEmpty();
    }
}

/// <summary>Stage 7 — what the gateway can decide about a body without a grain call.</summary>
public sealed class ValidationTests {
    [Fact]
    public async Task AMalformedBodyIs400WithNoStackTrace() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"properties\":"
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("InvalidRequestBody");
        response.Body.ShouldNotContain("   at ");
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ABodyThatIsNotAnObjectIs400() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "[1,2,3]"
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task APutWithNoBodyIs400() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("full replacement");
    }

    [Fact]
    public async Task AnActionWithNoBodyIsFine() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/restart",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task ASuspendedTenantKeepsItsReadsAndLosesItsWrites() {
        var gateway = new GatewayHarness(status: TenantStatus.Suspended);
        var token = gateway.Token(GatewayHarness.TenantA);

        (await gateway.SendAsync("GET", GatewayHarness.ResourcePath(GatewayHarness.TenantA), token))
            .Status
            .ShouldBe(StatusCodes.Status200OK);

        var write = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            token,
            body: "{\"properties\":{}}"
        );

        write.Status.ShouldBe(StatusCodes.Status403Forbidden);
        write.Body.ShouldContain("TenantSuspended");
    }
}
