using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>
///     Whether <b>this</b> silo is currently serving grain calls.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is the check a rolling upgrade depends on, and the one it is easiest to write
///             a lie for.
///         </b>
///         docs/plan/00 § The quality bar, concretely budgets <i>zero</i> failed tenant requests across a
///         rolling upgrade of a 30-silo cluster. Kubernetes adds a pod to a Service's endpoints the
///         moment its readiness probe passes and removes it the moment the probe fails, so a
///         readiness check that answers "the process is running" hands traffic to a silo that has
///         not joined the cluster yet, and keeps handing it to one that is draining. Both are
///         dropped requests, and both look green on a dashboard.
///     </para>
///     <para>
///         <b>What this actually probes, precisely:</b>
///     </para>
///     <list type="number">
///         <item>
///             It makes a real grain call — <c>IManagementGrain.GetHosts</c> — through this silo's
///             own dispatcher. That is not a formality: it is the difference between "the host
///             started" and "the grain runtime can route a message". If the call throws or times
///             out, the silo is <b>Unhealthy</b>, whatever the membership table says.
///         </item>
///         <item>
///             It then looks <i>itself</i> up in the returned membership snapshot by
///             <see cref="ILocalSiloDetails.SiloAddress" /> and requires
///             <see cref="SiloStatus.Active" />. Orleans only routes work to <c>Active</c> silos, so
///             this is the same predicate the runtime uses, asked from outside.
///         </item>
///     </list>
///     <para>
///         <b>What it deliberately does not probe.</b> Storage. A silo whose Redis shard is
///         unreachable is still correctly serving every grain that does not touch that shard, and
///         failing readiness would evict it from the Service and concentrate the same load on its
///         neighbours — the classic health-check-induced outage. Storage reachability belongs on a
///         metric and an alert, not on a probe that removes capacity.
///         <c>docs/plan/16</c> owns that; Survival's <c>StorageHealthCheck</c> is not copied here
///         for this reason.
///     </para>
///     <para>
///         ⚠ <b><see cref="SiloStatus.Joining" /> is Unhealthy, not Degraded.</b> Degraded still
///         passes the default Kubernetes readiness predicate
///         (<c>HealthStatus.Degraded &gt; Unhealthy</c> and only <c>Unhealthy</c> maps to 503), so a
///         Degraded answer during startup would admit traffic to a silo that cannot serve it. The
///         states are split on exactly one question: <i>would a request routed here be served?</i>
///     </para>
/// </remarks>
sealed class SiloReadinessHealthCheck(
    IGrainFactory grains,
    ILocalSiloDetails localSilo
)
    : IHealthCheck {
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        Dictionary<SiloAddress, SiloStatus> hosts;
        try {
            // The grain call IS the probe. IManagementGrain is Orleans' own system grain, so this
            // asserts the runtime end to end without this assembly declaring a grain of its own.
            hosts = await grains.GetGrain<IManagementGrain>(0).GetHosts().ConfigureAwait(false);
        } catch (Exception error) when (error is not OperationCanceledException) {
            return HealthCheckResult.Unhealthy(
                "This silo could not complete a grain call to IManagementGrain, so it cannot route "
                + "messages. Not ready.",
                error
            );
        }

        if (!hosts.TryGetValue(localSilo.SiloAddress, out var status)) {
            return HealthCheckResult.Unhealthy(
                $"This silo ({localSilo.SiloAddress}) is not in the membership snapshot it just "
                + "read. It has not joined the cluster, so nothing will be routed to it."
            );
        }

        return status switch {
            SiloStatus.Active => HealthCheckResult.Healthy($"Active, in a cluster of {hosts.Count} silo(s)."),

            // Startup. Reporting anything but Unhealthy here is what admits traffic to a silo that
            // cannot serve it — see the remarks.
            SiloStatus.Created or SiloStatus.Joining => HealthCheckResult.Unhealthy(
                $"This silo is {status} and is not serving yet. docs/plan/00 § The quality bar, "
                + "concretely budgets 20 s from "
                + "cold start to serving; if this persists past that, membership is the place to "
                + "look."
            ),

            // Shutdown. The pod must leave the Service's endpoints BEFORE Orleans stops accepting,
            // or the requests in flight during the gap are the failed ones that
            // docs/plan/00 § The quality bar, concretely forbids.
            SiloStatus.ShuttingDown or SiloStatus.Stopping or SiloStatus.Dead =>
                HealthCheckResult.Unhealthy(
                    $"This silo is {status} and is draining. Remove it from the load balancer."
                ),

            _ => HealthCheckResult.Unhealthy($"This silo reports the unexpected status {status}.")
        };
    }
}
