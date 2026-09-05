using Aspire.Hosting.Testing;

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
/// </remarks>
public sealed class ClusterBackedGatingTests {
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
}
