using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Hubs;

namespace CyberCloud.Gateway.Host.Routing;

/// <summary>What kind of endpoint a path names.</summary>
enum RouteKind {
    /// <summary>Nothing this gateway serves.</summary>
    Unknown = 0,

    /// <summary>A resource, by id. <c>GET</c>, <c>PUT</c>, <c>PATCH</c>, <c>DELETE</c>.</summary>
    Resource,

    /// <summary>A <c>POST</c> action on an existing resource — <c>restart</c>, <c>rotateKeys</c>.</summary>
    Action,

    /// <summary>The LRO polling endpoint. docs/plan/10 § Long-running operations.</summary>
    Operation,

    /// <summary>One of the four hubs. docs/plan/10 § SignalR.</summary>
    Hub,

    /// <summary>The generated document, per api-version.</summary>
    OpenApi
}

/// <summary>
///     Stage 6's answer: what the path names, once the tenant is already known from the token.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Resource" />'s tenant is the <i>token's</i>, never the path's.</b>
///     <see cref="GatewayRouter" /> rebuilds the id from <c>CallerContext.TenantId</c> after stage 3
///     has already answered <c>404</c> to any disagreement. Two defences rather than one, and the
///     second is the one that still holds if somebody deletes the first: even with the check gone,
///     the id that reaches dispatch addresses the caller's own tenant, so the failure mode of a
///     regression is "the resource is not found" rather than "another tenant's resource is returned".
/// </remarks>
/// <param name="Kind">Which endpoint.</param>
/// <param name="Resource">The resource, for <see cref="RouteKind.Resource" /> and <see cref="RouteKind.Action" />.</param>
/// <param name="Action">The action name, for <see cref="RouteKind.Action" />.</param>
/// <param name="OperationId">The operation, for <see cref="RouteKind.Operation" />.</param>
/// <param name="HubName">The hub, for <see cref="RouteKind.Hub" />.</param>
readonly record struct GatewayRoute(
    RouteKind Kind,
    ResourceId Resource,
    string Action,
    Guid OperationId,
    string HubName
) {
    /// <summary>Nothing matched.</summary>
    public static GatewayRoute None { get; } = new(RouteKind.Unknown, default, "", Guid.Empty, "");

    /// <summary>The address dispatch uses — rebuilt, so it always carries the token's tenant.</summary>
    public string ResourcePath => Kind is RouteKind.Resource or RouteKind.Action ? Resource.Path : "";
}

/// <summary>
///     Turns a URL path into a <see cref="GatewayRoute" />. docs/plan/10 § Request pipeline, stage 6.
/// </summary>
static class GatewayRouter {
    /// <summary>The <c>/hubs/</c> prefix docs/plan/10 § Shape gives the SignalR endpoints.</summary>
    public const string HubPrefix = "/hubs/";

    /// <summary>The LRO polling prefix docs/plan/10 § Long-running operations gives.</summary>
    public const string OperationsPrefix = "/operations/";

    /// <summary>The generated OpenAPI document.</summary>
    public const string OpenApiPath = "/openapi";

    /// <summary>
    ///     Resolves a path against a tenant that has already been established from the token.
    /// </summary>
    /// <param name="path">The URL path, decoded.</param>
    /// <param name="method">The HTTP method, upper case.</param>
    /// <param name="tenantId">
    ///     The tenant from the token. ⚠ The parsed path's own tenant segment is <b>discarded</b> —
    ///     stage 3 has already refused any request where the two disagree, and rebuilding from this
    ///     value is what makes that refusal impossible to bypass by deleting the check.
    /// </param>
    /// <returns>
    ///     The route, or a failure. ⚠ A malformed resource path is
    ///     <see cref="ErrorCode.InvalidResourceId" /> — a <c>400</c> — because it is a client bug and
    ///     not a hidden resource. A well-formed path to something that does not exist is a
    ///     <c>404</c>, and that decision belongs to the resource manager, not here.
    /// </returns>
    public static Result<GatewayRoute> Resolve(string path, string method, Guid tenantId) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(method);

        if (path.StartsWith(HubPrefix, StringComparison.Ordinal)) {
            var hub = path[HubPrefix.Length..].TrimEnd('/');

            return HubNames.IsKnown(hub)
                ? Result<GatewayRoute>.Success(new(RouteKind.Hub, default, "", Guid.Empty, hub))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        if (path.StartsWith(OperationsPrefix, StringComparison.Ordinal)) {
            var rest = path[OperationsPrefix.Length..].TrimEnd('/');

            // The `D` form only — the same rule ResourceId applies to every GUID it parses, and for
            // the same reason: five spellings of one id are five cache entries and five audit rows.
            return GatewayGuid.TryParseD(rest, out var operationId)
                ? Result<GatewayRoute>.Success(new(RouteKind.Operation, default, "", operationId, ""))
                : Result<GatewayRoute>.Failure(GatewayErrors.NotFound(path));
        }

        if (string.Equals(path, OpenApiPath, StringComparison.Ordinal)) {
            return Result<GatewayRoute>.Success(new(RouteKind.OpenApi, default, "", Guid.Empty, ""));
        }

        return string.Equals(method, HttpMethods.Post, StringComparison.Ordinal)
            ? ResolveAction(path, tenantId)
            : ResolveResource(path, tenantId);
    }

    static Result<GatewayRoute> ResolveResource(string path, Guid tenantId) {
        var parsed = ResourceId.ParsePath(path);
        if (parsed.TryGetError(out var error)) {
            return Result<GatewayRoute>.Failure(error);
        }

        return Result<GatewayRoute>.Success(new(
            RouteKind.Resource,
            parsed.GetValueOrThrow() with { TenantId = tenantId },
            "",
            Guid.Empty,
            ""
        ));
    }

    /// <summary>
    ///     A <c>POST</c> is always <c>{resource}/{action}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The last segment is stripped unconditionally, and the alternative is ambiguous.</b>
    ///     Resource types nest (<c>servers/databases/{name}</c>), so
    ///     <c>…/servers/main/restart</c> parses equally well as the resource <c>restart</c> of type
    ///     <c>servers/main</c>. Nothing in the path distinguishes them. docs/plan/08 § The write path,
    ///     end to end removes the ambiguity from the other end — <c>POST</c> <i>"appears only for
    ///     actions on an existing resource … never for creation"</i> — so on this verb the tail is an
    ///     action by definition, and the registry decides whether it is a <i>known</i> one.
    /// </remarks>
    static Result<GatewayRoute> ResolveAction(string path, Guid tenantId) {
        var lastSlash = path.LastIndexOf('/');

        if (lastSlash <= 0 || lastSlash == path.Length - 1) {
            return Result<GatewayRoute>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{path}' is not an action on a resource. A POST addresses "
                + "'{resource}/{action}' — docs/plan/08 § The write path, end to end."
            );
        }

        var parsed = ResourceId.ParsePath(path[..lastSlash]);
        if (parsed.TryGetError(out var error)) {
            return Result<GatewayRoute>.Failure(error);
        }

        return Result<GatewayRoute>.Success(new(
            RouteKind.Action,
            parsed.GetValueOrThrow() with { TenantId = tenantId },
            path[(lastSlash + 1)..],
            Guid.Empty,
            ""
        ));
    }

    /// <summary>
    ///     What stage 5 needs to know about a request <i>before</i> stage 6 has run.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is a wrinkle in docs/plan/10 § Request pipeline and it is worth naming.</b> Rate
    ///     limiting is stage 5 and routing is stage 6, but the rate limiter has to know whether the
    ///     request is a long-poll or a hub handshake, because docs/plan/10 § Rate limiting exempts
    ///     both from the request-count buckets. Waiting for stage 6 would mean rate limiting after
    ///     routing, and routing reads the provider registry — which is exactly the work stage 5 exists
    ///     to keep off a flood's path. So the classification here is a <b>prefix test on the raw
    ///     path</b>: no registry, no allocation beyond a substring, and it cannot fail.
    /// </remarks>
    public static RequestClass Classify(string path, string method, IQueryCollection query) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(query);

        if (path.StartsWith(HubPrefix, StringComparison.Ordinal)) {
            return RequestClass.Hub;
        }

        // docs/plan/10 § Rate limiting: "Counting a 30-second long-poll as one request against a
        // 5-minute window is how you accidentally rate-limit your own portal."
        if (path.StartsWith(OperationsPrefix, StringComparison.Ordinal) && query.ContainsKey("wait")) {
            return RequestClass.LongPoll;
        }

        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            ? RequestClass.Read
            : RequestClass.Write;
    }
}

/// <summary>Which of docs/plan/10 § Rate limiting's regimes a request falls under.</summary>
enum RequestClass {
    /// <summary>An ordinary read. Counted against the subscription-read bucket.</summary>
    Read = 0,

    /// <summary>An ordinary write. Counted against the subscription-write bucket.</summary>
    Write,

    /// <summary>A long-poll. ⚠ Exempt from the count buckets; gets a concurrency slot.</summary>
    LongPoll,

    /// <summary>A SignalR handshake or negotiate. ⚠ Same exemption, same reason.</summary>
    Hub
}
