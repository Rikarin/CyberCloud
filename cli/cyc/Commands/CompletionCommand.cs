using System.CommandLine;
using System.CommandLine.Completions;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc completion &lt;shell&gt;</c> and the hidden <c>cyc complete</c> it installs — docs/plan/21
///     § Decisions: <i>"Completion | bash, zsh, fish, pwsh — generated."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two halves, and only one of them is a script.</b> The scripts below are four short
///         shims that hand the current words back to <c>cyc complete</c>; every suggestion — the
///         groups, the resource types, their aliases, the verbs, the flags and a flag's
///         <c>choices</c> — is computed from the verb tree at the moment of the keystroke. A generated
///         script that listed the verbs would be a snapshot: correct until the day the tree changed,
///         and silently wrong after it.
///     </para>
///     <para>
///         ⚠ <b>That is also what makes <c>--api-version</c> completable.</b> The same binary knows
///         which trees it carries, so completing an older api-version's flags is the same code path as
///         completing the newest.
///     </para>
/// </remarks>
static class CompletionCommand {
    /// <summary>The shells with a shim.</summary>
    public static IReadOnlyList<string> Shells { get; } = ["bash", "zsh", "fish", "pwsh"];

    /// <summary>Builds <c>cyc completion</c>.</summary>
    /// <param name="host">The host.</param>
    public static Command Build(CycHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var shell = new Argument<string>("shell") { Description = $"One of {string.Join(", ", Shells)}." };
        shell.AcceptOnlyFromAmong([.. Shells]);

        var command = new Command("completion", "Print a shell completion script. Add it to your shell's start-up file.") { shell };

        command.SetAction(parse => {
            host.Console.Out.WriteLine(Script(parse.GetRequiredValue(shell)));
            host.Console.Out.Flush();

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     Builds the hidden <c>cyc complete</c> the shims call.
    /// </summary>
    /// <param name="root">A function producing the root command — deferred, because it is what this command is added to.</param>
    /// <param name="host">The host.</param>
    public static Command BuildResolver(Func<RootCommand> root, CycHost host) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(host);

        var words = new Argument<string[]>("words") {
            Description = "The command line typed so far, after the executable name.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("complete", "Print completions for a partial command line. Called by the shell shims.") {
            words,
        };

        command.Hidden = true;

        command.SetAction(parse => {
            foreach (var completion in Complete(root(), parse.GetValue(words) ?? []))
                host.Console.Out.WriteLine(completion);

            host.Console.Out.Flush();

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     The completions for a partial command line.
    /// </summary>
    /// <param name="root">The command surface, built from the verb tree.</param>
    /// <param name="words">The words typed so far, the partial one last.</param>
    /// <remarks>
    ///     ⚠ <b>The value branch is here because <c>System.CommandLine</c> 2.0.10 does not take
    ///     it.</b> <see cref="ParseResult.GetCompletions" /> on <c>… create --tier ⎵</c> answers with
    ///     the <i>sibling flag names</i> — <c>--allowed-cidrs</c>, <c>--api-version</c>, … — rather
    ///     than with <c>free basic standard premium</c>. Measured against 2.0.10, not assumed. That
    ///     would throw away the most useful completion the platform has: <c>CliEmitter</c> carries a
    ///     schema's closed set into the tree's <c>choices</c> precisely so a shell can offer it. So
    ///     when the word before the cursor names an option, its own completion sources are asked
    ///     directly — which is still the library's data, through
    ///     <see cref="Symbol.GetCompletions" />, rather than a second list.
    /// </remarks>
    internal static IEnumerable<string> Complete(RootCommand root, IReadOnlyList<string> words) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(words);

        var parse = root.Parse(words);
        var context = parse.GetCompletionContext();

        // `--tier ⎵` and `--tier fr⎵`: the option is the word before the cursor.
        if (words.Count >= 2 && TakesAValue(parse, words[^2]) is { } pending)
            return Values(pending, context, words[^1]);

        // `--tier⎵` with no trailing word, which is what a shell that does not append an empty token
        // sends.
        if (words.Count >= 1 && TakesAValue(parse, words[^1]) is { } named)
            return Values(named, context, string.Empty);

        return parse.GetCompletions().Select(x => x.Label);
    }

    static IEnumerable<string> Values(Option option, CompletionContext context, string prefix)
        => option
            .GetCompletions(context)
            .Select(x => x.Label)
            .Where(x => x.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    ///     The option a word names, if it is one that takes a value.
    /// </summary>
    /// <remarks>
    ///     Walks up from the command the parse settled on, so a recursive global such as
    ///     <c>--api-version</c> is found from anywhere in the tree.
    /// </remarks>
    static Option? TakesAValue(ParseResult parse, string word) {
        if (!word.StartsWith('-'))
            return null;

        for (var command = parse.CommandResult.Command; command is not null; command = command.Parents.OfType<Command>().FirstOrDefault()) {
            foreach (var option in command.Options) {
                if (option.Arity.MaximumNumberOfValues == 0)
                    continue;

                if (string.Equals(option.Name, word, StringComparison.Ordinal) || option.Aliases.Contains(word, StringComparer.Ordinal))
                    return option;
            }
        }

        return null;
    }

    /// <summary>The shim for one shell.</summary>
    /// <param name="shell">The shell's name.</param>
    /// <exception cref="CycUsageException">The shell is not one of <see cref="Shells" />.</exception>
    public static string Script(string shell)
        => shell switch {
            "bash" => Bash,
            "zsh" => Zsh,
            "fish" => Fish,
            "pwsh" => PowerShell,
            _ => throw new CycUsageException($"'{shell}' has no completion script. Available: {string.Join(", ", Shells)}."),
        };

    const string Bash = """
        # cyc completion for bash. Add to ~/.bashrc:  source <(cyc completion bash)
        _cyc_complete() {
            local words
            words=("${COMP_WORDS[@]:1:$COMP_CWORD}")
            COMPREPLY=($(cyc complete -- "${words[@]}" 2>/dev/null))
        }
        complete -F _cyc_complete cyc
        """;

    const string Zsh = """
        # cyc completion for zsh. Add to ~/.zshrc:  source <(cyc completion zsh)
        _cyc_complete() {
            local -a completions
            completions=(${(f)"$(cyc complete -- ${words[2,$CURRENT]} 2>/dev/null)"})
            compadd -a completions
        }
        compdef _cyc_complete cyc
        """;

    const string Fish = """
        # cyc completion for fish. Add to ~/.config/fish/config.fish:  cyc completion fish | source
        function __cyc_complete
            set -l tokens (commandline -opc) (commandline -ct)
            cyc complete -- $tokens[2..-1] 2>/dev/null
        end
        complete -c cyc -f -a '(__cyc_complete)'
        """;

    const string PowerShell = """
        # cyc completion for PowerShell. Add to $PROFILE:  cyc completion pwsh | Out-String | Invoke-Expression
        Register-ArgumentCompleter -Native -CommandName cyc -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $words = $commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() }
            cyc complete -- @words 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;
}
