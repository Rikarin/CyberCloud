using System.Diagnostics;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     docs/plan/21 § Decisions: <i>"Exit codes | <c>0</c> ok · <c>1</c> client error · <c>2</c> usage
///     · <c>3</c> auth · <c>4</c> server · <c>5</c> timeout. Documented, stable, so CI can branch on
///     them."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>Each code is provoked by a condition that actually produces it, end to end.</b> Asserting
///     the enum's numbers would prove nothing: the promise CI relies on is that a <c>403</c> gives 3
///     and a <c>500</c> gives 4 <i>through the whole host</i>, so every test here runs a real command
///     line against a scripted platform and reads the process's answer.
/// </remarks>
public sealed class ExitCodeTests {
    static readonly string[] Show = [
        "sample", "widgets", "show",
        "--name", "w1", "--resource-group", "prod", "--subscription", "sub-1", "--tenant", "contoso",
    ];

    [Fact]
    public async Task ZeroWhenTheCallSucceeds() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1","location":"eu-central"}""")));

        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.Ok);
        host.Stdout.ShouldContain("w1");
    }

    [Fact]
    public async Task OneWhenThePlatformRefusesTheRequest() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.BadRequest, "InvalidResourceName", "'w1' is not a legal resource name.")));

        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain("InvalidResourceName");
    }

    [Fact]
    public async Task TwoWhenTheCommandLineIsWrong() {
        using var host = TestHost.Create();

        (await host.RunAsync("sample", "widgets", "show", "--nope", "x")).ShouldBe((int)ExitCode.Usage);
    }

    [Fact]
    public async Task TwoWhenARequiredFlagIsMissing() {
        using var host = TestHost.Create();

        // Nothing is sent: RefusingTransport would fail the test if anything were.
        (await host.RunAsync("sample", "widgets", "show", "--name", "w1")).ShouldBe((int)ExitCode.Usage);
    }

    [Fact]
    public async Task TwoWhenAClosedSetIsViolated() {
        using var host = TestHost.Create();

        var code = await host.RunAsync(
            "sample", "widgets", "create",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--location", "eu-central", "--message", "hi", "--cluster-id", "c1",
            "--tier", "gold");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("gold");
    }

    [Fact]
    public async Task ThreeWhenThePlatformRejectsTheCredential() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.Forbidden, "AuthorizationFailed", "The caller may not read this resource.")));

        // ⚠ 403 is exit 3, not exit 1. A script that retries on "client error" must not retry a
        // permission failure, and one that re-authenticates on exit 3 must be given the chance.
        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.Auth);
    }

    [Fact]
    public async Task ThreeWhenThereIsNoCredentialAtAll() {
        using var host = TestHost.Create(
            new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}")),
            credential: new UnavailableCredential("No sign-in is cached. Run 'cyc login'."));

        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.Auth);
        host.Stderr.ShouldContain("cyc login");
    }

    [Fact]
    public async Task FourWhenThePlatformFails() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.InternalServerError, "InternalError", "Something went wrong.")));

        (await host.RunAsync(Show)).ShouldBe((int)ExitCode.ServerError);
    }

    [Fact]
    public async Task FiveWhenTheDeadlinePasses() {
        // A transport that never answers until its token is cancelled — what a hung gateway looks
        // like from here. ⚠ The SDK disables HttpClient's own timeout on purpose, so the deadline
        // under test is genuinely --timeout and not a 100-second default.
        using var host = TestHost.Create(new StallingTransport());

        var code = await host.RunAsync([.. Show, "--timeout", "1"]);

        code.ShouldBe((int)ExitCode.Timeout);
        host.Stderr.ShouldContain("--timeout");
    }

    [Fact]
    public async Task FiveWhenTheOperationOutlivesTheDeadline() {
        var polls = 0;

        using var host = TestHost.Create(new ScriptedTransport((request, index) => {
            if (index == 0)
                return Responses.Accepted("https://api.cybercloud.io/operations/op-1");

            Interlocked.Increment(ref polls);

            // Runs forever. The deadline is what stops it.
            return Responses.Json(HttpStatusCode.OK, """{"status":"Running","percentComplete":10,"progress":[]}""");
        }));

        var code = await host.RunAsync(
            "sample", "widgets", "delete",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--timeout", "1");

        code.ShouldBe((int)ExitCode.Timeout);
        polls.ShouldBeGreaterThan(0);
    }

    /// <summary>A transport that answers only when the request is cancelled.</summary>
    sealed class StallingTransport : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new UnreachableException();
        }
    }
}
