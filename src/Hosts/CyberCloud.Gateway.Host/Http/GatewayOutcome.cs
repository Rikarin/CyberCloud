using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     One header on a response.
/// </summary>
/// <param name="Name">The header name. Use a constant from <see cref="GatewayHeaders" />.</param>
/// <param name="Value">The value, already formatted.</param>
readonly record struct ResponseHeader(string Name, string Value);

/// <summary>
///     What stage 9 writes: a status code, headers, and either a JSON body or nothing.
/// </summary>
/// <remarks>
///     ⚠ <b>Built by a stage, written by stage 9, never written by a stage directly.</b> That split
///     is what keeps the two rules docs/plan/10 § Request pipeline attaches to the ends of the
///     pipeline true of <i>every</i> response: the correlation id is on all of them, including the
///     errors the first eight stages produce, and no body can be written that did not pass through
///     <see cref="ErrorBody" />. A stage that wrote to <c>HttpResponse</c> itself would be a second
///     response path, and the first one to forget a header would be found by a customer.
/// </remarks>
sealed record GatewayOutcome {
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; init; } = StatusCodes.Status200OK;

    /// <summary>The error, when this outcome is a failure. Rendered by <see cref="ErrorBody" />.</summary>
    public Error? Error { get; init; }

    /// <summary>A success body, as JSON text, or <see langword="null" /> for no body.</summary>
    public string? Json { get; init; }

    /// <summary>Headers to add. Never default; possibly empty.</summary>
    public ImmutableArray<ResponseHeader> Headers { get; init; } = [];

    /// <summary>Returns this outcome with one more header.</summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The value.</param>
    public GatewayOutcome WithHeader(string name, string value) =>
        this with { Headers = Headers.Add(new(name, value)) };

    /// <summary>An error outcome.</summary>
    /// <param name="status">The status code <see cref="ResultShaper" /> chose for the code.</param>
    /// <param name="error">The error.</param>
    public static GatewayOutcome Failure(int status, Error error) =>
        new() { StatusCode = status, Error = error };
}
