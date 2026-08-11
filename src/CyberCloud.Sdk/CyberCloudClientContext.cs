using System.Net.Http.Headers;

namespace CyberCloud.Sdk;

/// <summary>
///     Everything a generated client needs and nothing it should have to build for itself: the
///     endpoint, the api-version, the pipeline and the polling interval.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the emitter's whole view of the hand-written half.</b> A generated
///         <c>{Type}Collection</c> or <c>{Type}Resource</c> takes exactly one of these and never
///         constructs a pipeline, never reads <see cref="CyberCloudClientOptions" /> and never spells
///         the api-version — which is what makes <i>"everything else is regenerated per release and
///         never edited"</i> (docs/plan/21 § Generation) survive a change to the retry policy or the
///         correlation header.
///     </para>
///     <para>
///         Created once by <see cref="CyberCloudClient" /> and handed down the resource tree unchanged.
///         Immutable and safe to share across threads, as the pipeline it wraps is.
///     </para>
/// </remarks>
public sealed class CyberCloudClientContext {
    internal CyberCloudClientContext(Uri endpoint, CyberCloudPipeline pipeline, CyberCloudClientOptions options) {
        Endpoint = endpoint;
        Pipeline = pipeline;
        ApiVersion = options.ApiVersion;
        PollingInterval = options.PollingInterval;
    }

    /// <summary>The service endpoint every relative path is resolved against.</summary>
    public Uri Endpoint { get; }

    /// <summary>
    ///     The api-version on the wire — <c>2026-08-01</c>. ⚠ A generated client does not need to send
    ///     it: <see cref="ApiVersionHandler" /> is in the pipeline and puts it on every request,
    ///     including the operation poll no emitter wrote. It is exposed because a hand-written
    ///     convenience method sometimes has to branch on it.
    /// </summary>
    public string ApiVersion { get; }

    /// <summary>The authenticated, retrying, correlated pipeline.</summary>
    public CyberCloudPipeline Pipeline { get; }

    /// <summary>
    ///     The long-running-operation polling interval override, or <see langword="null" /> to honour
    ///     the service's <c>Retry-After</c>.
    /// </summary>
    public TimeSpan? PollingInterval { get; }

    /// <summary>Starts a request against a path relative to <see cref="Endpoint" />.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">
    ///     The already-escaped path — <c>/tenants/{t}/subscriptions/{s}/…</c>. The emitter escapes each
    ///     segment as it interpolates it; this method does not, because a path built from a resource id
    ///     already contains the <c>/</c> separators escaping would destroy.
    /// </param>
    public HttpRequestMessage CreateRequest(HttpMethod method, string path) {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    /// <summary>Starts a request for an absolute URL — the <c>Azure-AsyncOperation</c> poll target, or a <c>nextLink</c>.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="uri">The absolute URL, as the service sent it.</param>
    public HttpRequestMessage CreateRequest(HttpMethod method, Uri uri) {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    /// <summary>Attaches a JSON body.</summary>
    /// <param name="request">The request.</param>
    /// <param name="body">The already-serialised body.</param>
    public static void SetJsonBody(HttpRequestMessage request, ReadOnlyMemory<byte> body) {
        ArgumentNullException.ThrowIfNull(request);

        request.Content = new ByteArrayContent(body.ToArray()) {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
        };
    }

    /// <summary>
    ///     Turns a failed response into the exception to throw. The one place a generated client
    ///     produces an error, so that docs/plan/08 § Errors' mapping is written once.
    /// </summary>
    /// <param name="response">The response, whose body is parsed for the error shape.</param>
    public static CyberCloudRequestFailedException CreateFailure(Response response) => new(response);
}
