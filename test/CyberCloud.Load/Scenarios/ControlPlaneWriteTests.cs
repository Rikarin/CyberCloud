using CyberCloud.Providers.Sample.Contracts;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, row 2: <i>500 writes/s sustained → write p99 &lt; 60 ms;
///     reconcile queue does not grow unboundedly</i>, driven at a tenth of the rate through the real
///     gateway.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The write is a create, and its latency is the 202.</b> A PUT of a new widget runs the
///         twelve-step saga of docs/plan/06 § Two-phase create — quota lease, index claim, resource
///         grain, group membership, operation start, index confirm — across two shards' PostgreSQL,
///         and answers Accepted. That is the number the budget is about; what happens after the 202
///         is the queue.
///     </para>
///     <para>
///         ⚠ <b>The queue is drained by the platform's own reminders and nothing else.</b> Nothing in
///         this scenario drives an operation. <c>OperationGrain</c> registers a one-minute reminder
///         on start and no timer, so the first pass of every create lands about a minute after its
///         202; the queue therefore fills for the first minute of a sustained write rate by
///         construction, and "does not grow unboundedly" is a claim about what happens
///         <i>after</i> that. The depth is sampled through the real listing every ten seconds, and
///         the slope Build.Load gates is the least-squares slope over the samples from the first
///         reminder period onward, in items per minute — the same reading Build.Load.cs' remarks
///         give the row. A run shorter than two reminder periods could not measure it at all.
///     </para>
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class ControlPlaneWriteTests(LoadTopology topology) {
    /// <summary>docs/plan/23's 500 writes/s at <see cref="LoadReport.Scale" />.</summary>
    const double Rate = 500 * LoadReport.Scale;

    const string LatencyMetric = "control-plane-write-p99-ms";
    const string QueueMetric = "reconcile-queue-depth-slope-per-minute";
    static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Window = TimeSpan.FromSeconds(150);
    static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);
    static readonly TimeSpan DrainBudget = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task SustainedWritesAtATenthOfTargetRateAndTheQueueBehindThem() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var subscriptions = topology.Subscriptions;
        var callersBySubscription = subscriptions.ToDictionary(x => x.Subscription, x => topology.CallersOf(x));
        var run = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6];

        // ── The queue sampler, alongside the writes. ──────────────────────────────────────────
        var samples = new List<(TimeSpan At, int Depth, int Total)>();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = Stopwatch.StartNew();

        var sampler = Task.Run(async () => {
                while (!sampling.IsCancellationRequested) {
                    var (depth, total) = await DepthAsync(run, token);
                    samples.Add((clock.Elapsed, depth, total));
                    output?.WriteLine($"[{clock.Elapsed.TotalSeconds,5:F0}s] queue depth {depth} of {total} created");

                    try {
                        await Task.Delay(SampleEvery, sampling.Token);
                    } catch (OperationCanceledException) {
                        // Stopped.
                    }
                }
            },
            CancellationToken.None
        );

        // ── The writes. ───────────────────────────────────────────────────────────────────────
        var driver = new OpenLoopDriver(Rate, WarmUp, Window);

        var distribution = await driver.RunAsync(async (i, ct) => {
                var world = subscriptions[i % subscriptions.Count];
                var callers = callersBySubscription[world.Subscription];
                var caller = callers[(i / subscriptions.Count) % callers.Count];

                using var response = await topology.Http.SendAsync(
                    topology.Put(caller, world, $"load-{run}-{i.ToString(CultureInfo.InvariantCulture)}", "load"),
                    HttpCompletionOption.ResponseContentRead,
                    ct
                );

                return response.StatusCode == HttpStatusCode.Accepted ? null : $"HTTP {(int)response.StatusCode}";
            },
            token
        );

        var writesEnded = clock.Elapsed;
        output?.WriteLine(distribution.ToString());

        foreach (var (reason, count) in driver.Failures) {
            output?.WriteLine($"  {count} × {reason}");
        }

        // ── The drain: the platform's reminders finish what was accepted. ─────────────────────
        TimeSpan? drained = null;

        while (clock.Elapsed - writesEnded < DrainBudget) {
            await Task.Delay(SampleEvery, token);
            var depth = samples.Count == 0 ? int.MaxValue : samples[^1].Depth;

            if (depth == 0) {
                drained = clock.Elapsed - writesEnded;
                break;
            }
        }

        await sampling.CancelAsync();
        await sampler;

        var (finalDepth, finalTotal) = await DepthAsync(run, token);
        var failed = await FailedAsync(run, token);

        // ── The slope: least squares over the samples after the first reminder period, during the writes. ──
        var steady = samples.Where(x => x.At >= WarmUp + ReminderPeriod && x.At <= writesEnded).ToList();
        var slopePerMinute = Slope(steady);

        topology.Report.Measured(LatencyMetric, distribution.P99, distribution);
        topology.Report.Aside(LatencyMetric, "worstMsPerTenSeconds", string.Join(" ", driver.WorstPer(TimeSpan.FromSeconds(10)).Select(x => x.ToString("0", CultureInfo.InvariantCulture))));
        topology.Report.Aside(LatencyMetric, "errorRate", distribution.Samples + distribution.Errors == 0 ? 0 : Math.Round((double)distribution.Errors / (distribution.Samples + distribution.Errors), 4));
        topology.Report.Measured(QueueMetric, Math.Round(slopePerMinute, 2));
        topology.Report.Aside(QueueMetric, "created", finalTotal);
        topology.Report.Aside(QueueMetric, "peakDepth", samples.Count == 0 ? 0 : samples.Max(x => x.Depth));
        topology.Report.Aside(QueueMetric, "depthAtEndOfWrites", samples.Where(x => x.At <= writesEnded).Select(x => x.Depth).LastOrDefault());
        topology.Report.Aside(QueueMetric, "steadySamples", steady.Count);
        topology.Report.Aside(QueueMetric, "drainSeconds", drained is { } d ? Math.Round(d.TotalSeconds, 1) : -1);
        topology.Report.Aside(QueueMetric, "depthAfterDrainBudget", finalDepth);
        topology.Report.Aside(QueueMetric, "failedOperations", failed);

        topology.Report.Note(
            LatencyMetric,
            $"PUT of a new widget (a create, answered 202) through CyberCloud.Gateway.Host over loopback HTTP at {Rate.ToString("0", CultureInfo.InvariantCulture)} writes/s "
            + $"(a tenth of the row's 500) for {Window.TotalSeconds:F0} s after {WarmUp.TotalSeconds:F0} s of warm-up, across {subscriptions.Count} subscriptions on 2 shards; failures: "
            + (driver.Failures.IsEmpty ? "none" : string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")))
        );

        topology.Report.Note(
            QueueMetric,
            $"depth = accepted creates not yet terminal, sampled every {SampleEvery.TotalSeconds:F0} s through the real listing; slope is least-squares over the "
            + $"{steady.Count} samples from {ReminderPeriod.TotalSeconds:F0} s into the writes to their end, in items/min; the queue is drained by the one-minute "
            + $"reminder alone (nothing drives a pass); peak {(samples.Count == 0 ? 0 : samples.Max(x => x.Depth))}, drained to zero "
            + $"{(drained is { } d2 ? $"{d2.TotalSeconds:F0} s" : "NOT within " + DrainBudget.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min")} after the writes stopped, "
            + $"{failed} of {finalTotal} creates ended Failed."
        );

        distribution.AchievedRate.ShouldBeGreaterThan(Rate * 0.9, $"the driver reached {distribution.AchievedRate:F0} writes/s of the {Rate:F0} asked for.");
        distribution.Errors.ShouldBeLessThan((int)(0.005 * (distribution.Samples + distribution.Errors)) + 1, "more than 0.5 % of writes failed: " + string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")));
        steady.Count.ShouldBeGreaterThanOrEqualTo(4, "too few queue samples after the first reminder period to fit a slope to.");
        drained.ShouldNotBeNull($"the reconcile queue did not drain to zero within {DrainBudget.TotalMinutes:F0} minutes of the writes stopping ({finalDepth} of {finalTotal} still not terminal).");
    }

    /// <summary>The number of this run's creates that are not terminal, and how many there are in total.</summary>
    async Task<(int Depth, int Total)> DepthAsync(string run, CancellationToken cancellationToken) {
        var depth = 0;
        var total = 0;

        foreach (var world in topology.Subscriptions) {
            await foreach (var resource in ListAsync(world, run, cancellationToken)) {
                total++;

                if (resource.ProvisioningState is ProvisioningState.Creating or ProvisioningState.Updating) {
                    depth++;
                }
            }
        }

        return (depth, total);
    }

    async Task<int> FailedAsync(string run, CancellationToken cancellationToken) {
        var failed = 0;

        foreach (var world in topology.Subscriptions) {
            await foreach (var resource in ListAsync(world, run, cancellationToken)) {
                if (resource.ProvisioningState == ProvisioningState.Failed) {
                    failed++;
                }
            }
        }

        return failed;
    }

    async IAsyncEnumerable<ResourceSnapshot> ListAsync(
        Chaos.Topology.TenantWorld world,
        string run,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    ) {
        var continuation = string.Empty;

        do {
            var page = await topology.Platform.Manager.ListAsync(
                new() {
                    Path = world.Widgets.Path,
                    ApiVersion = SampleWidgets.V2026,
                    Caller = world.Caller,
                    Top = ListRequest.MaxPageSize,
                    Continuation = continuation
                },
                cancellationToken
            );

            if (page.IsFailure) {
                yield break;
            }

            foreach (var resource in page.GetValueOrThrow().Resources) {
                if (resource.Name.StartsWith("load-" + run + "-", StringComparison.Ordinal)) {
                    yield return resource;
                }
            }

            continuation = page.GetValueOrThrow().Continuation;
        } while (continuation.Length > 0);
    }

    /// <summary>Least-squares slope of depth over time, in items per minute.</summary>
    static double Slope(List<(TimeSpan At, int Depth, int Total)> samples) {
        if (samples.Count < 2) {
            return 0;
        }

        var xs = samples.Select(x => x.At.TotalMinutes).ToArray();
        var ys = samples.Select(x => (double)x.Depth).ToArray();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var numerator = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));

        return denominator == 0 ? 0 : numerator / denominator;
    }
}
