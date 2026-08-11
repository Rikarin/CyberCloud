namespace CyberCloud.Sdk;

/// <summary>
///     The request pipeline: correlation, api-version, retry, authentication, transport — in that
///     order, and the order is the design.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/10 § Request pipeline says of the gateway's own stages that <i>"order matters and
///         each step is here for a named reason"</i>. The same is true of the client's, and the two
///         load-bearing placements are:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Correlation and api-version are outside the retry.</b> They are properties of the
///             logical call, so all three attempts of a 429-429-200 carry one id and one version.
///         </item>
///         <item>
///             <b>Authentication is inside the retry.</b> Each attempt re-reads the token cache, so an
///             attempt made after a 10-minute token expired (docs/plan/11 § Protocol) fetches a new
///             one instead of replaying a dead header.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The transport is an injection point, and that is how this SDK is tested against a
///         gateway that does not exist yet.</b> <see cref="CyberCloudClientOptions.Transport" /> takes
///         any <see cref="HttpMessageHandler" />; the tests put a scripted one there and assert on the
///         requests it receives. Nothing in the SDK's own test suite opens a socket.
///     </para>
/// </remarks>
public sealed class CyberCloudPipeline : IDisposable {
    readonly HttpClient client;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="credential">The credential the bearer handler asks for tokens.</param>
    /// <param name="options">The client options, which supply the retry settings and the transport.</param>
    public CyberCloudPipeline(TokenCredential credential, CyberCloudClientOptions options) {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(options);

        // Innermost first: each handler wraps the one built before it.
        HttpMessageHandler handler = options.Transport ?? CreateDefaultTransport();

        handler = new BearerTokenHandler(credential, options.Scopes) { InnerHandler = handler };
        handler = new RetryHandler(options.Retry) { InnerHandler = handler };
        handler = new ApiVersionHandler(options.ApiVersion) { InnerHandler = handler };
        handler = new CorrelationRequestIdHandler { InnerHandler = handler };

        // ⚠ HttpClient's own timeout is disabled and the per-request CancellationToken is the only
        // deadline. A default 100-second timeout applies to the *whole* handler chain including every
        // retry and every backoff, so a legitimate 429-with-Retry-After-60 followed by a slow attempt
        // would surface as a TaskCanceledException that looks like a caller cancellation. Cancellation
        // in this SDK means the caller asked; nothing else is allowed to produce it.
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Sends a request and buffers the response.</summary>
    /// <param name="request">
    ///     The request. ⚠ It is consumed: <see cref="RetryHandler" /> clones it per attempt, so the
    ///     instance passed here must not be reused by the caller either.
    /// </param>
    /// <param name="cancellationToken">The token. It is the only deadline the pipeline observes.</param>
    public async ValueTask<Response> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);

        using var message = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return await BufferedHttpResponse.CreateAsync(message, cancellationToken).ConfigureAwait(false);
    }

    static SocketsHttpHandler CreateDefaultTransport()
        => new SocketsHttpHandler {
            // The control plane is chatty in bursts and idle between them; recycling the connection
            // every two minutes is what keeps a client that lives for days from pinning itself to a
            // gateway pod that has since been drained.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // Redirects are not part of this API's contract (docs/plan/10 § Shape lists four routes and
            // no redirect), and following one silently would re-send the Authorization header to
            // whatever host the redirect named.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
