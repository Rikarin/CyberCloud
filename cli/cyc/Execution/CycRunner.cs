using System.CommandLine;
using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Execution;

/// <summary>Turns a parse result into the context a command runs against.</summary>
static class CycRunner {
    /// <summary>Reads the global flags and resolves the profile behind them.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree the surface was built from.</param>
    /// <param name="parse">The parse result.</param>
    public static CycInvocation Bind(CycHost host, GlobalOptions globals, VerbTreeDocument tree, ParseResult parse) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(globals);

        var values = globals.Read(parse);
        var settings = CycSettings.Resolve(host.Config, host.Environment, values.Profile);

        return new CycInvocation(host, tree, values, settings);
    }
}
