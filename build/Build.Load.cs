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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    /// <summary>
    ///     One number a load run has to produce, and what it has to be under.
    /// </summary>
    /// <param name="Scenario">The row of docs/plan/23 § The load scenarios it belongs to.</param>
    /// <param name="Metric">The key in the results file.</param>
    /// <param name="Budget">The absolute ceiling. ⚠ Lower is better for every metric here.</param>
    /// <param name="Unit">For the message; a bare number in a failure is a number somebody misreads.</param>
    sealed record LoadMetric(string Scenario, string Metric, double Budget, string Unit);

    /// <summary>
    ///     The six scenarios of docs/plan/23 § The load scenarios, as the numbers they assert.
    ///     <para>
    ///         ⚠ <b>Two of the doc's assertions are prose and are operationalised here, which is a
    ///         decision this build is making and doc 23 is not.</b> "Reconcile queue does not grow
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
    static readonly LoadMetric[] LoadMetrics =
    [
        new("10 000 tenants, 1 000 000 resources, 5 000 rps reads", "control-plane-read-p99-ms", 25, "ms"),
        new("500 writes/s sustained", "control-plane-write-p99-ms", 60, "ms"),
        new("500 writes/s sustained", "reconcile-queue-depth-slope-per-minute", 0, "items/min"),
        new("ReBAC: 5-deep groups, 10 000 members, 20 000 checks/s", "rebac-check-p99-warm-ms", 10, "ms"),
        new("ReBAC: 5-deep groups, 10 000 members, 20 000 checks/s", "rebac-check-p99-cold-ms", 50, "ms"),
        new("2 000 000 resident grains", "silo-working-set-gb", 12, "GB"),
        new("2 000 000 resident grains", "grain-activation-churn-per-minute", 0, "activations/min"),
        new("1 000 concurrent terminal sessions", "terminal-stream-p99-ms", 80, "ms"),
        new("500 000 spans/s ingest", "span-ingest-drops", 0, "spans"),
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
    [Parameter("A load results JSON file to gate on instead of driving the scenarios. Used by the release gate to re-check the weekly run's numbers.")]
    readonly string? LoadResults;

    /// <summary>
    ///     ⚠ The deployment is an input rather than a dependency — see the note beside the target
    ///     graph in <c>Build.cs</c>.
    /// </summary>
    void RunLoadTests()
    {
        Log.Information(
            "Load: {Count} metric(s) across {Scenarios} scenario(s) — docs/plan/23 § The load "
            + "scenarios, budgets from docs/plan/00 § The quality bar",
            LoadMetrics.Length,
            LoadMetrics.Select(x => x.Scenario).Distinct(StringComparer.Ordinal).Count());

        Log.Warning(
            "Load: \"ingest pods scale linearly\" (docs/plan/23 § The load scenarios, scenario 6) is "
            + "NOT gated. It asserts the shape of a curve and this target compares numbers; a "
            + "single-number stand-in would report a pass on a claim nobody checked.");

        var results = LoadResults is not null
            ? ReadLoadNumbers((AbsolutePath)LoadResults, "the results file passed to --load-results")
            : DriveScenarios();

        Gate(results);
    }

    /// <summary>
    ///     Runs the suite against a real deployment, returning what it measured.
    /// </summary>
    Dictionary<string, double> DriveScenarios()
    {
        var suites = ProjectsIn(TestSuite.Load);
        var resultsFile = ArtifactsDirectory / "load" / "results.json";
        var preconditions = new TargetPreconditions(nameof(Load));

        preconditions.Require(
            suites.Count > 0,
            "there is no load suite — no project under test/ is named CyberCloud.Load",
            "create test/CyberCloud.Load (docs/plan/03 § test/), driving the scenarios and writing "
            + $"the metric names in Build.Load.cs § LoadMetrics to {resultsFile.Name}. "
            + "Build.Test.cs § SuiteOwning already routes that name here");

        preconditions.Require(
            !string.IsNullOrWhiteSpace(E2EBaseUrl),
            "no deployment is configured to drive",
            "pass --e2e-base-url for an environment that can actually be driven to the numbers — "
            + "10 000 tenants, 5 000 rps, 2 000 000 resident grains. docs/plan/23 § Environments and "
            + "rollout puts the weekly suite on staging, and a smaller environment does not produce a "
            + "smaller version of these answers, it produces different ones");

        preconditions.Require(
            LoadBaselineFile.FileExists(),
            $"there is no {LoadBaselineFile.Name}, so the trend half of this target has nothing to "
            + "compare against",
            $"commit {LoadBaselineFile.Name} with the previous release's numbers, one key per metric "
            + "in Build.Load.cs § LoadMetrics. ⚠ Until it exists the 20 % rule in docs/plan/23 § The "
            + "load scenarios is unenforceable, and the budgets alone would pass a release that got "
            + "five times slower while staying under them");

        preconditions.AssertSatisfied(
            "docs/plan/23 § Test layers, row Load: the docs/plan/00 quality bar at scale, gating "
            + "\"budgets met\" before a release.");

        RunSuites(
            nameof(Load),
            suites,
            new Dictionary<string, string>
            {
                ["CYBERCLOUD_LOAD_BASE_URL"] = E2EBaseUrl!,
                ["CYBERCLOUD_LOAD_RESULTS"] = resultsFile,
            });

        return ReadLoadNumbers(resultsFile, $"what {string.Join(", ", suites.Select(x => x.NameWithoutExtension))} measured");
    }

    /// <summary>Both halves of the gate: the six budgets, and the 20 % trend.</summary>
    void Gate(Dictionary<string, double> results)
    {
        var baseline = LoadBaselineFile.FileExists()
            ? ReadLoadNumbers(LoadBaselineFile, $"the previous release, from {LoadBaselineFile.Name}")
            : new Dictionary<string, double>();

        var violations = new List<string>();

        foreach (var metric in LoadMetrics)
        {
            if (!results.TryGetValue(metric.Metric, out var measured))
            {
                violations.Add(
                    $"{metric.Metric} is not in the results. docs/plan/23 § The load scenarios asserts "
                    + $"it for \"{metric.Scenario}\", and a missing number is an unrun scenario, not a "
                    + "pass");

                continue;
            }

            if (measured > metric.Budget)
            {
                violations.Add(
                    $"{metric.Metric} = {Number(measured)} {metric.Unit}, over its budget of "
                    + $"{Number(metric.Budget)} {metric.Unit} — \"{metric.Scenario}\"");
            }

            if (!baseline.TryGetValue(metric.Metric, out var previous))
            {
                Log.Warning(
                    "  ○ {Metric} = {Measured} {Unit} — no previous release recorded, so the 20 % "
                    + "rule did not apply to it",
                    metric.Metric,
                    Number(measured),
                    metric.Unit);

                continue;
            }

            var limit = previous * (1 + RegressionLimit);

            if (measured > limit)
            {
                violations.Add(
                    $"{metric.Metric} regressed {Percent(previous, measured)} against the previous "
                    + $"release ({Number(previous)} → {Number(measured)} {metric.Unit}), past the "
                    + $"{RegressionLimit:P0} limit. ⚠ THIS BLOCKS THE RELEASE EVEN THOUGH "
                    + (measured > metric.Budget
                        ? $"it is also over budget."
                        : $"{Number(measured)} is still under the {Number(metric.Budget)} {metric.Unit} "
                        + "budget — docs/plan/23 § The load scenarios: \"the trend is the signal\""));

                continue;
            }

            Log.Information(
                "  ✔ {Metric,-40} {Measured,8} {Unit,-15} budget {Budget}, previous {Previous} ({Delta})",
                metric.Metric,
                Number(measured),
                metric.Unit,
                Number(metric.Budget),
                Number(previous),
                Percent(previous, measured));
        }

        if (violations.Count == 0)
        {
            Log.Information(
                "Load: {Count} metric(s) within budget and within {Limit:P0} of the previous release",
                LoadMetrics.Length,
                RegressionLimit);

            return;
        }

        foreach (var violation in violations)
            Log.Error("Load: {Violation}", violation);

        Assert.Fail(
            $"{violations.Count} load violation(s), listed above. docs/plan/23 § The load scenarios "
            + "makes both halves release blockers: a missed budget, and a 20 % regression between "
            + "releases even where the budget still passes.");
    }

    static string Number(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    static string Percent(double previous, double measured)
        => previous == 0
            ? measured == 0 ? "0 → 0" : "up from zero"
            : ((measured - previous) / previous).ToString("+0.#%;-0.#%;0%", CultureInfo.InvariantCulture);

    /// <summary>
    ///     A flat <c>{ "metric": number }</c> map out of a results or baseline file.
    /// </summary>
    /// <remarks>
    ///     ⚠ Fails on a non-numeric value rather than skipping it. A results file whose p99 is the
    ///     string <c>"n/a"</c> is a run that did not measure that scenario, and treating it as absent
    ///     would turn it into a warning; treating it as an error is how a broken harness stops being
    ///     mistaken for a clean release.
    /// </remarks>
    // Dictionary rather than IReadOnlyDictionary: CA1859 is an error here and this is a private helper.
    static Dictionary<string, double> ReadLoadNumbers(AbsolutePath file, string what)
    {
        Assert.FileExists(
            file,
            $"{file} does not exist, and it is where this target reads {what}.");

        var root = JsonNode.Parse(file.ReadAllBytes())?.AsObject()
                   ?? throw new System.Text.Json.JsonException($"{file} is not a JSON object.");

        // "metrics" if it is there, the root object otherwise — the baseline is a bare map and a
        // results file may want to carry a release name beside its numbers.
        var metrics = root["metrics"]?.AsObject() ?? root;
        var numbers = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (key, value) in metrics)
        {
            if (value is null || value.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            {
                if (value?.GetValueKind() == System.Text.Json.JsonValueKind.Object)
                    continue;

                Assert.Fail(
                    $"{file}: \"{key}\" is not a number. A load result that is not a number is a "
                    + "scenario that did not produce one, and this target must not read that as a pass.");
            }

            numbers[key] = value!.GetValue<double>();
        }

        Log.Information("Load: read {Count} number(s) — {What}", numbers.Count, what);

        return numbers;
    }
}
