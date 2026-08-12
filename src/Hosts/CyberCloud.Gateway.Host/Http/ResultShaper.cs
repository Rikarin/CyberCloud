using System.Collections.Frozen;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     Stage 9 — <see cref="Result" /> to a status code and the one error body. docs/plan/10
///     § Request pipeline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The status code is authorization output, so this table is a security artefact.</b>
///         docs/plan/07 § The enforcement seam: <i>"403 is returned only when the caller can read the
///         object but not perform the action — which is a real and useful distinction, and it means
///         the response code itself is authorization output."</i>
///         <see cref="ErrorCode.ResourceNotFound" /> is <c>404</c> and
///         <see cref="ErrorCode.AuthorizationFailed" /> is <c>403</c>, and the component that decides
///         which of the two to return is the resource manager, not this file.
///     </para>
///     <para>
///         ⚠ <b><see cref="ErrorCode.SchemaInvalid" /> maps to <c>404</c>, not <c>500</c>.</b>
///         docs/plan/07 § The enforcement seam says so in as many words: a check against an
///         undefined relation <i>"still renders it to the caller as 404; the difference is that it
///         also appears on a dashboard"</i>. Rendering it as a <c>500</c> would tell a prober that
///         their probe hit something real.
///     </para>
/// </remarks>
static class ResultShaper {
    static readonly FrozenDictionary<string, int> StatusByCode = new Dictionary<string, int>(StringComparer.Ordinal) {
        [ErrorCode.AuthorizationFailed.Value] = StatusCodes.Status403Forbidden,
        [ErrorCode.Conflict.Value] = StatusCodes.Status409Conflict,
        [ErrorCode.InternalError.Value] = StatusCodes.Status500InternalServerError,
        [ErrorCode.InvalidApiVersion.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.InvalidGrainKey.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.InvalidRequestBody.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.InvalidResourceId.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.InvalidResourceName.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.InvalidResourceType.Value] = StatusCodes.Status400BadRequest,
        [ErrorCode.OperationCanceled.Value] = StatusCodes.Status409Conflict,
        [ErrorCode.OperationInProgress.Value] = StatusCodes.Status409Conflict,
        [ErrorCode.OperationTimeout.Value] = StatusCodes.Status500InternalServerError,
        [ErrorCode.PolicyViolation.Value] = StatusCodes.Status403Forbidden,
        [ErrorCode.PreconditionFailed.Value] = StatusCodes.Status412PreconditionFailed,
        [ErrorCode.ProvisioningFailed.Value] = StatusCodes.Status500InternalServerError,
        [ErrorCode.QuotaExceeded.Value] = StatusCodes.Status429TooManyRequests,
        [ErrorCode.ResourceAlreadyExists.Value] = StatusCodes.Status409Conflict,
        // The three "some scope above the resource is missing" codes. All 404, and all re-rendered
        // through GatewayErrors.NotFound: "that subscription does not exist" and "that resource does
        // not exist" must not be distinguishable, or the subscription id is enumerable.
        [ErrorCode.ResourceGroupNotFound.Value] = StatusCodes.Status404NotFound,
        // ⚠ 409 and NOT rewritten to the canonical 404, which is safe only because the caller has
        // already been through the enforcement seam on this path: DeleteAsync authorizes before the
        // child gate runs, so anyone who sees this could already read the resource and its children
        // are not an existence oracle. docs/plan/08 § Deleting a parent resource that has children.
        [ErrorCode.ResourceHasChildren.Value] = StatusCodes.Status409Conflict,
        [ErrorCode.ResourceNotFound.Value] = StatusCodes.Status404NotFound,
        [ErrorCode.SubscriptionNotFound.Value] = StatusCodes.Status404NotFound,
        [ErrorCode.TenantNotFound.Value] = StatusCodes.Status404NotFound,
        [ErrorCode.SchemaInvalid.Value] = StatusCodes.Status404NotFound,
        [ErrorCode.ScopeLocked.Value] = StatusCodes.Status409Conflict,
        [ErrorCode.TenantSuspended.Value] = StatusCodes.Status403Forbidden
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    ///     The codes whose body is replaced by <see cref="GatewayErrors.NotFound" /> on the way out.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Replacing the message is the point, not a tidy-up.</b> Four components below produce
    ///     a <c>404</c> for four different reasons and each writes its own sentence. Left alone, the
    ///     sentences differ, and the difference reconstructs exactly the distinction the shared
    ///     status code was chosen to erase.
    /// </remarks>
    static readonly FrozenSet<string> RewrittenToNotFound = new[] {
        ErrorCode.ResourceNotFound.Value,
        ErrorCode.ResourceGroupNotFound.Value,
        ErrorCode.SubscriptionNotFound.Value,
        ErrorCode.TenantNotFound.Value,
        ErrorCode.SchemaInvalid.Value
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Turns a failure into the outcome stage 9 writes.</summary>
    /// <param name="error">The error the pipeline stopped with.</param>
    /// <param name="requestPath">
    ///     The path the caller asked for. Used only to build the canonical <c>404</c>, so that the
    ///     body never carries anything the gateway learned while refusing the request.
    /// </param>
    public static GatewayOutcome Shape(Error error, string requestPath) {
        ArgumentNullException.ThrowIfNull(error);

        var status = StatusByCode.TryGetValue(error.Code.Value, out var mapped)
            ? mapped
            // An unmapped code is a code added to the registry without a status. 500 rather than a
            // guess: a wrong 4xx would be cached by a client and a wrong 404 would hide a real fault.
            : StatusCodes.Status500InternalServerError;

        return GatewayOutcome.Failure(
            status,
            RewrittenToNotFound.Contains(error.Code.Value) ? GatewayErrors.NotFound(requestPath) : error
        );
    }

    /// <summary>The status code a registered error code renders as.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The status, or <c>500</c> for a code with no mapping.</returns>
    public static int StatusFor(ErrorCode code) {
        ArgumentNullException.ThrowIfNull(code);

        return StatusByCode.TryGetValue(code.Value, out var mapped)
            ? mapped
            : StatusCodes.Status500InternalServerError;
    }

    /// <summary>Whether a code's body is replaced by the canonical <c>404</c>.</summary>
    /// <param name="code">The code.</param>
    public static bool IsRewrittenToNotFound(ErrorCode code) {
        ArgumentNullException.ThrowIfNull(code);

        return RewrittenToNotFound.Contains(code.Value);
    }
}
