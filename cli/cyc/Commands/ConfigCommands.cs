using System.CommandLine;
using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc config</c> — docs/plan/21 § Decisions: <i>"<c>~/.cyc/config</c> with named profiles;
///     every setting also an env var (<c>CYC_SUBSCRIPTION</c>, …) for CI."</i>
/// </summary>
static class ConfigCommands {
    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        return new Command("config", "Read and write ~/.cyc/config.") {
            Get(host, globals, tree),
            Set(host, globals, tree),
            List(host, globals, tree),
            UseProfile(host, globals, tree),
            Telemetry(host, globals, tree),
        };
    }

    static Command Get(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var key = new Argument<string>("key") { Description = "The setting — subscription, tenant, endpoint, output." };
        var command = new Command("get", "One setting's value, resolved through the environment and the profile.") { key };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var name = parse.GetRequiredValue(key);

            invocation.Render(Payload.Object([
                new KeyValuePair<string, Payload>("key", Payload.Text(name)),
                new KeyValuePair<string, Payload>(
                    "value",
                    invocation.Settings.Get(name) is { } value ? Payload.Text(value) : Payload.Null),
                // ⚠ Where it came from is the useful half of the answer: "why is my subscription
                // wrong" is nearly always an environment variable somebody forgot they exported.
                new KeyValuePair<string, Payload>("source", Payload.Text(Source(invocation, name))),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command Set(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var key = new Argument<string>("key") { Description = "The setting." };
        var value = new Argument<string>("value") { Description = "The value." };
        var command = new Command("set", "Write a setting into the current profile.") { key, value };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);

            invocation.Settings.File
                .Set(invocation.Settings.Profile, parse.GetRequiredValue(key), parse.GetRequiredValue(value))
                .Write(Path.Combine(host.StateDirectory, "config"));

            invocation.Console.Note($"Set {parse.GetRequiredValue(key)} in profile '{invocation.Settings.Profile}'.");

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command List(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var command = new Command("list", "Every setting in the current profile.");

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var settings = invocation.Settings.File.Settings(invocation.Settings.Profile);

            invocation.Render(Payload.Array([
                .. settings.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Payload.Object([
                    new KeyValuePair<string, Payload>("key", Payload.Text(x.Key)),
                    new KeyValuePair<string, Payload>("value", Payload.Text(x.Value)),
                    new KeyValuePair<string, Payload>("env", Payload.Text(CycSettings.VariableFor(x.Key))),
                ])),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command UseProfile(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var name = new Argument<string>("profile") { Description = "The profile to make default." };
        var command = new Command("use-profile", "Choose the profile used when --profile and CYC_PROFILE are absent.") { name };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);

            invocation.Settings.File
                .WithDefaultProfile(parse.GetRequiredValue(name))
                .Write(Path.Combine(host.StateDirectory, "config"));

            invocation.Console.Note($"Default profile is now '{parse.GetRequiredValue(name)}'.");

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     <c>cyc config telemetry on|off|status</c> — docs/plan/21 § Decisions: <i>"Opt-in, off by
    ///     default, and asked once. Opt-out telemetry in a developer tool is a trust cost that is
    ///     never worth the data."</i>
    /// </summary>
    static Command Telemetry(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var state = new Argument<string>("state") {
            Description = "on, off, or status.",
            DefaultValueFactory = _ => "status",
        };

        state.AcceptOnlyFromAmong("on", "off", "status");

        var command = new Command("telemetry", "Whether cyc may send usage telemetry. Off unless you turn it on.") { state };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var choice = parse.GetRequiredValue(state);

            if (!string.Equals(choice, "status", StringComparison.Ordinal)) {
                TelemetryConsent.Record(host, invocation.Settings, string.Equals(choice, "on", StringComparison.Ordinal));
                invocation.Console.Note($"Telemetry is {choice}.");
            }

            invocation.Render(Payload.Object([
                new KeyValuePair<string, Payload>("telemetry", Payload.Text(TelemetryConsent.IsEnabled(invocation.Settings) ? "on" : "off")),
                new KeyValuePair<string, Payload>("asked", Payload.Boolean(TelemetryConsent.WasAsked(invocation.Settings))),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static string Source(CycInvocation invocation, string key) {
        if (invocation.Host.Environment.ContainsKey(CycSettings.VariableFor(key)))
            return CycSettings.VariableFor(key);

        if (invocation.Settings.File.Value(invocation.Settings.Profile, key) is not null)
            return $"profile {invocation.Settings.Profile}";

        return "unset";
    }
}
