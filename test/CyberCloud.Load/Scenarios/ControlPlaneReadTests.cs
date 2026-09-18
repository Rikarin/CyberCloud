using System.Globalization;
using System.Net;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, row 1: <i>10 000 tenants, 1 000 000 resources, 5 000 rps
///     reads → control-plane read p99 &lt; 25 ms</i>, driven at a tenth of the rate through the real
///     gateway over HTTP.
/// </summary>
/// <remarks>
///     ⚠ <b>The read is a full request</b>: Kestrel, the eight stages — correlation, JWKS bearer
///     validation, tenant resolution against the directory cache, region routing, the rate limiter,
///     routing, validation, dispatch — the real <c>ResourceManagerService.ReadAsync</c> with the real
///     ReBAC check, a grain call to the resource, and the body written back. What it is not is a
///     million resources and ten thousand tenants: the population is <see cref="LoadTopology" />'s,
///     and the results file says so.
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class ControlPlaneReadTests(LoadTopology topology) {
    /// <summary>docs/plan/23's 5 000 rps at <see cref="LoadReport.Scale" />.</summary>
    const double Rate = 5_000 * LoadReport.Scale;

    const string Metric = "control-plane-read-p99-ms";
    static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ControlPlaneReadsAtATenthOfTargetRateThroughTheRealGateway() {
        var token = TestContext.Current.CancellationToken;
        var subscriptions = topology.Subscriptions;
        var callersBySubscription = subscriptions.ToDictionary(x => x.Subscription, x => topology.CallersOf(x));

        var driver = new OpenLoopDriver(Rate, WarmUp, Window);

        var distribution = await driver.RunAsync(async (i, ct) => {
                // Round-robin over subscriptions and callers so no bucket of the rate limiter is
                // asked for more than its share, and every read is a different widget from the last.
                var world = subscriptions[i % subscriptions.Count];
                var callers = callersBySubscription[world.Subscription];
                var caller = callers[(i / subscriptions.Count) % callers.Count];
                var widgets = topology.Widgets[world.Subscription];
                var name = widgets[(i / (subscriptions.Count * callers.Count)) % widgets.Count];

                using var response = await topology.Http.SendAsync(topology.Get(caller, world, name), HttpCompletionOption.ResponseContentRead, ct);

                return response.StatusCode == HttpStatusCode.OK
                    ? null
                    : $"HTTP {(int)response.StatusCode}";
            },
            token
        );

        TestContext.Current.TestOutputHelper?.WriteLine(distribution.ToString());

        foreach (var (reason, count) in driver.Failures) {
            TestContext.Current.TestOutputHelper?.WriteLine($"  {count} × {reason}");
        }

        topology.Report.Measured(Metric, distribution.P99, distribution);
        topology.Report.Aside(Metric, "worstMsPerTenSeconds", string.Join(" ", driver.WorstPer(TimeSpan.FromSeconds(10)).Select(x => x.ToString("0", CultureInfo.InvariantCulture))));
        topology.Report.Aside(Metric, "errorRate", distribution.Samples + distribution.Errors == 0 ? 0 : Math.Round((double)distribution.Errors / (distribution.Samples + distribution.Errors), 4));

        topology.Report.Note(
            Metric,
            $"GET of a converged widget through CyberCloud.Gateway.Host over loopback HTTP at {Rate.ToString("0", CultureInfo.InvariantCulture)} rps "
            + $"(a tenth of the row's 5 000) for {Window.TotalSeconds:F0} s after {WarmUp.TotalSeconds:F0} s of warm-up, round-robin over "
            + $"{subscriptions.Count} subscriptions and {topology.Callers.Count} service-principal callers; failures: "
            + (driver.Failures.IsEmpty ? "none" : string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")))
        );

        // ⚠ Not the budget — the build gates the budget and prints the trend. What this asserts is
        // that the number is a measurement of the thing: enough of the asked-for rate was reached,
        // and few enough requests failed, that the p99 is over the right population.
        distribution.AchievedRate.ShouldBeGreaterThan(Rate * 0.9, $"the driver reached {distribution.AchievedRate:F0} rps of the {Rate:F0} asked for, so this is not the row's rate.");
        distribution.Errors.ShouldBeLessThan((int)(0.005 * (distribution.Samples + distribution.Errors)) + 1, "more than 0.5 % of reads failed: " + string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")));
    }
}
