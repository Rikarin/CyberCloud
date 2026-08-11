using System.Collections.Concurrent;
using CyberCloud.Sdk;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The HTTP seam every test drives the CLI through: a scripted <see cref="HttpMessageHandler" />
///     that records what it was asked for and answers from a function.
/// </summary>
/// <remarks>
///     ⚠ The same shape as <c>CyberCloud.Sdk.Tests</c>'s, and for the same reason: docs/plan/10's
///     gateway does not exist, and this is the injection point a caller uses to point the SDK at a
///     private deployment. Nothing in this suite opens a socket.
/// </remarks>
sealed class ScriptedTransport : HttpMessageHandler {
    readonly Func<HttpRequestMessage, int, HttpResponseMessage> script;
    readonly ConcurrentQueue<RecordedRequest> recorded = new();
    readonly Action<int>? onRequest;

    int count;

    /// <summary>Answers from a function of the request and the zero-based request number.</summary>
    /// <param name="script">The script.</param>
    /// <param name="onRequest">
    ///     Called as each request arrives, before it is answered. ⚠ How the progress-streaming test
    ///     interleaves what the console has seen with what the transport has been asked for.
    /// </param>
    public ScriptedTransport(Func<HttpRequestMessage, int, HttpResponseMessage> script, Action<int>? onRequest = null) {
        this.script = script;
        this.onRequest = onRequest;
    }

    /// <summary>Every request, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. recorded];

    /// <summary>How many requests were made.</summary>
    public int RequestCount => Volatile.Read(ref count);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var index = Interlocked.Increment(ref count) - 1;

        recorded.Enqueue(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

        onRequest?.Invoke(index);

        return script(request, index);
    }

    /// <summary>What the CLI sent.</summary>
    /// <param name="Method">The method.</param>
    /// <param name="Uri">The URL, api-version and all.</param>
    /// <param name="Headers">The request headers, as the pipeline left them.</param>
    /// <param name="Body">The request body.</param>
    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body) {
        /// <summary>One header's value.</summary>
        /// <param name="name">The header name.</param>
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }
}

/// <summary>Builds the responses a test scripts.</summary>
static class Responses {
    /// <summary>A JSON response.</summary>
    /// <param name="status">The status.</param>
    /// <param name="body">The body.</param>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) {
        var response = new HttpResponseMessage(status) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.RequestId, "req-" + Guid.NewGuid().ToString("N")[..8]);

        return response;
    }

    /// <summary>The <c>202</c> that starts a long-running operation — docs/plan/10 § Long-running operations, over HTTP.</summary>
    /// <param name="operationUrl">The <c>Azure-AsyncOperation</c> URL.</param>
    public static HttpResponseMessage Accepted(string operationUrl) {
        var response = Json(HttpStatusCode.Accepted, "{}");

        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.AsyncOperation, operationUrl);
        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.RetryAfter, "0");

        return response;
    }

    /// <summary>An error body in docs/plan/08 § Errors' one shape.</summary>
    /// <param name="status">The status.</param>
    /// <param name="code">The stable error code.</param>
    /// <param name="message">The human message.</param>
    /// <param name="target">The RFC 6901 pointer into the request body, or <c>null</c>.</param>
    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message, string? target = null) {
        var pointer = target is null ? string.Empty : $",\"target\":\"{target}\"";
        var body = $$"""{"error":{"code":"{{code}}","message":"{{message}}"{{pointer}}""" + "}}";

        return Json(status, body);
    }
}
