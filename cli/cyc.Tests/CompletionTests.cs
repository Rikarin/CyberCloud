using CyberCloud.Cli.Commands;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     docs/plan/21 § Decisions: <i>"Completion | bash, zsh, fish, pwsh — generated."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>The completions are computed from the verb tree at the moment of the keystroke, not
///     baked into the script.</b> The four scripts are shims that call <c>cyc complete</c>; a script
///     that listed the verbs would be a snapshot that goes wrong the day the tree changes, and the
///     tree changes every release.
/// </remarks>
public sealed class CompletionTests {
    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    public async Task EveryShellHasAScript(string shell) {
        using var host = TestHost.Create();

        (await host.RunAsync("completion", shell)).ShouldBe((int)ExitCode.Ok);

        host.Stdout.ShouldContain("cyc complete");

        // ⚠ No verb appears in the script itself. That is the difference between a shim and a
        // snapshot.
        host.Stdout.Contains("widgets", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [Fact]
    public void TheShellListAndTheScriptsAgree() {
        foreach (var shell in CompletionCommand.Shells)
            CompletionCommand.Script(shell).ShouldNotBeNullOrWhiteSpace();

        Should.Throw<CycUsageException>(() => CompletionCommand.Script("csh"))
            .Message.ShouldContain("bash, zsh, fish, pwsh");
    }

    [Fact]
    public async Task CompletingAGroupOffersTheGeneratedResourceTypes() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "sample", string.Empty);

        host.Stdout.ShouldContain("widgets");
    }

    [Fact]
    public async Task CompletingAVerbOffersItsGeneratedFlags() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "sample", "widgets", "create", "--");

        host.Stdout.ShouldContain("--location");
        host.Stdout.ShouldContain("--cluster-id");

        // The wait flags are offered on a long-running verb, from the tree's own waitFlags.
        host.Stdout.ShouldContain("--no-wait");
    }

    [Fact]
    public async Task CompletingAClosedSetOffersItsValues() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "sample", "widgets", "create", "--tier", string.Empty);

        // ⚠ The enum from the schema, carried through the tree's `choices`. This is the completion
        // that could not exist before the emitter cashed in the enum gap — and the one
        // System.CommandLine 2.0.10 does not produce on its own. See CompletionCommand § Complete.
        host.Stdout.ShouldContain("premium");
        host.Stdout.ShouldContain("free");
        host.Stdout.Contains("--allowed-cidrs", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task APartialValueIsFilteredByItsPrefix() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "sample", "widgets", "create", "--tier", "pre");

        host.Stdout.Trim().ShouldBe("premium");
    }

    [Fact]
    public async Task CompletingApiVersionOffersWhatTheBinaryCarries() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "--api-version", string.Empty);

        host.Stdout.ShouldContain(TestHost.Catalog().Newest);
    }

    [Fact]
    public async Task CompletingOutputOffersTheFiveFormats() {
        using var host = TestHost.Create();

        await host.RunAsync("complete", "--", "sample", "widgets", "show", "--output", string.Empty);

        foreach (var format in Output.OutputFormats.Names)
            host.Stdout.ShouldContain(format);
    }
}
