// Test — docs/plan/23 § Build, row `Test`: "Unit + grain tests, coverage floor per project".
// The coverage floor is not enforced here yet; see the note at the bottom of this file.

using System;
using System.Collections.Generic;
using System.Linq;
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

        TestResultsDirectory.CreateOrCleanDirectory();

        Log.Information("Test: running {Count} test project(s)", projects.Count);

        // ⚠ No .SetResultsDirectory() and no .AddLoggers("trx;…") here, however natural they look.
        //
        // Every option on Nuke's DotNetTestSettings is VSTest-shaped, and these test projects run on
        // Microsoft.Testing.Platform (Directory.Build.targets § Test project conventions). MTP does
        // not ignore VSTest options, it rejects them — observed:
        //
        //   error MTP0001: VSTest-specific properties are set but will be ignored when using
        //   Microsoft.Testing.Platform. The following properties are set: VSTestLogger;
        //   VSTestResultsDirectory;
        //
        // MTP's own arguments go through the TestingPlatformCommandLineArguments property instead.
        //
        // The TRX switch is `--report-xunit-trx`, NOT the `--report-trx` that MTP documents: that
        // one belongs to the Microsoft.Testing.Extensions.TrxReport package, which nothing here
        // references, and using it fails the run with "Unknown option '--report-trx'". xunit.v3
        // ships its own reporters. If the TrxReport package is ever added to
        // Directory.Packages.props, this can move to the vendor-neutral spelling.
        DotNetTasks.DotNetTest(s => s
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild()
                .CombineWith(projects, (settings, project) => settings
                    .SetProjectFile(project)
                    .SetProperty(
                        "TestingPlatformCommandLineArguments",
                        // ⚠ --minimum-expected-tests 1 is the guard against the worst outcome for
                        // this target: a test project that discovers nothing, runs nothing and
                        // reports success. Without it, breaking test discovery — a bad filter, a
                        // missing runner reference, a project that stops being an MTP host — shows
                        // up as a green build. With it, a project that finds no tests fails.
                        "--minimum-expected-tests 1 --report-xunit-trx "
                        + $"--report-xunit-trx-filename {project.NameWithoutExtension}.trx "
                        + $"--results-directory {TestResultsDirectory}")),
            degreeOfParallelism: Environment.ProcessorCount,
            completeOnFailure: true);

        // ⚠ NOT IMPLEMENTED: the "coverage ≥ 70 % per project" floor in docs/plan/23 § Test layers.
        // `dotnet-coverage` is already pinned in .config/dotnet-tools.json for it. Enforcing a
        // coverage floor before there is a single test would only calibrate the threshold against
        // nothing, so it lands with the first real test project.
    }
}
