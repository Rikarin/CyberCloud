using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.Extensions;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc extension</c> and the out-of-process invocation path — docs/plan/21 § Extensions.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The trust boundary is what most of these assert, and it is a boundary rather than a
///         feature.</b> <c>cyc</c> runs only what <c>cyc extension add</c> installed into
///         <c>~/.cyc/extensions</c>; <c>PATH</c> is never searched, because a writable directory on
///         <c>PATH</c> would otherwise be arbitrary code execution under the user's cloud credentials.
///         <see cref="PathIsNeverSearchedForExtensions" /> is the test that says so out loud.
///     </para>
///     <para>
///         ⚠ <b>Only one test starts a real process</b> — <see cref="AScriptRunsForRealWithItsArguments" />.
///         Everything else drives the resolution, the integrity check and the environment through
///         <c>CycHost.LaunchExtension</c>, which is the only way to read what a child <i>would</i> have
///         been handed and prove a token is not in it.
///     </para>
/// </remarks>
public sealed class ExtensionTests : IDisposable {
    readonly string sources = Path.Combine(Path.GetTempPath(), "cyc-extension-tests", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() {
        try {
            Directory.Delete(sources, recursive: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) {
            // A leftover temporary directory is not worth failing a passing test over.
        }
    }

    // ─── the trust boundary ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PathIsNeverSearchedForExtensions() {
        // ⚠ The whole decision in one test. `git` and `kubectl` would run this file; cyc does not,
        // because `cyc` carries a cloud credential and `git` does not. A green assertion here is a
        // promise that no writable PATH directory is an execution vector.
        using var host = TestHost.Create(environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["PATH"] = OnPath(),
        });

        var code = await host.RunAsync("onpath", "--whatever");

        code.ShouldBe((int)ExitCode.Usage);
        host.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnExecutableNobodyInstalledIsRefusedRatherThanRun() {
        // ⚠ docs/plan/23 calls out the step that inspects nothing and reports success. Here the
        // opposite risk: a file that looks installed to whoever copied it in. Running it would move
        // the trust boundary from `cyc extension add` to "can write a directory"; saying nothing
        // would send the reader hunting for a PATH mechanism that does not exist.
        using var host = TestHost.Create();

        Directory.CreateDirectory(host.ExtensionsDirectory);
        Script(Path.Combine(host.ExtensionsDirectory, "cyc-stray"));

        var code = await host.RunAsync("stray");

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain("cyc-stray");
        host.Stderr.ShouldContain("no extension named 'stray' is installed");
        host.Stderr.ShouldContain("cyc extension add");
        host.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task InstallingSaysWhatIsBeingHandedOver() {
        using var host = TestHost.Create();

        (await Install(host, "probe")).ShouldBe((int)ExitCode.Ok);

        // krew prints "not audited for security" and gh says extensions are "not verified, signed, or
        // endorsed by GitHub". Neither names the asset. This does.
        host.Stderr.ShouldContain("runs");
        host.Stderr.ShouldContain("as you");
        host.Stderr.ShouldContain("cyc account get-access-token");
    }

    [Fact]
    public async Task AnExtensionInADirectoryOthersCanWriteIsRefused() {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix file modes only. ExtensionStore's remarks record that the Windows ACL case is unchecked.");

        using var host = TestHost.Create();

        await Install(host, "probe");

        AllowOthersToWrite(host.ExtensionsDirectory);

        var code = await host.RunAsync("probe");

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain("writable");
        host.Launches.ShouldBeEmpty();
    }

    // ─── credentials across the process boundary ───────────────────────────────────────────────

    [Fact]
    public async Task NoAccessTokenCrossesTheProcessBoundary() {
        // ⚠ docs/plan/21 § Extensions: a token in the child's environment is a token in
        // /proc/<pid>/environ, in every grandchild, and in any CI step that prints its environment.
        // The extension asks cyc for one instead.
        using var host = TestHost.Create();

        await Install(host, "probe");
        (await host.RunAsync("probe")).ShouldBe((int)ExitCode.Ok);

        var launch = host.Launches.ShouldHaveSingleItem();

        foreach (var variable in launch.Environment) {
            // The same predicate that keeps token material out of ~/.cyc/config, reused so that a
            // CYC_ACCESS_TOKEN added to ExtensionLauncher.EnvironmentFor turns this red.
            CycConfigFile.LooksLikeCredential(variable.Key).ShouldBeFalse($"'{variable.Key}' is credential-shaped.");
            variable.Value.ShouldNotContain(TestHost.FixedToken);
        }
    }

    [Fact]
    public async Task TheChildIsToldWhichCycToAskForAToken() {
        // ⚠ The path, not the word `cyc`. An extension that re-resolved the name through PATH would
        // hand the token question to whichever binary answered — the same hole, from the other side.
        using var host = TestHost.Create();

        await Install(host, "probe");
        await host.RunAsync("probe");

        var launch = host.Launches.ShouldHaveSingleItem();

        launch.Environment[ExtensionLauncher.ExecutableVariable].ShouldBe(TestHost.FixedExecutablePath);
        launch.Environment[ExtensionLauncher.NameVariable].ShouldBe("probe");
    }

    [Fact]
    public async Task TheChildInheritsTheResolvedContextAndNotJustTheCommandLine() {
        using var host = TestHost.Create(config: """
            default = work

            [work]
            subscription = sub-42
            tenant       = contoso
            endpoint     = https://api.lab.internal/
            """);

        await Install(host, "probe");
        await host.RunAsync("--output", "json", "probe");

        var launch = host.Launches.ShouldHaveSingleItem();

        // Resolved through the profile, so the extension sees what `cyc` would have used — not only
        // what appeared on the command line.
        launch.Environment[CycSettings.VariableFor("profile")].ShouldBe("work");
        launch.Environment[CycSettings.VariableFor("subscription")].ShouldBe("sub-42");
        launch.Environment[CycSettings.VariableFor("tenant")].ShouldBe("contoso");
        launch.Environment[CycSettings.VariableFor("endpoint")].ShouldBe("https://api.lab.internal/");
        launch.Environment[CycSettings.VariableFor("output")].ShouldBe("json");
        launch.Environment[CycSettings.VariableFor("api-version")].ShouldBe(TestHost.Catalog().Newest);
    }

    // ─── a verb that shadows a real one ────────────────────────────────────────────────────────

    /// <summary>The nine names <c>CommandTree.ReservedGroups</c> holds, plus a group the embedded tree really carries.</summary>
    public static TheoryData<string> Shadowing => [
        "login", "logout", "account", "rest", "config", "completion", "complete", "extension", "version", "sample",
    ];

    [Theory]
    [MemberData(nameof(Shadowing))]
    public async Task InstallingUnderAHostOrGeneratedNameIsRefused(string reserved) {
        using var host = TestHost.Create();

        var code = await host.RunAsync("extension", "add", "--source", Script(Path.Combine(sources, "cyc-x")), "--name", reserved);

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain($"'{reserved}'");
        host.Stderr.ShouldContain("could never run");
    }

    [Fact]
    public async Task AnExtensionSmuggledIntoTheIndexStillCannotShadowABuiltIn() {
        // ⚠ THE STRUCTURAL CLAIM, TESTED AGAINST A HOSTILE INDEX RATHER THAN AGAINST `add`.
        // `cyc extension add` refuses the name, but the index is a file a user can edit. Dispatch
        // runs only after System.CommandLine has already failed to match the verb, so `cyc login`
        // reaches LoginCommand however the index was written.
        using var host = TestHost.Create();

        Smuggle(host, "login");

        var code = await host.RunAsync("login", "--service-principal");

        // LoginCommand's own complaint about a half-given service principal — which proves the
        // built-in ran. What matters is that the extension did not.
        code.ShouldNotBe((int)ExitCode.Ok);
        host.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task AShadowingExtensionDoesNotCostTheWholeCli() {
        // ⚠ The asymmetry with CommandTree.ReservedGroups, which throws while the root command is
        // built and so takes `cyc --help` down with it (ReservedGroupTests.RefusingATreeCostsTheWholeCli).
        // That is the right cost for a generated tree, which is ours. It is the wrong cost entirely
        // for a name that came out of a directory a user can write: a file called `cyc-login` must
        // not be able to disable the CLI.
        using var host = TestHost.Create();

        Smuggle(host, "login");

        (await host.RunAsync("--help")).ShouldBe((int)ExitCode.Ok);
        (await host.RunAsync("version")).ShouldBe((int)ExitCode.Ok);
    }

    [Fact]
    public async Task AShadowedExtensionIsListedAsShadowedRatherThanAsWorking() {
        using var host = TestHost.Create();

        Smuggle(host, "login");

        (await host.RunAsync("extension", "list", "--output", "json")).ShouldBe((int)ExitCode.Ok);

        using var listed = host.StdoutAsJson();

        listed.RootElement[0].GetProperty("state").GetString().ShouldBe("shadowed");
        host.Stderr.ShouldContain("shadowed");
    }

    // ─── discovery that finds nothing, and discovery that finds something broken ───────────────

    [Fact]
    public async Task NothingInstalledIsAnEmptyListAndASuccess() {
        using var host = TestHost.Create();

        (await host.RunAsync("extension", "list", "--output", "json")).ShouldBe((int)ExitCode.Ok);

        using var listed = host.StdoutAsJson();

        listed.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        listed.RootElement.GetArrayLength().ShouldBe(0);
        host.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnreadableIndexIsAFailureAndNotAnEmptyList() {
        // ⚠ The failure this codebase keeps hitting: a step that inspects nothing and reports
        // success. "No extensions installed" and "I cannot tell which extensions are installed" are
        // different answers and must not share an exit code.
        using var host = TestHost.Create();

        Directory.CreateDirectory(host.ExtensionsDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(host.ExtensionsDirectory, ExtensionStore.IndexFileName),
            "{ this is not json",
            TestContext.Current.CancellationToken);

        var code = await host.RunAsync("extension", "list");

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain(ExtensionStore.IndexFileName);
        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMissingExecutableIsReportedRatherThanSkipped() {
        using var host = TestHost.Create();

        await Install(host, "probe");
        File.Delete(Path.Combine(host.ExtensionsDirectory, "cyc-probe"));

        (await host.RunAsync("probe")).ShouldBe((int)ExitCode.ClientError);
        host.Launches.ShouldBeEmpty();

        (await host.RunAsync("extension", "list", "--output", "json")).ShouldBe((int)ExitCode.Ok);

        host.Stdout.ShouldContain("missing");
        host.Stderr.ShouldContain("will not run");
    }

    [Fact]
    public async Task ABinaryReplacedAfterInstallIsRefused() {
        // ⚠ What the recorded hash buys, and no more. It catches a file swapped without the index
        // being rewritten — a package manager, a half-finished copy, a truncated download. It does
        // nothing against somebody who can write both files; ExtensionStore's remarks say so.
        using var host = TestHost.Create();

        await Install(host, "probe");

        await File.WriteAllTextAsync(
            Path.Combine(host.ExtensionsDirectory, "cyc-probe"),
            "#!/bin/sh\nexit 1\n",
            TestContext.Current.CancellationToken);

        var code = await host.RunAsync("probe");

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain("not the file that was installed");
        host.Launches.ShouldBeEmpty();
    }

    // ─── the invocation path ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EverythingAfterTheVerbGoesToTheChildUntouched() {
        using var host = TestHost.Create();

        await Install(host, "probe");
        await host.RunAsync("probe", "sub", "--flag", "value", "--output", "json", "--", "-x");

        var launch = host.Launches.ShouldHaveSingleItem();

        // Not reinterpreted, not reordered, and `--output json` after the verb is the extension's
        // business rather than cyc's.
        launch.Arguments.ShouldBe(["sub", "--flag", "value", "--output", "json", "--", "-x"]);
    }

    [Fact]
    public async Task GlobalFlagsBeforeTheVerbAreConsumedByCycAndNotForwarded() {
        using var host = TestHost.Create();

        await Install(host, "probe");
        await host.RunAsync("--output", "json", "--verbose", "probe", "go");

        var launch = host.Launches.ShouldHaveSingleItem();

        launch.Arguments.ShouldBe(["go"]);
        launch.Environment[CycSettings.VariableFor("output")].ShouldBe("json");
    }

    [Fact]
    public async Task TheChildsExitCodeComesBackUnchanged() {
        // ⚠ docs/plan/21 § Decisions' six codes are a contract for cyc's own commands. There is no
        // mapping from an arbitrary program's codes onto that table that does not throw information
        // away, so an extension owns its exit codes the way a `git-` subcommand does.
        using var host = TestHost.Create(launchExtension: (_, _) => Task.FromResult(42));

        await Install(host, "probe");

        (await host.RunAsync("probe")).ShouldBe(42);
    }

    [Fact]
    public async Task RemovingAnExtensionMakesTheVerbUnknownAgain() {
        using var host = TestHost.Create();

        await Install(host, "probe");
        (await host.RunAsync("probe")).ShouldBe((int)ExitCode.Ok);

        (await host.RunAsync("extension", "remove", "probe")).ShouldBe((int)ExitCode.Ok);

        File.Exists(Path.Combine(host.ExtensionsDirectory, "cyc-probe")).ShouldBeFalse();
        (await host.RunAsync("probe")).ShouldBe((int)ExitCode.Usage);
    }

    [Fact]
    public async Task RemovingSomethingThatIsNotInstalledSaysWhatIs() {
        using var host = TestHost.Create();

        await Install(host, "probe");

        var code = await host.RunAsync("extension", "remove", "prboe");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("probe");
    }

    [Fact]
    public async Task AUrlSourceIsRefusedWithTheReason() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("extension", "add", "--source", "https://example.invalid/cyc-thing");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("does not download");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("with/slash")]
    [InlineData("Upper")]
    [InlineData(".hidden")]
    [InlineData("trailing-")]
    public async Task ANameThatCouldEscapeTheDirectoryIsRefused(string name) {
        using var host = TestHost.Create();

        var code = await host.RunAsync("extension", "add", "--source", Script(Path.Combine(sources, "cyc-x")), "--name", name);

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("cannot be an extension name");
    }

    [Fact]
    public async Task AScriptRunsForRealWithItsArguments() {
        // ⚠ The one test that starts a process, so CycHost.LaunchExtension cannot drift away from
        // what a child actually receives. The script writes to a file rather than to stdout because
        // the real launcher inherits the process's streams — which is the behaviour under test.
        if (OperatingSystem.IsWindows())
            Assert.Skip("The probe is a /bin/sh script.");

        using var host = TestHost.Create(launchExtension: ExtensionLauncher.StartAsync);

        var report = Path.Combine(sources, "report.txt");

        Script(Path.Combine(sources, "cyc-probe"), """
            #!/bin/sh
            { echo "argv:$*"; env; } > "$1"
            exit 7
            """);

        (await Install(host, "probe")).ShouldBe((int)ExitCode.Ok);

        var code = await host.RunAsync("probe", report, "extra");

        code.ShouldBe(7);

        var written = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);

        written.ShouldContain($"argv:{report} extra");
        written.ShouldContain($"{ExtensionLauncher.NameVariable}=probe");
        written.ShouldContain($"{ExtensionLauncher.ExecutableVariable}={TestHost.FixedExecutablePath}");
        written.ShouldNotContain(TestHost.FixedToken);
    }

    // ─── the argument walk ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("probe")]
    [InlineData("--output json probe")]
    [InlineData("--output=json probe")]
    [InlineData("-o json probe")]
    [InlineData("--verbose probe")]
    [InlineData("--timeout 30 --verbose probe")]
    public void TheVerbIsFoundPastTheGlobalFlags(string line) {
        var arguments = line.Split(' ');
        var globals = GlobalOptions.For(TestHost.Catalog());

        var operand = ExtensionDispatch.FirstOperand(globals, arguments);

        operand.ShouldNotBeNull();
        operand.Value.Name.ShouldBe("probe");
        operand.Value.Index.ShouldBe(arguments.Length - 1);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--output json")]
    [InlineData("")]
    public void ACommandLineOfNothingButFlagsHasNoVerb(string line) {
        var arguments = line.Length == 0 ? [] : line.Split(' ');
        var globals = GlobalOptions.For(TestHost.Catalog());

        ExtensionDispatch.FirstOperand(globals, arguments).ShouldBeNull();
    }

    // ─── helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs the real <c>cyc extension add</c> against a throwaway script.</summary>
    async Task<int> Install(TestHost host, string name) {
        var source = Path.Combine(sources, ExtensionStore.FilePrefix + name);

        if (!File.Exists(source))
            Script(source);

        return await host.RunAsync("extension", "add", "--source", source);
    }

    /// <summary>Writes an executable script and returns its path.</summary>
    static string Script(string path, string body = "#!/bin/sh\nexit 0\n") {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    /// <summary>Makes a directory group- and world-writable — the hole the install-directory model exists to close.</summary>
    static void AllowOthersToWrite(string directory) {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
    }

    /// <summary>A directory holding <c>cyc-onpath</c>, as a hostile <c>PATH</c> entry would.</summary>
    string OnPath() {
        Script(Path.Combine(sources, "path", "cyc-onpath"));

        return Path.Combine(sources, "path");
    }

    /// <summary>
    ///     Writes an index entry by hand, bypassing <c>cyc extension add</c>'s refusals.
    /// </summary>
    /// <remarks>
    ///     ⚠ The index is a file a user can edit, so every claim that rests on <c>add</c> having
    ///     refused something has to be re-tested against an index that did not go through it.
    /// </remarks>
    void Smuggle(TestHost host, string name) {
        var file = Script(Path.Combine(host.ExtensionsDirectory, ExtensionStore.FilePrefix + name));

        var index = $$"""
            {
              "format": "{{ExtensionStore.SupportedFormat}}",
              "extensions": [
                {
                  "name": "{{name}}",
                  "sha256": "{{ExtensionStore.HashOf(file)}}",
                  "size": {{new FileInfo(file).Length}},
                  "source": "smuggled",
                  "installed": "2026-08-12T00:00:00+00:00"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(host.ExtensionsDirectory, ExtensionStore.IndexFileName), index);
    }
}
