namespace CyberCloud.Cli.Execution;

/// <summary>
///     A failed request, carrying the status the exit-code mapping needs and the flag the user typed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A wrapper rather than a subclass of the SDK's exception, because the status must
///         survive.</b> <see cref="CyberCloudRequestFailedException" /> only sets
///         <see cref="CyberCloudRequestFailedException.Status" /> through its response constructor, so
///         re-throwing it with an added sentence would produce an exception with status <c>0</c> and
///         send every failure to exit code 1.
///     </para>
///     <para>
///         ⚠ <b><see cref="Flag" /> is docs/plan/08 § Errors' <c>target</c>, translated.</b> That
///         document makes <c>target</c> a JSON Pointer <i>"so the portal can highlight the field"</i>;
///         a terminal has no field to highlight and the equivalent is naming the flag, because nobody
///         typed <c>/properties/tier</c>.
///     </para>
/// </remarks>
sealed class CycRequestException : Exception {
    /// <summary>Creates the exception.</summary>
    public CycRequestException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public CycRequestException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public CycRequestException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>The HTTP status, which is what chooses the exit code.</summary>
    public int Status { get; private init; }

    /// <summary>The platform's stable error code — <c>QuotaExceeded</c>.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>The flag whose value was rejected, when the error's <c>target</c> named one.</summary>
    public string? Flag { get; private init; }

    /// <summary>The platform's request id, to quote in a support request.</summary>
    public string? ServiceRequestId { get; private init; }

    /// <summary>Builds the exception from a failed response.</summary>
    /// <param name="response">The response.</param>
    /// <param name="flag">The flag the error's <c>target</c> maps to, or <c>null</c>.</param>
    public static CycRequestException From(Response response, string? flag) {
        ArgumentNullException.ThrowIfNull(response);

        var failure = CyberCloudClientContext.CreateFailure(response);

        return new CycRequestException(failure.Message, failure) {
            Status = failure.Status,
            ErrorCode = failure.ErrorCode,
            Flag = flag,
            ServiceRequestId = failure.ServiceRequestId,
        };
    }

    /// <summary>Builds the exception from an operation that reached <c>Failed</c>.</summary>
    /// <param name="failure">What the SDK threw.</param>
    public static CycRequestException From(CyberCloudRequestFailedException failure) {
        ArgumentNullException.ThrowIfNull(failure);

        return new CycRequestException(failure.Message, failure) {
            // ⚠ A poll that reports a failed operation is itself a 200, so the status here is the
            // poll's. Mapping that to exit 0 would be absurd; an operation that failed is a server
            // failure unless its error code says otherwise, which is what Program's mapping reads.
            Status = failure.Status == 200 ? 500 : failure.Status,
            ErrorCode = failure.ErrorCode,
            ServiceRequestId = failure.ServiceRequestId,
        };
    }
}
