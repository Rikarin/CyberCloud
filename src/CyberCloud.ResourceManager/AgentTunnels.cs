using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using Microsoft.Extensions.Options;

namespace CyberCloud.ResourceManager;

/// <summary>
///     What an install command tells the agent about this deployment — where to dial and what to run.
/// </summary>
/// <remarks>
///     Bound from <c>CyberCloud:Agent</c>. The gateway composition defaults
///     <see cref="TunnelEndpoint" /> from its own public base URI, so a deployment that sets nothing
///     gets an install command that points at the gateway that minted it.
/// </remarks>
public sealed class AgentTunnelOptions {
    /// <summary>The configuration section: <c>CyberCloud:Agent</c>.</summary>
    public const string SectionName = "CyberCloud:Agent";

    /// <summary>The WebSocket URL the agent dials — <c>wss://{gateway}/agent/v1/tunnel</c>.</summary>
    public string TunnelEndpoint { get; set; } = string.Empty;

    /// <summary>
    ///     The chart reference the install command names. <c>charts/agent</c> — the path in a
    ///     checkout of this repository — until a deployment publishes the chart and sets this to
    ///     where.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not an OCI reference by default, because nothing publishes one.</b> <c>Build.Charts</c>
    ///     packages into <c>artifacts/charts/</c> and pushes nowhere, and the first cut defaulted to
    ///     <c>oci://ghcr.io/rikarin/cybercloud/charts/cybercloud-agent</c> — a reference no build
    ///     produces, under a name the chart does not have (<c>helm package</c> names the tarball
    ///     after <c>Chart.yaml</c>'s <c>name</c>, which is <c>agent</c>). A tenant pasting that
    ///     command got a pull error. A deployment that publishes the chart sets this to its own
    ///     <c>oci://…/agent</c>; until then the command is runnable from a checkout, which the
    ///     response's <c>/chart</c> says. <c>charts/agent/conformance.yaml § owed</c>,
    ///     <c>the-chart-is-not-published</c>.
    /// </remarks>
    public string ChartReference { get; set; } = "charts/agent";

    /// <summary>
    ///     The agent image the chart is told to run. ⚠ By digest in any real deployment —
    ///     docs/plan/18 § Platform security, "a pinned digest, never a tag" — which is what
    ///     <c>Build.Images</c> prints for <c>cybercloud-agent-host</c>. Empty leaves the chart's own
    ///     default, a tag, which is what a development install pulls.
    /// </summary>
    public string AgentImage { get; set; } = string.Empty;
}

/// <summary>
///     The <see cref="IAgentTunnels" /> that writes to and reads from <see cref="IAgentTunnelGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         In this assembly for the reason <see cref="GrainClusterConnectionRegistrar" /> is: the
///         obvious home is beside the grain in <c>CyberCloud.Kubernetes</c>, and the seam's consumer
///         is the reconcile driver here, so the implementation sits on the side of the edge
///         <c>module-layering.txt</c> already declares.
///     </para>
///     <para>
///         ⚠ <b>The plaintext token is minted here, hashed here, and leaves here exactly once</b> —
///         inside the <see cref="AgentEnrollment" /> the action handler turns into a response body.
///         The grain is armed with the hash. See <see cref="AgentCredentials" />.
///     </para>
///     <para>
///         ⚠ <b>The grain reference is unqualified</b>, for the reason the registrar gives: the tunnel
///         shares the connection grain's null-tenant key, and there is one per cluster platform-wide.
///     </para>
/// </remarks>
/// <param name="grains">The grain factory.</param>
/// <param name="options">Where the agent dials and what it runs.</param>
/// <param name="clock">What the token's expiry is measured from.</param>
public sealed class GrainAgentTunnels(IGrainFactory grains, IOptions<AgentTunnelOptions> options, IClock clock)
    : IAgentTunnels {
    /// <inheritdoc />
    public async Task<Result<AgentEnrollment>> EnrollAsync(
        Guid clusterId,
        Guid owningTenantId,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.TunnelEndpoint)) {
            return Result<AgentEnrollment>.Failure(
                ErrorCode.InternalError,
                $"{AgentTunnelOptions.SectionName}:TunnelEndpoint is not configured, so an install "
                + "command would tell the agent to dial nowhere. Set it to the gateway's public "
                + "wss://…/agent/v1/tunnel."
            );
        }

        var minted = AgentCredentials.MintEnrollment();
        var expiresAt = clock.UtcNow + AgentCredentials.EnrollmentLifetime;

        var armed = await Grain(clusterId)
            .ArmAsync(
                new() {
                    OwningTenantId = owningTenantId,
                    EnrollmentHash = minted.Hash,
                    ExpiresAt = expiresAt,
                    HeartbeatInterval = heartbeatInterval
                }
            );

        if (armed.TryGetError(out var error)) {
            return Result<AgentEnrollment>.Failure(error);
        }

        return Result<AgentEnrollment>.Success(
            new() {
                ClusterId = clusterId,
                EnrollmentToken = minted.Plaintext,
                ExpiresAt = expiresAt,
                TunnelEndpoint = settings.TunnelEndpoint,
                ChartReference = settings.ChartReference,
                AgentImage = settings.AgentImage,
                HeartbeatInterval = heartbeatInterval
            }
        );
    }

    /// <inheritdoc />
    public Task<Result<AgentTunnelStatus>> GetStatusAsync(Guid clusterId, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return Grain(clusterId).GetStatusAsync();
    }

    /// <inheritdoc />
    public Task<Result> RevokeAsync(Guid clusterId, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        return Grain(clusterId).RevokeAsync();
    }

    IAgentTunnelGrain Grain(Guid clusterId) =>
        grains.GetGrain<IAgentTunnelGrain>(GrainKeys.ClusterConnection(clusterId));
}
