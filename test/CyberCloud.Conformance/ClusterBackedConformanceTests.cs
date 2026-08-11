namespace CyberCloud.Conformance;

/// <summary>
///     The half of the conformance suite that needs real infrastructure, present by name and
///     <b>skipped loudly</b>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why these are not quietly absent.</b> docs/plan/03 § Providers lists five things every
///         provider must pass and docs/plan/24 § Phase 1 turns three of them into exit criteria. Two
///         cannot be honestly claimed against <c>Orleans.TestingHost</c> with in-memory storage and a
///         dictionary for an API server. The dishonest options are both worse than skipping: deleting
///         the test leaves a suite that is green because it asked less, and re-pointing the test at
///         the in-memory harness leaves a suite that is green because it asserted something weaker
///         under the same name. Either way somebody later reads "conformance: green" and believes
///         criterion 3 is met.
///     </para>
///     <para>
///         So each one runs, names what it needs, names what it would prove, and calls
///         <c>Assert.Skip</c>. A skip is visible in the runner's output and in CI; an absence is not.
///         <c>--minimum-expected-tests</c> exists for the same reason one level up.
///     </para>
///     <para>
///         ⚠ <b>What turning these on costs.</b> <c>Testcontainers.K3s</c> and
///         <c>Testcontainers.PostgreSql</c> are pinned in <c>Directory.Packages.props</c> and are
///         <i>not</i> referenced by <c>CyberCloud.Conformance.csproj</c>, deliberately — a package
///         reference would make the whole suite refuse to run without a Docker daemon, which is the
///         opposite of the split this class exists to make. Enabling them means a second project
///         (<c>CyberCloud.Conformance.Cluster</c>, or these tests moved into
///         <c>CyberCloud.E2E</c>, which docs/plan/23 § Test layers already runs nightly against a real
///         deployment) rather than a package added here.
///     </para>
/// </remarks>
/// <param name="case">The provider under test — named in every skip message so the output is useful.</param>
public abstract class ClusterBackedConformanceTests(ProviderConformanceCase @case) {
    /// <summary>The provider under test.</summary>
    protected ProviderConformanceCase Case { get; } = @case;

    /// <summary>What every skip message ends with.</summary>
    const string HowToRun =
        "To run it: add Testcontainers.K3s and Testcontainers.PostgreSql to a cluster-backed "
        + "conformance project (NOT to CyberCloud.Conformance — see this class's remarks), start a "
        + "Docker daemon, and re-run. docs/plan/23 § Test layers already has a nightly deployment-backed "
        + "lane these belong in.";

    [Fact]
    [Trait("Requires", "cluster")]
    public void TheLifecycleRunsAgainstARealApiServer() {
        Assert.Skip(
            $"SKIPPED — {Case.DisplayName}'s lifecycle was exercised against an in-memory API server, "
            + "not a real one. NEEDS: a k3s container. WOULD PROVE: that the rendered manifest is one "
            + "the API server accepts, that server-side apply under our field manager behaves as "
            + "ADR-013 assumes, that the seven labels survive admission, and that the plural in the "
            + "GroupVersionKind addresses a real REST path. The in-memory harness cannot fail any of "
            + "those, because it is a dictionary. "
            + HowToRun
        );
    }

    [Fact]
    [Trait("Requires", "cluster")]
    public void KillingTheSiloMidCreateStillConverges() {
        // ⚠ THIS IS docs/plan/24 § Phase 1's EXIT CRITERION 3, AND IT IS NOT MET.
        Assert.Skip(
            $"SKIPPED — {Case.DisplayName} has not been shown to survive a silo kill, so "
            + "docs/plan/24 § Phase 1's exit criterion 3 is NOT met and must not be claimed. NEEDS: a "
            + "multi-silo cluster and real durable storage (PostgreSQL) plus real reminder storage "
            + "(Redis). WOULD PROVE: docs/plan/08 § Long-running operations' claim that OperationGrain "
            + "state 'includes everything needed to re-drive' and that 'on activation after a silo "
            + "loss the grain re-registers its reminder and continues'. An in-process TestCluster over "
            + "in-memory storage cannot tell that apart from a grain deactivating and reactivating "
            + "over the same dictionary — the state never left the process, so nothing was recovered. "
            + HowToRun
        );
    }

    [Fact]
    [Trait("Requires", "cluster")]
    public void DriftIsCorrectedAfterARealKubectlDelete() {
        Assert.Skip(
            $"SKIPPED — {Case.DisplayName}'s drift correction was exercised by emptying a dictionary "
            + "behind the reconciler, which proves the reconciler reads the world back but not that "
            + "the platform NOTICES on its own. NEEDS: a k3s container and the per-cluster informer of "
            + "docs/plan/09 § Observing, which is not built — IClusterObjectInventory's shipped "
            + "implementation fails rather than reporting an empty cluster, on purpose. WOULD PROVE: "
            + "the hourly per-cluster scan of docs/plan/08 § The reconcile loop, including the orphan "
            + "and stray cases, which are the two things nothing else would find. "
            + HowToRun
        );
    }

    [Fact]
    [Trait("Requires", "storage")]
    public void DesiredStateSurvivesARealSerializationRoundTrip() {
        Assert.Skip(
            $"SKIPPED — {Case.DisplayName}'s grain state was never serialized. NEEDS: PostgreSQL "
            + "through Microsoft.Orleans.Persistence.AdoNet, which is the durable tier ADR-003 "
            + "specifies. WOULD PROVE: that ResourceState and OperationGrainState round-trip — "
            + "in-memory storage keeps the object graph, so the 'System.Text.Json does not populate a "
            + "get-only collection' trap cannot fire here. CyberCloud.ResourceManager.Tests' .csproj "
            + "records the same debt against the same two types. "
            + HowToRun
        );
    }

    [Fact]
    [Trait("Requires", "cluster")]
    public void AFieldConflictWithAnotherManagerBecomesADriftEventWithAName() {
        Assert.Skip(
            $"SKIPPED — {Case.DisplayName}'s conflict path was exercised by a switch on the fake "
            + "cluster, so the DriftEvent it produced was one the harness wrote. NEEDS: a k3s "
            + "container and a second field manager editing a field we own. WOULD PROVE: ADR-013's "
            + "'if a tenant hand-edits a field we own, the next apply reports a conflict rather than "
            + "silently reverting, and THAT becomes a drift event with a name' — specifically that the "
            + "API server's 409 body parses into the field paths and owner names ConflictParser "
            + "expects. "
            + HowToRun
        );
    }
}
