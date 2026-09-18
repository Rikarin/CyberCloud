using CyberCloud.Chaos.Topology;
using System.Diagnostics;

namespace CyberCloud.Chaos.Invariants;

/// <summary>
///     docs/plan/23 § The chaos invariants, 4: <i>blackhole a managed cluster → its resources go
///     Degraded, reconciles suspend, no operations fail, clean resumption on restore.</i>
///     docs/plan/09 § Testing the fabric asks for the same run in the same words.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The blackhole is the k3s container stopped</b>, with a create and an update in flight
///         against it. Stopped rather than paused: a paused container swallows packets and every
///         call waits for a client timeout, which is a different fault with a different signature
///         (a hang), and the connection grain's own probes would take minutes to notice. A stopped
///         container refuses the connection, which is what a cluster whose API server is gone does.
///     </para>
///     <para>
///         ⚠ <b>"Its resources go Degraded" is read as the cluster connection's health</b>, because
///         <c>ProvisioningState</c> has no Degraded value — docs/plan/09 § Connection health puts the
///         state on the connection and has the portal say "cannot reach your cluster". What the
///         resource itself shows during the window is measured rather than assumed: a retryable
///         reconcile failure records itself on the resource as Failed
///         (<c>ResourceGrain.CompleteAsync</c>) while the operation keeps running, so a tenant
///         reading the resource in the seconds before the health window closes may see "Failed"
///         for a network outage, which is exactly the sentence docs/plan/09 says must not appear.
///         ⚠ Such a read is a violation of the clause, not a footnote to it: the count is in the
///         verdict, and the row is ✘ while it is nonzero. The first version of this test reported
///         the count and printed ✔ beside it, which the review of the branch called what it was.
///     </para>
///     <para>
///         ⚠ <b>Clean resumption includes the object deleted behind the reconciler's back.</b> After
///         the cluster is back and both operations have converged, the first widget's ConfigMap is
///         deleted with the raw client and the widget is updated again; the update's pass has to put
///         the object back. That is the drift case of the conformance suite run through the platform
///         rather than through a reconciler instance in a test.
///     </para>
/// </remarks>
[Collection(ChaosSuite.Name)]
public sealed class Invariant4ClusterBlackholeTests(ChaosTopology topology) {
    static readonly TimeSpan ConvergeBudget = TimeSpan.FromMinutes(3);
    static readonly TimeSpan DegradeBudget = TimeSpan.FromSeconds(90);
    static readonly TimeSpan HealthyBudget = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Invariant4_BlackholingTheManagedClusterDegradesItSuspendsReconcilesFailsNoOperationAndResumesCleanly() {
        var token = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper;
        var world = await topology.CreateTenantAsync(ChaosTopology.TenantOn(ChaosTopology.ShardB), "blackhole", token);

        // ── Before: one widget converged, which is the cluster answering. ─────────────────────
        var first = (await topology.PutWidgetAsync(world, "bh-first", "v1", token)).GetValueOrThrow();
        (await topology.DriveUntilTerminalAsync(world.Tenant, first.OperationId, ConvergeBudget, token)).Last?.State.ShouldBe(OperationState.Succeeded);

        // ── The blackhole, with a create and an update in flight. ─────────────────────────────
        TimeSpan? degradedAfter = null;
        var passesDriven = 0;
        var suspendedPasses = 0;
        var retryingPasses = 0;
        var failedOperations = new List<string>();
        var resourceReadFailedDuringBlackhole = 0;
        var resourceReads = 0;
        var lastProgress = string.Empty;
        (string Name, Guid OperationId)[] inFlight;
        Stopwatch restore;

        await topology.StopClusterAsync(token);
        var blackhole = Stopwatch.StartNew();

        try {
            var create = (await topology.PutWidgetAsync(world, "bh-second", "v1", token)).GetValueOrThrow();
            var update = (await topology.PutWidgetAsync(world, "bh-first", "v2", token)).GetValueOrThrow();
            update.NoOp.ShouldBeFalse("the update was a no-op, so nothing is mid-provision against the dead cluster.");

            inFlight = [(Name: "bh-second", create.OperationId), (Name: "bh-first", update.OperationId)];

            // ⚠ Degraded is read off the operations, not off the connection grain. The grain refuses a
            // caller that is neither the owning tenant nor a null-tenant silo service —
            // ClusterConnectionGrain.EnsureCallerMayReach answers a cluster client with the canonical
            // 404, and the first run of this test asked it anyway. What a tenant can see is the pass:
            // a Degraded connection answers every apply with ApplyResult.Suspended, the reconciler
            // reports the "waiting-for-cluster" step with the health message in it, and the operation
            // stays Running. Before the window closes the same pass reports "retrying" — a transport
            // failure, retryable — which is the window in which the resource itself reads Failed.
            while (blackhole.Elapsed < DegradeBudget) {
                foreach (var (name, operationId) in inFlight) {
                    var driven = await topology.Operation(world.Tenant, operationId).DriveAsync();
                    passesDriven++;

                    if (driven.IsSuccess) {
                        var status = driven.GetValueOrThrow();
                        lastProgress = $"{status.LastProgress?.Step}: {status.LastProgress?.Detail}";

                        if (status.State == OperationState.Failed) {
                            failedOperations.Add($"{name}: {status.Error?.Message}");
                        }

                        // ⚠ The reconciler reports "waiting-for-cluster" and OperationGrain.ScheduleAsync
                        // then appends its own "waiting" entry carrying the outcome's reason, so the LAST
                        // entry after a suspended pass is "waiting" with docs/plan/09's sentence in it.
                        // The sentence is what a tenant reads, so it is what is matched.
                        if (IsSuspended(status)) {
                            suspendedPasses++;
                            degradedAfter ??= blackhole.Elapsed;
                        } else if (string.Equals(status.LastProgress?.Step, "retrying", StringComparison.Ordinal)) {
                            retryingPasses++;
                        }
                    }

                    var read = await topology.ReadWidgetAsync(world, name, token);
                    resourceReads++;

                    if (read.IsSuccess && read.GetValueOrThrow().ProvisioningState == ProvisioningState.Failed) {
                        resourceReadFailedDuringBlackhole++;
                    }
                }

                // Both operations have been seen suspended: "reconciles suspend" is observed, not inferred.
                if (suspendedPasses >= inFlight.Length) {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }

            output?.WriteLine(
                $"degraded after {(degradedAfter is { } d ? $"{d.TotalSeconds:F1} s" : "never")}; {passesDriven} passes, {retryingPasses} retrying, {suspendedPasses} suspended, "
                + $"{failedOperations.Count} operations failed; {resourceReadFailedDuringBlackhole}/{resourceReads} resource reads showed Failed. Last: {lastProgress}"
            );
        } finally {
            // ── Restore — in a finally, so the k3s comes back whatever the blackhole did to this test.
            //    Timed from the container's start, k3s boot included. ───────────────────────────
            restore = Stopwatch.StartNew();
            await topology.StartClusterAsync(token);
        }

        var apiAnsweredAfter = restore.Elapsed;
        TimeSpan? healthyAfter = null;

        // The connection grain's own timer pings every ChaosSiloConfigurator.PingInterval and marks
        // the cluster Healthy again; the first pass that is not suspended is when a tenant sees it.
        while (restore.Elapsed < HealthyBudget) {
            var driven = await topology.Operation(world.Tenant, inFlight[0].OperationId).DriveAsync();

            if (driven.IsSuccess && !IsSuspended(driven.GetValueOrThrow())) {
                healthyAfter = restore.Elapsed;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        var resumed = new List<string>();

        foreach (var (name, operationId) in inFlight) {
            var (last, _) = await topology.DriveUntilTerminalAsync(world.Tenant, operationId, ConvergeBudget, token);

            if (last?.State != OperationState.Succeeded) {
                resumed.Add($"{name} → {last?.State.ToString() ?? "never answered"}: {last?.Error?.Message ?? last?.LastProgress?.Detail}");
            }
        }

        var resumedAfter = restore.Elapsed;
        var secondPresent = await topology.ConfigMapExistsAsync(world, "bh-second", token);
        var firstMessage = await topology.ConfigMapMessageAsync(world, "bh-first", token);

        // ── The object deleted behind the reconciler's back comes back on the next write. ─────
        await topology.DeleteConfigMapBehindTheReconcilersBackAsync(world, "bh-first", token);
        (await topology.ConfigMapExistsAsync(world, "bh-first", token)).ShouldBeFalse("the raw delete did not remove the ConfigMap.");

        var repair = (await topology.PutWidgetAsync(world, "bh-first", "v3", token)).GetValueOrThrow();
        var (repaired, _) = await topology.DriveUntilTerminalAsync(world.Tenant, repair.OperationId, ConvergeBudget, token);
        var repairedMessage = await topology.ConfigMapMessageAsync(world, "bh-first", token);
        var driftCorrected = repaired?.State == OperationState.Succeeded && string.Equals(repairedMessage, "v3", StringComparison.Ordinal);

        var numbers = new Dictionary<string, double>(StringComparer.Ordinal) {
            ["healthWindowSeconds"] = ChaosSiloConfigurator.HealthStalenessWindow.TotalSeconds,
            ["pingIntervalSeconds"] = ChaosSiloConfigurator.PingInterval.TotalSeconds,
            ["degradedAfterSeconds"] = degradedAfter is { } dd ? Math.Round(dd.TotalSeconds, 1) : -1,
            ["passesDrivenDuringBlackhole"] = passesDriven,
            ["passesRetryingBeforeDegraded"] = retryingPasses,
            ["passesSuspended"] = suspendedPasses,
            ["operationsFailed"] = failedOperations.Count,
            ["resourceReadsDuringBlackhole"] = resourceReads,
            ["resourceReadsShowingFailed"] = resourceReadFailedDuringBlackhole,
            ["apiAnsweredAfterSeconds"] = Math.Round(apiAnsweredAfter.TotalSeconds, 1),
            ["healthyAfterSeconds"] = healthyAfter is { } h ? Math.Round(h.TotalSeconds, 1) : -1,
            ["resumedAfterSeconds"] = Math.Round(resumedAfter.TotalSeconds, 1),
            ["operationsResumed"] = inFlight.Length - resumed.Count,
            ["driftCorrected"] = driftCorrected ? 1 : 0
        };

        var detail =
            $"k3s stopped with a create and an update in flight: the connection went Degraded after "
            + $"{(degradedAfter is { } d2 ? $"{d2.TotalSeconds:F1} s" : "NEVER")} (window {ChaosSiloConfigurator.HealthStalenessWindow.TotalSeconds:F0} s), "
            + $"{retryingPasses} of {passesDriven} passes were retrying before the window closed and {suspendedPasses} were suspended after, {failedOperations.Count} operations failed, and "
            + $"{resourceReadFailedDuringBlackhole}/{resourceReads} resource reads during the window showed Failed; after restore the API server answered "
            + $"at {apiAnsweredAfter.TotalSeconds:F1} s and the connection was Healthy after {(healthyAfter is { } h2 ? $"{h2.TotalSeconds:F1} s" : "NEVER")} and {inFlight.Length - resumed.Count}/{inFlight.Length} operations converged "
            + $"by {resumedAfter.TotalSeconds:F0} s (second widget present: {secondPresent}, first says '{firstMessage}'); the ConfigMap deleted behind "
            + $"the reconciler's back {(driftCorrected ? "was" : "was NOT")} put back by the next update.";

        // ⚠ "Its resources go Degraded" is the invariant's first clause, and a resource that reads
        // Failed during the window is the opposite of it — so it is in `held`, and the row is ✘
        // until ResourceGrain stops recording a retryable pass as Failed (docs/plan/23 finding 3).
        // The first version of this test counted the reads and left them out of the verdict.
        var held = degradedAfter is not null
            && suspendedPasses > 0
            && failedOperations.Count == 0
            && resourceReadFailedDuringBlackhole == 0
            && healthyAfter is not null
            && resumed.Count == 0
            && secondPresent
            && string.Equals(firstMessage, "v2", StringComparison.Ordinal)
            && driftCorrected;

        if (held) {
            topology.Report.Held(4, detail, numbers);
        } else {
            topology.Report.Violated(4, detail, numbers);
        }

        degradedAfter.ShouldNotBeNull($"no pass was suspended within {DegradeBudget} of the cluster going away, so the connection never went Degraded. Last: {lastProgress}");
        suspendedPasses.ShouldBeGreaterThan(0, "no pass was suspended after the cluster went Degraded — docs/plan/09 § Connection health: reconciles are suspended, not failed.");
        failedOperations.ShouldBeEmpty("docs/plan/23 § The chaos invariants, 4: no operations fail. Failed: " + string.Join("; ", failedOperations));

        resourceReadFailedDuringBlackhole.ShouldBe(
            0,
            $"docs/plan/23 § The chaos invariants, 4: its resources go Degraded. {resourceReadFailedDuringBlackhole} of {resourceReads} reads of the "
            + "resources during the blackhole answered ProvisioningState.Failed while their operations were Running and suspended — a tenant "
            + "reading the resource sees 'provisioning failed' for a network outage, which docs/plan/09 § Connection health says must not appear."
        );
        healthyAfter.ShouldNotBeNull($"every pass was still suspended {HealthyBudget} after the cluster came back, so the connection never went Healthy again.");
        resumed.ShouldBeEmpty("clean resumption: " + string.Join("; ", resumed));
        secondPresent.ShouldBeTrue("the create that was in flight during the blackhole says Succeeded and its ConfigMap is not in the cluster.");
        firstMessage.ShouldBe("v2", "the update that was in flight during the blackhole says Succeeded and the ConfigMap does not carry it.");
        driftCorrected.ShouldBeTrue($"the ConfigMap deleted behind the reconciler's back was not restored: operation {repaired?.State}, message '{repairedMessage}'.");
    }

    /// <summary>Whether the operation's last pass was suspended by a Degraded connection — the sentence docs/plan/09 § Connection health mandates.</summary>
    static bool IsSuspended(OperationStatus status) =>
        status.LastProgress?.Detail.Contains("Cannot reach your cluster", StringComparison.Ordinal) == true
        || string.Equals(status.LastProgress?.Step, "waiting-for-cluster", StringComparison.Ordinal);
}
