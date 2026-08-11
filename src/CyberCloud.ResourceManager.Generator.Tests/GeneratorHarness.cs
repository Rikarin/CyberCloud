using CyberCloud.Providers.Sample;
using CyberCloud.Providers.Sample.Contracts;
using System.Globalization;

namespace CyberCloud.ResourceManager.Generator.Tests;

/// <summary>What one in-process run of the generator did.</summary>
/// <param name="ExitCode">
///     <c>0</c> ok, <c>2</c> bad arguments, <c>3</c> the run could not happen. ⚠ Drift is <c>0</c> —
///     the reasoning is on <c>Program.Main</c>, and every test here asserts against that contract
///     rather than against an intuition about what a generator "should" exit with.
/// </param>
/// <param name="Output">Everything written to <see cref="Console.Out" />.</param>
/// <param name="Error">Everything written to <see cref="Console.Error" />.</param>
public sealed record GeneratorRun(int ExitCode, string Output, string Error);

/// <summary>
///     A throwaway directory tree, and the arguments that point the generator at it.
/// </summary>
/// <remarks>
///     ⚠ <b>The one rule of this suite.</b> <c>openapi/</c> and <c>generated/</c> are tracked files
///     that <c>build/Build.Generate.cs</c> compares byte for byte; a test that regenerated them would
///     be a test that rewrites the evidence the Generated surfaces gate is reading. Nothing here ever
///     names a path inside the repository, and the two directories below do not exist until a run
///     that is supposed to write creates them — which is what makes <c>--check</c> checkable.
/// </remarks>
public sealed class TemporaryTree : IDisposable {
    public TemporaryTree() {
        Root = Path.Combine(
            Path.GetTempPath(),
            "cyc-generator-tests",
            Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture)
        );

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Stands in for the repository's <c>openapi/</c>.</summary>
    public string OpenApiDirectory => Path.Combine(Root, "openapi");

    /// <summary>Stands in for the repository's <c>generated/</c>.</summary>
    public string DerivedDirectory => Path.Combine(Root, "generated");

    /// <summary>
    ///     Two levels below <see cref="Root" /> on purpose: the build points <c>--report</c> at
    ///     <c>artifacts/generation-report.json</c>, and <c>artifacts/</c> is routinely deleted.
    /// </summary>
    public string ReportFile => Path.Combine(Root, "artifacts", "generation-report.json");

    public void Dispose() {
        try {
            Directory.Delete(Root, recursive: true);
        } catch (IOException) {
            // A leftover temp directory is not worth failing a test over.
        } catch (UnauthorizedAccessException) {
            // As above.
        }
    }

    /// <summary>The report the build reads, parsed.</summary>
    public JsonObject Report() =>
        JsonNode.Parse(File.ReadAllBytes(ReportFile))?.AsObject()
        ?? throw new InvalidOperationException($"{ReportFile} is not a JSON object.");

    /// <summary>Every file below a directory, relative to it, ordered. Empty when it does not exist.</summary>
    public static IReadOnlyList<string> FilesUnder(string directory) =>
        Directory.Exists(directory)
            ? [
                .. Directory
                    .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(x => Path.GetRelativePath(directory, x).Replace('\\', '/'))
                    .OrderBy(x => x, StringComparer.Ordinal)
            ]
            : [];
}

/// <summary>
///     Runs <c>Program.Main</c> and captures what a shell would have seen.
/// </summary>
/// <remarks>
///     ⚠ <b>In-process, not <c>dotnet run</c>, and the reason is the coverage floor.</b> A child
///     process is a process <c>dotnet-coverage</c> never instruments, so a suite that started one
///     would drive every line of the generator and still report 0 % for it — docs/plan/23 § Test
///     layers, whose floor is per shipping project. The cost is that <c>Environment.Exit</c> would be
///     fatal here; <c>Main</c> returns its exit code instead, which is also what makes it testable.
/// </remarks>
public static class Generator {
    /// <summary>The built <c>CyberCloud.Providers.Sample</c> assembly — a real provider, on disk.</summary>
    public static string SampleProviderAssembly => typeof(SampleProvider).Assembly.Location;

    /// <summary>
    ///     The built <c>CyberCloud.Providers.Sample.Contracts</c> assembly: a real, loadable managed
    ///     assembly that declares no <c>IResourceProvider</c> at all. It is how "scanned nothing" and
    ///     "scanned something and found nothing" are told apart.
    /// </summary>
    public static string ProviderlessAssembly => typeof(SampleWidgets).Assembly.Location;

    public static GeneratorRun Invoke(params string[] arguments) {
        var previousOut = Console.Out;
        var previousError = Console.Error;

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        try {
            Console.SetOut(output);
            Console.SetError(error);

            return new(GeneratorProgram.Main(arguments), output.ToString(), error.ToString());
        } finally {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>
    ///     The invocation <c>build/Build.Generate.cs</c> makes, against a temporary tree.
    /// </summary>
    /// <param name="tree">Where to write.</param>
    /// <param name="check"><c>true</c> for the <c>Architecture</c> gate's read-only mode.</param>
    /// <param name="providerAssemblies">
    ///     What the build passes one <c>--provider-assembly</c> per. Empty is a legitimate state — it
    ///     is the one the build warns about rather than fails on.
    /// </param>
    public static GeneratorRun Run(TemporaryTree tree, bool check = false, params string[] providerAssemblies) {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(providerAssemblies);

        var arguments = new List<string> {
            "--output", tree.OpenApiDirectory,
            "--derived-output", tree.DerivedDirectory,
            "--report", tree.ReportFile
        };

        if (check) {
            arguments.Add("--check");
        }

        foreach (var assembly in providerAssemblies) {
            arguments.Add("--provider-assembly");
            arguments.Add(assembly);
        }

        return Invoke([.. arguments]);
    }
}
