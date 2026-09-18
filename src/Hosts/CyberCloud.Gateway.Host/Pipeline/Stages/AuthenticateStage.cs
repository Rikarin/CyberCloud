using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Identity.Validation;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 2 — the credential becomes claims. docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The claims are parked, not applied.</b> This stage does not set
///         <see cref="GatewayRequestContext.Caller" />; stage 3 does, after it has resolved the tenant
///         and refused every request whose path, header, query or body disagrees. Splitting them means
///         there is no window in which a caller context exists that has not been through the tenant
///         check — and it means the tenant check has exactly one place to live rather than being
///         "wherever the token is read".
///     </para>
///     <para>
///         ⚠
///         <b>
///             Two credentials, one of them derived from the other, and the second is read from the
///             query string on exactly one shape of request.
///         </b> <see cref="ICallerContextResolver" />
///         reads the <c>Authorization</c> header and nothing else, and its remarks say why: anything
///         else is a caller-controlled surface inside authentication. A browser's WebSocket upgrade
///         has no header to read, so for a request that is exactly a hub's path, carries no header and
///         carries <c>?ticket=</c>, the claims come from <see cref="IHubTicketStore" /> instead — claims
///         this same stage produced seconds earlier for the request that minted the ticket, so nothing
///         is established here that a header did not establish first. <c>HubTickets</c> carries the
///         argument; what is stated here is the ordering: the header is tried first and, when present,
///         decides alone, so a ticket is never a second chance for a refused token.
///     </para>
/// </remarks>
sealed class AuthenticateStage(ICallerContextResolver resolver, IHubTicketStore tickets) : IGatewayStage {
    /// <summary>Where stage 2 leaves the claims for stage 3.</summary>
    /// <remarks>An <c>HttpContext.Items</c> key rather than a field, so the stage stays a singleton.</remarks>
    public const string ClaimsItemKey = "cybercloud.token-claims";

    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Authenticate;

    /// <summary>
    ///     The three routes docs/plan/10 § Shape serves without an identity-host token.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An anonymous route is not an unlimited one.</b> These three are the only requests
    ///     that reach stage 5 with no tenant, which is what makes docs/plan/10 § Rate limiting's
    ///     <i>
    ///         per IP,
    ///         unauthenticated
    ///     </i> bucket reachable at all — see the defect note in
    ///     <c>GatewayRateLimiter</c>. The first two are public documents by design: the OpenAPI
    ///     document is the generated API surface, and <c>/.well-known</c> holds the RFC 9116
    ///     <c>security.txt</c> today and the OIDC discovery document once it is proxied — neither
    ///     says anything about a tenant. The third, <c>/agent</c>, is anonymous to <i>this</i> stage
    ///     and authenticated at its endpoint, as the comment on it says.
    /// </remarks>
    public static bool IsAnonymous(PathString path) =>
        path.StartsWithSegments("/openapi")
        || path.StartsWithSegments("/.well-known")
        // ⚠ Anonymous to THIS stage only. An agent authenticates with a per-cluster credential the
        // tunnel grain checks at the endpoint — docs/plan/09 § Cluster connections, "the tunnel
        // identity is bound to the cluster resource id at the gateway" — and a JWT from the identity
        // host is not a thing a pod in a tenant's cluster holds.
        || path.StartsWithSegments("/agent");

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var resolved = await resolver.ResolveAsync(context.Http.Request, cancellationToken);

        if (resolved.TryGetError(out var error)) {
            if (HubTickets.IsRedeemableOn(context.Http.Request, out var hub, out var ticket)) {
                var redeemed = await tickets.RedeemAsync(ticket, hub, cancellationToken);

                if (redeemed is { } claims) {
                    context.Http.Items[ClaimsItemKey] = claims;
                    return null;
                }

                // The same 401 an absent header gets, with a reason that says which credential was
                // read. ⚠ Not "expired" versus "spent" versus "another hub's": a ticket is thirty
                // seconds of opaque bytes, and the distinction would tell a holder of a leaked URL
                // which of the three it was.
                return GatewayOutcome.Failure(
                    StatusCodes.Status401Unauthorized,
                    BearerTokenErrors.Unauthenticated(
                        "the hub ticket was not accepted; mint another with POST /hubs/" + hub + "/ticket"
                    )
                )
                    .WithHeader("WWW-Authenticate", "Bearer");
            }

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
