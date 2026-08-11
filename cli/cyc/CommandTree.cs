using System.CommandLine;
using CyberCloud.Cli.Commands;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli;

/// <summary>
///     Turns one api-version's verb tree into the <c>System.CommandLine</c> surface, and hangs the
///     hand-written commands beside it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Built at run time from a description embedded at build time.</b>
///         <see cref="VerbTreeCatalog" /> argues that decision; the short version is that
///         <c>--api-version</c> selects between trees and <c>CliEmitter</c> deliberately emits a
///         description rather than C#.
///     </para>
///     <para>
///         ⚠ <b>This used to cite <c>cyc extension add</c> "adding groups from an assembly load
///         context" as a third reason, and that mechanism cannot exist here.</b> A NativeAOT binary
///         cannot load a managed assembly — measured: every load path throws
///         <c>PlatformNotSupportedException</c>, and the call does not even compile in this project,
///         because <c>EnableAotAnalyzer</c> plus <c>TreatWarningsAsErrors</c> makes it
///         <c>error IL2026</c>. Recorded as a defect in docs/plan/21 § Extensions, which needs a
///         decision before anything is built. The two surviving reasons above still carry this
///         design on their own.
///     </para>
///     <para>
///         ⚠ <b>The alias table is generated and this file is the proof.</b> docs/plan/21 § Grammar
///         calls the alias table <i>"the only hand-maintained part of the CLI's surface"</i>. That is
///         no longer true and has not been since the registry grew <c>shortName</c>: aliases arrive in
///         the tree's <c>alias</c> members and are added by <see cref="Command.Aliases" /> below.
///         There is no alias list in this assembly, and a grep for one is the check. Reported as a
///         defect in docs/plan/21 § Grammar.
///     </para>
/// </remarks>
static class CommandTree {
    /// <summary>
    ///     The names the host owns, which no generated group may take.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The emitter declares no reserved names, so this list is the only thing standing
    ///     between a provider called <c>CyberCloud.Login</c> and a CLI where <c>cyc login</c> means two
    ///     things.</b> Checked rather than assumed — a collision fails loudly here instead of shadowing
    ///     sign-in at run time. Reported: the reserved list belongs in the generated tree, where the
    ///     generator can refuse the provider name instead.
    /// </remarks>
    static readonly string[] ReservedGroups = [
        "login", "logout", "account", "rest", "config", "completion", "complete", "extension", "version",
    ];

    /// <summary>Builds the whole command surface.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree for the selected api-version.</param>
    /// <exception cref="CycUsageException">A generated group collides with a host-owned command.</exception>
    public static RootCommand Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(tree);

        globals.AssertMatchesTree(tree);

        var root = new RootCommand(
            "cyc — the Cyber Cloud command-line interface. docs/plan/21 § `cyc`."
            + $"{Environment.NewLine}api-version {tree.ApiVersion}. Exit codes: 0 ok · 1 client · 2 usage · 3 auth · 4 server · 5 timeout.");

        foreach (var option in globals.All)
            root.Options.Add(option);

        foreach (var command in HostCommands.Build(host, globals, tree))
            root.Subcommands.Add(command);

        foreach (var group in tree.Groups.OrderBy(x => x.Key, StringComparer.Ordinal))
            root.Subcommands.Add(Group(host, globals, tree, group.Key, group.Value));

        return root;
    }

    static Command Group(CycHost host, GlobalOptions globals, VerbTreeDocument tree, string name, VerbTreeGroup group) {
        if (ReservedGroups.Contains(name, StringComparer.Ordinal))
            throw new CycUsageException(
                $"The verb tree names a group '{name}', which is a command cyc owns "
                + $"({string.Join(", ", ReservedGroups)}). The provider namespace has to change — a group "
                + "that shadows 'cyc login' is a CLI nobody can sign in to.");

        var command = new Command(name, group.Summary);

        foreach (var child in group.Commands.OrderBy(x => x.Key, StringComparer.Ordinal))
            command.Subcommands.Add(Resource(host, globals, tree, child.Key, child.Value));

        return command;
    }

    static Command Resource(CycHost host, GlobalOptions globals, VerbTreeDocument tree, string name, VerbTreeCommand resource) {
        var summary = resource.Deprecated ? $"{resource.Summary} ⚠ Deprecated." : resource.Summary;
        var command = new Command(name, summary);

        // The generated short form — `cyc sample widget show` for `widgets`. Nothing here decides
        // what it is; the registry did.
        if (resource.Alias is { Length: > 0 } alias && !string.Equals(alias, name, StringComparison.Ordinal))
            command.Aliases.Add(alias);

        foreach (var verb in resource.Verbs.OrderBy(x => x.Key, StringComparer.Ordinal))
            command.Subcommands.Add(Verb(host, globals, tree, verb.Value));

        return command;
    }

    static Command Verb(CycHost host, GlobalOptions globals, VerbTreeDocument tree, VerbTreeVerb verb) {
        var command = new Command(verb.Name, verb.Summary);
        var bindings = verb.Flags.Select(FlagBinding.Create).ToList();

        foreach (var binding in bindings)
            command.Options.Add(binding.Option);

        var waitOptions = WaitFor(verb);

        if (waitOptions is not null) {
            foreach (var option in waitOptions.Options)
                command.Options.Add(option);
        }

        command.SetAction((parse, cancellationToken) => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);

            return ResourceVerb.RunAsync(invocation, verb, bindings, waitOptions, parse, cancellationToken);
        });

        return command;
    }

    /// <summary>
    ///     Declares <c>--wait</c> and <c>--no-wait</c> when — and only when — the tree says the verb
    ///     is long-running.
    /// </summary>
    /// <remarks>
    ///     ⚠ Read off <c>waitFlags</c> rather than off <c>longRunning</c>, because the two are
    ///     separate members and the emitter could stop emitting the flags for a verb it still marks
    ///     long-running. The pair's names come from the tree too, so a rename there does not need one
    ///     here.
    /// </remarks>
    static WaitOptions? WaitFor(VerbTreeVerb verb) {
        if (verb.WaitFlags.Count != 2)
            return null;

        var wait = new Option<bool>(verb.WaitFlags[0]) {
            Description = "Wait for the operation to finish, streaming its progress to stderr. The default.",
        };

        var noWait = new Option<bool>(verb.WaitFlags[1]) {
            Description = "Return as soon as the operation is accepted, printing its id.",
        };

        return new WaitOptions(wait, noWait);
    }
}
