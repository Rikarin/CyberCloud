namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>
///     What <c>listInstallCommand</c> hands the tenant: the one-time token and where the agent
///     should dial.
/// </summary>
/// <remarks>
///     ⚠ Not <c>[GenerateSerializer]</c>, and it must never become so. <see cref="EnrollmentToken" />
///     is a plaintext credential; this record is built in the process that minted it and goes into
///     one HTTP response body and nowhere else — no operation record, no grain state, no log line.
/// </remarks>
public sealed record AgentEnrollment {
    /// <summary>The cluster the token admits an agent for.</summary>
    public Guid ClusterId { get; init; }

    /// <summary>The one-time token. Shown once.</summary>
    public string EnrollmentToken { get; init; } = string.Empty;

    /// <summary>When the token stops being accepted.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The WebSocket URL the agent dials — <c>wss://{gateway}/agent/v1/tunnel</c>.</summary>
    public string TunnelEndpoint { get; init; } = string.Empty;

    /// <summary>The chart the install command references — an OCI reference or a repo path.</summary>
    public string ChartReference { get; init; } = string.Empty;

    /// <summary>The agent image the chart is told to run, by digest where the deployment has one.</summary>
    public string AgentImage { get; init; } = string.Empty;

    /// <summary>
    ///     The heartbeat interval the install command passes to the chart — the resource's own, and
    ///     the same value the tunnel grain was armed with, so the welcome agrees with the chart.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; }
}

/// <summary>
///     The agent-tunnel side of the fabric, as a provider may see it: arm a cluster's tunnel, read
///     its status, revoke it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A seam for the reason <c>IClusterConnectionRegistrar</c> is one.</b> A provider
///         references <c>CyberCloud.Kubernetes.Contracts</c> only (docs/plan/03 § The .Contracts
///         split) and may not take a grain factory, so <c>ConnectedClusterReconciler</c> cannot
///         reach <see cref="IAgentTunnelGrain" /> — and it should not: minting a credential and
///         reading whether an agent has heartbeated are the manager's acts, made on the provider's
///         behalf. <c>ReconcileContext.Agents</c> and <c>ActionContext.Agents</c> carry this; the
///         resource manager's <c>GrainAgentTunnels</c> implements it over the grain.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The plaintext token is minted here and hashed here, on the calling side of the
///             grain.
///         </b> The grain is armed with a hash; the plaintext goes into the
///         <see cref="AgentEnrollment" /> and out through one action response. See
///         <see cref="AgentCredentials" /> for why that ordering is the security property.
///     </para>
/// </remarks>
public interface IAgentTunnels {
    /// <summary>Mints a one-time enrollment token and arms the cluster's tunnel with its hash.</summary>
    /// <param name="clusterId">The connected-cluster resource's id.</param>
    /// <param name="owningTenantId">The tenant the resource belongs to — a fact from the manager, never from a body.</param>
    /// <param name="heartbeatInterval">
    ///     The resource's <c>heartbeatSeconds</c>. Armed into the grain so every welcome carries it,
    ///     and returned in the enrollment so the install command passes the same value to the chart.
    /// </param>
    /// <param name="cancellationToken">The caller's budget.</param>
    Task<Result<AgentEnrollment>> EnrollAsync(
        Guid clusterId,
        Guid owningTenantId,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken = default
    );

    /// <summary>What the platform knows about the cluster's agent, without touching the network.</summary>
    /// <param name="clusterId">The connected-cluster resource's id.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    Task<Result<AgentTunnelStatus>> GetStatusAsync(Guid clusterId, CancellationToken cancellationToken = default);

    /// <summary>Forgets every credential and drops the agent. The resource was deleted.</summary>
    /// <param name="clusterId">The connected-cluster resource's id.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    Task<Result> RevokeAsync(Guid clusterId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The <see cref="IAgentTunnels" /> a context carries when nobody supplied one. Every call
///     fails, naming the seam.
/// </summary>
/// <remarks>
///     ⚠ Fails rather than answering "not armed, no heartbeat", for the reason
///     <c>RefusingClusterConnectionSink</c> throws: a default that reports a plausible status would
///     let a connected-cluster resource sit <c>Creating</c> forever in a host that forgot to
///     register the real seam, with the reconciler's own log saying the agent has not connected yet.
/// </remarks>
public sealed class UnavailableAgentTunnels : IAgentTunnels {
    /// <inheritdoc />
    public Task<Result<AgentEnrollment>> EnrollAsync(
        Guid clusterId,
        Guid owningTenantId,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result<AgentEnrollment>.Failure(Unavailable(clusterId, "enroll an agent for")));

    /// <inheritdoc />
    public Task<Result<AgentTunnelStatus>> GetStatusAsync(
        Guid clusterId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result<AgentTunnelStatus>.Failure(Unavailable(clusterId, "read the agent status of")));

    /// <inheritdoc />
    public Task<Result> RevokeAsync(Guid clusterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Unavailable(clusterId, "revoke the agent of")));

    static Error Unavailable(Guid clusterId, string verb) =>
        new(
            ErrorCode.InternalError,
            $"This host has no IAgentTunnels, so it cannot {verb} cluster {clusterId:D}. A silo or "
            + "gateway that composes the resource manager registers GrainAgentTunnels; a context "
            + "built by hand supplies one through ReconcileContext.Agents or ActionContext.Agents."
        );
}
