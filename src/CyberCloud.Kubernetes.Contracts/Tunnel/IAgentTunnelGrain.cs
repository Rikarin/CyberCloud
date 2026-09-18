using System.Globalization;

namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>
///     What <c>listInstallCommand</c> arms the tunnel with: who owns the cluster and the hash of the
///     one-time token the install command carries.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.AgentArmRequest")]
public sealed record AgentArmRequest {
    /// <summary>The tenant whose resource the cluster is. Fixed on the first arm; checked on every later one.</summary>
    [Id(0)]
    public Guid OwningTenantId { get; init; }

    /// <summary><see cref="AgentCredentials.Hash" /> of the enrollment token. Never the token.</summary>
    [Id(1)]
    public string EnrollmentHash { get; init; } = string.Empty;

    /// <summary>When the enrollment token stops being accepted.</summary>
    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    ///     How often the agent is told to heartbeat, in every welcome from now on. Zero means the
    ///     platform's default.
    /// </summary>
    /// <remarks>
    ///     ⚠ The resource's <c>heartbeatSeconds</c> travels two ways from <c>listInstallCommand</c>:
    ///     into the chart through the command, and into the grain through this — and the agent
    ///     adopts the <i>welcome</i>. Before this member existed the welcome carried
    ///     <c>KubernetesOptions.AgentHeartbeatInterval</c> regardless, and a resource asking for
    ///     thirty seconds got fifteen.
    /// </remarks>
    [Id(3)]
    public TimeSpan HeartbeatInterval { get; init; }
}

/// <summary>What an agent presented on the upgrade request, already hashed by the gateway.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.AgentHello")]
public sealed record AgentHello {
    /// <summary><see cref="AgentCredentials.Hash" /> of the bearer value the agent sent.</summary>
    [Id(0)]
    public string PresentedHash { get; init; } = string.Empty;

    /// <summary>Whether the bearer value had the enrollment prefix rather than the credential one.</summary>
    [Id(1)]
    public bool PresentedEnrollment { get; init; }

    /// <summary>
    ///     <see cref="AgentCredentials.Hash" /> of a credential the gateway minted for this
    ///     connection. Stored only when <see cref="PresentedEnrollment" /> is true and the token
    ///     matched; the gateway then hands the plaintext to the agent in the welcome.
    /// </summary>
    [Id(2)]
    public string ReplacementHash { get; init; } = string.Empty;

    /// <summary>The agent's version header, for the status the portal shows.</summary>
    [Id(3)]
    public string AgentVersion { get; init; } = string.Empty;
}

/// <summary>What the grain answers when it accepts an agent.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.AgentAcceptance")]
public sealed record AgentAcceptance {
    /// <summary>The session, named in every frame delivery from now on.</summary>
    [Id(0)]
    public Guid SessionId { get; init; }

    /// <summary>
    ///     Whether the enrollment token was spent on this connection — in which case the gateway
    ///     hands the agent the replacement credential whose hash it sent.
    /// </summary>
    [Id(1)]
    public bool EnrollmentConsumed { get; init; }

    /// <summary>How often the agent is expected to heartbeat.</summary>
    [Id(2)]
    public TimeSpan HeartbeatInterval { get; init; }
}

/// <summary>What the platform knows about one cluster's agent, without touching the network.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Kubernetes.Tunnel.AgentTunnelStatus")]
public sealed record AgentTunnelStatus {
    /// <summary>The cluster.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>Whether <c>listInstallCommand</c> has ever armed this tunnel.</summary>
    [Id(1)]
    public bool Armed { get; init; }

    /// <summary>Whether an enrollment token is currently valid and unspent.</summary>
    [Id(2)]
    public bool EnrollmentOpen { get; init; }

    /// <summary>Whether an agent holds a session right now.</summary>
    [Id(3)]
    public bool Connected { get; init; }

    /// <summary>When the last heartbeat arrived. <c>default</c> when none ever has.</summary>
    [Id(4)]
    public DateTimeOffset LastHeartbeatAt { get; init; }

    /// <summary>When the first heartbeat arrived — what makes the resource <c>Succeeded</c>. <c>default</c> until then.</summary>
    [Id(5)]
    public DateTimeOffset FirstHeartbeatAt { get; init; }

    /// <summary>The version the connected or last-connected agent reported.</summary>
    [Id(6)]
    public string AgentVersion { get; init; } = string.Empty;

    /// <summary>The API server version the agent last reported. Empty until a heartbeat carries one.</summary>
    [Id(7)]
    public string KubernetesVersion { get; init; } = string.Empty;

    /// <summary>Whether the cluster was revoked — its resource deleted — so no credential is accepted.</summary>
    [Id(8)]
    public bool Revoked { get; init; }

    /// <summary>Whether the agent has ever heartbeated.</summary>
    public bool HasHeartbeated => FirstHeartbeatAt != default;

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"agent for cluster {ClusterId:D}: armed={Armed}, connected={Connected}, lastHeartbeat={LastHeartbeatAt:O}"
        );
}

/// <summary>
///     The gateway's side of one agent session, as the grain sees it: where frames for the agent go.
/// </summary>
/// <remarks>
///     ⚠ <b>A grain observer, because the socket is in the gateway and the grain is in a silo.</b>
///     The gateway is an Orleans client (docs/plan/10 § Shape) and a grain cannot call a client
///     except through one of these. The grain calls <see cref="SendAsync" /> for every request the
///     connection grain routes; the gateway writes it to the socket. The reverse direction — a
///     response or heartbeat off the socket — is an ordinary grain call,
///     <see cref="IAgentTunnelGrain.DeliverAsync" />.
/// </remarks>
public interface IAgentTunnelObserver : IGrainObserver {
    /// <summary>Writes one frame to the agent.</summary>
    /// <param name="frame">A request, or a goodbye.</param>
    [Alias("Send")]
    Task SendAsync(TunnelFrame frame);
}

/// <summary>
///     One cluster's agent tunnel: the credential that admits its agent, the session the agent
///     holds, and the requests in flight down it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Cluster connections, the <c>AgentInitiated</c> row:
///         <i>
///             "the tunnel identity
///             is bound to the cluster resource id at the gateway"
///         </i>. This grain is that binding. It is
///         keyed by the same null-tenant key as <c>IClusterConnectionGrain</c> —
///         <c>cluster/{clusterId:N}</c>, <c>GrainKeys.ClusterConnection</c> — because it is the same
///         cluster's second platform grain, and because the analyzers already know that key builder
///         is null-tenant. Orleans identity is (type, key), so the two grains do not collide.
///     </para>
///     <para>
///         ⚠ <b>Why a second grain rather than methods on the connection grain.</b> Every method on
///         <c>IClusterConnectionGrain</c> checks the caller's tenant first, and
///         <c>ClusterConnectionTenancyTests</c> probes that there is no method without it. The
///         gateway is a <i>client</i> caller, which that check refuses on purpose — so the three
///         methods the gateway needs (<see cref="AcceptAsync" />, <see cref="DeliverAsync" />,
///         <see cref="DisconnectedAsync" />) cannot live there without an exception to the one rule
///         that grain exists to keep. Here the credential is the check: an agent that knows the
///         token is the agent, whoever relayed it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The tenancy check on the other side is <see cref="ExchangeAsync" />'s, and it is the
///             load-bearing one.
///         </b> A tenant grain can address this key — tenant → null-tenant edges are
///         allowed platform-wide — so a reconciler in tenant B could route a request down tenant A's
///         tunnel if nothing stopped it. <see cref="ExchangeAsync" /> accepts only a null-tenant
///         caller, which in production is exactly one thing: the connection grain, after it has made
///         the owner check docs/plan/06 § Grain keys puts on every call.
///         <c>AgentTunnelGrainTests.ATenantGrainCannotExchangeDownAnotherTenantsTunnel</c> is the
///         probe.
///     </para>
/// </remarks>
[Alias("K8s.AgentTunnel")]
public interface IAgentTunnelGrain : IGrainWithStringKey {
    /// <summary>
    ///     Arms the tunnel with a fresh enrollment token's hash. Called by <c>listInstallCommand</c>,
    ///     through <see cref="IAgentTunnels" />.
    /// </summary>
    /// <param name="request">The owner, the hash, and the heartbeat interval the welcome will carry.</param>
    /// <remarks>
    ///     ⚠ Re-arming replaces the previous enrollment token — the tenant asked for a new install
    ///     command, so the old one must stop working — and leaves a connected agent's credential
    ///     alone, because a re-issued command is not a revocation. It does replace the heartbeat
    ///     interval, which a connected agent adopts on its next session.
    /// </remarks>
    [Alias("Arm")]
    Task<Result> ArmAsync(AgentArmRequest request);

    /// <summary>Admits an agent whose credential the gateway has hashed.</summary>
    /// <param name="hello">What was presented.</param>
    /// <param name="observer">Where to send frames for this agent.</param>
    /// <returns>
    ///     The session, or a refusal. ⚠ Every refusal is the same <see cref="ErrorCode.AuthorizationFailed" />
    ///     with the same message — an unarmed cluster, a spent token, an expired one, a wrong one,
    ///     and a revoked cluster are indistinguishable to the caller, so a stolen install command
    ///     cannot be used to learn which of them it is.
    /// </returns>
    /// <remarks>
    ///     An agent already holding a session is replaced: the old session's observer gets a
    ///     goodbye and its pending requests fail. A pod that was restarted by the kubelet reconnects
    ///     before the old socket's NAT mapping has timed out, and waiting for that would leave the
    ///     cluster unreachable for minutes.
    /// </remarks>
    [Alias("Accept")]
    Task<Result<AgentAcceptance>> AcceptAsync(AgentHello hello, IAgentTunnelObserver observer);

    /// <summary>Delivers a frame that came off the agent's socket.</summary>
    /// <param name="sessionId">The session the gateway got from <see cref="AcceptAsync" />.</param>
    /// <param name="frame">A response or a heartbeat. Anything else is logged and dropped.</param>
    /// <remarks>
    ///     A frame for a session that is not the current one is dropped silently: it is the tail of
    ///     a socket that was replaced, and there is nothing useful to say to it.
    /// </remarks>
    [Alias("Deliver")]
    Task DeliverAsync(Guid sessionId, TunnelFrame frame);

    /// <summary>The gateway lost the socket.</summary>
    /// <param name="sessionId">Which session.</param>
    /// <param name="reason">Why, for the log.</param>
    [Alias("Disconnected")]
    Task DisconnectedAsync(Guid sessionId, string reason);

    /// <summary>
    ///     Sends one request down the tunnel and waits for its response. ⚠ Null-tenant callers only
    ///     — see the remarks on this interface.
    /// </summary>
    /// <param name="request">A <see cref="TunnelFrameKind.Request" />. Its id is assigned here.</param>
    /// <returns>
    ///     The response frame, or a failure: <see cref="ErrorCode.OperationTimeout" /> when the
    ///     agent did not answer within the tunnel's request timeout, and
    ///     <see cref="ErrorCode.ProvisioningFailed" /> when no agent is connected. ⚠ Neither counts
    ///     as the cluster answering — <c>KubeFailures.MeansTheClusterAnswered</c> is false for both
    ///     — so either drives the connection grain's health toward <c>Degraded</c>, which is what a
    ///     tunnel with nobody at the other end should do.
    /// </returns>
    [Alias("Exchange")]
    Task<Result<TunnelFrame>> ExchangeAsync(TunnelFrame request);

    /// <summary>What the platform knows, without touching the network.</summary>
    [Alias("Status")]
    Task<Result<AgentTunnelStatus>> GetStatusAsync();

    /// <summary>
    ///     Forgets every credential and closes the session. The cluster's resource was deleted.
    /// </summary>
    /// <remarks>
    ///     Idempotent, because a delete pass runs until it converges. A revoked tunnel can be armed
    ///     again — the tenant re-created the resource under the same id, which the resource manager
    ///     does not do, but which is not this grain's rule to make.
    /// </remarks>
    [Alias("Revoke")]
    Task<Result> RevokeAsync();
}
