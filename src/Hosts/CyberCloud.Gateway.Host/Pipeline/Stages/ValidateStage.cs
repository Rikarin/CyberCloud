using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 7 — everything about a request that can be decided without a grain call.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The JSON Schema check is NOT here, and that is a deliberate reading of two documents
///         that overlap.</b> docs/plan/10 § Request pipeline gives stage 7 as <i>"JSON Schema for that
///         api-version, before any grain call"</i>; docs/plan/08 § The write path, end to end gives
///         the same check as the manager's step 2. Both cannot own it, and putting it here would put
///         a <i>copy</i> of every type's schema on the gateway — a second source of truth for the API
///         surface, which is the exact failure docs/plan/08 § The provider registry exists to make
///         impossible (<i>"the same registry that generates the CLI is the one that validates the
///         request body. That identity is what makes drift impossible rather than merely
///         detectable"</i>).
///     </para>
///     <para>
///         So the gateway validates what is <i>its</i> to validate — the request is a request — and
///         the schema is checked once, in the manager, from the one registry. The document's
///         <i>"before any grain call"</i> is still satisfied for the schema: the manager's step 2 runs
///         before its step 3, and the only grain call ahead of it is the index resolve in step 1,
///         which reads no caller-supplied body.
///     </para>
///     <para>
///         What that costs, stated so it can be paid rather than discovered: a malformed body for a
///         known type reaches the manager's process before it is rejected. That is one in-process
///         call, not a provider call, and it is bounded by stage 5.
///     </para>
/// </remarks>
sealed class ValidateStage(GatewayOptions options) : IGatewayStage {
    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Validate;

    /// <inheritdoc />
    public Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var request = context.Http.Request;

        if (!HttpMethods.IsPut(request.Method)
            && !HttpMethods.IsPatch(request.Method)
            && !HttpMethods.IsPost(request.Method)) {
            return Stop(null);
        }

        if (context.Route.Kind == RouteKind.Hub) {
            return Stop(null);
        }

        // ⚠ Compared against the CAP, not against what was read. ResolveTenantStage stops reading at
        // its cap, so a 2 GB body would otherwise arrive here looking like a 1 MB one and be
        // accepted with its tail silently missing.
        if (request.ContentLength > options.MaxBodyBytes) {
            return Stop(Bad(
                ErrorCode.InvalidRequestBody,
                $"The request body is {request.ContentLength} bytes and the limit is "
                + $"{options.MaxBodyBytes}. A resource's desired state is a description, not a "
                + "payload — a body this size is a client bug."
            ));
        }

        if (context.Body.Length == 0) {
            return HttpMethods.IsPost(request.Method)
                // An action with no arguments is normal — POST /restart has nothing to say.
                ? Stop(null)
                : Stop(Bad(
                    ErrorCode.InvalidRequestBody,
                    $"A {request.Method} needs a body. docs/plan/08 § The write path, end to end: PUT "
                    + "is a full replacement and PATCH is a JSON Merge Patch, and neither means "
                    + "anything without one."
                ));
        }

        var contentType = request.ContentType ?? "";
        if (contentType.Length > 0 && !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) {
            return Stop(new GatewayOutcome {
                StatusCode = StatusCodes.Status415UnsupportedMediaType,
                Error = new(
                    ErrorCode.InvalidRequestBody,
                    $"Content-Type '{contentType}' is not supported. Every body this API accepts is "
                    + "'application/json'."
                )
            });
        }

        try {
            using var document = JsonDocument.Parse(context.Body);

            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return Stop(Bad(
                    ErrorCode.InvalidRequestBody,
                    $"The request body is a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}. "
                    + "A resource body is a JSON object."
                ));
            }
        }
        catch (JsonException exception) {
            // ⚠ The parser's message describes the CALLER'S OWN INPUT — a position and a character —
            // and is not a stack trace. docs/plan/08 § Errors bans exception detail, and this is not
            // any: nothing here names a type, a method or a file of ours.
            return Stop(Bad(ErrorCode.InvalidRequestBody, $"The request body is not valid JSON: {exception.Message}"));
        }

        return Stop(null);
    }

    static GatewayOutcome Bad(ErrorCode code, string message) =>
        GatewayOutcome.Failure(StatusCodes.Status400BadRequest, new(code, message, ""));

    static Task<GatewayOutcome?> Stop(GatewayOutcome? outcome) => Task.FromResult(outcome);
}
