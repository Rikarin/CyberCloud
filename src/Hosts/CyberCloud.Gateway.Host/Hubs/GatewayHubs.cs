using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>
///     What the three interest-based hubs share: a connection grain, a concurrency slot, and a
///     per-subscribe check.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The caller comes from the pipeline, not from the hub.</b> A SignalR connection goes
///         through the same nine stages as any other request — the handshake is an HTTP request —
///         so <see cref="GatewayRequestContext.Caller" /> is already built from the token by the time
///         a hub method runs. Rebuilding one here from <c>Context.User</c> would be a second
///         authentication path, and the second one is the one that gets the tenant wrong.
///     </para>
///     <para>
///         ⚠ <b>Reconnect carries a <c>since</c> version.</b> docs/plan/10 § SignalR:
///         <i>"Reconnect uses SignalR's automatic reconnect plus a <c>since</c> version on
///         resubscribe, so a portal tab that slept through a deploy catches up rather than showing
///         stale state forever."</i> The parameter is on <see cref="SubscribeAsync" /> and is passed
///         through to the stream replay; the replay itself needs the stream bridge that
///         <see cref="IConnectionGrain" /> documents as owed — and which is now buildable, because
///         the grain lives in a silo rather than in this client.
///     </para>
/// </remarks>
public abstract class InterestHub(IGrainFactory grains, IConcurrencyLimiter limiter) : Hub {
    /// <summary>Which hub this is. One of <see cref="HubNames" />.</summary>
    protected abstract string HubName { get; }

    /// <inheritdoc />
    public override async Task OnConnectedAsync() {
        var caller = Caller();

        if (!limiter.TryAcquireConnection(caller.TenantId)) {
            // docs/plan/10 § Rate limiting — connections per tenant. Aborting rather than throwing:
            // a client that is over the cap should reconnect with backoff, not see a hub exception.
            Context.Abort();
            return;
        }

        await Connection().AttachAsync(caller, HubName);
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception) {
        limiter.ReleaseConnection(Caller().TenantId);

        // ⚠ The interest set dies with the socket, which is what "no storage" means in practice —
        // docs/plan/05 § Hot. The grain would drop out on idle anyway; saying so at the moment the
        // connection goes means the pod does not hold an activation for a socket it has closed.
        await Connection().DeactivateAsync();

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Subscribes this connection to a resource's events.</summary>
    /// <param name="resourcePath">The resource, as docs/plan/06 § Identifiers spells it.</param>
    /// <param name="since">
    ///     The version already seen, or 0 for "from now". docs/plan/10 § SignalR's catch-up.
    /// </param>
    /// <returns>
    ///     <c>true</c> when the subscription was accepted. ⚠ <c>false</c> for a resource the caller
    ///     may not read <i>and</i> for one that does not exist — the same non-answer the REST
    ///     <c>404</c> gives, because a hub method that distinguished them would be the enumeration
    ///     oracle the REST path closed.
    /// </returns>
    public async Task<bool> SubscribeAsync(string resourcePath, long since = 0) {
        var subscribed = await Connection().SubscribeAsync(new(HubName, resourcePath));

        _ = since;

        return !subscribed.TryGetError(out _);
    }

    /// <summary>Stops listening to a resource.</summary>
    /// <param name="resourcePath">The resource.</param>
    public async Task UnsubscribeAsync(string resourcePath) =>
        await Connection().UnsubscribeAsync(new(HubName, resourcePath));

    /// <summary>This connection's grain, tenant-qualified from the token.</summary>
    /// <remarks>
    ///     ⚠ <c>ForTenant</c>, and CC1006 fails the build on anything else. The gateway is an Orleans
    ///     client, so nothing below this line re-checks the tenant — docs/plan/00 § The
    ///     tenant-separation row, corrected.
    /// </remarks>
    protected IConnectionGrain Connection() =>
        grains
            .ForTenant(Caller().TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IConnectionGrain>(ConnectionGrainKeys.Connection(Context.ConnectionId));

    /// <summary>The caller the pipeline established for this connection.</summary>
    /// <exception cref="HubException">
    ///     The connection did not go through the pipeline. Not reachable through the mapped
    ///     endpoints; thrown rather than defaulted because a default would be a caller with no tenant.
    /// </exception>
    protected CallerContext Caller() =>
        Context.GetHttpContext()?.Items[GatewayCallerFeature.ItemKey] as CallerContext
        ?? throw new HubException(
            "This connection has no caller context. A hub endpoint must be mapped behind the gateway "
            + "pipeline — docs/plan/10 § Request pipeline."
        );
}

/// <summary>Where the pipeline parks the caller for SignalR to pick up.</summary>
static class GatewayCallerFeature {
    /// <summary>The <c>HttpContext.Items</c> key.</summary>
    public const string ItemKey = "cybercloud.caller";
}

/// <summary>Resource-changed events for the blades a user is looking at. Orleans streams → hub.</summary>
public sealed class ResourcesHub(IGrainFactory grains, IConcurrencyLimiter limiter)
    : InterestHub(grains, limiter) {
    /// <inheritdoc />
    protected override string HubName => HubNames.Resources;
}

/// <summary>Operation progress. Same shape, same streams.</summary>
public sealed class OperationsHub(IGrainFactory grains, IConcurrencyLimiter limiter)
    : InterestHub(grains, limiter) {
    /// <inheritdoc />
    protected override string HubName => HubNames.Operations;
}

/// <summary>
///     Live metric tiles. docs/plan/10 § SignalR: pre-aggregates from the hot tier, polled
///     server-side.
/// </summary>
/// <remarks>
///     ⚠ Polled server-side rather than streamed, because a metric tile wants the <i>latest</i> value
///     at a fixed cadence, not every sample. Streaming raw samples to a browser is how a metrics
///     channel becomes the most expensive thing on the connection.
/// </remarks>
public sealed class MetricsHub(IGrainFactory grains, IConcurrencyLimiter limiter)
    : InterestHub(grains, limiter) {
    /// <inheritdoc />
    protected override string HubName => HubNames.Metrics;
}

/// <summary>
///     The cloud shell. docs/plan/10 § SignalR: <i>"Direct to the session grain — binary, no
///     backplane."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>No interest set and no connection grain, deliberately.</b> A terminal is one caller and
///     one session, so there is nothing to fan out and an interest set would be a subscription of
///     size one with an authorization re-check attached. The session grain is docs/plan/19's and does
///     not exist yet; what is here is the hub's shape and its authorization seam, so that wiring the
///     session grain is an implementation rather than a redesign.
/// </remarks>
public sealed class TerminalHub : Hub {
    /// <summary>Sends a chunk of input to the session. Binary, unbuffered.</summary>
    /// <param name="sessionId">The terminal session.</param>
    /// <param name="data">The bytes the user typed.</param>
    /// <exception cref="HubException">
    ///     Always, until docs/plan/19's session grain exists. ⚠ Failing loudly rather than accepting
    ///     and dropping: a terminal that silently swallows input is worse than one that is closed.
    /// </exception>
    public Task SendAsync(string sessionId, byte[] data) {
        _ = sessionId;
        _ = data;

        throw new HubException(
            "The cloud terminal's session grain is docs/plan/19 and is not implemented. The hub is "
            + "mapped so the route and its authorization seam exist; the data plane is owed."
        );
    }
}
