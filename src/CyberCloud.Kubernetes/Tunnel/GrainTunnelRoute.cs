using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts.Tunnel;

namespace CyberCloud.Kubernetes.Tunnel;

/// <summary>
///     <see cref="ITunnelRoute" /> over <see cref="IAgentTunnelGrain" /> — what the connection
///     grain's <see cref="TunnelKubeApiClient" /> sends down in production, where the socket is in
///     the gateway and the multiplexer is in the tunnel grain.
/// </summary>
/// <remarks>
///     ⚠ <b>The grain reference is unqualified, and that is CC1006's documented second form.</b>
///     The tunnel grain shares the connection grain's null-tenant key
///     (<see cref="GrainKeys.ClusterConnection" />), for the reason <c>GrainClusterConnectionRegistrar</c>
///     gives: there is exactly one per cluster platform-wide, and <c>ForTenant</c> would fork one
///     per tenant. The tenancy this key cannot carry is enforced at the far end —
///     <c>AgentTunnelGrain.ExchangeAsync</c> admits null-tenant callers only, and this route is only
///     ever constructed by the factory for the connection grain.
/// </remarks>
/// <param name="grains">The grain factory.</param>
/// <param name="clusterId">The cluster.</param>
public sealed class GrainTunnelRoute(IGrainFactory grains, Guid clusterId) : ITunnelRoute {
    /// <summary>The cluster this route reaches.</summary>
    public Guid ClusterId => clusterId;

    /// <inheritdoc />
    public Task<Result<TunnelFrame>> ExchangeAsync(TunnelFrame request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return grains
            .GetGrain<IAgentTunnelGrain>(GrainKeys.ClusterConnection(clusterId))
            .ExchangeAsync(request);
    }
}
