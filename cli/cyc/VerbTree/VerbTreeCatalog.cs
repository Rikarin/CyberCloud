using System.Reflection;

namespace CyberCloud.Cli.VerbTree;

/// <summary>
///     Every verb tree this build carries, keyed by api-version.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The tree is embedded at build time and the command tree is built from it at run
///         time, and the split is the decision.</b> The alternative — a source generator that emitted
///         a <c>System.CommandLine</c> class per verb — was rejected for three reasons, and
///         <c>CliEmitter</c>'s own remarks give the first: <i>"A generator that emitted C# command
///         classes instead would fuse the two and make every CLI behaviour change a generator
///         change."</i> The second is that <c>--api-version</c> selects between trees, and a compiled
///         tree can only be the one version it was compiled from — docs/plan/10 § API versioning
///         keeps every published version alive. The third is docs/plan/21 § Extensions:
///         <c>cyc extension add</c> loads a command group out of an
///         <c>AssemblyLoadContext</c>, so the tree has to be constructible at run time whatever this
///         file does.
///     </para>
///     <para>
///         ⚠ <b>Embedded rather than read from <c>generated/cli/</c> beside the executable.</b>
///         docs/plan/21 § `cyc` publishes one self-contained file per RID; a sibling directory is
///         not something a single-file artifact has. The .csproj's <c>EmbeddedResource</c> item is
///         the other half of this.
///     </para>
/// </remarks>
sealed class VerbTreeCatalog {
    const string ResourcePrefix = "cyc.VerbTrees.";

    readonly Dictionary<string, VerbTreeDocument> trees;

    VerbTreeCatalog(Dictionary<string, VerbTreeDocument> trees) {
        this.trees = trees;
        ApiVersions = [.. trees.Keys.OrderBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>Every api-version this build knows, oldest first.</summary>
    public IReadOnlyList<string> ApiVersions { get; }

    /// <summary>
    ///     The newest api-version. ⚠ Used as the default and never spelled <c>latest</c> on the wire —
    ///     docs/plan/10 § API versioning: a version is a date, and "latest" is a version that changes
    ///     underneath a script.
    /// </summary>
    public string Newest => ApiVersions.Count > 0 ? ApiVersions[^1] : string.Empty;

    /// <summary>Reads every tree embedded in an assembly.</summary>
    /// <param name="assembly">The assembly holding the <c>cyc.VerbTrees.*.json</c> resources.</param>
    /// <exception cref="CycUsageException">A tree is unreadable or announces a format this build does not know.</exception>
    public static VerbTreeCatalog FromAssembly(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        var trees = new Dictionary<string, VerbTreeDocument>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames()) {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new CycUsageException($"The embedded verb tree '{name}' could not be opened.");

            var tree = Read(stream, name);
            trees[tree.ApiVersion] = tree;
        }

        return new VerbTreeCatalog(trees);
    }

    /// <summary>Reads one tree — the seam a test builds a synthetic catalog through.</summary>
    /// <param name="documents">The trees, in any order.</param>
    public static VerbTreeCatalog Of(params VerbTreeDocument[] documents) {
        ArgumentNullException.ThrowIfNull(documents);

        return new VerbTreeCatalog(documents.ToDictionary(x => x.ApiVersion, StringComparer.Ordinal));
    }

    /// <summary>Parses a tree from JSON.</summary>
    /// <param name="json">The document, as <c>CliEmitter</c> writes it.</param>
    /// <exception cref="CycUsageException">The document does not parse, or announces an unknown format.</exception>
    public static VerbTreeDocument Parse(string json) {
        VerbTreeDocument? document;

        try {
            document = JsonSerializer.Deserialize(json, VerbTreeJsonContext.Default.VerbTreeDocument);
        } catch (JsonException e) {
            throw new CycUsageException($"The verb tree is not valid JSON: {e.Message}", e);
        }

        return Validate(document, "the verb tree");
    }

    /// <summary>
    ///     The tree for an api-version.
    /// </summary>
    /// <param name="apiVersion">The version, or <c>null</c> for <see cref="Newest" />.</param>
    /// <exception cref="CycUsageException">
    ///     ⚠ Names every version this build carries. "Unknown api-version" on its own leaves a user
    ///     guessing at a date, and the answer is three lines away in the binary they are already
    ///     running.
    /// </exception>
    public VerbTreeDocument Select(string? apiVersion) {
        if (trees.Count == 0)
            throw new CycUsageException(
                "This build of cyc carries no verb tree, so it has no commands. It was built without "
                + "generated/cli/*.json — run ./build.sh Generate and rebuild.");

        if (string.IsNullOrEmpty(apiVersion))
            return trees[Newest];

        if (trees.TryGetValue(apiVersion, out var tree))
            return tree;

        throw new CycUsageException(
            $"'{apiVersion}' is not an api-version this build of cyc knows. Available: "
            + $"{string.Join(", ", ApiVersions)}. There is no 'latest' — docs/plan/10 § API versioning "
            + "makes a version a date, and omitting --api-version uses the newest this build carries "
            + $"({Newest}).");
    }

    static VerbTreeDocument Read(Stream stream, string resource) {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        VerbTreeDocument? document;

        try {
            document = JsonSerializer.Deserialize(reader.ReadToEnd(), VerbTreeJsonContext.Default.VerbTreeDocument);
        } catch (JsonException e) {
            throw new CycUsageException($"The embedded verb tree '{resource}' is not valid JSON: {e.Message}", e);
        }

        return Validate(document, resource);
    }

    /// <summary>
    ///     Checks the two members without which nothing else can be trusted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>CliEmitter.FormatVersion</c> is the file's own shape, not the api-version, and it is
    ///     checked first: <i>"a host that silently accepted a format it did not understand would
    ///     mis-parse the flags rather than say so"</i>.
    /// </remarks>
    static VerbTreeDocument Validate(VerbTreeDocument? document, string source) {
        if (document is null)
            throw new CycUsageException($"{source} is empty.");

        if (!string.Equals(document.Format, SupportedFormat, StringComparison.Ordinal))
            throw new CycUsageException(
                $"{source} announces verb-tree format '{document.Format}' and this build of cyc reads "
                + $"format '{SupportedFormat}'. Upgrade cyc, or regenerate the tree with a matching build.");

        if (document.ApiVersion.Length == 0)
            throw new CycUsageException($"{source} names no api-version.");

        return document;
    }

    /// <summary>
    ///     The verb-tree format this build reads — <c>CliEmitter.FormatVersion</c>. ⚠ Not the
    ///     api-version: this one is the contract between the generator and the host.
    /// </summary>
    public const string SupportedFormat = "1";
}
