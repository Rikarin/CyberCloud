using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 6 — path plus <c>api-version</c> to a provider and a type, from the registry.
///     docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     ⚠ <b>The <c>api-version</c> check runs before the registry lookup, and the order is
///     visible to callers.</b> A request with no <c>api-version</c> gets a <c>400</c> naming the
///     current version whether or not the path names anything real — docs/plan/10 § API versioning
///     makes the parameter required <i>"on every request"</i>. Doing the registry lookup first would
///     answer <c>404</c> to a caller who simply forgot the parameter, which sends them looking for a
///     missing resource instead of at their URL.
/// </remarks>
sealed class RouteStage(IProviderRegistry registry, GatewayOptions options) : IGatewayStage {
    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Route;

    /// <inheritdoc />
    public Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var request = context.Http.Request;
        var path = request.Path.Value ?? "";

        var routed = GatewayRouter.Resolve(path, request.Method, context.Caller.TenantId);
        if (routed.TryGetError(out var routeError)) {
            return Stop(ResultShaper.Shape(routeError, path));
        }

        var route = routed.GetValueOrThrow();
        context.Route = route;

        // A hub handshake carries no api-version — SignalR's negotiate is not a versioned API
        // surface, and the messages on the hub are versioned by the hub's own contract.
        if (route.Kind == RouteKind.Hub) {
            return Stop(null);
        }

        // ⚠ A COLLECTION IS LOOKED UP LIKE A RESOURCE, off its own address's type. Skipping it would
        // let a GET on a type the registry does not serve resolve an api-version against no
        // registration — so a retired version would be accepted here and refused two stages later,
        // and an unknown type would get its 404 from the resource manager rather than from the stage
        // that owns "is this a path this gateway serves".
        var registration = route.Kind switch {
            RouteKind.Resource or RouteKind.Action => Lookup(route.Resource.Type),
            RouteKind.Collection => Lookup(route.Collection.Type),
            _ => null
        };

        var version = ApiVersioning.Resolve(request.Query, registration, options.CurrentApiVersion);
        if (version.TryGetError(out var versionError)) {
            return Stop(ResultShaper.Shape(versionError, path));
        }

        context.ApiVersion = version.GetValueOrThrow();

        // ⚠ An unknown type is a 404 and it is the canonical one. A distinct "no such provider"
        // message would tell a caller which provider namespaces exist, which is a small leak that
        // becomes a large one once providers are per-customer.
        if (route.Kind is RouteKind.Resource or RouteKind.Action or RouteKind.Collection
            && registration is null) {
            return Stop(GatewayOutcome.Failure(StatusCodes.Status404NotFound, GatewayErrors.NotFound(path)));
        }

        if (route.Kind == RouteKind.Action
            && registration is not null
            && !registration.TryGetAction(route.Action, out _)) {
            return Stop(GatewayOutcome.Failure(StatusCodes.Status404NotFound, GatewayErrors.NotFound(path)));
        }

        return Stop(null);
    }

    /// <summary>
    ///     The type alone, without the api-version.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="IProviderRegistry.TryGetType" /> rather than
    ///     <see cref="IProviderRegistry.Resolve" />, and the split matters for the error a caller
    ///     sees. <c>Resolve</c> answers both questions at once and returns the first failure, so an
    ///     unknown type and an unknown version arrive as one <c>InvalidResourceType</c> — which
    ///     would tell a caller their path is wrong when the real problem is their date.
    /// </remarks>
    ResourceTypeRegistration? Lookup(ResourceTypeName type) =>
        registry.TryGetType(type, out var registration) ? registration : null;

    static Task<GatewayOutcome?> Stop(GatewayOutcome? outcome) => Task.FromResult(outcome);
}
