// Load — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `Load`: the docs/plan/00 quality bar at scale, weekly and
// pre-release; the numbers are § The load scenarios.

partial class Build
{
    /// <summary>
    ///     ⚠ The deployment is an input rather than a dependency — see the note beside the target
    ///     graph in <c>Build.cs</c>. <c>test/CyberCloud.Load</c> is already discovered and assigned
    ///     to this target by <c>Build.Test.cs</c> § <c>SuiteOwning</c>;
    ///     <c>ProjectsIn(TestSuite.Load)</c> is the list to run.
    ///     <para>
    ///         ⚠ Whoever implements this owns a requirement no other target has: docs/plan/23 § The
    ///         load scenarios makes <em>the trend</em> the gate — "a 20 % regression between
    ///         releases is a release blocker even if the absolute number still passes". That means
    ///         results have to be published somewhere durable and compared against the previous
    ///         release, so a pass/fail against the six absolute budgets is only half the target.
    ///     </para>
    /// </summary>
    void RunLoadTests()
        => NotImplementedYet(
            nameof(Load),
            "run the six scenarios in docs/plan/23 § The load scenarios against a real deployment — "
            + "10 000 tenants at 5 000 rps, 500 writes/s, the ReBAC check rate, 2 000 000 resident "
            + "grains, 1 000 terminal sessions, 500 000 spans/s — failing on a missed budget and on "
            + "a 20 % regression against the previous release even where the budget still passes",
            "docs/plan/23 § Build (row `E2E` `Chaos` `Load`) and § The load scenarios. Depends on "
            + "test/CyberCloud.Load existing (docs/plan/03 § test/), on an environment that can "
            + "actually be driven to those numbers, and on somewhere to keep results between "
            + "releases for the trend comparison.");
}
