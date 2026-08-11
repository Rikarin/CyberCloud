using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Reflection;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Generator;

/// <summary>
///     ADR-012's generation step: provider registry → the four surfaces. OpenAPI is written; the
///     other three are not, and <see cref="Main" /> says so rather than implying it did everything.
/// </summary>
static class Program {
    const int Ok = 0;
    const int BadArguments = 2;
    const int Failed = 3;

    /// <summary>
    ///     ⚠ <b>A dirty tree is exit 0, not a failure, and that is deliberate.</b> This process
    ///     reports facts — what drifted, what broke, what is stale — and <c>build/Build.Generate.cs</c>
    ///     turns them into a verdict, because the verdict differs between the two callers:
    ///     <c>Generate</c> writes and then fails on drift, <c>Architecture</c> only looks. Two callers
    ///     agreeing on facts and disagreeing on consequences is only possible if the facts come back
    ///     as data. A non-zero exit here means the run itself could not happen.
    /// </summary>
    static int Main(string[] arguments) {
        string? output = null;
        string? derived = null;
        string? report = null;
        var write = true;
        var providerAssemblies = new List<string>();

        for (var i = 0; i < arguments.Length; i++) {
            switch (arguments[i]) {
                case "--output" when i + 1 < arguments.Length:
                    output = arguments[++i];
                    break;

                case "--derived-output" when i + 1 < arguments.Length:
                    derived = arguments[++i];
                    break;

                case "--report" when i + 1 < arguments.Length:
                    report = arguments[++i];
                    break;

                case "--provider-assembly" when i + 1 < arguments.Length:
                    providerAssemblies.Add(arguments[++i]);
                    break;

                case "--check":
                    write = false;
                    break;

                default:
                    Console.Error.WriteLine(
                        $"Unrecognised argument '{arguments[i]}'. Usage: --output <dir> "
                        + "[--derived-output <dir>] [--report <file>] [--check] "
                        + "[--provider-assembly <path>]..."
                    );

                    return BadArguments;
            }
        }

        if (string.IsNullOrEmpty(output)) {
            Console.Error.WriteLine("--output <dir> is required: it is where the OpenAPI documents are checked in.");
            return BadArguments;
        }

        try {
            var providers = ProviderDiscovery.FromAssemblies(providerAssemblies);
            var registry = ProviderRegistry.Build(providers);
            var generated = OpenApiArtifacts.Generate(registry, output, write);

            foreach (var line in OpenApiArtifacts.Describe(generated)) {
                Console.WriteLine(line);
            }

            // ⚠ The other three read the documents this process just emitted, not the files on disk —
            // docs/plan/21 § Generation's one hop. See OpenApiArtifacts.Documents for why reading the
            // checked-in file back would let one drifted document seed three more.
            var surfaces = derived is { Length: > 0 }
                ? DerivedSurfaces.Generate(OpenApiArtifacts.Documents(registry), derived, write)
                : new DerivedReport([], []);

            foreach (var line in DerivedSurfaces.Describe(surfaces)) {
                Console.WriteLine(line);
            }

            // ⚠ Said on every run, including the clean ones. docs/plan/02 § ADR-012 names four
            // surfaces; a log that did not say which of them ran reads like the pipeline is finished
            // whatever it did.
            Console.WriteLine(
                derived is { Length: > 0 }
                    ? "All four of ADR-012's surfaces: the OpenAPI document, the cyc verb tree, the "
                      + ".NET SDK and the portal forms. The last three are generated from the first — "
                      + "docs/plan/21 § Generation. ⚠ The TypeScript, Python and Go SDKs and the "
                      + "Terraform provider are docs/plan/21 § Other SDKs and are not written."
                    : "OpenAPI only: no --derived-output was given, so the cyc verb tree, the .NET SDK "
                      + "and the portal forms were not written."
            );

            if (report is { Length: > 0 }) {
                var directory = Path.GetDirectoryName(Path.GetFullPath(report));

                if (!string.IsNullOrEmpty(directory)) {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(
                    report,
                    DeterministicJson.ToBytes(Render(generated, surfaces, providerAssemblies.Count))
                );
            }

            return Ok;
        } catch (Exception failure) when (failure is InvalidOperationException
                                              or ArgumentException
                                              or IOException
                                              or BadImageFormatException
                                              or ReflectionTypeLoadException) {
            // Every one of these is "the registry or the assemblies handed to us are wrong", which is
            // a message a contributor can act on. Anything else is a bug here and is left to crash
            // with its stack, because that is the one a contributor should report rather than read.
            Console.Error.WriteLine(failure.Message);
            return Failed;
        }
    }

    /// <summary>
    ///     The report <c>build/Build.Generate.cs</c> and <c>build/Build.Architecture.cs</c> read.
    /// </summary>
    static JsonObject Render(GenerationReport generated, DerivedReport surfaces, int assembliesScanned) {
        var documents = new JsonArray();

        foreach (var document in generated.Documents) {
            documents.Add(new JsonObject {
                ["apiVersion"] = document.ApiVersion,
                ["breakingChanges"] = Lines(document.BreakingChanges.Select(x => x.ToString())),
                ["drifted"] = document.Drifted,
                ["file"] = document.FileName,
                ["published"] = document.Published,
                ["structuralProblems"] = Lines(document.StructuralProblems)
            });
        }

        var derived = new JsonArray();

        foreach (var document in surfaces.Documents) {
            derived.Add(new JsonObject {
                ["apiVersion"] = document.ApiVersion,
                ["drifted"] = document.Drifted,
                ["file"] = document.FileName,
                ["problems"] = Lines(document.Problems),
                ["published"] = document.Published,
                ["surface"] = document.Surface
            });
        }

        return new JsonObject {
            ["apiVersions"] = generated.ApiVersions,
            ["assembliesScanned"] = assembliesScanned,
            ["clean"] = generated.IsClean && surfaces.IsClean,
            // ⚠ A separate array rather than merged into `documents`: the two are gated differently.
            // A published OpenAPI document is diffed for compatibility; a derived surface is not,
            // because docs/plan/21 § Generation makes it a function of the document that already was.
            ["derived"] = derived,
            ["derivedStale"] = Lines(surfaces.Stale),
            ["documents"] = documents,
            ["providers"] = generated.Providers,
            ["resourceTypes"] = generated.ResourceTypes,
            ["stale"] = Lines(generated.Stale)
        };
    }

    static JsonArray Lines(IEnumerable<string> values) {
        var array = new JsonArray();

        foreach (var value in values) {
            array.Add(value);
        }

        return array;
    }
}
