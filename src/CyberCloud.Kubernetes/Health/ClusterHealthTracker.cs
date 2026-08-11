using CyberCloud.Core.Time;

namespace CyberCloud.Kubernetes.Health;

/// <summary>
///     Connection health as docs/plan/09 § Cluster connections defines it: a cluster that has not
///     answered a ping in <see cref="StalenessWindow" /> is <see cref="ClusterHealthState.Degraded" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rule this type encodes, and the reason it is a type at all.</b> docs/plan/09
///         § Cluster connections:
///         <i>
///             "A cluster that has not answered a ping in 90 seconds is
///             Degraded; its resources' reconciles are suspended (not failed) and the portal says
///             'cannot reach your cluster' instead of 'provisioning failed'. The distinction between
///             our failure and unreachable is what stops a tenant's network outage from looking like a
///             platform bug."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Degraded is decided by elapsed time, not by a failure count.</b> Those differ in the
///         case that matters: a silo that stops pinging — because it was busy, or because the
///         reminder did not fire — records no failures at all, and a count-based rule would call
///         that cluster healthy while nothing had confirmed it for an hour. The window is measured
///         from the last <i>success</i>, so "we have not heard from it" and "it told us no" converge
///         on the same answer.
///     </para>
///     <para>
///         <b>Pure and clock-injected</b> (<see cref="IClock" />), so that the 90-second transition
///         is asserted by advancing a clock rather than by a test that sleeps for 90 seconds — which
///         is a test nobody runs. Same argument as <c>TestClock</c> in the tenancy suite.
///     </para>
/// </remarks>
public sealed class ClusterHealthTracker(Guid clusterId, IClock clock, TimeSpan? stalenessWindow = null) {
    /// <summary>
    ///     90 seconds — docs/plan/09 § Cluster connections, stated as a number and therefore
    ///     transcribed as one.
    /// </summary>
    public static readonly TimeSpan StalenessWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     How often the connection grain's reminder pings. A third of the window, so that two
    ///     consecutive pings can be lost before the cluster is called degraded — one lost ping is a
    ///     dropped packet, three is an outage.
    /// </summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    DateTimeOffset lastSuccessAt;
    DateTimeOffset lastAttemptAt;
    bool everSucceeded;
    int consecutiveFailures;
    string message = string.Empty;

    /// <summary>The window this instance is using.</summary>
    public TimeSpan Window { get; } = stalenessWindow ?? StalenessWindow;

    /// <summary>Health as of now.</summary>
    /// <remarks>
    ///     ⚠ Computed on read rather than on write, deliberately. The transition to
    ///     <see cref="ClusterHealthState.Degraded" /> is caused by <i>time passing</i>, and nothing
    ///     writes when time passes. A state field updated only by ping callbacks would stay
    ///     <see cref="ClusterHealthState.Healthy" /> forever on a silo whose reminder stopped firing
    ///     — which is precisely the case this property exists to catch.
    /// </remarks>
    public ClusterHealth Current {
        get {
            var now = clock.UtcNow;

            var state = !everSucceeded
                ? consecutiveFailures > 0 ? ClusterHealthState.Degraded : ClusterHealthState.Unknown
                : now - lastSuccessAt >= Window
                    ? ClusterHealthState.Degraded
                    : ClusterHealthState.Healthy;

            return new() {
                ClusterId = clusterId,
                State = state,
                LastSuccessAt = lastSuccessAt,
                LastAttemptAt = lastAttemptAt,
                ConsecutiveFailures = consecutiveFailures,
                Message = state == ClusterHealthState.Degraded ? DegradedMessage(now) : string.Empty
            };
        }
    }

    /// <summary>
    ///     Restores the last known success from durable state, so that a rehomed activation does not
    ///     start its window over.
    /// </summary>
    /// <param name="at">When a ping last succeeded.</param>
    /// <remarks>
    ///     ⚠ Without this, an activation that moved silos would report
    ///     <see cref="ClusterHealthState.Unknown" /> and — worse — could not report
    ///     <see cref="ClusterHealthState.Degraded" />, because the transition is measured from the
    ///     last success and it would have none. A cluster that went unreachable an hour ago would
    ///     read as "no information" rather than "cannot reach your cluster" for a full window after
    ///     every rebalance.
    /// </remarks>
    public void SeedLastSuccess(DateTimeOffset at) {
        lastSuccessAt = at;
        lastAttemptAt = at;
        everSucceeded = true;
    }

    /// <summary>Records a successful ping.</summary>
    public void RecordSuccess() {
        lastAttemptAt = clock.UtcNow;
        lastSuccessAt = lastAttemptAt;
        everSucceeded = true;
        consecutiveFailures = 0;
        message = string.Empty;
    }

    /// <summary>Records a ping that did not answer.</summary>
    /// <param name="reason">What went wrong, for <see cref="ClusterHealth.Message" />.</param>
    public void RecordFailure(string reason) {
        lastAttemptAt = clock.UtcNow;
        consecutiveFailures++;
        message = reason;
    }

    /// <summary>
    ///     The portal's text. docs/plan/09 § Cluster connections wants "cannot reach your cluster",
    ///     not "provisioning failed", and the wording is part of the requirement rather than
    ///     decoration — it is what tells a tenant the problem is on their side of the connection.
    /// </summary>
    string DegradedMessage(DateTimeOffset now) {
        var forHowLong = everSucceeded
            ? $"for {(now - lastSuccessAt).TotalSeconds:F0} seconds"
            : "since the platform first tried";

        var because = message.Length > 0 ? $" Last error: {message}." : string.Empty;

        return $"Cannot reach your cluster — it has not answered a health check {forHowLong} "
            + $"(the limit is {Window.TotalSeconds:F0} seconds).{because} Reconciles for "
            + "resources on this cluster are suspended, not failed, and will resume automatically "
            + "when the cluster is reachable again.";
    }
}
