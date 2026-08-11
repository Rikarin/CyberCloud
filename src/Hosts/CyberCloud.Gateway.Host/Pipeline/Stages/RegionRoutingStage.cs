using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Regions;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 4 — serve here, or one hop to the tenant's home region. docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     ⚠ <b>The correlation id survives the hop.</b> docs/plan/10 § Request pipeline says so
///     explicitly, and it is the difference between a two-region trace and two unrelated traces. The
///     forwarding gateway sends its own <see cref="GatewayHeaders.CorrelationRequestId" /> along and
///     stamps <see cref="GatewayHeaders.ForwardedByRegion" />, which is also the loop guard.
/// </remarks>
sealed class RegionRoutingStage(GatewayOptions options, IRegionProxy proxy) : IGatewayStage {
    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.RegionRouting;

    /// <inheritdoc />
    public async Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var forwardedBy = context.Http.Request.Headers.TryGetValue(
            GatewayHeaders.ForwardedByRegion,
            out var header
        )
            ? header.ToString()
            : "";

        var decision = RegionRouting.Decide(
            context.Tenant?.HomeRegion ?? "",
            options.Region,
            forwardedBy
        );

        switch (decision.Action) {
            case RegionAction.Serve:
                return null;

            case RegionAction.RefuseSecondHop:
                return GatewayOutcome.Failure(
                    StatusCodes.Status502BadGateway,
                    new(
                        ErrorCode.InternalError,
                        $"This request was already forwarded once, by region '{forwardedBy}', and "
                        + $"region '{decision.ThisRegion}' is still not the tenant's home region "
                        + $"('{decision.HomeRegion}'). docs/plan/10 § Request pipeline: one hop, "
                        + "never two. Two regions disagreeing about a tenant's placement is the "
                        + "cause; the directory snapshot on one of them is stale."
                    )
                );

            default:
                var forwarded = await proxy.ForwardAsync(context.Http, decision, cancellationToken);

                return forwarded.TryGetError(out var error)
                    ? ResultShaper.Shape(error, context.Http.Request.Path.Value ?? "")
                    // The proxy wrote the home region's response through. Nothing else to do, and
                    // stage 9 must not write a body over it — an empty outcome says exactly that.
                    : new GatewayOutcome { StatusCode = context.Http.Response.StatusCode };
        }
    }
}
