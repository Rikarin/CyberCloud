using System.Diagnostics;

namespace CyberCloud.Sdk.Tests;

/// <summary>
///     <c>GetProgressAsync()</c> — docs/plan/21 § The .NET SDK: <i>"ours; Azure's SDK has no
///     equivalent … Azure's LROs expose no progress; ours do and the SDK should not hide it."</i>
/// </summary>
public sealed class ProgressStreamsIncrementallyTests {
    /// <summary>
    ///     ⚠ <b>The failure this test exists to catch is a "streaming" API that batches.</b> Three
    ///     progress entries arrive over three polls; the consumer must see each one <i>before</i> the
    ///     poll that carries the next has happened. Asserting only that all three arrive would pass
    ///     against an implementation that waited for the operation to finish and then handed over a
    ///     list — which is exactly the API docs/plan/21 says we must not ship.
    /// </summary>
    [Fact]
    public async Task Entries_surface_as_each_poll_returns_them_not_in_a_batch_at_the_end() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            1 => Responses.Operation("Running", [("etcd", "etcd cluster ready", 20)]),
            2 => Responses.Operation("Running", [("etcd", "etcd cluster ready", 20), ("apiserver", "apiserver ready", 60)]),
            3 => Responses.Operation("Succeeded",
                [("etcd", "etcd cluster ready", 20), ("apiserver", "apiserver ready", 60), ("ready", "cluster ready", 100)]),
            _ => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        // The request count observed at the moment each entry was yielded. If the entries were
        // batched, all three would be observed at the same (final) count.
        var observed = new List<(string Step, int RequestsSoFar)>();

        await foreach (var progress in operation.GetProgressAsync(Cancel.Token))
            observed.Add((progress.Step, transport.RequestCount));

        observed.Select(x => x.Step).ShouldBe(["etcd", "apiserver", "ready"]);

        // 1 = the PUT, then one poll per entry — so each entry is observed at a strictly higher request
        // count than the one before, which is the streaming claim. A batching implementation would
        // report the same (final) count for all three.
        //
        // The third is 5 rather than 4 because the poll that carried it also reported Succeeded, and
        // that same poll goes on to GET the resource — docs/plan/10 § Long-running operations, over
        // HTTP: "→ 200 { "status": "Succeeded" } → then GET the resource".
        observed[0].RequestsSoFar.ShouldBe(2);
        observed[1].RequestsSoFar.ShouldBe(3);
        observed[2].RequestsSoFar.ShouldBe(5);
    }

    /// <summary>A late subscriber gets the history rather than nothing.</summary>
    [Fact]
    public async Task Enumerating_after_completion_replays_every_entry() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            1 => Responses.Operation("Succeeded", [("etcd", "ready", 100)]),
            _ => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Completed, "main", TestClient.SampleData(), Cancel.Token);

        operation.HasCompleted.ShouldBeTrue();

        var replayed = new List<OperationProgress>();

        await foreach (var progress in operation.GetProgressAsync(Cancel.Token))
            replayed.Add(progress);

        replayed.Count.ShouldBe(1);
        replayed[0].Step.ShouldBe("etcd");
    }
}

/// <summary><see cref="WaitUntil" /> — both members are in docs/plan/21 § The .NET SDK's example.</summary>
public sealed class WaitUntilTests {
    [Fact]
    public async Task Started_returns_before_the_operation_has_completed() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            _ => Responses.Operation("Succeeded"),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        operation.HasCompleted.ShouldBeFalse();
        operation.HasValue.ShouldBeFalse();

        // ⚠ Exactly one request — the PUT. WaitUntil.Started must not have polled even once, or the
        // caller who wanted to render progress has already missed the first entries.
        transport.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Completed_does_not_return_until_the_operation_has_completed() {
        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            1 => Responses.Operation("Running", [("etcd", "starting", 10)]),
            2 => Responses.Operation("Succeeded", [("etcd", "starting", 10), ("ready", "done", 100)]),
            _ => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Completed, "main", TestClient.SampleData(), Cancel.Token);

        operation.HasCompleted.ShouldBeTrue();
        operation.HasValue.ShouldBeTrue();
        operation.Value.Data.Location.ShouldBe("eu-central");

        // PUT, two polls, then the resource GET — docs/plan/10 § Long-running operations, over HTTP:
        // "→ 200 { "status": "Succeeded" } → then GET the resource".
        transport.RequestCount.ShouldBe(4);
        transport.Requests[3].Method.ShouldBe(HttpMethod.Get);
        transport.Requests[3].Uri.AbsolutePath.ShouldEndWith("/providers/CyberCloud.Sample/widgets/main");
    }

    [Fact]
    public async Task A_failed_operation_throws_from_the_wait_carrying_its_code_and_target() {
        var error = """{"code":"QuotaExceeded","message":"Subscription quota for 'vcpu' would be exceeded.","target":"/properties/sku"}""";

        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            _ => Responses.Operation("Failed", [("quota", "checking", 5)], error),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        var thrown = await Should.ThrowAsync<CyberCloudRequestFailedException>(
            async () => await operation.WaitForCompletionAsync(Cancel.Token));

        thrown.ErrorCode.ShouldBe("QuotaExceeded");
        thrown.Target.ShouldBe("/properties/sku");
    }

    /// <summary>
    ///     ⚠ The progress stream ends rather than throwing when the operation fails — see
    ///     <c>Operation&lt;T&gt;.GetProgressAsync</c>'s remarks. The docs/plan/21 example puts the
    ///     <c>await foreach</c> above the <c>WaitForCompletionAsync</c>, and a throwing enumerator
    ///     would mean the failure surfaced from the line that is only there to print.
    /// </summary>
    [Fact]
    public async Task A_failed_operation_ends_the_progress_stream_without_throwing() {
        var error = """{"code":"ProvisioningFailed","message":"The reconciler gave up."}""";

        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            _ => Responses.Operation("Failed", [("apply", "applying", 30)], error),
        });

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        var entries = new List<OperationProgress>();

        await foreach (var progress in operation.GetProgressAsync(Cancel.Token))
            entries.Add(progress);

        entries.Single().Step.ShouldBe("apply");
        operation.Status!.State.ShouldBe(OperationState.Failed);
    }
}

/// <summary>Cancellation — the poll stops promptly and leaves nothing running.</summary>
public sealed class OperationCancellationTests {
    /// <summary>
    ///     ⚠ <b>Two claims, and the second is the one that is easy to get wrong.</b> The wait must stop
    ///     quickly — proving the delay is a cancellable <c>Task.Delay</c> and not a sleep — and no
    ///     further poll may happen afterwards, which is the observable form of "no orphaned background
    ///     loop and no leaked timer". <see cref="OperationPoller" /> has no background task at all, so
    ///     there is nothing that could keep going; this test is what says so from outside.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_wait_stops_the_polling_promptly_and_nothing_polls_afterwards() {
        var transport = new ScriptedTransport((request, index) => index == 0
            ? Responses.Accepted(TestClient.OperationUri)
            : Responses.Operation("Running", [("etcd", "still going", 10)]));

        // A long interval, so the wait is asleep in Task.Delay when the token is cancelled. If the
        // delay were not cancellable this test would take 30 seconds and then fail.
        using var client = TestClient.Create(transport, configure: options => options.PollingInterval = TimeSpan.FromSeconds(30));

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var wait = operation.WaitForCompletionAsync(cancellation.Token).AsTask();

        await Task.Delay(50, Cancel.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        var after = transport.RequestCount;
        await Task.Delay(250, CancellationToken.None);

        transport.RequestCount.ShouldBe(after);
    }

    /// <summary>An already-cancelled token costs no request at all.</summary>
    [Fact]
    public async Task An_already_cancelled_token_issues_no_poll() {
        var transport = new ScriptedTransport((request, index) => Responses.Accepted(TestClient.OperationUri));

        using var client = TestClient.Create(transport);

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await operation.WaitForCompletionAsync(cancellation.Token));

        transport.RequestCount.ShouldBe(1);
    }
}
