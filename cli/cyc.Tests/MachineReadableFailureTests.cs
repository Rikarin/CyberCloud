namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>--output json</c> stays machine-parseable when the command fails.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the failure class that breaks scripts silently.</b> A CLI that prints "Error:
///     resource not found" into a stream something is running through <c>jq</c> does not fail the
///     script's error handling — it fails its <i>parser</i>, which surfaces three steps later as a
///     confusing message about a token at position 0. The rule is absolute: stdout carries a valid
///     JSON document or carries nothing at all, and every failure goes to stderr.
/// </remarks>
public sealed class MachineReadableFailureTests {
    static readonly string[] Show = [
        "sample", "widgets", "show",
        "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
        "--output", "json",
    ];

    [Fact]
    public async Task StdoutIsEmptyWhenTheRequestFails() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.NotFound, "ResourceNotFound", "No widget called 'w1' in 'prod'.")));

        var code = await host.RunAsync(Show);

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task StdoutIsEmptyWhenTheCommandLineIsWrong() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("sample", "widgets", "show", "--nope", "--output", "json");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task StdoutIsEmptyWhenTheApiVersionIsUnknown() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("sample", "widgets", "show", "--api-version", "2019-01-01", "--output", "json");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheErrorItselfIsJsonOnStderr() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.Conflict, "ResourceLocked", "A CanNotDelete lock is on 'prod'.")));

        await host.RunAsync(Show);

        // ⚠ Not decoration. A script that captures stderr to explain a failure should be able to read
        // the code rather than grep the prose, and the exit code is in the document too so a log with
        // only stderr in it is still self-contained.
        using var reported = JsonDocument.Parse(host.Stderr);
        var error = reported.RootElement.GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe("ResourceLocked");
        error.GetProperty("exitCode").GetInt32().ShouldBe((int)ExitCode.ClientError);
    }

    [Fact]
    public async Task ASucceedingCommandWritesOneDocumentAndNothingElse() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1","properties":{"tier":"free"}}""")));

        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.Ok);

        using var document = host.StdoutAsJson();
        document.RootElement.GetProperty("name").GetString().ShouldBe("w1");
    }

    [Fact]
    public async Task ProgressNeverReachesStdout() {
        using var host = TestHost.Create(new ScriptedTransport((_, index) => index switch {
            0 => Responses.Accepted("https://api.cybercloud.io/operations/op-1"),
            1 => Responses.Json(HttpStatusCode.OK, """
                {"status":"Running","percentComplete":40,
                 "progress":[{"at":"2026-08-11T10:00:00Z","step":"etcd","message":"etcd cluster ready"}]}
                """),
            2 => Responses.Json(HttpStatusCode.OK, """{"status":"Succeeded"}"""),
            _ => Responses.Json(HttpStatusCode.OK, """{"name":"w1"}"""),
        }));

        var code = await host.RunAsync(
            "sample", "widgets", "create",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--location", "eu-central", "--message", "hi", "--cluster-id", "c1",
            "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);

        // The progress line is on stderr; stdout is one document, still parseable.
        host.Stderr.ShouldContain("etcd cluster ready");

        using var document = host.StdoutAsJson();
        document.RootElement.GetProperty("name").GetString().ShouldBe("w1");
    }
}
