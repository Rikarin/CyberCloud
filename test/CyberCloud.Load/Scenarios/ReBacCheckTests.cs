using CyberCloud.Authorization.Contracts;
using System.Diagnostics;
using System.Globalization;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     docs/plan/23 § The load scenarios, row 3:
///     <i>
///         ReBAC: 5-deep groups, 10 000 members, 20 000
///         checks/s → check p99 &lt; 10 ms warm, &lt; 50 ms cold
///     </i>, at a tenth of the members and the
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
///         ⚠ <b>Cold is a miss, and every sample is checked to be one.</b> The check grain's cache
///         is <c>IPersistentState</c> on the Hot tier, written after every walk, so deactivating the
///         grain does not empty it — the next activation reads it back from Redis and answers a
///         subject it has seen from the cache, in a millisecond, with <c>FromCache</c> true. The
///         first version of this test deactivated the grain and re-checked subjects the warm phase
///         had already cached, and its 9.7 ms "cold" p99 was five hundred Redis reads. So the cold
///         subjects are members the warm phase never asks about — in the innermost group like the
///         others, never checked before their one cold sample — and each sample is the first check
///         after the grain is deactivated, asserted <c>FromCache == false</c>, with the triples the
///         walk visited reported beside the number. What a cold check pays here: the activation
///         read back from the hot tier, the tenant version read, the walk through the relation
///         grains and the membership index, and the cache written. What it does not pay: the
///         relation grains' own activations, which stay warm across samples — the full-consistency
///         walk that re-reads every object's durable row and uses no index is measured too, as an
///         aside, so the row's reader has both numbers.
///     </para>
/// </remarks>
[Collection(LoadSuite.Name)]
public sealed class ReBacCheckTests(LoadTopology topology) {
    /// <summary>docs/plan/23's 20 000 checks/s at <see cref="LoadReport.Scale" />.</summary>
    const double Rate = 20_000 * LoadReport.Scale;

    /// <summary>docs/plan/23's 10 000 members at <see cref="LoadReport.Scale" />.</summary>
    const int Members = (int)(10_000 * LoadReport.Scale);

    const int Depth = 5;

    /// <summary>
    ///     Cold samples, each its own member. Five hundred rather than the first version's two
    ///     hundred: a nearest-rank p99 over 200 samples is the second-slowest sample, which is a
    ///     number about that one sample, and the trend rule in <c>Build.Load</c> fired on it.
    /// </summary>
    const int ColdSamples = 500;

    const int FullyConsistentSamples = 100;
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
        var groups = Enumerable.Range(0, Depth).Select(static i => $"load-g{i}").ToList();

        for (var i = 1; i < Depth; i++) {
            await WriteAsync(
                store,
                Tuple(
                    ObjectTypes.Group,
                    groups[i],
                    Relations.Member,
                    SubjectRef.Userset(ObjectTypes.Group, groups[i - 1], Relations.Member)
                )
            );
        }

        var users = Enumerable.Range(0, Members).Select(static i => $"load-member-{i}").ToList();

        // The cold subjects: members like the others, and never checked until their one sample.
        var coldUsers = Enumerable.Range(0, ColdSamples + FullyConsistentSamples)
            .Select(static i => $"load-cold-{i}")
            .ToList();

        foreach (var chunk in users.Concat(coldUsers).Chunk(50)) {
            await Task.WhenAll(
                chunk.Select(user => WriteAsync(
                        store,
                        Tuple(ObjectTypes.Group, groups[0], Relations.Member, SubjectRef.Of(SubjectTypes.User, user))
                    )
                )
            );
        }

        // The grant, on a resource group, to the outermost group's members.
        var target = AuthObjectRef.Create(
            ObjectTypes.ResourceGroup,
            world.Subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + world.Group
        )
            .GetValueOrThrow();
        await WriteAsync(
            store,
            RelationTuple.Create(
                target,
                Relations.Reader,
                SubjectRef.Userset(ObjectTypes.Group, groups[^1], Relations.Member)
            )
                .GetValueOrThrow()
        );

        var check = platform.For(world.Tenant).GetGrain<ICheckGrain>(GrainKeys.CheckCache(target.Type, target.Id));
        output?.WriteLine(
            $"graph of {Depth} groups and {Members + coldUsers.Count} members written in {build.Elapsed.TotalSeconds:F1} s"
        );

        // A check that must be true, so the walk is the whole walk.
        var probe = await check.CheckAsync(Permissions.Read, SubjectRef.Of(SubjectTypes.User, users[0]), null);
        probe.IsSuccess.ShouldBeTrue(probe.Error?.Message);
        probe.GetValueOrThrow()
            .Allowed.ShouldBeTrue(
                "a member of the innermost group is not a reader of the resource group; the chain is not what the row asks for."
            );

        // ── Warm: the rate, against the real check grain. ─────────────────────────────────────
        var driver = new OpenLoopDriver(Rate, WarmUp, Window, 4_000);

        var warm = await driver.RunAsync(
            async (i, _) => {
                var answer = await check.CheckAsync(
                    Permissions.Read,
                    SubjectRef.Of(SubjectTypes.User, users[i % users.Count]),
                    null
                );
                return answer.IsSuccess
                    ? answer.GetValueOrThrow().Allowed ? null : "denied"
                    : answer.Error!.Code.ToString();
            },
            token
        );

        output?.WriteLine("warm: " + warm);

        // ── Cold: one deactivation and one never-checked subject per sample. ──────────────────
        var cold = new List<double>();
        var coldErrors = 0;
        var coldFromCache = 0;
        var coldTriples = new List<int>();
        var coldClock = Stopwatch.StartNew();

        for (var i = 0; i < ColdSamples; i++) {
            await check.DeactivateAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);

            var started = Stopwatch.GetTimestamp();
            var answer = await check.CheckAsync(Permissions.Read, SubjectRef.Of(SubjectTypes.User, coldUsers[i]), null);
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (!answer.IsSuccess || !answer.GetValueOrThrow().Allowed) {
                coldErrors++;
                continue;
            }

            var result = answer.GetValueOrThrow();

            if (result.FromCache) {
                // ⚠ Not a cold sample. The number goes nowhere, and the count fails the test.
                coldFromCache++;
                continue;
            }

            cold.Add(took);
            coldTriples.Add(result.TriplesVisited);
        }

        var coldDistribution = Distribution.Of(cold, coldErrors + coldFromCache, coldClock.Elapsed, 0);
        output?.WriteLine(
            $"cold: {coldDistribution}; {coldFromCache} answered from the cache; triples visited {(coldTriples.Count == 0 ? 0 : coldTriples.Min())}–{(coldTriples.Count == 0 ? 0 : coldTriples.Max())}"
        );

        // ── The aside: the full-consistency walk, no cache, no index, every durable row re-read. ──
        var fully = new List<double>();
        var fullyErrors = 0;

        for (var i = 0; i < FullyConsistentSamples; i++) {
            var started = Stopwatch.GetTimestamp();
            var answer = await check.CheckAsync(
                Permissions.Read,
                SubjectRef.Of(SubjectTypes.User, coldUsers[ColdSamples + i]),
                Consistency.FullyConsistent
            );
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (answer.IsSuccess && answer.GetValueOrThrow().Allowed && !answer.GetValueOrThrow().FromCache) {
                fully.Add(took);
            } else {
                fullyErrors++;
            }
        }

        var fullyDistribution = Distribution.Of(fully, fullyErrors, TimeSpan.Zero, 0);
        output?.WriteLine("fully consistent: " + fullyDistribution);

        topology.Report.Measured(WarmMetric, warm.P99, warm);
        topology.Report.Measured(ColdMetric, coldDistribution.P99, coldDistribution);
        topology.Report.Aside(WarmMetric, "groupsDeep", Depth);
        topology.Report.Aside(WarmMetric, "members", Members);
        topology.Report.Aside(WarmMetric, "graphBuildSeconds", Math.Round(build.Elapsed.TotalSeconds, 1));
        topology.Report.Aside(ColdMetric, "coldMembers", coldUsers.Count);
        topology.Report.Aside(ColdMetric, "samplesAnsweredFromCache", coldFromCache);
        topology.Report.Aside(ColdMetric, "triplesVisitedMin", coldTriples.Count == 0 ? 0 : coldTriples.Min());
        topology.Report.Aside(ColdMetric, "triplesVisitedMax", coldTriples.Count == 0 ? 0 : coldTriples.Max());
        topology.Report.Aside(ColdMetric, "fullyConsistentP50Ms", Math.Round(fullyDistribution.P50, 3));
        topology.Report.Aside(ColdMetric, "fullyConsistentP99Ms", Math.Round(fullyDistribution.P99, 3));
        topology.Report.Aside(ColdMetric, "fullyConsistentSamples", fully.Count);

        topology.Report.Note(
            WarmMetric,
            $"ICheckGrain.CheckAsync(read) over a {Depth}-deep group chain with {Members} members (a tenth of the row's 10 000) at {Rate.ToString("0", CultureInfo.InvariantCulture)} checks/s "
            + $"(a tenth of 20 000) for {Window.TotalSeconds:F0} s from the cluster client; failures: "
            + (driver.Failures.IsEmpty
                    ? "none"
                    : string.Join(", ", driver.Failures.Select(static x => $"{x.Value} × {x.Key}")))
        );

        topology.Report.Note(
            ColdMetric,
            $"the first check after the check grain is deactivated, for a member the cache has never held, {ColdSamples} times, each asserted FromCache == false "
            + $"({coldFromCache} were not); the activation is read back from the hot tier and the chain walked through the relation grains and the membership index, "
            + $"{(coldTriples.Count == 0 ? 0 : coldTriples.Min())}–{(coldTriples.Count == 0 ? 0 : coldTriples.Max())} triples per walk. "
            + $"The full-consistency walk — no cache, no index, every object's durable row re-read — is p50 {fullyDistribution.P50:F1} / p99 {fullyDistribution.P99:F1} ms over {fully.Count} samples."
        );

        warm.AchievedRate.ShouldBeGreaterThan(
            Rate * 0.9,
            $"the driver reached {warm.AchievedRate:F0} checks/s of the {Rate:F0} asked for."
        );
        warm.Errors.ShouldBe(
            0,
            "checks failed or denied: " + string.Join(", ", driver.Failures.Select(static x => $"{x.Value} × {x.Key}"))
        );
        coldErrors.ShouldBe(0, "cold checks failed or denied.");
        coldFromCache.ShouldBe(
            0,
            $"{coldFromCache} of {ColdSamples} cold samples were answered from the check grain's persisted cache, so they were not cold and the p99 is not a cold number."
        );
        fullyErrors.ShouldBe(0, "fully consistent checks failed, were denied, or came from the cache.");
    }

    static RelationTuple Tuple(string objectType, string objectId, string relation, SubjectRef subject) =>
        RelationTuple.Create(AuthObjectRef.Create(objectType, objectId).GetValueOrThrow(), relation, subject)
            .GetValueOrThrow();

    static async Task WriteAsync(ITupleStoreGrain store, RelationTuple tuple) {
        var written = await store.WriteAsync(tuple);
        written.IsSuccess.ShouldBeTrue($"{tuple} could not be written: {written.Error?.Message}");
    }
}
