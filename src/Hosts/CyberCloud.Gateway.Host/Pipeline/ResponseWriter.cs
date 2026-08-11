using CyberCloud.Gateway.Host.Http;
using System.Text;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     Stage 9 — the only thing in this assembly that writes to an <c>HttpResponse</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One writer is what makes three rules hold on every response rather than on most of
///         them.</b> docs/plan/10 § Request pipeline puts <c>x-cybercloud-request-id</c> on every
///         response; docs/plan/08 § Errors bans exception detail in every body; docs/plan/10
///         § Rate limiting wants the remaining-budget headers on successes as well as on the
///         <c>429</c>. Each of those is trivially true here and would be nine separate acts of care
///         if the stages wrote their own responses.
///     </para>
///     <para>
///         ⚠ <b>An error body is rendered by <see cref="ErrorBody" /> and cannot be anything else.</b>
///         The outcome carries an <see cref="Error" />, not a string, so there is no path by which a
///         caught exception's <c>ToString()</c> reaches a caller — docs/plan/08 § Errors, <i>"No
///         exception details, ever"</i>.
///     </para>
/// </remarks>
static class ResponseWriter {
    /// <summary>Writes the outcome.</summary>
    /// <param name="context">The request, for the ids and the rate-limit headers.</param>
    /// <param name="outcome">What to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task WriteAsync(
        GatewayRequestContext context,
        GatewayOutcome outcome,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

        context.Trace.Enter(GatewayStage.ShapeResponse);

        var response = context.Http.Response;

        if (response.HasStarted) {
            // A region proxy already streamed the home region's response through. Writing again
            // would corrupt it, and the ids are already on it because the proxy copied them.
            return;
        }

        response.StatusCode = outcome.StatusCode;

        // On EVERY response, including every error. docs/plan/10 § Request pipeline.
        response.Headers[GatewayHeaders.RequestId] = context.RequestId;
        response.Headers[GatewayHeaders.CorrelationRequestId] = context.CorrelationId;

        foreach (var header in context.RateLimitHeaders) {
            response.Headers[header.Name] = header.Value;
        }

        foreach (var header in outcome.Headers) {
            response.Headers[header.Name] = header.Value;
        }

        if (outcome.Error is { } error) {
            var body = ErrorBody.Render(error);
            response.ContentType = ErrorBody.ContentType;
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, cancellationToken);
            return;
        }

        if (outcome.Json is not { Length: > 0 } json) {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = ErrorBody.ContentType;
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, cancellationToken);
    }
}
