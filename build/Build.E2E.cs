// E2E — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `E2E`: "Playwright + `cyc` against a real staging deployment",
// nightly and pre-release.

partial class Build
{
    /// <summary>
    ///     ⚠ The deployment these three targets run against is an <em>input</em>, not a dependency.
    ///     See the note beside the target graph in <c>Build.cs</c> for why there is no
    ///     <c>DependsOn(Deploy)</c> edge and no <c>Deploy</c> target to point one at.
    ///     <para>
    ///         The project this target will run, <c>test/CyberCloud.E2E</c>, is already discovered:
    ///         <c>Build.Test.cs</c> § <c>SuiteOwning</c> assigns it to <see cref="TestSuite.EndToEnd" />,
    ///         and <c>ProjectsIn(TestSuite.EndToEnd)</c> is the list to run. Implementing this
    ///         target is a matter of what to do with that list, not of finding it.
    ///     </para>
    /// </summary>
    void RunE2ETests()
        => NotImplementedYet(
            nameof(E2E),
            "drive the public REST API and the Playwright journeys against a real staging "
            + "deployment through the `cyc` CLI, and fail the release if any journey is red",
            "docs/plan/23 § Build (row `E2E` `Chaos` `Load`) and § Test layers (row `E2E`). Depends "
            + "on test/CyberCloud.E2E existing (docs/plan/03 § test/) and on there being a staging "
            + "environment to point it at — docs/plan/23 § Environments and rollout.");
}
