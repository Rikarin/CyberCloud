// Load — docs/plan/23 § Build, row `E2E` `Chaos` `Load`: "Against a real deployment".
// docs/plan/23 § Test layers, row `Load`: the docs/plan/00 quality bar at scale, weekly and
// pre-release; the numbers are § The load scenarios.
//
// ── ⚠ THE HALF OF THIS TARGET THAT IS EASY TO LOSE ───────────────────────────────────────────────
//
// docs/plan/23 § The load scenarios: "its results are tracked over time. A 20 % REGRESSION BETWEEN
// RELEASES IS A RELEASE BLOCKER EVEN IF THE ABSOLUTE NUMBER STILL PASSES — the trend is the signal."
//
// A target that only checks the six budgets satisfies the table and silently drops that sentence,
// and the drop is invisible: every run is green, and the p99 that walked from 4 ms to 24 ms over six
// releases is still "under 25 ms". Both checks are here, and the trend one is the reason
// LoadBaselineFile is a committed file rather than a build artefact.
//
// ── ⚠ THE DEPLOYMENT IS ONE THE SUITE STARTS, AT A TENTH OF THE ROW'S SCALE — issue #44 ────────
//
// test/CyberCloud.Load starts the chaos suite's topology — three silos over a real Redis, three
// PostgreSQL shards and a k3s in Docker — puts the real CyberCloud.Gateway.Host in front of it on a
// free port, and drives it over HTTP at one tenth of the rates docs/plan/23 names: 500 rps of reads,
// 50 writes/s, 2 000 ReBAC checks/s, 200 000 resident grains. The budgets are NOT scaled: a p99
// ceiling does not move with load, so meeting 25 ms at 500 rps is necessary for meeting it at 5 000
// and not sufficient, and every line this target prints says which scale the number is from. The
// one budget that IS about population — the silo working set at two million grains — is reported ○
// with the tenth-scale number beside it rather than ticked against a ceiling written for ten times
// the population.

using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

partial class Build {
    /// <summary>
    ///     One number a load run has to produce, and what it has to be under.
    /// </summary>
    /// <param name="Scenario">The row of docs/plan/23 § The load scenarios it belongs to.</param>
    /// <param name="Metric">The key in the results file.</param>
    /// <param name="Budget">The absolute ceiling. ⚠ Lower is better for every metric here.</param>
    /// <param name="Unit">For the message; a bare number in a failure is a number somebody misreads.</param>
    /// <param name="ScalesWithPopulation">
    ///     Whether the budget is about how much there is rather than how fast it answers. A working
    ///     set measured over a tenth of the grains says nothing about the ceiling for all of them, so
    ///     such a metric is ○ at any scale below 1 rather than ✔ — see <see cref="Gate" />.
    /// </param>
    sealed record LoadMetric(
        string Scenario,
        string Metric,
        double Budget,
        string Unit,
        bool ScalesWithPopulation = false);

    /// <summary>
    ///     The six scenarios of docs/plan/23 § The load scenarios, as the numbers they assert.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Two of the doc's assertions are prose and are operationalised here, which is a
    ///             decision this build is making and doc 23 is not.
    ///         </b> "Reconcile queue does not grow
    ///         unboundedly" becomes a queue-depth slope with a ceiling of zero, and "no activation
    ///         thrash" becomes an activation-churn rate with a ceiling of zero. Both are the
    ///         narrowest reading of the words; if the intent was "grows sublinearly" or "some churn
    ///         is fine", the fix is to say so in doc 23 and change the budget here — not to drop the
    ///         row, which is what happens to a prose assertion nobody encodes.
    ///     </para>
    ///     <para>
    ///         ⚠ "Ingest pods scale linearly" (scenario 6) is <b>not</b> here. It is a claim about a
    ///         curve rather than about a number, and a fabricated single-number proxy for it would be
    ///         worse than its absence — see the log line in <see cref="RunLoadTests" />, which says
    ///         so on every run rather than leaving the gap silent.
    ///     </para>
    /// </summary>
    static readonly LoadMetric[] LoadMetrics = [
        new("10 000 tenants, 1 000 000 resources, 5 000 rps reads", "control-plane-read-p99-ms", 25, "ms"),
        new("500 writes/s sustained", "control-plane-write-p99-ms", 60, "ms"),
        new("500 writes/s sustained", "reconcile-queue-depth-slope-per-minute", 0, "items/min"),
        new("ReBAC: 5-deep groups, 10 000 members, 20 000 checks/s", "rebac-check-p99-warm-ms", 10, "ms"),
        new("ReBAC: 5-deep groups, 10 000 members, 20 000 checks/s", "rebac-check-p99-cold-ms", 50, "ms"),
        new("2 000 000 resident grains", "silo-working-set-gb", 12, "GB", ScalesWithPopulation: true),
        new("2 000 000 resident grains", "grain-activation-churn-per-minute", 0, "activations/min"),
        new("1 000 concurrent terminal sessions", "terminal-stream-p99-ms", 80, "ms"),
        new("500 000 spans/s ingest", "span-ingest-drops", 0, "spans")
    ];

    /// <summary>
    ///     How much worse than the previous release a metric may get before it blocks the release,
    ///     even while under budget. docs/plan/23 § The load scenarios.
    /// </summary>
    const double RegressionLimit = 0.20;

    /// <summary>
    ///     The previous release's numbers, committed so the comparison survives the machine.
    /// </summary>
    /// <remarks>
    ///     ⚠ At the repository root and in git, for the same reason <c>durable-grains.txt</c> is
    ///     (Build.Architecture.cs § DurableGrainsFile): a trend gate whose baseline lives in
    ///     <c>artifacts/</c> compares a release against whatever happened to be on the runner, which
    ///     on a fresh CI machine is nothing — and a trend check that silently has no baseline is the
    ///     exact failure this whole half of the target exists to prevent. Updating it is a reviewed
    ///     diff at release time, and the review is the point: somebody has to look at a p99 moving
    ///     from 4 ms to 19 ms and agree to it.
    ///     <para>
    ///         ⚠ <b>The file's <c>release</c> is what arms the rule.</b> docs/plan/23's sentence is
    ///         about a regression <i>between releases</i>, and there has been none: the committed
    ///         numbers are one run on one laptop, with a cold p99 over a few hundred samples that
    ///         moved 50 % between two runs of the same code (the branch's review measured it). A
    ///         baseline with no <c>release</c> is provisional — the deltas against it are printed
    ///         so the trend is visible from the first run, and none of them blocks. The first
    ///         release stamps the file with its tag, and from then on the 20 % rule fails the build.
    ///     </para>
    /// </remarks>
    AbsolutePath LoadBaselineFile => RootDirectory / "load-baseline.json";

    /// <summary>
    ///     A results file to gate on instead of running the scenarios.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is not a way to skip the run. It exists because the run and the gate genuinely
    ///     happen in different places: docs/plan/23 § CI shape puts the load suite in `weekly.yml`
    ///     against an environment that takes an hour to drive to 5 000 rps, and the release gate that
    ///     has to consult those numbers is a different workflow days later. Without it, "check the
    ///     trend" would mean "re-run the load suite", which nobody does, which is how the trend rule
    ///     stops being enforced.
    /// </remarks>
    [Parameter(
        "A load results JSON file to gate on instead of driving the scenarios. Used by the release gate to re-check the weekly run's numbers."
    )]
    readonly string? LoadResults;

    /// <summary>What a results file says: the numbers, the rows that could not be measured, and the scale.</summary>
    /// <param name="Numbers">Metric name to what was measured.</param>
    /// <param name="Vacuous">Metric name to why it was not measured here.</param>
    /// <param name="Scale">
    ///     The fraction of docs/plan/23's rates the numbers were driven at; 1 for a baseline with no
    ///     <c>scale</c>.
    /// </param>
    /// <param name="Release">
    ///     The release tag a baseline was stamped with, or <see langword="null" /> for a provisional one —
    ///     see <see cref="LoadBaselineFile" />.
    /// </param>
    sealed record LoadNumbers(
        Dictionary<string, double> Numbers,
        Dictionary<string, string> Vacuous,
        double Scale,
        string? Release);

    /// <summary>
    ///     ⚠ The deployment is an input rather than a dependency — see the note beside the target
    ///     graph in <c>Build.cs</c>. Here the input is a Docker daemon: the suite starts what it drives.
    /// </summary>
    void RunLoadTests() {
        Log.Information(
            "Load: {Count} metric(s) across {Scenarios} scenario(s) — docs/plan/23 § The load "
            + "scenarios, budgets from docs/plan/00 § The quality bar",
            LoadMetrics.Length,
            LoadMetrics.Select(x => x.Scenario).Distinct(StringComparer.Ordinal).Count()
        );

        Log.Warning(
            "Load: \"ingest pods scale linearly\" (docs/plan/23 § The load scenarios, scenario 6) is "
            + "NOT gated. It asserts the shape of a curve and this target compares numbers; a "
            + "single-number stand-in would report a pass on a claim nobody checked."
        );

        var results = LoadResults is not null
            ? ReadLoadNumbers((AbsolutePath)LoadResults, "the results file passed to --load-results")
            : DriveScenarios();

        Gate(results);
    }

    /// <summary>
    ///     Runs the suite against the topology it starts, returning what it measured.
    /// </summary>
    LoadNumbers DriveScenarios() {
        var suites = ProjectsIn(TestSuite.Load);
        var resultsFile = ArtifactsDirectory / "load" / "results.json";
        var preconditions = new TargetPreconditions(nameof(Load));

        preconditions.Require(
            suites.Count > 0,
            "there is no load suite — no project under test/ is named CyberCloud.Load",
            "create test/CyberCloud.Load (docs/plan/03 § test/), driving the scenarios and writing "
            + $"the metric names in Build.Load.cs § LoadMetrics to {resultsFile.Name}. "
            + "Build.Test.cs § SuiteOwning already routes that name here"
        );

        // ⚠ Resolved, not run — TargetPreconditions.Tool's remarks. The suite's fixture is what
        // finds out whether the daemon answers; this checks there is one to ask.
        preconditions.Tool(
            "docker",
            "install Docker Desktop on a cgroup v2 host — docs/plan/23 § The lane that needs a kubelet"
        );

        // ⚠ A base URL is REFUSED rather than ignored, for the reason Build.Chaos gives its kube
        // context: the suite has no mode that drives a deployment, and a --e2e-base-url that was
        // quietly dropped would leave the person who passed it believing staging had been measured.
        preconditions.Require(
            string.IsNullOrWhiteSpace(E2EBaseUrl),
            "--e2e-base-url was passed, and the load suite has no mode that drives an existing "
            + "deployment — it starts the topology it drives, at a tenth of docs/plan/23's scale",
            "drop --e2e-base-url. The full-scale weekly run against staging is owed under "
            + "docs/plan/23 § The load scenarios, and a suite mode that takes a base URL and a token "
            + "source is what it needs"
        );

        preconditions.Require(
            LoadBaselineFile.FileExists(),
            $"there is no {LoadBaselineFile.Name}, so the trend half of this target has nothing to "
            + "compare against",
            $"commit {LoadBaselineFile.Name} with the previous release's numbers, one key per metric "
            + "in Build.Load.cs § LoadMetrics. ⚠ Until it exists the 20 % rule in docs/plan/23 § The "
            + "load scenarios is unenforceable, and the budgets alone would pass a release that got "
            + "five times slower while staying under them"
        );

        preconditions.AssertSatisfied(
            "docs/plan/23 § Test layers, row Load: the docs/plan/00 quality bar at scale, gating "
            + "\"budgets met\" before a release."
        );

        resultsFile.Parent.CreateDirectory();
        resultsFile.DeleteFile();

        RunSuites(
            nameof(Load),
            suites,
            new Dictionary<string, string> { ["CYBERCLOUD_LOAD_RESULTS"] = resultsFile }
        );

        return ReadLoadNumbers(
            resultsFile,
            $"what {string.Join(", ", suites.Select(x => x.NameWithoutExtension))} measured"
        );
    }

    /// <summary>Both halves of the gate: the six budgets, and the 20 % trend.</summary>
    /// <remarks>
    ///     ⚠ A metric the results file lists under <c>vacuous</c> is ○ with its reason and fails
    ///     nothing; a metric under neither <c>metrics</c> nor <c>vacuous</c> is an unrun scenario and
    ///     fails. The difference is the suite having said, in a sentence, why it could not measure
    ///     the row — which is the only thing that separates "not hosted here" from "forgotten".
    /// </remarks>
    void Gate(LoadNumbers results) {
        var baseline = LoadBaselineFile.FileExists()
            ? ReadLoadNumbers(LoadBaselineFile, $"the previous release, from {LoadBaselineFile.Name}")
            : new LoadNumbers(
                new Dictionary<string, double>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                1,
                null
            );

        if (results.Scale < 1) {
            Log.Warning(
                "Load: measured at {Scale:P0} of docs/plan/23 § The load scenarios' rates. A budget met here "
                + "is necessary for the row and not sufficient; the full-scale run is the staging environment's.",
                results.Scale
            );
        }

        // ⚠ The trend rule compares like with like. A baseline driven at a tenth of the rates and a
        // run driven at all of them differ in what they measured, not in how the platform did, so
        // the 20 % rule is not applied across scales — and the log says so rather than printing a
        // regression that is a change of population.
        var trendApplies = Math.Abs(baseline.Scale - results.Scale) < 0.001;

        // ⚠ Armed by a release, not by the file's existence — LoadBaselineFile's remarks.
        var trendEnforced = trendApplies && baseline.Release is not null;

        if (trendApplies && !trendEnforced && baseline.Numbers.Count > 0) {
            Log.Warning(
                "Load: {Baseline} carries no release, so it is a provisional baseline — one run, not a release. The deltas "
                + "against it are printed below and none of them blocks; the 20 % rule of docs/plan/23 § The load scenarios "
                + "arms when a release stamps the file with its tag.",
                LoadBaselineFile.Name
            );
        }

        if (!trendApplies && baseline.Numbers.Count > 0) {
            Log.Warning(
                "Load: {Baseline} was measured at {BaselineScale:P0} and this run at {Scale:P0}; the 20 % rule "
                + "does not apply across scales, so this run's numbers are compared to the budgets only. "
                + "Commit this run's results as the baseline at its scale to arm the rule for the next one.",
                LoadBaselineFile.Name,
                baseline.Scale,
                results.Scale
            );
        }

        var violations = new List<string>();
        var vacuous = new List<string>();

        foreach (var metric in LoadMetrics) {
            if (results.Vacuous.TryGetValue(metric.Metric, out var reason)) {
                Log.Warning("  ○ {Metric,-40} VACUOUS — {Reason}", metric.Metric, reason);
                vacuous.Add(metric.Metric);
                continue;
            }

            if (!results.Numbers.TryGetValue(metric.Metric, out var measured)) {
                violations.Add(
                    $"{metric.Metric} is not in the results. docs/plan/23 § The load scenarios asserts "
                    + $"it for \"{metric.Scenario}\", and a missing number is an unrun scenario, not a "
                    + "pass"
                );

                continue;
            }

            if (metric.ScalesWithPopulation && results.Scale < 1) {
                // ⚠ ○, not ✔. 1.9 GB over 200 000 grains against a 12 GB budget for 2 000 000 is
                // a number about a different population, and printing a tick beside it would be
                // the false reassurance GateStatus.Vacuous exists to refuse.
                Log.Warning(
                    "  ○ {Metric,-40} {Measured,8} {Unit,-15} measured at {Scale:P0} of the population — the "
                    + "{Budget} {Unit} budget is written for all of it and is not compared",
                    metric.Metric,
                    Number(measured),
                    metric.Unit,
                    results.Scale,
                    Number(metric.Budget),
                    metric.Unit
                );

                vacuous.Add(metric.Metric);
                continue;
            }

            if (measured > metric.Budget) {
                violations.Add(
                    $"{metric.Metric} = {Number(measured)} {metric.Unit}, over its budget of "
                    + $"{Number(metric.Budget)} {metric.Unit} — \"{metric.Scenario}\""
                );
            }

            if (!trendApplies || !baseline.Numbers.TryGetValue(metric.Metric, out var previous)) {
                Log.Warning(
                    "  {Marker} {Metric} = {Measured} {Unit} — budget {Budget}; no previous release at this scale, "
                    + "so the 20 % rule did not apply to it",
                    measured > metric.Budget ? "✘" : "✔",
                    metric.Metric,
                    Number(measured),
                    metric.Unit,
                    Number(metric.Budget)
                );

                continue;
            }

            var limit = previous * (1 + RegressionLimit);

            if (measured > limit && !trendEnforced) {
                Log.Warning(
                    "  {Marker} {Metric,-40} {Measured,8} {Unit,-15} budget {Budget}, provisional {Previous} ({Delta}) — past the "
                    + "{Limit:P0} limit against a run that is not a release, so it does not block",
                    measured > metric.Budget ? "✘" : "✔",
                    metric.Metric,
                    Number(measured),
                    metric.Unit,
                    Number(metric.Budget),
                    Number(previous),
                    Percent(previous, measured),
                    RegressionLimit
                );

                continue;
            }

            if (measured > limit) {
                violations.Add(
                    $"{metric.Metric} regressed {Percent(previous, measured)} against the previous "
                    + $"release ({Number(previous)} → {Number(measured)} {metric.Unit}), past the "
                    + $"{RegressionLimit:P0} limit. ⚠ THIS BLOCKS THE RELEASE EVEN THOUGH "
                    + (measured > metric.Budget
                            ? $"it is also over budget."
                            : $"{Number(measured)} is still under the {Number(metric.Budget)} {metric.Unit} "
                            + "budget — docs/plan/23 § The load scenarios: \"the trend is the signal\"")
                );

                continue;
            }

            Log.Information(
                "  {Marker} {Metric,-40} {Measured,8} {Unit,-15} budget {Budget}, previous {Previous} ({Delta})",
                measured > metric.Budget ? "✘" : "✔",
                metric.Metric,
                Number(measured),
                metric.Unit,
                Number(metric.Budget),
                Number(previous),
                Percent(previous, measured)
            );
        }

        if (vacuous.Count > 0) {
            Log.Warning(
                "Load: {Count} metric(s) are ○, not ✔: {Metrics}. Each row above says why; docs/plan/23 "
                + "§ The load scenarios carries the dated table.",
                vacuous.Count,
                string.Join(", ", vacuous)
            );
        }

        if (violations.Count == 0) {
            Log.Information(
                trendEnforced
                    ? "Load: {Count} metric(s) within budget and within {Limit:P0} of the previous release, {Vacuous} ○"
                    : "Load: {Count} metric(s) within budget; the {Limit:P0} rule is not armed, no release baseline yet; {Vacuous} ○",
                LoadMetrics.Length - vacuous.Count,
                RegressionLimit,
                vacuous.Count
            );

            return;
        }

        foreach (var violation in violations) {
            Log.Error("Load: {Violation}", violation);
        }

        Assert.Fail(
            $"{violations.Count} load violation(s), listed above. docs/plan/23 § The load scenarios "
            + "makes both halves release blockers: a missed budget, and a 20 % regression between "
            + "releases even where the budget still passes."
        );
    }

    static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static string Percent(double previous, double measured) =>
        previous == 0
            ? measured == 0 ? "0 → 0" : "up from zero"
            : ((measured - previous) / previous).ToString("+0.#%;-0.#%;0%", CultureInfo.InvariantCulture);

    /// <summary>
    ///     A results or baseline file: the <c>{ "metric": number }</c> map, the <c>vacuous</c> map,
    ///     and the <c>scale</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Fails on a non-numeric value rather than skipping it. A results file whose p99 is the
    ///     string <c>"n/a"</c> is a run that did not measure that scenario, and treating it as absent
    ///     would turn it into a warning; treating it as an error is how a broken harness stops being
    ///     mistaken for a clean release. The place for "could not measure" is the <c>vacuous</c>
    ///     map, with a sentence.
    /// </remarks>
    static LoadNumbers ReadLoadNumbers(AbsolutePath file, string what) {
        Assert.FileExists(
            file,
            $"{file} does not exist, and it is where this target reads {what}."
        );

        var root = JsonNode.Parse(file.ReadAllBytes())?.AsObject()
            ?? throw new System.Text.Json.JsonException($"{file} is not a JSON object.");

        // "metrics" if it is there, the root object otherwise — the baseline is a bare map and a
        // results file carries a scale, the vacuous rows and the distributions beside its numbers.
        var metrics = root["metrics"]?.AsObject() ?? root;
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (key, value) in metrics) {
            if (value is null || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) {
                if (value?.GetValueKind() == System.Text.Json.JsonValueKind.Object) {
                    continue;
                }

                Assert.Fail(
                    $"{file}: \"{key}\" is not a number. A load result that is not a number is a "
                    + "scenario that did not produce one, and this target must not read that as a pass."
                );
            }

            numbers[key] = value!.GetValue<double>();
        }

        var vacuous = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root["vacuous"] is JsonObject unmeasured) {
            foreach (var (key, value) in unmeasured) {
                vacuous[key] = value?.GetValue<string>() ?? "no reason given";
            }
        }

        var scale = root["scale"] is { } s && s.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? s.GetValue<double>()
            : 1;
        var release = root["release"] is { } r && r.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? r.GetValue<string>()
            : null;

        Log.Information(
            "Load: read {Count} number(s) and {Vacuous} vacuous row(s) at scale {Scale}, release {Release} — {What}",
            numbers.Count,
            vacuous.Count,
            scale,
            release ?? "none (provisional)",
            what
        );

        return new(numbers, vacuous, scale, release);
    }
}
