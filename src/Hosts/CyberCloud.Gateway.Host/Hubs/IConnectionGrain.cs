using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>The four hubs of docs/plan/10 § SignalR. The split is by lifecycle, not by feature.</summary>
public static class HubNames {
    /// <summary>Resource-changed events for the blades a user is looking at.</summary>
    public const string Resources = "resources";

    /// <summary>Operation progress.</summary>
    public const string Operations = "operations";

    /// <summary>
    ///     The cloud shell. ⚠ Binary and direct to the session grain — no connection grain, no
    ///     backplane, no interest set. docs/plan/10 § SignalR.
    /// </summary>
    public const string Terminal = "terminal";

    /// <summary>Live metric tiles, from pre-aggregates polled server-side.</summary>
    public const string Metrics = "metrics";

    /// <summary>Whether a path segment names one of the four.</summary>
    /// <param name="name">The segment after <c>/hubs/</c>.</param>
    public static bool IsKnown(string? name) =>
        name is Resources or Operations or Terminal or Metrics;
}

/// <summary>What one connection has asked to hear about.</summary>
/// <remarks>
///     ⚠ <b>An interest is a resource path, not a topic string, and that is what makes it
///     authorizable.</b> A free-form topic could not be checked against anything — the question
///     "may this caller read this?" needs an object, and docs/plan/07 § The model types its objects.
/// </remarks>
/// <param name="Hub">Which hub the interest belongs to.</param>
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
///         exempted from it.
///     </para>
///     <para>
///         ⚠ <b>WHERE THIS GRAIN RUNS IS AN OPEN DEPLOYMENT QUESTION AND docs/plan/10 DOES NOT ANSWER
///         IT.</b> The interface and implementation live in the gateway host because that is the only
///         component the document gives them to, but the gateway is an Orleans <i>client</i> and a
///         client does not host grains. A silo has to load this type for the hubs to work in
///         production, which means either the silo references this assembly — inverting
///         docs/plan/00 § Layer discipline — or the grain moves to a shared assembly. The second is
///         right and is owed. Nothing in the tests depends on the answer: the grain is exercised by
///         direct instantiation, which is possible precisely because it uses no grain infrastructure
///         beyond activation.
///     </para>
/// </remarks>
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
}

/// <summary>The grain keys this host mints.</summary>
static class GatewayGrainKeys {
    /// <summary>
    ///     <c>conn/{connectionId}</c> — <see cref="IConnectionGrain" />, always tenant-qualified.
    /// </summary>
    /// <param name="connectionId">
    ///     SignalR's connection id. ⚠ Server-generated, so it is not a caller-controlled key
    ///     component — which matters, because the tenant qualification is the only thing between this
    ///     key and another tenant's grain.
    /// </param>
    public static string Connection(string connectionId) {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        return string.Create(CultureInfo.InvariantCulture, $"conn/{connectionId}");
    }
}
