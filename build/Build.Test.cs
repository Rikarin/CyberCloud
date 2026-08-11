// Test — docs/plan/23 § Build, row `Test`: "Unit + grain tests, coverage floor per project".
// The floor is docs/plan/23 § Test layers, row Unit: "Coverage ≥ 70 % per project". It is enforced
// at the bottom of this file; CoverageReport.cs is the half that reads a report.
//
// The discovery half of this file is shared: it decides which target runs each test project, not
// only what `Test` runs. See TestSuite below. So is the run half — `E2E`, `Chaos` and `Load` drive
// their own suites through RunSuites, so there is one answer to "how is a test host invoked".

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;

partial class Build
{
    /// <summary>
    ///     The target that runs a test project.
    ///     <para>
    ///         ⚠ This enum is the point of the file's discovery half. <c>Directory.Build.props</c>
    ///         § "Project role detection" and this build were once asking the same question through
    ///         one list, and they are not the same question:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             props asks <em>does this build as an xunit.v3 / Microsoft.Testing.Platform
    ///             host?</em> — true for all of them, the deployment-driven suites included;
    ///         </item>
    ///         <item>
    ///             this asks <em>which target runs it, and how often?</em> — and docs/plan/23
    ///             § Test layers puts E2E and Chaos on nightly and Load on weekly, none of them
    ///             per-PR, against a real deployment no per-PR run has.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Observed with the single list, against three empty projects named CyberCloud.E2E,
    ///         .Chaos and .Load: <c>./build.sh Test</c> logged "running 3 test project(s)" and tried
    ///         to run all three. <c>Test</c> gates every PR under a 3-minute budget (docs/plan/23
    ///         § CI shape), so that is the nightly and the weekly suites on every PR, against
    ///         nothing. With the split it logs "no per-PR test projects found".
    ///     </para>
    /// </summary>
    enum TestSuite
    {
        /// <summary>
        ///     <c>Test</c>, every PR: the unit, grain, reconciler, conformance, isolation and
        ///     contract layers of docs/plan/23 § Test layers.
        /// </summary>
        PerPullRequest,

        /// <summary><c>E2E</c> — nightly and pre-release, against a real deployment.</summary>
        EndToEnd,

        /// <summary><c>Chaos</c> — nightly, against a real deployment.</summary>
        Chaos,

        /// <summary><c>Load</c> — weekly and pre-release, against a real deployment.</summary>
        Load,
    }

    /// <summary>
    ///     The suite that owns a project, or <c>null</c> if it is not a test project at all.
    ///     <para>
    ///         ⚠ One arm per rule in <c>Directory.Build.props</c> § Project role detection, in the
    ///         same order, so the two can be diffed by eye. Keeping the arms one-to-one with the
    ///         props rules — rather than listing only the ones <c>Test</c> cares about — is what
    ///         makes "a project that builds as a test host and is run by nothing" unrepresentable:
    ///         a rule cannot appear here without naming the target that runs it.
    ///         <see cref="AssertEveryTestProjectIsOwned" /> covers the direction this cannot, a
    ///         project added under test/ and to neither file.
    ///     </para>
    /// </summary>
    static TestSuite? SuiteOwning(AbsolutePath project)
        => project.NameWithoutExtension switch
        {
            var name when name.EndsWith(".Tests", StringComparison.Ordinal) => TestSuite.PerPullRequest,
            var name when name.EndsWith(".Conformance", StringComparison.Ordinal) => TestSuite.PerPullRequest,
            "CyberCloud.E2E" => TestSuite.EndToEnd,
            "CyberCloud.Chaos" => TestSuite.Chaos,
            "CyberCloud.Load" => TestSuite.Load,
            "CyberCloud.Isolation" => TestSuite.PerPullRequest,
            _ => null,
        };

    /// <summary>
    ///     Every test project under <see cref="SourceRoots" />, paired with the suite that runs it.
    ///     Ordered so the run order and the log are stable across machines.
    ///     <para>
    ///         Discovery walks the filesystem rather than the solution deliberately — see
    ///         <see cref="AssertTestProjectsAreInSolution" />.
    ///     </para>
    /// </summary>
    IReadOnlyCollection<(AbsolutePath Project, TestSuite Suite)> ClassifiedTestProjects =>
        SourceRoots
            .SelectMany(root => root.GlobFiles("**/*.csproj"))
            .Select(project => (Project: project, Suite: SuiteOwning(project)))
            .Where(x => x.Suite is not null)
            .Select(x => (x.Project, Suite: x.Suite!.Value))
            .OrderBy(x => x.Project.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     The projects a given target runs. <c>E2E</c>, <c>Chaos</c> and <c>Load</c> are stubs and
    ///     do not call this yet; it lives here rather than in their partials because the
    ///     classification is one decision, and splitting it four ways is how the lists drift apart
    ///     again.
    /// </summary>
    // Returns List rather than IReadOnlyCollection because CA1859 is an error here and this is a
    // private helper — the property below is what the rest of the build sees.
    List<AbsolutePath> ProjectsIn(TestSuite suite) =>
        ClassifiedTestProjects.Where(x => x.Suite == suite).Select(x => x.Project).ToList();

    /// <summary>The projects <c>Test</c> runs — the "Every PR" rows of docs/plan/23 § Test layers.</summary>
    IReadOnlyCollection<AbsolutePath> TestProjects => ProjectsIn(TestSuite.PerPullRequest);

    /// <summary>
    ///     Fails if <c>test/</c> holds a project that no suite claims.
    ///     <para>
    ///         docs/plan/03 § test/ says <c>test/</c> is nothing but the cross-cutting suites, so an
    ///         unclaimed project there is not a library in the wrong place — it is a suite somebody
    ///         added without telling any target about it, which would otherwise show up as a green
    ///         <c>Test</c> that quietly ran one fewer thing. Same reasoning as
    ///         <c>--minimum-expected-tests 1</c> below, one level up.
    ///     </para>
    ///     <para>
    ///         ⚠ Only <c>test/</c>, not <see cref="SourceRoots" />: <c>src/</c> and <c>cli/</c> are
    ///         mostly production projects, where "no suite claims it" is the normal answer.
    ///     </para>
    /// </summary>
    void AssertEveryTestProjectIsOwned()
    {
        var testRoot = RootDirectory / "test";

        if (!testRoot.DirectoryExists())
            return;

        var unowned = testRoot
            .GlobFiles("**/*.csproj")
            .Where(project => SuiteOwning(project) is null)
            .Select(x => x.NameWithoutExtension)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(
            unowned,
            $"test/ holds {unowned.Count} project(s) that no target runs: {string.Join(", ", unowned)}. "
            + "Every project under test/ must be claimed by Test, E2E, Chaos or Load — add it to "
            + "Build.Test.cs § SuiteOwning and to Directory.Build.props § Project role detection, "
            + "which have to agree. docs/plan/03 § test/, docs/plan/23 § Test layers.");
    }

    /// <summary>
    ///     Fails the build if a test project exists on disk but is not a member of the solution.
    ///     <para>
    ///         <c>Compile</c> builds <c>CyberCloud.slnx</c>; discovery here walks the filesystem.
    ///         Those two answers can disagree, and when they do the resulting failure is
    ///         incoherent: the project is never built, <c>Test</c> runs it anyway, and the error
    ///         names a missing <c>.dll</c> instead of the actual mistake, which was a line missing
    ///         from the solution file.
    ///     </para>
    ///     <para>
    ///         Three things could make that coherent — discover from the solution, build what is
    ///         discovered, or fail loudly. This is the third. Discovering from the solution would
    ///         mean a test project can be dropped out of CI by omitting one line, silently and
    ///         permanently, which is precisely the property a gate must not have. Building what is
    ///         discovered papers over the omission so it never gets fixed, and leaves the project
    ///         outside every other solution-scoped gate regardless.
    ///     </para>
    /// </summary>
    void AssertTestProjectsAreInSolution(IReadOnlyCollection<AbsolutePath> projects)
    {
        var inSolution = Solution.AllProjects
            .Select(x => (AbsolutePath)x.Path)
            .ToHashSet();

        var orphans = projects.Where(x => !inSolution.Contains(x)).ToList();
        if (orphans.Count == 0)
            return;

        foreach (var orphan in orphans)
        {
            Log.Error(
                "{Project} is on disk but is not a member of {Solution}, so `Compile` never built "
                + "it and there is nothing for `Test` to run. Add it with: dotnet sln {Solution} "
                + "add {Project}",
                orphan,
                SolutionFile.Name);
        }

        Assert.Fail(
            $"{orphans.Count} test project(s) on disk are missing from {SolutionFile.Name} — "
            + "listed above.");
    }

    void RunTests()
    {
        // ⚠ Both guards run before the empty check, and both cover EVERY test project rather than
        // the per-PR ones about to be run.
        //
        // Discovery is split by owning target, but "does any target run it?" and "is it in the
        // solution?" are questions about all of them: a CyberCloud.Load missing from
        // CyberCloud.slnx is exactly as broken for `Load` as it would be for `Test`. `Test` is the
        // only one of the four that runs on every PR, so it is the only one positioned to notice —
        // which also means neither may hide behind the early return below, or a repository whose
        // only suites are E2E/Chaos/Load would report "nothing to run" over a real defect.
        AssertEveryTestProjectIsOwned();
        AssertTestProjectsAreInSolution(ClassifiedTestProjects.Select(x => x.Project).ToList());

        var projects = TestProjects;

        if (projects.Count == 0)
        {
            // ⚠ This is a PASS, on purpose.
            //
            // docs/plan/23 § CI shape gates every PR on `Test`. A `Test` target that is red because
            // the repository has no test projects yet is a gate that is red from commit one, and a
            // gate everyone has learned to ignore is not a gate. It goes green the moment the first
            // `*.Tests` project lands, and it fails properly the moment a test fails.
            Log.Information(
                "Test: no per-PR test projects found under {Roots} — nothing to run. Discovery is "
                + "*.Tests, *.Conformance and CyberCloud.Isolation; the E2E, Chaos and Load suites "
                + "are owned by their own targets. Build.Test.cs § SuiteOwning.",
                string.Join(", ", SourceRoots.Select(x => x.Name)));
            return;
        }

        TestResultsDirectory.CreateOrCleanDirectory();
        CoverageDirectory.CreateOrCleanDirectory();

        Log.Information("Test: running {Count} test project(s)", projects.Count);

        RunSuites(nameof(Test), projects, environment: null, collectCoverage: true);

        EnforceCoverageFloor();
    }

    // ── Running a suite ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Runs a set of Microsoft.Testing.Platform hosts, in parallel, reporting every failure.
    /// </summary>
    /// <param name="target">The calling target, for the log and the failure message.</param>
    /// <param name="projects">The suites to run.</param>
    /// <param name="environment">
    ///     What the suite needs to find its deployment. <c>Test</c> passes nothing; the three
    ///     deployment-driven targets pass a base URL or a kube context.
    /// </param>
    /// <param name="collectCoverage">
    ///     Whether to wrap each host in <c>dotnet-coverage</c>. Only <c>Test</c> does: docs/plan/23
    ///     § Test layers puts the coverage floor on the Unit row, and instrumenting a load run would
    ///     change the numbers the load run exists to measure.
    /// </param>
    /// <remarks>
    ///     ⚠ `dotnet run`, NOT `dotnet test`. This is the design of this target, not an accident.</remarks>
    void RunSuites(
        string target,
        IReadOnlyCollection<AbsolutePath> projects,
        // Dictionary rather than IReadOnlyDictionary: CA1859 is an error here, and this is internal
        // to the build — the same concession Build.Test.cs § ProjectsIn already makes.
        Dictionary<string, string>? environment,
        bool collectCoverage = false)
    {
        //
        // These are Microsoft.Testing.Platform hosts: OutputType=Exe, xunit.v3, no VSTest adapter.
        // `dotnet test` inserts a runner-selection step in front of that, and when it selects VSTest
        // the run does not fail cleanly — it aborts, with an error about the wrong thing entirely:
        //
        //   An assembly specified in the application dependencies manifest (testhost.deps.json)
        //   was not found: package: 'testhost', version: '18.6.0-release-26329-109'
        //   Test Run Aborted.
        //
        // Even when `dotnet test` selects correctly it summarises rather than reports: a failing
        // assertion surfaces as "error run failed: Tests failed: '…_net10.0_arm64.log'", and the
        // Shouldly message is only inside that log file. Running the host directly puts the
        // assertion, its stack and its source line on stdout, and passes the process exit code
        // (2 on failure) straight through.
        //
        // It also makes every option below a native MTP one, so none of Nuke's VSTest-shaped
        // DotNetTestSettings (`--logger`, `--results-directory`) is in play — those produce
        // `error MTP0001: VSTest-specific properties are set but will be ignored…`.
        //
        // Directory.Build.props § Test runner still configures `dotnet test` to pick MTP. Nothing
        // here depends on that; it is there so a developer running `dotnet test` by hand, or an IDE
        // doing it for them, gets the same answer as CI.
        // ⚠ One invocation is built by hand rather than through DotNetTasks.DotNetRun's CombineWith,
        // and the reason is the coverage prefix. `dotnet-coverage collect … -- dotnet run …` puts a
        // process in front of the one the settings object describes, and no ToolSettings can express
        // that. Every argument below is the one CombineWith produced; the shape of the command is
        // unchanged, which is checkable by eye against the invocation Nuke logs.
        var environmentVariables = environment is null
            ? null
            : EnvironmentInfo.Variables
                .Concat(environment.Where(x => !EnvironmentInfo.Variables.ContainsKey(x.Key)))
                .ToDictionary(x => x.Key, x => environment.TryGetValue(x.Key, out var value) ? value : x.Value);

        var failures = new ConcurrentBag<string>();

        Parallel.ForEach(
            projects,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            project =>
            {
                var name = project.NameWithoutExtension;
                var exitCode = 0;

                // ⚠ --minimum-expected-tests 1 guards the worst outcome this target can have: a
                // project that discovers nothing, runs nothing and reports success. Without it,
                // broken discovery — a bad filter, a lost runner reference, a project that stops
                // being an MTP host — shows up as a green build.
                //
                // The TRX switch is xunit's `--report-xunit-trx`, not MTP's `--report-trx`: the
                // latter lives in Microsoft.Testing.Extensions.TrxReport, which nothing here
                // references, and using it fails with "Unknown option '--report-trx'".
                var run =
                    $"run --configuration {Configuration} --no-restore --no-build --no-launch-profile "
                    + $"--project {project} -- --minimum-expected-tests 1 --report-xunit-trx "
                    + $"--report-xunit-trx-filename {name}.trx --results-directory {TestResultsDirectory}";

                var arguments = collectCoverage && CoverageCollectionIsAvailable
                    ? $"dotnet-coverage collect --nologo --output-format cobertura "
                      + $"--output {CoverageDirectory / $"{name}.cobertura.xml"} -- dotnet {run}"
                    : run;

                DotNetTasks.DotNet(
                    arguments,
                    workingDirectory: RootDirectory,
                    environmentVariables: environmentVariables,
                    exitHandler: process => exitCode = process.ExitCode);

                if (exitCode != 0)
                    failures.Add($"{name} exited {exitCode}");
            });

        // ⚠ Every suite runs before any failure is reported — the same reasoning as
        // Build.Architecture.cs § Report, and the reason the old call passed completeOnFailure.
        Assert.Empty(
            failures.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            $"{failures.Count} of {projects.Count} {target} suite(s) failed. Their output is above.");
    }

    /// <summary>
    ///     Whether a suite <em>declares</em> a test matching a filter, without running it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>--list-tests</c>, and the distinction from <see cref="SuiteTestPasses" /> is a
    ///         safety one rather than a performance one.</b> The caller that needs this is
    ///         <c>Chaos</c>, checking that each of the seven invariants in docs/plan/23 § The chaos
    ///         invariants has a test. Answering that by running the test would kill a silo and
    ///         <c>FLUSHALL</c> a Redis to find out whether somebody wrote the test that kills a silo.
    ///     </para>
    ///     <para>
    ///         ⚠ Asks the suite rather than reflecting over its assembly: these are
    ///         Microsoft.Testing.Platform hosts, so the suite's own discovery is the authority on what
    ///         it contains — including <c>[Theory]</c> expansion and any custom discoverer, neither of
    ///         which a metadata reader here would see.
    ///     </para>
    /// </remarks>
    bool SuiteListsTest(AbsolutePath project, string methodFilter)
    {
        var output = DotNetTasks.DotNet(
            $"run --configuration {Configuration} --no-restore --no-build --no-launch-profile "
            + $"--project {project} -- --list-tests --filter-method {methodFilter}",
            workingDirectory: RootDirectory,
            logOutput: false,
            exitHandler: process => process.ExitCode);

        // xunit prints "Test discovery summary: found N test(s)". Reading the number rather than the
        // exit code because a filter matching nothing is a successful discovery of nothing.
        return output
            .Select(x => DiscoveredTestCount.Match(x.Text ?? string.Empty))
            .Any(x => x.Success && int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    static readonly System.Text.RegularExpressions.Regex DiscoveredTestCount =
        new(@"found (\d+) test", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    ///     Whether one named test exists in a suite <em>and passes</em>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>--minimum-expected-tests 1</c> is what makes the two halves one answer: a filter that
    ///     matches nothing exits non-zero rather than reporting a vacuous success. Without it, a
    ///     renamed test would turn this into a check that runs nothing and approves of it — which is
    ///     the failure mode the Labels gate exists to avoid, one level down.
    /// </remarks>
    bool SuiteTestPasses(AbsolutePath project, string methodFilter)
    {
        var exitCode = 0;

        DotNetTasks.DotNet(
            $"run --configuration {Configuration} --no-restore --no-build --no-launch-profile "
            + $"--project {project} -- --minimum-expected-tests 1 --filter-method {methodFilter}",
            workingDirectory: RootDirectory,
            exitHandler: process => exitCode = process.ExitCode);

        return exitCode == 0;
    }

    // ── The coverage floor — docs/plan/23 § Test layers, row Unit ──────────────────────────────

    /// <summary>Where each suite's Cobertura report lands, and where the merged one is written.</summary>
    AbsolutePath CoverageDirectory => ArtifactsDirectory / "coverage";

    /// <summary>docs/plan/23 § Test layers, row Unit: "Coverage ≥ 70 % per project".</summary>
    const double CoverageFloor = 0.70;

    /// <summary>
    ///     A report to enforce the floor against instead of collecting one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Exists because collection and enforcement are not always on the same machine — see
    ///     <see cref="CoverageCollectionIsAvailable" />, which is a whole platform on which they
    ///     cannot be.
    /// </remarks>
    [Parameter("A Cobertura report to enforce the coverage floor against instead of collecting one.")]
    readonly string? CoverageReportFile;

    /// <summary>
    ///     Whether <c>dotnet-coverage</c> can actually instrument on this machine.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>dotnet-coverage 18.9.0 ships no macOS arm64 profiler, and it does not say so.</b>
    ///         Verified by inspecting the tool package: <c>tools/net8.0/any/macos/</c> contains only
    ///         <c>x64</c>, and the one arm64 native engine in the package
    ///         (<c>arm64/MicrosoftInstrumentationEngine_arm64.dll</c>) is a Windows DLL. A collection
    ///         run on an Apple Silicon machine therefore succeeds, exits 0, writes a report with
    ///         <c>number of packages: 0</c>, and prints one line — "No code coverage data available.
    ///         Profiler was not initialized" — in the middle of the test output.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is exactly the shape of a gate that silently stops gating</b>, and the
    ///         reason this is a probe rather than a try/catch: an empty report analysed against the
    ///         floor would report every project at 0 % and fail for the wrong reason, while a
    ///         swallowed error would pass. Neither is the truth, which is "not measured here".
    ///     </para>
    /// </remarks>
    static bool CoverageCollectionIsAvailable =>
        !(EnvironmentInfo.Platform == PlatformFamily.OSX
            && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64);

    /// <summary>
    ///     Merges what the run collected and fails any project under the floor.
    /// </summary>
    /// <remarks>
    ///     ⚠ The floor is applied to <b>shipping projects</b>, not to the assemblies the report
    ///     happens to mention. Those two lists differ in the case the floor exists for: a project
    ///     nothing tests produces no package element at all, so a report-driven floor would give it a
    ///     pass — see CoverageReport.cs § Violations.
    /// </remarks>
    void EnforceCoverageFloor()
    {
        var reports = CoverageReportFile is not null
            ? [(AbsolutePath)CoverageReportFile]
            : CoverageDirectory.DirectoryExists()
                ? CoverageDirectory.GlobFiles("*.cobertura.xml").OrderBy(x => x.Name, StringComparer.Ordinal).ToList()
                : [];

        if (reports.Count == 0)
        {
            var message =
                "Test: the coverage floor was NOT ENFORCED. docs/plan/23 § Test layers requires "
                + $"≥ {CoverageFloor:P0} per project and nothing here measured it — "
                + (CoverageCollectionIsAvailable
                    ? $"dotnet-coverage {DotNetCoverageVersion} produced no report, which on a "
                      + "platform it supports means the collection itself failed; its output is above"
                    : $"dotnet-coverage {DotNetCoverageVersion} ships no profiler for "
                      + $"{EnvironmentInfo.Platform}/arm64, and Build.Test.cs "
                      + "§ CoverageCollectionIsAvailable has the evidence")
                + ". ○, not ✔: this run says nothing about coverage.";

            // ⚠ A warning locally and a failure on CI, deliberately asymmetric. Failing every Apple
            // Silicon developer's `Test` for a gap in a Microsoft tool would get the floor deleted
            // within a week; passing CI over the same gap would mean the floor never runs anywhere.
            // docs/plan/23 § CI shape puts this gate on every PR, and PRs are built on Linux.
            if (IsServerBuild)
            {
                Assert.Fail(
                    message
                    + " On CI this is a failure, not a warning — a coverage floor that CI skips is a "
                    + "floor that does not exist.");
            }

            Log.Warning(message);

            return;
        }

        var coverage = CoverageReport.Read(reports.Select(x => x.ToString()).ToArray());
        var shipping = ShippingAssemblyPaths.Select(x => x.Project.NameWithoutExtension).ToList();

        Log.Information(
            "Test: coverage floor {Floor:P0} per project, over {Reports} report(s) — docs/plan/23 § Test layers",
            CoverageFloor,
            reports.Count);

        foreach (var module in coverage.Modules.Where(x => shipping.Contains(x.Assembly, StringComparer.Ordinal)))
        {
            Log.Information(
                "  {Marker} {Assembly,-46} {Rate,7:P1}  ({Covered} of {Coverable} lines)",
                module.Rate >= CoverageFloor ? "✔" : "✘",
                module.Assembly,
                module.Rate,
                module.Covered,
                module.Coverable);
        }

        var violations = coverage.Violations(shipping, CoverageFloor);

        if (violations.Count == 0)
        {
            Log.Information(
                "Test: {Count} shipping project(s) at or above the {Floor:P0} floor",
                shipping.Count,
                CoverageFloor);

            return;
        }

        foreach (var violation in violations)
            Log.Error("Test: {Violation}", violation);

        Assert.Fail(
            $"{violations.Count} of {shipping.Count} shipping project(s) are below the "
            + $"{CoverageFloor:P0} coverage floor, listed above. docs/plan/23 § Test layers. ⚠ The "
            + "fix is a test, never an exclusion — a floor with a list of exemptions is a floor "
            + "shaped like whatever the tree happened to be on the day it was added.");
    }

    /// <summary>The pinned version, for the message. .config/dotnet-tools.json is the pin.</summary>
    const string DotNetCoverageVersion = "18.9.0";
}
