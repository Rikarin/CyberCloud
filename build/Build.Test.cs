// Test — docs/plan/23 § Build, row `Test`: "Unit + grain tests, coverage floor per project".
// The coverage floor is not enforced here yet; see the note at the bottom of this file.
//
// The discovery half of this file is shared: it decides which target runs each test project, not
// just what `Test` runs. See TestSuite below.

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

    void RunTests()
    {
        // Before the empty check, not after: a repository whose only test project is one nobody
        // owns has zero per-PR projects, and that must fail rather than read as "nothing to run".
        AssertEveryTestProjectIsOwned();

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
