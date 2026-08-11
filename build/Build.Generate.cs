// Generate — docs/plan/23 § Build, row `Generate`:
// "Provider registry → OpenAPI → CLI verbs → SDK → portal forms (ADR-012). Fails on drift".
//
// This target owns the verdict and owns no logic. It runs CyberCloud.ResourceManager.Generator, which
// loads the provider assemblies for real and emits the documents, and reads back the report that says
// what changed. The reasons for the split are in that project's .csproj; the short version is that the
// emitters must read the same registry object the write path validates against
// (docs/plan/08 § The provider registry), which means running a provider's Describe, which means
// loading Orleans into somebody's process — and it must not be this one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;

partial class Build
{
    /// <summary>The project that hosts ADR-012's emitters.</summary>
    const string GeneratorProject = "CyberCloud.ResourceManager.Generator";

    /// <summary>
    ///     Where the generated OpenAPI documents are checked in.
    ///     <para>
    ///         ⚠ Tracked, not under <c>artifacts/</c>. docs/plan/21 § OpenAPI makes the document "a
    ///         build artifact that is <b>diffed</b>", and <c>artifacts/</c> is gitignored — a file git
    ///         cannot see is a file no diff can fail on.
    ///     </para>
    /// </summary>
    AbsolutePath OpenApiDirectory => RootDirectory / "openapi";

    /// <summary>
    ///     Where ADR-012's other three surfaces are checked in — the <c>cyc</c> verb tree, the .NET
    ///     SDK and the portal form schemas.
    ///     <para>
    ///         ⚠ Tracked, for the same reason <c>openapi/</c> is: docs/plan/23 § The architecture
    ///         gates asks that all four "regenerate byte-identically", and a byte comparison needs a
    ///         previous copy. Under <c>artifacts/</c> — which is gitignored — a fresh clone would have
    ///         nothing to compare against and every run would report "new", which is a gate that can
    ///         only pass.
    ///     </para>
    ///     <para>
    ///         A separate root from <c>openapi/</c> because docs/plan/10 § Shape makes the gateway
    ///         serve that directory as files, and these three are not served to anyone.
    ///     </para>
    /// </summary>
    AbsolutePath DerivedSurfacesDirectory => RootDirectory / "generated";

    /// <summary>Where docs/plan/03 § Providers puts every provider. Matched at any depth below.</summary>
    AbsolutePath ProvidersRoot => RootDirectory / "src" / "Providers";

    AbsolutePath GenerationReportFile => ArtifactsDirectory / "generation-report.json";

    /// <summary>The built assembly of a project, in the configuration this run is building.</summary>
    AbsolutePath AssemblyOf(string project) =>
        ArtifactsDirectory / "bin" / project / Configuration.ToLowerInvariant() / (project + ".dll");

    /// <summary>
    ///     Every provider implementation assembly, for the generator to load.
    ///     <para>
    ///         Read off the solution rather than globbed off disk, so a provider that is on disk and
    ///         not in <c>CyberCloud.slnx</c> is missing from the build <i>and</i> from the generated
    ///         document, rather than from one of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>src/Providers/</c> is the only source. docs/plan/03 § Providers puts every provider
    ///         there, and a provider anywhere else is a repository-layout problem rather than something
    ///         this predicate should quietly accommodate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Match anywhere under <c>src/Providers/</c>, not at a fixed depth.</b> An earlier
    ///         version tested <c>Parent?.Parent?.Name == "Providers"</c>, which assumes
    ///         <c>src/Providers/{Name}/{Name}.csproj</c>. docs/plan/03 § Providers gives a provider
    ///         <i>five</i> projects under one folder, so the real path is
    ///         <c>src/Providers/{Name}/{Name}/{Name}.csproj</c> — one level deeper. The predicate
    ///         matched nothing, and because a registry with no providers is a legitimate state, the
    ///         target reported "no provider is registered" and <b>passed</b> with the first provider
    ///         sitting in the solution. A discovery rule that silently finds nothing is the failure
    ///         mode this file's own vacuous-pass warning exists to make visible; depth was the wrong
    ///         thing to key on.
    ///     </para>
    /// </summary>
    IReadOnlyList<AbsolutePath> ProviderAssemblies =>
        Solution.AllProjects
            .Select(x => (AbsolutePath)x.Path)
            .Where(project => project.Parent is not null
                && ProvidersRoot is not null
                && project.ToString().StartsWith(ProvidersRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(project => SuiteOwning(project) is null)
            .Where(project => !project.NameWithoutExtension.EndsWith(".Contracts", StringComparison.Ordinal)
                && !project.NameWithoutExtension.EndsWith(".Application", StringComparison.Ordinal))
            .OrderBy(project => project.NameWithoutExtension, StringComparer.Ordinal)
            .Select(project => AssemblyOf(project.NameWithoutExtension))
            .ToList();

    void GenerateSurfaces()
    {
        if (!SolutionHasProjects)
        {
            SkippingEmptySolution(nameof(Generate));
            return;
        }

        var report = RunGenerator(write: true);

        Log.Information(
            "Generate: {Providers} provider namespace(s) across {Assemblies} provider assembl{Suffix}, "
            + "{Types} resource type(s), {Versions} api-version(s) → {Directory}/",
            report.Providers,
            report.AssembliesScanned,
            report.AssembliesScanned == 1 ? "y" : "ies",
            report.ResourceTypes,
            report.ApiVersions,
            OpenApiDirectory.Name);

        if (report.Providers == 0)
        {
            // The same distinction Build.Architecture.cs draws with GateStatus.Vacuous: a run that
            // inspected nothing is not a run that found nothing wrong. It still wrote openapi/index.json
            // — a valid, empty OpenAPI document that says zero in three places — so "the generator did
            // not run" and "the generator found no provider" are different files on disk.
            Log.Warning(
                "Generate: no provider is registered, so there is no api-version to describe and "
                + "openapi/index.json is the only document. That is a pass and it is worth nobody's "
                + "trust yet — docs/plan/03 § Providers has twenty of them planned.");
        }

        var failures = new List<string>();

        foreach (var document in report.Documents)
        {
            foreach (var problem in document.StructuralProblems)
                failures.Add($"{document.File} is not a valid OpenAPI 3.1 document: {problem}");

            foreach (var breaking in document.BreakingChanges)
            {
                failures.Add(
                    $"{document.File} breaks api-version {document.ApiVersion}, which is published and "
                    + $"immutable: {breaking}");
            }

            if (document.Drifted && document.Published)
            {
                failures.Add(
                    $"{OpenApiDirectory.Name}/{document.File} was not what the provider registry "
                    + "generates. It has been rewritten in place — review the diff and commit it. "
                    + "docs/plan/23 § The architecture gates, row Generated surfaces.");
            }
        }

        foreach (var stale in report.Stale)
        {
            failures.Add(
                $"{OpenApiDirectory.Name}/{stale} is checked in and this run did not produce it, so the "
                + "api-version it describes has left the registry. Removing a version needs a 12-month "
                + "notice window — docs/plan/08 § The provider registry.");
        }

        foreach (var surface in report.Derived)
        {
            foreach (var problem in surface.Problems)
                failures.Add($"{DerivedSurfacesDirectory.Name}/{surface.File} is not usable: {problem}");

            if (surface.Drifted && surface.Published)
            {
                failures.Add(
                    $"{DerivedSurfacesDirectory.Name}/{surface.File} was not what the OpenAPI document "
                    + "generates. It has been rewritten in place — review the diff and commit it. "
                    + "docs/plan/23 § The architecture gates, row Generated surfaces.");
            }
        }

        foreach (var stale in report.DerivedStale)
        {
            failures.Add(
                $"{DerivedSurfacesDirectory.Name}/{stale} is checked in and this run did not produce "
                + "it. A generated surface nothing generates is one nobody can reproduce.");
        }

        if (failures.Count == 0)
        {
            Log.Information(
                "Generate: {Count} document(s) regenerate byte-identically and break nothing published",
                report.Documents.Count);

            return;
        }

        foreach (var failure in failures)
            Log.Error("Generate: {Failure}", failure);

        Assert.Fail(
            $"{failures.Count} generated-surface problem(s). Listed above. ADR-012's whole claim is that "
            + "a generated surface cannot drift from the registry, and a gate that does not fail is the "
            + "claim without the mechanism.");
    }

    // ── Running the generator and reading its report ──────────────────────────────────────────

    sealed record GeneratedFile(
        string File,
        string ApiVersion,
        bool Published,
        bool Drifted,
        IReadOnlyList<string> StructuralProblems,
        IReadOnlyList<string> BreakingChanges);

    sealed record DerivedFile(
        string Surface,
        string File,
        string ApiVersion,
        bool Published,
        bool Drifted,
        IReadOnlyList<string> Problems);

    sealed record GenerationReport(
        int Providers,
        int ResourceTypes,
        int ApiVersions,
        int AssembliesScanned,
        IReadOnlyList<GeneratedFile> Documents,
        IReadOnlyList<string> Stale,
        IReadOnlyList<DerivedFile> Derived,
        IReadOnlyList<string> DerivedStale);

    /// <summary>
    ///     Runs the generator and parses its report.
    /// </summary>
    /// <param name="write">
    ///     <c>false</c> for the <c>Architecture</c> gate, which must not modify the tree it inspects.
    /// </param>
    /// <remarks>
    ///     ⚠ The generator exits non-zero only when it could not run at all — a drifted or
    ///     incompatible document is exit 0 plus a report saying so. The reason is on its <c>Main</c>:
    ///     two callers need the same facts and reach different verdicts about them.
    /// </remarks>
    GenerationReport RunGenerator(bool write)
    {
        Assert.True(
            ShippingProjectFiles.TryGetValue(GeneratorProject, out var project),
            $"{GeneratorProject} is not in {SolutionFile.Name}. ADR-012's generation step cannot run, and "
            + "the Generated surfaces gate would pass over an API surface nobody generated.");

        Assert.FileExists(
            AssemblyOf(GeneratorProject),
            $"{GeneratorProject} has no built assembly. Run ./build.sh Compile first, in the same "
            + $"configuration ({Configuration}).");

        var arguments = new List<string>
        {
            "--output", OpenApiDirectory,
            "--derived-output", DerivedSurfacesDirectory,
            "--report", GenerationReportFile
        };

        if (!write)
            arguments.Add("--check");

        foreach (var assembly in ProviderAssemblies)
        {
            Assert.FileExists(
                assembly,
                $"A provider project is in {SolutionFile.Name} and has no built assembly at {assembly}. "
                + "The generated document would silently lose that provider's whole API surface.");

            arguments.Add("--provider-assembly");
            arguments.Add(assembly);
        }

        // ⚠ `dotnet run --no-build`, not `dotnet exec <dll>`, and the reason is quoting rather than
        // taste. Nuke's ArgumentStringHandler treats a pre-joined string as ONE value and quotes the
        // whole of it, so `DotNet(string.Join(' ', …))` reaches the process as a single argument and
        // fails with "Could not execute because the specified command or file was not found."
        // SetApplicationArguments takes the parts and quotes each one, which is also how
        // Build.Test.cs passes arguments to a test host.
        DotNetTasks.DotNetRun(s => s
            .SetConfiguration(Configuration)
            .EnableNoRestore()
            .EnableNoBuild()
            .EnableNoLaunchProfile()
            .SetProjectFile(project)
            .SetApplicationArguments(arguments.ToArray()));

        return Parse(GenerationReportFile);
    }

    static GenerationReport Parse(AbsolutePath reportFile)
    {
        Assert.FileExists(reportFile, $"{GeneratorProject} wrote no report at {reportFile}.");

        var root = JsonNode.Parse(reportFile.ReadAllBytes())?.AsObject()
                   ?? throw new JsonException($"{reportFile} is not a JSON object.");

        var documents = (root["documents"] as JsonArray ?? new JsonArray())
            .Select(x => x!.AsObject())
            .Select(x => new GeneratedFile(
                x["file"]!.GetValue<string>(),
                x["apiVersion"]!.GetValue<string>(),
                x["published"]!.GetValue<bool>(),
                x["drifted"]!.GetValue<bool>(),
                Strings(x["structuralProblems"]),
                Strings(x["breakingChanges"])))
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToList();

        var derived = (root["derived"] as JsonArray ?? new JsonArray())
            .Select(x => x!.AsObject())
            .Select(x => new DerivedFile(
                x["surface"]!.GetValue<string>(),
                x["file"]!.GetValue<string>(),
                x["apiVersion"]!.GetValue<string>(),
                x["published"]!.GetValue<bool>(),
                x["drifted"]!.GetValue<bool>(),
                Strings(x["problems"])))
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToList();

        return new(
            root["providers"]!.GetValue<int>(),
            root["resourceTypes"]!.GetValue<int>(),
            root["apiVersions"]!.GetValue<int>(),
            root["assembliesScanned"]!.GetValue<int>(),
            documents,
            Strings(root["stale"]),
            derived,
            Strings(root["derivedStale"]));
    }

    static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<string>()).ToList();
}
