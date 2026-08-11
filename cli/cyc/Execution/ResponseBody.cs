using CyberCloud.Cli.Output;

namespace CyberCloud.Cli.Execution;

/// <summary>
///     A parsed response body, owned for as long as the renderer needs it.
/// </summary>
/// <remarks>
///     ⚠ <b>The <see cref="JsonDocument" /> has to outlive the <see cref="Payload" />.</b>
///     <c>Payload</c> wraps <see cref="JsonElement" />, which is a view over the document's buffer, so
///     disposing the document first hands the renderer freed memory. Every caller holds this type in a
///     <c>using</c> and renders inside it.
/// </remarks>
sealed class ResponseBody : IDisposable {
    readonly JsonDocument? document;

    ResponseBody(JsonDocument? document, Payload value) {
        this.document = document;
        Value = value;
    }

    /// <summary>The body, or <see cref="Payload.Missing" /> when there was none.</summary>
    public Payload Value { get; }

    /// <summary>An empty body — a <c>204</c>, or a <c>DELETE</c> that finished.</summary>
    public static ResponseBody Empty { get; } = new(null, Payload.Missing);

    /// <summary>Parses a response.</summary>
    /// <param name="response">The response, whose <see cref="Response.Content" /> is already buffered.</param>
    /// <exception cref="CycClientException">
    ///     The body is not JSON. ⚠ Reported rather than swallowed: the platform's contract is that
    ///     every response is JSON (docs/plan/10 § Shape), so HTML arriving here means something in
    ///     front of the gateway answered instead — a captive portal, a proxy, an expired ingress — and
    ///     "empty output, exit 0" would be the wrong answer.
    /// </exception>
    public static ResponseBody Parse(Response response) {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content.Length == 0)
            return Empty;

        try {
            var parsed = JsonDocument.Parse(response.Content);

            return new ResponseBody(parsed, Payload.Of(parsed.RootElement));
        } catch (JsonException e) {
            throw new CycClientException(
                $"The response to this request was not JSON (status {response.Status}, "
                + $"{response.Content.Length} byte(s)). Request id: {response.ServiceRequestId ?? "none"}.",
                e);
        }
    }

    /// <inheritdoc />
    public void Dispose() => document?.Dispose();
}

/// <summary>
///     Turns the resource <c>GET</c> that follows a successful operation into a body the CLI can
///     render — <see cref="IOperationSource{T}" />, implemented once for an untyped host.
/// </summary>
/// <remarks>
///     ⚠ The generated SDK has one of these per resource type, producing a <c>{Type}Resource</c>. The
///     CLI wants the document rather than the type, so it has exactly one — which is also what lets
///     <c>cyc</c> drive a verb the generator emitted after this binary was built.
/// </remarks>
sealed class ResponseBodyOperationSource : IOperationSource<ResponseBody> {
    /// <inheritdoc />
    public ValueTask<ResponseBody> CreateResultAsync(Response response, CancellationToken cancellationToken)
        => ValueTask.FromResult(ResponseBody.Parse(response));
}
