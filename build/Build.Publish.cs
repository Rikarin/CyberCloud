// Publish — docs/plan/23 § Build, row `Publish`: "NuGet, npm, charts, CLI binaries per RID".
// docs/plan/23 § CI shape, row `release.yml`: "Tag | Full gate, publish everything, staged rollout".

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;

partial class Build
{
    /// <summary>
    ///     The version stamped on everything this target pushes.
    /// </summary>
    /// <remarks>
    ///     ⚠ No fallback to a timestamp, a build number or <c>0.0.0</c>. docs/plan/23 § CI shape
    ///     triggers `release.yml` on a tag, so a run of this target that cannot say which version it
    ///     is publishing is a run that should not be publishing: the artefacts would be real,
    ///     immutable on their feeds, and named after nothing.
    /// </remarks>
    [Parameter("Version to publish, e.g. 1.2.0. Required — defaults to the tag on HEAD when there is exactly one.")]
    readonly string? Version;

    [Parameter("NuGet feed to push packages to.")]
    readonly string? NuGetFeed;

    [Parameter("NuGet API key.")]
    [Secret]
    readonly string? NuGetApiKey;

    [Parameter("OCI registry for packaged Helm charts, e.g. oci://registry.example.com/cybercloud/charts.")]
    readonly string? ChartRegistry;

    /// <summary>
    ///     The RIDs <c>cyc</c> is published for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This list is a decision this build makes and docs/plan does not.</b> docs/plan/21
    ///     § The CLI says "single-file AOT-published per RID so there is no runtime prerequisite" and
    ///     never enumerates the RIDs; docs/plan/23 § Build says "CLI binaries per RID" and does the
    ///     same. Six is the set that follows from the rest of the plan — Linux and macOS on both
    ///     architectures because that is what operators and developers run, Windows x64 because the
    ///     portal's users are not all on Unix, and <c>win-arm64</c> because omitting it is the kind
    ///     of gap that is noticed by exactly the person who cannot work around it.
    ///     <para>
    ///         ⚠ AOT publishing cannot cross-compile: each of these has to be produced on a machine
    ///         of that OS. A single-runner release job can only ever produce the subset it can build,
    ///         which is why <see cref="PublishCli" /> fails rather than skipping the ones it cannot.
    ///     </para>
    /// </remarks>
    static readonly string[] CliRuntimeIdentifiers =
        ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];

    AbsolutePath PackageDirectory => ArtifactsDirectory / "packages";
    AbsolutePath CliBinaryDirectory => ArtifactsDirectory / "cyc";

    /// <summary>
    ///     <c>Publish</c> is the widest node in the graph on purpose: docs/plan/23 § CI shape gives
    ///     <c>release.yml</c> exactly one job — "full gate, publish everything" — so
    ///     <c>./build.sh Publish</c> has to <em>be</em> that gate rather than trust a workflow file
    ///     to have run the gate first.
    ///     <para>
    ///         ⚠ <c>E2E</c>, <c>Chaos</c> and <c>Load</c> are <em>not</em> dependencies, though
    ///         docs/plan/23 § Test layers does gate a release on them. They run against a real
    ///         deployment of the candidate, so the order is gate → deploy the candidate → suites →
    ///         publish, and this edge would invert it.
    ///     </para>
    ///     <para>
    ///         ⚠ There is no edge to <c>Images</c> for the images themselves either. <c>Images</c>
    ///         already pushes by digest, so by the time <c>Publish</c> runs the images are published;
    ///         it appears here only underneath <c>Licence</c>, as something scanned.
    ///     </para>
    /// </summary>
    void PublishArtefacts()
    {
        var packages = PackableProjects;
        var charts = ChartPackageDirectory.DirectoryExists()
            ? ChartPackageDirectory.GlobFiles("*.tgz").ToList()
            : [];

        var npm = PublishableNpmPackages;
        var version = Version ?? TagOnHead;

        Log.Information(
            "Publish: {Packages} NuGet package(s), {Npm} npm package(s), {Charts} chart(s), "
            + "{Rids} `cyc` RID(s)",
            packages.Count,
            npm.Count,
            charts.Count,
            CliRuntimeIdentifiers.Length);

        var preconditions = new TargetPreconditions(nameof(Publish));

        preconditions.Require(
            !string.IsNullOrWhiteSpace(version),
            "there is no version to publish — --version was not passed and HEAD carries no tag",
            "pass --version 1.2.0, or run on a tagged commit. docs/plan/23 § CI shape triggers "
            + "release.yml on a tag, so the tag is normally the answer");

        preconditions.Require(
            !string.IsNullOrWhiteSpace(NuGetFeed) && !string.IsNullOrWhiteSpace(NuGetApiKey),
            "no NuGet feed or API key is configured",
            "pass --nuget-feed and --nuget-api-key. ⚠ Neither has a default on purpose: a default "
            + "feed is how a pre-release build ends up on nuget.org");

        preconditions.Require(
            !string.IsNullOrWhiteSpace(ChartRegistry),
            "no chart registry is configured",
            "pass --chart-registry oci://…. docs/plan/23 § Build, row Publish lists charts among the "
            + "four things a release pushes");

        preconditions.Require(
            CycProject is not null,
            "there is no `cyc` project under cli/, so there are no CLI binaries to publish",
            "build the CLI under cli/ — docs/plan/03 § cli/, docs/plan/21 § The CLI. Until it "
            + "exists, one of doc 23's four Publish outputs does not exist to be published");

        preconditions.AssertSatisfied(
            "docs/plan/23 § Build, row Publish: \"NuGet, npm, charts, CLI binaries per RID\", behind "
            + "docs/plan/23 § CI shape's single release.yml job.");

        // ⚠ Reported, not failed on, and the difference is which of the four is missing. A release
        // with no npm package is a real release of the other three — docs/plan/21 § Other SDKs says
        // the TypeScript client is not written, and every package.json under portal/ is `private`.
        // A release with no CLI is not, which is why that one is a precondition above.
        if (npm.Count == 0)
        {
            Log.Warning(
                "Publish: 0 npm package(s). Every package.json under portal/ is marked private, and "
                + "docs/plan/21 § Other SDKs has the generated TypeScript client unwritten — so the "
                + "npm column of docs/plan/23 § Build, row Publish has nothing behind it yet. ○, not ✔.");
        }

        PublishPackages(packages, version!);
        PublishCharts(charts);
        PublishCli(version!);
    }

    /// <summary>Projects that opted in with <c>IsPackable</c>. Directory.Build.props defaults it off.</summary>
    IReadOnlyList<AbsolutePath> PackableProjects =>
        Solution.AllProjects
            .Select(x => (AbsolutePath)x.Path)
            .Where(project => project.ReadAllText().Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal))
            .OrderBy(x => x.NameWithoutExtension, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Every <c>package.json</c> under <c>portal/</c> that is not <c>private</c>.
    /// </summary>
    IReadOnlyList<AbsolutePath> PublishableNpmPackages
    {
        get
        {
            var portal = RootDirectory / "portal";

            if (!portal.DirectoryExists())
                return [];

            return portal
                .GlobFiles("apps/**/package.json", "libs/**/package.json")
                .Where(x => !x.ToString().Contains("node_modules", StringComparison.Ordinal))
                .Where(x => !x.ReadAllText().Contains("\"private\": true", StringComparison.Ordinal))
                .OrderBy(x => x.ToString(), StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>The tag on HEAD, when there is exactly one.</summary>
    /// <remarks>
    ///     ⚠ Exactly one. Two tags on the same commit means two candidate versions and no way to pick,
    ///     and picking the first alphabetically would publish <c>1.10.0</c> as <c>1.2.0</c>.
    /// </remarks>
    string? TagOnHead
    {
        get
        {
            var tags = GitTasks
                .Git("tag --points-at HEAD", RootDirectory, logOutput: false, logInvocation: false)
                .Select(x => x.Text.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            return tags.Count == 1 ? tags[0].TrimStart('v') : null;
        }
    }

    /// <summary>
    ///     Packs and pushes the NuGet packages.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>--skip-duplicate</c> is the whole of this target's idempotence story for NuGet, and it
    ///     is load-bearing: a release that failed after pushing two of five packages is re-run, and
    ///     without it the re-run fails on the first already-pushed package with a 409 — leaving the
    ///     release permanently half-published, because the only remedy a feed offers is unlisting.
    /// </remarks>
    void PublishPackages(IReadOnlyList<AbsolutePath> projects, string version)
    {
        if (projects.Count == 0)
        {
            Log.Warning(
                "Publish: 0 NuGet package(s). Nothing in {Solution} sets <IsPackable>true</IsPackable>. "
                + "○, not ✔.",
                SolutionFile.Name);

            return;
        }

        PackageDirectory.CreateOrCleanDirectory();

        foreach (var project in projects)
        {
            DotNetTasks.DotNetPack(s => s
                .SetProject(project)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .EnableNoBuild()
                .SetVersion(version)
                .SetOutputDirectory(PackageDirectory));
        }

        foreach (var package in PackageDirectory.GlobFiles("*.nupkg"))
        {
            DotNetTasks.DotNetNuGetPush(s => s
                .SetTargetPath(package)
                .SetSource(NuGetFeed)
                .SetApiKey(NuGetApiKey)
                .EnableSkipDuplicate());
        }

        Log.Information("Publish: {Count} NuGet package(s) at {Version} → {Feed}", projects.Count, version, NuGetFeed);
    }

    /// <summary>
    ///     Pushes the chart packages <c>Charts</c> produced.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reads <see cref="ChartPackageDirectory" /> rather than running <c>helm package</c> again.
    ///     <c>Publish</c> depends on <c>Licence</c>, which depends on <c>Charts</c>, so the tarballs
    ///     in that directory are the ones the licence scan actually looked at — re-packaging here
    ///     would publish bytes nothing in the gate ever saw.
    /// </remarks>
    // List rather than IReadOnlyList: CA1859 is an error here and this is a private helper.
    void PublishCharts(List<AbsolutePath> charts)
    {
        if (charts.Count == 0)
        {
            Log.Warning(
                "Publish: 0 chart package(s) in {Directory}. `Charts` runs before this target and "
                + "packages every chart it finds, so an empty directory means it found none. ○, not ✔.",
                RootDirectory.GetRelativePathTo(ChartPackageDirectory));

            return;
        }

        var helm = ResolveHelm();

        foreach (var chart in charts)
            helm($"push {chart} {ChartRegistry}", workingDirectory: RootDirectory);

        Log.Information("Publish: {Count} chart(s) → {Registry}", charts.Count, ChartRegistry);
    }

    /// <summary>
    ///     A single-file AOT <c>cyc</c> per RID — docs/plan/21 § The CLI: "single-file AOT-published
    ///     per RID so there is no runtime prerequisite".
    /// </summary>
    /// <remarks>
    ///     ⚠ Fails on the first RID it cannot build rather than publishing the ones it can. AOT does
    ///     not cross-compile, so a Linux runner asked for all six will produce four and fail two —
    ///     and a release that quietly shipped four of six RIDs is one where the missing platforms are
    ///     discovered by their users. The fix is a matrix job per OS in `release.yml`, each passing
    ///     the RIDs it can actually produce.
    /// </remarks>
    void PublishCli(string version)
    {
        CliBinaryDirectory.CreateOrCleanDirectory();

        foreach (var rid in CliRuntimeIdentifiers)
        {
            DotNetTasks.DotNetPublish(s => s
                .SetProject(CycProject)
                .SetConfiguration(Configuration)
                .SetRuntime(rid)
                .SetSelfContained(true)
                .SetVersion(version)
                .SetPublishSingleFile(true)
                .SetOutput(CliBinaryDirectory / rid));
        }

        Log.Information(
            "Publish: `cyc` {Version} for {Count} RID(s) → {Directory}",
            version,
            CliRuntimeIdentifiers.Length,
            RootDirectory.GetRelativePathTo(CliBinaryDirectory));
    }
}
