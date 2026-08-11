using System.CommandLine;
using CyberCloud.Cli.Commands;
using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.Output;

namespace CyberCloud.Cli;

/// <summary>
///     One run of <c>cyc</c>, from an argument array to an exit code.
/// </summary>
/// <remarks>
///     ⚠ <b>Everything <see cref="Program" /> does is here, and nothing here touches a process.</b>
///     The tests drive this type with a <see cref="CycHost" /> whose console is two
///     <see cref="StringWriter" />s and whose client is scripted, so the exit-code contract, the
///     stdout/stderr split and the failure messages are asserted rather than hoped for.
/// </remarks>
static class CycApplication {
    /// <summary>Runs one command line.</summary>
    /// <param name="host">The host.</param>
    /// <param name="arguments">The arguments, without the executable name.</param>
    /// <param name="cancellationToken">The token. Cancelling it is <see cref="ExitCode.Timeout" />.</param>
    public static async Task<int> RunAsync(CycHost host, string[] arguments, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(arguments);

        // ⚠ Before anything else, and deliberately outside the try below: the format decides how a
        // failure is rendered, so it has to be known before there is a failure to render. An
        // unparseable --output is itself a usage error, and Parse() throws one.
        var format = ParseFormatEarly(arguments);

        try {
            var globals = GlobalOptions.For(host.Catalog);
            var tree = host.Catalog.Select(GlobalOptions.PeekApiVersion(arguments));
            var root = CommandTree.Build(host, globals, tree);

            root.Subcommands.Add(CompletionCommand.BuildResolver(() => root, host));

            var parse = root.Parse(arguments);

            if (parse.Errors.Count > 0)
                return ErrorWriter.ReportParseErrors(host.Console, format, parse.Errors);

            var values = globals.Read(parse);

            AskAboutTelemetryOnce(host, values);

            using var deadline = Deadline(host, values, cancellationToken);

            var configuration = new InvocationConfiguration {
                Output = host.Console.Out,
                Error = host.Console.Error,
                // ⚠ Off. System.CommandLine's own handler prints a stack trace and returns 1, which
                // would make the exit-code table a suggestion. Every failure goes through
                // ErrorWriter instead.
                EnableDefaultExceptionHandler = false,
            };

            return await parse.InvokeAsync(configuration, deadline.Token).ConfigureAwait(false);
        } catch (Exception failure) {
            return ErrorWriter.Report(host.Console, format, failure, Verbose(arguments));
        } finally {
            host.Console.Flush();
        }
    }

    /// <summary>
    ///     Reads <c>--output</c> straight off the arguments.
    /// </summary>
    /// <remarks>
    ///     ⚠ The parser cannot answer this, because the thing that failed may be the parse itself —
    ///     or the api-version that decides which tree the parser is built from. A failure rendered in
    ///     the wrong format is the defect this exists to avoid: <c>cyc --output json … | jq</c> must
    ///     read a JSON error from stderr even when the command line never parsed.
    /// </remarks>
    static OutputFormat ParseFormatEarly(string[] arguments) {
        for (var i = 0; i < arguments.Length; i++) {
            var argument = arguments[i];

            if (argument.StartsWith("--output=", StringComparison.Ordinal))
                return Safe(argument["--output=".Length..]);

            if (argument.StartsWith("-o=", StringComparison.Ordinal))
                return Safe(argument["-o=".Length..]);

            if ((string.Equals(argument, "--output", StringComparison.Ordinal) || string.Equals(argument, "-o", StringComparison.Ordinal))
                && i + 1 < arguments.Length) {
                return Safe(arguments[i + 1]);
            }
        }

        return OutputFormat.Table;

        // An unrecognised value is still a usage error, but it is reported by the parser with a list
        // of what is allowed. Rendering that report must not itself throw.
        static OutputFormat Safe(string value) {
            try {
                return OutputFormats.Parse(value);
            } catch (CycUsageException) {
                return OutputFormat.Table;
            }
        }
    }

    static bool Verbose(string[] arguments) => arguments.Contains("--verbose", StringComparer.Ordinal);

    /// <summary>
    ///     The <c>--timeout</c> deadline, linked to the caller's token.
    /// </summary>
    /// <remarks>
    ///     ⚠ One deadline for the whole command, including the wait on a long-running operation.
    ///     docs/plan/21 § Decisions gives exit code 5 to a timeout and a nine-minute cluster create is
    ///     exactly the thing a CI job wants a ceiling on.
    /// </remarks>
    static CancellationTokenSource Deadline(CycHost host, GlobalValues values, CancellationToken cancellationToken) {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (values.Timeout is { } timeout)
            source.CancelAfter(timeout);

        return source;
    }

    /// <summary>
    ///     Puts the telemetry question, once, before the command runs.
    /// </summary>
    /// <remarks>
    ///     ⚠ Skipped for <c>--output json</c> and for a redirected stderr. A script's first run must
    ///     not block on a question, and a prompt in front of a JSON consumer is a prompt nobody sees.
    /// </remarks>
    static void AskAboutTelemetryOnce(CycHost host, GlobalValues values) {
        var settings = CycSettings.Resolve(host.Config, host.Environment, values.Profile);
        var interactive = !host.Console.IsErrorRedirected && values.Output != OutputFormat.Json;

        TelemetryConsent.EnsureAsked(host, settings, interactive, Console.ReadLine);
    }
}
