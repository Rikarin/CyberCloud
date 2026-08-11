using System.CommandLine;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc account</c> — docs/plan/21 § Grammar's <c>cyc account list | set --subscription S | show</c>,
///     plus the one command the SDK depends on.
/// </summary>
/// <remarks>
///     ⚠ <b><c>cyc account get-access-token</c> is a published contract, not a convenience.</b>
///     <c>CyberCloudCliCredential</c> runs exactly <c>cyc account get-access-token --output json</c>
///     and deserialises the result into <c>CliTokenPayload</c> — <c>accessToken</c> and
///     <c>expiresOn</c>, camel-cased. It also branches on the exit code: <b>3 means "not signed in,
///     run cyc login"</b> and anything else means something different went wrong, so mapping a missing
///     credential to any other code would make the SDK give wrong advice.
///     <c>AccessTokenContractTests</c> pins both halves.
/// </remarks>
static class AccountCommands {
    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        var command = new Command("account", "The signed-in account, the current profile, and its token.") {
            Show(host, globals, tree),
            Set(host, globals, tree),
            List(host, globals, tree),
            GetAccessToken(host, globals, tree),
        };

        return command;
    }

    static Command Show(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var command = new Command("show", "What the current profile resolves to.");

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);

            invocation.Render(Payload.Object([
                Line("profile", invocation.Settings.Profile),
                Line("subscription", invocation.Settings.Get("subscription")),
                Line("tenant", invocation.Settings.Get("tenant")),
                Line("endpoint", invocation.Settings.Endpoint.ToString()),
                Line("apiVersion", invocation.ApiVersion),
                Line("configFile", invocation.Settings.File.Path),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command Set(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var subscription = new Option<string>("--subscription") { Description = "The subscription to make current." };
        var tenant = new Option<string>("--tenant") { Description = "The tenant to make current." };
        var command = new Command("set", "Change the current profile's subscription or tenant.") { subscription, tenant };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var file = invocation.Settings.File;
            var changed = false;

            if (parse.GetValue(subscription) is { Length: > 0 } value) {
                file = file.Set(invocation.Settings.Profile, "subscription", value);
                changed = true;
            }

            if (parse.GetValue(tenant) is { Length: > 0 } tenantId) {
                file = file.Set(invocation.Settings.Profile, "tenant", tenantId);
                changed = true;
            }

            if (!changed)
                throw new CycUsageException("cyc account set needs --subscription or --tenant.");

            file.Write(Path.Combine(host.StateDirectory, "config"));
            invocation.Console.Note($"Updated profile '{invocation.Settings.Profile}'.");

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     Lists the profiles in <c>~/.cyc/config</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Profiles, not the platform's subscriptions, and the difference is worth stating.</b>
    ///     docs/plan/21 § Grammar's <c>cyc account list</c> reads as "list my subscriptions", and no
    ///     verb for that exists: the provider registry emits verbs per resource type and
    ///     subscriptions are docs/plan/06 § The hierarchy's scope rather than a resource type, so the
    ///     generated tree has no <c>subscriptions</c> group to hang one on. Until it does,
    ///     <c>cyc rest --method GET --uri /tenants/{tenant}/subscriptions</c> is the platform-side
    ///     answer, which is exactly what docs/plan/21 says the escape hatch is for. Reported.
    /// </remarks>
    static Command List(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var command = new Command("list", "The profiles in ~/.cyc/config. For the platform's subscriptions, see 'cyc rest'.");

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var file = invocation.Settings.File;

            invocation.Render(Payload.Array([
                .. file.Profiles.Select(profile => Payload.Object([
                    Line("name", profile),
                    Line("current", string.Equals(profile, invocation.Settings.Profile, StringComparison.Ordinal) ? "true" : "false"),
                    Line("subscription", file.Value(profile, "subscription")),
                    Line("tenant", file.Value(profile, "tenant")),
                ])),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command GetAccessToken(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var tenant = new Option<string>("--tenant") { Description = "The tenant to get a token for." };

        var scope = new Option<string[]>("--scope") {
            Description = "A scope to ask for. Repeatable. Defaults to the resource API's own.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command(
            "get-access-token",
            "Print an access token for the signed-in account. ⚠ The output carries token material — "
            + "it is what CyberCloudCliCredential reads, and it does not belong in a log.") {
            tenant, scope,
        };

        command.SetAction(async (parse, cancellationToken) => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var scopes = parse.GetValue(scope) is { Length: > 0 } requested ? requested : [CyberCloudScopes.Default];
            var tenantId = parse.GetValue(tenant) ?? invocation.Settings.Get("tenant");

            var credential = host.CreateCredential();

            try {
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext(scopes, tenantId), cancellationToken)
                    .ConfigureAwait(false);

                // ⚠ The member names are CliTokenPayload's and are a contract with the SDK. They are
                // camelCase because that type's [JsonPropertyName] attributes say so.
                invocation.Render(Payload.Object([
                    new KeyValuePair<string, Payload>("accessToken", Payload.Text(token.Token)),
                    new KeyValuePair<string, Payload>("expiresOn", Payload.Text(token.ExpiresOn.ToString("O", CultureInfo.InvariantCulture))),
                ]));

                return (int)ExitCode.Ok;
            } finally {
                (credential as IDisposable)?.Dispose();
            }
        });

        return command;
    }

    static KeyValuePair<string, Payload> Line(string name, string? value)
        => new(name, value is null ? Payload.Null : Payload.Text(value));
}
