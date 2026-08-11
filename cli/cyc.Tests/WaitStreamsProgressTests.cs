namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>--wait</c> renders progress as it arrives, not batched at the end — docs/plan/21
///     § Decisions: <i>"<c>--wait</c> streams the operation's progress array — this is what makes a
///     nine-minute cluster creation bearable in a terminal."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>"Streams" is only meaningful as a claim about <i>when</i>, so the test is about
///     ordering.</b> A host that collected every poll's entries and printed them after
///     <c>WaitForCompletion</c> would produce identical output and be useless at a terminal. The
///     transport records what the console had already been told at the moment each poll arrived, so an
///     implementation that batched would show an empty console at poll three and fail here.
/// </remarks>
public sealed class WaitStreamsProgressTests {
    [Fact]
    public async Task EachEntryIsPrintedBeforeTheNextPollIsMade() {
        var seenBeforeEachRequest = new List<string>();
        TestHost? host = null;

        var transport = new ScriptedTransport(
            (_, index) => index switch {
                0 => Responses.Accepted("https://api.cybercloud.io/operations/op-1"),
                1 => Poll(20, Entry(20, "applying", "namespace created")),
                2 => Poll(60, Entry(20, "applying", "namespace created"), Entry(60, "waiting-for-ready", "1 of 2 replicas ready")),
                3 => Poll(
                    90,
                    Entry(20, "applying", "namespace created"),
                    Entry(60, "waiting-for-ready", "1 of 2 replicas ready"),
                    Entry(90, "waiting-for-ready", "2 of 2 replicas ready")),
                4 => Responses.Json(HttpStatusCode.OK, """{"status":"Succeeded"}"""),
                _ => Responses.Json(HttpStatusCode.OK, """{"name":"w1"}"""),
            },
            index => seenBeforeEachRequest.Add(host?.Stderr ?? string.Empty));

        using var created = TestHost.Create(transport);
        host = created;

        var code = await created.RunAsync(
            "sample", "widgets", "create",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--location", "eu-central", "--message", "hi", "--cluster-id", "c1",
            "--wait");

        code.ShouldBe((int)ExitCode.Ok);

        // Request 2 is the second poll. By the time it was made, the first poll's line had already
        // been written — that is the whole assertion.
        seenBeforeEachRequest[2].ShouldContain("namespace created");
        seenBeforeEachRequest[2].ShouldNotContain("1 of 2 replicas ready");

        seenBeforeEachRequest[3].ShouldContain("1 of 2 replicas ready");
        seenBeforeEachRequest[3].ShouldNotContain("2 of 2 replicas ready");

        created.Stderr.ShouldContain("2 of 2 replicas ready");
        created.Stderr.ShouldContain("90%");
    }

    [Fact]
    public async Task NoWaitReturnsTheOperationIdWithoutPolling() {
        var transport = new ScriptedTransport((_, index) => index == 0
            ? Responses.Accepted("https://api.cybercloud.io/operations/op-77")
            : throw new ShouldAssertException("--no-wait polled the operation."));

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "sample", "widgets", "create",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--location", "eu-central", "--message", "hi", "--cluster-id", "c1",
            "--no-wait", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);
        transport.RequestCount.ShouldBe(1);

        using var document = host.StdoutAsJson();
        document.RootElement.GetProperty("operationId").GetString().ShouldBe("op-77");
        document.RootElement.GetProperty("status").GetString().ShouldBe("Accepted");
    }

    [Fact]
    public async Task WaitAndNoWaitTogetherIsAUsageError() {
        using var host = TestHost.Create();

        var code = await host.RunAsync(
            "sample", "widgets", "delete",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--wait", "--no-wait");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("contradict");
    }

    [Fact]
    public async Task AFailedOperationIsAServerFailureRatherThanExitZero() {
        using var host = TestHost.Create(new ScriptedTransport((_, index) => index switch {
            0 => Responses.Accepted("https://api.cybercloud.io/operations/op-1"),
            _ => Responses.Json(HttpStatusCode.OK, """
                {"status":"Failed","error":{"code":"QuotaExceeded","message":"Subscription quota for 'vcpu' would be exceeded."}}
                """),
        }));

        var code = await host.RunAsync(
            "sample", "widgets", "delete",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t");

        // ⚠ The poll that reported the failure was itself a 200. Reading the status off the response
        // rather than off the operation would have made this exit 0 — a "successful" delete that
        // deleted nothing.
        code.ShouldBe((int)ExitCode.ServerError);
        host.Stderr.ShouldContain("QuotaExceeded");
    }

    /// <summary>
    ///     One poll's answer.
    /// </summary>
    /// <remarks>
    ///     ⚠ The array is cumulative, which is what the platform sends — docs/plan/10 § Long-running
    ///     operations, over HTTP, and <c>OperationStatus.Progress</c>'s own remarks. A test that sent
    ///     only the new entry each time would exercise a service this SDK does not have, and its
    ///     second and third lines would never be published.
    /// </remarks>
    static HttpResponseMessage Poll(int percent, params string[] entries)
        => Responses.Json(
            HttpStatusCode.OK,
            $$"""{"status":"Running","percentComplete":{{percent}},"progress":[{{string.Join(",", entries)}}]}""");

    static string Entry(int percent, string step, string message)
        => $$"""{"at":"2026-08-11T10:00:0{{percent / 30}}Z","step":"{{step}}","message":"{{message}}","percentComplete":{{percent}}}""";
}
