using System.CommandLine;
using System.Reflection;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     The commands the host owns — everything in docs/plan/21 § Grammar that is not a resource verb.
/// </summary>
/// <remarks>
///     ⚠ <b>These are the CLI's own surface and are deliberately not generated.</b> Signing in,
///     choosing a profile, printing a completion script and calling a raw URL are not functions of any
///     provider's schema, and a generator that emitted them would be a generator with a hand-written
///     special case in it. <c>CommandTree.ReservedGroups</c> is the fence between the two halves.
/// </remarks>
static class HostCommands {
    /// <summary>Builds them.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree for the selected api-version.</param>
    public static IReadOnlyList<Command> Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        return [
            LoginCommand.Build(host, globals, tree),
            LogoutCommand(host),
            AccountCommands.Build(host, globals, tree),
            RestCommand.Build(host, globals, tree),
            ConfigCommands.Build(host, globals, tree),
            ExtensionCommands.Build(host, globals, tree),
            CompletionCommand.Build(host),
            VersionCommand(host, tree),
        ];
    }

    /// <summary>
    ///     <c>cyc logout</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It clears the SDK's cache entry and nothing else, because there is nothing else.</b>
    ///     The refresh token lives in the OS keychain under a key the SDK owns; <c>cyc</c> has no file
    ///     of its own to delete, which is the whole point of docs/plan/21 § Decisions' token-cache row.
    /// </remarks>
    static Command LogoutCommand(CycHost host) {
        var command = new Command("logout", "Forget the signed-in account. Removes the SDK's keychain entry.");

        command.SetAction(async (parse, cancellationToken) => {
            var cache = TokenCache.CreatePersistent();
            var key = TokenCache.KeyFor(CyberCloudAuthorityHosts.Default, CyberCloudCliCredential.CliClientId, tenantId: null);

            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

            host.Console.Note("Signed out.");

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     <c>cyc version</c> — the build, the api-versions it carries, and where its configuration is.
    /// </summary>
    /// <remarks>
    ///     ⚠ The api-version list is here rather than only in <c>--help</c> because it is the first
    ///     question a failing script raises: a pipeline that broke after an upgrade wants to know which
    ///     versions this binary speaks, and docs/plan/10 § API versioning makes that a fixed list
    ///     rather than a range.
    /// </remarks>
    static Command VersionCommand(CycHost host, VerbTreeDocument tree) {
        var command = new Command("version", "The build, the api-versions it carries, and its configuration file.");

        command.SetAction(parse => {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

            host.Console.Out.WriteLine($"cyc {version}");
            host.Console.Out.WriteLine($"api-versions: {string.Join(", ", host.Catalog.ApiVersions)} (default {tree.ApiVersion})");
            host.Console.Out.WriteLine($"config: {Path.Combine(host.StateDirectory, "config")}");
            host.Console.Out.Flush();

            return (int)ExitCode.Ok;
        });

        return command;
    }
}
