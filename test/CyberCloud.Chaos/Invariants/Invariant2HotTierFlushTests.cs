using CyberCloud.Chaos.Topology;
using CyberCloud.ResourceManager.Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
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
///         ⚠ <b>"Zero acknowledged writes lost" is watched, not driven.</b> The reminder table is in
///         the flushed Redis — <c>SiloComposition.ConfigureStorage</c> puts <c>UseRedisReminderService</c>
///         on the hot tier's connection string — so a FLUSHALL also empties every operation's safety
///         net, and an operation accepted before the flush and never driven is the case. The first
///         version of this test then drove that operation to completion itself and called the row
///         held, which proved the operation could be driven and nothing about whether the platform
///         would. Now nothing here calls <c>DriveAsync</c> on it: its durable row is read out of its
///         shard until it is terminal (<c>ChaosTopology.ObserveUntilTerminalAsync</c>), its reminder
///         row is watched for the moment it comes back, and every attempt counted is the platform's.
///         What is expected to bring it back, and is measured rather than inferred: the reminder
///         service's local copy of the table keeps ticking until its next refresh
///         (<c>ReminderOptions.RefreshReminderListPeriod</c>, reported), the tick activates the
///         grain, and <c>OperationGrain.OnActivateAsync</c> re-registers the reminder — the row
///         count going 0 → 1 with no call from this test is that. ⚠ The ordering this does not
///         induce is the unlucky one: a list refresh landing between the flush and the next tick,
///         after which the operation has no driver until something activates it. The flush here
///         lands seconds after the acceptance, so the tick is at most a period away and the refresh
///         up to five times that; the row measures the ordering it gets and the results say which.
///     </para>
///     <para>
///         "Full function within 60 s" is the other half and is driven, because it is about the
///         write path and the reconcile path working again, not about who calls the pass.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant2HotTierFlushTests(ChaosTopology topology) {
    const int Seeded = 8;
    static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);

    /// <summary>Three reminder periods: the first tick, a pass, and a margin for the pass after it.</summary>
    static readonly TimeSpan WatchBudget = OperationGrain.ReminderPeriod * 3;

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
        var refreshPeriod = topology.Cluster.GetSiloServiceProvider().GetRequiredService<IOptions<ReminderOptions>>().Value.RefreshReminderListPeriod;

        // ── The flush. ────────────────────────────────────────────────────────────────────────
        var flushed = await topology.FlushHotTierAsync(token);
        var since = Stopwatch.StartNew();

        var keysAfter = await topology.HotTierKeysAsync();
        var remindersAfter = await topology.AllReminderRowsAsync();
        var inFlightReminderAfter = await topology.ReminderRowsAsync(inFlightOperation);

        // Every idle activation goes, so what follows is read from storage — and the in-flight
        // operation's activation goes with them, so what drives it next has to find it in storage.
        await topology.DeactivateEverythingAsync();

        // ── Zero acknowledged writes lost: the platform, not this test, takes the in-flight create to terminal. ──
        TimeSpan? reminderRowBackAt = null;
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(token);

        var reminderWatch = Task.Run(async () => {
                while (!watching.IsCancellationRequested) {
                    if (await topology.ReminderRowsAsync(inFlightOperation) > 0) {
                        reminderRowBackAt = since.Elapsed;
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                }
            },
            CancellationToken.None
        );

        var inFlightWatch = topology.ObserveUntilTerminalAsync(world.Tenant, inFlight.OperationId, WatchBudget, token);

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

        // ── The in-flight create, as the platform left it. ────────────────────────────────────
        var (inFlightState, inFlightPasses, inFlightActivations, inFlightTerminalAt) = await inFlightWatch;
        await watching.CancelAsync();
        await reminderWatch;

        var inFlightSucceeded = inFlightState == OperationState.Succeeded
            && await topology.ConfigMapExistsAsync(world, "flush-in-flight", token);

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["hotKeysBefore"] = keysBefore,
            ["hotKeysFlushed"] = flushed,
            ["hotKeysAfter"] = keysAfter,
            ["reminderRowsBefore"] = remindersBefore,
            ["reminderRowsAfter"] = remindersAfter,
            ["reminderListRefreshMinutes"] = refreshPeriod.TotalMinutes,
            ["inFlightReminderRowsAfter"] = inFlightReminderAfter,
            ["inFlightReminderRowBackAfterSeconds"] = reminderRowBackAt is { } back ? Math.Round(back.TotalSeconds, 1) : -1,
            ["inFlightReminderPasses"] = inFlightPasses,
            ["inFlightActivations"] = inFlightActivations,
            ["inFlightTerminalAfterSeconds"] = inFlightTerminalAt is { } terminal ? Math.Round(terminal.TotalSeconds, 1) : -1,
            ["inFlightConverged"] = inFlightSucceeded ? 1 : 0,
            ["durableWidgets"] = seeded.Count,
            ["durableLost"] = lost.Count,
            ["durableRowsMissing"] = durableRowsMissing.Count,
            ["groupReadable"] = groupReadable ? 1 : 0,
            ["freshWriteFaults"] = writeFaults,
            ["freshWriteAcceptedAfterSeconds"] = acceptedAfter == TimeSpan.MaxValue ? -1 : Math.Round(acceptedAfter.TotalSeconds, 1),
            ["wholeAgainAfterSeconds"] = wholeAgainAfter == TimeSpan.MaxValue ? -1 : Math.Round(wholeAgainAfter.TotalSeconds, 1),
            ["budgetSeconds"] = RecoveryBudget.TotalSeconds
        };

        var detail =
            $"FLUSHALL dropped {flushed} keys ({remindersBefore} → {remindersAfter} reminder rows, the in-flight operation's went "
            + $"{(inFlightReminderAfter == 0 ? "with them" : "nowhere")}); after collecting every activation, {seeded.Count}/{seeded.Count - lost.Count} "
            + $"durable widgets read back intact ({durableRowsMissing.Count} rows missing in PostgreSQL), the group "
            + $"{(groupReadable ? "is" : "is NOT")} readable; the acknowledged in-flight create, untouched by the test, "
            + $"{(inFlightSucceeded ? "converged" : "did NOT converge")} — its reminder row was "
            + (reminderRowBackAt is { } b
                ? $"back {b.TotalSeconds:F0} s after the flush"
                : inFlightSucceeded
                    ? "re-registered and removed inside the one pass that converged it (never seen by a once-a-second poll)"
                    : "NEVER back")
            + " and the platform drove "
            + $"{inFlightPasses} pass(es) over {inFlightActivations} activation(s), terminal "
            + $"{(inFlightTerminalAt is { } t ? $"at {t.TotalSeconds:F0} s" : $"NEVER within {WatchBudget.TotalSeconds:F0} s")} "
            + $"(reminder list refresh every {refreshPeriod.TotalMinutes:F0} min); "
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
            "docs/plan/23 § The chaos invariants, 2: zero acknowledged control-plane writes lost. The platform did not take the "
            + $"acknowledged create to Succeeded on its own within {WatchBudget.TotalSeconds:F0} s of the flush: it is {inFlightState} after "
            + $"{inFlightPasses} reminder pass(es), its reminder row {(reminderRowBackAt is null ? "never came back" : "came back")}."
        );

        (wholeAgainAfter <= RecoveryBudget).ShouldBeTrue(
            $"full function came back after {Seconds(wholeAgainAfter)}, over the {RecoveryBudget.TotalSeconds:F0} s budget "
            + $"({writeFaults} write attempts threw). Last answer: {lastRefusal?.Message ?? "accepted"}."
        );
    }

    static string Seconds(TimeSpan value) => value == TimeSpan.MaxValue ? "never" : $"{value.TotalSeconds:F1} s";
}
