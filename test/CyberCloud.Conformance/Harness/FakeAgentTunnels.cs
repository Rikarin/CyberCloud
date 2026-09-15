using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Collections.Concurrent;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     An in-memory stand-in for the agent-tunnel grain, as <see cref="IAgentTunnels" /> sees it:
///     mint-once enrollment, a status a test can advance, a revocation that sticks.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this does not prove, stated first.</b> It is a dictionary, not a tunnel. Nothing
///         here hashes a token, admits a socket, relays a frame or routes a Kubernetes call. All of
///         that is <c>AgentTunnelGrainTests</c>' — the real grain, the real relay, a real agent over
///         pipes — and the two suites meet at this interface. What this proves is the half that is
///         about the <b>resource</b>: that a connected cluster is <c>Creating</c> until its agent has
///         heartbeated and <c>Succeeded</c> after, that the install command mints a token the grain
///         would be armed with, and that a delete revokes.
///     </para>
///     <para>
///         <see cref="Heartbeat" /> is the lever: it is the test playing the agent's first heartbeat,
///         which in production arrives through the gateway and lands in the grain's state.
///     </para>
/// </remarks>
public sealed class FakeAgentTunnels(IClock clock) : IAgentTunnels {
    readonly ConcurrentDictionary<Guid, AgentTunnelStatus> statuses = new();
    readonly ConcurrentDictionary<Guid, string> enrollmentHashes = new();

    /// <summary>Every enrollment minted, in order — the cluster and the plaintext token.</summary>
    public ConcurrentQueue<(Guid ClusterId, string EnrollmentToken)> Enrollments { get; } = new();

    /// <summary>Every revocation, in order.</summary>
    public ConcurrentQueue<Guid> Revocations { get; } = new();

    /// <summary>Where the install command tells the agent to dial. Fixed, and checked by the suite.</summary>
    public const string TunnelEndpoint = "wss://conformance.example/agent/v1/tunnel";

    /// <summary>Forgets everything.</summary>
    public void Reset() {
        statuses.Clear();
        enrollmentHashes.Clear();
        Enrollments.Clear();
        Revocations.Clear();
    }

    /// <summary>Whether the current enrollment token for a cluster hashes to this — the grain's own check, in miniature.</summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="token">A plaintext token.</param>
    public bool WouldAdmit(Guid clusterId, string token) =>
        enrollmentHashes.TryGetValue(clusterId, out var hash) && AgentCredentials.HashesMatch(hash, AgentCredentials.Hash(token));

    /// <summary>The agent's first (or next) heartbeat — the event the resource converges on.</summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="agentVersion">What the agent reports.</param>
    /// <param name="kubernetesVersion">What the API server reports.</param>
    public void Heartbeat(Guid clusterId, string agentVersion = "conformance-agent", string kubernetesVersion = "v1.35.0") {
        var now = clock.UtcNow;

        statuses.AddOrUpdate(
            clusterId,
            _ => throw new InvalidOperationException(
                $"A heartbeat for cluster {clusterId:D} arrived before it was armed. The install "
                + "command mints the token an agent connects with; a heartbeat with no enrollment is "
                + "not a state the real grain can be in."
            ),
            (_, current) => current with {
                Connected = true,
                EnrollmentOpen = false,
                FirstHeartbeatAt = current.FirstHeartbeatAt == default ? now : current.FirstHeartbeatAt,
                LastHeartbeatAt = now,
                AgentVersion = agentVersion,
                KubernetesVersion = kubernetesVersion
            }
        );

        enrollmentHashes.TryRemove(clusterId, out _);
    }

    /// <summary>The agent went away — its socket closed. The first heartbeat stays recorded.</summary>
    /// <param name="clusterId">The cluster.</param>
    public void Disconnect(Guid clusterId) {
        if (statuses.TryGetValue(clusterId, out var current)) {
            statuses[clusterId] = current with { Connected = false };
        }
    }

    /// <inheritdoc />
    public Task<Result<AgentEnrollment>> EnrollAsync(Guid clusterId, Guid owningTenantId, CancellationToken cancellationToken = default) {
        var minted = AgentCredentials.MintEnrollment();
        var expiresAt = clock.UtcNow + AgentCredentials.EnrollmentLifetime;

        statuses.AddOrUpdate(
            clusterId,
            _ => new() { ClusterId = clusterId, Armed = true, EnrollmentOpen = true },
            (_, current) => current with { Armed = true, EnrollmentOpen = true, Revoked = false }
        );

        // ⚠ Replaced, not added: a second install command voids the first token, as the grain does.
        enrollmentHashes[clusterId] = minted.Hash;
        Enrollments.Enqueue((clusterId, minted.Plaintext));

        return Task.FromResult(
            Result<AgentEnrollment>.Success(
                new() {
                    ClusterId = clusterId,
                    EnrollmentToken = minted.Plaintext,
                    ExpiresAt = expiresAt,
                    TunnelEndpoint = TunnelEndpoint,
                    ChartReference = "oci://conformance.example/charts/cybercloud-agent",
                    AgentImage = string.Empty,
                    HeartbeatInterval = TimeSpan.FromSeconds(15)
                }
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<AgentTunnelStatus>> GetStatusAsync(Guid clusterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<AgentTunnelStatus>.Success(
                statuses.TryGetValue(clusterId, out var status) ? status : new() { ClusterId = clusterId }
            )
        );

    /// <inheritdoc />
    public Task<Result> RevokeAsync(Guid clusterId, CancellationToken cancellationToken = default) {
        Revocations.Enqueue(clusterId);
        enrollmentHashes.TryRemove(clusterId, out _);

        if (statuses.TryGetValue(clusterId, out var current)) {
            statuses[clusterId] = current with { Revoked = true, Connected = false, EnrollmentOpen = false };
        }

        return Task.FromResult(Result.Success);
    }
}

/// <summary>
///     An <see cref="IClusterConnectionRegistrar" /> that records what the driver attached, so a
///     suite can assert a converged cluster was registered — and under which tenant.
/// </summary>
/// <remarks>
///     ⚠ Until this existed the harness ran with <c>UnavailableClusterConnectionRegistrar</c>, and
///     no case noticed, because no case converged on a pass that reported a connection: the managed
///     cluster's Cluster API status is never written by anything in a Docker-free run. The
///     connected cluster's converging pass reports one every time, and the refusing default would
///     have failed it with "could not be registered" — for the harness's reason, not the provider's.
/// </remarks>
public sealed class RecordingClusterConnectionRegistrar : IClusterConnectionRegistrar {
    /// <summary>Every descriptor attached, in order.</summary>
    public ConcurrentQueue<ClusterConnectionDescriptor> Attached { get; } = new();

    /// <summary>Forgets everything.</summary>
    public void Reset() => Attached.Clear();

    /// <inheritdoc />
    public Task<Result> AttachAsync(ClusterConnectionDescriptor descriptor, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(descriptor);
        Attached.Enqueue(descriptor);
        return Task.FromResult(Result.Success);
    }
}
