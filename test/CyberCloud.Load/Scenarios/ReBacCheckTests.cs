using CyberCloud.Authorization.Contracts;
using CyberCloud.Chaos.Topology;
using System.Diagnostics;
using System.Globalization;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, row 3: <i>ReBAC: 5-deep groups, 10 000 members, 20 000
///     checks/s → check p99 &lt; 10 ms warm, &lt; 50 ms cold</i>, at a tenth of the members and the
///     rate. docs/plan/25 § R5 is the risk this row measures.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The graph is the pathological one the row names</b>: a chain of five groups, each a
///         member of the next, a thousand users in the innermost, and the outermost granted
///         <c>reader</c> on a resource group — so a check for one user has to walk five usersets
///         and a membership of a thousand before it answers. The checks go to the real
///         <c>ICheckGrain</c> through the cluster client, which is the call
///         <c>ReBacResourceAuthorizer</c> makes on every request; what a request adds on top is the
///         read scenario's number.
///     </para>
///     <para>
///         ⚠ <b>Cold is measured, not inferred.</b> A cold check is the first one after the check
///         grain has been deactivated — its cache gone, the tuples re-read from the durable tier,
///         the chain re-walked. One deactivation per cold sample, a few hundred samples; the
///         distribution is over those and nothing warm leaks into it.
///     </para>
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class ReBacCheckTests(LoadTopology topology) {
    /// <summary>docs/plan/23's 20 000 checks/s at <see cref="LoadReport.Scale" />.</summary>
    const double Rate = 20_000 * LoadReport.Scale;

    /// <summary>docs/plan/23's 10 000 members at <see cref="LoadReport.Scale" />.</summary>
    const int Members = (int)(10_000 * LoadReport.Scale);

    const int Depth = 5;
    const int ColdSamples = 200;
    const string WarmMetric = "rebac-check-p99-warm-ms";
    const string ColdMetric = "rebac-check-p99-cold-ms";
    static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ReBacChecksOverAFiveDeepThousandMemberGraphWarmAndCold() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var world = topology.Subscriptions[0];
        var platform = topology.Platform;
        var store = platform.For(world.Tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(world.Tenant));

        // ── The graph. ────────────────────────────────────────────────────────────────────────
        var build = Stopwatch.StartNew();
        var groups = Enumerable.Range(0, Depth).Select(i => $"load-g{i}").ToList();

        for (var i = 1; i < Depth; i++) {
            await WriteAsync(store, Tuple(ObjectTypes.Group, groups[i], Relations.Member, SubjectRef.Userset(ObjectTypes.Group, groups[i - 1], Relations.Member)));
        }

        var users = Enumerable.Range(0, Members).Select(i => $"load-member-{i}").ToList();

        foreach (var chunk in users.Chunk(50)) {
            await Task.WhenAll(chunk.Select(user => WriteAsync(store, Tuple(ObjectTypes.Group, groups[0], Relations.Member, SubjectRef.Of(SubjectTypes.User, user)))));
        }

        // The grant, on a resource group, to the outermost group's members.
        var target = AuthObjectRef.Create(ObjectTypes.ResourceGroup, world.Subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + world.Group).GetValueOrThrow();
        await WriteAsync(store, RelationTuple.Create(target, Relations.Reader, SubjectRef.Userset(ObjectTypes.Group, groups[^1], Relations.Member)).GetValueOrThrow());

        var check = platform.For(world.Tenant).GetGrain<ICheckGrain>(GrainKeys.CheckCache(target.Type, target.Id));
        output?.WriteLine($"graph of {Depth} groups and {Members} members written in {build.Elapsed.TotalSeconds:F1} s");

        // A check that must be true, so the walk is the whole walk.
        var probe = await check.CheckAsync(Permissions.Read, SubjectRef.Of(SubjectTypes.User, users[0]), null);
        probe.IsSuccess.ShouldBeTrue(probe.Error?.Message);
        probe.GetValueOrThrow().Allowed.ShouldBeTrue("a member of the innermost group is not a reader of the resource group; the chain is not what the row asks for.");

        // ── Warm: the rate, against the real check grain. ─────────────────────────────────────
        var driver = new OpenLoopDriver(Rate, WarmUp, Window, inFlightCap: 4_000);

        var warm = await driver.RunAsync(async (i, _) => {
                var answer = await check.CheckAsync(Permissions.Read, SubjectRef.Of(SubjectTypes.User, users[i % users.Count]), null);
                return answer.IsSuccess ? answer.GetValueOrThrow().Allowed ? null : "denied" : answer.Error!.Code.ToString();
            },
            token
        );

        output?.WriteLine("warm: " + warm);

        // ── Cold: one deactivation per sample. ────────────────────────────────────────────────
        var cold = new List<double>();
        var coldErrors = 0;
        var coldClock = Stopwatch.StartNew();

        for (var i = 0; i < ColdSamples; i++) {
            await check.DeactivateAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);

            var started = Stopwatch.GetTimestamp();
            var answer = await check.CheckAsync(Permissions.Read, SubjectRef.Of(SubjectTypes.User, users[(i * 7) % users.Count]), null);
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (answer.IsSuccess && answer.GetValueOrThrow().Allowed) {
                cold.Add(took);
            } else {
                coldErrors++;
            }
        }

        var coldDistribution = Distribution.Of(cold, coldErrors, coldClock.Elapsed, 0);
        output?.WriteLine("cold: " + coldDistribution);

        topology.Report.Measured(WarmMetric, warm.P99, warm);
        topology.Report.Measured(ColdMetric, coldDistribution.P99, coldDistribution);
        topology.Report.Aside(WarmMetric, "groupsDeep", Depth);
        topology.Report.Aside(WarmMetric, "members", Members);
        topology.Report.Aside(WarmMetric, "graphBuildSeconds", Math.Round(build.Elapsed.TotalSeconds, 1));

        topology.Report.Note(
            WarmMetric,
            $"ICheckGrain.CheckAsync(read) over a {Depth}-deep group chain with {Members} members (a tenth of the row's 10 000) at {Rate.ToString("0", CultureInfo.InvariantCulture)} checks/s "
            + $"(a tenth of 20 000) for {Window.TotalSeconds:F0} s from the cluster client; failures: "
            + (driver.Failures.IsEmpty ? "none" : string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")))
        );

        topology.Report.Note(ColdMetric, $"the first check after the check grain is deactivated, {ColdSamples} times; each re-reads the tuples and walks the chain.");

        warm.AchievedRate.ShouldBeGreaterThan(Rate * 0.9, $"the driver reached {warm.AchievedRate:F0} checks/s of the {Rate:F0} asked for.");
        warm.Errors.ShouldBe(0, "checks failed or denied: " + string.Join(", ", driver.Failures.Select(x => $"{x.Value} × {x.Key}")));
        coldErrors.ShouldBe(0, "cold checks failed or denied.");
    }

    static RelationTuple Tuple(string objectType, string objectId, string relation, SubjectRef subject) =>
        RelationTuple.Create(AuthObjectRef.Create(objectType, objectId).GetValueOrThrow(), relation, subject).GetValueOrThrow();

    static async Task WriteAsync(ITupleStoreGrain store, RelationTuple tuple) {
        var written = await store.WriteAsync(tuple);
        written.IsSuccess.ShouldBeTrue($"{tuple} could not be written: {written.Error?.Message}");
    }
}
