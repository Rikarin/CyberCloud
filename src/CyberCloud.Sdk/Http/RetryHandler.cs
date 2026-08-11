using Polly;
using Polly.Retry;

namespace CyberCloud.Sdk;

/// <summary>
///     Retries the requests that can succeed on a second attempt and no others — docs/plan/21 § The
///     .NET SDK's <i>"Retry, <c>Retry-After</c> on 429, correlation ids … Ours, over <c>Polly</c>
///     8.6.5, already in the register"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <c>4xx</c> that is not <c>429</c> is never retried, and that is a correctness rule
///         rather than a politeness one.</b> A <c>409</c> from docs/plan/06 § Two-phase create means
///         the name is taken; a <c>400</c> means the body failed the api-version's JSON Schema
///         (docs/plan/10 § Request pipeline, stage 7). Replaying either produces the same answer,
///         three times slower — and for a <c>409</c> raised after a partial write it invites the
///         caller to reason about a write that may or may not have landed.
///     </para>
///     <para>
///         ⚠ <b><c>429</c> waits exactly as long as the service asked.</b> docs/plan/10 § Rate
///         limiting sends <c>Retry-After</c> with every <c>429</c> <i>"because every cloud SDK's retry
///         policy already understands those headers"</i>. <see cref="RetryStrategyOptions{T}.DelayGenerator" />
///         is where that becomes true of this one: it reads the header and returns it, and Polly's
///         exponential backoff is used only when the service did not say.
///     </para>
///     <para>
///         ⚠ <b>Every attempt sends a fresh <see cref="HttpRequestMessage" />.</b>
///         <c>HttpClient</c> refuses to send the same message twice, so a retry that reused it would
///         throw <see cref="InvalidOperationException" /> on the first <c>503</c> — a failure that
///         only ever appears under load, which is the worst time to discover it. The clone is cheap
///         because the SDK's bodies are already buffered byte arrays.
///     </para>
/// </remarks>
public sealed class RetryHandler : DelegatingHandler {
    readonly ResiliencePipeline<HttpResponseMessage> pipeline;

    /// <summary>Creates the handler.</summary>
    /// <param name="options">The retry settings.</param>
    public RetryHandler(RetryOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ Polly refuses MaxRetryAttempts = 0 with a ValidationException rather than treating it as
        // "do not retry", so zero has to mean "no retry strategy at all". A caller who sets
        // Retry.MaxRetries = 0 means exactly that — a CI job that wants one attempt and a fast, honest
        // failure — and turning their configuration into a startup crash would be the wrong answer.
        if (options.MaxRetries <= 0) {
            pipeline = ResiliencePipeline<HttpResponseMessage>.Empty;

            return;
        }

        pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage> {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(static response => IsRetriable(response.StatusCode))
                    // A connection that never opened and a DNS failure are the retriable exceptions.
                    // ⚠ OperationCanceledException is NOT among them: retrying a cancelled request is
                    // how a cancelled WaitForCompletionAsync keeps polling after the caller left.
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>(),
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.Delay,
                MaxDelay = options.MaxDelay,
                DelayGenerator = static arguments => new ValueTask<TimeSpan?>(RetryAfter(arguments.Outcome.Result)),
            })
            .Build();
    }

    /// <summary>The statuses worth a second attempt.</summary>
    /// <param name="status">The status.</param>
    public static bool IsRetriable(HttpStatusCode status)
        => status switch {
            HttpStatusCode.RequestTimeout => true,       // 408
            HttpStatusCode.TooManyRequests => true,      // 429 — docs/plan/10 § Rate limiting
            HttpStatusCode.InternalServerError => true,  // 500
            HttpStatusCode.BadGateway => true,           // 502
            HttpStatusCode.ServiceUnavailable => true,   // 503
            HttpStatusCode.GatewayTimeout => true,       // 504
            // ⚠ 501 Not Implemented is deliberately absent: the route will not exist on the second
            // attempt either, and a client that hammers it turns a clear answer into a slow one.
            _ => false,
        };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        // The body is read once here rather than per attempt: HttpContent is also single-use, and a
        // second attempt would otherwise send an empty body — a PUT that silently became a PUT of
        // `null` on retry is the kind of bug that is found in production by a customer.
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var contentType = request.Content?.Headers.ContentType;

        return await pipeline.ExecuteAsync(
                async (state, token) => await base.SendAsync(Clone(state.request, state.body, state.contentType), token).ConfigureAwait(false),
                (request, body, contentType),
                cancellationToken)
            .ConfigureAwait(false);
    }

    static TimeSpan? RetryAfter(HttpResponseMessage? response) {
        var value = response?.Headers.RetryAfter;

        if (value is null)
            return null;

        if (value.Delta is { } delta)
            return delta;

        if (value.Date is { } date) {
            var wait = date - DateTimeOffset.UtcNow;

            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body, System.Net.Http.Headers.MediaTypeHeaderValue? contentType) {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (body is null)
            return clone;

        clone.Content = new ByteArrayContent(body);

        if (contentType is not null)
            clone.Content.Headers.ContentType = contentType;

        return clone;
    }
}

/// <summary>How hard the pipeline tries. The defaults are the ones a control-plane API wants.</summary>
public sealed class RetryOptions {
    /// <summary>
    ///     Attempts after the first. Three, because docs/plan/10 § Rate limiting's windows are five
    ///     minutes long and a client that retries a dozen times spends its own budget arguing.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>The first backoff, doubling with jitter. Ignored whenever the service sent <c>Retry-After</c>.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(0.8);

    /// <summary>The backoff ceiling.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(1);
}
