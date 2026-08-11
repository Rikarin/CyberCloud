using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Http;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 2 — the credential becomes claims. docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     ⚠ <b>The claims are parked, not applied.</b> This stage does not set
///     <see cref="GatewayRequestContext.Caller" />; stage 3 does, after it has resolved the tenant
///     and refused every request whose path, header, query or body disagrees. Splitting them means
///     there is no window in which a caller context exists that has not been through the tenant
///     check — and it means the tenant check has exactly one place to live rather than being
///     "wherever the token is read".
/// </remarks>
sealed class AuthenticateStage(ICallerContextResolver resolver) : IGatewayStage {
    /// <summary>Where stage 2 leaves the claims for stage 3.</summary>
    /// <remarks>An <c>HttpContext.Items</c> key rather than a field, so the stage stays a singleton.</remarks>
    public const string ClaimsItemKey = "cybercloud.token-claims";

    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Authenticate;

    /// <summary>
    ///     The two routes docs/plan/10 § Shape serves without a token.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An anonymous route is not an unlimited one.</b> These two are the only requests that
    ///     reach stage 5 with no tenant, which is what makes docs/plan/10 § Rate limiting's <i>per IP,
    ///     unauthenticated</i> bucket reachable at all — see the defect note in
    ///     <c>GatewayRateLimiter</c>. Both are public documents by design: the OpenAPI document is
    ///     the generated API surface and the discovery document is OIDC's, and neither says anything
    ///     about a tenant.
    /// </remarks>
    public static bool IsAnonymous(PathString path) =>
        path.StartsWithSegments("/openapi") || path.StartsWithSegments("/.well-known");

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var resolved = await resolver.ResolveAsync(context.Http.Request, cancellationToken);

        if (resolved.TryGetError(out var error)) {
            if (IsAnonymous(context.Http.Request.Path)) {
                // No claims parked. Stage 3 leaves the caller empty and stage 5 counts the request
                // against the per-IP bucket instead of a tenant's.
                return null;
            }

            // 401 rather than the 403 the error code maps to. The distinction is real: 403 says "we
            // know who you are and the answer is no", and saying that to an anonymous caller hides
            // the one thing they can act on — that they need to authenticate.
            return GatewayOutcome.Failure(StatusCodes.Status401Unauthorized, error)
                .WithHeader("WWW-Authenticate", "Bearer");
        }

        context.Http.Items[ClaimsItemKey] = resolved.GetValueOrThrow();
        return null;
    }
}
