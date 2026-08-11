using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Routing;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 5 — the five buckets, over Redis counters, before anything can cost a grain.
///     docs/plan/10 § Request pipeline and § Rate limiting.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One of the two load-bearing stages, and the reason is its position.</b>
///         docs/plan/10 § Request pipeline: <i>"Rate limiting before dispatch means a flood costs
///         Redis <c>INCR</c>s, not grain activations."</i> Moving this after routing would put the
///         provider registry on a flood's path; moving it after dispatch would put grain activations
///         there. <see cref="GatewayTraceBuilder" /> refuses either.
///     </para>
///     <para>
///         ⚠ <b>The subscription id comes from the path and that is safe here, unlike the tenant.</b>
///         The key is <c>rl:{'{'}t:{'{'}tenant{'}'}{'}'}:sub:…</c> — hash-tagged by the token's
///         tenant — so the worst a caller can do by naming somebody else's subscription is spend
///         their own tenant's budget under an odd name. Naming another <i>tenant</i> is impossible:
///         stage 3 already refused it, and the tag comes from the caller context rather than the URL.
///     </para>
/// </remarks>
sealed class RateLimitStage(GatewayRateLimiter limiter) : IGatewayStage {
    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.RateLimit;

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Http.Request;

        // ⚠ Classified from the raw path, not from the route — stage 6 has not run yet, on purpose.
        // See GatewayRouter.Classify for why the document's ordering forces this.
        context.RequestClass = GatewayRouter.Classify(
            request.Path.Value ?? "",
            request.Method,
            request.Query
        );

        var decision = await limiter.EvaluateAsync(
            context.RequestClass,
            context.Caller.TenantId,
            context.Caller.SubjectId,
            SubscriptionFromPath(request.Path.Value ?? ""),
            context.Http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            cancellationToken
        );

        context.RateLimitHeaders = decision.Remaining;

        return decision.Allowed ? null : GatewayRateLimiter.TooManyRequests(decision);
    }

    /// <summary>
    ///     The subscription segment, without parsing the whole path.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately not <see cref="ResourceId.ParsePath" />. A full parse validates every
    ///     component, which is work a flood would be buying — and a request whose <i>path</i> is
    ///     malformed still has to be counted, or "send garbage fast" is an unlimited channel.
    /// </remarks>
    internal static Guid SubscriptionFromPath(string path) {
        const string marker = "/subscriptions/";

        var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) {
            return Guid.Empty;
        }

        var rest = path[(start + marker.Length)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var segment = slash < 0 ? rest : rest[..slash];

        return GatewayGuid.TryParseD(segment, out var subscription) ? subscription : Guid.Empty;
    }
}
