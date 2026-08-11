using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>What one connection has asked to hear about.</summary>
/// <remarks>
///     ⚠ <b>An interest is a resource path, not a topic string, and that is what makes it
///     authorizable.</b> A free-form topic could not be checked against anything — the question
///     "may this caller read this?" needs an object, and docs/plan/07 § The model types its objects.
/// </remarks>
/// <param name="Hub">
///     Which hub the interest belongs to. ⚠ An opaque string here on purpose: the four hub names are
///     the gateway's vocabulary (docs/plan/10 § SignalR) and belong in the gateway, which is the only
///     component that routes them. The grain never interprets it — it partitions the interest set.
/// </param>
/// <param name="ResourcePath">
///     The resource, as docs/plan/06 § Identifiers spells it. A resource group's path subscribes to
///     everything under it.
/// </param>
[GenerateSerializer]
[Alias("CyberCloud.Gateway.ConnectionInterest")]
public readonly record struct ConnectionInterest(
    [property: Id(0)] string Hub,
    [property: Id(1)] string ResourcePath
) {
    /// <inheritdoc />
    public override string ToString() => $"{Hub}:{ResourcePath}";
}

/// <summary>
///     One SignalR connection's interest set. docs/plan/10 § SignalR.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Session · <b>Tier</b> <b>none</b> · <b>Key</b> <c>conn/{connectionId}</c>,
///         tenant-qualified. Build it with <see cref="ConnectionGrainKeys.Connection" />.
///     </para>
///     <para>
///         ⚠ <b>Why a grain at all, when the connection is pinned to one pod:</b> because the
///         <i>fan-out</i> is not. docs/plan/10 § SignalR rejects a Redis backplane because it
///         <i>"broadcasts every message to every server, which is the wrong shape here: our fan-out
///         is already tenant → interested connections, and Orleans streams already do exactly
///         that"</i>. The grain subscribes to the streams its connection cares about, so a message
///         reaches the one pod holding that connection — <c>O(interested)</c> rather than
///         <c>O(pods)</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>[PersistentState]</c>, and not even the hot tier.</b> This state cannot outlive
///         the connection that created it, so there is nothing to rebuild and nothing to lose. That
///         is also why the type is absent from <c>durable-grains.txt</c> — see the Storage tier gate
///         in <c>build/Build.Architecture.cs</c>, which is satisfied by the absence rather than
///         exempted from it. ⚠ Adding a <c>[PersistentState]</c> here would not be an optimisation; it
///         would be a durable record of a socket that no longer exists.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>CyberCloud.Gateway.Host</c>, because a client hosts no
///         grains.</b> docs/plan/10 § SignalR names the connection grain and says nothing about where
///         it runs, and the gateway is the only component the document hands it to — so the interface
///         and its implementation shipped there. But docs/plan/03 § Hosts and docs/plan/10 § Shape
///         both make the gateway an Orleans <b>client</b> (<c>CreateClient</c>), and a client's
///         <c>IGrainFactory</c> produces references to grains a <i>silo</i> activates. A grain type
///         that only the gateway assembly declares is a grain no silo can load, and the shipped
///         arrangement was kept honest only by the tests instantiating the class directly — a
///         constraint on future edits rather than a design, and one that quietly forbade the grain
///         from being a grain.
///     </para>
///     <para>
///         ⚠ <b>This assembly, specifically, because of what the interest set is made of.</b> An
///         interest is a resource path and every operation on it is "may this caller read that
///         resource?" — which is <see cref="IResourceManager.ReadAsync" />'s question and the seam
///         docs/plan/07 § The enforcement seam puts inside the resource manager. The implementation
///         needs that seam, and the silo composition that already registers it
///         (<c>AddCyberCloudResourceManager</c>) is exactly the wiring the grain was missing. Nothing
///         about SignalR crosses this boundary: the hubs, the hub names and the connection-count limit
///         stay in the gateway.
///     </para>
///     <para>
///         ⚠ <b>And it unblocks the re-check, which was the half that could not be built at all.</b>
///         <see cref="RecheckAsync" /> has to be driven by the tenant's relation-version stream —
///         grain-to-grain, inside a silo, over the <c>Events</c> provider. A type living in an Orleans
///         client could not subscribe to anything; here it can, and <c>ConnectionStreamBridge</c> is
///         an implementation rather than a redesign.
///     </para>
/// </remarks>
[Alias("Rm.Connection")]
public interface IConnectionGrain : IGrainWithStringKey {
    /// <summary>Binds a connection to a caller. Called once, on connect.</summary>
    /// <param name="caller">
    ///     Who connected. ⚠ Built by stage 3 from the token; the hub does not construct one.
    /// </param>
    /// <param name="hub">Which hub.</param>
    /// <returns>
    ///     Success. ⚠ Note what this does <b>not</b> do: it authorizes nothing. docs/plan/10
    ///     § SignalR — <i>"Subscription authorization is per-subscribe, not per-connect."</i>
    /// </returns>
    Task<Result> AttachAsync(CallerContext caller, string hub);

    /// <summary>Adds one interest, if the caller may read it.</summary>
    /// <param name="interest">What to listen to.</param>
    /// <returns>
    ///     Success when the interest is registered, or <see cref="ErrorCode.ResourceNotFound" /> when
    ///     the caller may not read it — the same <c>404</c> the REST path gives, for the same reason.
    /// </returns>
    Task<Result> SubscribeAsync(ConnectionInterest interest);

    /// <summary>Removes one interest. Removing one that was never held is success.</summary>
    /// <param name="interest">What to stop listening to.</param>
    Task<Result> UnsubscribeAsync(ConnectionInterest interest);

    /// <summary>What this connection currently hears.</summary>
    Task<ImmutableArray<ConnectionInterest>> InterestsAsync();

    /// <summary>
    ///     Re-checks every interest and drops the ones the caller may no longer read.
    /// </summary>
    /// <returns>How many interests were dropped.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the method that keeps the live channel from being an authorization
    ///     bypass.</b> docs/plan/10 § SignalR: <i>"A user who loses access to a resource group must
    ///     stop receiving its events — otherwise the live-update channel is an authorization bypass
    ///     with a nice UI."</i> A per-connect check would pass at 09:00 and still be delivering that
    ///     resource group's events at 17:00 to somebody who was removed at 09:05.
    /// </remarks>
    Task<int> RecheckAsync();

    /// <summary>
    ///     Drops this activation — the connection closed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The whole lifecycle, and the reason there is no storage.</b> A connection grain that
    ///     outlived its socket would hold an interest set nobody is listening to and a re-check per
    ///     relation change to maintain it. The hub calls this from <c>OnDisconnectedAsync</c>; a pod
    ///     that dies without calling it loses the activation anyway, which is the same outcome by a
    ///     different route — docs/plan/05 § Hot, "dies with the connection".
    /// </remarks>
    Task DeactivateAsync();
}

/// <summary>
///     The connection grain's key.
/// </summary>
/// <remarks>
///     ⚠ <b>Here rather than in <c>CyberCloud.Core.Resources.GrainKeys</c>, and the reason is that
///     docs/plan/06 § Grain keys does not have a row for it.</b> Every kind <c>GrainKeys</c> parses is
///     a durable entity with a place in docs/plan/06's hierarchy; a SignalR connection id is neither —
///     it is a socket's name, minted by the SignalR server and gone with the socket. Giving it a
///     <c>GrainKeyKind</c> would put an ephemeral session into the parser that also validates
///     subscription and resource ids. It was minted in the gateway host before this and moved here
///     with the interface, so there is still exactly one place that builds it.
/// </remarks>
public static class ConnectionGrainKeys {
    /// <summary>The key prefix. One segment, so the connection id may not contain <c>/</c>.</summary>
    public const string Prefix = "conn/";

    /// <summary>
    ///     <c>conn/{connectionId}</c> — <see cref="IConnectionGrain" />, always tenant-qualified.
    /// </summary>
    /// <param name="connectionId">
    ///     SignalR's connection id. ⚠ Server-generated, so it is not a caller-controlled key
    ///     component — which matters, because the tenant qualification is the only thing between this
    ///     key and another tenant's grain.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     The id is empty, or contains a <c>/</c>. ⚠ Thrown rather than escaped: a connection id with
    ///     a separator in it is not something SignalR produces, so it means our own code composed the
    ///     key from the wrong value — docs/plan/00 § Coding standards puts that in the "page someone"
    ///     class rather than the "return a tidy 400" one.
    /// </exception>
    public static string Connection(string connectionId) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        if (connectionId.Contains('/', StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"'{connectionId}' is not a SignalR connection id: it contains '/', which would add a "
                + "segment to the grain key and let one connection address another's grain.",
                nameof(connectionId)
            );
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{connectionId}");
    }
}
