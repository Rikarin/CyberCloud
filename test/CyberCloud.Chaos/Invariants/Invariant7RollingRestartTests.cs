using CyberCloud.Chaos.Topology;
using System.Diagnostics;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 7: <i>rolling upgrade of a 30-silo cluster under load →
///     zero failed tenant requests.</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Measured at three silos and one version, and reported as ○ for that reason unless it
///         fails.</b> The machine hosts three silos, not thirty, and there is one build of the silo,
///         not two, so what runs here is a rolling <i>restart</i>: every secondary silo is stopped
///         gracefully and started again, one at a time, while tenant traffic — reads, creates, and
///         the passes that converge them — runs against the cluster. That is the mechanism a rolling
///         upgrade exercises (activations leaving a silo that is going away, calls landing on a silo
///         that is not there yet) at a scale that cannot show the thirty-silo effects docs/plan/00
///         § The quality bar is about. A failed request at three silos is still a failed request, so
///         a nonzero count is a violation; a zero is not a pass, it is a small number.
///     </para>
///     <para>
///         ⚠ <b>The primary is not restarted.</b> Under the testing host's membership it holds the
///         table, and stopping it is a different fault. A real rolling upgrade restarts every pod.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant7RollingRestartTests(ChaosTopology topology) {
    const int Seeded = 6;
    const int Readers = 4;
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Invariant7_ARollingRestartOfEverySecondarySiloUnderLoadFailsNoTenantRequest() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var world = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardA), "rolling", token);

        for (var i = 0; i < Seeded; i++) {
            var accepted = (await topology.PutWidgetAsync(world, $"roll-{i}", "v1", token)).GetValueOrThrow();
            (await topology.DriveUntilTerminalAsync(world.Tenant, accepted.OperationId, ConvergeBudget, token)).Last?.State.ShouldBe(OperationState.Succeeded);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var requests = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var createsAccepted = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        // ── The load: readers hammering existing widgets, a writer creating new ones. ──────────
        var load = Enumerable.Range(0, Readers).Select(reader => Task.Run(async () => {
                    var i = 0;

                    while (!stop.IsCancellationRequested) {
                        i++;
                        var name = $"roll-{(reader + i) % Seeded}";

                        try {
                            Interlocked.Increment(ref requests);
                            var read = await topology.ReadWidgetAsync(world, name, token);

                            if (read.IsFailure) {
                                failures.Add($"read {name}: {read.Error!.Code} — {read.Error.Message}");
                            }

                            if (reader == 0 && i % 5 == 0) {
                                // ⚠ A CREATE of a fresh name, not an update of an existing widget.
                                // The first run of this test updated the seeded widgets and counted
                                // 27 failures that were all OperationInProgress — the write path
                                // refusing a second change to a resource whose previous change was
                                // still being reconciled, which is docs/plan/08 § Long-running
                                // operations doing its job and not a failed request. A fresh name
                                // has no operation in flight to collide with.
                                Interlocked.Increment(ref requests);
                                var write = await topology.PutWidgetAsync(world, $"roll-new-{i}", "v1", token);

                                if (write.IsFailure) {
                                    failures.Add($"create roll-new-{i}: {write.Error!.Code} — {write.Error.Message}");
                                } else if (!write.GetValueOrThrow().NoOp) {
                                    createsAccepted.Add(write.GetValueOrThrow().OperationId);
                                }
                            }
                        } catch (Exception ex) when (ex is not OperationCanceledException) {
                            failures.Add($"{name}: {ex.GetType().Name}: {Shorten(ex.Message)}");
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
                    }
                }, CancellationToken.None)
            )
            .ToList();

        // ── The rolling restart, one secondary at a time. ─────────────────────────────────────
        var rolling = Stopwatch.StartNew();
        var restarts = new List<(SiloAddress Old, SiloAddress New, TimeSpan Took, TimeSpan StopTook)>();

        try {
            foreach (var silo in topology.SecondarySilos) {
                var step = Stopwatch.StartNew();
                var (replacement, stopTook) = await topology.RestartSiloAsync(silo);
                restarts.Add((silo, replacement, step.Elapsed, stopTook));
                output?.WriteLine($"[{rolling.Elapsed.TotalSeconds:F1}s] {silo} → {replacement} in {step.Elapsed.TotalSeconds:F1} s (graceful stop {stopTook.TotalSeconds:F1} s)");

                // Traffic keeps flowing on the new membership before the next one goes.
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        } finally {
            // The readers stop and the cluster is back to strength whatever the restart did —
            // a stop that threw between StopSiloAsync and the replacement would otherwise hand the
            // next invariant a two-silo cluster and four readers still hammering it.
            await stop.CancelAsync();
            await topology.RestoreClusterStrengthAsync();
        }

        await Task.WhenAll(load);
        var loadLength = rolling.Elapsed;

        // Everything accepted converges on the cluster as it stands now.
        var notConverged = new List<string>();

        foreach (var operationId in createsAccepted) {
            var (last, _) = await topology.DriveUntilTerminalAsync(world.Tenant, operationId, ConvergeBudget, token);

            if (last?.State != OperationState.Succeeded) {
                notConverged.Add($"{operationId:N} → {last?.State.ToString() ?? "never answered"}: {last?.Error?.Message}");
            }
        }

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["silos"] = topology.Silos.Count,
            ["silosRestarted"] = restarts.Count,
            ["versions"] = 1,
            ["docSilos"] = 30,
            ["loadSeconds"] = Math.Round(loadLength.TotalSeconds, 1),
            ["requests"] = requests,
            ["requestsPerSecond"] = Math.Round(requests / Math.Max(1, loadLength.TotalSeconds), 1),
            ["failedRequests"] = failures.Count,
            ["createsAccepted"] = createsAccepted.Count,
            ["createsNotConverged"] = notConverged.Count,
            ["restartSecondsMax"] = Math.Round(restarts.Count == 0 ? 0 : restarts.Max(x => x.Took.TotalSeconds), 1),
            ["gracefulStopSecondsMax"] = Math.Round(restarts.Count == 0 ? 0 : restarts.Max(x => x.StopTook.TotalSeconds), 1)
        };

        var detail =
            $"{restarts.Count} of {topology.Silos.Count} silos restarted gracefully one at a time (slowest {(restarts.Count == 0 ? 0 : restarts.Max(x => x.Took.TotalSeconds)):F1} s, of which the graceful stop "
            + $"{(restarts.Count == 0 ? 0 : restarts.Max(x => x.StopTook.TotalSeconds)):F1} s) "
            + $"under {requests} tenant requests over {loadLength.TotalSeconds:F0} s ({requests / Math.Max(1, loadLength.TotalSeconds):F0} rps): "
            + $"{failures.Count} failed, {createsAccepted.Count} creates accepted, {notConverged.Count} did not converge. "
            + "⚠ 3 silos and one version, not the 30 and two the invariant names — a zero here is a small number, not the number.";

        if (failures.IsEmpty && notConverged.Count == 0) {
            topology.Report.Vacuous(7, detail, numbers);
        } else {
            topology.Report.Violated(7, detail, numbers);
        }

        failures.ShouldBeEmpty(
            "docs/plan/23 § The chaos invariants, 7: zero failed tenant requests during a rolling restart. Failed: "
            + string.Join("; ", failures.Take(10))
        );

        notConverged.ShouldBeEmpty("creates accepted during the rolling restart did not converge: " + string.Join("; ", notConverged));

        Assert.Skip(
            $"VACUOUS at this scale — {detail} The row is ○ in build/Build.Chaos.cs; the thirty-silo, two-version run is the "
            + "staging environment's, docs/plan/23 § Environments and rollout."
        );
    }

    static string Shorten(string message) => message.Length <= 200 ? message : message[..200] + "…";
}
