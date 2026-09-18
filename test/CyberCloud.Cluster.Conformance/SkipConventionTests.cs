using CyberCloud.Cluster.Conformance.Infrastructure;
using System.Text.RegularExpressions;

namespace CyberCloud.Cluster.Conformance;

/// <summary>
///     That the word <c>build/Build.Test.cs</c> reads out of a cluster-backed skip is the word this
///     assembly writes into one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two spellings of one token across a boundary neither compiler checks.</b>
///         <c>Build.Test.cs</c> § <c>ReportClusterBackedCases</c> fails a <c>Test</c> run in which a
///         suite holding a cluster skipped for a named missing prerequisite while a Docker endpoint
///         was present, and it recognises such a skip by <c>PrerequisiteMarker</c> in the reason.
///         <see cref="ClusterInfrastructure.SkipMessage" /> is where the reason is written. If either
///         side is edited alone, nothing goes red: the build simply stops seeing the skips, and the
///         gate that exists to catch a lane that never ran goes back to not catching it. Same defect
///         class, same defence, as <c>CyberCloud.AppHost.Tests</c> § <c>ClusterBackedGatingTests</c>,
///         which pins the build's globs against the assembly they classify.
///     </para>
///     <para>
///         ⚠ <b>Daemon-free on purpose</b>, like <c>TheCaseOwnsClusterObjectsOrThisWholeSuiteWouldBeVacuous</c>:
///         this reads a string and a source file, so it runs — and can fail — on a machine with no
///         Docker, which is the machine on which the guard it protects would otherwise be the only
///         thing noticing.
///     </para>
/// </remarks>
public sealed class SkipConventionTests {
    /// <summary>The build's spelling, read out of its source rather than assumed.</summary>
    static readonly Regex Marker = new("const string PrerequisiteMarker = \"([^\"]+)\";", RegexOptions.Compiled);

    [Fact]
    public void TheBuildReadsTheWordThisAssemblyWritesIntoAPrerequisiteSkip() {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "build", "Build.Test.cs"));
        var match = Marker.Match(source);

        match.Success.ShouldBeTrue(
            "build/Build.Test.cs no longer declares `const string PrerequisiteMarker = \"…\";`, so this "
            + "test cannot say which word the build reads out of a skip. Rename the constant here and "
            + "there together."
        );

        var marker = match.Groups[1].Value;

        ClusterInfrastructure.SkipMessage("CyberCloud.Sample/widgets", "nothing").ShouldContain(
            marker,
            Case.Sensitive,
            $"ClusterInfrastructure.SkipMessage no longer carries \"{marker}\", the word build/Build.Test.cs "
            + "§ PrerequisiteSkips reads to tell a lane that did not run from one that ran and had "
            + "nothing to say. The guard is blind to this suite's skips until the two agree again."
        );
    }

    static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No CyberCloud.slnx above " + AppContext.BaseDirectory + ".");
    }
}
