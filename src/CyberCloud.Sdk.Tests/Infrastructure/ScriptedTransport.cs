using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace CyberCloud.Sdk.Tests;

/// <summary>
///     The HTTP seam every test drives the SDK through: a scripted
///     <see cref="HttpMessageHandler" /> that records what it was asked for and answers from a queue
///     or a function.
/// </summary>
/// <remarks>
///     ⚠ docs/plan/10's gateway does not exist. This is the whole of the test suite's contact with a
///     server, and it is the same injection point a caller uses to point the SDK at a private
///     deployment — so the tests exercise a supported configuration rather than a back door.
/// </remarks>
public sealed class ScriptedTransport : HttpMessageHandler {
    readonly ConcurrentQueue<HttpResponseMessage> queued = new();
    readonly Func<HttpRequestMessage, int, HttpResponseMessage>? script;
    readonly ConcurrentQueue<RecordedRequest> recorded = new();

    int count;

    public ScriptedTransport() { }

    /// <summary>Answers from a function of the request and the zero-based request number.</summary>
    public ScriptedTransport(Func<HttpRequestMessage, int, HttpResponseMessage> script) => this.script = script;

    /// <summary>Every request the SDK made, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. recorded];

    /// <summary>How many requests the SDK made.</summary>
    public int RequestCount => Volatile.Read(ref count);

    /// <summary>Queues one response, consumed in order.</summary>
    public ScriptedTransport Enqueue(HttpResponseMessage response) {
        queued.Enqueue(response);

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var index = Interlocked.Increment(ref count) - 1;

        recorded.Enqueue(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase),
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

        if (script is not null)
            return script(request, index);

        if (queued.TryDequeue(out var response))
            return response;

        throw new InvalidOperationException($"The transport script ran out after {index} request(s): {request.Method} {request.RequestUri}.");
    }

    /// <summary>What the SDK sent.</summary>
    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body) {
        public string? Header(string name) => Headers.GetValueOrDefault(name);

        public string? Authorization => Header("Authorization");

        public string? CorrelationId => Header(CyberCloudHeaders.CorrelationRequestId);
    }
}

/// <summary>Builds the responses a test scripts.</summary>
public static class Responses {
    public static HttpResponseMessage Json(HttpStatusCode status, string body) {
        var response = new HttpResponseMessage(status) {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.RequestId, "req-" + Guid.NewGuid().ToString("N")[..8]);

        return response;
    }

    /// <summary>The <c>202</c> of docs/plan/10 § Long-running operations, over HTTP.</summary>
    public static HttpResponseMessage Accepted(string operationUri, int retryAfterSeconds = 1) {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);

        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.AsyncOperation, operationUri);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        response.Headers.TryAddWithoutValidation(CyberCloudHeaders.RequestId, "req-accepted");
        response.Content = new StringContent(string.Empty);

        return response;
    }

    public static HttpResponseMessage TooManyRequests(int retryAfterSeconds) {
        var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests","message":"Slow down."}}""");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));

        return response;
    }

    /// <summary>An <c>OperationStatus</c> body — openapi/2026-08-01.json § OperationStatus.</summary>
    public static HttpResponseMessage Operation(string state, IEnumerable<(string Step, string Message, int Percent)>? progress = null, string? error = null) {
        var entries = string.Join(",", (progress ?? []).Select(x =>
            $$"""{"at":"2026-08-11T10:00:00Z","step":"{{x.Step}}","message":"{{x.Message}}","percentComplete":{{x.Percent}}}"""));

        var body = $$"""
            {"status":"{{state}}","progress":[{{entries}}]{{(error is null ? "" : $",\"error\":{error}")}}}
            """;

        return Json(HttpStatusCode.OK, body);
    }
}
