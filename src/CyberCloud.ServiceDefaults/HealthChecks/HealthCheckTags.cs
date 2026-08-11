namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>
///     The two tags that decide what a Kubernetes probe sees.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The split is the whole design; the tag strings are incidental.</b> A liveness probe
///         that fails <i>restarts the pod</i>; a readiness probe that fails
///         <i>
///             removes it from the
///             Service
///         </i>
///         . Putting a cluster-wide or dependency-wide condition behind
///         <see cref="Live" /> means a Redis blip restarts thirty silos and moves two million
///         grains — the health check causing the outage it was added to detect.
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="Live" />: <i>is this process wedged?</i> Nothing that depends on another
///             machine may be tagged Live. Only the <c>self</c> check is, today.
///         </item>
///         <item>
///             <see cref="Ready" />: <i>would a request routed here be served?</i> This is what
///             gates a rolling upgrade, and the only thing tagged with it on a silo is
///             <c>SiloReadinessHealthCheck</c>.
///         </item>
///     </list>
/// </remarks>
public static class HealthCheckTags {
    /// <summary>Liveness — the Kubernetes <c>livenessProbe</c>. Process-local conditions only.</summary>
    public const string Live = "live";

    /// <summary>Readiness — the Kubernetes <c>readinessProbe</c>. "Am I serving?" and nothing else.</summary>
    public const string Ready = "ready";
}
