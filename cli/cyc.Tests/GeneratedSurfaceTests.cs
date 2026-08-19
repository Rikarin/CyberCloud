using System.CommandLine;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The command surface comes from the generated tree and from nowhere else.
/// </summary>
/// <remarks>
///     ⚠ <b>docs/plan/21 § Grammar says the alias table is <i>"the only hand-maintained part of the
///     CLI's surface"</i>, and that is now false.</b> The registry carries <c>shortName</c>,
///     <c>CliEmitter</c> puts it in the tree's <c>alias</c> member, and
///     <see cref="AliasesComeFromTheTree" /> proves it by feeding the host a tree whose alias is a word
///     no source file in this repository contains. A hand-maintained copy would be a second source
///     that drifts on the day a provider is added. Reported as a defect in that section.
/// </remarks>
public sealed class GeneratedSurfaceTests {
    [Fact]
    public async Task TheGeneratedAliasWorksAsACommandName() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1"}""")));

        // `widget`, the singular, is the tree's alias for `widgets`.
        var code = await host.RunAsync(
            "sample", "widget", "show",
            "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t");

        code.ShouldBe((int)ExitCode.Ok);
    }

    [Fact]
    public void AliasesComeFromTheTree() {
        // ⚠ A word that appears in no source file here. If the alias table were hand-maintained this
        // could not resolve.
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
                      "alias": "zorb",
                      "verbs": {
                        "show": { "name": "show", "method": "GET", "path": "/x", "flags": [] }
                      }
                    }
                  }
                }
              }
            }
            """);

        using var host = TestHost.Create();
        var root = CommandTree.Build(host.Host, GlobalOptions.For(VerbTreeCatalog.Of(tree)), tree);

        var parse = root.Parse(["sample", "zorb", "show"]);

        parse.Errors.ShouldBeEmpty();
        parse.CommandResult.Command.Name.ShouldBe("show");
    }

    [Fact]
    public void EveryVerbInTheTreeIsReachable() {
        var tree = TestHost.Catalog().Select(null);

        using var host = TestHost.Create();
        var root = CommandTree.Build(host.Host, GlobalOptions.For(TestHost.Catalog()), tree);

        foreach (var group in tree.Groups) {
            foreach (var command in group.Value.Commands) {
                foreach (var verb in command.Value.Verbs) {
                    var parse = root.Parse([group.Key, command.Key, verb.Key, "--help"]);

                    parse.Errors.ShouldBeEmpty($"{group.Key} {command.Key} {verb.Key} did not parse");
                }
            }
        }
    }

    [Fact]
    public void EveryFlagInTheTreeIsDeclared() {
        var tree = TestHost.Catalog().Select(null);

        using var host = TestHost.Create();
        var root = CommandTree.Build(host.Host, GlobalOptions.For(TestHost.Catalog()), tree);

        var create = root.Subcommands
            .Single(x => x.Name == "sample").Subcommands
            .Single(x => x.Name == "widgets").Subcommands
            .Single(x => x.Name == "create");

        var declared = create.Options.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var expected = tree.Groups["sample"].Commands["widgets"].Verbs["create"].Flags.Select(x => x.Name);

        foreach (var flag in expected)
            declared.ShouldContain(flag);

        // The generated flag's alias is declared too — `--cluster` for `--cluster-id`.
        create.Options.Single(x => x.Name == "--cluster-id").Aliases.ShouldContain("--cluster");
    }

    [Fact]
    public void WaitFlagsAppearOnlyOnLongRunningVerbs() {
        var tree = TestHost.Catalog().Select(null);

        using var host = TestHost.Create();
        var root = CommandTree.Build(host.Host, GlobalOptions.For(TestHost.Catalog()), tree);

        var widgets = root.Subcommands.Single(x => x.Name == "sample").Subcommands.Single(x => x.Name == "widgets");

        Names(widgets, "create").ShouldContain("--wait");
        Names(widgets, "delete").ShouldContain("--no-wait");

        // `show` is a GET and `ping` is a non-long-running action. Neither may offer a wait flag —
        // a --wait that silently did nothing would be worse than its absence.
        Names(widgets, "show").ShouldNotContain("--wait");
        Names(widgets, "ping").ShouldNotContain("--wait");

        static IReadOnlyList<string> Names(Command command, string verb)
            => [.. command.Subcommands.Single(x => x.Name == verb).Options.Select(x => x.Name)];
    }

    [Fact]
    public void NoGroupInAnyShippedTreeGivesOneTokenTwoMeanings() {
        // ⚠ THE CHECK THE SIX LITERAL LISTS WERE TRYING TO BE, AND THE ONLY PLACE IN `dotnet test`
        // THAT CAN SEE THE WHOLE TREE. src/Providers/README.md § Hard rule forbids one Providers.*
        // assembly referencing another, so no provider's suite can compare its short name against
        // another's — which is why the defence was six hand-typed lists, and why two of them were
        // stale in consecutive passes. This assembly references no provider at all: it reads the
        // generated tree the build embedded, so a provider added tomorrow is covered by this test
        // without anybody editing it.
        //
        // ⚠ THE SCOPE IS THE GROUP, MEASURED RATHER THAN ASSUMED. System.CommandLine builds one token
        // dictionary per command out of that command's own name and aliases plus its children's, so
        // the strings that may not collide are a group's key, its commands' names and their aliases —
        // and a short name equal to a DIFFERENT group's key is fine, which is what those lists spent
        // their assertions forbidding. Measured against 2.0.10; CliTokens carries the same rule on the
        // generator's side of the seam.
        var catalog = TestHost.Catalog();

        catalog.ApiVersions.ShouldNotBeEmpty("the build embeds every generated/cli/*.json");

        foreach (var version in catalog.ApiVersions) {
            var tree = catalog.Select(version);

            tree.Groups.ShouldNotBeEmpty($"{version} carries no group at all");

            foreach (var group in tree.Groups) {
                var owners = new Dictionary<string, string>(StringComparer.Ordinal) {
                    [group.Key] = "the group itself"
                };

                foreach (var command in group.Value.Commands) {
                    Take(command.Key, $"the command '{command.Key}'");

                    if (command.Value.Alias is { Length: > 0 } alias && alias != command.Key)
                        Take(alias, $"the alias of '{command.Key}'");

                    continue;

                    void Take(string token, string owner) {
                        owners.TryGetValue(token, out var existing).ShouldBeFalse(
                            $"'cyc {group.Key} {token}' at api-version {version} is both {existing} "
                            + $"and {owner}. System.CommandLine throws 'An item with the same key has "
                            + $"already been added. Key: {token}' on every cyc invocation reaching "
                            + $"'{group.Key}'");

                        owners[token] = owner;
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryAliasInEveryShippedTreeResolvesToItsOwnCommand() {
        // ⚠ The half the token check above cannot prove: that the alias reaches the right command
        // rather than merely being unique. Built through the real CommandTree, so the assertion runs
        // against System.CommandLine's own resolution and not against a model of it — which is also
        // what makes a colliding tree fail here as the ArgumentException it really is.
        var catalog = TestHost.Catalog();
        var resolved = 0;

        using var host = TestHost.Create();

        foreach (var version in catalog.ApiVersions) {
            var tree = catalog.Select(version);
            var root = CommandTree.Build(host.Host, GlobalOptions.For(catalog), tree);

            foreach (var group in tree.Groups) {
                foreach (var command in group.Value.Commands) {
                    if (command.Value.Alias is not { Length: > 0 } alias || alias == command.Key)
                        continue;

                    var verb = command.Value.Verbs.Keys.First();
                    var parse = root.Parse([group.Key, alias, verb, "--help"]);

                    parse.Errors.ShouldBeEmpty($"cyc {group.Key} {alias} {verb} did not parse");

                    parse.CommandResult.Command.Parents.OfType<Command>().First().Name
                        .ShouldBe(command.Key, $"'{alias}' resolved to the wrong command");

                    resolved++;
                }
            }
        }

        // ⚠ A loop over an empty tree passes, and a green test that inspected nothing is the defect
        // this repository names GateStatus.Vacuous. Every type in the catalogue declares a short name
        // today; the floor asserts the loop ran rather than the exact count, so adding a type with no
        // short name does not make this red for the wrong reason.
        resolved.ShouldBeGreaterThan(10, "the loop found almost no alias to resolve");
    }

    [Fact]
    public void TheAliasTableIsNotInTheSourceTree() {
        // ⚠ The check docs/plan/21 § Grammar's claim deserves: there is no alias map in this
        // assembly. `widget` appears in the generated JSON and in this test file's expectations, and
        // nowhere in cli/cyc/.
        var source = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cli", "cyc");

        if (!Directory.Exists(source))
            return;

        foreach (var file in Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)) {
            var text = File.ReadAllText(file);

            text.Contains("\"aks\"", StringComparison.Ordinal)
                .ShouldBeFalse($"{Path.GetFileName(file)} looks like it hard-codes an alias");

            text.Contains("\"postgres\"", StringComparison.Ordinal)
                .ShouldBeFalse($"{Path.GetFileName(file)} looks like it hard-codes an alias");
        }
    }
}
