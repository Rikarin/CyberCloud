using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The nine stages, in the documented order, on every response.
/// </summary>
public sealed class PipelineTests {
    [Fact]
    public void TheCanonicalOrderIsTheDocumentsOrder() {
        GatewayTrace.Canonical.ShouldBe([
            GatewayStage.Correlation,
            GatewayStage.Authenticate,
            GatewayStage.ResolveTenant,
            GatewayStage.RegionRouting,
            GatewayStage.RateLimit,
            GatewayStage.Route,
            GatewayStage.Validate,
            GatewayStage.Dispatch,
            GatewayStage.ShapeResponse
        ]);
    }

    [Fact]
    public async Task AFullRequestEntersAllNineStagesInOrder() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Trace.ShouldBe([
            "Correlation",
            "Authenticate",
            "ResolveTenant",
            "RegionRouting",
            "RateLimit",
            "Route",
            "Validate",
            "Dispatch",
            "ShapeResponse"
        ]);
    }

    /// <summary>
    ///     ⚠ Rate limiting must come before routing and dispatch, or a flood buys registry lookups
    ///     and grain activations.
    /// </summary>
    [Fact]
    public async Task RateLimitingRunsBeforeRoutingAndDispatch() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        var stages = response.Trace.ToList();
        stages.IndexOf("RateLimit").ShouldBeLessThan(stages.IndexOf("Route"));
        stages.IndexOf("RateLimit").ShouldBeLessThan(stages.IndexOf("Dispatch"));
        stages.IndexOf("ResolveTenant").ShouldBeLessThan(stages.IndexOf("Route"));
    }

    [Fact]
    public void ATraceThatSkippedOrReorderedAStageIsNotCanonical() {
        new GatewayTrace { Reached = [GatewayStage.Correlation, GatewayStage.RateLimit] }
            .IsCanonicalPrefix()
            .ShouldBeFalse();

        new GatewayTrace { Reached = [GatewayStage.Correlation, GatewayStage.Authenticate] }
            .IsCanonicalPrefix()
            .ShouldBeTrue();
    }

    [Fact]
    public void EnteringAStageOutOfOrderThrowsAtTheCallSite() {
        var trace = new GatewayTraceBuilder();
        trace.Enter(GatewayStage.Dispatch);

        Should.Throw<InvalidOperationException>(() => trace.Enter(GatewayStage.RateLimit))
            .Message.ShouldContain("Order matters");
    }

    /// <summary>
    ///     A pipeline missing a stage does not start. ⚠ A missing stage 3 is a cross-tenant hole and
    ///     a missing stage 5 is an unlimited channel, and neither shows up in a successful response.
    /// </summary>
    [Fact]
    public void APipelineMissingAStageRefusesToBeComposed() {
        Should.Throw<InvalidOperationException>(() =>
                new GatewayPipeline([new Pipeline.Stages.CorrelationStage()], NullLogger<GatewayPipeline>.Instance)
            )
            .Message.ShouldContain("cross-tenant hole");
    }

    // ── Correlation, on every response including errors ────────────────────────────────────────

    [Fact]
    public async Task TheCallersCorrelationIdComesBackAndARequestIdIsMinted() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (GatewayHeaders.CorrelationRequestId, "cli-run-42")
        );

        response.Header(GatewayHeaders.CorrelationRequestId).ShouldBe("cli-run-42");
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
        response.Header(GatewayHeaders.RequestId).ShouldNotBe("cli-run-42");
    }

    [Theory]
    [InlineData("GET", "/tenants/nonsense", null, 401)]
    [InlineData("GET", "/openapi", "", 400)]
    public async Task EveryErrorResponseCarriesBothIds(string method, string path, string? query, int expected) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            path,
            query is null ? null : gateway.Token(GatewayHarness.TenantA),
            query ?? "api-version=" + OneTypeRegistry.TheVersion
        );

        response.Status.ShouldBe(expected);
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
        response.Header(GatewayHeaders.CorrelationRequestId).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ACorrelationIdWithANewlineCannotForgeALogLine() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (GatewayHeaders.CorrelationRequestId, "ok\n2026-08-11 FATAL forged")
        );

        response.Header(GatewayHeaders.CorrelationRequestId).ShouldBe("ok2026-08-11 FATAL forged");
    }

    // ── No stack traces, ever ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     docs/plan/08 § Errors: <i>"No exception details, ever. A stack trace in an error body is
    ///     an information leak and a support-cost multiplier."</i>
    /// </summary>
    [Fact]
    public async Task AFaultingStageProducesA500WithNoDetailInTheBody() {
        var gateway = new GatewayHarness();

        gateway.Manager.OnRead = _ => throw new InvalidOperationException(
            "connection to postgres shard 3 at 10.4.2.11:5432 refused"
        );

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status500InternalServerError);

        response.Body.ShouldNotContain("postgres");
        response.Body.ShouldNotContain("10.4.2.11");
        response.Body.ShouldNotContain("InvalidOperationException");
        response.Body.ShouldNotContain("   at ");
        response.Body.ShouldNotContain("CyberCloud.Gateway");

        // The one thing the caller is given is the id that finds the detail in the trace.
        response.Body.ShouldContain(GatewayHeaders.RequestId);
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
    }

    [Fact]
    public void TheErrorBodyIsAzuresShapeAndHasNoRoomForATrace() {
        var body = System.Text.Encoding.UTF8.GetString(ErrorBody.Render(
            new(ErrorCode.QuotaExceeded, "vcpu would be exceeded (requested 8, available 2).", "/properties/sku")
        ));

        body.ShouldBe(
            "{\"error\":{\"code\":\"QuotaExceeded\","
            + "\"message\":\"vcpu would be exceeded (requested 8, available 2).\","
            + "\"target\":\"/properties/sku\"}}"
        );
    }
}
