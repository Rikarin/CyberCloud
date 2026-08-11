namespace CyberCloud.Sdk;

/// <summary>
///     The entry point. docs/plan/21 § The .NET SDK:
///     <code>
///     var credential = new CyberCloudCliCredential();
///     var client = new CyberCloudClient(credential);
///     </code>
/// </summary>
/// <remarks>
///     <para>
///         The client owns one pipeline and is safe to share. Create it once per application, not once
///         per call: the pipeline holds the connection pool and the credential's token cache, and a
///         client per call throws both away every request — which is how a 10-minute token
///         (docs/plan/11 § Protocol) turns into a token request per API call.
///     </para>
///     <para>
///         ⚠ <b>The resource tree hangs off <see cref="Context" /> and is generated.</b>
///         <c>GetSubscriptionAsync</c>, <c>GetResourceGroups()</c> and every <c>{Type}Collection</c> in
///         docs/plan/21's worked example are emitted from the provider registry (ADR-012) into this
///         class's <c>partial</c> other half. This file is the half that is never regenerated; see
///         EmitterContract.cs for what the other half must contain.
///     </para>
/// </remarks>
public partial class CyberCloudClient : IDisposable {
    readonly CyberCloudPipeline pipeline;

    /// <summary>The public service endpoint — docs/plan/10 § Long-running operations, over HTTP.</summary>
    public static Uri DefaultEndpoint { get; } = new("https://api.cybercloud.io/");

    /// <summary>Creates a client against <see cref="DefaultEndpoint" />.</summary>
    /// <param name="credential">
    ///     The credential. <see cref="DefaultCyberCloudCredential" /> is the one to reach for unless you
    ///     know which single mechanism you want.
    /// </param>
    public CyberCloudClient(TokenCredential credential) : this(DefaultEndpoint, credential, options: null) { }

    /// <summary>Creates a client.</summary>
    /// <param name="endpoint">The service endpoint. A private or regional deployment passes its own.</param>
    /// <param name="credential">The credential.</param>
    /// <param name="options">Options, or <see langword="null" /> for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint" /> or <paramref name="credential" /> is null.</exception>
    public CyberCloudClient(Uri endpoint, TokenCredential credential, CyberCloudClientOptions? options = null) {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        options ??= new CyberCloudClientOptions();
        pipeline = new CyberCloudPipeline(credential, options);
        Context = new CyberCloudClientContext(endpoint, pipeline, options);
    }

    /// <summary>Constructor for mocking. Every generated client has one, by the same convention.</summary>
    protected CyberCloudClient() {
        pipeline = null!;
        Context = null!;
    }

    /// <summary>
    ///     The endpoint, api-version and pipeline this client was built with. Generated clients take
    ///     it; application code rarely needs it.
    /// </summary>
    public CyberCloudClientContext Context { get; }

    /// <summary>The service endpoint.</summary>
    public Uri Endpoint => Context.Endpoint;

    /// <inheritdoc />
    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the pipeline.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    protected virtual void Dispose(bool disposing) {
        if (disposing)
            pipeline?.Dispose();
    }
}
