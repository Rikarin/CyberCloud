using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Kubernetes.Contracts.Tunnel;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     How the pipeline treats an agent dialling in — docs/plan/09 § Cluster connections, the
///     <c>AgentInitiated</c> row, and <c>RouteKind.AgentTunnel</c>'s remarks.
/// </summary>
/// <remarks>
///     The endpoint itself — the admission, the upgrade, the relay — is <c>AgentTunnelEndpoint</c>
///     over <c>AgentTunnelRelay</c>, and the relay is what <c>AgentTunnelGrainTests</c> drives with a
///     real agent. What this suite pins is narrower: that the request reaches the endpoint with no
///     identity-host token and no api-version, that it does so through every stage rather than
///     around them, and that nothing else under the exempted prefix is served.
/// </remarks>
public sealed class AgentTunnelRoutingTests {
    [Fact]
    public async Task TheTunnelPathLeavesThePipelineForTheEndpointWithNoTokenAndNoApiVersion() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", TunnelCodec.TunnelPath, null, "");

        // No outcome was written: the pipeline handed the request on, as it does for a hub. The
        // harness has no endpoint behind it, so the status is the default's untouched 200 and the
        // body is empty — a 401 or a 400 here would mean a stage stopped it.
        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldBeEmpty();
        response.Trace.ShouldContain("Correlation");
        response.Trace.ShouldContain("RateLimit");
        response.Trace.ShouldContain("Dispatch");
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public void TheRouteIsExactlyTheTunnelPathOnGet() {
        GatewayRouter.Resolve(TunnelCodec.TunnelPath, "GET", Guid.Empty)
            .GetValueOrThrow()
            .Kind.ShouldBe(RouteKind.AgentTunnel);
        GatewayRouter.Resolve(TunnelCodec.TunnelPath, "POST", Guid.Empty).IsFailure.ShouldBeTrue();
        GatewayRouter.Resolve("/agent/v1/tunnel/extra", "GET", Guid.Empty).IsFailure.ShouldBeTrue();
        GatewayRouter.Resolve("/agent/v2/tunnel", "GET", Guid.Empty).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("POST", "/agent/v1/tunnel", 404)]
    [InlineData("GET", "/agent/v1/anything-else", 400)]
    [InlineData("GET", "/agent/", 400)]
    public async Task EverythingElseUnderTheExemptedPrefixIsRefusedInJsonWithoutATokenCheck(
        string method,
        string path,
        int expected
    ) {
        // ⚠ Exemption from stage 2 is not routing. The tunnel path on any other verb is the canonical
        // 404, as security.txt's is; any other path under /agent/ falls through to the resource
        // parser and is the 400 every malformed resource path gets — the same shape
        // /.well-known/openid-configuration has, pinned in SecurityTxtTests. Neither is a 401: the
        // prefix is anonymous, and neither says anything a probe could learn from.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(method, path, null, "");

        response.Status.ShouldBe(expected);
        response.Body.ShouldContain(
            expected == 404 ? "\"code\":\"ResourceNotFound\"" : "\"code\":\"InvalidResourceId\""
        );
    }

    [Fact]
    public async Task ATokenOnTheTunnelPathIsNeitherRequiredNorRefused() {
        // An agent never holds one, but an operator poking the URL with their own token should get
        // the endpoint's answer rather than the pipeline's.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            TunnelCodec.TunnelPath,
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldBeEmpty();
    }
}
