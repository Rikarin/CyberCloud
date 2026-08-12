using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway actually binds an action to, and what it sends back when one answers
///     directly.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE OPENAPI DOCUMENT IS NOT EVIDENCE AND THAT IS WHY THIS SUITE EXISTS.</b>
///         <c>openapi/2026-08-01.json</c> declares every <c>listKeys</c> as a <c>POST</c> — and the
///         document is generated from the registry, while the route is decided by
///         <c>GatewayRouter.Resolve</c> reading the HTTP method. Two independent statements about one
///         fact; asserting the first proves nothing about the second. These assertions drive the real
///         pipeline.
///     </para>
///     <para>
///         ⚠ <b>What a <c>GET</c> would cost, which is why the verb is worth a suite.</b> The resource
///         address ends up in access logs, in a proxy's history and in the browser's; the response
///         becomes cacheable by default; and a credential arrives somewhere nobody is looking after
///         it. None of that is recoverable by rotating, because rotation needs two live credentials
///         and this platform can hold one.
///     </para>
/// </remarks>
public sealed class ActionRoutingTests {
    // ── Failure class (b): the route the gateway binds, not the one the document claims ────────

    [Fact]
    public async Task AnActionIsReachableOnPost() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/listKeys",
            gateway.Token(GatewayHarness.TenantA)
        );

        // ⚠ The assertion is that the ACTION path was taken, not the status: the fake manager answers
        // Completed: false by default, so this is a 202. What matters here is that the router
        // stripped `listKeys` as an action name and dispatch reached ActionAsync with it — the tests
        // below pin each status branch against a manager scripted for it.
        response.Status.ShouldNotBe(StatusCodes.Status404NotFound, response.Body);

        gateway.Manager.Actions.ShouldContain(
            "listKeys",
            "the router did not bind POST …/listKeys to the action path at all"
        );
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task AnActionIsNotReachableOnAReadVerb(string method) {
        // ⚠ GatewayRouter.Resolve sends everything that is not a POST to ResolveResource, so
        // `…/servers/main/listKeys` is parsed as a RESOURCE address — and there is no resource type
        // spelled that way. The refusal is what keeps a credential out of an access log.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/listKeys",
            gateway.Token(GatewayHarness.TenantA)
        );

        // ⚠ ASSERTED ON WHAT WAS DISPATCHED, NOT ON THE STATUS CODE, AND A SABOTAGE RUN IS WHY.
        // Routing GET to the action path makes the fake manager answer 202 — which is not 200, so a
        // status assertion passes while the credential path is wide open. `Paths` is no better: the
        // router strips the action segment before building the address, so it never ends in
        // `listKeys` either way. The only honest evidence is whether ActionAsync was called.
        gateway.Manager.Actions.ShouldBeEmpty(
            $"{method} on an action reached the manager's action path. The action's own permission is "
            + "checked on the POST path only, and a readable action puts the resource address in "
            + "access logs and browser history where a credential cannot be recalled from."
        );

        response.Status.ShouldNotBe(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AnActionIsNotReachableOnAWriteVerbEither(string method) {
        // The other half of the same rule: only POST names an action. A PUT to this path is a PUT to
        // a resource type nobody declared, and the platform must not treat it as an invocation.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/listKeys",
            gateway.Token(GatewayHarness.TenantA),
            body: "{}"
        );

        gateway.Manager.Actions.ShouldBeEmpty($"{method} was dispatched as an action invocation");
        response.Status.ShouldNotBe(StatusCodes.Status200OK);
    }

    // ── The synchronous answer ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompletedActionAnswersTwoHundredWithItsOwnBodyAndNothingToPoll() {
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = request => Result<WriteAccepted>.Success(
            new() {
                Completed = true,
                ActionResponse = """{"accessKeyId":"AKIA","secretAccessKey":"shh"}""",
                Resource = new() { Path = request.Path, Name = "main" }
            }
        );

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/listKeys",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Body.ShouldContain("accessKeyId");

        // ⚠ NO Azure-AsyncOperation, because there is nothing to poll. A 202 carrying an operation id
        // of Guid.Empty is a URL that answers 404 to every client polite enough to follow it.
        response.Header("Azure-AsyncOperation").ShouldBeEmpty();
        response.Header("Retry-After").ShouldBeEmpty();
    }

    [Fact]
    public async Task ACompletedActionsResponseIsNotCacheable() {
        // docs/plan/08 § The provider registry: a secret action's response is "never cached". A
        // credential in a proxy's cache outlives the request that was authorized for it.
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = request => Result<WriteAccepted>.Success(
            new() {
                Completed = true,
                ActionResponse = """{"accessKeyId":"AKIA","secretAccessKey":"shh"}""",
                Resource = new() { Path = request.Path, Name = "main" }
            }
        );

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/listKeys",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Header("Cache-Control").ShouldBe("no-store");
    }

    [Fact]
    public async Task ALongRunningActionStillAnswersTwoHundredAndTwoWithBothHeaders() {
        // The branch that has always worked, kept honest: docs/plan/10 § Long-running operations
        // pairs Azure-AsyncOperation with Retry-After, and that pair is what makes Operation<T> and
        // `cyc --wait` work with no bespoke code.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/restart",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted, response.Body);
        response.Header("Azure-AsyncOperation").ShouldNotBeEmpty();
        response.Header("Retry-After").ShouldNotBeEmpty();
    }
}
