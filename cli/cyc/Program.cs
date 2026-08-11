using System.Reflection;
using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli;

/// <summary>
///     The process. Everything it does is <see cref="CycApplication" />; this file exists to build the
///     real <see cref="CycHost" /> and to make sure the exit code is the one docs/plan/21 § Decisions
///     promises, whatever happens.
/// </summary>
static class Program {
    /// <summary>Runs the CLI.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>One of the six codes in <see cref="ExitCode" />.</returns>
    public static async Task<int> Main(string[] args) {
        var console = CycConsole.System();

        try {
            var environment = CycDefaults.Environment();
            var credential = new Lazy<TokenCredential>(CycDefaults.SignedInCredential);

            var host = new CycHost(
                console,
                environment,
                CycConfigFile.Read(),
                VerbTreeCatalog.FromAssembly(Assembly.GetExecutingAssembly()),
                request => CycDefaults.Client(request, credential.Value),
                CycDefaults.OpenBrowser) {
                CreateCredential = () => credential.Value,
            };

            // ⚠ Started, never awaited — docs/plan/21 § Decisions' "non-blocking". See UpdateCheck.
            _ = UpdateCheck.Start(host, Version);

            using var cancellation = new CancellationTokenSource();

            // Ctrl-C stops the command rather than the process, so a --wait that is cancelled still
            // leaves the operation running server-side and says so through the exit code.
            Console.CancelKeyPress += (_, e) => {
                e.Cancel = true;
                cancellation.Cancel();
            };

            return await CycApplication.RunAsync(host, args, cancellation.Token).ConfigureAwait(false);
        } catch (Exception failure) {
            // ⚠ The last line of defence. Anything that escapes CycApplication — a failure building
            // the host itself, an unreadable config file — still leaves the process with one of the
            // six documented codes rather than an unhandled-exception 134.
            return ErrorWriter.Report(console, Output.OutputFormat.Table, failure, verbose: args.Contains("--verbose"));
        }
    }

    /// <summary>This build's version, as <c>cyc version</c> prints it.</summary>
    static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
