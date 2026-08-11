using CyberCloud.Kubernetes.Health;
using CyberCloud.Kubernetes.Informers;

namespace CyberCloud.Kubernetes;

/// <summary>Configuration for the Kubernetes fabric — docs/plan/09.</summary>
public sealed class KubernetesOptions
{
    /// <summary>The configuration section: <c>CyberCloud:Kubernetes</c>.</summary>
    public const string SectionName = "CyberCloud:Kubernetes";

    /// <summary>
    ///     How often a connection grain pings its cluster. Defaults to
    ///     <see cref="ClusterHealthTracker.PingInterval" />.
    /// </summary>
    public TimeSpan PingInterval { get; set; } = ClusterHealthTracker.PingInterval;

    /// <summary>
    ///     ⚠ How long a cluster may go unheard-from before it is <c>Degraded</c>. Defaults to the 90
    ///     seconds docs/plan/09 § Cluster connections states. Configurable because a hostile BYO
    ///     cluster on a bad link may want more, <b>not</b> because the default is a guess.
    /// </summary>
    public TimeSpan HealthStalenessWindow { get; set; } = ClusterHealthTracker.StalenessWindow;

    /// <summary>
    ///     The window informer re-establishment is spread over — docs/plan/09 § Observing.
    /// </summary>
    /// <remarks>
    ///     ⚠ Setting this to <see cref="TimeSpan.Zero" /> disables staggering and re-creates the
    ///     synchronized list storm docs/plan/09 § Observing warns about. It is allowed only because a
    ///     single-silo integration test should not wait out a spread window.
    /// </remarks>
    public TimeSpan InformerStaggerWindow { get; set; } = InformerStagger.DefaultWindow;

    /// <summary>
    ///     Whether a connection grain starts its own health-ping timer on activation.
    /// </summary>
    /// <remarks>
    ///     Off in tests that drive <c>PingAsync</c> by hand, for the reason
    ///     <c>TenancyRefreshOptions.RunBackgroundRefresh</c> gives: a test asserting a health
    ///     transition cannot share a process with a loop that is quietly repairing it.
    /// </remarks>
    public bool RunHealthTimer { get; set; } = true;
}
