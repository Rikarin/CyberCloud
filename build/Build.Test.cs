// Test — docs/plan/23 § Build, row `Test`: "Unit + grain tests, coverage floor per project".
// The coverage floor is not enforced here yet; see the note at the bottom of this file.

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
    ///     Every test project in the repository, discovered by the same naming conventions that
    ///     <c>Directory.Build.props</c> § "Project role detection" uses to set
    ///     <c>IsTestProject</c>. The two lists must stay in step: a project this misses is a project
    ///     that builds as a test (xunit.v3 executable) but never runs.
    ///     <para>
    ///         Discovery walks the filesystem rather than the solution deliberately — see
    ///         <see cref="AssertTestProjectsAreInSolution" />.
    ///     </para>
    /// </summary>
    IReadOnlyCollection<AbsolutePath> TestProjects =>
        SourceRoots
            .SelectMany(root => root.GlobFiles("**/*.csproj"))
            .Where(IsTestProject)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    static bool IsTestProject(AbsolutePath project)
    {
        var name = project.NameWithoutExtension;

        return name.EndsWith(".Tests", StringComparison.Ordinal)
            || name.EndsWith(".Conformance", StringComparison.Ordinal)
            || name is "CyberCloud.E2E"
                or "CyberCloud.Chaos"
                or "CyberCloud.Load"
                or "CyberCloud.Isolation";
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
                "Test: no test projects found under {Roots} — nothing to run. Discovery follows the "
                + "same *.Tests / *.Conformance / E2E / Chaos / Load / Isolation convention as "
                + "Directory.Build.props § Project role detection.",
                string.Join(", ", SourceRoots.Select(x => x.Name)));
            return;
        }

        AssertTestProjectsAreInSolution(projects);

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
