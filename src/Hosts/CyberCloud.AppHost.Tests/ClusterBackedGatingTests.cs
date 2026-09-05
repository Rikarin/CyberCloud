using Aspire.Hosting.Testing;
using System.Text.RegularExpressions;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     That <c>build/Build.Test.cs</c> can still tell this suite holds a Kubernetes cluster.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite starts a k3s, and for months nothing in <c>build/</c> knew.</b>
///         <c>Build.Test.cs</c> § <c>StartsContainers</c> decided which suites had to queue for a
///         container permit by globbing each suite's output directory for
///         <c>Testcontainers*.dll</c>. This one brings up Redis, PostgreSQL, NATS <em>and</em> a k3s
///         on the fixed host port <c>6443</c> — see <see cref="LocalTopology" /> — through Aspire,
///         and ships no Testcontainers assembly at all, so it ran ungated beside whichever
///         <c>*.Cluster.Conformance</c> suite held the cross-process permit. #77 is the run where
///         that was measured, and <c>Build.Test.cs</c> § <c>StartsCluster</c> is the fix: it globs
///         for <c>Aspire.Hosting.Testing*.dll</c> as well.
///     </para>
///     <para>
///         ⚠ <b>Which leaves a literal assembly name in a build file as the only thing holding the
///         gate on.</b> Neither compiler checks it. If this suite's bring-up moves to another
///         library, or that library is renamed, or the reference stops being copied next to the test
///         host, the glob matches nothing, the build silently calls this suite cheap again, and the
///         symptom is a <em>different</em> suite failing somewhere else in the run — which is exactly
///         how #77 presented and exactly how long it took to attribute. So the name is spelled here
///         too, against the type this suite actually uses to start the topology. Same defect class,
///         and same defence, as <c>CyberCloud.ResourceManager.Generator.Tests</c>
///         § <c>GenerationReportTests</c>: two spellings of one name across a boundary neither
///         compiler checks, and the only defence is a test that spells both.
///     </para>
///     <para>
///         ⚠ <b>Deliberately outside <see cref="LocalTopologySuite" />.</b> These are facts about
///         what this assembly was built with, not about a running topology, so they must not wait on
///         the collection fixture — and they are the only tests here that answer on a machine with no
///         Docker daemon, which is what keeps <c>--minimum-expected-tests 1</c> satisfiable when the
///         rest of the suite cannot run. Same reasoning as <c>ClusterInfrastructure</c>'s remark
///         about the one test in that assembly that never touches Docker.
///     </para>
///     <para>
///         ⚠ <b>And that sentence was false for one day, which is issue #82.</b> The two tests that
///         read <c>build/Build.Test.cs</c> arrived with a <c>static string BuildTestSource { get; } =
///         File.ReadAllText(…)</c>, and a static property initialiser runs in the type's static
///         constructor — triggered by first access to <em>any</em> static member of the type. So the
///         claim above quietly became "answer on a machine with no Docker daemon <em>and</em> with the
///         repository source tree beside the artifacts directory": a run from published or copied
///         output failed all of these with one <c>TypeInitializationException</c>, and a
///         published-artifacts run is exactly the degraded environment the daemonless ones exist to
///         survive. The read is now a <see cref="Lazy{T}" /> dereferenced inside
///         <see cref="SourceOf" />, so <b>four</b> of the six tests here need nothing but this
///         assembly, and the two that need the source tree fail on their own with the path in the
///         exception. <see cref="ReachingTheRepositoryIsNotPartOfInitialisingThisClass" /> is what
///         keeps it that way.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS CLASS COVERS, STATED EXACTLY, BECAUSE #77'S COMMIT MESSAGE CALLED IT "THE
///         REGRESSION TEST" AND THAT OVERSTATED IT.</b> The first three tests below assert facts
///         about <em>this</em> assembly against a copy of the build's globs, so on their own they
///         would all stay green if <c>StartsCluster</c> were deleted outright and
///         <c>StartsContainers</c> reverted to its bare <c>Testcontainers*.dll</c> glob — the exact
///         hole #77 measured, reopened, with a green suite over it. The review that found that is
///         right, and the two below the line that says so are the repair: they read
///         <c>build/Build.Test.cs</c> itself and fail if the globs the build runs are no longer the
///         globs this file copies, or if <c>StartsContainers</c> stops delegating to
///         <c>StartsCluster</c>. There is no test project under <c>build/</c> to put them in, and a
///         suite that is <i>itself</i> the subject of the classification is the honest second-best
///         place for them. The sixth,
///         <see cref="ReachingTheRepositoryIsNotPartOfInitialisingThisClass" />, covers the cost that
///         reading a source file from here turned out to have — issue #82, two paragraphs up.
///     </para>
///     <para>
///         ⚠ <b>What they still do not cover</b> is the semaphore, the ordering and the degrees —
///         those are behaviour of a <c>Parallel.ForEach</c> over the whole tree, and a test that
///         re-implemented them would be a test of itself. They are covered by the argument written
///         out at length in <c>Build.Test.cs</c> § <c>ClusterBackedSuiteDegree</c> and by the log
///         line <c>RunSuites</c> prints on every run, which names both counts and both degrees.
///     </para>
/// </remarks>
public sealed partial class ClusterBackedGatingTests {
    /// <summary>
    ///     Whether <see cref="TestPaths.Repository" /> had already been resolved by something else in
    ///     this assembly before this class began initialising.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>ITS POSITION IN THIS FILE IS LOAD-BEARING AND IT MUST STAY THE FIRST STATIC FIELD.</b>
    ///     Static field initialisers run in textual order and all of them run before the static
    ///     constructor body, so this line is the only place that can observe the repository flag
    ///     BEFORE any of this class's own initialisers have had a chance to move it. A field added
    ///     above this one that reaches the repository would be invisible to
    ///     <see cref="ReachingTheRepositoryIsNotPartOfInitialisingThisClass" /> — a false green in the
    ///     guard whose entire job is to refuse one.
    ///     ⚠ <b>Why a snapshot and not just an assertion that the flag is false.</b> A
    ///     <see cref="Lazy{T}" /> resolves once for the process and cannot say who forced it, and
    ///     <c>ReconcileThroughTheRealHostTests</c> and <c>TenantOverHttpTests</c> both resolve
    ///     <see cref="TestPaths.AppHostDirectory" /> from static initialisers of their own. In a
    ///     whole-suite run either may initialise first, so the bare flag answers "true" or "false"
    ///     depending on execution order. The delta between this snapshot and the same flag read in
    ///     the static constructor does not: it is exactly what THIS class's initialisation did.
    /// </remarks>
    static readonly bool RepositoryWasResolvedBeforeThisClass = TestPaths.RepositoryHasBeenResolved;

    /// <summary>
    ///     The globs <c>build/Build.Test.cs</c> § <c>StartsCluster</c> runs over a suite's output
    ///     directory, copied verbatim.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both, not only the one that matches today. A test that knew about the Aspire glob alone
    ///     would go green if somebody replaced it with something else that happened to match, which
    ///     is a check on this file rather than on the build.
    /// </remarks>
    static readonly string[] ClusterEvidence = ["Testcontainers.K3s*.dll", "Aspire.Hosting.Testing*.dll"];

    /// <summary>Where this test host was built, which is the directory the build globs.</summary>
    static string OutputDirectory { get; } =
        Path.GetDirectoryName(typeof(ClusterBackedGatingTests).Assembly.Location)!;

    [Fact]
    public void TheLibraryThatBringsUpTheTopologyIsTheOneTheBuildGlobsFor() {
        // ⚠ The type, not a string: LocalTopology calls DistributedApplicationTestingBuilder to run
        // CyberCloud.AppHost's own Program.cs, so this is the assembly whose presence beside the test
        // host is what makes the k3s happen. If it ever moves, this line moves with it and the
        // assertion below is what says the build has to be told.
        var providing = typeof(DistributedApplicationTestingBuilder).Assembly.GetName().Name;

        providing.ShouldBe(
            "Aspire.Hosting.Testing",
            "build/Build.Test.cs § StartsCluster decides that this suite holds a Kubernetes cluster "
            + "by globbing its output directory for Aspire.Hosting.Testing*.dll. The type that "
            + "actually starts the topology now lives in a different assembly, so that glob matches "
            + "nothing and the build has gone back to running this suite's k3s ungated, beside "
            + "another one. Add the new name to StartsCluster and to this test. #77."
        );
    }

    [Fact]
    public void TheEvidenceTheBuildLooksForIsBesideThisTestHost() {
        var found = ClusterEvidence
            .SelectMany(pattern => Directory.GetFiles(OutputDirectory, pattern))
            .ToList();

        found.ShouldNotBeEmpty(
            $"none of [{string.Join(", ", ClusterEvidence)}] is in {OutputDirectory}, and those are "
            + "the globs build/Build.Test.cs § StartsCluster answers with. This suite still starts a "
            + "k3s — LocalTopology publishes one on host port 6443 — so the build is now free to run "
            + "it at the same time as one of the sixteen other cluster-backed suites, which is the "
            + "overlap #77 measured. The likely cause is a PackageReference that stopped being copied "
            + "next to the test host, not a suite that stopped needing a cluster."
        );
    }

    [Fact]
    public void ThisSuitesOutputDirectoryIsTheOneTheBuildInspects() {
        // ⚠ The other half of both globs, and it is an assumption rather than an observation
        // everywhere else: Build.Test.cs § SuiteOutputDirectory computes
        // artifacts/bin/<project>/<configuration> from Directory.Build.props' ArtifactsPath and
        // never checks that a suite actually landed there. When it is wrong the build does not fail
        // — StartsContainers logs a warning and treats the suite as container-backed — so the whole
        // classification quietly degrades to "everything is expensive" and the only visible symptom
        // is a slower gate. That is a bad way to find out, so it is asserted from the one side that
        // can see where the assembly really is.
        //
        // ⚠ AND "SLOWER" UNDERSTATES IT SINCE #77, which is worth knowing before reading a red run
        // here as cosmetic. StartsCluster answers the same missing directory the same safe way, so a
        // suite that lands there is gated on BOTH permits and the cluster one is 1 — the degraded
        // case used to be the whole gate at the derived container degree and is now the whole gate
        // strictly serial, 73 suites one at a time on a job with timeout-minutes: 30.
        // Build.Test.cs § StartsContainers has the warning that says so.
        var configuration = new DirectoryInfo(OutputDirectory);

        var because =
            "Build.Test.cs § SuiteOutputDirectory expects artifacts/bin/<project>/<configuration>, "
            + $"and this assembly is in {OutputDirectory}. Directory.Build.props § ArtifactsPath is "
            + "what decides the layout; the build's container and cluster classification both read a "
            + "directory computed from it.";

        // ⚠ Pulled out and asserted non-null rather than reached through `?.`, which would make a
        // missing parent a silently passing test — the shape of green this repository refuses.
        var project = configuration.Parent.ShouldNotBeNull(because);

        project.Name.ShouldBe("CyberCloud.AppHost.Tests", because);
        project.Parent.ShouldNotBeNull(because).Name.ShouldBe("bin", because);
    }

    // ── The two that read build/Build.Test.cs, and why they have to ───────────────────────────────
    //
    // ⚠ Everything above this line asserts a property of THIS assembly. That is a rot detector for
    // the Aspire reference, and the #77 review pointed out what it is not: with StartsCluster
    // deleted and StartsContainers reverted to `Testcontainers*.dll`, every test above still passes
    // and this suite goes back to running its k3s ungated beside another one. ClusterEvidence was a
    // PRIVATE COPY of two literals with nothing checking that it was still a copy of anything.
    //
    // ⚠ So the copy is checked against the original. Reading a source file is a blunt instrument and
    // it is the one available: build/ has no test project, `_build.csproj` is not referenced from
    // any suite, and the alternative — a third file listing the globs for both to agree with — is
    // one more place for the same drift. Both tests fail with the edit a reader has to make, which
    // is the property that matters: this is the file the person renaming a package will not think of.

    /// <summary>
    ///     The build file both globs are actually spelled in, read on first use and never before.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="Lazy{T}" /> and not a property initialiser, and issue #82 is the whole of
    ///     the reason.</b> This read
    ///     <c>static string BuildTestSource { get; } = File.ReadAllText(…)</c>, which runs in the
    ///     static constructor along with <see cref="ClusterEvidence" /> and
    ///     <see cref="OutputDirectory" />, so <see cref="TestPaths" />'s walk for
    ///     <c>CyberCloud.slnx</c> and this <c>ReadAllText</c> both became preconditions of touching the
    ///     class at all. Constructing a <c>Lazy&lt;string&gt;</c> touches no disk; the repository is
    ///     reached only from <see cref="SourceOf" />, which only the two source-reading tests call.
    ///     ⚠ Not <c>=> File.ReadAllText(…)</c> on every access either: <see cref="SourceOf" /> reads
    ///     the property three times per call and is called twice, and six reads of the same file to
    ///     avoid one <c>Lazy</c> is a worse trade in a suite whose whole complaint is I/O it did not
    ///     need. <c>Lazy&lt;T&gt;</c>'s default mode also caches the exception, so a missing source
    ///     tree gives both tests the same message rather than a different one each.
    /// </remarks>
    static readonly Lazy<string> LazyBuildTestSource =
        new(() => File.ReadAllText(Path.Combine(TestPaths.Repository, "build", "Build.Test.cs")));

    static string BuildTestSource => LazyBuildTestSource.Value;

    /// <summary>
    ///     Whether initialising this type reached the repository — by forcing
    ///     <see cref="LazyBuildTestSource" />, or by resolving <see cref="TestPaths.Repository" /> any
    ///     other way.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Frozen in the static constructor rather than asked at test time, and that is the only
    ///     way the question has a stable answer.</b> By the time any test in this class runs the type
    ///     is initialised and one of the source-reading tests may already have forced the
    ///     <c>Lazy</c>, so <c>IsValueCreated</c> read from a test body would answer "true" or "false"
    ///     depending on execution order. The static constructor body runs after every static field
    ///     initialiser in the type and before any test, so what it sees is exactly "did initialising
    ///     this class reach the repository", which is the property issue #82 is about.
    ///     <para>
    ///         ⚠ <b>TWO TERMS, AND THE SECOND ONE ARRIVED WITH THIS BRANCH'S REVIEW.</b> The flag was
    ///         <c>LazyBuildTestSource.IsValueCreated</c> alone, which observes exactly one sabotage:
    ///         something forcing THAT <c>Lazy</c>. Issue #82's defect class is wider than one field —
    ///         it is "initialising this class reaches the repository" — so a future static initialiser
    ///         that called <see cref="TestPaths.Repository" /> for some unrelated path would re-create
    ///         the whole defect with this guard still green. The
    ///         <see cref="RepositoryWasResolvedBeforeThisClass" /> delta closes that: any initialiser
    ///         here that resolves the repository root, through <c>TestPaths</c> or through the
    ///         <c>Lazy</c> whose factory calls it, moves the flag between the first static field and
    ///         the static constructor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first term is still needed and is not redundant.</b> If another class in this
    ///         assembly has already resolved the root, the delta is false even when this class's
    ///         initialisation forces the source read — the <c>Lazy</c> resolves once for the process.
    ///         So the two terms cover different halves of one property and the guard is their OR.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>WHAT IS STILL NOT OBSERVED, stated rather than implied — and it is two things,
    ///         not one.</b> First: a static initialiser added here that reaches the disk WITHOUT going
    ///         through either — its own <c>File.ReadAllText</c> on a path it computes for itself —
    ///         re-creates issue #82 and leaves this green. No in-process flag can see that; only a
    ///         reviewer can. Second, and less obvious: <b>the second term is a LOWER BOUND, not a
    ///         measurement.</b> <c>RepositoryWasResolvedBeforeThisClass</c> is a snapshot, so if any
    ///         other class in this assembly resolved the root first the snapshot is already
    ///         <c>true</c> and the term is structurally incapable of moving — a future initialiser
    ///         here that reaches the repository through <c>TestPaths</c> rather than through
    ///         <see cref="LazyBuildTestSource" /> is then invisible, and the guard stays green. So the
    ///         delta answers "did THIS class's initialisation reach the repository" only when this
    ///         class is the first to ask; otherwise it answers nothing and cannot say so.
    ///     </para>
    ///     <para>
    ///         ⚠ That is the same overstatement twice, and it is worth naming as a pattern rather
    ///         than fixing quietly: #77's commit message called <c>ClusterBackedGatingTests</c> "the
    ///         regression test" when it asserted nothing about the code it named, #82's first guard
    ///         claimed to cover "initialising this class reaches the repository" when it watched one
    ///         <c>Lazy</c>, and this paragraph's predecessor claimed the widened delta "closes" that
    ///         class when it narrows it. A guard in this file has been described as stronger than it
    ///         is on every attempt so far; the next person to widen it should assume the same of their
    ///         own wording and write down what it cannot see before writing what it can.
    ///     </para>
    /// </remarks>
    static readonly bool ReachedTheRepositoryDuringTypeInitialisation;

    static ClusterBackedGatingTests() =>
        ReachedTheRepositoryDuringTypeInitialisation =
            LazyBuildTestSource.IsValueCreated
            || (TestPaths.RepositoryHasBeenResolved && !RepositoryWasResolvedBeforeThisClass);

    /// <summary>The source of one method of <see cref="BuildTestSource" />, by its signature.</summary>
    /// <remarks>
    ///     ⚠ Bracketed by the signature and the first <c>}</c> at member indentation, rather than
    ///     parsed. A brace counter would be the correct way to do this and would be forty lines of
    ///     test-only code with its own defects; both methods here are eight lines with no nested type
    ///     in them, and a reformat that breaks this shows up as a failing test naming the signature
    ///     it could not find, which is the outcome a reader can act on either way.
    /// </remarks>
    static string SourceOf(string signature) {
        var start = BuildTestSource.IndexOf(signature, StringComparison.Ordinal);

        start.ShouldBeGreaterThanOrEqualTo(
            0,
            $"build/Build.Test.cs no longer contains the line `{signature}`. Either the method was "
            + "renamed or removed — in which case the classification #77 added has gone and this "
            + "suite's k3s is ungated again — or it was only reformatted, in which case this test "
            + "needs the new spelling. #77."
        );

        var end = BuildTestSource.IndexOf("\n    }", start, StringComparison.Ordinal);

        end.ShouldBeGreaterThan(
            start,
            $"`{signature}` in build/Build.Test.cs is not closed by a `}}` at member indentation, so "
            + "this test cannot tell where the method ends."
        );

        return BuildTestSource[start..end];
    }

    [Fact]
    public void TheGlobsThisTestCopiesAreTheGlobsTheBuildActuallyRuns() {
        var call = GlobFilesCall.Match(SourceOf("bool StartsCluster(AbsolutePath project) {"));

        call.Success.ShouldBeTrue(
            "build/Build.Test.cs § StartsCluster no longer decides anything with a GlobFiles call, so "
            + "the evidence this test copies cannot be compared to it. If the classifier now reads "
            + "something other than the suite's output directory, this test has to read it too. #77."
        );

        var globs = StringLiteral
            .Matches(call.Groups["args"].Value)
            .Select(match => match.Groups["glob"].Value)
            .ToArray();

        globs.ShouldBe(
            ClusterEvidence,
            "build/Build.Test.cs § StartsCluster globs for a different set of assemblies than "
            + "ClusterEvidence above lists, so this suite's copy of the rule has drifted from the "
            + "rule. Whichever is right, the two spellings are what #77 added this class to keep "
            + "together — fix both."
        );
    }

    [Fact]
    public void TheContainerClassifierStillDefersToTheClusterOne() {
        SourceOf("bool StartsContainers(AbsolutePath project) {").ShouldContain(
            "StartsCluster(project)",
            customMessage:
            "build/Build.Test.cs § StartsContainers no longer asks StartsCluster, so "
            + "\"cluster-backed implies container-backed\" is back to holding only while two globs "
            + "happen to agree. This suite ships no Testcontainers assembly, so the container glob "
            + "alone calls it cheap: it is the one that stops being gated, which is exactly #77."
        );
    }

    /// <summary>
    ///     That the three tests above which need only this assembly still need only this assembly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It asserts a fact about the STATIC CONSTRUCTOR, because that is where issue #82
    ///     lived and nothing else in this class can see it.</b> Adding
    ///     <c>_ = LazyBuildTestSource.Value;</c> to the static constructor turns this red and
    ///     leaves the other five green. A static initialiser that resolves
    ///     <see cref="TestPaths.Repository" /> for some other reason turns it red <i>only when this
    ///     class is the first in the assembly to resolve the root</i> — see the second half of
    ///     <see cref="ReachedTheRepositoryDuringTypeInitialisation" />'s remarks, which says why that
    ///     term is a lower bound rather than a measurement. Red here is the shape the defect had: every test passing on a
    ///     machine that has the source tree, and every test failing at once on a machine that does
    ///     not.
    ///     ⚠ <b>Why not the direct test</b> — run the class with the repository absent and watch the
    ///     other four still pass: <see cref="TestPaths" /> finds the root by walking up from the
    ///     assembly's own location, so a test process cannot be handed a different answer without
    ///     moving the assembly. This is the observable half of the same property, in-process and with
    ///     no fixture.
    ///     <para>
    ///         ⚠ <b>WHAT A LITERAL REVERT DOES, BECAUSE THIS REMARK CLAIMED THE WRONG THING AND THE
    ///         DISTINCTION IS THE POINT OF THE TEST.</b> It said that "putting
    ///         <c>File.ReadAllText</c> back in a static initialiser" turns this red. It did not.
    ///         Someone restoring the pre-#82 shape literally writes
    ///         <c>static string BuildTestSource { get; } = File.ReadAllText(…)</c> and DELETES
    ///         <see cref="LazyBuildTestSource" /> with it, so a guard that named that field stopped
    ///         compiling — and the reverter's cheapest way out of a build error is to delete the test,
    ///         not to fix the defect. That is a guard removed by the same edit that reopens the bug,
    ///         which is the worst failure mode a regression test has. It is now only half true: the
    ///         first term of <see cref="ReachedTheRepositoryDuringTypeInitialisation" /> still stops
    ///         compiling, but the second does not, and that literal revert resolves
    ///         <see cref="TestPaths.Repository" /> from a static initialiser of this class — so once
    ///         the reverter has dropped the dead first term, the guard compiles and fails. The
    ///         deletion is at least no longer forced, and it is visible in a diff.
    ///     </para>
    ///     <para>
    ///         ✔ <b>Verified by breaking it, on 2026-09-05</b>, filtered to this class alone so that
    ///         nothing in the run starts a container — which is the state this class is supposed to
    ///         answer in, and the rest of this suite cannot. Runs of
    ///         <c>--filter-class …ClusterBackedGatingTests</c>, all probes reverted afterwards:
    ///         as committed, <b>6 passed</b>; with <c>_ = LazyBuildTestSource.Value;</c> added to the
    ///         static constructor — the fix reverted in the smallest way that still compiles —
    ///         <b>1 failed, 5 passed</b>, and the one was this test; with the <c>Lazy</c> pointed at a
    ///         file that does not exist, standing in for the absent source tree, <b>2 failed,
    ///         4 passed</b> — the two that read the source, which is the outcome issue #82 asked for;
    ///         with both sabotages at once, the pre-#82 shape exactly, <b>6 failed, 0 passed</b> on one
    ///         <c>TypeInitializationException</c>.
    ///     </para>
    ///     <para>
    ///         ✔ <b>And the term this branch's review added was measured against the guard it
    ///         replaces, which is the only way to show a widening is not decoration.</b> A third
    ///         sabotage, <c>_ = TestPaths.Repository;</c> as the first statement of the static
    ///         constructor — initialisation reaching the repository while touching no <c>Lazy</c> of
    ///         this class's, which is the defect shape the review named — run twice on 2026-09-05:
    ///         with the flag as it now stands, <b>1 failed, 5 passed</b> and the one was this test;
    ///         with this class put back to the one-term guard exactly as it stood before the review —
    ///         <see cref="RepositoryWasResolvedBeforeThisClass" /> deleted and the flag reduced to
    ///         <c>LazyBuildTestSource.IsValueCreated</c> — and the same sabotage in place,
    ///         <b>6 passed, 0 failed</b>. The old guard was blind to it.
    ///         ⚠ It is a statement in the static constructor rather than the static FIELD the defect
    ///         would really arrive as, and that is a limitation of the probe and not of the guard:
    ///         a field spelled <c>static readonly string Unused = TestPaths.Repository;</c> does not
    ///         build here — <c>IDE0051</c> is an error in this tree — and both forms run between
    ///         <see cref="RepositoryWasResolvedBeforeThisClass" /> and the flag, which is the whole
    ///         of what the delta measures.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ReachingTheRepositoryIsNotPartOfInitialisingThisClass() {
        ReachedTheRepositoryDuringTypeInitialisation.ShouldBeFalse(
            "initialising this class resolved the repository root — by reading build/Build.Test.cs, "
            + "or by asking TestPaths for a path some other way — so it is a precondition of touching "
            + "ANY test here rather than of the two that read the build source. A machine with no "
            + "repository source tree beside the artifacts directory — a published-artifacts or "
            + "copied-output run — now fails all six of these with one TypeInitializationException, "
            + "including the ones that assert facts about this assembly and nothing else. Those are "
            + "what keeps --minimum-expected-tests 1 satisfiable when no Docker daemon is available, "
            + "so coupling them to the source tree is issue #82 reopened. Read the file through "
            + "SourceOf, and reach TestPaths from a test body, not from a static initialiser."
        );
    }

    /// <summary>The argument list of a <c>GlobFiles</c> call, which is where the evidence is named.</summary>
    [GeneratedRegex(@"GlobFiles\((?<args>[^)]*)\)")]
    private static partial Regex GlobFilesCall { get; }

    /// <summary>One double-quoted literal.</summary>
    /// <remarks>
    ///     ⚠ No escape handling, and it does not need any: a glob that contained a <c>\"</c> would be
    ///     split in half here and the equality assertion above would fail naming both halves. The
    ///     failure mode is a confusing red, not a false green, which is the direction to be sloppy in.
    /// </remarks>
    [GeneratedRegex("\"(?<glob>[^\"]*)\"")]
    private static partial Regex StringLiteral { get; }
}
