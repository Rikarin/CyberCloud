using CyberCloud.Chaos.Topology;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.Tenancy;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 1: <i>kill a random silo every 90 s during a provisioning
///     storm → zero resources stuck in a transitional state after settling; every operation reaches
///     Succeeded or Failed.</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The 90 s is compressed to the length of a wave, and the compression is the only
///         liberty taken.</b> The invariant is about what a kill does to the operations in flight
///         when it lands, not about how often it lands. So the kills land <i>during</i> the writes:
///         the second and third waves of creates are issued and a secondary silo is killed while
///         the write path is mid-saga on some of them and the previous wave's operations are
///         mid-pass. That is "kill a silo mid-write", literally, and the first run of this test
///         produced what docs/plan/06 § Two-phase create says it produces — a claim with no confirm.
///     </para>
///     <para>
///         ⚠ <b>The sweep knows the difference between stuck and orphaned, and asserts both.</b> A
///         silo that dies between claiming a name and confirming it leaves a resource-group member
///         in Creating with no operation behind it. docs/plan/06 says exactly what happens next:
///         the claim's five-minute lease expires and the name is free again, and the orphan is
///         swept by the group's reaper. Neither is "stuck" — both have an armed remedy — and neither
///         is what a resource whose operation ended without moving it looks like. So the sweep
///         asserts three things for an orphan: the group's reaper can see it
///         (<c>ListOrphansAsync</c>), the reaper's reminder is in the Redis reminder table where a
///         silo loss cannot take it, and the retried PUT of the same name was accepted once the
///         lease expired — the measured number is how long that took. Everything else transitional
///         after settling is stuck, and fails.
///     </para>
///     <para>
///         ⚠ <b>The reaper's own sweep is not waited for.</b> <c>ResourceGroupGrain.OrphanSweepPeriod</c>
///         is fifteen minutes and <c>IResourceGroupGrain.OrphanAge</c> another fifteen, so watching an
///         orphan disappear costs half an hour of wall clock per run. The three assertions above are
///         what makes "it will be reaped" a checked claim rather than a hoped one; the sweep itself
///         is <c>OrphanReaperArmingTests</c>' to prove, and it does.
///     </para>
///     <para>
///         ⚠ <b>"Noticed after 0.0 s" is the testing host, not the probe path.</b> Orleans'
///         <c>KillSiloAsync</c> stops the silo abruptly enough to lose every activation, but the
///         dying host still writes its own Dead row, so the cluster learns of the death at once. A
///         pod that is SIGKILLed does not get to do that, and detection then takes the membership
///         probes at their shipped defaults — about a minute. The number is reported as measured and
///         the invariant is about what happens after the death is known, which is the same either
///         way.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant1SiloKillStormTests(ChaosTopology topology) {
    const int Waves = 3;
    const int PerTenantPerWave = 4;

    /// <summary>Long enough for a claim's five-minute lease to expire and the freed name to be created and converged.</summary>
    static readonly TimeSpan SettleBudget = TimeSpan.FromMinutes(8);

    [Fact]
    public async Task Invariant1_ASiloKilledMidWriteDuringAProvisioningStormLeavesNothingStuck() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;

        var a = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardA), "storm-a", token);
        var b = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardB), "storm-b", token);

        var accepted = new ConcurrentBag<(TenantWorld World, string Name, Guid ResourceId, Guid OperationId)>();
        var drivers = new List<Task<(TenantWorld World, string Name, OperationStatus? Last, int Faults)>>();
        var claimWaits = new ConcurrentBag<(string Name, TimeSpan AnsweredAfter, bool NoOp)>();
        var writeFaults = 0;
        var kills = new List<(SiloAddress Silo, TimeSpan At, TimeSpan Noticed, int ActivationsLost)>();
        var storm = Stopwatch.StartNew();

        // ── The storm, in waves; a silo dies under the second and the third. ───────────────────
        for (var wave = 0; wave < Waves; wave++) {
            var writes = new List<Task>();

            foreach (var world in new[] { a, b }) {
                for (var i = 0; i < PerTenantPerWave; i++) {
                    writes.Add(WriteUntilAcceptedAsync(world, $"w{wave}-{i}-{world.Group}"));
                }
            }

            if (wave > 0) {
                // ⚠ Mid-write, mid-pass: the PUTs above are in flight and the previous wave's
                // operations are being driven when this lands.
                var killed = await KillAndNoticeAsync();
                kills.Add(killed);

                output?.WriteLine(
                    $"[{killed.At.TotalSeconds:F1}s] killed {killed.Silo} holding {killed.ActivationsLost} activations; "
                    + $"membership noticed after {killed.Noticed.TotalSeconds:F1} s"
                );
            }

            await Task.WhenAll(writes);

            // Every accepted operation is driven from the moment it is accepted, so the next wave's
            // kill lands on operations mid-pass and not only on writes mid-saga.
            foreach (var x in accepted.Where(x => x.OperationId != Guid.Empty && x.Name.StartsWith($"w{wave}-", StringComparison.Ordinal))) {
                drivers.Add(DriveAsync(x));
            }
        }

        // The cluster is brought back to strength so the settle runs on a whole cluster — the
        // invariant is about the operations, not about running a storm on one silo forever.
        for (var i = 0; i < kills.Count; i++) {
            output?.WriteLine($"[{storm.Elapsed.TotalSeconds:F1}s] started {await topology.StartASiloAsync()}");
        }

        accepted.Count.ShouldBe(
            Waves * PerTenantPerWave * 2,
            $"{accepted.Count} of {Waves * PerTenantPerWave * 2} creates were accepted within {SettleBudget}; a "
            + "write that never came back is a write the storm lost, and the sweep below could not see it."
        );

        // ── Settle: every accepted operation reaches a terminal state, or the budget says so. ──
        var driven = await Task.WhenAll(drivers);

        var settled = storm.Elapsed;
        var driveFaults = driven.Sum(x => x.Faults);
        var notTerminal = driven.Where(x => x.Last is null || !x.Last.IsTerminal).ToList();
        var succeeded = driven.Count(x => x.Last?.State == OperationState.Succeeded);
        var failed = driven.Count(x => x.Last?.State == OperationState.Failed);
        var acceptedIds = accepted.Select(x => x.ResourceId).ToHashSet();

        // ── The sweep: every member of both groups, through the real read path. ───────────────
        var stuck = new List<string>();
        var orphans = new List<string>();
        var orphansUnseenByReaper = new List<string>();
        var reaperRemindersMissing = new List<string>();
        var duplicateNames = new List<string>();
        var swept = 0;
        var configMaps = 0;
        var succeededMembers = 0;

        foreach (var world in new[] { a, b }) {
            var page = await topology.Manager.ListAsync(
                new() { Path = world.Widgets.Path, ApiVersion = SampleWidgets.V2026, Caller = world.Caller, Top = ListRequest.MaxPageSize },
                token
            );

            page.IsSuccess.ShouldBeTrue($"the sweep could not list {world.Group}'s widgets: {page.Error?.Message}");

            var group = topology.For(world.Tenant)
                .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(world.Subscription, world.Group));

            var visibleToReaper = (await group.ListOrphansAsync(TimeSpan.Zero)).GetValueOrThrow()
                .Select(x => x.ResourceId)
                .ToHashSet();

            var members = page.GetValueOrThrow().Resources;

            // ⚠ Two members under one name is the ghost the first run of this storm produced: a
            // create whose write path died before confirming its claim, driven to Succeeded by its
            // reminder, and a second create under the same name once the lease expired. Neither is
            // transitional, so a sweep that only looked at states would call it clean.
            duplicateNames.AddRange(
                members.GroupBy(x => x.Name, StringComparer.Ordinal)
                    .Where(x => x.Count() > 1)
                    .Select(x => $"{world.Group}/{x.Key} × {x.Count()}")
            );

            foreach (var resource in members) {
                swept++;

                var transitional = resource.ProvisioningState is ProvisioningState.Creating
                    or ProvisioningState.Updating
                    or ProvisioningState.Deleting
                    or ProvisioningState.Unknown;

                if (!transitional) {
                    if (resource.ProvisioningState == ProvisioningState.Succeeded) {
                        succeededMembers++;

                        if (await topology.ConfigMapExistsAsync(world, resource.Name, token)) {
                            configMaps++;
                        }
                    }

                    continue;
                }

                if (resource.ProvisioningState == ProvisioningState.Creating && !acceptedIds.Contains(resource.Id)) {
                    // A claim whose silo died before the confirm. Not stuck — if the reaper can see it.
                    orphans.Add($"{resource.Name} ({resource.Id:N})");

                    if (!visibleToReaper.Contains(resource.Id)) {
                        orphansUnseenByReaper.Add(resource.Name);
                    }

                    if (await topology.ReminderRowsAsync(group) == 0) {
                        reaperRemindersMissing.Add(world.Group);
                    }

                    continue;
                }

                stuck.Add($"{resource.Name} is {resource.ProvisioningState}");
            }
        }

        var noOps = claimWaits.Count(x => x.NoOp);

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["creates"] = accepted.Count,
            ["kills"] = kills.Count,
            ["activationsOnKilledSilos"] = kills.Sum(k => k.ActivationsLost),
            ["deathNoticedSecondsMax"] = Math.Round(kills.Count == 0 ? 0 : kills.Max(k => k.Noticed.TotalSeconds), 1),
            ["silosAfter"] = topology.Silos.Count,
            ["writeFaults"] = writeFaults,
            ["claimsLeftByADeadWrite"] = claimWaits.Count,
            ["claimsFinishedByTheirOperation"] = noOps,
            ["claimAnsweredAfterSecondsMax"] = Math.Round(claimWaits.IsEmpty ? 0 : claimWaits.Max(x => x.AnsweredAfter.TotalSeconds), 1),
            ["driveFaults"] = driveFaults,
            ["operationsDriven"] = driven.Length,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["notTerminal"] = notTerminal.Count,
            ["swept"] = swept,
            ["duplicateNames"] = duplicateNames.Count,
            ["stuck"] = stuck.Count,
            ["orphansAwaitingReaper"] = orphans.Count,
            ["orphansUnseenByReaper"] = orphansUnseenByReaper.Count,
            ["reaperSweepMinutes"] = ResourceGroupGrain.OrphanSweepPeriod.TotalMinutes,
            ["succeededMembers"] = succeededMembers,
            ["configMapsForSucceeded"] = configMaps,
            ["settleSeconds"] = Math.Round(settled.TotalSeconds, 1)
        };

        var detail =
            $"{accepted.Count} creates across 2 tenants/2 shards, {kills.Count} silos killed mid-write "
            + $"(at {string.Join(", ", kills.Select(k => k.At.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s"))}, "
            + $"holding {kills.Sum(k => k.ActivationsLost)} activations, noticed within "
            + $"{string.Join("/", kills.Select(k => k.Noticed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)))} s); "
            + $"{writeFaults} write calls threw; {claimWaits.Count} names were left claimed by a dead write, {noOps} of them finished "
            + $"by their own operation so the retried PUT was a no-op, all answered within "
            + $"{(claimWaits.IsEmpty ? 0 : claimWaits.Max(x => x.AnsweredAfter.TotalSeconds)):F0} s; {driveFaults} drive calls threw; "
            + $"settled in {settled.TotalSeconds:F0} s with {succeeded} Succeeded, {failed} Failed, {notTerminal.Count} still running "
            + $"of {driven.Length} driven; sweep of {swept} members found {stuck.Count} stuck, {duplicateNames.Count} duplicated names and "
            + $"{orphans.Count} orphans awaiting the {ResourceGroupGrain.OrphanSweepPeriod.TotalMinutes:F0}-minute reaper "
            + $"({orphansUnseenByReaper.Count} unseen by it); {configMaps}/{succeededMembers} Succeeded members have their ConfigMap in k3s.";

        var held = notTerminal.Count == 0
            && stuck.Count == 0
            && duplicateNames.Count == 0
            && orphansUnseenByReaper.Count == 0
            && reaperRemindersMissing.Count == 0
            && configMaps == succeededMembers;

        if (held) {
            topology.Report.Held(1, detail, numbers);
        } else {
            topology.Report.Violated(1, detail, numbers);
        }

        notTerminal.ShouldBeEmpty(
            $"{notTerminal.Count} operation(s) never reached a terminal state within {SettleBudget}: "
            + string.Join(
                "; ",
                notTerminal.Select(x =>
                    $"{x.Name} → {x.Last?.State.ToString() ?? "never answered"} after {x.Last?.Attempts ?? 0} attempts, last: {x.Last?.LastProgress?.Detail}"
                )
            )
        );

        stuck.ShouldBeEmpty(
            "docs/plan/23 § The chaos invariants, 1: zero resources stuck in a transitional state after settling. Found: "
            + string.Join("; ", stuck)
        );

        orphansUnseenByReaper.ShouldBeEmpty(
            "a member left in Creating by a killed write is not in the group's ListOrphansAsync, so the reaper "
            + "docs/plan/06 § Two-phase create relies on would never remove it: " + string.Join(", ", orphansUnseenByReaper)
        );

        reaperRemindersMissing.ShouldBeEmpty(
            "a group holding an orphan has no reap-orphans reminder in the Redis reminder table, so the sweep "
            + "that would remove the orphan is not armed anywhere a silo loss cannot reach."
        );

        duplicateNames.ShouldBeEmpty(
            "two resources share one name in a group: a create whose write path died before confirming its claim "
            + "converged anyway and a second create took the name when the lease expired. OperationGrain.ConfirmClaimAsync "
            + "exists to make this impossible — " + string.Join(", ", duplicateNames)
        );

        configMaps.ShouldBe(succeededMembers, "a widget the platform reports Succeeded has no ConfigMap in the cluster.");

        async Task<(TenantWorld World, string Name, OperationStatus? Last, int Faults)> DriveAsync(
            (TenantWorld World, string Name, Guid ResourceId, Guid OperationId) x
        ) {
            var (last, faults) = await topology.DriveUntilTerminalAsync(x.World.Tenant, x.OperationId, SettleBudget, token);
            return (x.World, x.Name, last, faults);
        }

        // Kills one secondary and measures two things about it: how many activations it was
        // holding — zero would mean the storm never touched it and the kill proved nothing — and how
        // long the cluster took to notice (see the class remarks on why that is instant here).
        async Task<(SiloAddress Silo, TimeSpan At, TimeSpan Noticed, int ActivationsLost)> KillAndNoticeAsync() {
            var at = storm.Elapsed;
            var victim = topology.Cluster.SecondarySilos[0].SiloAddress;
            var held = (await topology.Management.GetRuntimeStatistics([victim]))[0].ActivationCount;
            var dead = await topology.KillASecondarySiloAsync();
            var noticing = Stopwatch.StartNew();

            while (noticing.Elapsed < TimeSpan.FromMinutes(2)) {
                var hosts = await topology.Management.GetHosts(true);

                if (!hosts.ContainsKey(dead)) {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }

            return (dead, at, noticing.Elapsed, held);
        }

        // The client a gateway is: a PUT that threw is retried, a PUT that met a name claimed by the
        // attempt that just died is retried until the lease frees it, and anything else refused is
        // a write path answer the sweep will see the consequence of.
        async Task WriteUntilAcceptedAsync(TenantWorld world, string name) {
            var started = storm.Elapsed;
            TimeSpan? firstClaimConflict = null;

            while (storm.Elapsed - started < SettleBudget) {
                try {
                    var write = await topology.PutWidgetAsync(world, name, "storm", token);

                    if (write.TryGetError(out var error)) {
                        if (error.Code == ErrorCode.ResourceAlreadyExists || error.Code == ErrorCode.Conflict) {
                            // ⚠ docs/plan/06 § Two-phase create: the silo died between claiming the
                            // name and confirming it. The operation the dying write path started
                            // confirms the claim on its first pass — OperationGrain.ConfirmClaimAsync,
                            // added when the first run of this storm found that it did not — after
                            // which this same-body PUT is the no-op the document promises. How long
                            // that takes is measured.
                            firstClaimConflict ??= storm.Elapsed;
                            await Task.Delay(TimeSpan.FromSeconds(5), token);
                            continue;
                        }

                        throw new InvalidOperationException($"the storm's PUT of '{name}' was refused: {error.Code} — {error.Message}");
                    }

                    var value = write.GetValueOrThrow();

                    if (firstClaimConflict is { } since) {
                        claimWaits.Add((name, storm.Elapsed - since, value.NoOp));
                    }

                    if (value.NoOp) {
                        // The resource exists and carries this body already: the dead attempt's
                        // operation finished the create. There is nothing to drive; the sweep will
                        // find the resource and judge it like the others.
                        accepted.Add((world, name, value.Resource.Id, Guid.Empty));
                        return;
                    }

                    accepted.Add((world, name, value.Resource.Id, value.OperationId));
                    return;
                } catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException)) {
                    // ⚠ The silo under this write just died. Counted, and retried — which is what a
                    // gateway's client does with a dropped connection.
                    Interlocked.Increment(ref writeFaults);
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }

            throw new InvalidOperationException(
                $"the storm's PUT of '{name}' was not accepted within {SettleBudget}"
                + (firstClaimConflict is null ? "." : $"; its name has been claimed by a dead attempt since {firstClaimConflict.Value.TotalSeconds:F0} s.")
            );
        }
    }
}
