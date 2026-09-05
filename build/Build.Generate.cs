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

    /// <summary>
    ///     Where the portal's TypeScript client is checked in.
    ///     <para>
    ///         ⚠ <b>Not under <c>generated/</c>, and rule 6 is why.</b> docs/plan/03 § Assembly graph
    ///         rules says <c>portal/libs/api</c> has no hand-written files and the generator owns the
    ///         directory, and <c>Build.Architecture</c>'s rule 6 enforces that by reading the
    ///         head of every file <i>there</i>. Writing under
    ///         <c>generated/</c> and copying across would make that gate inspect a copy, and the copy
    ///         step would be the one part of the chain nothing checked.
    ///     </para>
    /// </summary>
    AbsolutePath PortalApiDirectory => RootDirectory / "portal" / "libs" / "api";

    /// <summary>How a message names that directory — repository-relative, with forward slashes.</summary>
    const string PortalApiRelative = "portal/libs/api";

    /// <summary>
    ///     The subdirectory of <c>generated/</c> holding the .NET SDK — <c>SdkEmitter.DirectoryName</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A literal rather than the constant, because <c>build/_build.csproj</c> is deliberately
    ///     outside <c>CyberCloud.slnx</c> and references nothing under <c>src/</c> — the same reason
    ///     <see cref="PortalApiRelative" /> above is a literal. It cannot drift silently: the
    ///     directory is where <c>Generate</c> writes and where <see cref="GeneratedSdkFiles" />
    ///     reads, so a rename empties this glob and the gate reports ○ over zero files rather than ✔.
    /// </remarks>
    const string SdkSurfaceDirectory = "sdk";

    /// <summary>The hand-written half the generated one is compiled against — issue #73.</summary>
    const string SdkAssemblyName = "CyberCloud.Sdk";

    /// <summary>
    ///     Every checked-in file of the .NET SDK surface, oldest api-version first.
    /// </summary>
    /// <remarks>
    ///     Globbed off disk rather than taken from the generation report, deliberately: issue #73 is
    ///     about the file that is CHECKED IN never reaching a compiler, and a list derived from the
    ///     generator would compile what the generator just produced instead. The two are equal
    ///     whenever the <c>Generated surfaces</c> row is green, and the whole point is not to depend
    ///     on that.
    /// </remarks>
    IReadOnlyList<AbsolutePath> GeneratedSdkFiles =>
        (DerivedSurfacesDirectory / SdkSurfaceDirectory) is var directory && directory.DirectoryExists()
            ? directory.GlobFiles("*.cs").OrderBy(x => x.Name, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    ///     Each of those files as a C# compiler sees it — <see cref="GeneratedSdkSurface" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One compilation per file, never one over the set.</b> The generated types carry no
    ///     api-version in their namespace, so two published api-versions declare the same type names;
    ///     compiling them together would be <c>CS0101</c> on every model in the SDK. The reasoning is
    ///     on <see cref="GeneratedSdkSurface" />, and it is the reason this is not a throwaway
    ///     <c>.csproj</c>.
    /// </remarks>
    // List rather than IReadOnlyList: CA1859 is an error here and this is a private helper — the
    // same reason Build.Architecture.cs § ShippingProjectFiles returns a Dictionary.
    List<GeneratedSdkFile> CompiledGeneratedSdk()
    {
        var references = new[] { AssemblyOf(SdkAssemblyName) };

        return GeneratedSdkFiles.Select(file => GeneratedSdkSurface.Compile(file, references)).ToList();
    }

    /// <summary>
    ///     The reason the compilation above could not be trusted, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reported as one line rather than left to produce hundreds. Without
    ///     <c>CyberCloud.Sdk.dll</c> every <c>Response&lt;T&gt;</c>, <c>Operation&lt;T&gt;</c>,
    ///     <c>WaitUntil</c> and <c>AsyncPageable&lt;T&gt;</c> in the file is <c>CS0246</c> — a gate
    ///     failing loudly for a reason that has nothing to do with the file it is inspecting, which
    ///     is how a reader learns to disbelieve it.
    /// </remarks>
    string? GeneratedSdkBlocker()
        => AssemblyOf(SdkAssemblyName).FileExists()
            ? null
            : $"{SdkAssemblyName} has no built assembly at {AssemblyOf(SdkAssemblyName)}, so the "
            + $"generated SDK cannot be compiled against the shapes it names — Response<T>, "
            + $"Operation<T>, WaitUntil and AsyncPageable<T> are all its. Run ./build.sh Compile "
            + $"first, in the same configuration ({Configuration}).";

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

        // ⚠ THE .NET SDK IS COMPILED, HERE AND IN `Architecture` — issue #73. This target has just
        // rewritten the file, so it is the earliest place an emitter change that produces invalid C#
        // can be told about it; the gate in Build.Architecture.cs is the one CI forms a verdict from.
        // The alternative — leaving it to `Architecture` alone — is an author who runs `Generate`,
        // sees green, commits, and learns from CI what the compiler already knew locally.
        if (GeneratedSdkBlocker() is { } blocker)
        {
            failures.Add(blocker);
        }
        else
        {
            foreach (var compiled in CompiledGeneratedSdk())
            {
                foreach (var error in compiled.Errors)
                {
                    failures.Add(
                        $"{DerivedSurfacesDirectory.Name}/{SdkSurfaceDirectory}/{compiled.File} does "
                        + $"not compile — {error}. The Generated surfaces comparison is byte-for-byte "
                        + "and byte-identical is not valid; fix the emitter, not the file.");
                }
            }
        }

        // ⚠ THE PORTAL'S CLIENT — issue #21. Reported against its own directory rather than folded
        // into the three above, because it is written to portal/libs/api and a message naming
        // generated/ would send a reader to look at the wrong tree.
        foreach (var problem in report.TypeScriptProblems)
            failures.Add($"{PortalApiRelative} is not usable: {problem}");

        foreach (var file in report.TypeScript)
        {
            if (file.Drifted && file.Published)
            {
                failures.Add(
                    $"{PortalApiRelative}/{file.File} was not what the OpenAPI document generates. It "
                    + "has been rewritten in place — review the diff and commit it. docs/plan/23 § The "
                    + "architecture gates, row Generated surfaces.");
            }
        }

        foreach (var stale in report.TypeScriptStale)
        {
            failures.Add(
                $"{PortalApiRelative}/{stale} is checked in and this run did not produce it. "
                + "docs/plan/03 § Assembly graph rules, rule 6 gives this directory to the generator, "
                + "so a file it does not produce is a hand-written one.");
        }

        if (failures.Count == 0)
        {
            Log.Information(
                "Generate: {Count} document(s) regenerate byte-identically and break nothing published; "
                + "{Client} TypeScript client file(s) for the portal",
                report.Documents.Count,
                report.TypeScript.Count);

            // ⚠ Zero files is news rather than silence, the same distinction Build.Charts.cs draws:
            // a run that emitted no client is not a run that emitted a correct one, and a portal
            // with nothing to import is issue #21's whole state.
            if (report.TypeScript.Count == 0)
            {
                Log.Warning(
                    "Generate: the TypeScript client is empty, so portal/libs/api holds nothing the "
                    + "portal can import and every page would hand-roll its calls — issue #21.");
            }

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

    /// <summary>One chart whose <c>@param</c> block was regenerated — ADR-012's fifth surface.</summary>
    /// <param name="Chart">The chart, relative to <c>charts/</c>.</param>
    /// <param name="File">The file, relative to <c>charts/</c>.</param>
    /// <param name="ResourceType">The type the chart is the configuration surface of.</param>
    /// <param name="ApiVersion">Which api-version's body was emitted.</param>
    /// <param name="Published">Whether the file was already checked in.</param>
    /// <param name="Drifted">Whether the regenerated bytes differ from the checked-in ones.</param>
    /// <param name="Problems">What the registry could not say in the annotation vocabulary.</param>
    /// <param name="PreservedInternalLines">
    ///     Lines of <c>@internal</c> region carried through from the checked-in file untouched, at every
    ///     depth. Reported in the drift message so it states what the rewrite did rather than asserting
    ///     it — see the remarks on <c>ChartAnnotationEmitter.Rewrite</c>.
    /// </param>
    sealed record ChartAnnotationFile(
        string Chart,
        string File,
        string ResourceType,
        string ApiVersion,
        bool Published,
        bool Drifted,
        IReadOnlyList<string> Problems,
        int PreservedInternalLines);

    /// <summary>
    ///     One action a provider declared, as the registry holds it.
    ///     <para>
    ///         ⚠ <b>Read off the registry rather than off a generated surface, because none of the
    ///         four carries it.</b> An action reaches <c>openapi/</c>, the <c>cyc</c> verb tree, the
    ///         SDK and the portal form from its declaration alone; whether anything can serve it is
    ///         not part of any of those documents, so a document publishing an action nothing can run
    ///         is byte-identical to one publishing an action that works. <see cref="Handler" /> is
    ///         the fact the <c>Action handlers</c> gate forms its verdict from.
    ///     </para>
    /// </summary>
    /// <param name="Type">The resource type, for example <c>CyberCloud.Storage/accounts</c>.</param>
    /// <param name="Name">The action, for example <c>listKeys</c>.</param>
    /// <param name="LongRunning">
    ///     Whether the action answers <c>202</c> and drives the type's reconciler through an
    ///     operation. A long-running action needs no handler and must never be asked for one.
    /// </param>
    /// <param name="Secret">Whether the response carries secret material.</param>
    /// <param name="Handler">
    ///     The <c>IResourceActionHandler</c> implementation's full name, or <c>null</c> when the
    ///     provider declared none.
    /// </param>
    sealed record DeclaredAction(
        string Type,
        string Name,
        bool LongRunning,
        bool Secret,
        string? Handler);

    sealed record GenerationReport(
        int Providers,
        int ResourceTypes,
        int ApiVersions,
        int AssembliesScanned,
        IReadOnlyList<GeneratedFile> Documents,
        IReadOnlyList<string> Stale,
        IReadOnlyList<DerivedFile> Derived,
        IReadOnlyList<string> DerivedStale,
        IReadOnlyList<ChartAnnotationFile> ChartAnnotations,
        IReadOnlyList<string> ChartUnpaired,
        int ManagedCharts,
        int TypesNamingAChart,
        IReadOnlyList<DeclaredAction> Actions,
        IReadOnlyList<TypeScriptFile> TypeScript,
        IReadOnlyList<string> TypeScriptProblems,
        IReadOnlyList<string> TypeScriptStale);

    /// <summary>One file of the portal's generated TypeScript client — issue #21.</summary>
    /// <param name="File">The path under <c>portal/libs/api</c>.</param>
    /// <param name="ApiVersion">The api-version it was generated at.</param>
    /// <param name="Published">Whether the file was already checked in.</param>
    /// <param name="Drifted">Whether the regenerated bytes differ from the checked-in ones.</param>
    sealed record TypeScriptFile(string File, string ApiVersion, bool Published, bool Drifted);

    /// <summary>
    ///     Runs the generator and parses its report.
    /// </summary>
    /// <param name="write">
    ///     <c>false</c> for the <c>Architecture</c> gate, which must not modify the tree it inspects.
    /// </param>
    /// <param name="charts">
    ///     Whether to regenerate the fifth surface — a managed chart's <c>@param</c> block.
    ///     <para>
    ///         ⚠ <b>Off by default, because only <c>Charts</c> forms a verdict about it.</b> ADR-010
    ///         § Which end authors the schema puts that gate in <c>Build.Charts</c>, and a run that
    ///         rewrote a chart while <c>Generate</c> was reporting on OpenAPI would leave a modified
    ///         file nothing in that target's output mentioned.
    ///     </para>
    /// </param>
    /// <remarks>
    ///     ⚠ The generator exits non-zero only when it could not run at all — a drifted or
    ///     incompatible document is exit 0 plus a report saying so. The reason is on its <c>Main</c>:
    ///     two callers need the same facts and reach different verdicts about them.
    /// </remarks>
    GenerationReport RunGenerator(bool write, bool charts = false)
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
            // ⚠ Always, unlike --charts. The fifth surface is off by default because only `Charts`
            // forms a verdict about it; the TypeScript client is `Generate`'s own, exactly as the
            // three under generated/ are, and a run that skipped it would leave a checked-in client
            // that nothing regenerated — which is the state issue #21 describes with a client that
            // did not exist at all.
            "--typescript", PortalApiDirectory,
            "--report", GenerationReportFile
        };

        if (charts)
        {
            arguments.Add("--charts");
            arguments.Add(ChartsDirectory);
        }

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

        var chartAnnotations = (root["chartAnnotations"] as JsonArray ?? new JsonArray())
            .Select(x => x!.AsObject())
            .Select(x => new ChartAnnotationFile(
                x["chart"]!.GetValue<string>(),
                x["file"]!.GetValue<string>(),
                x["resourceType"]!.GetValue<string>(),
                x["apiVersion"]!.GetValue<string>(),
                x["published"]!.GetValue<bool>(),
                x["drifted"]!.GetValue<bool>(),
                Strings(x["problems"]),
                x["preservedInternalLines"]?.GetValue<int>() ?? 0))
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToList();

        var typescript = (root["typescript"] as JsonArray ?? new JsonArray())
            .Select(x => x!.AsObject())
            .Select(x => new TypeScriptFile(
                x["file"]!.GetValue<string>(),
                x["apiVersion"]!.GetValue<string>(),
                x["published"]!.GetValue<bool>(),
                x["drifted"]!.GetValue<bool>()))
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToList();

        var actions = (root["actions"] as JsonArray ?? new JsonArray())
            .Select(x => x!.AsObject())
            .Select(x => new DeclaredAction(
                x["type"]!.GetValue<string>(),
                x["name"]!.GetValue<string>(),
                x["longRunning"]!.GetValue<bool>(),
                x["secret"]!.GetValue<bool>(),
                // ⚠ A JSON null is a JsonNode that is absent from the object, not a JsonNull node —
                // System.Text.Json.Nodes drops it — so both the missing key and the declared-null case
                // arrive here the same way, which is what "the provider named no handler" means.
                x["handler"] is { } handler ? handler.GetValue<string>() : null))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        return new(
            root["providers"]!.GetValue<int>(),
            root["resourceTypes"]!.GetValue<int>(),
            root["apiVersions"]!.GetValue<int>(),
            root["assembliesScanned"]!.GetValue<int>(),
            documents,
            Strings(root["stale"]),
            derived,
            Strings(root["derivedStale"]),
            chartAnnotations,
            Strings(root["chartUnpaired"]),
            root["chartManagedCharts"]?.GetValue<int>() ?? 0,
            root["chartTypesNamingAChart"]?.GetValue<int>() ?? 0,
            actions,
            typescript,
            Strings(root["typescriptProblems"]),
            Strings(root["typescriptStale"]));
    }

    static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? new JsonArray()).Select(x => x!.GetValue<string>()).ToList();
}
