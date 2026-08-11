// Test — docs/plan/23 § Build, row `Test`: "Unit + grain tests, coverage floor per project".
// The coverage floor is not enforced here yet; see the note at the bottom of this file.
//
// The discovery half of this file is shared: it decides which target runs each test project, not
// only what `Test` runs. See TestSuite below.

using System;
using System.Collections.Generic;
using System.Linq;
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

        Log.Information("Test: running {Count} test project(s)", projects.Count);

        // ⚠ `dotnet run`, NOT `dotnet test`. This is the design of this target, not an accident.
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
        DotNetTasks.DotNetRun(s => s
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild()
                .EnableNoLaunchProfile()
                .CombineWith(projects, (settings, project) => settings
                    .SetProjectFile(project)
                    .SetApplicationArguments(
                        // ⚠ --minimum-expected-tests 1 guards the worst outcome this target can
                        // have: a project that discovers nothing, runs nothing and reports success.
                        // Without it, broken discovery — a bad filter, a lost runner reference, a
                        // project that stops being an MTP host — shows up as a green build.
                        "--minimum-expected-tests", "1",
                        // The TRX switch is xunit's `--report-xunit-trx`, not MTP's `--report-trx`:
                        // the latter lives in Microsoft.Testing.Extensions.TrxReport, which nothing
                        // here references, and using it fails with "Unknown option '--report-trx'".
                        "--report-xunit-trx",
                        "--report-xunit-trx-filename", $"{project.NameWithoutExtension}.trx",
                        "--results-directory", TestResultsDirectory)),
            degreeOfParallelism: Environment.ProcessorCount,
            completeOnFailure: true);

        // ⚠ NOT IMPLEMENTED: the "coverage ≥ 70 % per project" floor in docs/plan/23 § Test layers.
        // `dotnet-coverage` is already pinned in .config/dotnet-tools.json for it. Enforcing a
        // coverage floor before there is a single test would only calibrate the threshold against
        // nothing, so it lands with the first real test project.
    }
}
