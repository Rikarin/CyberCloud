using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>
///     The shape of the cluster as a whole, from wherever this is running.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Never Unhealthy, by construction.</b> This check describes <i>peers</i>, and a peer
///         being down is not a reason to take <i>this</i> node out of service — it is a reason to
///         keep it in, since the remaining capacity is now more load-bearing, not less. A cluster
///         check wired into a readiness probe is a cascading-failure generator: one silo dies, every
///         surviving silo reports the same degradation, every one is evicted, and the outage the
///         probe existed to prevent is the outage the probe caused. The only statuses it returns are
///         <see cref="HealthStatus.Healthy" /> and <see cref="HealthStatus.Degraded" />.
///     </para>
///     <para>
///         It is registered on both the silo and the client, because the gateway wants the same
///         picture and gets it through the same system grain.
///     </para>
/// </remarks>
sealed class ClusterHealthCheck(IGrainFactory grains) : IHealthCheck {
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        Dictionary<SiloAddress, SiloStatus> hosts;
        try {
            hosts = await grains.GetGrain<IManagementGrain>(0).GetHosts().ConfigureAwait(false);
        } catch (Exception error) when (error is not OperationCanceledException) {
            return HealthCheckResult.Degraded("Could not read the cluster membership table.", error);
        }

        var active = hosts.Values.Count(x => x is SiloStatus.Active);
        var leaving = hosts.Values.Count(x => x is SiloStatus.ShuttingDown or SiloStatus.Stopping);

        if (active == 0) {
            // Degraded rather than Unhealthy even here: on a silo, "no active silos" cannot be true
            // while this code is running, so it means the read was stale; on a client it means the
            // gateway list is empty, which the client's own reconnect handles.
            return HealthCheckResult.Degraded($"No active silos in a membership table of {hosts.Count} entries.");
        }

        return leaving > 0
            ? HealthCheckResult.Degraded(
                $"{active} silo(s) active, {leaving} shutting down — a rolling upgrade or a "
                + "scale-in is in progress."
            )
            : HealthCheckResult.Healthy($"{active} silo(s) active.");
    }
}
