using CyberCloud.Cli.Execution;
using CyberCloud.Cli.VerbTree;
using System.CommandLine;
using System.Reflection;

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
            LogoutCommand(host, globals, tree),
            AccountCommands.Build(host, globals, tree),
            RestCommand.Build(host, globals, tree),
            GraphCommands.Build(host, globals, tree),
            ConfigCommands.Build(host, globals, tree),
            ExtensionCommands.Build(host, globals, tree),
            CompletionCommand.Build(host),
            VersionCommand(host, tree)
        ];
    }

    /// <summary>
    ///     <c>cyc logout</c> — revoke the cached sign-in at the identity server, then forget it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It revokes, and it did not use to.</b> Until #43 this cleared the keychain entry and
    ///         nothing else, which left the refresh token valid on the server for fourteen days in any
    ///         other copy of it. The protocol is the SDK's (<see cref="CyberCloudSignOut" />): the
    ///         refresh token goes to the host's <c>/revoke</c>, which ends the session behind it, and
    ///         the entry is forgotten whatever the server answered — a <c>logout</c> on a machine that
    ///         has lost its network still signs it out locally, and says the revocation is owed.
    ///     </para>
    ///     <para>
    ///         ⚠ The authority and the cache are the ones <c>cyc login</c> used — the profile's
    ///         <c>authority</c> and <see cref="CycHost.CreateCredentialOptions" /> — where this used to
    ///         key the entry off the SDK's default authority and would miss a sign-in made against
    ///         any other.
    ///     </para>
    /// </remarks>
    static Command LogoutCommand(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var command = new Command("logout", "Sign out: revoke the cached sign-in at the identity host and forget it.");

        command.SetAction(async (parse, cancellationToken) => {
                var invocation = CycRunner.Bind(host, globals, tree, parse);
                var options = host.CreateCredentialOptions();
                options.AuthorityHost = LoginCommand.Authority(invocation);

                var result = await CyberCloudSignOut
                    .SignOutAsync(options, CyberCloudCliCredential.CliClientId, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.HadSignIn) {
                    host.Console.Note("Not signed in.");
                } else if (result.Revoked) {
                    host.Console.Note("Signed out. The session was revoked at the identity host.");
                } else {
                    host.Console.Note(
                        "Signed out on this machine, but the session could not be revoked at the identity "
                        + "host: " + result.Detail
                    );
                }

                return (int)ExitCode.Ok;
            }
        );

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
                host.Console.Out.WriteLine(
                    $"api-versions: {string.Join(", ", host.Catalog.ApiVersions)} (default {tree.ApiVersion})"
                );
                host.Console.Out.WriteLine($"config: {Path.Combine(host.StateDirectory, "config")}");
                host.Console.Out.Flush();

                return (int)ExitCode.Ok;
            }
        );

        return command;
    }
}
