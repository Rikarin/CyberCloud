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
using System.Globalization;
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

        // ⚠ Read HERE, before a single suite starts, and not where it is used ten minutes later.
        // A malformed row — a missing rate, a pin without a reason above it, a project named twice —
        // is a typo, and finding a typo at the end of a full test run is how a file becomes one
        // people edit by copying an existing line and hoping. The parse is milliseconds; the wait
        // it removes is the whole gate.
        var baseline = CoverageBaseline();

        Log.Information("Test: running {Count} test project(s)", projects.Count);

        RunSuites(nameof(Test), projects, environment: null, collectCoverage: true);

        EnforceCoverageFloor(baseline);
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
        // on this ten-CPU host at 71 suites, four starved `CyberCloud.AppHost.Tests` — it exited 2
        // and passed 10/10 in 1 m 29 s when run alone — while three finished the whole gate green.
        // Two things had grown: three more container-backed suites, and `AppHost.Tests` itself,
        // which now stands up the real topology AND asserts the namespace `ReconcileDriver` creates,
        // holding k3s, Redis and PostgreSQL for longer per test.
        //
        // ⚠ That measurement dates from before StartsContainers was fixed, so it is history rather
        // than calibration — "degree 4" did not mean then what it means now. CpusPerContainerBackedSuite
        // says what the three is actually measured against.
        //
        // ⚠ Changing 4 to 3 would have gone stale again, and in the other direction it is already
        // wrong: the right number is a property of the machine, not of the tree, and a 32-CPU runner
        // is idling at three. So it is derived, from the same `Environment.ProcessorCount` the
        // suite-level degree below already uses. See ContainerBackedSuiteDegree for the arithmetic
        // and for what the derivation deliberately does not model.
        //
        // ⚠ AND THE DEGREE WAS NEVER THE WHOLE STORY, BECAUSE THE SET IT APPLIED TO WAS WRONG.
        // Which suites are container-backed used to be decided by grepping each `.csproj` for the
        // word "Testcontainers": counted 2026-08-20 over 71 suites, 28 files said it and 19 suites
        // actually shipped it, and three of the missing ones held a k3s cluster each. They ran
        // ungated on top of the semaphore, so a nominal cap of 3 was really up to 6 — which is most
        // of why 4 starved a suite and 3 did not. See StartsContainers, which asks the built output
        // instead. ⚠ Every count in this comment is a dated measurement of a tree that grows; the
        // arithmetic below reads the suites it is given and none of these numbers is wired into it.
        //
        // ⚠ AND THE SET IS NOW TWO SETS, BECAUSE THE CONSTRAINT WAS NEVER CPU — #77.
        //
        // #77 measured the derived degree of 3 losing
        // `CyberCloud.Providers.ContainerRegistry.Cluster.Conformance` on this ten-CPU host at
        // 8548ee9 — 8/8 when run alone — while a degree of 2 took all 73 suites green in 18 m 14 s.
        // The obvious repair is a smaller divisor and it is the wrong one, for the reason the
        // paragraphs above already give about red that means nothing: a degree lowered until the
        // build passes is a degree that will hide the next real defect, and it would have hidden
        // this one. The number was not what had gone stale. The set it applied to was, again.
        //
        // ⚠ MEASURED 2026-09-05 OVER THIS TREE'S OWN BUILD OUTPUT, AT 73 PER-PR SUITES: 21 ship
        // something that can start a container, and SEVENTEEN OF THE 21 HOLD A WHOLE KUBERNETES
        // CLUSTER. The four that do not are
        // `CyberCloud.{Authorization,ServiceDefaults,Tenancy,Vault}.Tests`. So a budget denominated
        // in "container-backed suites" was, for very nearly every slot it ever handed out, a budget
        // in k3s clusters — and three separate mechanisms were each answering "may I hold one?" on
        // their own, none of them aware of the other two:
        //
        //   * FIFTEEN assemblies take `ClusterSlot` — a lock file in the temp directory, held for the
        //     life of the process — so at most one of the fifteen holds a cluster at a time. That
        //     permit is the tree's real model of the constraint #77 asks for, it has been in
        //     test/CyberCloud.Cluster.Conformance all along, and nothing in build/ knew it existed.
        //   * `CyberCloud.Kubernetes.Tests` starts its own k3s through `K3sFixture` and takes NO
        //     permit. ClusterInfrastructure's remarks say so in as many words: "It does not serialise
        //     against CyberCloud.Kubernetes.Tests, which would need one line in that project."
        //   * `CyberCloud.AppHost.Tests` starts a k3s as well — through Aspire rather than
        //     Testcontainers, on the FIXED host port 6443 — and StartsContainers could not see it at
        //     all, because the suite ships `Aspire.Hosting.Testing` and not one Testcontainers
        //     assembly. It takes a machine-wide lock of its own,
        //     `cybercloud-apphost-local-topology.lock`, which is a DIFFERENT file from ClusterSlot's
        //     and therefore excludes nothing except a second copy of itself.
        //
        // Three disjoint answers to one question, so three k3s API servers could be live at once
        // underneath a cap that said "three container suites". That is the arithmetic #77 measured,
        // and it is why 2 looked like the honest divisor: at 2 there was no third slot for
        // `CyberCloud.Kubernetes.Tests` to enter beside whichever assembly held ClusterSlot, and
        // `AppHost.Tests` — ungated either way — had one fewer neighbour.
        //
        // ⚠ SO THE DIVISOR IS UNCHANGED AT 3 AND THE CLUSTER-BACKED SUITES GET A SEMAPHORE OF THEIR
        // OWN. That is #77's third option, and it is the only one of the three that models the
        // constraint rather than the symptom: the count that has to be capped is clusters, the
        // number is not a property of this host, and it is not tuned — see ClusterBackedSuiteDegree,
        // which is 1 because 1 is what ClusterSlot already enforces among fifteen of the seventeen.
        var containerBacked = projects.Where(StartsContainers).ToHashSet();

        // ⚠ A SUBSET of containerBacked by construction rather than by two globs that happen to
        // agree — StartsContainers returns true for everything StartsCluster does, and says why. A
        // cluster-backed suite therefore takes BOTH permits, which is what keeps the container
        // budget a bound on the total rather than a bound on the cheap half of it.
        var clusterBacked = projects.Where(StartsCluster).ToHashSet();

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

        // ⚠ Both counts and both degrees, because a reader diagnosing a starved run needs to know
        // which of the two budgets the named suite was spending. The line that reported only the
        // container one is how #77 came to be read as a problem with the divisor.
        Log.Information(
            "Test: {Cluster} of {Total} suite(s) hold a Kubernetes cluster and run {ClusterDegree} at "
            + "a time; {Container} can start a container at all and run at a parallelism of {Degree} "
            + "({Source}); the remaining {Rest} run at {Cpu}. Build.Test.cs § "
            + "ClusterBackedSuiteDegree and § ContainerBackedSuiteDegree have the measurements.",
            clusterBacked.Count,
            projects.Count,
            ClusterBackedSuiteDegree,
            containerBacked.Count,
            containerDegree,
            containerDegree == derivedDegree
                ? $"{Environment.ProcessorCount} CPU(s) ÷ {CpusPerContainerBackedSuite}"
                : $"CC_TEST_CONTAINER_PARALLELISM, overriding the derived {derivedDegree}",
            projects.Count - containerBacked.Count,
            Environment.ProcessorCount
        );

        // The container-backed ones go first, because they dominate wall clock and the cheap suites
        // fill the idle cores behind them rather than the other way round.
        //
        // ⚠ And the cluster-backed ones go first WITHIN that, which is new with #77 and is not
        // cosmetic. Seventeen suites sharing one permit are a serial chain about as long as the whole
        // gate — 18 m 14 s of which the cluster suites are most — so the chain has to start at t = 0.
        // Ordered the other way it starts whenever a thread first happens to reach a cluster suite,
        // and the gate is that chain PLUS whatever ran before it. OrderBy is a stable sort, so the
        // ordinal order ClassifiedTestProjects established survives inside each of the three groups
        // and the run order is still the same on every machine.
        var ordered = projects
            .OrderByDescending(clusterBacked.Contains)
            .ThenByDescending(containerBacked.Contains)
            .ToList();

        // ⚠ Constructed HERE, not lazily inside the loop body. The first version of this was a
        // `static SemaphoreSlim? slots` with `slots ??= new(...)`, which is not thread-safe: several
        // workers each built their own semaphore, so a thread could `Wait` on one instance and
        // `Release` another, and the run died with "Adding the specified count to the semaphore would
        // cause it to exceed its maximum count". One object, created before anything can race for it.
        using var containerSlots = new SemaphoreSlim(containerDegree, containerDegree);

        // ⚠ A SECOND semaphore rather than a smaller first one, and the difference is the whole of
        // #77. One semaphore can only express "how many suites", and the suites are not alike: four
        // of the twenty-one hold a PostgreSQL or a Redis and seventeen hold a Kubernetes control
        // plane. Shrinking the shared cap until the heavy case fits makes the light case wait for a
        // reason that is not true of it, and — worse — leaves the tree with no place to write down
        // what the real limit is, so the next cluster-backed suite silently spends the same budget
        // again. Two caps say two different sentences, and each one is checkable on its own.
        using var clusterSlots = new SemaphoreSlim(ClusterBackedSuiteDegree, ClusterBackedSuiteDegree);

        Parallel.ForEach(
            Partitioner.Create(ordered, EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            project =>
            {
                // ⚠ Two semaphores, each taken only by the suites it is about. A cheap suite waits on
                // neither, so capping either one costs nothing on the other 52 (2026-09-05).
                var gated = containerBacked.Contains(project);
                var holdsCluster = clusterBacked.Contains(project);

                // ⚠ CLUSTER PERMIT FIRST, CONTAINER PERMIT SECOND, ON EVERY THREAD, AND THE ORDER IS
                // THE CORRECTNESS ARGUMENT. Two locks taken in one order by everybody cannot
                // deadlock; taken in two orders they can, and the symptom would be a run sitting at
                // zero tests started — which is one of the four symptoms the failure message at the
                // bottom of this method already names as contention, so it would be diagnosed as the
                // thing it is not. The single `finally` below releases in the mirror-image order for
                // the same reader's sake, though release order cannot deadlock anything.
                //
                // ⚠ It also cannot starve. Only ClusterBackedSuiteDegree suites can be holding a
                // cluster permit and waiting for a container slot, and every holder of a container
                // slot is a suite that is running and will finish, so the wait is bounded by one
                // suite's wall clock rather than by another semaphore.
                //
                // ⚠ Both are taken OUTSIDE the try rather than one inside the other's, and that is
                // safe only because nothing between them can throw: neither Wait is given a token,
                // and both semaphores are disposed after Parallel.ForEach returns, so neither
                // OperationCanceledException nor ObjectDisposedException is reachable here. Two
                // nested try/finallys would say the same thing and would put two `try {` lines at
                // one indentation in a body this long, which is how a reader loses which is which.
                if (holdsCluster) {
                    clusterSlots.Wait();
                }

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
                // what makes running all of them at once safe: it rewrites the IL of the assemblies
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
                // specified command or file was not found" — an error about `dotnet` that is really
                // an error about a quote. Observed on every suite at once, all of them failing in
                // about ten seconds, which is the shape to recognise it by: a real tooling problem
                // does not arrive simultaneously everywhere and does not arrive that fast.
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
                    // ⚠ Released in the mirror image of the order they were taken. It does not matter
                    // to correctness here — a release never blocks — but a reader checking the
                    // lock-ordering argument above should be able to check it from one place.
                    if (gated) {
                        containerSlots.Release();
                    }

                    if (holdsCluster) {
                        clusterSlots.Release();
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

        ReportSkippedTests(target);

        Assert.Empty(
            named,
            $"{failures.Count} of {projects.Count} {target} suite(s) failed: {string.Join(", ", named)}. "
            + "Their output is above. ⚠ A suite named here with no failing test in its .trx did not "
            + "fail an assertion — it exited non-zero, and build/README.md § failed to bind host port "
            + "covers the commonest cause on this platform. ⚠ That section names a port-bind refusal "
            + "and this failure may not look like one: an Npgsql connect timeout inside a collection "
            + "fixture, a suite sitting at zero tests started, and a TLS reset from a k3s API server "
            + "that went away are all the same cause wearing different clothes. Run the named suite "
            + "alone before believing it, and if it passes alone, lower CC_TEST_CONTAINER_PARALLELISM. "
            + "⚠ If the named suite holds a Kubernetes cluster, that lever is the wrong one and "
            + "lowering it will look like it worked: at most "
            + $"{ClusterBackedSuiteDegree} such suite(s) run at a time whatever the container degree "
            + "is, so a cluster-backed suite starved on this host is a host that cannot hold ONE "
            + "cluster beside the container budget, not a degree that is too high. Build.Test.cs § "
            + "ClusterBackedSuiteDegree says what to do about that and why the number is not a "
            + "parameter. #77.");
    }

    /// <summary>
    ///     Prints what every suite in this run skipped, from the reports the suites just wrote.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing in this build read a skip count until this method, and that is a bigger
    ///         hole than it sounds.</b> Pass and fail come from a process exit code, and a suite
    ///         whose cluster-backed tests all skipped exits <b>0</b>: its daemonless companions keep
    ///         <c>--minimum-expected-tests 1</c> satisfied, the assembly prints <c>Passed!</c>, and
    ///         a gate reading the exit code cannot tell a run that proved nothing from one that
    ///         proved everything. That was measured — the bundle assembly's 1-in-8 second-k3s flake
    ///         (<c>EmptyClusterFixture</c>'s remarks) reported <c>Skipped: 1</c> inside a green
    ///         build, and it left no other trace.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It reports and does not fail, and the reason is the mirror-image trap
    ///         <c>Build.Architecture.cs</c> § <c>LabelsGate</c> spends a paragraph on.</b> Skipping
    ///         when no Docker daemon answers is this repository's contract, kept deliberately so a
    ///         developer without one gets a report rather than a red build. Failing on any skip
    ///         would break every such machine, and a gate people switch off is not a gate. Where a
    ///         skip is genuinely dishonest — a daemon that has already run one cluster in this
    ///         process and then could not run a second — the failure belongs at the fixture, which
    ///         is where <c>EmptyClusterFixture</c> now throws.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Best-effort by construction.</b> A suite that exits non-zero before writing a
    ///         report writes no <c>.trx</c> at all, and that case is already named in the failure
    ///         message above — so an unreadable or missing report is passed over here rather than
    ///         turned into a second, less informative failure about XML.
    ///     </para>
    /// </remarks>
    /// <param name="target">The target whose run is being summarised, for the log line.</param>
    void ReportSkippedTests(string target)
    {
        var skipped = TestResultsDirectory
            .GlobFiles("*.trx")
            .Select(report => (Suite: report.NameWithoutExtension, Count: NotExecuted(report)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Suite, StringComparer.Ordinal)
            .ToList();

        if (skipped.Count == 0)
        {
            Log.Information("{Target}: no test was skipped in this run.", target);

            return;
        }

        Log.Information(
            "{Target}: {Total} test(s) skipped across {Suites} suite(s) — {Detail}. ⚠ A skip is not "
            + "a pass: these are sentences this run did NOT check, and the exit code says nothing "
            + "about them. The commonest honest cause is no Docker daemon. Build.Test.cs § "
            + "ReportSkippedTests.",
            target,
            skipped.Sum(x => x.Count),
            skipped.Count,
            string.Join(", ", skipped.Select(x => $"{x.Suite} {x.Count}")));
    }

    /// <summary>
    ///     The skip count in one xunit TRX report, or <c>0</c> if it cannot be read.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>ResultSummary/Counters/@notExecuted</c> is TRX's name for a skip, and it is read
    ///     rather than the <c>UnitTestResult</c> rows counted: the summary is one attribute the
    ///     writer computes, and counting rows would quietly answer a different question the day a
    ///     report gains an outcome this build has not met.
    /// </remarks>
    /// <param name="report">The <c>.trx</c> a suite wrote.</param>
    static int NotExecuted(AbsolutePath report)
    {
        try
        {
            return XDocument.Load(report)
                .Descendants()
                .Where(x => x.Name.LocalName == "Counters")
                .Select(x => int.TryParse(
                    x.Attribute("notExecuted")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : 0)
                .Sum();
        }
        catch (Exception unreadable) when (unreadable is IOException or System.Xml.XmlException)
        {
            Log.Debug(
                unreadable,
                "Test: {Report} could not be read for its skip count, so this run's report omits it.",
                report);

            return 0;
        }
    }

    /// <summary>
    ///     Whether a suite can start a container, answered from what it was built with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS USED TO GREP THE <c>.csproj</c> FOR THE WORD "Testcontainers", AND THE CAP
    ///         ABOVE WAS THEREFORE NOT CAPPING WHAT IT SAID IT WAS.</b> Measured over this tree on
    ///         2026-08-20, at 71 suites: 28 project files contained the word and <b>19</b> suites
    ///         actually shipped the assemblies. It was wrong in both directions and the two errors
    ///         compounded. ⚠ Those are dated counts of a tree that grows a suite most weeks, and
    ///         nothing below reads them — they are here to say how large the gap was, not how large
    ///         it is.
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
    ///     <para>
    ///         ⚠ <b>"Testcontainers" WAS STILL TOO NARROW A PIECE OF EVIDENCE, AND #77 IS WHAT THAT
    ///         COST.</b> <c>CyberCloud.AppHost.Tests</c> starts Redis, PostgreSQL, NATS <em>and a
    ///         k3s</em> — through Aspire, whose <c>DistributedApplicationTestingBuilder</c> runs
    ///         <c>CyberCloud.AppHost</c>'s own <c>Program.cs</c> — and it ships not one
    ///         <c>Testcontainers</c> assembly, so this method called it cheap and the semaphore never
    ///         saw it. Measured 2026-09-05 on this tree: it is the only suite in it that ships
    ///         <c>Aspire.Hosting.Testing</c>, and the k3s it publishes is on the fixed host port 6443.
    ///         ⚠ That is the SAME failure as the <c>.csproj</c> grep this method replaced, one library
    ///         further along — a check that answers a narrower question than it appears to — so the
    ///         answer is not another package name bolted on here but
    ///         <see cref="StartsCluster" />, asked as an <c>or</c> below, which makes
    ///         "cluster-backed implies container-backed" true by construction rather than by two
    ///         globs that happen to agree.
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

        return output.GlobFiles("Testcontainers*.dll").Count > 0 || StartsCluster(project);
    }

    /// <summary>
    ///     Whether a suite holds a whole Kubernetes cluster, answered from what it was built with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the class the cap was always really about, and until #77 the tree had no
    ///         name for it.</b> Measured 2026-09-05 over this tree's build output, at 73 per-PR
    ///         suites: 21 can start a container and <b>17 of the 21 hold a k3s API server</b> — the
    ///         fifteen <c>*.Cluster.Conformance</c> assemblies, <c>CyberCloud.Kubernetes.Tests</c> and
    ///         <c>CyberCloud.AppHost.Tests</c>. The other four are
    ///         <c>CyberCloud.{Authorization,ServiceDefaults,Tenancy,Vault}.Tests</c>. A budget
    ///         denominated in container-backed suites was therefore a budget in clusters wearing a
    ///         more forgiving name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two pieces of evidence, and neither is a project name.</b>
    ///         <c>Testcontainers.K3s</c> in the output is the direct one. <c>Aspire.Hosting.Testing</c>
    ///         is the indirect one and is worth the paragraph it needs: a suite that ships it starts a
    ///         <c>DistributedApplication</c>, the only one in this tree is
    ///         <c>CyberCloud.AppHost</c>, and docs/plan/02 § ADR-014 puts a k3s in it — so on this
    ///         tree the inference is exact. On a tree where it is not, the wrong answer is the
    ///         over-gating one: a suite that starts an app host holding no cluster would wait for a
    ///         permit it does not need, which costs wall clock, and that is the same direction the
    ///         missing-output case below already chooses. ⚠ What would make it stale is a second
    ///         <c>DistributedApplication</c> in this repository with no cluster in it; the fix then is
    ///         to read the app host's resources rather than to add a project name to a list here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It deliberately does not ask whether the suite takes <c>ClusterSlot</c>.</b> That
    ///         lock file is the tree's cross-process permit and fifteen assemblies take it, but two of
    ///         the seventeen do not — <c>ClusterInfrastructure</c>'s own remarks say
    ///         <c>CyberCloud.Kubernetes.Tests</c> does not, and <c>CyberCloud.AppHost.Tests</c> takes
    ///         a different lock file entirely — so a rule phrased over the permit would have missed
    ///         exactly the two suites whose overlap #77 measured. The evidence has to be the cluster,
    ///         not the promise about it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Silent on a missing output directory, and only because
    ///         <see cref="StartsContainers" /> has already warned about the same directory</b> — both
    ///         are asked of every suite, and one fact should not produce two warnings. The answer is
    ///         the same safe direction: an unknown suite is treated as holding a cluster.
    ///     </para>
    /// </remarks>
    bool StartsCluster(AbsolutePath project) {
        var output = SuiteOutputDirectory(project);

        if (!output.DirectoryExists()) {
            return true;
        }

        return output.GlobFiles("Testcontainers.K3s*.dll", "Aspire.Hosting.Testing*.dll").Count > 0;
    }

    /// <summary>
    ///     The CPUs a container-backed suite is assumed to need to itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three, and it is measured — but read the next paragraph before quoting the
    ///         measurement, because the obvious one does not say what it looks like it says.</b>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE "FOUR STARVES A SUITE, THREE DOES NOT" MEASUREMENT OF 2026-08-19 CANNOT BE
    ///         USED TO CALIBRATE THIS NUMBER, AND THE REASON IS <see cref="StartsContainers" />.</b>
    ///         It was taken while the container-backed set was decided by grepping <c>.csproj</c>
    ///         files, which held slots for twelve suites that start no container and let three that
    ///         each hold a k3s cluster run free. "Degree 4" then meant "four mostly-cheap suites in
    ///         slots, plus up to three k3s clusters outside them"; it means "at most four container
    ///         suites, full stop" now. Dividing ten CPUs by a number that measured a different
    ///         mechanism would be arithmetic on a coincidence — the exact shape of mistake the
    ///         detector itself was.
    ///     </para>
    ///     <para>
    ///         So the evidence for three is direct, at the corrected meaning, on this ten-CPU host:
    ///         five full <c>Test</c> runs, four green at 71 suites, and the one loss was
    ///         <c>CyberCloud.Tenancy.Tests</c> failing its collection fixture on an Npgsql connect
    ///         timeout — one of the four symptoms the failure message names — which then passed
    ///         131/131 in 13.6 s when run alone. That is contention at the margin rather than a
    ///         degree that does not work, and it is recorded here rather than smoothed over: a
    ///         reader who sees this suite go red once in five runs should recognise it, not
    ///         rediscover it.
    ///     </para>
    ///     <para>
    ///         ⚠ It is a budget for the <em>suite</em>, not for one container. A `.Cluster.Conformance`
    ///         run holds a k3s API server, PostgreSQL and Redis plus its own test host, and k3s alone
    ///         spends most of its start-up saturating a core. ⚠ And it is a budget for the machine
    ///         the measurement was taken on. Four would be the safer number and costs roughly a third
    ///         of the gate's wall clock on a ten-core host; the lever for a host that needs it is
    ///         <c>CC_TEST_CONTAINER_PARALLELISM</c>, which is why the failure message names it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>#77 REPORTED THREE FAILING AND TWO PASSING ON THIS HOST, AND THE THREE STAYS —
    ///         2026-09-05.</b> Read on its own that table says the divisor is one too large; read
    ///         with <see cref="StartsCluster" /> it says something else. At 8548ee9 a slot could be
    ///         spent on a suite holding a whole k3s, and nothing stopped a second and a third being
    ///         held beside it: <c>CyberCloud.Kubernetes.Tests</c> takes no cross-process permit and
    ///         <c>CyberCloud.AppHost.Tests</c> was not gated at all. So "degree 3" could mean three
    ///         concurrent Kubernetes control planes, and "degree 2" made that particular overlap
    ///         one slot less likely — which is a difference in how often the run gets away with it,
    ///         not a calibration. Lowering the divisor would have bought green by narrowing a
    ///         window, and left the tree with the same three unrelated answers to "may I hold a
    ///         cluster". <see cref="ClusterBackedSuiteDegree" /> is the cap that closes it; this
    ///         number keeps meaning what it has meant since 2026-08-20, and the five-run measurement
    ///         above is still the evidence for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What would make the three stale</b> is what has always made it stale: a change to
    ///         what one container-backed suite costs. Concretely — a fifth non-cluster suite growing
    ///         a second heavy container, or the four that exist today
    ///         (<c>CyberCloud.{Authorization,ServiceDefaults,Tenancy,Vault}.Tests</c>, counted
    ///         2026-09-05) gaining a k3s and moving out of this budget into the other one. A new
    ///         <c>*.Cluster.Conformance</c> suite does <em>not</em> make it stale any more, which is
    ///         the property #77 asked for: they are capped by count of clusters, not by CPU.
    ///     </para>
    /// </remarks>
    const int CpusPerContainerBackedSuite = 3;

    /// <summary>
    ///     How many suites may hold a Kubernetes cluster at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One, and — unlike every other number in this file — it is not measured, not
    ///         derived, and not tunable, because it is not a property of the host.</b> It is the
    ///         invariant fifteen of the seventeen cluster-backed assemblies already keep among
    ///         themselves: <c>ClusterSlot</c>, in
    ///         <c>test/CyberCloud.Cluster.Conformance/Infrastructure/ClusterInfrastructure.cs</c>, is
    ///         a lock file taken before the containers and held until the process exits, and its own
    ///         remark states the guarantee — "however many of them a run contains, at most one is
    ///         holding a k3s container at a time". This constant is <c>build/</c> finally being told
    ///         about it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why it is worth having even though the permit already exists.</b> Two
    ///         suites are outside it and both were invisible here before #77:
    ///         <c>CyberCloud.Kubernetes.Tests</c>, which <c>ClusterInfrastructure</c>'s remark names
    ///         as unserialised ("would need one line in that project"), and
    ///         <c>CyberCloud.AppHost.Tests</c>, which takes
    ///         <c>cybercloud-apphost-local-topology.lock</c> — a different file, excluding only a
    ///         second copy of itself. Gating here covers all seventeen with no edit to either suite,
    ///         and it is the right place for the rule regardless: a permit taken inside a test
    ///         process cannot stop the build from starting the process, so the seventeen used to
    ///         spend the container budget on waiting rather than on working.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is deliberately no <c>CC_TEST_CLUSTER_PARALLELISM</c>.</b> An override that
    ///         cannot take effect is worse than none: raising this to 2 would still leave fifteen of
    ///         the seventeen queued behind <c>ClusterSlot</c>, so the setting would appear to work,
    ///         change almost nothing, and be believed. If a host genuinely holds two clusters, the
    ///         edit is this constant <em>and</em> <c>ClusterSlot</c>'s permit count, together, and
    ///         the reason they have to move together is this paragraph.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cost, and it is smaller than it looks.</b> The seventeen were already serial
    ///         in practice — fifteen by <c>ClusterSlot</c> — so this does not lengthen the chain; it
    ///         stops the other two from overlapping it, and it hands the container budget back to the
    ///         four suites that can actually use it. #77 measured the whole tree at 18 m 14 s with
    ///         the cluster suites effectively serialised already.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One consequence to know before reaching for it: a waiting suite still holds a
    ///         <see cref="Parallel" /> worker.</b> <c>MaxDegreeOfParallelism</c> is
    ///         <see cref="Environment.ProcessorCount" /> and the partitioner hands out one item at a
    ///         time in order, so while the head of the queue is seventeen cluster-backed suites, the
    ///         workers are pinned to them and the cheap suites behind them do not start — the queue
    ///         drains rather than overlaps until fewer cluster suites remain than there are workers.
    ///         That was already true before #77 (the container-backed suites were ordered first and
    ///         most of them were these), so it is not a regression, and it is deliberately left
    ///         alone: the fix would be to raise <c>MaxDegreeOfParallelism</c> above the core count on
    ///         the argument that a thread blocked on a semaphore is not using a CPU, and that also
    ///         raises the ceiling on how many <em>cheap</em> suites run at once, which is a number
    ///         nobody here has measured. It is the next thing to try if this gate's wall clock
    ///         becomes the complaint, and it should be measured before it is believed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What would make the one stale</b> is a change to the thing it mirrors: a
    ///         <c>ClusterSlot</c> that admits more than one process, or a host whose daemon is
    ///         measured holding two k3s API servers without either suite slowing down. Neither is a
    ///         thing this tree can observe on its own, which is exactly why the number is written
    ///         here as a claim about the tree rather than derived from
    ///         <see cref="Environment.ProcessorCount" /> — CPU count was never what limited it.
    ///     </para>
    /// </remarks>
    const int ClusterBackedSuiteDegree = 1;

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
    ///         ⚠ <b>The second of those two is no longer left to this arithmetic — #77.</b> It has no
    ///         API, but it does have an answer, and the answer had been in the tree the whole time:
    ///         <c>ClusterSlot</c> serialises the assemblies that hold one. <see cref="StartsCluster" />
    ///         is which suites those are and <see cref="ClusterBackedSuiteDegree" /> is the cap, and
    ///         the reason it is a separate semaphore rather than a smaller value here is that this
    ///         number is about CPU and that one is about a thing CPU never predicted. So what remains
    ///         unmodelled here is memory alone.
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
    /// <param name="baseline">
    ///     The reviewed rows of <see cref="CoverageBaselineFile" />, already parsed. ⚠ Passed in
    ///     rather than read here so a malformed row fails before the suites run rather than after —
    ///     see the call site.
    /// </param>
    void EnforceCoverageFloor(Dictionary<string, CoverageReport.Pin> baseline)
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
            "Test: coverage floor {Floor:P0} per project, over {Reports} report(s), with {Pinned} "
            + "project(s) pinned by {File} — docs/plan/23 § Test layers",
            CoverageFloor,
            reports.Count,
            baseline.Count,
            CoverageBaselineFile.Name);

        foreach (var module in coverage.Modules.Where(x => shipping.Contains(x.Assembly, StringComparer.Ordinal)))
        {
            // ⚠ A pinned project gets its own marker rather than a ✘, and the pin is printed beside
            // the rate. ✘ has to keep meaning "this run broke something": a reader scanning 68 rows
            // for a cross must not find six that were already true this morning, or they stop
            // scanning. ▪ says "known debt, and this is the number it is held to".
            var pinned = baseline.TryGetValue(module.Assembly, out var pin);

            Log.Information(
                "  {Marker} {Assembly,-46} {Rate,7:P1}  ({Covered} of {Coverable} lines){Pin}",
                module.Rate >= CoverageFloor ? "✔" : pinned ? "▪" : "✘",
                module.Assembly,
                module.Rate,
                module.Covered,
                module.Coverable,
                pinned ? $"  pinned {pin!.Rate:P1} — {CoverageBaselineFile.Name} line {pin.Line}" : string.Empty);
        }

        foreach (var project in nothingToCover.OrderBy(x => x, StringComparer.Ordinal))
        {
            Log.Information(
                "  {Marker} {Assembly,-46} {Note}",
                "○",
                project,
                "no coverable line to instrument — CoverageReport.cs § CoverableLines");
        }

        var violations = coverage.Violations(
            shipping,
            CoverageFloor,
            nothingToCover,
            baseline,
            CoverageBaselineFile.Name);

        if (violations.Count == 0)
        {
            Log.Information(
                "Test: {Count} shipping project(s) at or above the {Floor:P0} floor ({Empty} of them "
                + "with no executable code at all, {Pinned} below it and pinned by {File})",
                shipping.Count,
                CoverageFloor,
                nothingToCover.Count,
                baseline.Count,
                CoverageBaselineFile.Name);

            return;
        }

        foreach (var violation in violations)
            Log.Error("Test: {Violation}", violation);

        Assert.Fail(
            $"{violations.Count} coverage violation(s) over {shipping.Count} shipping project(s), "
            + $"listed above. docs/plan/23 § Test layers puts the floor at {CoverageFloor:P0}. ⚠ The "
            + "fix is a test, never an exclusion — a floor with a list of exemptions is a floor "
            + $"shaped like whatever the tree happened to be on the day it was added. {CoverageBaselineFile.Name} "
            + "is not that list: every row in it names a measured rate the project may not fall "
            + "below, must be deleted the moment the project reaches the floor, and is a review "
            + "request rather than a build fix.");
    }

    /// <summary>The reviewed list of projects below the floor, with the rate each is held to.</summary>
    AbsolutePath CoverageBaselineFile => RootDirectory / "coverage-below-floor.txt";

    /// <summary>
    ///     Each pinned project against its measured rate and the 1-based line it is written on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A row without a reason is refused, and that is the point of the file rather than
    ///         a formatting rule.</b> The whole value of <c>actions-without-handlers.txt</c> is that
    ///         every row says what is missing and what closing it would take, so the next person's
    ///         decision is cheap. A bare list of names and numbers is a list somebody adds to at 6pm;
    ///         a list where adding a row means writing a sentence a reviewer will read is a list that
    ///         stays short. The comment must be the line immediately above the row, with no blank
    ///         line between them, so a reason cannot drift away from what it explains.
    ///     </para>
    ///     <para>
    ///         Deliberately the same shape as Build.Architecture.cs § ActionsWithoutHandlers, down to
    ///         reporting a duplicate by both line numbers: two pins for one project means one of them
    ///         is not the measurement.
    ///     </para>
    /// </remarks>
    // Dictionary rather than IReadOnlyDictionary: CA1859 is an error here and this is internal to
    // the build — the same concession Build.Test.cs § ProjectsIn already makes.
    Dictionary<string, CoverageReport.Pin> CoverageBaseline()
    {
        Assert.FileExists(
            CoverageBaselineFile,
            $"{CoverageBaselineFile.Name} is missing. It is the reviewed list of projects below the "
            + $"{CoverageFloor:P0} floor and the rate each is held to; without it this gate cannot "
            + "tell debt somebody signed off from a regression this change introduced, and treating "
            + "all of it as new would fail the build over the former.");

        var rows = new Dictionary<string, CoverageReport.Pin>(StringComparer.Ordinal);
        var lines = CoverageBaselineFile.ReadAllLines();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.True(
                parts.Length == 2,
                $"{CoverageBaselineFile.Name} line {i + 1} is '{line}'. A row is the project name, "
                + "whitespace, and the measured rate as a percentage with one decimal — for example "
                + "'CyberCloud.Silo.Host    32.5'.");

            Assert.True(
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                && percent >= 0
                && percent < CoverageFloor * 100,
                $"{CoverageBaselineFile.Name} line {i + 1} pins {parts[0]} at '{parts[1]}'. That has to "
                + $"be a number between 0 and {CoverageFloor * 100:F0} — a pin at or above the floor "
                + "is not debt, it is a project that belongs out of this file entirely.");

            // ⚠ The reason, and it is required. See the remarks: the line immediately above, no blank
            // line between, so it cannot drift away from the row it explains.
            Assert.True(
                i > 0 && lines[i - 1].TrimStart().StartsWith('#') && lines[i - 1].Trim().TrimStart('#').Trim().Length > 0,
                $"{CoverageBaselineFile.Name} line {i + 1} pins {parts[0]} and the line above it is not "
                + "a comment. Every row needs a sentence directly above it saying what is uncovered "
                + "and what closing it would take — a row is a review request, and a reviewer cannot "
                + "answer one that is a name and a number.");

            Assert.True(
                rows.TryAdd(parts[0], new(percent / 100, i + 1)),
                $"{CoverageBaselineFile.Name} pins '{parts[0]}' twice, on lines "
                + $"{rows.GetValueOrDefault(parts[0])?.Line} and {i + 1}. Two rates for one project "
                + "means one of them is not the measurement.");
        }

        return rows;
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
