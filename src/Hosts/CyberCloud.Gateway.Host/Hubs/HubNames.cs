namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>The four hubs of docs/plan/10 § SignalR. The split is by lifecycle, not by feature.</summary>
/// <remarks>
///     ⚠ <b>The hub names stay in the gateway, and the connection grain does not.</b>
///     <see cref="IConnectionGrain" /> moved to <c>CyberCloud.ResourceManager.Contracts</c> because a
///     grain needs a silo to activate it and the gateway is an Orleans client — its remarks carry the
///     argument. These four strings did not move with it: routing <c>/hubs/{name}</c> is this
///     component's job and nothing else in the platform decides what a hub is.
///     <see cref="ConnectionInterest.Hub" /> is therefore an opaque string on the grain's side, which
///     the grain uses to partition an interest set and never interprets.
/// </remarks>
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
