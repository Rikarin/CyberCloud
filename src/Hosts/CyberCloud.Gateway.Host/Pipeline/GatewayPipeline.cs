using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     The nine stages of docs/plan/10 § Request pipeline, run in order, once per request.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The order is checked at construction, not trusted.</b> The stages arrive from the
///         container, and DI ordering is registration order — which is a comment in a file somebody
///         else edits. <see cref="GatewayPipeline(IEnumerable{IGatewayStage}, ILogger{GatewayPipeline})" />
///         sorts them by <see cref="IGatewayStage.Stage" /> and throws if the set is not exactly the
///         eight stages that can stop a request. A missing stage 3 would be a cross-tenant hole
///         introduced by a registration line.
///     </para>
///     <para>
///         ⚠ <b>Stage 9 always runs</b>, including after an earlier stage stopped, which is what puts
///         the correlation id on error responses.
///     </para>
/// </remarks>
sealed class GatewayPipeline {
    /// <summary>The eight stages that can stop a request. Stage 9 is <see cref="ResponseWriter" />.</summary>
    public static ImmutableArray<GatewayStage> Stoppable { get; } = [
        GatewayStage.Correlation,
        GatewayStage.Authenticate,
        GatewayStage.ResolveTenant,
        GatewayStage.RegionRouting,
        GatewayStage.RateLimit,
        GatewayStage.Route,
        GatewayStage.Validate,
        GatewayStage.Dispatch
    ];

    readonly ImmutableArray<IGatewayStage> stages;
    readonly ILogger<GatewayPipeline> logger;

    /// <summary>Composes the pipeline, checking that every stage is present exactly once.</summary>
    /// <param name="registered">The stages, in whatever order the container produced them.</param>
    /// <param name="logger">The pipeline's own log.</param>
    /// <exception cref="InvalidOperationException">
    ///     A stage is missing, duplicated, or is not one of the eight. Thrown at startup rather than
    ///     per request, so a wiring mistake is a pod that will not start rather than a hole that
    ///     serves traffic.
    /// </exception>
    public GatewayPipeline(IEnumerable<IGatewayStage> registered, ILogger<GatewayPipeline> logger) {
        ArgumentNullException.ThrowIfNull(registered);

        this.logger = logger;

        var ordered = registered.OrderBy(x => x.Stage).ToImmutableArray();
        var present = ordered.Select(x => x.Stage).ToImmutableArray();

        if (!present.SequenceEqual(Stoppable)) {
            throw new InvalidOperationException(
                $"The gateway pipeline was composed as [{string.Join(", ", present)}] and must be "
                + $"exactly [{string.Join(", ", Stoppable)}]. docs/plan/10 § Request pipeline — a "
                + "missing stage 3 is a cross-tenant hole and a missing stage 5 is an unlimited "
                + "channel, and neither is visible in the response of a request that succeeds."
            );
        }

        stages = ordered;
    }

    /// <summary>Runs one request.</summary>
    /// <param name="http">The ASP.NET Core context.</param>
    /// <returns>
    ///     The context, after stage 9 has written. Returned rather than discarded so a test can
    ///     assert the trace — which is how "no stage moved" becomes an assertion.
    /// </returns>
    public async Task<GatewayRequestContext> RunAsync(HttpContext http) {
        ArgumentNullException.ThrowIfNull(http);

        var context = new GatewayRequestContext(http);
        var cancellationToken = http.RequestAborted;

        GatewayOutcome? outcome = null;

        foreach (var stage in stages) {
            context.Trace.Enter(stage.Stage);

            try {
                outcome = await stage.RunAsync(context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) {
                // ⚠ The exception goes to the log and the correlation id goes to the caller.
                // docs/plan/08 § Errors: "A stack trace in an error body is an information leak and
                // a support-cost multiplier. The correlation id goes in the response header; the
                // details go to the trace."
                logger.LogError(
                    exception,
                    "Gateway stage {Stage} faulted for request {RequestId} (correlation "
                    + "{CorrelationId}). Answered 500 with no detail in the body.",
                    stage.Stage,
                    context.RequestId,
                    context.CorrelationId
                );

                outcome = GatewayOutcome.Failure(
                    StatusCodes.Status500InternalServerError,
                    new(
                        ErrorCode.InternalError,
                        "The request could not be completed. Quote the "
                        + GatewayHeaders.RequestId
                        + " response header when reporting this."
                    )
                );
            }

            if (outcome is not null) {
                break;
            }
        }

        // A hub request that reached stage 8 without an outcome leaves the pipeline for SignalR's
        // own middleware; there is nothing to write and writing an empty 200 would break negotiate.
        if (outcome is null && context.Route.Kind == RouteKind.Hub) {
            return context;
        }

        await ResponseWriter.WriteAsync(
            context,
            outcome ?? GatewayOutcome.Failure(
                StatusCodes.Status404NotFound,
                GatewayErrors.NotFound(http.Request.Path.Value ?? "")
            ),
            cancellationToken
        );

        return context;
    }
}
