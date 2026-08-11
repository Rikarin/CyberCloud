using System.CommandLine;

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
            var typed = parse.GetValue(words) ?? [];

            foreach (var completion in root().Parse(typed).GetCompletions())
                host.Console.Out.WriteLine(completion.Label);

            host.Console.Out.Flush();

            return (int)ExitCode.Ok;
        });

        return command;
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
