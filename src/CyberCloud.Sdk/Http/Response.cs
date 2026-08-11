using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

namespace CyberCloud.Sdk;

/// <summary>
///     One HTTP response, as the SDK sees it: a status, headers and a buffered body.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/21 § The .NET SDK borrows this shape from Azure.Core and implements it here —
///         the table's third column reads <b>Ours</b> on every row, and this is the bottom of that
///         stack.
///     </para>
///     <para>
///         ⚠ <b>The body is buffered, deliberately.</b> Every response this SDK handles is a
///         control-plane document: a resource, an operation status, a page of ids, an error. Buffering
///         is what lets the retry strategy replay an attempt, the error mapper parse a body the
///         caller will also read, and <see cref="Operation{T}" /> keep the last poll's
///         response after the connection is gone. A streaming response would be the right shape for a
///         blob download; there is no blob download on this API.
///     </para>
/// </remarks>
public abstract class Response {
    /// <summary>The HTTP status code.</summary>
    public abstract int Status { get; }

    /// <summary>The reason phrase, when the server sent one.</summary>
    public abstract string? ReasonPhrase { get; }

    /// <summary>The buffered body. Never <see langword="null" />; possibly empty.</summary>
    public abstract ReadOnlyMemory<byte> Content { get; }

    /// <summary>Every header, response and content headers together.</summary>
    public abstract IEnumerable<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Reads one header's first value.</summary>
    /// <param name="name">The header name, matched case-insensitively.</param>
    /// <param name="value">The value, or <see langword="null" />.</param>
    public abstract bool TryGetHeader(string name, [NotNullWhen(true)] out string? value);

    /// <summary>Whether the status is <c>4xx</c> or <c>5xx</c>.</summary>
    public bool IsError => Status >= 400;

    /// <summary>
    ///     The service's own request id, from <c>x-cybercloud-request-id</c> — the identifier to quote
    ///     in a support request. docs/plan/10 § Request pipeline, stage 1.
    /// </summary>
    public string? ServiceRequestId => TryGetHeader(CyberCloudHeaders.RequestId, out var id) ? id : null;

    /// <summary>
    ///     <c>Retry-After</c>, as a delay. Understands both spellings the RFC allows — a number of
    ///     seconds and an HTTP date — because docs/plan/10 § Rate limiting promises the header and does
    ///     not promise which form.
    /// </summary>
    public TimeSpan? RetryAfter {
        get {
            if (!TryGetHeader(CyberCloudHeaders.RetryAfter, out var value))
                return null;

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when)) {
                var delay = when - DateTimeOffset.UtcNow;

                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            return null;
        }
    }

    /// <summary>Pairs a value with the response it was read from.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="response">The response.</param>
    public static Response<T> FromValue<T>(T value, Response response) => new ValueResponse<T>(value, response);

    /// <inheritdoc />
    public override string ToString() => $"Status: {Status} ({ReasonPhrase})";
}

/// <summary>
///     A response paired with a deserialised value, and implicitly convertible to it — so
///     <c>WidgetData data = await client.GetAsync(…)</c> reads as if the call returned the value while
///     the status and headers stay one <c>.GetRawResponse()</c> away.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
public abstract class Response<T> : NullableResponse<T> {
    /// <summary>The value. Always present on this type.</summary>
    public abstract override T Value { get; }

    /// <summary>Always <see langword="true" />. Use <see cref="NullableResponse{T}" /> when it might not be.</summary>
    public override bool HasValue => true;

    /// <summary>Unwraps the value.</summary>
    /// <param name="response">The response.</param>
    public static implicit operator T(Response<T> response) {
        ArgumentNullException.ThrowIfNull(response);

        return response.Value;
    }
}

/// <summary>
///     A response that may or may not carry a value — what a <c>GetIfExists</c> returns.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <remarks>
///     ⚠ The type exists so that "absent" is not spelled as an exception. docs/plan/07 § The
///     enforcement seam makes a resource the caller may not read indistinguishable from one that does
///     not exist, so <c>404</c> is an ordinary answer on this API rather than an exceptional one, and a
///     caller checking existence in a loop should not be paying for a throw each time.
/// </remarks>
public abstract class NullableResponse<T> {
    /// <summary>The value.</summary>
    /// <exception cref="InvalidOperationException"><see cref="HasValue" /> is <see langword="false" />.</exception>
    public abstract T Value { get; }

    /// <summary>Whether a value was returned.</summary>
    public abstract bool HasValue { get; }

    /// <summary>The status, headers and body the value came from.</summary>
    public abstract Response GetRawResponse();

    /// <summary>A response with no value.</summary>
    /// <param name="response">The raw response — typically a <c>404</c>.</param>
    public static NullableResponse<T> FromNoValue(Response response) => new NoValueResponse<T>(response);
}

sealed class ValueResponse<T> : Response<T> {
    readonly Response response;

    internal ValueResponse(T value, Response response) {
        Value = value;
        this.response = response;
    }

    public override T Value { get; }

    public override Response GetRawResponse() => response;
}

sealed class NoValueResponse<T> : NullableResponse<T> {
    readonly Response response;

    internal NoValueResponse(Response response) => this.response = response;

    public override T Value
        => throw new InvalidOperationException(
            $"The response carries no value (status {response.Status}). Check HasValue first.");

    public override bool HasValue => false;

    public override Response GetRawResponse() => response;
}

/// <summary>A <see cref="Response" /> over a completed, buffered <see cref="HttpResponseMessage" />.</summary>
sealed class BufferedHttpResponse : Response {
    readonly HttpResponseHeaders headers;
    readonly HttpContentHeaders? contentHeaders;

    BufferedHttpResponse(int status, string? reasonPhrase, ReadOnlyMemory<byte> content, HttpResponseHeaders headers, HttpContentHeaders? contentHeaders) {
        Status = status;
        ReasonPhrase = reasonPhrase;
        Content = content;
        this.headers = headers;
        this.contentHeaders = contentHeaders;
    }

    public override int Status { get; }

    public override string? ReasonPhrase { get; }

    public override ReadOnlyMemory<byte> Content { get; }

    public override IEnumerable<KeyValuePair<string, string>> Headers {
        get {
            foreach (var header in headers)
                yield return new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value));

            if (contentHeaders is null)
                yield break;

            foreach (var header in contentHeaders)
                yield return new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value));
        }
    }

    public override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value) {
        if (headers.TryGetValues(name, out var values) || contentHeaders?.TryGetValues(name, out values) == true) {
            value = string.Join(",", values!);

            return true;
        }

        value = null;

        return false;
    }

    /// <summary>Buffers a live response. The <see cref="HttpResponseMessage" /> may be disposed afterwards.</summary>
    public static async ValueTask<BufferedHttpResponse> CreateAsync(HttpResponseMessage message, CancellationToken cancellationToken) {
        var bytes = await message.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        return new BufferedHttpResponse(
            (int)message.StatusCode,
            message.ReasonPhrase,
            bytes,
            message.Headers,
            message.Content.Headers);
    }
}
