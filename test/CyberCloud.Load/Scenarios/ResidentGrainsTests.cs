using CyberCloud.Providers.Sample.Contracts;
using System.Diagnostics;
using System.Globalization;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, row 4:
///     <i>
///         2 000 000 resident grains → silo working set
///         ≤ 12 GB; no activation thrash
///     </i>, at a tenth of the grains.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The working set is the test process's, which holds three silos, the cluster client
///             and the gateway
///         </b>, so the number is an upper bound on what one silo would hold for the
///         same activations, and it is the number reported because there is no other one to report:
///         the testing host runs its silos in-process. The build prints it beside the 12 GB budget
///         with the scale, and the two are not compared as equals — 200 000 grains against a budget
///         written for 2 000 000 is a tenth of a measurement, and the row is ○ until a run at the
///         row's own scale exists.
///     </para>
///     <para>
///         ⚠ <b>The resident grains are resource grains with no resource behind them.</b> Each
///         activation reads its durable row (a miss) and stays resident; what it costs in memory is
///         the grain, its state object and its place in the directory, which is less than a widget
///         with a body costs. The delta below is therefore a floor for the per-grain cost, and says
///         so.
///     </para>
///     <para>
///         ⚠ <b>"No activation thrash" is churn per minute over an idle minute plus a re-touch.</b>
///         Activations that were collected while idle show as a drop in the count; activations that
///         come back when a sample of the set is touched again show as a rise. Both are churn, and
///         the row's budget is zero.
///     </para>
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class ResidentGrainsTests(LoadTopology topology) {
    /// <summary>docs/plan/23's 2 000 000 resident grains at <see cref="LoadReport.Scale" />.</summary>
    const int Resident = (int)(2_000_000 * LoadReport.Scale);

    const int Concurrency = 64;
    const string WorkingSetMetric = "silo-working-set-gb";
    const string ChurnMetric = "grain-activation-churn-per-minute";
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TwoHundredThousandResidentGrainsAndTheirWorkingSet() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var platform = topology.Platform;
        var tenants = topology.Subscriptions.Select(static x => x.Tenant).Distinct().ToList();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var activationsBefore = await ResourceActivationsAsync();

        // ── Make them resident. ───────────────────────────────────────────────────────────────
        var ids = Enumerable.Range(0, Resident)
            .Select(i => (Tenant: tenants[i % tenants.Count], Id: Guid.NewGuid()))
            .ToList();
        var activating = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(Concurrency);
        var faults = 0;

        await Task.WhenAll(
            ids.Select(async x => {
                    await gate.WaitAsync(token);

                    try {
                        _ = await platform.For(x.Tenant)
                            .GetGrain<IResourceGrain>(GrainKeys.Resource(x.Id))
                            .GetAsync(SampleWidgets.V2026, []);
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        Interlocked.Increment(ref faults);
                    } finally {
                        gate.Release();
                    }
                }
            )
        );

        var activatedIn = activating.Elapsed;
        var activationsAfter = await ResourceActivationsAsync();

        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var perSilo = await platform.Management.GetRuntimeStatistics(null);
        var totalActivations = await platform.Management.GetTotalActivationCount();

        // On the console, not only the test output, and with three counters side by side — see
        // ResourceActivationsAsync for what the sixth run's results file said when it was one.
        Console.WriteLine(
            $"[CyberCloud.Load] {Resident} touched in {activatedIn.TotalSeconds:F0} s ({faults} faults): resource activations {activationsBefore} → {activationsAfter}; "
            + $"working set {Gb(workingSetBefore):F2} → {Gb(workingSetAfter):F2} GB; activations per silo: "
            + $"{string.Join(", ", perSilo.Select(static x => x.ActivationCount.ToString(CultureInfo.InvariantCulture)))}; total activation count {totalActivations}"
        );
        output?.WriteLine($"{Resident} touched; the numbers are on the console.");

        // ── Idle, then re-touch a sample. ─────────────────────────────────────────────────────
        await Task.Delay(Idle, token);
        var afterIdle = await ResourceActivationsAsync();
        var collected = Math.Max(0, activationsAfter - afterIdle);

        var sample = ids.Where(static (_, i) => i % 100 == 0).ToList();

        await Task.WhenAll(
            sample.Select(async x => {
                    await gate.WaitAsync(token);

                    try {
                        _ = await platform.For(x.Tenant)
                            .GetGrain<IResourceGrain>(GrainKeys.Resource(x.Id))
                            .GetAsync(SampleWidgets.V2026, []);
                    } finally {
                        gate.Release();
                    }
                }
            )
        );

        var afterTouch = await ResourceActivationsAsync();
        var reactivated = Math.Max(0, afterTouch - afterIdle);
        var churnPerMinute = (collected + reactivated) / Idle.TotalMinutes;

        output?.WriteLine(
            $"after {Idle.TotalSeconds:F0} s idle: {afterIdle} ({collected} collected); after re-touching {sample.Count}: {afterTouch} ({reactivated} re-activated); churn {churnPerMinute:F1}/min"
        );

        var deltaGb = Gb(workingSetAfter - workingSetBefore);

        topology.Report.Measured(WorkingSetMetric, Math.Round(Gb(workingSetAfter), 3));
        topology.Report.Aside(WorkingSetMetric, "residentGrains", Resident);
        topology.Report.Aside(WorkingSetMetric, "docResidentGrains", 2_000_000);
        topology.Report.Aside(WorkingSetMetric, "resourceActivations", activationsAfter);
        topology.Report.Aside(WorkingSetMetric, "activationsByType", await ActivationsByTypeAsync());
        topology.Report.Aside(WorkingSetMetric, "workingSetBeforeGb", Math.Round(Gb(workingSetBefore), 3));
        topology.Report.Aside(WorkingSetMetric, "workingSetDeltaGb", Math.Round(deltaGb, 3));
        topology.Report.Aside(
            WorkingSetMetric,
            "bytesPerActivationFloor",
            activationsAfter - activationsBefore <= 0
                ? 0
                : Math.Round((double)(workingSetAfter - workingSetBefore) / (activationsAfter - activationsBefore))
        );
        topology.Report.Aside(WorkingSetMetric, "activationSeconds", Math.Round(activatedIn.TotalSeconds, 1));
        topology.Report.Measured(ChurnMetric, Math.Round(churnPerMinute, 1));
        topology.Report.Aside(ChurnMetric, "collectedWhileIdle", collected);
        topology.Report.Aside(ChurnMetric, "reactivatedOnTouch", reactivated);
        topology.Report.Aside(ChurnMetric, "idleSeconds", Idle.TotalSeconds);

        topology.Report.Note(
            WorkingSetMetric,
            $"{Resident} resource grains (a tenth of the row's 2 000 000, and empty ones: a grain, its state object and its directory entry, no body) made resident "
            + $"in {activatedIn.TotalSeconds:F0} s across 3 in-process silos; the number is the whole test process's working set — three silos, the client and the "
            + $"gateway — so an upper bound; the delta over the population was {deltaGb:F2} GB, {Math.Round((double)(workingSetAfter - workingSetBefore) / Math.Max(1, activationsAfter - activationsBefore)):F0} bytes per activation."
        );

        topology.Report.Note(
            ChurnMetric,
            $"activations collected during {Idle.TotalSeconds:F0} s idle plus activations re-created when {sample.Count} of the set were touched again, per minute."
        );

        faults.ShouldBe(0, "some activations faulted, so the count is not the population.");
        (activationsAfter - activationsBefore).ShouldBeGreaterThan(
            (int)(Resident * 0.95),
            $"only {activationsAfter - activationsBefore} of {Resident} grains became resident."
        );
    }

    /// <summary>Resource-grain activations across the cluster.</summary>
    /// <remarks>
    ///     ⚠ <b>The maximum over the silos' rows, not their sum.</b> Orleans 10.2.2's
    ///     <c>IManagementGrain.GetSimpleGrainStatistics</c> documents a row as "the number of
    ///     activations of this grain type on this given silo", and returns one row per silo — each
    ///     carrying the cluster-wide count. The seventh load run put it beside the other two
    ///     counters: 200 400 resource grains on each of three silos by these rows, 68 207 + 67 485 +
    ///     67 344 = 203 036 by <c>GetRuntimeStatistics</c>, and 203 036 by
    ///     <c>GetTotalActivationCount</c>. The sixth run's results file said 624 450 for a
    ///     population of 208 150 because this method summed them.
    /// </remarks>
    async Task<int> ResourceActivationsAsync() {
        var statistics = await topology.Platform.Management.GetSimpleGrainStatistics();

        return statistics
            .Where(static x => x.GrainType.Contains("ResourceGrain", StringComparison.Ordinal))
            .GroupBy(static x => x.GrainType, StringComparer.Ordinal)
            .Sum(static g => g.Max(static x => x.ActivationCount));
    }

    /// <summary>
    ///     Every grain type with a thousand or more activations across the cluster, largest first —
    ///     so "resource activations" can be read against what else the touch made resident. The
    ///     type is the class name; the rows name it as <c>Namespace.Class,Assembly</c>.
    /// </summary>
    async Task<string> ActivationsByTypeAsync() {
        var statistics = await topology.Platform.Management.GetSimpleGrainStatistics();

        return string.Join(
            "; ",
            statistics
                .GroupBy(static x => x.GrainType, StringComparer.Ordinal)
                .Select(static g => (Type: ClassName(g.Key), Count: g.Max(static x => x.ActivationCount)))
                .Where(static x => x.Count >= 1_000)
                .OrderByDescending(static x => x.Count)
                .Select(static x => $"{x.Type} {x.Count.ToString(CultureInfo.InvariantCulture)}")
        );
    }

    static string ClassName(string grainType) {
        var typeName = grainType.Split(',')[0];
        return typeName[(typeName.LastIndexOf('.') + 1)..];
    }

    static double Gb(long bytes) => bytes / 1_000_000_000d;
}
