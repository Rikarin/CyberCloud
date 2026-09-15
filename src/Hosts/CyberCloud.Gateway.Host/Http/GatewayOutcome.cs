using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     One header on a response.
/// </summary>
/// <param name="Name">The header name. Use a constant from <see cref="GatewayHeaders" />.</param>
/// <param name="Value">The value, already formatted.</param>
readonly record struct ResponseHeader(string Name, string Value);

/// <summary>
///     What stage 9 writes: a status code, headers, and at most one body — an error, JSON, or
///     plain text.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Built by a stage, written by stage 9, never written by a stage directly.</b> That
///         split is what keeps the two rules docs/plan/10 § Request pipeline attaches to the ends of
///         the pipeline true of <i>every</i> response: the correlation id is on all of them, including
///         the errors the first eight stages produce, and no body can be written that did not pass
///         through <see cref="ErrorBody" />. A stage that wrote to <c>HttpResponse</c> itself would be
///         a second response path, and the first one to forget a header would be found by a customer.
///     </para>
///     <para>
///         ⚠ <b>The body is a closed set of kinds, and the content type is a consequence of the kind
///         rather than a member.</b> docs/plan/18 § Disclosure names the cost of serving
///         <c>security.txt</c> from this host: <i>"giving the outcome a content type … loosens the
///         single-response-path invariant"</i>. So the outcome was not given one. <see cref="Text" />
///         is the third kind, it is always <c>text/plain; charset=utf-8</c>, and
///         <see cref="Pipeline.ResponseWriter" /> refuses an outcome that sets two kinds rather than picking
///         one. A stage cannot name a media type, cannot emit HTML, and cannot route around
///         <see cref="ErrorBody" /> for an error — the invariant is the same size it was, with one
///         more member. A fourth kind needs a fourth member and its own paragraph here.
///     </para>
/// </remarks>
sealed record GatewayOutcome {
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; init; } = StatusCodes.Status200OK;

    /// <summary>The error, when this outcome is a failure. Rendered by <see cref="ErrorBody" />.</summary>
    public Error? Error { get; init; }

    /// <summary>A success body, as JSON text, or <see langword="null" /> for no body.</summary>
    public string? Json { get; init; }

    /// <summary>
    ///     A success body, as plain text, or <see langword="null" /> for no body. Written as
    ///     <c>text/plain; charset=utf-8</c> and nothing else.
    /// </summary>
    /// <remarks>
    ///     One producer: the RFC 9116 <c>security.txt</c> that <c>DispatchStage</c> serves from
    ///     <c>SecurityTxt</c>. It exists because RFC 9116 § 3 requires <c>text/plain</c> and a JSON
    ///     rendering of the file would not be the file. Setting it together with <see cref="Json" />
    ///     or <see cref="Error" /> is a programming error <see cref="Pipeline.ResponseWriter" /> throws on.
    /// </remarks>
    public string? Text { get; init; }

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
    public static GatewayOutcome Failure(int status, Error error) => new() { StatusCode = status, Error = error };

    /// <summary>A <c>200</c> carrying plain text.</summary>
    /// <param name="text">The body, as committed. Written as UTF-8.</param>
    public static GatewayOutcome PlainText(string text) => new() { StatusCode = StatusCodes.Status200OK, Text = text };
}
