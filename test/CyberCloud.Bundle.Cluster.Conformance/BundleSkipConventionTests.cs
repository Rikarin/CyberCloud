using CyberCloud.Cluster.Conformance.Infrastructure;
using Shouldly;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     That every skip this assembly makes for a missing daemon or tool carries the word
///     <c>build/Build.Test.cs</c> reads — the bundle's half of
///     <c>CyberCloud.Cluster.Conformance</c> § <c>SkipConventionTests</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the suite the build's second clause was written for, and it was the one
///         suite the convention's test did not reach.</b> On 2026-09-17 this assembly passed with
///         Docker present and <c>helm</c> absent — 16 executed, 3 skipped, exit 0 — which is what
///         made <c>Build.Test.cs</c> § <c>ReportClusterBackedCases</c> read <c>NEEDS:</c> out of a
///         skip at all. The two fixtures' <c>Skip</c> methods then spelled the word by hand, and
///         <c>SkipConventionTests</c> pinned only <c>ClusterInfrastructure.SkipMessage</c>; the
///         review of that commit found the bundle's skips free to drift out of the guard silently.
///         They now interpolate <see cref="ClusterInfrastructure.PrerequisiteMarker" />, that
///         constant is what <c>SkipConventionTests</c> pins against the build, and this class pins
///         the three writers here against the constant — so a hand-typed replacement of any one of
///         them goes red on a machine with no Docker at all.
///     </para>
///     <para>
///         The fixtures are constructed and never initialised: <c>Skip</c> reads a field the
///         constructor leaves null and reports "no exception was recorded", which is the message's
///         shape and all this class reads.
///     </para>
/// </remarks>
public sealed class BundleSkipConventionTests {
    [Fact]
    public void TheEmptyClusterFixtureSkipNamesItsPrerequisites() =>
        new EmptyClusterFixture().Skip(BundleInstaller.CertManagerComponent, "some-row", "nothing").ShouldContain(
            ClusterInfrastructure.PrerequisiteMarker,
            Case.Sensitive,
            "EmptyClusterFixture.Skip no longer carries ClusterInfrastructure.PrerequisiteMarker, so a run "
            + "with Docker and no helm skips the three installing classes and reads as green — the exact "
            + "shape measured on 2026-09-17 that build/Build.Test.cs § PrerequisiteSkips exists to refuse."
        );

    [Fact]
    public void TheStoryFixtureSkipNamesItsPrerequisites() =>
        new M1StoryClusterFixture().Skip("nothing").ShouldContain(
            ClusterInfrastructure.PrerequisiteMarker,
            Case.Sensitive,
            "M1StoryClusterFixture.Skip no longer carries ClusterInfrastructure.PrerequisiteMarker, so the "
            + "docs/plan/24 § Phase 2 story skipping beside a daemon reads as a green run."
        );

    [Fact]
    public void TheDaemonFreeSkipNamesItsPrerequisite() =>
        BundleInstaller.SkipWithoutBash("install.sh", "nothing").ShouldContain(
            ClusterInfrastructure.PrerequisiteMarker,
            Case.Sensitive,
            "BundleInstaller.SkipWithoutBash no longer carries ClusterInfrastructure.PrerequisiteMarker, so "
            + "the thirteen daemon-free tests skipping for a missing bash beside a daemon read as green."
        );
}
