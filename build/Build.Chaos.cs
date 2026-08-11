// Chaos — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `Chaos`, nightly; the assertions are § The chaos invariants.

partial class Build
{
    /// <summary>
    ///     ⚠ As with <c>E2E</c>, the deployment is an input rather than a dependency — see the note
    ///     beside the target graph in <c>Build.cs</c>. <c>test/CyberCloud.Chaos</c> is already
    ///     discovered and assigned to this target by <c>Build.Test.cs</c> § <c>SuiteOwning</c>;
    ///     <c>ProjectsIn(TestSuite.Chaos)</c> is the list to run.
    ///     <para>
    ///         Unlike <c>Build.Architecture.cs</c>, this file does not mirror its doc's list into a
    ///         table. The ten architecture gates are enforced from inside this build and nothing
    ///         else names them; the seven chaos invariants are assertions that will live in
    ///         <c>test/CyberCloud.Chaos</c>, and copying them here would create a second copy to
    ///         keep in step with docs/plan/23 that the real one does not read.
    ///     </para>
    /// </summary>
    void RunChaosTests()
        => NotImplementedYet(
            nameof(Chaos),
            "inject the seven faults in docs/plan/23 § The chaos invariants against a real "
            + "deployment — silo kills, a hot-tier FLUSHALL, shard failover, cluster and "
            + "global-directory blackholes, a NATS partition, a rolling upgrade under load — and "
            + "assert the matching invariant after each, failing on the first that does not hold",
            "docs/plan/23 § Build (row `E2E` `Chaos` `Load`) and § The chaos invariants. Depends on "
            + "test/CyberCloud.Chaos existing (docs/plan/03 § test/) and on a deployment big enough "
            + "to be worth breaking — the rolling-upgrade invariant alone needs 30 silos.");
}
