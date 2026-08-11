using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Execution;

/// <summary>
///     One command's worth of resolved context: the host, what the global flags said, the settings
///     behind them, and the tree the surface was built from.
/// </summary>
sealed class CycInvocation {
    /// <summary>Creates the context.</summary>
    /// <param name="host">The host.</param>
    /// <param name="tree">The verb tree the command tree was built from.</param>
    /// <param name="globals">What the global flags said.</param>
    /// <param name="settings">Flag, then environment, then profile.</param>
    public CycInvocation(CycHost host, VerbTreeDocument tree, GlobalValues globals, CycSettings settings) {
        Host = host;
        Tree = tree;
        Globals = globals;
        Settings = settings;
    }

    /// <summary>The host.</summary>
    public CycHost Host { get; }

    /// <summary>The tree.</summary>
    public VerbTreeDocument Tree { get; }

    /// <summary>The global flags.</summary>
    public GlobalValues Globals { get; }

    /// <summary>The resolved settings.</summary>
    public CycSettings Settings { get; }

    /// <summary>The two streams.</summary>
    public CycConsole Console => Host.Console;

    /// <summary>The api-version on the wire.</summary>
    public string ApiVersion => Tree.ApiVersion;

    /// <summary>Builds the SDK client for this invocation.</summary>
    /// <param name="tenantId">The tenant to authenticate against, or <c>null</c> for the credential's own.</param>
    public CyberCloudClient CreateClient(string? tenantId)
        => Host.CreateClient(new CycClientRequest(Settings.Endpoint, ApiVersion, tenantId));

    /// <summary>
    ///     Applies <c>--query</c> and writes the answer to stdout.
    /// </summary>
    /// <param name="value">The value, as it came back.</param>
    /// <exception cref="CycUsageException">The expression does not parse.</exception>
    public void Render(Payload value) {
        var rendered = Globals.Query is { Length: > 0 } query ? JmesPath.Evaluate(query, value) : value;

        ResultWriter.Write(Console, Globals.Output, rendered);
    }

    /// <summary>
    ///     Writes a diagnostic line, but only under <c>--verbose</c>.
    /// </summary>
    /// <param name="line">The line. ⚠ Never build one out of a header value — use <see cref="Redaction" />.</param>
    public void Trace(string line) {
        if (Globals.Verbose)
            Console.Note("cyc: " + line);
    }
}
