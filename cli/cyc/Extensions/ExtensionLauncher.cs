using System.ComponentModel;
using System.Diagnostics;
using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.Output;

namespace CyberCloud.Cli.Extensions;

/// <summary>
///     Runs an installed extension as a child process, and decides what it is told.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NO TOKEN CROSSES THIS BOUNDARY, AND THE EXTENSION RE-AUTHENTICATES INSTEAD.</b>
///         docs/plan/21 § Extensions: an access token in the child's environment is a token in
///         <c>/proc/&lt;pid&gt;/environ</c>, in every grandchild the extension spawns, in a crash dump
///         and in any CI step that prints its environment. <see cref="EnvironmentFor" /> passes
///         <i>context</i> — endpoint, profile, subscription, tenant, api-version, output format — and
///         <see cref="ExecutableVariable" />, the absolute path of the running <c>cyc</c>. The
///         extension asks for a token by running <c>$CYC_EXECUTABLE account get-access-token --output
///         json</c>, which is <c>gh</c>'s <c>gh auth token</c> arrangement and, better, a contract
///         this repository already has: <c>CyberCloudCliCredential</c> issues exactly that command, so
///         a .NET extension writes <c>new CyberCloudCliCredential()</c> and is done. The token stays
///         in the OS keychain, the SDK stays the only thing that speaks OAuth, and the credential's
///         lifetime is the child's own.
///     </para>
///     <para>
///         ⚠ <b>The rest of the environment is inherited, deliberately.</b> A <c>CYC_CLIENT_SECRET</c>
///         that CI exported is visible to the child, and stripping it would be theatre: the extension
///         already runs as the user, with the user's files and the user's keychain. The rule this type
///         keeps is narrower and worth stating precisely — <c>cyc</c> never <i>materializes</i> fresh
///         credential material into a place it was not already. Inheriting an exposure is not the same
///         as creating one.
///     </para>
///     <para>
///         ⚠ <b>The three streams are inherited too, so the extension owns the terminal.</b> Nothing
///         is redirected: an extension that prompts, draws a progress bar or streams to a pipe behaves
///         as if the user had run it directly, and <c>cyc</c> writes nothing to stdout on this path.
///         That is what makes <c>cyc mytool --output json | jq</c> work.
///     </para>
/// </remarks>
static class ExtensionLauncher {
    /// <summary>
    ///     The variable carrying the running <c>cyc</c>'s absolute path.
    /// </summary>
    /// <remarks>
    ///     ⚠ The path, not the word <c>cyc</c>. An extension that shelled out to whatever <c>cyc</c>
    ///     <c>PATH</c> resolved to would reintroduce the <c>PATH</c> trust problem
    ///     (<see cref="ExtensionStore" />) from the other direction, and would ask a different build
    ///     for a token than the one that launched it.
    /// </remarks>
    public static string ExecutableVariable => CycSettings.VariableFor("executable");

    /// <summary>The variable naming the extension cyc dispatched to, so a program can tell how it was started.</summary>
    public static string NameVariable => CycSettings.VariableFor("extension");

    /// <summary>
    ///     The variables <c>cyc</c> adds to the child's environment.
    /// </summary>
    /// <param name="name">The extension's verb.</param>
    /// <param name="settings">The resolved settings — flag, then environment, then profile.</param>
    /// <param name="apiVersion">The api-version the invocation settled on.</param>
    /// <param name="format">The output format, so an extension can match <c>cyc</c>'s own rendering.</param>
    /// <param name="executable">The running <c>cyc</c>'s path, or <c>null</c> when the runtime cannot say.</param>
    /// <remarks>
    ///     ⚠ Every name comes from <see cref="CycSettings.VariableFor" />, so the <c>CYC_</c> mapping
    ///     stays the one mechanical rule docs/plan/21 § Decisions asks for and this table cannot drift
    ///     from it. <c>ExtensionTests</c> asserts that no name here is credential-shaped by
    ///     <c>CycConfigFile.LooksLikeCredential</c> — the same predicate that keeps token material out
    ///     of <c>~/.cyc/config</c>, reused so that adding <c>CYC_ACCESS_TOKEN</c> to this method turns
    ///     a test red.
    /// </remarks>
    public static Dictionary<string, string> EnvironmentFor(
        string name,
        CycSettings settings,
        string apiVersion,
        OutputFormat format,
        string? executable) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(settings);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal) {
            [NameVariable] = name,
            [CycSettings.VariableFor("profile")] = settings.Profile,
            [CycSettings.VariableFor("endpoint")] = settings.Endpoint.ToString(),
            [CycSettings.VariableFor("api-version")] = apiVersion,
            [CycSettings.VariableFor("output")] = OutputFormats.NameOf(format),
        };

        Add("subscription", settings.Get("subscription"));
        Add("tenant", settings.Get("tenant"));

        if (executable is { Length: > 0 })
            variables[ExecutableVariable] = executable;

        return variables;

        void Add(string key, string? value) {
            if (value is { Length: > 0 })
                variables[CycSettings.VariableFor(key)] = value;
        }
    }

    /// <summary>
    ///     Starts the extension, waits for it, and hands back its exit code.
    /// </summary>
    /// <param name="launch">What to run, with what arguments and what added environment.</param>
    /// <param name="cancellationToken">The invocation's deadline. Cancelling it kills the child and everything it started.</param>
    /// <returns>
    ///     The child's exit code, unchanged. ⚠ docs/plan/21 § Decisions' six codes are a contract for
    ///     <c>cyc</c>'s own commands; an extension owns its exit codes the way <c>git</c>'s do, and
    ///     there is no mapping from an arbitrary program's codes onto that table that does not throw
    ///     information away. What <c>cyc</c> reports with the table is everything that happens
    ///     <i>before</i> the child starts.
    /// </returns>
    /// <exception cref="CycClientException">The executable would not start.</exception>
    public static async Task<int> StartAsync(ExtensionLaunch launch, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(launch);

        var start = new ProcessStartInfo(launch.Executable) {
            // ⚠ False, so the three streams are inherited rather than opened through a shell. It also
            // means the arguments below are passed as an array and never go near a command
            // interpreter, so a resource name with a space or a quote in it cannot become a second
            // argument.
            UseShellExecute = false,
        };

        foreach (var argument in launch.Arguments)
            start.ArgumentList.Add(argument);

        foreach (var variable in launch.Environment)
            start.Environment[variable.Key] = variable.Value;

        Process child;

        try {
            child = Process.Start(start)
                ?? throw new CycClientException($"'{launch.Executable}' did not start, and the operating system gave no reason.");
        } catch (Win32Exception e) {
            throw new CycClientException(
                $"'{launch.Executable}' could not be run: {e.Message} It is installed as a cyc extension, so it has to be an "
                + "executable this machine can run — check that it is built for this architecture and reinstall it with "
                + "'cyc extension add'.",
                e);
        }

        using (child) {
            try {
                await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // ⚠ The whole tree. An extension that spawned `psql` and was then cut off by
                // --timeout would otherwise leave it attached to the terminal after cyc had exited.
                Kill(child);

                throw;
            }

            return child.ExitCode;
        }
    }

    static void Kill(Process child) {
        try {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        } catch (Exception e) when (e is InvalidOperationException or NotSupportedException or Win32Exception or AggregateException) {
            // It exited between the question and the signal, which is the outcome the signal wanted.
        }
    }
}

/// <summary>What to run for one extension invocation.</summary>
/// <param name="Name">The verb the user typed.</param>
/// <param name="Executable">The absolute path of the installed file, already integrity-checked.</param>
/// <param name="Arguments">Everything after the verb, in the order it was typed and otherwise untouched.</param>
/// <param name="Environment">The <c>CYC_</c> variables to add. ⚠ Never a token — see <see cref="ExtensionLauncher" />.</param>
sealed record ExtensionLaunch(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);
