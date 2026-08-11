using CyberCloud.Sdk;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     Every request the CLI makes goes through the SDK's pipeline, <c>cyc rest</c> included.
/// </summary>
/// <remarks>
///     ⚠ docs/plan/21 § Grammar calls <c>cyc rest</c> <i>"the escape hatch for anything not yet a
///     verb"</i>. Raw means untyped, not unauthenticated: the tests below read the headers off the
///     transport and check that the bearer token, the correlation id and the api-version are all there
///     — none of which the CLI puts on the request itself.
/// </remarks>
public sealed class PipelineTests {
    [Fact]
    public async Task ARestCallIsAuthenticatedCorrelatedAndVersioned() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, """{"value":[]}"""));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync("rest", "--uri", "/tenants/t/subscriptions", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);

        var request = transport.Requests[0];

        request.Header("Authorization").ShouldBe($"Bearer {TestHost.FixedToken}");
        request.Header(CyberCloudHeaders.CorrelationRequestId).ShouldNotBeNullOrEmpty();
        request.Uri.Query.ShouldContain("api-version=2026-08-01");
    }

    [Fact]
    public async Task ARestWriteSendsTheBodyItWasGiven() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "rest", "--method", "POST", "--uri", "/tenants/t/x", "--body", """{"a":1}""", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);
        transport.Requests[0].Method.ShouldBe(HttpMethod.Post);
        transport.Requests[0].Body.ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task ARestBodyThatIsNotJsonIsAUsageError() {
        using var host = TestHost.Create();

        (await host.RunAsync("rest", "--uri", "/x", "--method", "POST", "--body", "{oops")).ShouldBe((int)ExitCode.Usage);
    }

    [Fact]
    public async Task AGeneratedVerbSendsOnlyTheFlagsThatWereGiven() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));
        using var host = TestHost.Create(transport);

        await host.RunAsync(
            "sample", "widgets", "update",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--message", "hello", "--output", "none");

        // ⚠ A PATCH is a merge patch. `--tier` has a default of `free` in the tree and was not
        // typed, so it must not be in the body — sending it would move a premium widget to free.
        using var body = JsonDocument.Parse(transport.Requests[0].Body);

        body.RootElement.GetProperty("properties").GetProperty("message").GetString().ShouldBe("hello");
        body.RootElement.GetProperty("properties").TryGetProperty("tier", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("location", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task FlagsAreWrittenAtTheirJsonPointers() {
        var transport = new ScriptedTransport((_, index) => index == 0
            ? Responses.Accepted("https://api.cybercloud.io/operations/op-1")
            : Responses.Json(HttpStatusCode.OK, """{"status":"Succeeded"}"""));

        using var host = TestHost.Create(transport);

        await host.RunAsync(
            "sample", "widgets", "create",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--location", "eu-central", "--message", "hi", "--cluster-id", "c1",
            "--replicas", "3", "--enabled", "--tags", "env=prod", "--tags", "owner=platform",
            "--allowed-cidrs", "10.0.0.0/8", "--allowed-cidrs", "10.1.0.0/16",
            "--no-wait", "--output", "none");

        using var body = JsonDocument.Parse(transport.Requests[0].Body);
        var root = body.RootElement;
        var properties = root.GetProperty("properties");

        root.GetProperty("location").GetString().ShouldBe("eu-central");
        properties.GetProperty("clusterId").GetString().ShouldBe("c1");
        properties.GetProperty("replicas").GetInt32().ShouldBe(3);
        properties.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        properties.GetProperty("allowedCidrs").EnumerateArray().Select(x => x.GetString()).ShouldBe(["10.0.0.0/8", "10.1.0.0/16"]);
        root.GetProperty("tags").GetProperty("env").GetString().ShouldBe("prod");
        root.GetProperty("tags").GetProperty("owner").GetString().ShouldBe("platform");
    }

    [Fact]
    public async Task AnErrorTargetIsReportedAsTheFlagThatCarriedIt() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Error(
            HttpStatusCode.BadRequest,
            "InvalidSku",
            "'gold' is not a tier this subscription may use.",
            "/properties/tier")));

        var code = await host.RunAsync(
            "sample", "widgets", "update",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--tier", "premium");

        code.ShouldBe((int)ExitCode.ClientError);

        // ⚠ docs/plan/08 § Errors makes `target` a JSON Pointer so the portal can highlight the
        // field. A terminal's equivalent is naming the flag: nobody typed /properties/tier.
        host.Stderr.ShouldContain("--tier");
    }

    [Fact]
    public async Task ARetriedRequestKeepsOneCorrelationId() {
        var transport = new ScriptedTransport((_, index) => index == 0
            ? Throttled()
            : Responses.Json(HttpStatusCode.OK, "{}"));

        using var host = TestHost.Create(transport);

        await host.RunAsync("rest", "--uri", "/tenants/t/x", "--output", "none");

        // Two attempts, one logical call — docs/plan/10 § Request pipeline, and the placement of the
        // correlation handler outside the retry handler is what makes it true. The CLI gets this for
        // free by not owning a pipeline.
        transport.RequestCount.ShouldBe(2);

        transport.Requests[0].Header(CyberCloudHeaders.CorrelationRequestId)
            .ShouldBe(transport.Requests[1].Header(CyberCloudHeaders.CorrelationRequestId));
    }

    static HttpResponseMessage Throttled() {
        var response = Responses.Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests","message":"Slow down."}}""");
        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.RetryAfter, "0");

        return response;
    }
}
