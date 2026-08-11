using System.CommandLine;
using CyberCloud.Cli.Output;

namespace CyberCloud.Cli;

/// <summary>
///     The flags every command takes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Declared here rather than read out of the verb tree, and the difference is what a
///         flag <i>does</i>.</b> The tree's <c>globalFlags</c> array carries <c>--output</c>,
///         <c>--query</c> and <c>--api-version</c> with their help text and their choices — which is
///         everything a generator can know — but "apply this JMESPath to the response" is host
///         behaviour that no OpenAPI document describes. <see cref="AssertMatchesTree" /> is what
///         keeps the two halves honest: it fails if the tree grows a global flag this host does not
///         implement.
///     </para>
///     <para>
///         ⚠ <b><c>--api-version</c> is read twice, and it has to be.</b> The tree it names is the
///         tree the command surface is built from, so it is peeled off the raw arguments before
///         <c>System.CommandLine</c> sees them (<see cref="PeekApiVersion" />) and then declared as a
///         real option so that it appears in help and completion.
///     </para>
/// </remarks>
sealed class GlobalOptions {
    GlobalOptions(IReadOnlyList<string> apiVersions, string newest) {
        Output = new Option<string>("--output", "-o") {
            Description = $"How to print the answer: {string.Join(", ", OutputFormats.Names)}. Defaults to table.",
            DefaultValueFactory = _ => "table",
        };

        Output.AcceptOnlyFromAmong([.. OutputFormats.Names]);

        Query = new Option<string>("--query") {
            Description = "A JMESPath expression over the response — docs/plan/21 § Decisions.",
        };

        ApiVersion = new Option<string>("--api-version") {
            Description =
                $"The api-version to speak. One of {string.Join(", ", apiVersions)}; defaults to {newest}. "
                + "There is no 'latest' — docs/plan/10 § API versioning.",
        };

        ApiVersion.AcceptOnlyFromAmong([.. apiVersions]);

        Profile = new Option<string>("--profile") {
            Description = "The profile in ~/.cyc/config to read settings from. Also CYC_PROFILE.",
        };

        Verbose = new Option<bool>("--verbose") {
            Description = "Trace each request and response to stderr. ⚠ Credentials are never traced.",
        };

        Timeout = new Option<int>("--timeout") {
            Description = "Give up after this many seconds and exit 5. Covers waiting on a long-running operation.",
        };

        foreach (var option in All)
            option.Recursive = true;
    }

    /// <summary><c>--output</c>.</summary>
    public Option<string> Output { get; }

    /// <summary><c>--query</c>.</summary>
    public Option<string> Query { get; }

    /// <summary><c>--api-version</c>.</summary>
    public Option<string> ApiVersion { get; }

    /// <summary><c>--profile</c>.</summary>
    public Option<string> Profile { get; }

    /// <summary><c>--verbose</c>.</summary>
    public Option<bool> Verbose { get; }

    /// <summary><c>--timeout</c>, in seconds.</summary>
    public Option<int> Timeout { get; }

    /// <summary>All of them, in help order.</summary>
    public IReadOnlyList<Option> All => [Output, Query, ApiVersion, Profile, Verbose, Timeout];

    /// <summary>Creates the options for a catalog's api-versions.</summary>
    /// <param name="catalog">The verb trees this build carries.</param>
    public static GlobalOptions For(VerbTree.VerbTreeCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        return new GlobalOptions(catalog.ApiVersions, catalog.Newest);
    }

    /// <summary>Reads the values out of a parse.</summary>
    /// <param name="parse">The parse result.</param>
    public GlobalValues Read(ParseResult parse) {
        ArgumentNullException.ThrowIfNull(parse);

        return new GlobalValues(
            OutputFormats.Parse(parse.GetValue(Output)),
            parse.GetValue(Query),
            parse.GetValue(ApiVersion),
            parse.GetValue(Profile),
            parse.GetValue(Verbose),
            parse.GetValue(Timeout) is var seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null);
    }

    /// <summary>
    ///     Finds <c>--api-version</c> in the raw arguments, before the command tree exists.
    /// </summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>The value, or <c>null</c> when the flag is absent.</returns>
    /// <exception cref="CycUsageException">The flag was given with no value after it.</exception>
    public static string? PeekApiVersion(IReadOnlyList<string> arguments) {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var i = 0; i < arguments.Count; i++) {
            var argument = arguments[i];

            if (argument.StartsWith("--api-version=", StringComparison.Ordinal))
                return argument["--api-version=".Length..];

            if (!string.Equals(argument, "--api-version", StringComparison.Ordinal))
                continue;

            if (i + 1 >= arguments.Count)
                throw new CycUsageException("--api-version was given with no value after it.");

            return arguments[i + 1];
        }

        return null;
    }

    /// <summary>
    ///     Fails when the generated tree declares a global flag this host does not implement.
    /// </summary>
    /// <param name="tree">The tree.</param>
    /// <exception cref="CycUsageException">
    ///     A <c>globalFlags</c> entry has no matching option. ⚠ The generator and the host are two
    ///     halves of one surface; a flag the tree promises and the host drops is help text that lies.
    /// </exception>
    public void AssertMatchesTree(VerbTree.VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(tree);

        var declared = All.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var missing = tree.GlobalFlags.Select(x => x.Name).Where(x => !declared.Contains(x)).ToList();

        if (missing.Count > 0)
            throw new CycUsageException(
                $"The verb tree for {tree.ApiVersion} declares global flag(s) this build of cyc does not "
                + $"implement: {string.Join(", ", missing)}. Upgrade cyc.");
    }
}

/// <summary>What the global flags said.</summary>
/// <param name="Output">The output format.</param>
/// <param name="Query">The JMESPath expression, or <c>null</c>.</param>
/// <param name="ApiVersion">The api-version, or <c>null</c> for the newest this build carries.</param>
/// <param name="Profile">The profile name, or <c>null</c>.</param>
/// <param name="Verbose">Whether to trace to stderr.</param>
/// <param name="Timeout">The deadline, or <c>null</c> for none.</param>
sealed record GlobalValues(
    OutputFormat Output,
    string? Query,
    string? ApiVersion,
    string? Profile,
    bool Verbose,
    TimeSpan? Timeout);
