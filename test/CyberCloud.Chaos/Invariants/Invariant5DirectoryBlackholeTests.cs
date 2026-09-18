using CyberCloud.Chaos.Topology;
using System.Diagnostics;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 5: <i>blackhole the global directory cluster for 10
///     minutes → zero tenant-facing errors; new tenant creation fails cleanly with a retryable
///     error.</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The blackhole is the platform shard stopped</b> — <c>platform-00</c>, where every
///         null-tenant grain lives: the tenant directory, the shard map, and the cluster connections.
///         That is the same fault <c>TenantDirectoryBlackholeTests</c> induces in
///         <c>CyberCloud.Tenancy.Tests</c>, for the reasons that class gives; this one runs it through
///         the whole write path rather than the tenancy grains alone, and asks the second clause the
///         other cannot: what a new tenant's creation answers, through
///         <c>IScopeManager.CreateTenantAsync</c>.
///     </para>
///     <para>
///         ⚠ <b>Ten minutes is compressed to a working window, and the compression is safe because
///         the property is TTL-less.</b> docs/plan/05 § The tenant directory has every silo keep a
///         snapshot with no expiry, so a directory that is gone for ten minutes and one that is gone
///         for one differ in nothing but the clock; the window here is long enough for every silo's
///         refresh service to fail at least once and be seen failing.
///     </para>
///     <para>
///         ⚠ <b>Every activation is collected first</b>, so the tenant-facing traffic during the
///         blackhole re-reads the tenant shards — which are up — and anything that quietly depended
///         on the platform shard has to go there and find it gone. Nothing is proven by an
///         activation that never needed to read anything.
///     </para>
///     <para>
///         ⚠ <b>"Fails cleanly with a retryable error" is asserted as a <c>Result</c> failure whose
///         HTTP status is 5xx or 429</b>, because that is what a gateway turns into a response a
///         client library retries. An exception escaping the scope manager is neither clean nor
///         retryable: the gateway answers 500 with no detail and the client has no idea whether to
///         try again.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant5DirectoryBlackholeTests(ChaosTopology topology) {
    /// <summary>Rounds of tenant-facing traffic while the directory is gone. Each is a read, a scope read and a write per tenant.</summary>
    const int Rounds = 8;

    /// <summary>The longest one tenant-facing call may take before it counts as an error. Orleans' own response timeout is 30 s.</summary>
    static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(35);

    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Invariant5_BlackholingTheGlobalDirectoryLeavesExistingTenantsWorkingAndRefusesNewOnesCleanly() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;

        var a = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardA), "dir-a", token);
        var b = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardB), "dir-b", token);

        foreach (var world in new[] { a, b }) {
            var seeded = (await topology.PutWidgetAsync(world, "existing", "before", token)).GetValueOrThrow();
            (await topology.DriveUntilTerminalAsync(world.Tenant, seeded.OperationId, ConvergeBudget, token)).Last?.State.ShouldBe(OperationState.Succeeded);
        }

        // ── The blackhole. ────────────────────────────────────────────────────────────────────
        var reads = 0;
        var writes = 0;
        var drives = 0;
        var errors = new List<string>();
        var accepted = new List<(TenantWorld World, Guid OperationId)>();
        var round = 0;
        List<string> notConverged;
        string newTenantOutcome;
        var cleanAndRetryable = false;
        TimeSpan newTenantTook;

        await topology.StopShardAsync(ChaosTopology.PlatformShard, token);
        var blackhole = Stopwatch.StartNew();

        try {
            await topology.DeactivateEverythingAsync();

            while (round < Rounds) {
                round++;

                foreach (var world in new[] { a, b }) {
                    try {
                        reads++;
                        var read = await topology.ReadWidgetAsync(world, "existing", token).WaitAsync(CallBudget, token);

                        if (read.IsFailure) {
                            errors.Add($"read {world.Group}/existing: {read.Error!.Code} — {read.Error.Message}");
                        }

                        reads++;
                        var group = await topology.ReadGroupAsync(world, token).WaitAsync(CallBudget, token);

                        if (group.IsFailure) {
                            errors.Add($"read {world.Group}: {group.Error!.Code} — {group.Error.Message}");
                        }

                        writes++;
                        var write = await topology.PutWidgetAsync(world, $"during-{round}", "during", token).WaitAsync(CallBudget, token);

                        if (write.IsFailure) {
                            errors.Add($"write {world.Group}/during-{round}: {write.Error!.Code} — {write.Error.Message}");
                        } else {
                            accepted.Add((world, write.GetValueOrThrow().OperationId));
                        }
                    } catch (Exception ex) when (ex is not OperationCanceledException || ex is TimeoutException) {
                        errors.Add($"{world.Group} round {round}: {ex.GetType().Name}: {Shorten(ex.Message)}");
                    }
                }

                Console.WriteLine($"[CyberCloud.Chaos] invariant 5 round {round}/{Rounds} at {blackhole.Elapsed.TotalSeconds:F0} s: {errors.Count} errors so far");
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }

            // The accepted writes are driven while the directory is still gone: a reconcile needs the
            // cluster connection, which is a null-tenant grain on the stopped shard — so this measures
            // whether "tenant-facing" reaches the data plane. Driven together, under one budget.
            var duringBlackhole = await Task.WhenAll(
                accepted.Select(async x => {
                        var (last, faults) = await topology.DriveUntilTerminalAsync(x.World.Tenant, x.OperationId, TimeSpan.FromSeconds(40), token);
                        return (x.World, x.OperationId, Last: last, Faults: faults);
                    }
                )
            );

            drives = duringBlackhole.Length;

            notConverged = duringBlackhole
                .Where(x => x.Last?.State != OperationState.Succeeded)
                .Select(x => $"{x.World.Group}/{x.OperationId:N} → {x.Last?.State.ToString() ?? "never answered"} after {x.Faults} faults: {x.Last?.Error?.Message ?? x.Last?.LastProgress?.Detail}")
                .ToList();

            Console.WriteLine($"[CyberCloud.Chaos] invariant 5: {accepted.Count - notConverged.Count}/{accepted.Count} accepted writes converged while the directory was gone");

            // ── New tenant creation, through the platform path. ───────────────────────────────────
            var attempt = Stopwatch.StartNew();

            try {
                var created = await topology.TryCreateTenantAsync(Guid.NewGuid(), "dir-new", token).WaitAsync(TimeSpan.FromSeconds(90), token);

                if (created.IsSuccess) {
                    newTenantOutcome = "ACCEPTED — a tenant was created with no directory to register it in";
                } else {
                    var error = created.Error!;
                    var status = error.Code.HttpStatus;
                    cleanAndRetryable = status is >= 500 and < 600 or 429;
                    newTenantOutcome = $"Result failure {error.Code} (HTTP {status}): {error.Message}";
                }
            } catch (TimeoutException) {
                newTenantOutcome = "NO ANSWER within 90 s — the call hung rather than failing";
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                newTenantOutcome = $"EXCEPTION {ex.GetType().Name}: {Shorten(ex.Message)}";
            }

            newTenantTook = attempt.Elapsed;
            output?.WriteLine($"new tenant during blackhole: {newTenantOutcome} ({newTenantTook.TotalSeconds:F1} s)");
        } finally {
            // ── Restore — in a finally, so the platform shard comes back whatever the blackhole did to this test. ──
            await topology.StartShardAsync(ChaosTopology.PlatformShard, token);
        }

        // ── A new tenant is possible again. ───────────────────────────────────────────────────
        var restore = Stopwatch.StartNew();
        TimeSpan? newTenantAfter = null;

        while (restore.Elapsed < TimeSpan.FromMinutes(2)) {
            try {
                if ((await topology.TryCreateTenantAsync(Guid.NewGuid(), "dir-after", token)).IsSuccess) {
                    newTenantAfter = restore.Elapsed;
                    break;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Still coming back.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        // Whatever did not converge during the blackhole must converge now — the directory being
        // gone is allowed to pause the data plane, never to lose an accepted write.
        var afterRestore = await Task.WhenAll(
            accepted.Select(async x => {
                    var (last, _) = await topology.DriveUntilTerminalAsync(x.World.Tenant, x.OperationId, ConvergeBudget, token);
                    return (x.World, x.OperationId, Last: last);
                }
            )
        );

        var lostAfterRestore = afterRestore
            .Where(x => x.Last?.State != OperationState.Succeeded)
            .Select(x => $"{x.World.Group}/{x.OperationId:N} → {x.Last?.State.ToString() ?? "never answered"}: {x.Last?.Error?.Message}")
            .ToList();

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["windowSeconds"] = Math.Round(blackhole.Elapsed.TotalSeconds, 1),
            ["rounds"] = round,
            ["reads"] = reads,
            ["writes"] = writes,
            ["writesAccepted"] = accepted.Count,
            ["tenantFacingErrors"] = errors.Count,
            ["acceptedNotConvergedDuringBlackhole"] = notConverged.Count,
            ["acceptedLostAfterRestore"] = lostAfterRestore.Count,
            ["newTenantCleanRetryable"] = cleanAndRetryable ? 1 : 0,
            ["newTenantAnswerSeconds"] = Math.Round(newTenantTook.TotalSeconds, 1),
            ["newTenantAfterRestoreSeconds"] = newTenantAfter is { } t ? Math.Round(t.TotalSeconds, 1) : -1
        };

        var detail =
            $"platform shard stopped for {blackhole.Elapsed.TotalSeconds:F0} s: {reads} reads and {writes} writes for 2 existing tenants "
            + $"produced {errors.Count} errors ({accepted.Count} writes accepted, {notConverged.Count} of them did not converge while the "
            + $"directory was gone, {lostAfterRestore.Count} lost after restore); new tenant creation answered in {newTenantTook.TotalSeconds:F1} s "
            + $"with: {newTenantOutcome}; a new tenant was possible again {(newTenantAfter is { } t2 ? $"{t2.TotalSeconds:F1} s" : "NEVER")} after the shard came back.";

        var held = errors.Count == 0 && lostAfterRestore.Count == 0 && cleanAndRetryable && newTenantAfter is not null;

        if (held) {
            topology.Report.Held(5, detail, numbers);
        } else {
            topology.Report.Violated(5, detail, numbers);
        }

        errors.ShouldBeEmpty(
            "docs/plan/23 § The chaos invariants, 5: zero tenant-facing errors while the global directory is gone. Found: "
            + string.Join("; ", errors.Take(10))
        );

        lostAfterRestore.ShouldBeEmpty("accepted writes did not converge after the directory came back: " + string.Join("; ", lostAfterRestore));

        cleanAndRetryable.ShouldBeTrue(
            "docs/plan/23 § The chaos invariants, 5: new tenant creation fails cleanly with a retryable error. It answered: "
            + newTenantOutcome
        );

        newTenantAfter.ShouldNotBeNull("no new tenant could be created within two minutes of the platform shard coming back.");
    }

    static string Shorten(string message) => message.Length <= 300 ? message : message[..300] + "…";
}
