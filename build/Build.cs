// docs/plan/03 § build/ — the target graph. Everything a target actually does lives in a sibling
// partial: Build.Compile.cs, Build.Test.cs, Build.Generate.cs, Build.Charts.cs, Build.Images.cs,
// Build.Architecture.cs, Build.Licence.cs, Build.Portal.cs, Build.E2E.cs, Build.Chaos.cs,
// Build.Load.cs, Build.Publish.cs. Three files are not partials of this class and are named for what
// they read rather than for a target: ArchitectureFacts.cs (assembly metadata), CoverageReport.cs
// (Cobertura) and TargetPreconditions.cs (the shape of an honest block).
//
// One partial per target, named after it. docs/plan/03 § Top level stopped at Build.Licence.cs and
// has been extended to list all of them: doc 03's tree describes this directory, docs/plan/23
// § Build is the authority on which targets exist, and the two disagreeing is how a target goes
// missing.

using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Serilog;

/// <summary>
///     The Cyber Cloud build. <c>./build.sh &lt;Target&gt;</c> is the single entry point for every
///     build action, locally and in CI — docs/plan/23 § Build.
/// </summary>
sealed partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    // ── Parameters ────────────────────────────────────────────────────────────────────────────

    [Parameter("Build configuration. Debug locally, Release on CI.")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    // ── Well-known paths ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The solution, read from <c>.nuke/parameters.json</c> and overridable with
    ///     <c>--solution</c>.
    ///     <para>
    ///         ✔ Verified that Nuke 10.1.0 parses the <c>.slnx</c> XML solution format — it depends
    ///         on <c>Microsoft.VisualStudio.SolutionPersistence</c>, and a probe target printed
    ///         <c>Parsed solution: …/CyberCloud.slnx</c> and <c>Project count: 0</c>. This is worth
    ///         recording because <c>.slnx</c> is new enough that a build tool not supporting it is
    ///         the obvious thing to fear.
    ///     </para>
    /// </summary>
    // `= null!` because Nuke assigns injected fields by reflection after construction, and
    // .editorconfig makes CS8618 (uninitialised non-nullable field) an error.
    [Solution] readonly Solution Solution = null!;

    AbsolutePath SolutionFile => Solution.Path!;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";

    /// <summary>
    ///     The directories that contain buildable .NET code. <c>references/</c> is excluded on
    ///     purpose — docs/plan/03 § Top level: "read-only, not built, not restored".
    /// </summary>
    IEnumerable<AbsolutePath> SourceRoots =>
        new[] { RootDirectory / "src", RootDirectory / "test", RootDirectory / "cli" }
            .Where(x => x.DirectoryExists());

    // ── Target graph ──────────────────────────────────────────────────────────────────────────
    //
    //   Clean
    //   Restore ──► Compile ──┬──► Test
    //                         ├──► Generate
    //                         ├──► Architecture
    //                         ├──► E2E            (blocks: no suite, no staging, no cyc)
    //                         ├──► Chaos          (blocks: no suite, no cluster)
    //                         ├──► Load           (blocks: no suite, no environment)
    //                         ├──► Charts ────────┐
    //                         └──► Images ────────┴──► Licence (stub)
    //   Portal (stub)
    //
    //   Publish ──► Test, Generate, Architecture, Portal, Licence   (blocks: no version, no feeds, no cyc)
    //
    // Publish's fan-in is written out rather than drawn: it reaches five nodes from three different
    // rows above, and the lines needed to show that cost more than they explain.
    //
    // ⚠ "BLOCKS" IS NOT "STUB", AND THE DIFFERENCE IS THE WHOLE DESIGN OF THOSE FIVE TARGETS. A stub
    // logs that it is unwritten and succeeds. A blocked target is written — it discovers its work,
    // reports how much of it there is, and then fails naming every input it does not have and the
    // command or parameter that would supply each. See TargetPreconditions.cs.
    //
    // The earlier note here said a stub that fails the build is worse than no stub, because it trains
    // everyone to ignore a red target. That is true of `Test` and `Architecture`, which gate every PR
    // (docs/plan/23 § CI shape) and must be green on a clean checkout. It is not true of these five:
    // none is on the PR path, each is invoked deliberately by somebody who wants images pushed or
    // staging exercised, and answering "nothing was pushed, and here is why" with exit 0 is how a
    // release goes out with no images in it.
    //
    // ⚠ Three edges that are missing on purpose, because each is the first thing a reader looks for:
    //
    // * There is no `Deploy` target, so `E2E`, `Chaos` and `Load` — docs/plan/23 § Build, "against a
    //   real deployment" — depend only on `Compile`, which builds the suites in test/ and the `cyc`
    //   they drive. The deployment is an input to the run, not something the graph produces: the
    //   nightly suites run against standing staging (docs/plan/23 § Environments and rollout) and
    //   the pre-release ones against a deployed candidate. Encoding "deploy first" as an edge would
    //   mean every local `./build.sh E2E` tried to deploy something.
    //
    // * `Portal` depends on nothing — see Build.Portal.cs, which is about keeping the pnpm and .NET
    //   toolchains independently invocable.
    //
    // * `Publish` does not depend on `E2E`/`Chaos`/`Load`, even though a release is gated on them —
    //   see Build.Publish.cs. They run against the deployed candidate, which is after the gate.

    Target Clean => _ => _
        .Description("Delete every build output. Never touches references/.")
        .Executes(CleanOutputs);

    Target Restore => _ => _
        .Description("dotnet restore over CyberCloud.slnx, with CPM.")
        .Executes(RestoreSolution);

    Target Compile => _ => _
        .Description("dotnet build over CyberCloud.slnx — deterministic, warnings are errors.")
        .DependsOn(Restore)
        .Executes(CompileSolution);

    Target Test => _ => _
        .Description("Unit and grain tests. docs/plan/23 § Test layers.")
        .DependsOn(Compile)
        .Executes(RunTests);

    Target Generate => _ => _
        .Description("Provider registry → OpenAPI → CLI verbs → SDK → portal forms (ADR-012). Fails on drift.")
        .DependsOn(Compile)
        .Executes(GenerateSurfaces);

    Target Architecture => _ => _
        .Description("The ten architecture gates. docs/plan/23 § The architecture gates.")
        .DependsOn(Compile)
        .Executes(CheckArchitecture);

    Target Charts => _ => _
        .Description("Registry → chart @param block, helm lint, values.schema.json, drift check, package.")
        // ⚠ This edge landed with ADR-012's fifth surface. A chart's @param block is now generated
        // from the provider registry (ADR-010 § Which end authors the schema), and building that
        // registry means running each provider's Describe — so this target needs the same compiled
        // assemblies Generate does. Before that, `Charts` needed only `helm`.
        .DependsOn(Compile)
        .Executes(BuildCharts);

    Target Images => _ => _
        .Description("Container images, SBOM (Syft), signatures (cosign), push by digest.")
        .DependsOn(Compile)
        .Executes(BuildImages);

    Target Licence => _ => _
        .Description("ADR-011 licence scan over charts and images.")
        .DependsOn(Charts, Images)
        .Executes(ScanLicences);

    Target Portal => _ => _
        .Description("pnpm install/lint/test/build over portal/, performance budget, axe.")
        .Executes(BuildPortal);

    Target E2E => _ => _
        .Description("Playwright + `cyc` against a real deployment. docs/plan/23 § Test layers.")
        .DependsOn(Compile)
        .Executes(RunE2ETests);

    Target Chaos => _ => _
        .Description("The seven chaos invariants against a real deployment. docs/plan/23.")
        .DependsOn(Compile)
        .Executes(RunChaosTests);

    Target Load => _ => _
        .Description("The six load scenarios against a real deployment. docs/plan/23.")
        .DependsOn(Compile)
        .Executes(RunLoadTests);

    Target Publish => _ => _
        .Description("NuGet, npm, charts and `cyc` binaries per RID, behind the full gate.")
        .DependsOn(Test, Generate, Architecture, Portal, Licence)
        .Executes(PublishArtefacts);

    // ── Shared helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Logs that a target is wired but not implemented, naming where the work is tracked, and
    ///     returns normally. See the note on the target graph above for why this does not throw.
    /// </summary>
    static void NotImplementedYet(string target, string what, string trackedIn)
    {
        // No "{Target}:" prefix on the warning — Nuke already prefixes the target name in both the
        // "Errors & Warnings" summary and the build.log line format, and three copies of it on one
        // line reads like a bug.
        Log.Warning("Not implemented yet — tracked in {TrackedIn}", trackedIn);
        Log.Information("When implemented, {Target} will: {What}", target, what);
    }

    /// <summary>
    ///     Whether the solution currently contains any project.
    ///     <para>
    ///         The repository legitimately has zero projects right now (docs/plan/03 — the directory
    ///         skeleton landed before the assemblies). Verified on SDK 10.0.302 that
    ///         <c>dotnet restore</c>, <c>dotnet build</c> and <c>dotnet test</c> against the empty
    ///         <c>CyberCloud.slnx</c> all exit 0, emitting only
    ///         <c>warning : Unable to find a project to restore!</c>. Relying on that staying true
    ///         across SDK releases would be a gate that silently flips red on an SDK bump, so the
    ///         emptiness is detected here and the .NET invocation is skipped explicitly instead.
    ///     </para>
    /// </summary>
    bool SolutionHasProjects => Solution.AllProjects.Count > 0;

    void SkippingEmptySolution(string target)
        => Log.Information(
            "{Target}: {Solution} contains no projects yet — nothing to do, and that is a pass, not a "
            + "skipped gate. docs/plan/03 § Top level.",
            target,
            SolutionFile.Name);
}
