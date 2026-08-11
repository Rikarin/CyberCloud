namespace CyberCloud.Sdk;

/// <summary>
///     The exception every failed call throws. It carries the whole of docs/plan/08 § Errors' shape:
///     the status, the stable <c>code</c>, the human message, the JSON Pointer and the details.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Target" /> is the member the API exists to deliver.</b> docs/plan/08
///         § Errors: <i>"<c>target</c> is a JSON Pointer into the request body so the portal can
///         highlight the field."</i> A pointer that reaches the SDK and stops there turns "this input
///         was rejected" back into "the request was rejected", and a form that cannot say which box is
///         wrong is a form the user fixes by guessing. <c>ErrorTargetSurvivesTests</c> follows one
///         from a scripted <c>400</c> to this property.
///     </para>
///     <para>
///         ⚠ <b>The message never contains credential material.</b> It is built from the status, the
///         code, the service's own message and the service request id — never from the request, its
///         headers or its body.
///     </para>
/// </remarks>
public class CyberCloudRequestFailedException : Exception {
    /// <summary>Creates the exception with no detail. Present so the type has the standard set.</summary>
    public CyberCloudRequestFailedException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public CyberCloudRequestFailedException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public CyberCloudRequestFailedException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>Builds the exception from a failed response, parsing its body for the error shape.</summary>
    /// <param name="response">The response.</param>
    public CyberCloudRequestFailedException(Response response) : this(response, CyberCloudError.TryParse(response?.Content)) { }

    /// <summary>Builds the exception from a response and an already-parsed error.</summary>
    /// <param name="response">
    ///     The response the error came from. ⚠ For a long-running operation that reached <c>Failed</c>
    ///     this is the <b>poll</b> response, so <see cref="Status" /> is <c>200</c> — the poll
    ///     succeeded and reported a failure. The operation's own outcome is in
    ///     <see cref="ErrorCode" />, <see cref="Exception.Message" /> and <see cref="Target" />.
    /// </param>
    /// <param name="error">The parsed error, or <see langword="null" /> when the body did not carry one.</param>
    public CyberCloudRequestFailedException(Response response, CyberCloudError? error)
        : base(Describe(response, error)) {
        ArgumentNullException.ThrowIfNull(response);

        Status = response.Status;
        ErrorCode = error?.Code;
        Target = error?.Target;
        Details = error?.Details ?? [];
        ServiceRequestId = response.ServiceRequestId;
        RawResponse = response;
    }

    /// <summary>The HTTP status.</summary>
    public int Status { get; }

    /// <summary>
    ///     The platform's stable, documented <c>code</c> — <c>QuotaExceeded</c>. docs/plan/08 § Errors
    ///     makes it part of the API contract, so branching on it is supported.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    ///     An RFC 6901 JSON Pointer into the request body — <c>/properties/sku</c> — naming the field
    ///     that was rejected, or <see langword="null" /> when the error is not about one field.
    /// </summary>
    public string? Target { get; }

    /// <summary>Every other problem found in the same request. Never <see langword="null" />; possibly empty.</summary>
    public IReadOnlyList<CyberCloudError> Details { get; } = [];

    /// <summary>
    ///     The platform's own request id, from <c>x-cybercloud-request-id</c> — the identifier to quote
    ///     in a support request.
    /// </summary>
    public string? ServiceRequestId { get; }

    /// <summary>The response, for a caller that needs a header the SDK does not model.</summary>
    public Response? RawResponse { get; }

    static string Describe(Response? response, CyberCloudError? error) {
        if (response is null)
            return "The request failed.";

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Status: {response.Status} ({response.ReasonPhrase})");

        if (error is not null) {
            builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}ErrorCode: {error.Code}");
            builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}Message: {error.Message}");

            if (error.Target is not null)
                builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}Target: {error.Target}");

            foreach (var detail in error.Details)
                builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  • {detail}");
        }

        // ⚠ The request id and nothing else about the request. docs/plan/08 § Errors puts the
        // correlation id in the header precisely so that this line can replace a body dump.
        if (response.ServiceRequestId is { } id)
            builder.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}RequestId: {id}");

        return builder.ToString();
    }
}
