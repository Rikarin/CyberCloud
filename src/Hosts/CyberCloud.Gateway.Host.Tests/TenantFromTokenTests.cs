using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using NSubstitute;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     THE test. Every surface a caller can write a tenant id into, tried against a token that says
///     something else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this file is the most important one in the project.</b> docs/plan/00 § The
///         tenant-separation row, corrected establishes from a decompilation that
///         <c>Orleans.Multitenant</c>'s <c>TenantSeparatingCallFilter</c> never consults the
///         authorizer for a caller that is not a grain, and
///         <c>CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests.Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>
///         demonstrates the hole with separation fully wired. The gateway is an Orleans client by
///         design. So there is no mechanism under these assertions: if one of them stops holding, a
///         cross-tenant read happens with no exception and no log line.
///     </para>
///     <para>
///         Every test here asserts <b>two</b> things: the answer is the canonical <c>404</c>, and
///         nothing reached tenant B — neither a dispatch carrying B's path nor a single call on the
///         grain factory.
///     </para>
/// </remarks>
public sealed class TenantFromTokenTests {
    [Fact]
    public async Task APathNamingAnotherTenantIs404AndNothingReachesThatTenant() {
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantB),
            token
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);

        // ⚠ Not merely "no B path was dispatched" — nothing was dispatched at all. Stage 3 stops
        // before stage 6 has resolved a route, so the manager was never asked anything.
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheStageThatRefusedIsStage3AndNotSomethingLater() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA)
        );

        // The trace is the assertion that the refusal happened where the document puts it. A 404
        // produced at stage 8 would pass the status assertion and would mean the request had already
        // been routed and dispatched with a foreign tenant in hand.
        response.Trace.ShouldBe(["Correlation", "Authenticate", "ResolveTenant", "ShapeResponse"]);
    }

    [Fact]
    public async Task ATenantHeaderNamingAnotherTenantIs404() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            headers: (GatewayHeaders.TenantIdHint, GatewayHarness.TenantB.ToString("D"))
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATenantQueryParameterNamingAnotherTenantIs404() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            $"api-version={OneTypeRegistry.TheVersion}&tenantId={GatewayHarness.TenantB:D}"
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("tid")]
    public async Task ATenantInTheBodyNamingAnotherTenantIs404(string property) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{\"" + property + "\":\"" + GatewayHarness.TenantB.ToString("D") + "\",\"properties\":{}}"
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AForgedTokenIs401AndCarriesNoTenantAtAll() {
        var gateway = new GatewayHarness();

        // A token this platform did not issue. It "says" tenant B in the sense that an attacker
        // wrote B into it; the resolver has no path on which that string is read as a claim.
        var forged = "cc_" + GatewayHarness.TenantB.ToString("N") + "-tid-" + GatewayHarness.TenantB.ToString("N");

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantB),
            forged
        );

        response.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        response.Header("WWW-Authenticate").ShouldBe("Bearer");
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();

        // ⚠ And the 401 body says nothing about tenants. A message naming the tenant the attacker
        // guessed would confirm the guess.
        response.Body.ShouldNotContain(GatewayHarness.TenantB.ToString("D"));
    }

    [Fact]
    public async Task NoTokenAtAllIs401() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", GatewayHarness.ResourcePath(GatewayHarness.TenantA), null);

        response.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnExpiredTokenIs401EvenThoughItWasIssuedHere() {
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);

        gateway.Clock.Advance(TimeSpan.FromMinutes(11));

        var response = await gateway.SendAsync("GET", GatewayHarness.ResourcePath(GatewayHarness.TenantA), token);

        response.Status.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    ///     A physical grain key in the URL — route 7b's move, tried through the front door.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the exact attack <c>Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>
    ///     performs successfully from a cluster client.</b> <c>Orleans.Multitenant</c> encodes the
    ///     tenant into a string key as <c>{tenant}|{key}</c>, so a raw key is a complete address for
    ///     another tenant's grain and nothing in the runtime refuses it. Through the gateway it never
    ///     becomes a grain key at all: it is not a resource-id path, so it does not parse, and it
    ///     names no tenant the token agrees with.
    /// </remarks>
    [Theory]
    [InlineData("/tenant/{0}")]
    [InlineData("/{0}|tenant/{0}")]
    [InlineData("/tenants/{0}|res/deadbeef")]
    public async Task ARawGrainKeyInTheUrlNeverBecomesAGrainReference(string shape) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, GatewayHarness.TenantB.ToString("N")),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBeOneOf(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>
    ///     The positive control: the same request against the caller's own tenant works, and the path
    ///     that reaches dispatch is the one rebuilt from the token.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without this test the file proves only that the gateway refuses everything.
    /// </remarks>
    [Fact]
    public async Task TheCallersOwnTenantIsServedAndDispatchCarriesTheTokensTenant() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        gateway.Manager.Paths.ShouldHaveSingleItem();
        gateway.Manager.Paths.Single().ShouldContain(GatewayHarness.TenantA.ToString("D"));
        gateway.Manager.Paths.Single().ShouldNotContain(GatewayHarness.TenantB.ToString("D"));
    }

    /// <summary>
    ///     ⚠ The second defence, asserted on its own: even a path that <i>agrees</i> is not the
    ///     source of the tenant.
    /// </summary>
    /// <remarks>
    ///     The path segment is discarded and the id is rebuilt from <c>CallerContext.TenantId</c>
    ///     (<c>GatewayRouter.Resolve</c>), so deleting the stage-3 check would degrade the failure
    ///     mode to "not found" rather than to "another tenant's resource". This test pins the
    ///     rebuild by using a path whose tenant is spelled in upper case: a value taken from the URL
    ///     would come back upper-cased, and one rebuilt from the token comes back canonical.
    /// </remarks>
    [Fact]
    public async Task TheDispatchedPathIsRebuiltRatherThanEchoed() {
        var gateway = new GatewayHarness();

        var upperCased = GatewayHarness
            .ResourcePath(GatewayHarness.TenantA)
            .Replace(
                GatewayHarness.TenantA.ToString("D"),
                GatewayHarness.TenantA.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal
            );

        var response = await gateway.SendAsync("GET", upperCased, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status200OK);
        gateway.Manager.Paths.Single().ShouldContain(GatewayHarness.TenantA.ToString("D"));
    }
}
