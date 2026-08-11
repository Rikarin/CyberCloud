using System.Collections.Concurrent;

namespace CyberCloud.Gateway.Host.RateLimiting;

/// <summary>
///     The limit the two exempt classes get instead of a request count. docs/plan/10 § Rate limiting.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Connections per tenant and streams per connection — two limits, because they stop two
///         different things.</b> The per-tenant connection cap stops one tenant's automation from
///         exhausting a pod's socket budget and taking every other tenant's portal down with it. The
///         per-connection stream cap stops one connection from subscribing to a tenant's entire
///         resource graph, which costs a stream subscription and a re-check per relation change for
///         each one — the cheapest possible request for the caller and an unbounded one for the
///         platform.
///     </para>
///     <para>
///         ⚠ <b>Only the first of the two is here.</b> The stream cap moved with the connection grain
///         to <c>CyberCloud.ResourceManager</c> (<c>ConnectionLimits.StreamsPerConnection</c>), for
///         the reason its remarks give: it bounds an interest set, which is the grain's own state, and
///         a limiter on this side would be checking a number it cannot see. What is left here is the
///         socket budget, which is genuinely per pod and belongs beside the hub that takes the socket.
///     </para>
/// </remarks>
public interface IConcurrencyLimiter {
    /// <summary>Takes a connection slot for a tenant.</summary>
    /// <param name="tenantId">The tenant, from the token.</param>
    /// <returns><c>true</c> when the tenant was under its connection cap and now holds one more.</returns>
    bool TryAcquireConnection(Guid tenantId);

    /// <summary>Returns a connection slot. Safe to call for a slot that was never taken.</summary>
    /// <param name="tenantId">The tenant.</param>
    void ReleaseConnection(Guid tenantId);
}

/// <summary>Options for <see cref="ProcessConcurrencyLimiter" />.</summary>
sealed class ConcurrencyLimits {
    /// <summary>
    ///     How many live SignalR connections one tenant may hold on one pod. Default 500.
    /// </summary>
    /// <remarks>
    ///     ⚠ Per pod, not per tenant globally, and the difference is deliberate. A global cap needs a
    ///     shared counter on the connect path, which is the same round trip stage 5 exists to avoid,
    ///     and the thing being protected — this pod's sockets — is per pod anyway.
    /// </remarks>
    public int ConnectionsPerTenant { get; init; } = 500;
}

/// <summary>The per-pod counter. No Redis, no grain, no round trip.</summary>
sealed class ProcessConcurrencyLimiter(ConcurrencyLimits limits) : IConcurrencyLimiter {
    readonly ConcurrentDictionary<Guid, int> connections = new();

    /// <inheritdoc />
    public bool TryAcquireConnection(Guid tenantId) {
        var taken = false;

        connections.AddOrUpdate(
            tenantId,
            _ => {
                taken = true;
                return 1;
            },
            (_, current) => {
                if (current >= limits.ConnectionsPerTenant) {
                    return current;
                }

                taken = true;
                return current + 1;
            }
        );

        return taken;
    }

    /// <inheritdoc />
    public void ReleaseConnection(Guid tenantId) =>
        connections.AddOrUpdate(tenantId, 0, (_, current) => current > 0 ? current - 1 : 0);

    /// <summary>How many connections a tenant holds on this pod. For tests and for a metric.</summary>
    /// <param name="tenantId">The tenant.</param>
    public int ConnectionsHeldBy(Guid tenantId) => connections.TryGetValue(tenantId, out var held) ? held : 0;
}
