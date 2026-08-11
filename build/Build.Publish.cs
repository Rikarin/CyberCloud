// Publish — docs/plan/23 § Build, row `Publish`: "NuGet, npm, charts, CLI binaries per RID".
// docs/plan/23 § CI shape, row `release.yml`: "Tag | Full gate, publish everything, staged rollout".

partial class Build
{
    /// <summary>
    ///     <c>Publish</c> is the widest node in the graph on purpose: docs/plan/23 § CI shape gives
    ///     <c>release.yml</c> exactly one job — "full gate, publish everything" — so
    ///     <c>./build.sh Publish</c> has to <em>be</em> that gate rather than trust a workflow file
    ///     to have run the gate first. A CI step that is a shell script in a workflow file is a
    ///     step nobody can reproduce or bisect. Hence <c>DependsOn(Test, Generate, Architecture,
    ///     Portal, Licence)</c>, which pulls <c>Compile</c>, <c>Charts</c> and <c>Images</c>
    ///     transitively.
    ///     <para>
    ///         ⚠ <c>E2E</c>, <c>Chaos</c> and <c>Load</c> are <em>not</em> dependencies, though
    ///         docs/plan/23 § Test layers does gate a release on them ("Green before release",
    ///         "Budgets met", both pre-release). They cannot be: all three run against a real
    ///         deployment of the candidate, so the order is gate → deploy the candidate → suites →
    ///         publish. The deploy in the middle is not a Nuke target — see the note beside the
    ///         target graph in <c>Build.cs</c> — so that sequencing lives in <c>release.yml</c>,
    ///         and this edge would invert it.
    ///     </para>
    ///     <para>
    ///         ⚠ There is no edge to <c>Images</c> for the images themselves either. <c>Images</c>
    ///         already pushes by digest, so by the time <c>Publish</c> runs the images are
    ///         published; it appears here only underneath <c>Licence</c>, as something scanned.
    ///         The four things this target actually pushes are the ones doc 23 lists.
    ///     </para>
    /// </summary>
    void PublishArtefacts()
        => NotImplementedYet(
            nameof(Publish),
            "push the NuGet packages, the npm packages for the generated TypeScript client and the "
            + "xUI-facing libs, the packaged Helm charts, and a `cyc` binary per published RID — "
            + "each signed, each idempotent so a re-run of a partly-failed release is safe",
            "docs/plan/23 § Build (row `Publish`) and § CI shape (row `release.yml`). Depends on "
            + "there being something to publish, and on decisions not yet made anywhere in "
            + "docs/plan: which feeds, which RID set, and how a version is stamped.");
}
