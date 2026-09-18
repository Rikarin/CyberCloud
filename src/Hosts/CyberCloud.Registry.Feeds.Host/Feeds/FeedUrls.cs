using System.Globalization;

namespace CyberCloud.Registry.Feeds.Host.Feeds;

/// <summary>
///     Where a feed lives on this host — the URL grammar every protocol's links are spelled in.
/// </summary>
/// <remarks>
///     <para>
///         <c>{origin}/{kind}/{subscriptionId}/{resourceGroup}/{feedName}/…</c>. The kind is first so
///         a route can be mapped per protocol; the three segments after it are the resource's own
///         address minus the tenant, which the token supplies. No tenant segment exists for a caller
///         to spell — <c>FeedAccess</c> says why.
///     </para>
///     <para>
///         The origin is <see cref="FeedsOptions.PublicBaseUri" /> when a deployment set it and the
///         request's own scheme and host otherwise. A NuGet service index that named an origin its
///         client could not reach would be a feed that lists and never downloads.
///     </para>
/// </remarks>
public static class FeedUrls {
    /// <summary>The route prefix for one protocol, as ASP.NET Core's routing reads it.</summary>
    /// <param name="kind">The protocol.</param>
    public static string RoutePrefix(FeedKind kind) =>
        "/" + ArtifactFeeds.NameOf(kind) + "/{subscription:guid}/{group}/{feed}";

    /// <summary>The absolute base of one feed, with no trailing slash.</summary>
    /// <param name="http">The request, for its origin when none is configured.</param>
    /// <param name="options">The host's configuration.</param>
    /// <param name="kind">The protocol.</param>
    /// <param name="subscription">The subscription segment.</param>
    /// <param name="group">The resource-group segment.</param>
    /// <param name="feed">The feed's name.</param>
    public static string BaseOf(
        HttpContext http,
        FeedsOptions options,
        FeedKind kind,
        Guid subscription,
        string group,
        string feed
    ) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        var origin = options.PublicBaseUri.Length > 0
            ? options.PublicBaseUri.TrimEnd('/')
            : $"{http.Request.Scheme}://{http.Request.Host}";

        return $"{origin}/{ArtifactFeeds.NameOf(kind)}/{subscription.ToString("D", CultureInfo.InvariantCulture)}/{group}/{feed}";
    }
}
