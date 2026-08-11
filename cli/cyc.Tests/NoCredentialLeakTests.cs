using CyberCloud.Cli.Execution;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     No credential material reaches disk or a <c>--verbose</c> trace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The structural half of this guarantee is that <c>cyc</c> never holds the token.</b>
///         <c>BearerTokenHandler</c> attaches <c>Authorization</c> inside the SDK's pipeline, below
///         anything the CLI can see, and the SDK owns the keychain-backed cache. So the tests below
///         assert two things a reviewer can check: a sentinel token never appears in any stream, and
///         the CLI writes no cache of its own — docs/plan/21 § Decisions: <i>"Never a plaintext file —
///         that is how CI credentials leak into container images."</i>
///     </para>
///     <para>
///         ⚠ The sentinel is the token <see cref="TestHost" />'s credential hands out, so a leak is a
///         string match rather than a judgement.
///     </para>
/// </remarks>
public sealed class NoCredentialLeakTests {
    [Fact]
    public async Task VerboseNeverPrintsTheToken() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1"}""")));

        var code = await host.RunAsync(
            "sample", "widgets", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--verbose");

        code.ShouldBe((int)ExitCode.Ok);

        // The trace is there — this is not passing because nothing was printed.
        host.Stderr.ShouldContain("GET https://api.cybercloud.io/tenants/t/");
        host.Stderr.ShouldNotContain(TestHost.FixedToken);
        host.Stdout.ShouldNotContain(TestHost.FixedToken);
    }

    [Fact]
    public async Task VerboseNeverPrintsTheTokenWhenTheRequestFails() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Error(HttpStatusCode.Unauthorized, "Unauthorized", "The token has expired.")));

        await host.RunAsync(
            "sample", "widgets", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--verbose");

        host.Stderr.ShouldNotContain(TestHost.FixedToken);
    }

    [Fact]
    public async Task NoTokenCacheIsWrittenByTheCli() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1"}""")));

        await host.RunAsync(
            "sample", "widgets", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t");

        // ⚠ The state directory is the CLI's whole footprint on disk. The only file a command may
        // leave there is `config`, which CycConfigFile refuses to put a credential in, and the
        // update-check stamp.
        var written = Directory.Exists(host.StateDirectory)
            ? Directory.GetFiles(host.StateDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList()
            : [];

        written.ShouldAllBe(name => name == "config" || name == "update-check");

        foreach (var file in Directory.GetFiles(host.StateDirectory, "*", SearchOption.AllDirectories))
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldNotContain(TestHost.FixedToken);
    }

    [Fact]
    public void TheConfigFileRefusesCredentialShapedKeys() {
        var file = Configuration.CycConfigFile.Parse("[default]\nsubscription = s\n");

        foreach (var key in new[] { "token", "client-secret", "password", "api-key", "credential" }) {
            var failure = Should.Throw<CycUsageException>(() => file.Set("default", key, "value"));

            failure.Message.ShouldContain("plaintext");
        }

        // The ordinary settings still work — the guard is about credential-shaped names, not about
        // making the file read-only.
        file.Set("default", "endpoint", "https://api.lab.internal/").Value("default", "endpoint")
            .ShouldBe("https://api.lab.internal/");
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("x-refresh-token")]
    [InlineData("Some-Secret-Header")]
    public void SecretHeadersAreRedacted(string header) {
        Redaction.Header(header, "super-secret").ShouldNotContain("super-secret");
        Redaction.Header(header, "super-secret").ShouldContain(Redaction.Placeholder);
    }

    [Fact]
    public void OrdinaryHeadersAreNot() {
        // ⚠ The point of --verbose is to show a header nobody anticipated, so redaction is by pattern
        // rather than by allow-list. A header the platform invents next month is printed.
        Redaction.Header("x-cybercloud-request-id", "req-1234").ShouldContain("req-1234");
        Redaction.Header("Retry-After", "10").ShouldContain("10");
    }

    [Fact]
    public void CredentialsInAQueryStringAreRedacted() {
        var url = Redaction.Url(new Uri("https://api.cybercloud.io/x?api-version=2026-08-01&access_token=abc"));

        url.ShouldContain("api-version=2026-08-01");
        url.ShouldNotContain("abc");
    }

    [Fact]
    public async Task ARestCallCannotSetItsOwnAuthorizationHeader() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}")));

        var code = await host.RunAsync("rest", "--uri", "/tenants/t/subscriptions", "--header", "Authorization=Bearer x");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("authenticated by the SDK's pipeline");
    }

    [Fact]
    public async Task ARestCallWillNotSendTheTokenToAnotherHost() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}")));

        var code = await host.RunAsync("rest", "--uri", "https://example.com/steal");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("example.com");
    }
}
