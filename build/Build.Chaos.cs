// Chaos — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `Chaos`, nightly; the assertions are § The chaos invariants.
//
// ── ⚠ THE DEPLOYMENT IS ONE THE SUITE STARTS, AND THAT IS WHAT MADE THIS TARGET RUNNABLE ─────────
//
// Until issue #44 this target was blocked on three inputs — a suite, a kube context, and a way to
// count silos — and "a real deployment" meant staging. test/CyberCloud.Chaos now starts its own: a
// Redis hot tier, three PostgreSQL shards and a k3s in Docker, and a three-silo Orleans cluster
// wired through the same extension methods CyberCloud.Silo.Host composes. That is a real Redis to
// FLUSHALL, a real shard to stop, a real API server to take away, and real silos to kill; what it
// is not is thirty silos or a NATS cluster, and the rows that need those say so as ○ rather than ✔.
// Docker on a cgroup v2 host is the one input a checkout does not contain — docs/plan/23 § The
// lane that needs a kubelet — and it is the one precondition left.

using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

partial class Build {
    /// <summary>
    ///     One of the seven in docs/plan/23 § The chaos invariants.
    /// </summary>
    /// <param name="Number">Its position in that list, which is how the suite names its test.</param>
    /// <param name="Fault">What is injected.</param>
    /// <param name="Invariant">What must hold afterwards. ⚠ An assertion, never an observation.</param>
    /// <param name="Needs">
    ///     What the deployment must have for this one to mean anything — the reason a row can be ○
    ///     on this machine, stated per invariant rather than as one hand-wave.
    /// </param>
    sealed record ChaosInvariant(int Number, string Fault, string Invariant, string Needs);

    /// <summary>
    ///     The roster, in docs/plan/23 § The chaos invariants' order.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This is a roster, not a second copy of the doc, and the distinction is what
    ///             changed.
    ///         </b> An earlier version of this file argued against mirroring the list here
    ///         because the assertions live in <c>test/CyberCloud.Chaos</c> and a second copy drifts.
    ///         That argument holds for the <i>assertions</i> and does not hold for the <i>census</i>:
    ///         without a roster, a suite that quietly stops covering invariant 5 is a green nightly
    ///         run, and the only thing that would catch it is somebody re-reading doc 23 next to the
    ///         test file. <see cref="AssertEveryInvariantIsCovered" /> is the check this table exists
    ///         for; the <c>Invariant</c> text is a label on a row, and the suite remains the only
    ///         place any of it is asserted.
    ///     </para>
    /// </summary>
    static readonly ChaosInvariant[] ChaosInvariants = [
        new(
            1,
            "kill a random silo every 90 s during a provisioning storm",
            "zero resources stuck in a transitional state after settling; every operation reaches Succeeded or Failed",
            "enough silos that killing one leaves a cluster, and a provisioning load to be storming"
        ),
        new(
            2,
            "FLUSHALL the hot tier",
            "zero durable state lost, zero acknowledged control-plane writes lost, full function within 60 s",
            "a real Redis hot tier and a real durable tier — docs/plan/05 § The two tiers"
        ),
        new(
            3,
            "fail over a durable shard",
            "writes for that shard's tenants pause and resume; no data loss; other tenants unaffected",
            "at least two durable shards with tenants on each, or 'other tenants unaffected' is vacuous"
        ),
        new(
            4,
            "blackhole a managed cluster",
            "its resources go Degraded, reconciles suspend, no operations fail, clean resumption on restore",
            "a managed cluster distinct from the platform's own — docs/plan/09 § The platform's own cluster, phase 1"
        ),
        new(
            5,
            "blackhole the global directory cluster for 10 minutes",
            "zero tenant-facing errors; new tenant creation fails cleanly with a retryable error",
            "a global directory store separate from the tenant shards"
        ),
        new(
            6,
            "partition the NATS cluster",
            "streams recover, consumers resume from their cursor, no duplicate billing after dedup",
            "a multi-node NATS cluster and billing consumers with something to dedup"
        ),
        new(
            7,
            "rolling upgrade of a 30-silo cluster under load",
            "zero failed tenant requests",
            "30 silos, two deployable versions, and load — the largest environment of the seven"
        )
    ];

    /// <summary>What one invariant's row in the suite's results file says.</summary>
    /// <param name="Status"><c>Held</c>, <c>Violated</c> or <c>Vacuous</c> — the suite's <c>InvariantStatus</c>.</param>
    /// <param name="Detail">The suite's sentence, numbers included.</param>
    sealed record ChaosOutcome(string Status, string Detail);

    /// <summary>Where the suite writes its outcomes, and where this target reads them.</summary>
    AbsolutePath ChaosResultsFile => ArtifactsDirectory / "chaos" / "results.json";

    /// <summary>
    ///     ⚠ As with <c>E2E</c>, the deployment is an input rather than a dependency — see the note
    ///     beside the target graph in <c>Build.cs</c>. Here the input is a Docker daemon: the suite
    ///     starts what it breaks.
    /// </summary>
    void RunChaosTests() {
        Log.Information(
            "Chaos: {Count} invariant(s) — docs/plan/23 § The chaos invariants",
            ChaosInvariants.Length
        );

        foreach (var invariant in ChaosInvariants) {
            Log.Information(
                "  {Number}. {Fault} → {Invariant}",
                invariant.Number,
                invariant.Fault,
                invariant.Invariant
            );
        }

        var suites = ProjectsIn(TestSuite.Chaos);
        var preconditions = new TargetPreconditions(nameof(Chaos));

        preconditions.Require(
            suites.Count > 0,
            "there is no chaos suite — no project under test/ is named CyberCloud.Chaos",
            "create test/CyberCloud.Chaos (docs/plan/03 § test/) with one test per invariant above, "
            + "named so AssertEveryInvariantIsCovered can find it. Build.Test.cs § SuiteOwning "
            + "already routes that name here"
        );

        // ⚠ Resolved, not run — TargetPreconditions.Tool's remarks. The suite's own fixture is what
        // finds out whether the daemon answers and whether its k3s gets a kubelet, and it says so
        // per test; what this checks is that there is a daemon to ask.
        preconditions.Tool(
            "docker",
            "install Docker Desktop on a cgroup v2 host — docs/plan/23 § The lane that needs a kubelet "
            + "says what a v1 host does to every cluster-backed suite, this one included"
        );

        // ⚠ A kube context is REFUSED rather than ignored. The suite starts its own topology and has
        // no mode that breaks somebody else's; a --kube-context that was quietly dropped would leave
        // the person who passed it believing staging had been chaos-tested.
        preconditions.Require(
            string.IsNullOrWhiteSpace(KubeContext),
            "--kube-context was passed, and the chaos suite has no mode that runs against an existing "
            + "cluster — it starts the topology it breaks",
            "drop --kube-context. The staging run docs/plan/23 § Environments and rollout puts on "
            + "nightly is owed under § The chaos invariants, and a suite mode that attaches to a "
            + "deployment is what it needs"
        );

        preconditions.AssertSatisfied(
            "docs/plan/23 § The chaos invariants: \"Each is an assertion, not an observation.\" "
            + "The suite induces every fault it can host and writes what it found; this target reads "
            + "the file, and a Docker daemon is what the faults are induced in."
        );

        AssertEveryInvariantIsCovered(suites);

        ChaosResultsFile.Parent.CreateDirectory();
        ChaosResultsFile.DeleteFile();

        Exception? suiteFailure = null;

        try {
            RunSuites(
                nameof(Chaos),
                suites,
                new Dictionary<string, string> { ["CYBERCLOUD_CHAOS_RESULTS"] = ChaosResultsFile }
            );
        } catch (Exception ex) {
            suiteFailure = ex;
        }

        // ⚠ The table is printed whether or not the suite exited clean, because a violated
        // invariant fails its test and the suite exits non-zero on that — and the table is the
        // part of the output somebody reads first.
        var (violated, vacuous) = ReportChaos(ReadChaosOutcomes());

        // ⚠ A violated row is the reason the suite exited non-zero, so it is the failure this
        // target reports. RunSuites' own message is about a suite that died — port binds, a
        // fixture that never started — and the first run of this target printed ✘ 5 and ✘ 7 and
        // then that message, sending the reader to build/README.md § failed to bind host port for
        // two invariants that had failed exactly as written. That message is kept for the case it
        // describes: a suite that failed with no violated row is a suite that crashed.
        Assert.Empty(
            violated,
            $"{violated.Count} of {ChaosInvariants.Length} chaos invariant(s) did not hold, listed above. "
            + "docs/plan/23 § The chaos invariants: \"Each is an assertion, not an observation.\""
        );

        if (suiteFailure is not null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(suiteFailure);
        }

        Log.Information(
            "Chaos: {Held} invariant(s) held, {Vacuous} vacuous, 0 violated",
            ChaosInvariants.Length - vacuous,
            vacuous
        );
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
    void AssertEveryInvariantIsCovered(IReadOnlyCollection<AbsolutePath> suites) {
        var uncovered = ChaosInvariants
            .Where(invariant => !suites.Any(suite => SuiteListsTest(suite, $"*Invariant{invariant.Number}*")))
            .ToList();

        Assert.Empty(
            uncovered.Select(x => $"invariant {x.Number} ({x.Fault})").ToList(),
            $"{uncovered.Count} of {ChaosInvariants.Length} chaos invariant(s) have no test named "
            + "*Invariant<n>* in the suite. docs/plan/23 § The chaos invariants is the list, and a "
            + "nightly run that is green because an invariant stopped being tested is the failure "
            + "mode this check exists for."
        );
    }

    /// <summary>The rows the suite wrote, by invariant number; empty when it wrote nothing.</summary>
    Dictionary<int, ChaosOutcome> ReadChaosOutcomes() {
        var outcomes = new Dictionary<int, ChaosOutcome>();

        if (!ChaosResultsFile.FileExists()) {
            return outcomes;
        }

        var root = JsonNode.Parse(ChaosResultsFile.ReadAllBytes())?.AsObject();

        if (root?["invariants"] is not JsonObject invariants) {
            return outcomes;
        }

        foreach (var (key, value) in invariants) {
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && value is JsonObject row) {
                outcomes[number] = new(
                    row["status"]?.GetValue<string>() ?? "Unknown",
                    row["detail"]?.GetValue<string>() ?? string.Empty
                );
            }
        }

        if (root["topology"] is JsonObject topology) {
            Log.Information(
                "Chaos: measured against {Topology}",
                string.Join(", ", topology.Select(x => $"{x.Key}={x.Value?.GetValue<string>()}"))
            );
        }

        return outcomes;
    }

    /// <summary>
    ///     One line per invariant — ✔ held, ○ vacuous with the reason, ✘ violated or unrecorded —
    ///     returning the ✘ rows for the caller to fail on, and how many rows were ○.
    /// </summary>
    /// <remarks>
    ///     ⚠ A row the suite did not write is ✘, not ○. The suite writes Vacuous rows for what it
    ///     cannot host, so an absent row is a test that threw before it could say what it found or a
    ///     suite that never got as far as its fixture — and "the nightly is green because the
    ///     results file was empty" is the exact failure this target's earlier version was blocked to
    ///     avoid.
    /// </remarks>
    static (List<string> Violated, int Vacuous) ReportChaos(Dictionary<int, ChaosOutcome> outcomes) {
        var violated = new List<string>();
        var vacuous = new List<int>();

        foreach (var invariant in ChaosInvariants) {
            if (!outcomes.TryGetValue(invariant.Number, out var outcome)) {
                Log.Error(
                    "  ✘ {Number} {Fault,-52} no outcome recorded — the test did not get as far as saying what it found",
                    invariant.Number,
                    invariant.Fault
                );
                violated.Add($"invariant {invariant.Number} ({invariant.Fault}): no outcome recorded");
                continue;
            }

            switch (outcome.Status) {
                case "Held":
                    Log.Information(
                        "  ✔ {Number} {Fault,-52} {Detail}",
                        invariant.Number,
                        invariant.Fault,
                        outcome.Detail
                    );
                    break;

                case "Vacuous":
                    Log.Warning(
                        "  ○ {Number} {Fault,-52} VACUOUS — {Detail}",
                        invariant.Number,
                        invariant.Fault,
                        outcome.Detail
                    );
                    vacuous.Add(invariant.Number);
                    break;

                default:
                    Log.Error(
                        "  ✘ {Number} {Fault,-52} {Status} — {Detail}",
                        invariant.Number,
                        invariant.Fault,
                        outcome.Status.ToUpperInvariant(),
                        outcome.Detail
                    );
                    violated.Add($"invariant {invariant.Number} ({invariant.Fault}): {outcome.Detail}");
                    break;
            }
        }

        if (vacuous.Count > 0) {
            Log.Warning(
                "Chaos: {Count} invariant(s) could not be induced on this machine and are ○, not ✔: {Numbers}. "
                + "Each row above says what it needs; docs/plan/23 § The chaos invariants carries the dated table.",
                vacuous.Count,
                string.Join(", ", vacuous)
            );
        }

        return (violated, vacuous.Count);
    }
}
