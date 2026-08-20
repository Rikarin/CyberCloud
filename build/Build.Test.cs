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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    ///     Whether to wrap each host in <c>coverlet</c>. Only <c>Test</c> does: docs/plan/23
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
        // and the reason is the coverage prefix. `coverlet … --target dotnet --targetargs "run …"`
        // puts a process in front of the one the settings object describes, and no ToolSettings can
        // express that. Every argument below is the one CombineWith produced; the shape of the
        // command is unchanged, which is checkable by eye against the invocation Nuke logs.
        var environmentVariables = environment is null
            ? null
            : EnvironmentInfo.Variables
                .Concat(environment.Where(x => !EnvironmentInfo.Variables.ContainsKey(x.Key)))
                .ToDictionary(x => x.Key, x => environment.TryGetValue(x.Key, out var value) ? value : x.Value);

        var failures = new ConcurrentBag<string>();

        // ⚠ Asked once, here, and not inside the loop. CoverageCollectionIsAvailable runs a real
        // collection to answer, and doing that from N threads at once would race on the memo field
        // and build the same probe project N times.
        var withCoverage = collectCoverage && CoverageCollectionIsAvailable;

        // ⚠ CONTAINER-BACKED SUITES RUN AT A LOWER DEGREE THAN THE REST, AND THE NUMBER IS MEASURED
        // RATHER THAN CHOSEN.
        //
        // `Environment.ProcessorCount` over every suite was right when six suites wanted a container.
        // 26 of 62 do now — every provider family ships a `.Cluster.Conformance` — so on a ten-core
        // host ten of them could start k3s, PostgreSQL and Redis simultaneously. Measured over six
        // full runs on an otherwise idle machine, exactly one passed first time. The other five each
        // lost a DIFFERENT suite, and the symptom differed every time: a port-bind refusal, an Npgsql
        // connect timeout inside a collection fixture, three suites sitting at zero tests started for
        // nineteen minutes, and a TLS reset from a k3s API server that went away mid-suite.
        //
        // ⚠ THE POINT IS NOT THE FLAKINESS, IT IS WHAT THE FLAKINESS COSTS. Each of those five runs
        // had to be read exception by exception to tell contention from a real regression — and twice
        // it WAS a real regression. A gate whose red is usually meaningless is a gate people stop
        // reading, which is the failure mode this whole target exists to avoid.
        //
        // A retry would be the wrong fix: it hides the two real regressions along with the three fake
        // ones. Capping the concurrency removes the cause instead.
        //
        // ⚠ THE DEGREE IS DERIVED FROM THE HOST, NOT WRITTEN DOWN. It used to be the literal 4, set
        // when there were 68 suites, and it went stale the way a constant does: measured 2026-08-19
        // on this ten-CPU host at 71 suites, four starves `CyberCloud.AppHost.Tests` — it exits 2 and
        // passes 10/10 in 1 m 29 s when run alone — while three finishes the whole gate green. Two
        // things had grown: three more container-backed suites, and `AppHost.Tests` itself, which now
        // stands up the real topology AND asserts the namespace `ReconcileDriver` creates, holding
        // k3s, Redis and PostgreSQL for longer per test.
        //
        // ⚠ Changing 4 to 3 would have gone stale again, and in the other direction it is already
        // wrong: the right number is a property of the machine, not of the tree, and a 32-CPU runner
        // is idling at three. So it is derived, from the same `Environment.ProcessorCount` the
        // suite-level degree below already uses. See ContainerBackedSuiteDegree for the arithmetic
        // and for what the derivation deliberately does not model.
        //
        // ⚠ AND THE DEGREE WAS NEVER THE WHOLE STORY, BECAUSE THE SET IT APPLIED TO WAS WRONG.
        // Which suites are container-backed used to be decided by grepping each `.csproj` for the
        // word "Testcontainers": 28 files said it, 19 suites actually ship it, and three of the
        // missing ones hold a k3s cluster each. They ran ungated on top of the semaphore, so a
        // nominal cap of 3 was really up to 6 — which is most of why 4 starved a suite and 3 did
        // not. See StartsContainers, which asks the built output instead.
        var containerBacked = projects.Where(StartsContainers).ToHashSet();

        var derivedDegree = ContainerBackedSuiteDegree;

        // ⚠ The override still wins, and it has to. The derivation models CPU and nothing else, and a
        // host whose Docker daemon has less memory than its core count implies is exactly the case
        // the arithmetic gets wrong — see the failure message at the bottom of this method, which
        // tells a reader to run the named suite alone and then lower this.
        var containerDegree =
            int.TryParse(Environment.GetEnvironmentVariable("CC_TEST_CONTAINER_PARALLELISM"), out var configured)
            && configured > 0
                ? configured
                : derivedDegree;

        Log.Information(
            "Test: {Container} of {Total} suite(s) ship Testcontainers and run at a parallelism of "
            + "{Degree} ({Source}); the remaining {Rest} run at {Cpu}. Build.Test.cs § "
            + "ContainerBackedSuiteDegree has the measurement.",
            containerBacked.Count,
            projects.Count,
            containerDegree,
            containerDegree == derivedDegree
                ? $"{Environment.ProcessorCount} CPU(s) ÷ {CpusPerContainerBackedSuite}"
                : $"CC_TEST_CONTAINER_PARALLELISM, overriding the derived {derivedDegree}",
            projects.Count - containerBacked.Count,
            Environment.ProcessorCount
        );

        // The container-backed ones go first, because they dominate wall clock and the cheap suites
        // fill the idle cores behind them rather than the other way round.
        var ordered = projects.OrderByDescending(containerBacked.Contains).ToList();

        // ⚠ Constructed HERE, not lazily inside the loop body. The first version of this was a
        // `static SemaphoreSlim? slots` with `slots ??= new(...)`, which is not thread-safe: several
        // workers each built their own semaphore, so a thread could `Wait` on one instance and
        // `Release` another, and the run died with "Adding the specified count to the semaphore would
        // cause it to exceed its maximum count". One object, created before anything can race for it.
        using var containerSlots = new SemaphoreSlim(containerDegree, containerDegree);

        Parallel.ForEach(
            Partitioner.Create(ordered, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            project =>
            {
                // ⚠ One semaphore, taken only by the suites that need a container. A cheap suite never
                // waits on it, so capping the containers costs nothing on the other 36.
                var gated = containerBacked.Contains(project);

                if (gated) {
                    containerSlots.Wait();
                }

                try {
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

                // ⚠ coverlet instruments the suite's OWN output directory and nothing else, which is
                // what makes running 71 of these at once safe: it rewrites the IL of the assemblies
                // on disk, runs the target, then puts them back, and every suite has its own copy of
                // every dependency under artifacts/bin/<project>/<configuration>. Point two suites at
                // one directory and they would corrupt each other's assemblies; the artifacts layout
                // means they never share one.
                //
                // ⚠ It also instruments only what has a portable PDB beside it, so the NuGet
                // dependencies in that directory are skipped without a filter having to name them —
                // 12 CyberCloud assemblies out of 102 DLLs, measured on CyberCloud.Identity.Tests.
                //
                // ⚠ THE TWO CALLS ARE NOT A TIDINESS FAILURE, THEY ARE THE ONLY SHAPE THAT WORKS, and
                // the reason is worth writing down because the wrong shape fails in a way that reads
                // like a missing tool. Nuke's ArgumentStringHandler is an interpolated-string handler:
                // each HOLE is quoted if it needs quoting, and a whole string handed over as one
                // argument goes through its implicit operator, which is `$"{value}"` — a single hole.
                // A command line containing a `"` therefore comes back double-quoted end to end with
                // its inner quotes escaped, so `dotnet` is asked to run one command named
                // `coverlet /path --target dotnet …` and answers "Could not execute because the
                // specified command or file was not found". Observed on all 71 suites at once.
                //
                // So the interpolated literal has to be AT the call, `run` has to arrive as a hole,
                // and it is pre-quoted because a hole is quoted only when the handler thinks it needs
                // it — an already-double-quoted value is passed through untouched, which is the one
                // way to say "this argument is one argument" and be sure of it.
                if (withCoverage)
                {
                    var report = CoverageDirectory / $"{name}.cobertura.xml";
                    var instrumented = SuiteOutputDirectory(project);
                    var targetArguments = $"\"{run}\"";

                    DotNetTasks.DotNet(
                        $"coverlet {instrumented} --target dotnet --targetargs {targetArguments} --format cobertura --output {report}",
                        workingDirectory: RootDirectory,
                        environmentVariables: environmentVariables,
                        exitHandler: process => exitCode = process.ExitCode);
                }
                else
                {
                    DotNetTasks.DotNet(
                        run,
                        workingDirectory: RootDirectory,
                        environmentVariables: environmentVariables,
                        exitHandler: process => exitCode = process.ExitCode);
                }

                if (exitCode != 0)
                    failures.Add($"{name} exited {exitCode}");
                }
                finally {
                    if (gated) {
                        containerSlots.Release();
                    }
                }
            });

        // ⚠ Every suite runs before any failure is reported — the same reasoning as
        // Build.Architecture.cs § Report, and the reason the old call passed completeOnFailure.
        //
        // ⚠ THE NAMES GO IN THE MESSAGE, NOT ONLY IN THE COLLECTION. They were in the collection
        // argument for a while, which reads as sufficient and is not: Nuke prints the message
        // prominently and the collection is easy to lose in several thousand lines of suite output.
        // A suite that exits non-zero WITHOUT a failing test — a container that could not bind its
        // port, a host process that would not shut down — writes no row to its .trx either, so the
        // message was the only place the name could have come from. That combination went unattributed
        // four times before anyone noticed the name had been captured all along.
        var named = failures.OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Empty(
            named,
            $"{failures.Count} of {projects.Count} {target} suite(s) failed: {string.Join(", ", named)}. "
            + "Their output is above. ⚠ A suite named here with no failing test in its .trx did not "
            + "fail an assertion — it exited non-zero, and build/README.md § failed to bind host port "
            + "covers the commonest cause on this platform. ⚠ That section names a port-bind refusal "
            + "and this failure may not look like one: an Npgsql connect timeout inside a collection "
            + "fixture, a suite sitting at zero tests started, and a TLS reset from a k3s API server "
            + "that went away are all the same cause wearing different clothes. Run the named suite "
            + "alone before believing it, and if it passes alone, lower CC_TEST_CONTAINER_PARALLELISM.");
    }

    /// <summary>
    ///     Whether a suite can start a container, answered from what it was built with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS USED TO GREP THE <c>.csproj</c> FOR THE WORD "Testcontainers", AND THE CAP
    ///         ABOVE WAS THEREFORE NOT CAPPING WHAT IT SAID IT WAS.</b> Measured over this tree on
    ///         2026-08-20: 28 project files contain the word and <b>19</b> suites actually ship the
    ///         assemblies. It was wrong in both directions and the two errors compounded.
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Three suites that hold a whole k3s cluster were invisible to it</b> —
    ///             <c>CyberCloud.Providers.{ContainerService,Network,Terminal}.Cluster.Conformance</c>
    ///             reach Testcontainers through a project reference, so their own file never says the
    ///             word. They ran ungated, at the suite-level degree, <em>on top of</em> whatever the
    ///             semaphore was letting through. A nominal cap of 3 was really up to 6.
    ///         </item>
    ///         <item>
    ///             <b>Twelve suites that start no container were holding slots</b>, several of them
    ///             because their <c>.csproj</c> carries a ⚠ comment explaining that they deliberately
    ///             do <em>not</em> use Testcontainers. <c>CyberCloud.Identity.Tests</c> says "NO
    ///             Testcontainers" in capitals and was gated for saying so.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ That is the whole shape of this repository's signature failure — a check that answers
    ///         a narrower question than it appears to. "Does this file mention a package?" is not
    ///         "does this suite start a container?", and the gap is invisible from the log line, which
    ///         cheerfully reported a count either way. The two suites left sitting at zero tests
    ///         started for four minutes in the 2026-08-20 run were two of the three it could not see.
    ///     </para>
    ///     <para>
    ///         ⚠ So the evidence is a built artefact rather than a string in a text file — the same
    ///         reasoning as CoverageReport.cs § CoverableLines, which counts sequence points in a PDB
    ///         rather than parsing a tool's stdout. A comment cannot fool it, a transitive reference
    ///         cannot hide from it, and it goes right the next time somebody adds a container to a
    ///         suite through a shared fixture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A suite whose output directory is not there is treated as container-backed</b>,
    ///         which is the safe direction: gating a cheap suite costs a little wall clock, and
    ///         letting an unknown one through costs the starved run this whole mechanism exists to
    ///         prevent. It should not happen — every caller of RunSuites depends on <c>Compile</c> —
    ///         so it is logged rather than passed over.
    ///     </para>
    /// </remarks>
    bool StartsContainers(AbsolutePath project) {
        var output = SuiteOutputDirectory(project);

        if (!output.DirectoryExists()) {
            Log.Warning(
                "Test: {Suite} has no build output under {Output}, so whether it starts a container "
                + "is unknown. Treating it as container-backed, which is the direction that costs "
                + "wall clock rather than a starved run. Build.Test.cs § StartsContainers.",
                project.NameWithoutExtension,
                output);

            return true;
        }

        return output.GlobFiles("Testcontainers*.dll").Count > 0;
    }

    /// <summary>
    ///     The CPUs a container-backed suite is assumed to need to itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three, and the three is the measurement rather than a guess.</b> On this ten-CPU
    ///         host a degree of four starves a suite and a degree of three does not, so the budget a
    ///         container-backed suite needs is more than 10 ÷ 4 = 2.5 CPUs and at most 10 ÷ 3 = 3.3.
    ///         Three is the whole number in that interval, and it is the only one: it reproduces the
    ///         measured answer on the machine the measurement was taken on.
    ///     </para>
    ///     <para>
    ///         ⚠ It is a budget for the <em>suite</em>, not for one container. A `.Cluster.Conformance`
    ///         run holds a k3s API server, PostgreSQL and Redis plus its own test host, and k3s alone
    ///         spends most of its start-up saturating a core.
    ///     </para>
    /// </remarks>
    const int CpusPerContainerBackedSuite = 3;

    /// <summary>
    ///     How many container-backed suites may be live at once, derived from this host.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What this models is CPU, and it is worth being explicit about the two things it
    ///         does not.</b> Memory is the obvious other candidate — a Docker daemon with 8 GB will
    ///         thrash long before its core count says it should — and the tree cannot observe it
    ///         honestly: on Linux the daemon shares the host's RAM and
    ///         <see cref="GC" />'s view of it is the right one, while on macOS and Windows the daemon
    ///         lives in a VM with its own allocation that a .NET process can only learn by asking
    ///         `docker info`, which is a subprocess that hangs when the daemon is unhealthy — inside
    ///         the target whose whole job is to tell a starved host from a broken one. The other is
    ///         how many k3s clusters a daemon will hold, which has no API at all.
    ///     </para>
    ///     <para>
    ///         So this is deliberately the constraint the tree can read for nothing, and
    ///         <c>CC_TEST_CONTAINER_PARALLELISM</c> is the lever for the ones it cannot. ⚠ A
    ///         derivation that is wrong on some host is fine <em>because</em> the override exists and
    ///         the failure message names it; a constant is wrong on every host but the one it was
    ///         measured on, and says nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ Never zero. A one-CPU machine runs the container-backed suites one at a time, slowly,
    ///         rather than deadlocking on a semaphore nobody can enter.
    ///     </para>
    /// </remarks>
    static int ContainerBackedSuiteDegree =>
        Math.Max(1, Environment.ProcessorCount / CpusPerContainerBackedSuite);

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

    /// <summary>
    ///     Where a suite's assemblies are, which is what <c>coverlet</c> instruments.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same expression as Build.Architecture.cs § ShippingAssemblyPaths, and it has to stay
    ///     the same one: <c>Directory.Build.props</c> sets <c>ArtifactsPath</c>, so the layout is
    ///     <c>artifacts/bin/&lt;project&gt;/&lt;configuration&gt;</c> for every project in the tree,
    ///     shipping or suite. A wrong directory here does not fail — coverlet finds nothing to
    ///     instrument, the suite still runs, and the report comes back empty, which is the shape of
    ///     failure this file spends the rest of its length refusing to accept quietly.
    /// </remarks>
    AbsolutePath SuiteOutputDirectory(AbsolutePath project) =>
        ArtifactsDirectory / "bin" / project.NameWithoutExtension / Configuration.ToLowerInvariant();

    /// <summary>docs/plan/23 § Test layers, row Unit: "Coverage ≥ 70 % per project".</summary>
    const double CoverageFloor = 0.70;

    /// <summary>
    ///     A report to enforce the floor against instead of collecting one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Exists because collection and enforcement are not always on the same machine:
    ///     docs/plan/23 § CI shape parallelises <c>pr.yml</c>, so the job that runs the suites and
    ///     the job that reads the answer can be two jobs. It was also the escape hatch for the
    ///     platform <see cref="CoverageCollectionIsAvailable" /> used to have to say no on, which is
    ///     no longer a reason to reach for it — the floor is measurable everywhere now.
    /// </remarks>
    [Parameter("A Cobertura report to enforce the coverage floor against instead of collecting one.")]
    readonly string? CoverageReportFile;

    /// <summary>
    ///     Whether coverage can actually be collected on this machine, answered by instrumenting
    ///     something and looking at what came back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE COLLECTOR IS <c>coverlet</c>, AND THE REASON IS THAT THE FLOOR HAS TO BE
    ///         MEASURABLE WHERE THE CODE IS WRITTEN.</b> It used to be <c>dotnet-coverage</c>, which
    ///         ships native profilers for <c>ubuntu/x64</c>, <c>alpine/x64</c>, <c>macos/x64</c> and
    ///         Windows and for nothing else — both of its arm64 directories hold
    ///         <c>MicrosoftInstrumentationEngine_arm64.dll</c>, a <em>Windows</em> DLL. So on every
    ///         Apple Silicon and every arm64 Linux machine the probe below correctly said "no", the
    ///         floor correctly reported ○, and the result was that <b>an x64 CI runner was the first
    ///         and only place the floor had ever run</b>. Two projects breached it unnoticed under
    ///         that arrangement, which is what a gate nobody can run locally is for.
    ///     </para>
    ///     <para>
    ///         coverlet has no native profiler at all: it rewrites IL, in the suite's own output
    ///         directory, and puts the assemblies back afterwards. Measured on osx-arm64 against a
    ///         project whose lines were counted by hand — one class with three methods, two of them
    ///         reached by two tests — it reported <c>line-rate="0.5454"</c>, which is 6 of 11
    ///         sequence-point lines and is the number a person gets with a pencil. libxml2, the
    ///         profiler matrix and the x64 pin on the CI job all stop being things this repository
    ///         has to know about.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The probe stays, and it now probes the tool that is actually used.</b> Its
    ///         history is worth keeping because the failure it was built for was so quiet:
    ///         <c>dotnet-coverage</c> 18.9.0 fails by exiting <b>0</b> and writing
    ///         <c>&lt;packages /&gt;</c>, with its one line of explanation — "No code coverage data
    ///         available. Profiler was not initialized" — buried in the middle of the test output.
    ///         Before the probe this was a platform allow-list that excluded macOS/arm64 and waved
    ///         everything else through, and it was wrong on two of the platforms it waved through.
    ///         ⚠ <b>Do not turn it back into a list.</b> An allow-list has to be updated every time
    ///         somebody finds a new way for a tool to be missing; a probe finds the next one itself,
    ///         and the next one will not be the one written down here.
    ///     </para>
    ///     <para>
    ///         ⚠ A wrong answer here is survivable in one direction only, and it is the right one:
    ///         a probe that wrongly says "no" skips collection, which leaves no report, which
    ///         <see cref="EnforceCoverageFloor" /> turns into a hard failure on CI. A probe that
    ///         wrongly says "yes" is caught after the fact by the same method, which refuses to read
    ///         a floor out of a report that mentions no assembly at all.
    ///     </para>
    /// </remarks>
    bool CoverageCollectionIsAvailable => coverageCollectionIsAvailable ??= ProbeCoverageCollection();

    bool? coverageCollectionIsAvailable;

    /// <summary>Where the probe assembly is written, built, and collected over.</summary>
    /// <remarks>
    ///     Beside the coverage reports rather than inside <see cref="CoverageDirectory" />, which
    ///     <c>Test</c> cleans before every run — the probe survives so the second run pays for the
    ///     collection only and not the compile.
    /// </remarks>
    AbsolutePath CoverageProbeDirectory => ArtifactsDirectory / "coverage-probe";

    /// <summary>
    ///     Builds a three-line assembly, collects coverage over it, and reports whether the numbers
    ///     came back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One covered line and one uncovered line, on purpose.</b> A probe whose every line
    ///         is covered reports <c>line-rate="1"</c>, which is also what the empty report at the
    ///         top of a broken run reports. Requiring a rate strictly between 0 and 1 means the probe
    ///         cannot pass by accident — the collector has to have both found the assembly and
    ///         recorded which lines actually ran.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>--include-test-assembly</c> because <c>Probe.dll</c> is not one and coverlet has
    ///         to be told so. Its default is to leave the assembly it was pointed at out of the
    ///         report, which for this probe would produce an empty one — the exact answer the probe
    ///         exists to distinguish from a broken collector.
    ///     </para>
    ///     <para>
    ///         ⚠ The three stopper files are what keep the probe a probe. <c>artifacts/</c> is inside
    ///         the repository, so without them MSBuild walks up into the root
    ///         <c>Directory.Build.props</c> and the probe inherits central package management,
    ///         analyzers, and <c>TreatWarningsAsErrors</c> — at which point a probe failure would
    ///         mean "the repository's analyzer settings changed", not "the profiler is missing".
    ///     </para>
    /// </remarks>
    bool ProbeCoverageCollection()
    {
        var probe = CoverageProbeDirectory;
        var assembly = probe / "bin" / "Probe.dll";
        var report = probe / "probe.cobertura.xml";

        WriteProbeSources(probe);

        var buildExitCode = 0;

        // Quiet while it works — this is three lines of scaffolding, not something a reader of a
        // green `Test` needs to see. The output is kept so a failure can print it.
        var buildOutput = DotNetTasks.DotNet(
            $"build {probe / "Probe.csproj"} --configuration Release --output {probe / "bin"} --nologo",
            workingDirectory: probe,
            logOutput: false,
            logInvocation: false,
            exitHandler: process => buildExitCode = process.ExitCode);

        if (buildExitCode != 0 || !assembly.FileExists())
        {
            // ⚠ Printed, not swallowed. "Could not build the probe" with no compiler output is a
            // dead end, and the reader has no probe project to go and build by hand — this method
            // wrote it.
            foreach (var line in buildOutput.TakeLast(20))
                Log.Warning("  {Line}", line.Text);

            Log.Warning(
                "Test: could not build the coverage probe in {Probe} (exit {Exit}), so whether "
                + "coverlet {Version} works here is unknown. Treating it as unavailable, which "
                + "makes the floor say it was not measured rather than guess a number.",
                probe,
                buildExitCode,
                CoverletVersion);

            return false;
        }

        report.DeleteFile();

        // ⚠ No exit-code check: the failure class this exists to catch exits 0 — dotnet-coverage did
        // it by writing <packages />, and any collector can do it by instrumenting nothing. The
        // report is the answer.
        DotNetTasks.DotNet(
            $"coverlet {probe / "bin"} --target dotnet --targetargs \"{assembly}\" "
            + $"--include-test-assembly --format cobertura --output {report}",
            workingDirectory: RootDirectory,
            logOutput: false,
            logInvocation: false,
            exitHandler: process => process.ExitCode);

        if (!report.FileExists())
        {
            Log.Warning(
                "Test: coverlet {Version} wrote no report for the probe at all on {Platform}. "
                + "Coverage will not be collected.",
                CoverletVersion,
                EnvironmentInfo.Platform);

            return false;
        }

        var probed = CoverageReport.Read(report).Modules
            .FirstOrDefault(x => x.Covered > 0 && x.Covered < x.Coverable);

        if (probed is null)
        {
            Log.Warning(
                "Test: coverlet {Version} cannot instrument on {Platform}/{Architecture} — it wrote a "
                + "report with nothing usable in it. The coverage floor will report that it was not "
                + "measured rather than report every project at 0 %. coverlet rewrites IL and needs "
                + "no native profiler, so unlike the dotnet-coverage this replaced there is no "
                + "platform on which this is expected: check that the probe assembly has a portable "
                + "PDB beside it, which is what coverlet looks for. Build.Test.cs § "
                + "CoverageCollectionIsAvailable has the history.",
                CoverletVersion,
                EnvironmentInfo.Platform,
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);

            return false;
        }

        Log.Information(
            "Test: coverlet {Version} instruments correctly here — the probe came back at "
            + "{Rate:P1} ({Covered} of {Coverable} lines), so the coverage floor below is measured.",
            CoverletVersion,
            probed.Rate,
            probed.Covered,
            probed.Coverable);

        return true;
    }

    /// <summary>Writes the probe's sources, leaving the timestamps alone when nothing changed.</summary>
    /// <remarks>
    ///     Rewriting an identical file would re-date it and cost a rebuild on every <c>Test</c> run.
    /// </remarks>
    void WriteProbeSources(AbsolutePath probe)
    {
        probe.CreateDirectory();

        // The framework the rest of the tree targets, read rather than repeated — a probe pinned to
        // a version this machine no longer has installed would fail to build and read as "the
        // profiler is missing", which is a different and much more confusing sentence.
        var targetFramework =
            XDocument.Load(RootDirectory / "Directory.Build.props")
                .Descendants("TargetFramework")
                .FirstOrDefault()?.Value
            ?? throw new InvalidOperationException(
                "Directory.Build.props declares no <TargetFramework>, so the coverage probe cannot "
                + "be pinned to the same one as the rest of the tree.");

        // ⚠ Three lines, one of them never called, so the answer is checkable by hand: a working
        // collector has to report 2 of 3 and nothing else can.
        Write(probe / "Program.cs", """
            public static class Probe
            {
                public static int Covered(int n) => n > 0 ? n * 2 : 0;
                public static int NeverCalled(int n) => n - 1;
                public static void Main() => System.Console.WriteLine(Covered(21));
            }

            """);

        Write(probe / "Probe.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{targetFramework}</TargetFramework>
                <AssemblyName>Probe</AssemblyName>
                <RootNamespace>Probe</RootNamespace>
                <!-- Portable PDBs beside the assembly: no symbols, nothing to instrument. -->
                <DebugType>portable</DebugType>
                <Optimize>false</Optimize>
                <EnableDefaultItems>true</EnableDefaultItems>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>

            """);

        // The stoppers. MSBuild and NuGet both walk up from the project directory, and artifacts/ is
        // inside the repository.
        foreach (var stopper in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            Write(probe / stopper, "<Project />\n");

        static void Write(AbsolutePath path, string content)
        {
            if (!path.FileExists() || path.ReadAllText() != content)
                path.WriteAllText(content);
        }
    }

    /// <summary>
    ///     Merges what the run collected and fails any project under the floor.
    /// </summary>
    /// <remarks>
    ///     ⚠ The floor is applied to <b>shipping projects</b>, not to the assemblies the report
    ///     happens to mention. Those two lists differ in the case the floor exists for: a project
    ///     nothing tests produces no package element at all, so a report-driven floor would give it a
    ///     pass — see CoverageReport.cs § Violations.
    ///     <para>
    ///         ⚠ There are three answers here, not two, and collapsing the third into either of the
    ///         others is this file's own worst failure: <b>every project passed</b>,
    ///         <b>these projects are under-covered</b>, and <b>nothing here measured anything</b>.
    ///         The third one is ○ — neither a tick nor a cross — and it is a hard failure on CI.
    ///     </para>
    /// </remarks>
    void EnforceCoverageFloor()
    {
        var reports = CoverageReportFile is not null
            ? [(AbsolutePath)CoverageReportFile]
            : CoverageDirectory.DirectoryExists()
                ? CoverageDirectory.GlobFiles("*.cobertura.xml").OrderBy(x => x.Name, StringComparer.Ordinal).ToList()
                : [];

        var coverage = reports.Count == 0
            ? null
            : CoverageReport.Read(reports.Select(x => x.ToString()).ToArray());

        // ⚠ THE TRIPWIRE. A report that mentions no assembly at all is what a collector writes when
        // it instrumented nothing — dotnet-coverage did it as <packages /> with exit 0 on three of
        // the four platforms in § CoverageCollectionIsAvailable, and coverlet does it by finding no
        // portable PDB in the directory it was pointed at. Reading a floor out of that would report
        // every shipping project at 0 % and blame the tests, which is a red build with a confident
        // wrong diagnosis.
        //
        // ⚠ It is also the backstop for the probe, and switching collectors did not retire it — if
        // anything it matters more, because the probe now instruments a directory holding ONE
        // assembly and the suites instrument directories holding a hundred. The probe decides
        // whether to collect at all; this decides whether what came back means anything, and it is
        // the one of the two that runs against the real suites.
        //
        // Zero, not "fewer than expected": 39 suites that between them touch every assembly in the
        // tree cannot honestly produce a report naming none of them, and any threshold above zero
        // would be a number nobody could defend.
        var measuredNothing = coverage is null || coverage.MentionedAssemblies.Count == 0;

        if (measuredNothing)
        {
            var why = reports.Count == 0
                ? CoverageCollectionIsAvailable
                    ? $"coverlet {CoverletVersion} produced no report, which on a machine where the "
                      + "probe passed means the collection itself failed; its output is above"
                    : $"coverlet {CoverletVersion} cannot instrument on "
                      + $"{EnvironmentInfo.Platform}/{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, "
                      + "which the probe above established by trying it"
                : $"{reports.Count} report(s) came back naming no assembly at all, so coverlet "
                  + $"{CoverletVersion} instrumented nothing for the suites even though the probe "
                  + "said it would. It instruments the assemblies it finds a portable PDB beside in "
                  + "the suite's own output directory, so the usual cause is that Build.Test.cs § "
                  + "SuiteOutputDirectory no longer names the directory the build writes to. "
                  + "Build.Test.cs § CoverageCollectionIsAvailable";

            var message =
                "Test: the coverage floor was NOT ENFORCED. docs/plan/23 § Test layers requires "
                + $"≥ {CoverageFloor:P0} per project and nothing here measured it — "
                + why
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

        var shipping = ShippingAssemblyPaths.Select(x => x.Project.NameWithoutExtension).ToList();
        var nothingToCover = ProjectsWithNothingToCover(coverage!.MentionedAssemblies);

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

        foreach (var project in nothingToCover.OrderBy(x => x, StringComparer.Ordinal))
        {
            Log.Information(
                "  {Marker} {Assembly,-46} {Note}",
                "○",
                project,
                "no coverable line to instrument — CoverageReport.cs § CoverableLines");
        }

        var violations = coverage.Violations(shipping, CoverageFloor, nothingToCover);

        if (violations.Count == 0)
        {
            Log.Information(
                "Test: {Count} shipping project(s) at or above the {Floor:P0} floor ({Empty} of them "
                + "with no executable code at all)",
                shipping.Count,
                CoverageFloor,
                nothingToCover.Count);

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

    /// <summary>
    ///     The shipping projects that are missing from the report because there is nothing in them to
    ///     instrument, as opposed to because no test loaded them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A project with no executable code and a project nothing tests look identical in a
    ///         Cobertura report</b> — neither gets a <c>&lt;package&gt;</c> element — and one of them
    ///         deserves a pass. Four projects in this tree are the first kind:
    ///         <c>CyberCloud.Providers.*.Application</c>, each one nothing but an ABP module
    ///         declaration with no body. <c>dotnet-coverage instrument</c> refuses all four with
    ///         <c>Reason: optimized_or_instrumented</c>, and the floor used to read that silence as
    ///         0 % — for projects whose three tests pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only projects already absent from the report are asked.</b> A project that shows
    ///         up with a real number is judged on that number, so this can never lift one over the
    ///         floor. The set it produces is the single place the floor can be switched off, which is
    ///         why the evidence behind it is a count of sequence points in a compiled assembly rather
    ///         than a name, a folder, or a list.
    ///     </para>
    ///     <para>
    ///         ⚠ An assembly this cannot read — missing, or built by some other run — is <b>not</b>
    ///         excused. <see cref="CoverageReport.CoverableLines" /> answers <see langword="null" />
    ///         there, and an unanswered question that turns into a pass is the failure this whole
    ///         file is written against.
    ///     </para>
    /// </remarks>
    /// <param name="mentioned">The assemblies the report does name, which need no examining.</param>
    // HashSet rather than IReadOnlySet: CA1859 is an error here and this is a private helper — the
    // same concession Build.Test.cs § ProjectsIn already makes.
    HashSet<string> ProjectsWithNothingToCover(IReadOnlySet<string> mentioned) =>
        ShippingAssemblyPaths
            .Where(x => !mentioned.Contains(x.Project.NameWithoutExtension))
            .Where(x => CoverageReport.CoverableLines(x.Assembly) == 0)
            .Select(x => x.Project.NameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The pinned version, for the message. .config/dotnet-tools.json is the pin.</summary>
    const string CoverletVersion = "10.0.1";
}
