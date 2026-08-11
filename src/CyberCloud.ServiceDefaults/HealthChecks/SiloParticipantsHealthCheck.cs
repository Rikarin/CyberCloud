using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>
///     What the Orleans runtime says about itself.
/// </summary>
/// <remarks>
///     <para>
///         Orleans' own subsystems — the incoming/outgoing message queues, the membership oracle,
///         the local grain directory — implement <see cref="IHealthCheckParticipant" /> and report
///         whether they have made progress since a given instant. This surfaces that, unchanged;
///         it is the one piece of Survival's health-check set that is copied more or less as-is,
///         because it is a thin adapter over a runtime interface and there is no second way to
///         write it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Degraded, not Unhealthy, and that is the point of keeping it separate from
///             <see cref="SiloReadinessHealthCheck" />.
///         </b>
///         A participant that has not made progress is
///         a strong signal and a weak proof: a silo genuinely idle for the interval reports the
///         same thing as a wedged one. Mapping it to Unhealthy would make an idle cluster evict
///         itself. It is here to be scraped and alerted on, not to move a pod out of a Service.
///     </para>
///     <para>
///         ⚠ <b>Statefulness.</b> The check remembers when it last ran and asks the participants
///         about that window, so its answer depends on the probe interval. It is registered
///         singleton for that reason; two instances would each see half the window and neither
///         would be right.
///     </para>
/// </remarks>
sealed class SiloParticipantsHealthCheck(IEnumerable<IHealthCheckParticipant> participants)
    : IHealthCheck {
    long lastCheckedTicks = DateTime.UtcNow.ToBinary();

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        var lastChecked = DateTime.FromBinary(Interlocked.Exchange(ref lastCheckedTicks, DateTime.UtcNow.ToBinary()));

        var complaints = participants
            .Select(participant =>
                (participant, healthy: participant.CheckHealth(lastChecked, out var reason), reason)
            )
            .Where(x => !x.healthy)
            .Select(x => $"{x.participant.GetType().Name}: {x.reason}")
            .ToList();

        return Task.FromResult(
            complaints.Count == 0
                ? HealthCheckResult.Healthy($"{lastChecked:O} → now: all participants progressed.")
                : HealthCheckResult.Degraded(
                    $"{complaints.Count} Orleans subsystem(s) reported no progress since "
                    + $"{lastChecked:O}: {string.Join("; ", complaints)}"
                )
        );
    }
}
