using CyberCloud.Chaos.Topology;
using System.Diagnostics;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 2: <i>FLUSHALL the hot tier → zero durable state lost,
///     zero acknowledged control-plane writes lost, full function within 60 s.</i> docs/plan/25 § R2
///     names this run as the thing that makes ADR-003's two tiers true "in month fourteen".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The flush is a real <c>FLUSHALL</c> on the Redis every silo's hot tier and reminder
///         table live in</b>, and the reads afterwards come out of PostgreSQL rather than out of an
///         activation's memory, because every idle activation is collected first. Without that step
///         the assertion would be that an in-memory object survived a Redis command, which nobody
///         doubted.
///     </para>
///     <para>
///         ⚠ <b>The reminder table is in the flushed Redis, and the test says what that costs.</b>
///         <c>SiloComposition.ConfigureStorage</c> puts <c>UseRedisReminderService</c> on the hot
///         tier's connection string, so a FLUSHALL of the hot tier also empties every operation's
///         safety net. An operation accepted before the flush and never driven is the case: its
///         reminder row is gone, and what brings it back is the next pass — which registers the
///         reminder again — or the reminder service's local copy ticking before its table refresh.
///         The row count before and after is reported rather than asserted away, and the in-flight
///         operation is driven to completion, which is the "acknowledged write not lost" half.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant2HotTierFlushTests(ChaosTopology topology) {
    const int Seeded = 8;
    static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Invariant2_FlushingTheHotTierLosesNoDurableStateAndIsWholeAgainWithinSixtySeconds() {
        var token = TestContext.Current.CancellationToken;
        var world = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardA), "flush", token);

        // ── Before: durable state, acknowledged and converged. ────────────────────────────────
        var seeded = new Dictionary<string, ResourceSnapshot>(StringComparer.Ordinal);

        for (var i = 0; i < Seeded; i++) {
            var name = $"flush-{i}";
            var accepted = (await topology.PutWidgetAsync(world, name, $"before-{i}", token)).GetValueOrThrow();
            var (last, _) = await topology.DriveUntilTerminalAsync(world.Tenant, accepted.OperationId, ConvergeBudget, token);
            last?.State.ShouldBe(OperationState.Succeeded, $"seeding '{name}' did not converge: {last?.Error?.Message}");
            seeded[name] = (await topology.ReadWidgetAsync(world, name, token)).GetValueOrThrow();
        }

        // An acknowledged write whose only driver is its reminder — the 202 came back and nothing
        // has driven it yet.
        var inFlight = (await topology.PutWidgetAsync(world, "flush-in-flight", "acknowledged", token)).GetValueOrThrow();
        var inFlightOperation = topology.Operation(world.Tenant, inFlight.OperationId);

        (await topology.ReminderRowsAsync(inFlightOperation)).ShouldBeGreaterThan(0, "the in-flight operation has no reminder before the flush, so the flush cannot be blamed for its absence.");

        var remindersBefore = await topology.AllReminderRowsAsync();
        var keysBefore = await topology.HotTierKeysAsync();

        // ── The flush. ────────────────────────────────────────────────────────────────────────
        var flushed = await topology.FlushHotTierAsync(token);
        var since = Stopwatch.StartNew();

        var keysAfter = await topology.HotTierKeysAsync();
        var remindersAfter = await topology.AllReminderRowsAsync();
        var inFlightReminderAfter = await topology.ReminderRowsAsync(inFlightOperation);

        // Every idle activation goes, so what follows is read from storage.
        await topology.DeactivateEverythingAsync();

        // ── Full function within 60 s: a fresh write is accepted and converges. ───────────────
        WriteAccepted? fresh = null;
        Error? lastRefusal = null;
        var writeFaults = 0;

        while (since.Elapsed < RecoveryBudget + TimeSpan.FromSeconds(30)) {
            try {
                var write = await topology.PutWidgetAsync(world, "flush-after", "after", token);

                if (write.IsSuccess) {
                    fresh = write.GetValueOrThrow();
                    break;
                }

                lastRefusal = write.Error;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                writeFaults++;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        var acceptedAfter = fresh is null ? TimeSpan.MaxValue : since.Elapsed;
        var freshSucceeded = false;

        if (fresh is not null) {
            var (freshLast, _) = await topology.DriveUntilTerminalAsync(world.Tenant, fresh.OperationId, ConvergeBudget, token);
            freshSucceeded = freshLast?.State == OperationState.Succeeded;
        }

        var wholeAgainAfter = freshSucceeded ? since.Elapsed : TimeSpan.MaxValue;

        // ── Zero durable state lost. ──────────────────────────────────────────────────────────
        var lost = new List<string>();
        var durableRowsMissing = new List<string>();

        foreach (var (name, before) in seeded) {
            var after = await topology.ReadWidgetAsync(world, name, token);

            if (after.IsFailure) {
                lost.Add($"{name}: {after.Error!.Code} — {after.Error.Message}");
                continue;
            }

            var snapshot = after.GetValueOrThrow();

            if (snapshot.Id != before.Id || snapshot.ProvisioningState != ProvisioningState.Succeeded || snapshot.Body != before.Body) {
                lost.Add($"{name}: was {before.ProvisioningState} {before.Id:N}, reads {snapshot.ProvisioningState} {snapshot.Id:N}");
            }

            // And the row is in PostgreSQL, around Orleans.
            if ((await topology.DurableRowsAsync(ChaosTopology.ShardOf(world.Tenant), before.Id.ToString("N"), token)).Count == 0) {
                durableRowsMissing.Add(name);
            }
        }

        var group = await topology.ReadGroupAsync(world, token);
        var groupReadable = group.IsSuccess;

        // ── Zero acknowledged writes lost: the in-flight create still completes. ──────────────
        var (inFlightLast, inFlightFaults) = await topology.DriveUntilTerminalAsync(world.Tenant, inFlight.OperationId, ConvergeBudget, token);
        var inFlightSucceeded = inFlightLast?.State == OperationState.Succeeded
            && await topology.ConfigMapExistsAsync(world, "flush-in-flight", token);

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["hotKeysBefore"] = keysBefore,
            ["hotKeysFlushed"] = flushed,
            ["hotKeysAfter"] = keysAfter,
            ["reminderRowsBefore"] = remindersBefore,
            ["reminderRowsAfter"] = remindersAfter,
            ["inFlightReminderRowsAfter"] = inFlightReminderAfter,
            ["durableWidgets"] = seeded.Count,
            ["durableLost"] = lost.Count,
            ["durableRowsMissing"] = durableRowsMissing.Count,
            ["groupReadable"] = groupReadable ? 1 : 0,
            ["inFlightConverged"] = inFlightSucceeded ? 1 : 0,
            ["inFlightDriveFaults"] = inFlightFaults,
            ["freshWriteFaults"] = writeFaults,
            ["freshWriteAcceptedAfterSeconds"] = acceptedAfter == TimeSpan.MaxValue ? -1 : Math.Round(acceptedAfter.TotalSeconds, 1),
            ["wholeAgainAfterSeconds"] = wholeAgainAfter == TimeSpan.MaxValue ? -1 : Math.Round(wholeAgainAfter.TotalSeconds, 1),
            ["budgetSeconds"] = RecoveryBudget.TotalSeconds
        };

        var detail =
            $"FLUSHALL dropped {flushed} keys ({remindersBefore} → {remindersAfter} reminder rows, the in-flight operation's went "
            + $"{(inFlightReminderAfter == 0 ? "with them" : "nowhere")}); after collecting every activation, {seeded.Count}/{seeded.Count - lost.Count} "
            + $"durable widgets read back intact ({durableRowsMissing.Count} rows missing in PostgreSQL), the group "
            + $"{(groupReadable ? "is" : "is NOT")} readable; the acknowledged in-flight create {(inFlightSucceeded ? "converged" : "did NOT converge")}; "
            + $"a fresh write was accepted after {Seconds(acceptedAfter)} and converged after {Seconds(wholeAgainAfter)} (budget {RecoveryBudget.TotalSeconds:F0} s).";

        var held = lost.Count == 0
            && durableRowsMissing.Count == 0
            && groupReadable
            && inFlightSucceeded
            && wholeAgainAfter <= RecoveryBudget;

        if (held) {
            topology.Report.Held(2, detail, numbers);
        } else {
            topology.Report.Violated(2, detail, numbers);
        }

        lost.ShouldBeEmpty("docs/plan/23 § The chaos invariants, 2: zero durable state lost. Lost: " + string.Join("; ", lost));
        durableRowsMissing.ShouldBeEmpty("a widget reads back but has no row in its shard: " + string.Join(", ", durableRowsMissing));
        groupReadable.ShouldBeTrue($"the resource group is not readable after the flush: {group.Error?.Message}");

        inFlightSucceeded.ShouldBeTrue(
            "an acknowledged create did not complete after the flush: "
            + $"{inFlightLast?.State.ToString() ?? "never answered"} — {inFlightLast?.Error?.Message ?? inFlightLast?.LastProgress?.Detail}"
        );

        (wholeAgainAfter <= RecoveryBudget).ShouldBeTrue(
            $"full function came back after {Seconds(wholeAgainAfter)}, over the {RecoveryBudget.TotalSeconds:F0} s budget "
            + $"({writeFaults} write attempts threw). Last answer: {lastRefusal?.Message ?? "accepted"}."
        );
    }

    static string Seconds(TimeSpan value) => value == TimeSpan.MaxValue ? "never" : $"{value.TotalSeconds:F1} s";
}
