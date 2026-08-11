// Chaos — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `Chaos`, nightly; the assertions are § The chaos invariants.

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Serilog;

partial class Build
{
    /// <summary>
    ///     One of the seven in docs/plan/23 § The chaos invariants.
    /// </summary>
    /// <param name="Number">Its position in that list, which is how the suite names its test.</param>
    /// <param name="Fault">What is injected.</param>
    /// <param name="Invariant">What must hold afterwards. ⚠ An assertion, never an observation.</param>
    /// <param name="Needs">
    ///     What the deployment must have for this one to mean anything — the reason the seven cannot
    ///     be run against a laptop, stated per invariant rather than as one hand-wave.
    /// </param>
    sealed record ChaosInvariant(int Number, string Fault, string Invariant, string Needs);

    /// <summary>
    ///     The roster, in docs/plan/23 § The chaos invariants' order.
    ///     <para>
    ///         ⚠ <b>This is a roster, not a second copy of the doc, and the distinction is what
    ///         changed.</b> An earlier version of this file argued against mirroring the list here
    ///         because the assertions live in <c>test/CyberCloud.Chaos</c> and a second copy drifts.
    ///         That argument holds for the <i>assertions</i> and does not hold for the <i>census</i>:
    ///         without a roster, a suite that quietly stops covering invariant 5 is a green nightly
    ///         run, and the only thing that would catch it is somebody re-reading doc 23 next to the
    ///         test file. <see cref="AssertEveryInvariantIsCovered" /> is the check this table exists
    ///         for; the <c>Invariant</c> text is a label on a row, and the suite remains the only
    ///         place any of it is asserted.
    ///     </para>
    /// </summary>
    static readonly ChaosInvariant[] ChaosInvariants =
    [
        new(1,
            "kill a random silo every 90 s during a provisioning storm",
            "zero resources stuck in a transitional state after settling; every operation reaches Succeeded or Failed",
            "enough silos that killing one leaves a cluster, and a provisioning load to be storming"),
        new(2,
            "FLUSHALL the hot tier",
            "zero durable state lost, zero acknowledged control-plane writes lost, full function within 60 s",
            "a real Redis hot tier and a real durable tier — docs/plan/05 § The two tiers"),
        new(3,
            "fail over a durable shard",
            "writes for that shard's tenants pause and resume; no data loss; other tenants unaffected",
            "at least two durable shards with tenants on each, or 'other tenants unaffected' is vacuous"),
        new(4,
            "blackhole a managed cluster",
            "its resources go Degraded, reconciles suspend, no operations fail, clean resumption on restore",
            "a managed cluster distinct from the platform's own — docs/plan/09 § The platform's own cluster, phase 1"),
        new(5,
            "blackhole the global directory cluster for 10 minutes",
            "zero tenant-facing errors; new tenant creation fails cleanly with a retryable error",
            "a global directory cluster separate from the regional one"),
        new(6,
            "partition the NATS cluster",
            "streams recover, consumers resume from their cursor, no duplicate billing after dedup",
            "a multi-node NATS cluster and billing consumers with something to dedup"),
        new(7,
            "rolling upgrade of a 30-silo cluster under load",
            "zero failed tenant requests",
            "30 silos, two deployable versions, and load — the largest environment of the seven"),
    ];

    /// <summary>
    ///     ⚠ As with <c>E2E</c>, the deployment is an input rather than a dependency — see the note
    ///     beside the target graph in <c>Build.cs</c>.
    /// </summary>
    void RunChaosTests()
    {
        Log.Information(
            "Chaos: {Count} invariant(s) — docs/plan/23 § The chaos invariants",
            ChaosInvariants.Length);

        foreach (var invariant in ChaosInvariants)
            Log.Information("  {Number}. {Fault} → {Invariant}", invariant.Number, invariant.Fault, invariant.Invariant);

        var suites = ProjectsIn(TestSuite.Chaos);
        var preconditions = new TargetPreconditions(nameof(Chaos));

        preconditions.Require(
            suites.Count > 0,
            "there is no chaos suite — no project under test/ is named CyberCloud.Chaos",
            "create test/CyberCloud.Chaos (docs/plan/03 § test/) with one test per invariant above, "
            + "named so AssertEveryInvariantIsCovered can find it. Build.Test.cs § SuiteOwning "
            + "already routes that name here");

        preconditions.Require(
            !string.IsNullOrWhiteSpace(KubeContext),
            "no cluster is configured to break",
            "pass --kube-context <context> for a deployment that can lose a silo, a shard and a "
            + "NATS node without anybody minding. docs/plan/23 § Environments and rollout puts the "
            + "nightly suites on staging");

        // ⚠ Reported even when the two above are unmet, and reported as its own line rather than
        // folded into "no cluster". Invariant 7 needs 30 silos (docs/plan/00 § The quality bar says
        // so too, as a 1.0 quality number), and a staging environment sized for the other six will
        // pass this target while never having run the one invariant the rollout design depends on.
        preconditions.Require(
            false,
            "the environment's size is unverified — invariant 7 needs 30 silos and nothing here can "
            + "count them",
            "give the suite a way to report the cluster it found (silo count, shard count, NATS "
            + "nodes) and fail the invariants whose Needs are unmet, rather than passing them "
            + "against a cluster too small to violate them. Until then this target cannot claim the "
            + "seven ran meaningfully even where it can run them");

        preconditions.AssertSatisfied(
            "docs/plan/23 § The chaos invariants: \"Each is an assertion, not an observation.\" "
            + "An assertion needs something to assert against, and all three inputs above are it.");

        AssertEveryInvariantIsCovered(suites);

        RunSuites(
            nameof(Chaos),
            suites,
            new Dictionary<string, string> { ["CYBERCLOUD_CHAOS_CONTEXT"] = KubeContext! });
    }

    /// <summary>
    ///     Every invariant in <see cref="ChaosInvariants" /> has a test in the suite.
    /// </summary>
    /// <remarks>
    ///     ⚠ Uses the suite's own filter rather than reflection: the suite is a
    ///     Microsoft.Testing.Platform host, so <c>--filter-method</c> with
    ///     <c>--minimum-expected-tests 1</c> answers "is there a test for invariant 5" without this
    ///     build loading the suite's assembly or knowing its test framework. The naming convention —
    ///     a method whose name contains <c>Invariant{n}</c> — is the whole contract.
    /// </remarks>
    void AssertEveryInvariantIsCovered(IReadOnlyCollection<Nuke.Common.IO.AbsolutePath> suites)
    {
        var uncovered = ChaosInvariants
            .Where(invariant => !suites.Any(suite => SuiteListsTest(suite, $"*Invariant{invariant.Number}*")))
            .ToList();

        Assert.Empty(
            uncovered.Select(x => $"invariant {x.Number} ({x.Fault})").ToList(),
            $"{uncovered.Count} of {ChaosInvariants.Length} chaos invariant(s) have no test named "
            + "*Invariant<n>* in the suite. docs/plan/23 § The chaos invariants is the list, and a "
            + "nightly run that is green because an invariant stopped being tested is the failure "
            + "mode this check exists for.");
    }
}
