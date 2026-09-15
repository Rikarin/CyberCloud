using CyberCloud.Gateway.Host.Http;
using System.Text;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     Stage 9 — the only thing in this assembly that writes to an <c>HttpResponse</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             One writer is what makes three rules hold on every response rather than on most of
///             them.
///         </b> docs/plan/10 § Request pipeline puts <c>x-cybercloud-request-id</c> on every
///         response; docs/plan/08 § Errors bans exception detail in every body; docs/plan/10
///         § Rate limiting wants the remaining-budget headers on successes as well as on the
///         <c>429</c>. Each of those is trivially true here and would be nine separate acts of care
///         if the stages wrote their own responses.
///     </para>
///     <para>
///         ⚠ <b>An error body is rendered by <see cref="ErrorBody" /> and cannot be anything else.</b>
///         The outcome carries an <see cref="Error" />, not a string, so there is no path by which a
///         caught exception's <c>ToString()</c> reaches a caller — docs/plan/08 § Errors,
///         <i>
///             "No
///             exception details, ever"
///         </i>.
///     </para>
///     <para>
///         ⚠ <b>Three body kinds, each with a fixed content type, and an outcome that sets two is
///         refused rather than resolved.</b> Error is <see cref="ErrorBody.ContentType" />, JSON is
///         the same, and <see cref="GatewayOutcome.Text" /> is <c>text/plain; charset=utf-8</c> —
///         the media type is decided here, by kind, and no stage can name one. Choosing a
///         precedence for a two-bodied outcome would make the mistake invisible; throwing makes it
///         an unhandled exception on the first request that hits it — the server's <c>500</c>,
///         not this writer's, because a stage that built two bodies is a defect in the stage and
///         not an outcome to shape. <c>SecurityTxtTests.AnOutcomeWithTwoBodiesIsRefusedNotResolved</c>
///         pins it.
///     </para>
/// </remarks>
static class ResponseWriter {
    /// <summary>The media type of a <see cref="GatewayOutcome.Text" /> body. RFC 9116 § 3's, and nothing else's.</summary>
    public const string TextContentType = "text/plain; charset=utf-8";

    /// <summary>Writes the outcome.</summary>
    /// <param name="context">The request, for the ids and the rate-limit headers.</param>
    /// <param name="outcome">What to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">
    ///     The outcome sets more than one of <see cref="GatewayOutcome.Error" />,
    ///     <see cref="GatewayOutcome.Json" />, and <see cref="GatewayOutcome.Text" />. A stage built
    ///     an outcome with two bodies, and there is no right one to pick.
    /// </exception>
    public static async Task WriteAsync(
        GatewayRequestContext context,
        GatewayOutcome outcome,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

        var kinds = (outcome.Error is null ? 0 : 1) + (outcome.Json is null ? 0 : 1) + (outcome.Text is null ? 0 : 1);
        if (kinds > 1) {
            throw new InvalidOperationException(
                $"The outcome for request {context.RequestId} sets {kinds} body kinds (error: "
                + $"{outcome.Error is not null}, json: {outcome.Json is not null}, text: {outcome.Text is not null}) "
                + "and a response has one body. GatewayOutcome is a closed set of kinds and a stage "
                + "chooses exactly one; see its remarks."
            );
        }

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

        if (outcome.Json is { Length: > 0 } json) {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = ErrorBody.ContentType;
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, cancellationToken);
            return;
        }

        if (outcome.Text is { Length: > 0 } text) {
            // The one non-JSON body this gateway writes, and the type is fixed here rather than
            // carried on the outcome — see GatewayOutcome's remarks on why that is the invariant.
            var bytes = Encoding.UTF8.GetBytes(text);
            response.ContentType = TextContentType;
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes, cancellationToken);
        }
    }
}
