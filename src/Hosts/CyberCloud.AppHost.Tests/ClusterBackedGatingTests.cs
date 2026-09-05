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
///         ⚠ <b>WHAT THIS CLASS COVERS, STATED EXACTLY, BECAUSE #77'S COMMIT MESSAGE CALLED IT "THE
///         REGRESSION TEST" AND THAT OVERSTATED IT.</b> The first three tests below assert facts
///         about <em>this</em> assembly against a copy of the build's globs, so on their own they
///         would all stay green if <c>StartsCluster</c> were deleted outright and
///         <c>StartsContainers</c> reverted to its bare <c>Testcontainers*.dll</c> glob — the exact
///         hole #77 measured, reopened, with a green suite over it. The review that found that is
///         right, and the last two tests are the repair: they read <c>build/Build.Test.cs</c> itself
///         and fail if the globs the build runs are no longer the globs this file copies, or if
///         <c>StartsContainers</c> stops delegating to <c>StartsCluster</c>. There is no test
///         project under <c>build/</c> to put them in, and a suite that is <i>itself</i> the subject
///         of the classification is the honest second-best place for them.
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

    /// <summary>The build file both globs are actually spelled in.</summary>
    static string BuildTestSource { get; } =
        File.ReadAllText(Path.Combine(TestPaths.Repository, "build", "Build.Test.cs"));

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
