using CyberCloud.Chaos.Topology;
using System.Diagnostics;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 3: <i>fail over a durable shard → writes for that
///     shard's tenants pause and resume; no data loss; other tenants unaffected.</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The shard is a stopped PostgreSQL container and the other shard keeps serving</b>,
///         which is the only arrangement under which "other tenants unaffected" can fail. One tenant
///         hashes to <c>durable-00</c> and one to <c>durable-01</c>; the first shard is stopped for
///         a measured window while both tenants write, then started again.
///     </para>
///     <para>
///         ⚠ <b>"Pause" is asserted from both sides.</b> A write for the stopped shard's tenant that
///         is <i>accepted</i> during the outage is worse than one that fails — it means the 202 was
///         given for state that went nowhere — so the count of accepted writes during the outage has
///         to be zero, and the count of refused ones has to be positive, or the shard was never
///         really down. How long each refusal took to come back is measured too: a pause that hangs
///         for Npgsql's default fifteen seconds is indistinguishable from a lost cluster.
///     </para>
///     <para>
///         ⚠ <b>Resume is measured from the container's start</b>, and no data loss is asserted after
///         every activation has been collected, so the reads that prove it come out of the shard
///         that just came back rather than out of memory.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant3ShardFailoverTests(ChaosTopology topology) {
    const int Seeded = 4;
    static readonly TimeSpan Outage = TimeSpan.FromSeconds(25);
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);
    static readonly TimeSpan ResumeBudget = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Invariant3_StoppingADurableShardPausesItsTenantsWritesAndNobodyElses() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;

        var affected = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardA), "shard-a", token);
        var bystander = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardB), "shard-b", token);

        ChaosTopology.ShardOf(affected.Tenant).ShouldBe(ChaosTopology.ShardA);
        ChaosTopology.ShardOf(bystander.Tenant).ShouldBe(ChaosTopology.ShardB);

        // ── Before: both tenants have converged state on their own shards. ────────────────────
        var before = new Dictionary<(TenantWorld World, string Name), ResourceSnapshot>();

        foreach (var world in new[] { affected, bystander }) {
            for (var i = 0; i < Seeded; i++) {
                var name = $"seed-{i}";
                var accepted = (await topology.PutWidgetAsync(world, name, "seed", token)).GetValueOrThrow();
                var (last, _) = await topology.DriveUntilTerminalAsync(world.Tenant, accepted.OperationId, ConvergeBudget, token);
                last?.State.ShouldBe(OperationState.Succeeded, $"seeding {world.Group}/{name} did not converge: {last?.Error?.Message}");
                before[(world, name)] = (await topology.ReadWidgetAsync(world, name, token)).GetValueOrThrow();
            }
        }

        // ── The outage. ───────────────────────────────────────────────────────────────────────
        var bystanderAccepted = new List<Guid>();
        var bystanderRefused = new List<string>();
        var affectedAccepted = new List<string>();
        var affectedRefused = new List<(string Reason, TimeSpan Took)>();
        var affectedReadsOk = 0;
        var affectedReadsFailed = 0;
        var round = 0;
        TimeSpan outageLength;

        await topology.StopShardAsync(ChaosTopology.ShardA, token);
        var outage = Stopwatch.StartNew();

        try {
            while (outage.Elapsed < Outage) {
                round++;

                // The bystander's write, which must land.
                try {
                    var write = await topology.PutWidgetAsync(bystander, $"during-{round}", "during", token);

                    if (write.IsSuccess) {
                        bystanderAccepted.Add(write.GetValueOrThrow().OperationId);
                    } else {
                        bystanderRefused.Add($"{write.Error!.Code} — {write.Error.Message}");
                    }
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    bystanderRefused.Add($"{ex.GetType().Name}: {ex.Message}");
                }

                // The affected tenant's write, which must pause — refused or thrown, and quickly.
                var attempt = Stopwatch.StartNew();

                try {
                    var write = await topology.PutWidgetAsync(affected, $"during-{round}", "during", token);

                    if (write.IsSuccess) {
                        affectedAccepted.Add($"during-{round}");
                    } else {
                        affectedRefused.Add(($"{write.Error!.Code}", attempt.Elapsed));
                    }
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    affectedRefused.Add((ex.GetType().Name, attempt.Elapsed));
                }

                // And a read of the affected tenant's existing state, for the record: an activation
                // still in memory may well answer, and that is worth knowing rather than assuming.
                try {
                    var read = await topology.ReadWidgetAsync(affected, "seed-0", token);
                    _ = read.IsSuccess ? affectedReadsOk++ : affectedReadsFailed++;
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    affectedReadsFailed++;
                }
            }

            outageLength = outage.Elapsed;
            output?.WriteLine(
                $"outage {outageLength.TotalSeconds:F0} s: bystander {bystanderAccepted.Count} accepted/{bystanderRefused.Count} refused; "
                + $"affected {affectedAccepted.Count} accepted/{affectedRefused.Count} refused (slowest refusal "
                + $"{(affectedRefused.Count == 0 ? 0 : affectedRefused.Max(x => x.Took.TotalSeconds)):F1} s), reads {affectedReadsOk} ok/{affectedReadsFailed} failed"
            );
        } finally {
            // ── Resume — in a finally, so the shard comes back whatever the outage did to this test. ──
            await topology.StartShardAsync(ChaosTopology.ShardA, token);
        }

        var resume = Stopwatch.StartNew();
        Guid? resumed = null;
        var resumeAttempts = 0;

        while (resume.Elapsed < ResumeBudget) {
            resumeAttempts++;

            try {
                var write = await topology.PutWidgetAsync(affected, "after-resume", "after", token);

                if (write.IsSuccess) {
                    resumed = write.GetValueOrThrow().OperationId;
                    break;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Still coming back.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        var resumedAfter = resumed is null ? TimeSpan.MaxValue : resume.Elapsed;

        // Everything accepted converges: the bystander's writes during the outage, the resume write.
        var notConverged = new List<string>();

        foreach (var operationId in bystanderAccepted.Concat(resumed is { } r ? [r] : [])) {
            var tenant = operationId == resumed ? affected.Tenant : bystander.Tenant;
            var (last, _) = await topology.DriveUntilTerminalAsync(tenant, operationId, ConvergeBudget, token);

            if (last?.State != OperationState.Succeeded) {
                notConverged.Add($"{operationId:N} → {last?.State.ToString() ?? "never answered"}: {last?.Error?.Message}");
            }
        }

        // ── No data loss, read from the shard that came back. ─────────────────────────────────
        await topology.DeactivateEverythingAsync();
        var lost = new List<string>();

        foreach (var ((world, name), snapshot) in before) {
            var after = await topology.ReadWidgetAsync(world, name, token);

            if (after.IsFailure || after.GetValueOrThrow().Id != snapshot.Id || after.GetValueOrThrow().ProvisioningState != ProvisioningState.Succeeded) {
                lost.Add($"{world.Group}/{name}: {(after.IsFailure ? after.Error!.Message : after.GetValueOrThrow().ProvisioningState.ToString())}");
            }
        }

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["outageSeconds"] = Math.Round(outageLength.TotalSeconds, 1),
            ["rounds"] = round,
            ["bystanderWritesAccepted"] = bystanderAccepted.Count,
            ["bystanderWritesRefused"] = bystanderRefused.Count,
            ["affectedWritesAccepted"] = affectedAccepted.Count,
            ["affectedWritesRefused"] = affectedRefused.Count,
            ["affectedRefusalSecondsMax"] = Math.Round(affectedRefused.Count == 0 ? 0 : affectedRefused.Max(x => x.Took.TotalSeconds), 1),
            ["affectedReadsOk"] = affectedReadsOk,
            ["affectedReadsFailed"] = affectedReadsFailed,
            ["resumedAfterSeconds"] = resumedAfter == TimeSpan.MaxValue ? -1 : Math.Round(resumedAfter.TotalSeconds, 1),
            ["resumeAttempts"] = resumeAttempts,
            ["acceptedNotConverged"] = notConverged.Count,
            ["seededWidgets"] = before.Count,
            ["lost"] = lost.Count
        };

        var refusals = affectedRefused.GroupBy(x => x.Reason, StringComparer.Ordinal).Select(x => $"{x.Key} ×{x.Count()}");

        var detail =
            $"shard {ChaosTopology.ShardA} stopped for {outageLength.TotalSeconds:F0} s over {round} rounds: the bystander tenant on "
            + $"{ChaosTopology.ShardB} had {bystanderAccepted.Count} writes accepted and {bystanderRefused.Count} refused; the affected tenant had "
            + $"{affectedAccepted.Count} accepted and {affectedRefused.Count} refused ({string.Join(", ", refusals)}; slowest refusal "
            + $"{(affectedRefused.Count == 0 ? 0 : affectedRefused.Max(x => x.Took.TotalSeconds)):F1} s), and {affectedReadsOk}/{affectedReadsOk + affectedReadsFailed} reads of its "
            + $"existing state answered from memory; writes resumed {(resumed is null ? "NEVER" : $"{resumedAfter.TotalSeconds:F1} s")} after the shard came back; "
            + $"{notConverged.Count} accepted writes failed to converge; {lost.Count}/{before.Count} seeded widgets lost.";

        var held = bystanderRefused.Count == 0
            && affectedAccepted.Count == 0
            && affectedRefused.Count > 0
            && resumed is not null
            && notConverged.Count == 0
            && lost.Count == 0;

        if (held) {
            topology.Report.Held(3, detail, numbers);
        } else {
            topology.Report.Violated(3, detail, numbers);
        }

        bystanderRefused.ShouldBeEmpty(
            "docs/plan/23 § The chaos invariants, 3: other tenants unaffected. The tenant on the healthy shard was refused: "
            + string.Join("; ", bystanderRefused)
        );

        affectedAccepted.ShouldBeEmpty(
            "a write for the stopped shard's tenant was ACCEPTED during the outage, so a 202 was given for state that had nowhere to go: "
            + string.Join(", ", affectedAccepted)
        );

        affectedRefused.Count.ShouldBeGreaterThan(0, "no write for the stopped shard's tenant was refused, so the shard was not really down.");
        resumed.ShouldNotBeNull($"writes for the affected tenant did not resume within {ResumeBudget} of the shard coming back ({resumeAttempts} attempts).");
        notConverged.ShouldBeEmpty("accepted writes did not converge: " + string.Join("; ", notConverged));
        lost.ShouldBeEmpty("docs/plan/23 § The chaos invariants, 3: no data loss. Lost: " + string.Join("; ", lost));
    }
}
