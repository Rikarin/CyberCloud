using System.Diagnostics;

namespace CyberCloud.Sdk.Tests;

/// <summary>Retry — docs/plan/10 § Rate limiting and docs/plan/21 § The .NET SDK's retry row.</summary>
public sealed class RetryTests {
    /// <summary>
    ///     ⚠ <b>The assertion is on the elapsed time, not just on the retry.</b> A pipeline that
    ///     retried a <c>429</c> immediately would pass a "did it retry" test and still be the client
    ///     docs/plan/10 § Rate limiting is trying to prevent. <c>Retry-After: 1</c> must cost about a
    ///     second.
    /// </summary>
    [Fact]
    public async Task A_429_waits_the_Retry_After_the_service_asked_for() {
        var transport = new ScriptedTransport((request, index) => index == 0
            ? Responses.TooManyRequests(retryAfterSeconds: 1)
            : Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport);

        var stopwatch = Stopwatch.StartNew();
        var response = await client.Widgets().GetAsync("main", Cancel.Token);

        response.Value.Data.Location.ShouldBe("eu-central");
        transport.RequestCount.ShouldBe(2);

        // The configured backoff is 1 ms, so anything close to a second can only have come from the
        // header.
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task A_5xx_is_retried_with_backoff() {
        var transport = new ScriptedTransport((request, index) => index < 2
            ? Responses.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"InternalError","message":"Restarting."}}""")
            : Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport);

        var response = await client.Widgets().GetAsync("main", Cancel.Token);

        response.Value.Data.Location.ShouldBe("eu-central");
        transport.RequestCount.ShouldBe(3);
    }

    /// <summary>
    ///     ⚠ docs/plan/06 § Two-phase create makes a <c>409</c> mean "the name is taken". Retrying it
    ///     gets the same answer three times and, after a partial write, invites the caller to wonder
    ///     whether the write landed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task A_4xx_that_is_not_429_is_not_retried(HttpStatusCode status) {
        var transport = new ScriptedTransport((request, index) =>
            Responses.Json(status, """{"error":{"code":"Conflict","message":"The name is taken."}}"""));

        using var client = TestClient.Create(transport);

        await Should.ThrowAsync<CyberCloudRequestFailedException>(async () => await client.Widgets().GetAsync("main", Cancel.Token));

        transport.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_retried_request_still_carries_its_body() {
        var transport = new ScriptedTransport((request, index) => index == 0
            ? Responses.Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Responses.Accepted(TestClient.OperationUri));

        using var client = TestClient.Create(transport);

        await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        // ⚠ HttpContent is single-use. A retry that reused the original message would send an empty
        // body, which no status code would reveal.
        transport.Requests[1].Body.ShouldContain("eu-central");
        transport.Requests[1].Body.ShouldBe(transport.Requests[0].Body);
    }
}

/// <summary>Correlation — docs/plan/10 § Request pipeline, stage 1.</summary>
public sealed class CorrelationTests {
    /// <summary>
    ///     ⚠ <b>One id per logical call, not per attempt.</b> If the id were minted per attempt the
    ///     three lines a 429-429-200 writes into the gateway's log would look like three unrelated
    ///     callers, which is the question the header exists to answer.
    /// </summary>
    [Fact]
    public async Task Every_attempt_of_one_call_carries_the_same_correlation_id() {
        var transport = new ScriptedTransport((request, index) => index < 2
            ? Responses.TooManyRequests(retryAfterSeconds: 0)
            : Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport);

        await client.Widgets().GetAsync("main", Cancel.Token);

        transport.RequestCount.ShouldBe(3);

        var ids = transport.Requests.Select(x => x.CorrelationId).ToList();

        ids.ShouldAllBe(x => x != null);
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
    }

    [Fact]
    public async Task Two_calls_carry_two_ids() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport);

        await client.Widgets().GetAsync("a", Cancel.Token);
        await client.Widgets().GetAsync("b", Cancel.Token);

        transport.Requests[0].CorrelationId.ShouldNotBe(transport.Requests[1].CorrelationId);
    }

    /// <summary>
    ///     docs/plan/10 § Request pipeline wants the id "on every log line and every span". Reusing the
    ///     ambient trace id makes the join between a caller's traces and the platform's free.
    /// </summary>
    [Fact]
    public async Task An_ambient_activity_supplies_the_correlation_id() {
        using var source = new ActivitySource("CyberCloud.Sdk.Tests");
        using var listener = new ActivityListener {
            ShouldListenTo = x => x.Name == "CyberCloud.Sdk.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);

        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));
        using var client = TestClient.Create(transport);

        using var activity = source.StartActivity("call");
        activity.ShouldNotBeNull();

        await client.Widgets().GetAsync("main", Cancel.Token);

        transport.Requests[0].CorrelationId.ShouldBe(activity.TraceId.ToHexString());
    }

    [Fact]
    public async Task Every_request_carries_the_api_version() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport);

        await client.Widgets().GetAsync("main", Cancel.Token);

        transport.Requests[0].Uri.Query.ShouldContain("api-version=2026-08-01");
    }

    /// <summary>
    ///     ⚠ The poll URL comes from the <c>Azure-AsyncOperation</c> header and already carries a
    ///     version. Appending a second one is a <c>400</c> that would only ever appear on the second
    ///     request of an LRO.
    /// </summary>
    [Fact]
    public async Task A_url_that_already_names_the_api_version_is_left_alone() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            _ => Responses.Operation("Succeeded"),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);
        await operation.UpdateStatusAsync(Cancel.Token);

        var poll = transport.Requests[1].Uri;

        poll.Query.ShouldBe("?api-version=2026-08-01");
    }
}

/// <summary>Error mapping — docs/plan/08 § Errors.</summary>
public sealed class ErrorTargetSurvivesTests {
    /// <summary>
    ///     ⚠ <b>This is the failure class the whole error type exists for.</b> docs/plan/08 § Errors:
    ///     <i>"<c>target</c> is a JSON Pointer into the request body so the portal can highlight the
    ///     field."</i> The pointer has to survive the transport, the buffering, the parse and the
    ///     exception construction, and arrive at the caller intact.
    /// </summary>
    [Fact]
    public async Task The_json_pointer_reaches_the_caller_intact() {
        const string body = """
            {"error":{"code":"QuotaExceeded",
                      "message":"Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2).",
                      "target":"/properties/sku",
                      "details":[{"code":"InvalidRequestBody","message":"replicas must be at least 1.","target":"/properties/replicas"}]}}
            """;

        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.BadRequest, body));

        using var client = TestClient.Create(transport);

        var thrown = await Should.ThrowAsync<CyberCloudRequestFailedException>(
            async () => await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token));

        thrown.Status.ShouldBe(400);
        thrown.ErrorCode.ShouldBe("QuotaExceeded");
        thrown.Target.ShouldBe("/properties/sku");
        thrown.Message.ShouldContain("requested 8, available 2");
        thrown.ServiceRequestId.ShouldNotBeNullOrEmpty();

        // Every problem at once — docs/plan/08 § Errors: "a form that has to be fixed one field per
        // round trip is a form nobody finishes".
        thrown.Details.Single().Target.ShouldBe("/properties/replicas");
    }

    /// <summary>
    ///     A gateway that fell over before stage 9 of docs/plan/10 § Request pipeline sends an HTML
    ///     page. The status code must still reach the caller.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_the_error_shape_still_produces_a_usable_exception() {
        var transport = new ScriptedTransport((request, index) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") });

        using var client = TestClient.Create(transport, configure: options => options.Retry.MaxRetries = 0);

        var thrown = await Should.ThrowAsync<CyberCloudRequestFailedException>(async () => await client.Widgets().GetAsync("main", Cancel.Token));

        thrown.Status.ShouldBe(502);
        thrown.ErrorCode.ShouldBeNull();
        thrown.Target.ShouldBeNull();
    }
}

/// <summary><c>Response&lt;T&gt;</c> / <c>NullableResponse&lt;T&gt;</c> — docs/plan/21 § The .NET SDK.</summary>
public sealed class ResponseTests {
    [Fact]
    public async Task A_found_resource_has_a_value() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));
        using var client = TestClient.Create(transport);

        var response = await client.Widgets().GetIfExistsAsync("main", Cancel.Token);

        response.HasValue.ShouldBeTrue();
        response.Value.Data.Properties!.Message.ShouldBe("hello");
        response.GetRawResponse().Status.ShouldBe(200);
    }

    /// <summary>
    ///     ⚠ docs/plan/07 § The enforcement seam makes "does not exist" and "you may not read it"
    ///     deliberately indistinguishable, so <c>404</c> is an ordinary answer on this API and a caller
    ///     checking existence should not pay for a throw.
    /// </summary>
    [Fact]
    public async Task A_missing_resource_is_an_answer_rather_than_an_exception() {
        var transport = new ScriptedTransport((request, index) =>
            Responses.Json(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"No such widget."}}"""));

        using var client = TestClient.Create(transport);

        var response = await client.Widgets().GetIfExistsAsync("gone", Cancel.Token);

        response.HasValue.ShouldBeFalse();
        response.GetRawResponse().Status.ShouldBe(404);
        Should.Throw<InvalidOperationException>(() => response.Value);
    }

    [Fact]
    public async Task A_response_converts_implicitly_to_its_value() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));
        using var client = TestClient.Create(transport);

        WidgetResource resource = await client.Widgets().GetAsync("main", Cancel.Token);

        resource.Data.Location.ShouldBe("eu-central");
    }
}

/// <summary><c>AsyncPageable&lt;T&gt;</c> — docs/plan/21 § The .NET SDK.</summary>
public sealed class PageableTests {
    static HttpResponseMessage PageOf(string name, string? nextLink)
        => Responses.Json(
            HttpStatusCode.OK,
            $$"""
              {"value":[{"location":"{{name}}"}]{{(nextLink is null ? "" : $",\"nextLink\":\"{nextLink}\"")}}}
              """);

    [Fact]
    public async Task Every_page_is_enumerated_and_pages_are_fetched_lazily() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => PageOf("one", "https://api.cybercloud.test/widgets?page=2&api-version=2026-08-01"),
            1 => PageOf("two", "https://api.cybercloud.test/widgets?page=3&api-version=2026-08-01"),
            _ => PageOf("three", null),
        });

        using var client = TestClient.Create(transport);

        var seen = new List<(string Location, int RequestsSoFar)>();

        await foreach (var widget in client.Widgets().GetAll(Cancel.Token))
            seen.Add((widget.Location, transport.RequestCount));

        seen.Select(x => x.Location).ShouldBe(["one", "two", "three"]);

        // Lazy: the third page has not been fetched when the first item is yielded.
        seen[0].RequestsSoFar.ShouldBe(1);
        seen[2].RequestsSoFar.ShouldBe(3);
    }

    [Fact]
    public async Task AsPages_exposes_the_continuation_token() {
        var transport = new ScriptedTransport((request, index) => index == 0
            ? PageOf("one", "https://api.cybercloud.test/widgets?page=2&api-version=2026-08-01")
            : PageOf("two", null));

        using var client = TestClient.Create(transport);

        var pages = new List<Page<WidgetData>>();

        await foreach (var page in client.Widgets().GetAll(Cancel.Token).AsPages())
            pages.Add(page);

        pages.Count.ShouldBe(2);
        pages[0].ContinuationToken.ShouldNotBeNull();
        pages[1].ContinuationToken.ShouldBeNull();
    }

    /// <summary>A <c>429</c> between pages is the pipeline's problem, not the consumer's.</summary>
    [Fact]
    public async Task A_429_between_pages_is_retried_underneath() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => PageOf("one", "https://api.cybercloud.test/widgets?page=2&api-version=2026-08-01"),
            1 => Responses.TooManyRequests(retryAfterSeconds: 0),
            _ => PageOf("two", null),
        });

        using var client = TestClient.Create(transport);

        var locations = new List<string>();

        await foreach (var widget in client.Widgets().GetAll(Cancel.Token))
            locations.Add(widget.Location);

        locations.ShouldBe(["one", "two"]);
    }
}
