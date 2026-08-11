using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     An unknown api-version, group, verb or flag fails with a message naming what <i>is</i>
///     available — never a stack trace.
/// </summary>
/// <remarks>
///     ⚠ <b>Every one of these is a question the binary can answer out of the tree it is already
///     holding.</b> "Unknown api-version 2025-01-01" without the list turns a typo into a support
///     conversation, and a stack trace turns it into a bug report. The exit code is 2 in every case
///     and nothing is sent.
/// </remarks>
public sealed class UnknownSurfaceTests {
    [Fact]
    public async Task AnUnknownApiVersionNamesTheOnesThisBuildCarries() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("sample", "widgets", "show", "--api-version", "2019-01-01");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("2019-01-01");
        host.Stderr.ShouldContain(TestHost.Catalog().Newest);
        host.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task AnUnknownGroupSuggestsWhatExists() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("smaple", "widgets", "show");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task AnUnknownVerbIsAUsageError() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("sample", "widgets", "frobnicate");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task AQueryThatDoesNotParseNamesTheOffset() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}")));

        var code = await host.RunAsync(
            "sample", "widgets", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--query", "properties[?tier ==");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("--query could not be parsed at offset");
    }

    [Fact]
    public async Task AnUnknownFunctionNamesTheOnesThatExist() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}")));

        var code = await host.RunAsync(
            "sample", "widgets", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--query", "reverse(@)");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("sort_by");
    }

    [Fact]
    public void AVerbTreeWithAFlagTypeThisBuildCannotAcceptSaysSo() {
        // ⚠ Forward compatibility, from the host's side. A tree emitted by a newer generator may
        // describe a flag shape this binary has never seen; the answer is "upgrade cyc", not a cast
        // exception halfway through building the command tree.
        var tree = VerbTreeCatalog.Parse("""
            {
              "format": "1",
              "apiVersion": "2026-08-01",
              "groups": {
                "sample": {
                  "name": "sample",
                  "commands": {
                    "widgets": {
                      "name": "widgets",
                      "verbs": {
                        "show": {
                          "name": "show", "method": "GET", "path": "/x",
                          "flags": [{ "name": "--weird", "type": "duration", "required": false }]
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        using var host = TestHost.Create();
        var globals = GlobalOptions.For(VerbTreeCatalog.Of(tree));

        var failure = Should.Throw<CycUsageException>(() => CommandTree.Build(host.Host, globals, tree));

        failure.Message.ShouldContain("duration");
        failure.Message.ShouldContain("Upgrade cyc");
    }

    [Fact]
    public void AGeneratedGroupMayNotShadowAHostCommand() {
        // ⚠ The emitter declares no reserved names. A provider namespace ending in `.Login` would
        // otherwise produce a `cyc login` that lists widgets.
        var tree = VerbTreeCatalog.Parse("""
            {
              "format": "1",
              "apiVersion": "2026-08-01",
              "groups": { "login": { "name": "login", "commands": {} } }
            }
            """);

        using var host = TestHost.Create();
        var globals = GlobalOptions.For(VerbTreeCatalog.Of(tree));

        var failure = Should.Throw<CycUsageException>(() => CommandTree.Build(host.Host, globals, tree));

        failure.Message.ShouldContain("cyc owns");
    }

    [Fact]
    public void AGlobalFlagTheHostDoesNotImplementIsRefused() {
        var tree = VerbTreeCatalog.Parse("""
            {
              "format": "1",
              "apiVersion": "2026-08-01",
              "globalFlags": [{ "name": "--colour", "type": "string" }],
              "groups": {}
            }
            """);

        using var host = TestHost.Create();
        var globals = GlobalOptions.For(VerbTreeCatalog.Of(tree));

        Should.Throw<CycUsageException>(() => CommandTree.Build(host.Host, globals, tree))
            .Message.ShouldContain("--colour");
    }
}
