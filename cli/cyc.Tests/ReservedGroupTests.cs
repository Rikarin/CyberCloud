using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>CommandTree.ReservedGroups</c> — the names a generated group may not take.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The reservation was enforced but untested, and one of the names it protects has
///         nothing behind it.</b> <c>CommandTree.Build</c> refuses a tree whose group shadows a
///         host-owned command, but no test exercised that path, so the list could have been reordered
///         or emptied without a red test. <c>extension</c> is on the list for
///         <c>cyc extension add</c> (docs/plan/21 § Extensions), which is unimplemented — the
///         reservation is holding the name open, and that is exactly the kind of guard that rots
///         quietly.
///     </para>
///     <para>
///         ⚠ <b>The refusal happens at start-up, not at generation.</b> <c>CliEmitter</c> declares no
///         reserved names, so a provider called <c>CyberCloud.Extension</c> generates a tree that
///         embeds cleanly and then fails <i>every</i> <c>cyc</c> invocation, including
///         <c>cyc --help</c> — the throw is in <c>Group</c>, which runs while the root command is
///         built. <see cref="RefusingATreeCostsTheWholeCli" /> pins that blast radius so the cost of
///         leaving the check here is visible. <c>CommandTree</c>'s own remarks report it: the list
///         belongs in the generated tree, where the generator can refuse the provider name instead.
///     </para>
/// </remarks>
public sealed class ReservedGroupTests {
    /// <summary>Every name the host owns. Kept in step with <c>CommandTree.ReservedGroups</c> by <see cref="TheReservedListIsExactlyTheseNames" />.</summary>
    static readonly string[] ReservedNames = [
        "login", "logout", "account", "rest", "config", "completion", "complete", "extension", "version",
    ];

    /// <summary>The same names, as xUnit theory rows.</summary>
    public static TheoryData<string> Reserved => [.. ReservedNames];

    [Theory]
    [MemberData(nameof(Reserved))]
    public void RefusesAGeneratedGroupThatShadowsAHostCommand(string reserved) {
        using var test = TestHost.Create();

        var failure = Should.Throw<CycUsageException>(
            () => CommandTree.Build(test.Host, GlobalOptions.For(test.Host.Catalog), TreeWith(reserved)));

        // The message has to name the group and say what to do about it: the reader is a provider
        // author whose namespace is wrong, and "collision" alone does not tell them which one.
        failure.Message.ShouldContain($"'{reserved}'");
        failure.Message.ShouldContain("provider namespace");
    }

    /// <summary>
    ///     The positive control. Without it the theory above passes just as well against a
    ///     <c>Build</c> that threw on every tree it was handed.
    /// </summary>
    [Fact]
    public void BuildsAGroupThatShadowsNothing() {
        using var test = TestHost.Create();

        var root = CommandTree.Build(test.Host, GlobalOptions.For(test.Host.Catalog), TreeWith("widgetry"));

        root.Subcommands.Select(x => x.Name).ShouldContain("widgetry");
    }

    /// <summary>
    ///     ⚠ Pins the list itself. The theory above only proves the names it is given are refused; it
    ///     would stay green if <c>extension</c> were deleted from <c>ReservedGroups</c> and from
    ///     <see cref="Reserved" /> in the same edit. This reads the real list through a refusal
    ///     message — which prints it — so a name that leaves the host fails here.
    /// </summary>
    [Fact]
    public void TheReservedListIsExactlyTheseNames() {
        using var test = TestHost.Create();

        var failure = Should.Throw<CycUsageException>(
            () => CommandTree.Build(test.Host, GlobalOptions.For(test.Host.Catalog), TreeWith("login")));

        foreach (var name in ReservedNames)
            failure.Message.ShouldContain(name);
    }

    /// <summary>
    ///     ⚠ The cost of refusing at start-up rather than at generation: a colliding group takes down
    ///     <c>cyc --help</c>, not just its own verbs. Exit code 2 — docs/plan/21 § Decisions' usage
    ///     code — rather than a crash, but the CLI is unusable until the provider is renamed.
    /// </summary>
    [Fact]
    public async Task RefusingATreeCostsTheWholeCli() {
        using var test = TestHost.Create();

        var failure = Should.Throw<CycUsageException>(
            () => CommandTree.Build(test.Host, GlobalOptions.For(test.Host.Catalog), TreeWith("extension")));

        failure.Message.ShouldContain("extension");

        // The real embedded tree has no colliding group, so the same host still runs `--help`. This
        // is what a provider rename restores.
        (await test.RunAsync("--help")).ShouldBe((int)ExitCode.Ok);
    }

    /// <summary>
    ///     A tree carrying exactly one group, under the name given.
    /// </summary>
    /// <remarks>
    ///     No global flags: <c>GlobalOptions.AssertMatchesTree</c> only fails on a flag the host does
    ///     not implement, so an empty list is the shape that puts nothing between the test and the
    ///     reservation it is testing.
    /// </remarks>
    /// <param name="group">The group name.</param>
    static VerbTreeDocument TreeWith(string group) => new() {
        Format = VerbTreeCatalog.SupportedFormat,
        ApiVersion = "2026-08-01",
        Groups = new Dictionary<string, VerbTreeGroup>(StringComparer.Ordinal) {
            [group] = new() { Name = group, Summary = "A group invented by this test." },
        },
    };
}
