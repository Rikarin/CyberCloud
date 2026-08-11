using System.Diagnostics;

namespace CyberCloud.Sdk;

/// <summary>
///     Puts <c>x-ms-correlation-request-id</c> on every outgoing request — docs/plan/10 § Request
///     pipeline, stage 1.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This handler sits outside the retry handler, and the placement is the whole
///         feature.</b> A correlation id identifies one logical operation; minted per attempt, the
///         three lines a 429-then-429-then-200 writes into the gateway's log would look like three
///         unrelated callers, which is the exact question the header exists to answer. The chain is
///         built once, in <see cref="CyberCloudPipeline" />, and the order there is load-bearing
///         rather than incidental.
///     </para>
///     <para>The id is chosen in three steps, most specific first:</para>
///     <list type="number">
///         <item>
///             A value the caller already set on the request wins — that is how a caller with its own
///             correlation scheme joins ours rather than being overwritten by it.
///         </item>
///         <item>
///             Otherwise the ambient <see cref="Activity" />'s W3C trace id. docs/plan/10 § Request
///             pipeline wants the id "on every log line and every span"; reusing the trace id makes
///             the join between the caller's traces and the platform's free.
///         </item>
///         <item>Otherwise a fresh GUID.</item>
///     </list>
/// </remarks>
public sealed class CorrelationRequestIdHandler : DelegatingHandler {
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.Contains(CyberCloudHeaders.CorrelationRequestId))
            request.Headers.TryAddWithoutValidation(CyberCloudHeaders.CorrelationRequestId, NextId());

        return base.SendAsync(request, cancellationToken);
    }

    static string NextId() {
        var activity = Activity.Current;

        return activity is { IdFormat: ActivityIdFormat.W3C }
            ? activity.TraceId.ToHexString()
            : Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     Appends <c>?api-version=</c> to every request that does not already carry one — docs/plan/10
///     § API versioning: <i>"required, on every request. Missing → 400 naming the current
///     version."</i>
/// </summary>
/// <remarks>
///     ⚠ <b>A handler rather than a line in every generated method, and the reason is the poll
///     URL.</b> A long-running operation's next request comes from the <c>Azure-AsyncOperation</c>
///     header — an absolute URL the SDK did not build and no emitter wrote. Putting the rule here
///     means that request carries a version too. It checks before it appends because two
///     <c>api-version</c> parameters is a <c>400</c>, and it would be a <c>400</c> that only ever
///     appeared on the second request of an LRO.
/// </remarks>
public sealed class ApiVersionHandler : DelegatingHandler {
    /// <summary>The query parameter's name. docs/plan/10 § API versioning explains why it is a query parameter.</summary>
    public const string QueryParameter = "api-version";

    readonly string apiVersion;

    /// <summary>Creates the handler for one api-version.</summary>
    /// <param name="apiVersion">The wire form — <c>2026-08-01</c>.</param>
    public ApiVersionHandler(string apiVersion) {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        this.apiVersion = apiVersion;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri;

        if (uri is not null && !HasApiVersion(uri.Query)) {
            var separator = uri.Query.Length > 0 ? '&' : '?';
            request.RequestUri = new Uri($"{uri.GetLeftPart(UriPartial.Path)}{uri.Query}{separator}{QueryParameter}={apiVersion}");
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Whether a query string already names the parameter. Matched on the whole parameter name
    ///     between a delimiter and <c>=</c>, so a hypothetical <c>?my-api-version=</c> is not mistaken
    ///     for it.
    /// </summary>
    internal static bool HasApiVersion(string query) {
        var index = query.IndexOf(QueryParameter, StringComparison.OrdinalIgnoreCase);

        while (index >= 0) {
            var startsParameter = index == 0 || query[index - 1] is '?' or '&';
            var end = index + QueryParameter.Length;

            if (startsParameter && end < query.Length && query[end] == '=')
                return true;

            index = query.IndexOf(QueryParameter, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>
///     Attaches the bearer token, fetching a new one whenever the cached one is spent.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This handler sits <i>inside</i> the retry handler, and that is what makes
///         docs/plan/11 § Protocol's 10-minute tokens survive a nine-minute operation.</b> Every
///         attempt runs it again, so an attempt that begins after the cached token expired asks the
///         credential for a new one rather than replaying a dead <c>Authorization</c> header. A
///         <c>401</c> on the eleventh minute of a poll is the failure this ordering exists to
///         prevent, and <c>TokenRefreshedMidPollTests</c> is the test that says so.
///     </para>
///     <para>
///         The cache lives here rather than in each credential because the refresh decision is a
///         property of the pipeline, not of how the token was obtained.
///         <see cref="AccessToken.RefreshAfter" /> is honoured when the credential sets it, and
///         otherwise a token is refreshed once it is inside
///         <see cref="AccessTokenCache.RefreshWindow" /> of expiry — early enough that no request
///         ever waits on a token request it could have made sooner.
///     </para>
///     <para>
///         ⚠ <b>The token never appears anywhere but the <c>Authorization</c> header.</b> It is not
///         logged, not put in an exception message, and not written into a diagnostic scope —
///         <c>CredentialsNeverLeakTests</c> asserts that over the whole surface.
///     </para>
/// </remarks>
public sealed class BearerTokenHandler : DelegatingHandler {
    readonly AccessTokenCache cache;
    readonly TokenRequestContext context;

    /// <summary>Creates the handler.</summary>
    /// <param name="credential">The credential to ask for tokens.</param>
    /// <param name="scopes">The scopes to request.</param>
    public BearerTokenHandler(TokenCredential credential, IEnumerable<string> scopes) {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(scopes);

        cache = new AccessTokenCache(credential);
        context = new TokenRequestContext([.. scopes]);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        var token = await cache.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
