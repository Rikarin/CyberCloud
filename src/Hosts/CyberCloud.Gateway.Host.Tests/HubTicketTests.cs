using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The hub ticket through the nine stages — <c>HubTickets</c>, docs/plan/10 § SignalR.
/// </summary>
/// <remarks>
///     <para>
///         What a browser does, in two requests: <c>POST /hubs/terminal/ticket</c> with its bearer
///         token, then <c>GET /hubs/terminal?ticket=…</c> with no header at all. The first is an
///         ordinary authenticated write stage 8 answers; the second is the WebSocket upgrade, and it
///         reaches stage 8 with the <i>same</i> caller the first one had — which is the whole claim,
///         and the one <see cref="TheUpgradeCarriesTheMintingRequestsCaller" /> makes.
///     </para>
///     <para>
///         ⚠ <b>Every refusal here is the same <c>401</c>.</b> A spent ticket, an expired one, one
///         minted for another hub, one that never existed, and the bearer token itself pasted where
///         the ticket goes: the pipeline tells none of them apart in the response, so a leaked URL
///         teaches its holder nothing. <c>HubTicketOverHttpTests</c> drives the same rules through
///         Kestrel and a real WebSocket upgrade; this suite is the cheap sweep.
///     </para>
/// </remarks>
public sealed class HubTicketTests {
    static string TicketOf(GatewayResponse response) =>
        JsonDocument.Parse(response.Body).RootElement.GetProperty("ticket").GetString() ?? "";

    [Fact]
    public async Task ATicketIsMintedForTheCallerAndForOneHub() {
        var gateway = new GatewayHarness();

        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        minted.Status.ShouldBe(StatusCodes.Status200OK);
        minted.Header("Cache-Control").ShouldBe("no-store");

        var body = JsonDocument.Parse(minted.Body).RootElement;
        body.GetProperty("hub").GetString().ShouldBe("/hubs/terminal");
        body.GetProperty("ticket").GetString()!.Length.ShouldBe(43); // 32 bytes, base64url, no padding
        DateTimeOffset.Parse(
            body.GetProperty("expiresAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture
        )
            .ShouldBe(gateway.Clock.UtcNow + HubTickets.Lifetime);

        // A mint is counted like the write it is, not exempted like the hub it opens.
        minted.Trace.ShouldContain("RateLimit");
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheUpgradeCarriesTheMintingRequestsCaller() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA, "user-7"),
            ""
        );

        var upgrade = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(minted));

        // No outcome was written: the request left the pipeline for SignalR, as a hub request does,
        // and did so through every stage — including stage 3, which built the caller.
        upgrade.Status.ShouldBe(StatusCodes.Status200OK);
        upgrade.Body.ShouldBeEmpty();
        upgrade.Trace.ShouldContain("ResolveTenant");
        upgrade.Trace.ShouldContain("Dispatch");

        // ⚠ And the caller is the one the mint had — tenant, subject type and subject id — not
        // merely *a* caller. `GatewayResponse.Caller` is the object MapGateway parks for the hub, so
        // this is the hub's view; a store that answered with another ticket's claims would pass every
        // line above and fail here. The correlation id is the one field that differs by design: it
        // is the upgrade's own request, not the mint's.
        upgrade.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        upgrade.Caller.SubjectType.ShouldBe("user");
        upgrade.Caller.SubjectId.ShouldBe("user-7");
        upgrade.Caller.ImpersonatedBy.ShouldBeEmpty();
        (upgrade.Caller with { CorrelationId = "" }).ShouldBe(minted.Caller with { CorrelationId = "" });
        upgrade.Caller.CorrelationId.ShouldNotBeEmpty();
        upgrade.Caller.CorrelationId.ShouldNotBe(minted.Caller.CorrelationId);
    }

    [Fact]
    public async Task EachTicketRedeemsToItsOwnCallerWhateverTheOrder() {
        // Two tenants mint, and the second ticket is redeemed first. The property is that a ticket is
        // keyed by its own bytes and nothing else — not "the last claims parked", not "the first".
        var gateway = new GatewayHarness();
        var byA = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA, "user-7"),
            ""
        );
        var byB = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantB, "sp-4", "servicePrincipal"),
            ""
        );

        var asB = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(byB));
        var asA = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(byA));

        asB.Status.ShouldBe(StatusCodes.Status200OK);
        asB.Caller.TenantId.ShouldBe(GatewayHarness.TenantB);
        asB.Caller.SubjectType.ShouldBe("servicePrincipal");
        asB.Caller.SubjectId.ShouldBe("sp-4");

        asA.Status.ShouldBe(StatusCodes.Status200OK);
        asA.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        asA.Caller.SubjectType.ShouldBe("user");
        asA.Caller.SubjectId.ShouldBe("user-7");
    }

    [Fact]
    public async Task ATicketIsSpentByItsFirstUse() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );
        var ticket = TicketOf(minted);

        (await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + ticket)).Status.ShouldBe(
            StatusCodes.Status200OK
        );

        var second = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + ticket);

        second.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        second.Header("WWW-Authenticate").ShouldBe("Bearer");
        second.Body.ShouldContain("the hub ticket was not accepted");

        // Refused at stage 2, so stage 3 never ran and no caller exists for a hub to read.
        second.Trace.ShouldNotContain("ResolveTenant");
        second.Caller.TenantId.ShouldBe(Guid.Empty);
        second.Caller.SubjectId.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATicketExpiresWithItsLifetime() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        gateway.Clock.Advance(HubTickets.Lifetime + TimeSpan.FromSeconds(1));

        var late = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(minted));

        late.Status.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ATicketExpiresWithTheTokenBehindItWhenThatIsSooner() {
        var gateway = new GatewayHarness();
        // The harness's tokens live ten minutes; wind the clock to leave the token five seconds.
        var token = gateway.Token(GatewayHarness.TenantA);
        gateway.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(5));

        var minted = await gateway.SendAsync("POST", "/hubs/terminal/ticket", token, "");
        minted.Status.ShouldBe(StatusCodes.Status200OK);

        var expiresAt = DateTimeOffset.Parse(
            JsonDocument.Parse(minted.Body).RootElement.GetProperty("expiresAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture
        );
        expiresAt.ShouldBe(gateway.Clock.UtcNow + TimeSpan.FromSeconds(5));

        gateway.Clock.Advance(TimeSpan.FromSeconds(6));

        (await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(minted))).Status
            .ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ATicketOpensOnlyTheHubItWasMintedFor() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/resources/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );
        var ticket = TicketOf(minted);

        var wrongHub = await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + ticket);
        wrongHub.Status.ShouldBe(StatusCodes.Status401Unauthorized);

        // ⚠ And the attempt spent it: the right hub does not get a second try.
        var rightHub = await gateway.SendAsync("GET", "/hubs/resources", null, "ticket=" + ticket);
        rightHub.Status.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task ATicketCannotMintAnotherTicket() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        // The ticket route is not a hub path, so the query is not read there — the request is the
        // anonymous POST it looks like, and gets the 401 an absent header gets.
        var chained = await gateway.SendAsync("POST", "/hubs/terminal/ticket", null, "ticket=" + TicketOf(minted));

        chained.Status.ShouldBe(StatusCodes.Status401Unauthorized);
        chained.Body.ShouldContain("no Authorization header");

        // And the ticket itself is untouched by the attempt: the upgrade still works.
        (await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + TicketOf(minted))).Status
            .ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task TheBearerTokenItselfIsNotAcceptedInTheUrl() {
        // The property the ticket exists for. The SignalR client's own convention is
        // `?access_token=<bearer>`; neither that spelling nor the ticket parameter accepts a token.
        var gateway = new GatewayHarness();
        var token = gateway.Token(GatewayHarness.TenantA);

        (await gateway.SendAsync("GET", "/hubs/terminal", null, "access_token=" + token)).Status
            .ShouldBe(StatusCodes.Status401Unauthorized);
        (await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + token)).Status
            .ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task AHeaderWhenPresentDecidesAloneAndATicketIsNotASecondChance() {
        var gateway = new GatewayHarness();
        var minted = await gateway.SendAsync(
            "POST",
            "/hubs/terminal/ticket",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );
        var ticket = TicketOf(minted);

        var refused = await gateway.SendAsync("GET", "/hubs/terminal", "cc_forged", "ticket=" + ticket);

        refused.Status.ShouldBe(StatusCodes.Status401Unauthorized);

        // The ticket was not read, so it is still good.
        (await gateway.SendAsync("GET", "/hubs/terminal", null, "ticket=" + ticket)).Status.ShouldBe(
            StatusCodes.Status200OK
        );
    }

    [Fact]
    public async Task MintingNeedsATokenAndIsPostOnly() {
        var gateway = new GatewayHarness();

        (await gateway.SendAsync("POST", "/hubs/terminal/ticket", null, "")).Status.ShouldBe(
            StatusCodes.Status401Unauthorized
        );
        (await gateway.SendAsync("GET", "/hubs/terminal/ticket", gateway.Token(GatewayHarness.TenantA), "")).Status
            .ShouldBe(StatusCodes.Status404NotFound);
        (await gateway.SendAsync("POST", "/hubs/nope/ticket", gateway.Token(GatewayHarness.TenantA), "")).Status
            .ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void TheRouterKnowsTheThreeShapesUnderAHubAndNothingElse() {
        GatewayRouter.Resolve("/hubs/terminal", "GET", Guid.Empty).GetValueOrThrow().Kind.ShouldBe(RouteKind.Hub);
        GatewayRouter.Resolve("/hubs/terminal/negotiate", "POST", Guid.Empty)
            .GetValueOrThrow()
            .Kind.ShouldBe(RouteKind.Hub);
        GatewayRouter.Resolve("/hubs/terminal/ticket", "POST", Guid.Empty)
            .GetValueOrThrow()
            .Kind.ShouldBe(RouteKind.HubTicket);
        GatewayRouter.Resolve("/hubs/terminal/ticket", "POST", Guid.Empty)
            .GetValueOrThrow()
            .HubName.ShouldBe("terminal");
        GatewayRouter.Resolve("/hubs/terminal/ticket", "GET", Guid.Empty).IsFailure.ShouldBeTrue();
        GatewayRouter.Resolve("/hubs/terminal/other", "GET", Guid.Empty).IsFailure.ShouldBeTrue();
        GatewayRouter.Resolve("/hubs/terminal/ticket/more", "POST", Guid.Empty).IsFailure.ShouldBeTrue();

        // Stage 5's classification, which runs before the router: the mint is counted, the hub is not.
        GatewayRouter.Classify("/hubs/terminal/ticket", "POST", new QueryCollection()).ShouldBe(RequestClass.Write);
        GatewayRouter.Classify("/hubs/terminal", "GET", new QueryCollection()).ShouldBe(RequestClass.Hub);
        GatewayRouter.Classify("/hubs/terminal/negotiate", "POST", new QueryCollection()).ShouldBe(RequestClass.Hub);
    }

    [Fact]
    public async Task ANegotiateWithATokenRoutesToTheHubNowRatherThanTo404() {
        // Before the ticket landed, `resources/negotiate` was looked up as a hub name and answered 404
        // to every SignalR client that negotiated. It is the hub.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            "/hubs/resources/negotiate",
            gateway.Token(GatewayHarness.TenantA),
            ""
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldBeEmpty();
    }
}
