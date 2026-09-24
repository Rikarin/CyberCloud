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
///         <i>
///             "Reconnect is the portal's own — a fresh ticket and a new socket, on a backoff ladder
///             — plus a <c>since</c> version on resubscribe, so a portal tab that slept through a
///             deploy catches up rather than showing stale state forever."
///         </i> The parameter is on <see cref="SubscribeAsync" /> and is passed
///         through to the stream replay; the replay itself needs the stream bridge that
///         <see cref="IConnectionGrain" /> documents as owed — and which is now buildable, because
///         the grain lives in a silo rather than in this client. The reconnect itself is the
///         client's and not SignalR's, because a browser opened this hub with a single-use
///         <c>HubTickets</c> ticket and the URL it would reopen holds the spent one.
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
///     The cloud shell. docs/plan/10 § SignalR:
///     <i>
///         "Direct to the session grain — binary, no
///         backplane."
///     </i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No interest set and no connection grain, deliberately.</b> A terminal is one caller
///         and one session, so there is nothing to fan out and an interest set would be a subscription
///         of size one with an authorization re-check attached. Each method goes straight to
///         <see cref="ITerminalSessionGrain" />, tenant-qualified from the token, and the pane's
///         output comes back through a grain observer (<see cref="TerminalPane" />) — not an Orleans
///         stream. <see cref="ITerminalViewer" />'s remarks say why: one producer, one consumer, and a
///         terminal needs order, backpressure and a delivery that fails at once when the pod holding
///         the socket is gone.
///     </para>
///     <para>
///         ⚠ <b>The hub authorizes nothing, and the gateway still cannot name the engine.</b> The
///         session grain checks that the caller is the person who opened the session and re-checks the
///         <c>connect</c> permission through the manager's <c>IResourceAuthorizer</c> — in a silo.
///         <c>GatewayIsolationTests.TheGatewayBindsNoTypeFromTheAuthorizationAssemblies</c> is still
///         true of this file.
///     </para>
///     <para>
///         ⚠ <b>The contract is the portal's.</b> Three methods a client invokes — <see cref="Attach" />,
///         <see cref="Send" /> and <see cref="Resize" /> — and two it receives,
///         <see cref="TerminalProtocol.Output" /> and <see cref="TerminalProtocol.Ended" />.
///         <c>TerminalHubTests.TheWireNamesAreTheFiveThePortalSpeaks</c> and the portal's
///         <c>terminal-session.spec.ts</c> pin the names from both sides.
///     </para>
/// </remarks>
/// <param name="grains">The gateway's Orleans client. Every reference goes through <c>ForTenant</c>.</param>
/// <param name="limiter">The per-tenant connection cap the interest hubs share — docs/plan/10 § Rate limiting.</param>
/// <param name="panes">Where a pane's output is sent from outside a hub invocation.</param>
public sealed class TerminalHub(
    IGrainFactory grains,
    IConcurrencyLimiter limiter,
    IHubContext<TerminalHub> panes
) : Hub {
    const string PaneKey = "cybercloud.terminal.pane";
    const string HeldSlot = "cybercloud.terminal.slot";

    /// <inheritdoc />
    public override async Task OnConnectedAsync() {
        if (!limiter.TryAcquireConnection(Caller().TenantId)) {
            // Aborted rather than thrown, for InterestHub's reason: a client over the cap should back
            // off and reconnect, not read a hub exception.
            Context.Abort();
            return;
        }

        // Marks the slot as held, so a connection aborted above does not release one it never took.
        Context.Items[HeldSlot] = true;
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception) {
        if (Context.Items.ContainsKey(HeldSlot)) {
            limiter.ReleaseConnection(Caller().TenantId);
        }

        // ⚠ The shell keeps running. A dropped socket is exactly what reconnect is for; ending the
        // session here would turn a Wi-Fi blip into a lost shell, the one thing docs/plan/19
        // § Architecture says the ring buffer exists to prevent.
        await DetachAsync();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    ///     Joins a session: replays its output ring buffer, then streams live output as
    ///     <see cref="TerminalProtocol.Output" /> until the socket closes or the session ends.
    /// </summary>
    /// <param name="sessionId">The session <c>connect</c> returned — the shell pod's own UID.</param>
    /// <param name="columns">The pane's width in cells, so the first frame is laid out for the pane it lands in.</param>
    /// <param name="rows">The pane's height in cells.</param>
    /// <exception cref="HubException">
    ///     The session grain refused: no such session for this caller, the caller no longer holds
    ///     <c>connect</c>, or the shell has ended. The message is the grain's, and the portal shows it.
    /// </exception>
    [HubMethodName(TerminalProtocol.Attach)]
    public async Task Attach(string sessionId, int columns, int rows) {
        var session = Session(sessionId);

        // One pane per socket. A second Attach on the same socket — the portal never sends one, a
        // hand-written client might — replaces the first rather than leaking its observer.
        await DetachAsync();

        var pane = new TerminalPane(panes, Context);
        var reference = grains.CreateObjectReference<ITerminalViewer>(pane);
        pane.Attached(session, reference, grains);

        // ⚠ Parked BEFORE the grain call: the replay is delivered to the pane during AttachAsync, and
        // Orleans holds an observer only weakly — see TerminalPane.
        Context.Items[PaneKey] = pane;

        var attached = await session.AttachAsync(Caller(), reference, columns, rows);

        if (attached.TryGetError(out var refusal)) {
            Context.Items.Remove(PaneKey);
            pane.Release();
            throw new HubException(refusal.Message);
        }
    }

    /// <summary>Sends a chunk of input to the session. Binary, unbuffered.</summary>
    /// <param name="sessionId">The terminal session.</param>
    /// <param name="data">The bytes the user typed.</param>
    /// <exception cref="HubException">
    ///     The session grain refused. ⚠ Failing loudly rather than accepting and dropping: a terminal
    ///     that silently swallows input is worse than one that is closed.
    /// </exception>
    [HubMethodName(TerminalProtocol.Send)]
    public async Task Send(string sessionId, byte[] data) {
        var sent = await Session(sessionId).SendAsync(Caller(), data);

        if (sent.TryGetError(out var refusal)) {
            throw new HubException(refusal.Message);
        }
    }

    /// <summary>Tells the shell its window changed size, so a full-screen program redraws for it.</summary>
    /// <param name="sessionId">The terminal session.</param>
    /// <param name="columns">The new width in cells.</param>
    /// <param name="rows">The new height in cells.</param>
    /// <exception cref="HubException">The session grain refused — see <see cref="Send" />.</exception>
    [HubMethodName(TerminalProtocol.Resize)]
    public async Task Resize(string sessionId, int columns, int rows) {
        var resized = await Session(sessionId).ResizeAsync(Caller(), columns, rows);

        if (resized.TryGetError(out var refusal)) {
            throw new HubException(refusal.Message);
        }
    }

    async Task DetachAsync() {
        if (Context.Items.Remove(PaneKey, out var held) && held is TerminalPane pane) {
            await pane.DetachAsync();
        }
    }

    /// <summary>The session grain, tenant-qualified from the token.</summary>
    /// <exception cref="HubException">The id is not a session id — refused before any grain is addressed.</exception>
    /// <remarks>
    ///     ⚠ <c>ForTenant</c>, and CC1006 fails the build on anything else. The session id is the one
    ///     caller-supplied part of the key and it is checked for a UID's shape first, so a crafted id
    ///     cannot add a segment; the tenant part comes from the token and cannot be supplied at all.
    /// </remarks>
    ITerminalSessionGrain Session(string sessionId) {
        if (!TerminalSessionKeys.IsSessionId(sessionId)) {
            throw new HubException(
                $"'{sessionId}' is not a session id. Call connect on the console and use the sessionId it returns."
            );
        }

        return grains
            .ForTenant(Caller().TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITerminalSessionGrain>(TerminalSessionKeys.Session(sessionId));
    }

    /// <summary>The caller the pipeline established for this connection — see <see cref="InterestHub" />.</summary>
    CallerContext Caller() =>
        Context.GetHttpContext()?.Items[GatewayCallerFeature.ItemKey] as CallerContext
        ?? throw new HubException(
            "This connection has no caller context. A hub endpoint must be mapped behind the gateway "
            + "pipeline — docs/plan/10 § Request pipeline."
        );
}

/// <summary>
///     One terminal pane's socket, as the session grain sees it — the grain observer the hub hands
///     to <see cref="ITerminalSessionGrain.AttachAsync" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Held by the connection, because Orleans holds an observer weakly.</b> The runtime keeps
///         only a weak reference to the object behind <c>CreateObjectReference</c>; a pane nothing else
///         referenced would be collected mid-session and the grain's next delivery would fail as though
///         the socket had gone. The hub parks it in the connection's items for exactly the socket's
///         lifetime.
///     </para>
///     <para>
///         ⚠ <b>Output goes through <see cref="IHubContext{THub}" />, not the hub.</b> A hub instance
///         lives for one invocation; the grain calls this whenever the shell prints.
///     </para>
/// </remarks>
/// <param name="panes">The hub context, for sending to this one connection.</param>
/// <param name="connection">The connection, for its id and so an ended session can close it.</param>
sealed class TerminalPane(IHubContext<TerminalHub> panes, HubCallerContext connection) : ITerminalViewer {
    ITerminalSessionGrain? session;
    ITerminalViewer? reference;
    IGrainFactory? grains;

    /// <summary>Records what this pane is attached to, so it can detach itself.</summary>
    /// <param name="session">The session grain.</param>
    /// <param name="reference">The observer reference the grain was given for this pane.</param>
    /// <param name="grains">The factory the reference was created on, which is where it is released.</param>
    public void Attached(ITerminalSessionGrain session, ITerminalViewer reference, IGrainFactory grains) {
        this.session = session;
        this.reference = reference;
        this.grains = grains;
    }

    /// <inheritdoc />
    public Task OutputAsync(byte[] data) =>
        panes.Clients.Client(connection.ConnectionId).SendAsync(TerminalProtocol.Output, data);

    /// <inheritdoc />
    /// <remarks>
    ///     Tells the pane, then closes the socket. ⚠ The pane must not treat the close as a dropped
    ///     connection and reconnect — a reconnect is <c>connect</c>, and <c>connect</c> after an idle
    ///     reclaim starts the pod the reclaim just stopped. <see cref="TerminalProtocol.Ended" /> is the
    ///     portal's cue to stop.
    /// </remarks>
    public async Task EndedAsync(string reason) {
        await panes.Clients.Client(connection.ConnectionId).SendAsync(TerminalProtocol.Ended, reason);
        connection.Abort();
    }

    /// <summary>Leaves the session and releases the observer.</summary>
    public async Task DetachAsync() {
        if (session is not null && reference is not null) {
            try {
                await session.DetachAsync(reference);
            } catch (Exception ex) when (ex is not OutOfMemoryException) {
                // The silo is gone or the grain with it; the observer is released below either way.
            }
        }

        Release();
    }

    /// <summary>Releases the observer without telling the grain — for an attach the grain refused.</summary>
    public void Release() {
        if (grains is not null && reference is not null) {
            try {
                // ⚠ The REFERENCE, not this object — AgentTunnelRelay's lesson: Orleans indexes a
                // registration by the reference it handed out.
                grains.DeleteObjectReference<ITerminalViewer>(reference);
            } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
                // Already released.
            }
        }

        reference = null;
        session = null;
    }
}

/// <summary>
///     The names on the wire between the portal's terminal pane and <see cref="TerminalHub" />.
/// </summary>
/// <remarks>
///     Constants rather than the methods' own names, because SignalR binds by string and the
///     portal is TypeScript: a rename on one side is a hub method the other side cannot find, and a
///     <c>HubMethodName</c> that reads a constant keeps the C# name free to follow its own
///     conventions while the wire name stays put.
/// </remarks>
public static class TerminalProtocol {
    /// <summary>Client → hub: join a session and start receiving <see cref="Output" />.</summary>
    public const string Attach = "Attach";

    /// <summary>Client → hub: bytes typed.</summary>
    public const string Send = "Send";

    /// <summary>Client → hub: the pane changed size.</summary>
    public const string Resize = "Resize";

    /// <summary>Hub → client: bytes the shell printed, as one <c>byte[]</c> argument.</summary>
    public const string Output = "Output";

    /// <summary>
    ///     Hub → client: the session is over — exited, reclaimed after sitting idle, stopped at its
    ///     hard cap, or terminated — with one sentence saying which. The socket closes after it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Its own callback so the pane can tell it from a dropped socket.</b> A dropped socket is
    ///     reconnected, and the portal's reconnect is <c>connect</c> — which, after an idle reclaim,
    ///     would start the very pod the reclaim stopped, for a tab nobody is looking at.
    /// </remarks>
    public const string Ended = "Ended";
}
