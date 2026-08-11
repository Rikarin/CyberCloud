using System.Security.Cryptography;

namespace CyberCloud.Sdk;

/// <summary>What the user has to be told to finish a device-code sign-in.</summary>
/// <remarks>
///     ⚠ <b>The SDK produces this; it does not display it.</b> docs/plan/21 § `cyc` owns the terminal
///     and the SDK has no idea whether one exists — a portal back end, a desktop app and a test all
///     use this credential and none of them wants a <c>Console.WriteLine</c>. Rendering is the
///     caller's, through <see cref="DeviceCodePromptCallback" />.
/// </remarks>
public sealed class DeviceCodeInfo {
    internal DeviceCodeInfo(string userCode, Uri verificationUri, Uri? verificationUriComplete, DateTimeOffset expiresOn) {
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        ExpiresOn = expiresOn;
    }

    /// <summary>The short code the user types.</summary>
    public string UserCode { get; }

    /// <summary>Where the user types it.</summary>
    public Uri VerificationUri { get; }

    /// <summary>The same URL with the code already in it, when the server offers one.</summary>
    public Uri? VerificationUriComplete { get; }

    /// <summary>When the code stops working.</summary>
    public DateTimeOffset ExpiresOn { get; }
}

/// <summary>Shows the user their device code. Called once, before polling begins.</summary>
/// <param name="info">What to show.</param>
/// <param name="cancellationToken">The token.</param>
public delegate Task DeviceCodePromptCallback(DeviceCodeInfo info, CancellationToken cancellationToken);

/// <summary>Opens the user's browser at an authorization URL.</summary>
/// <param name="authorizationUri">The URL.</param>
/// <param name="cancellationToken">The token.</param>
public delegate Task OpenBrowserCallback(Uri authorizationUri, CancellationToken cancellationToken);

/// <summary>
///     Device Authorization — docs/plan/11 § Protocol's Device Authorization row, <i>"<c>cyc
///     login</c> on a headless box"</i>.
/// </summary>
/// <remarks>
///     ⚠ The refresh token this produces is written to <see cref="CyberCloudCredentialOptions.TokenCache" />,
///     so the next process finds a sign-in rather than prompting again. That is also what
///     <see cref="CyberCloudCliCredential" /> reads, which is why both halves live here — see
///     <see cref="ITokenCache" />'s remarks.
/// </remarks>
public sealed class DeviceCodeCredential : TokenEndpointCredential {
    readonly string clientId;
    readonly DeviceCodePromptCallback prompt;
    readonly string cacheKey;

    /// <summary>Creates the credential.</summary>
    /// <param name="clientId">The application's client id.</param>
    /// <param name="prompt">
    ///     Shows the user their code. ⚠ Required, and required on purpose: a credential with a default
    ///     that printed to the console would be a credential that writes to stdout inside a web
    ///     request.
    /// </param>
    /// <param name="options">The options.</param>
    public DeviceCodeCredential(string clientId, DeviceCodePromptCallback prompt, CyberCloudCredentialOptions? options = null)
        : base(options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(prompt);

        this.clientId = clientId;
        this.prompt = prompt;
        cacheKey = TokenCache.KeyFor(Options.AuthorityHost, clientId, Options.TenantId);
    }

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        var cached = await Options.TokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached is { Poisoned: false, RefreshToken.Length: > 0 }) {
            try {
                return await RefreshTokenExchange
                    .RedeemAsync(Identity, Options.TokenCache, cacheKey, cached, clientId, context, cancellationToken)
                    .ConfigureAwait(false);
            } catch (CredentialUnavailableException) {
                // The chain is gone or unsafe to use. Falling through to a fresh sign-in is the whole
                // point of having asked; RefreshTokenExchange has already cleaned up the cache entry.
            }
        }

        var form = new List<KeyValuePair<string, string>> { new("client_id", clientId) };
        AddScopes(form, context);

        var authorization = await Identity.RequestDeviceCodeAsync(form, cancellationToken).ConfigureAwait(false);

        var info = new DeviceCodeInfo(
            authorization.UserCode,
            new Uri(authorization.VerificationUri),
            Uri.TryCreate(authorization.VerificationUriComplete, UriKind.Absolute, out var complete) ? complete : null,
            DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn));

        await prompt(info, cancellationToken).ConfigureAwait(false);

        return await PollAsync(authorization, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits between device-code polls. Substituted by the tests so that a flow which legitimately
    ///     takes eight seconds of RFC 8628 back-off costs none of it.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    async ValueTask<AccessToken> PollAsync(DeviceAuthorizationPayload authorization, TokenRequestContext context, CancellationToken cancellationToken) {
        // RFC 8628 § 3.5: start at the server's interval, and add five seconds every time it says
        // slow_down. A client that ignores that is a client the server starts refusing.
        var interval = TimeSpan.FromSeconds(Math.Max(authorization.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn);

        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.DeviceCode),
            new("device_code", authorization.DeviceCode),
            new("client_id", clientId),
        };

        AddScopes(form, context);

        while (DateTimeOffset.UtcNow < deadline) {
            await Delay(interval, cancellationToken).ConfigureAwait(false);

            TokenPayload payload;

            try {
                payload = await Identity.RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
            } catch (AuthenticationFailedException e) when (string.Equals(e.ErrorCode, "authorization_pending", StringComparison.Ordinal)) {
                continue;
            } catch (AuthenticationFailedException e) when (string.Equals(e.ErrorCode, "slow_down", StringComparison.Ordinal)) {
                interval += TimeSpan.FromSeconds(5);

                continue;
            }

            await StoreAsync(payload, cancellationToken).ConfigureAwait(false);

            return ToAccessToken(payload);
        }

        throw new AuthenticationFailedException("The device code expired before the sign-in was completed.");
    }

    async ValueTask StoreAsync(TokenPayload payload, CancellationToken cancellationToken) {
        if (payload.RefreshToken is null)
            return;

        await Options.TokenCache
            .SetAsync(
                cacheKey,
                new TokenCacheRecord {
                    RefreshToken = payload.RefreshToken,
                    AccessToken = payload.AccessToken,
                    ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
                    Authority = Options.AuthorityHost.ToString(),
                    ClientId = clientId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
///     Authorization Code + PKCE against a loopback redirect — docs/plan/11 § Protocol's <i>"the only
///     interactive flow. No implicit, no hybrid"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The SDK catches the redirect; the caller opens the browser.</b> Splitting it there is
///         what keeps the protocol in one place — the listener, the <c>state</c> check and the PKCE
///         verifier are security-relevant and must not be reimplemented per host — while leaving "how
///         does a browser get opened on this machine" to the program that knows.
///     </para>
///     <para>
///         ⚠ <b>The loopback listener binds <c>127.0.0.1</c>, never <c>0.0.0.0</c>.</b> An
///         authorization code delivered to a port another machine can reach is an authorization code
///         another machine can steal, and PKCE is the second line of defence rather than the first.
///     </para>
///     <para>
///         ⚠ <b><c>state</c> is compared before the code is read.</b> A redirect that arrives with the
///         wrong <c>state</c> is a cross-site request forgery attempt against the sign-in itself, and
///         the code that came with it is not redeemed.
///     </para>
/// </remarks>
public sealed class InteractiveBrowserCredential : TokenEndpointCredential {
    readonly string clientId;
    readonly OpenBrowserCallback openBrowser;
    readonly string cacheKey;

    /// <summary>Creates the credential.</summary>
    /// <param name="clientId">The application's client id.</param>
    /// <param name="openBrowser">
    ///     Opens the browser. ⚠ Required. The SDK does not decide that launching a browser is
    ///     appropriate on the machine it happens to be running on.
    /// </param>
    /// <param name="options">The options.</param>
    public InteractiveBrowserCredential(string clientId, OpenBrowserCallback openBrowser, CyberCloudCredentialOptions? options = null)
        : base(options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(openBrowser);

        this.clientId = clientId;
        this.openBrowser = openBrowser;
        cacheKey = TokenCache.KeyFor(Options.AuthorityHost, clientId, Options.TenantId);
    }

    /// <summary>
    ///     Catches the redirect. Substituted by the tests so the flow is exercised without a browser
    ///     or a socket.
    /// </summary>
    internal IAuthorizationCodeListener Listener { get; set; } = new LoopbackListener();

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        var cached = await Options.TokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached is { Poisoned: false, RefreshToken.Length: > 0 }) {
            try {
                return await RefreshTokenExchange
                    .RedeemAsync(Identity, Options.TokenCache, cacheKey, cached, clientId, context, cancellationToken)
                    .ConfigureAwait(false);
            } catch (CredentialUnavailableException) {
                // Fall through to a fresh sign-in.
            }
        }

        var document = await Identity.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(document.AuthorizationEndpoint))
            throw new CredentialUnavailableException(
                $"{Options.AuthorityHost} advertises no authorization_endpoint, so the interactive flow is not available here.");

        var pkce = Pkce.Create();
        var state = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));

        await using var listener = await Listener.StartAsync(cancellationToken).ConfigureAwait(false);

        var authorizationUri = BuildAuthorizationUri(document.AuthorizationEndpoint, listener.RedirectUri, pkce, state, context);

        await openBrowser(authorizationUri, cancellationToken).ConfigureAwait(false);

        var code = await listener.WaitForCodeAsync(state, cancellationToken).ConfigureAwait(false);

        var form = new List<KeyValuePair<string, string>> {
            new("grant_type", OAuthGrants.AuthorizationCode),
            new("code", code),
            new("client_id", clientId),
            new("redirect_uri", listener.RedirectUri.ToString()),
            new("code_verifier", pkce.Verifier),
        };

        var payload = await Identity.RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);

        if (payload.RefreshToken is not null) {
            await Options.TokenCache
                .SetAsync(
                    cacheKey,
                    new TokenCacheRecord {
                        RefreshToken = payload.RefreshToken,
                        AccessToken = payload.AccessToken,
                        ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
                        Authority = Options.AuthorityHost.ToString(),
                        ClientId = clientId,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return ToAccessToken(payload);
    }

    Uri BuildAuthorizationUri(string endpoint, Uri redirectUri, Pkce pkce, string state, TokenRequestContext context) {
        var query = new StringBuilder(endpoint);
        query.Append(endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?');

        Append(query, "client_id", clientId);
        Append(query, "response_type", "code");
        Append(query, "redirect_uri", redirectUri.ToString());
        Append(query, "code_challenge", pkce.Challenge);
        Append(query, "code_challenge_method", Pkce.Method);
        Append(query, "state", state);

        if (context.Scopes is { Length: > 0 })
            Append(query, "scope", string.Join(' ', context.Scopes));

        if ((context.TenantId ?? Options.TenantId) is { } tenant)
            Append(query, "tenant_id", tenant);

        return new Uri(query.ToString().TrimEnd('&'));

        static void Append(StringBuilder builder, string name, string value)
            => builder.Append(name).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
    }
}

/// <summary>
///     A query string, split into its pairs.
/// </summary>
/// <remarks>
///     ⚠ Hand-rolled rather than <c>System.Web.HttpUtility.ParseQueryString</c>: that type returns a
///     <c>NameValueCollection</c>, which is a reflection-heavy shape the trim analyser has opinions
///     about, and this SDK's .csproj sets <c>IsAotCompatible</c>. Eight lines is cheaper than an
///     unsuppressible IL2026 on the sign-in path.
/// </remarks>
static class QueryString {
    public static Dictionary<string, string> Parse(Uri? uri) => Parse(uri?.Query);

    public static Dictionary<string, string> Parse(string? query) {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(query))
            return pairs;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var separator = part.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
                pairs[Uri.UnescapeDataString(part)] = string.Empty;
            else
                pairs[Uri.UnescapeDataString(part[..separator])] = Uri.UnescapeDataString(part[(separator + 1)..]);
        }

        return pairs;
    }
}

/// <summary>Catches an authorization code redirect. One implementation for real use, one for tests.</summary>
interface IAuthorizationCodeListener {
    ValueTask<IAuthorizationCodeSession> StartAsync(CancellationToken cancellationToken);
}

/// <summary>One live redirect target.</summary>
interface IAuthorizationCodeSession : IAsyncDisposable {
    /// <summary>The redirect URI to put in the authorization request and in the code redemption.</summary>
    Uri RedirectUri { get; }

    /// <summary>Waits for the redirect and returns the code.</summary>
    /// <param name="expectedState">The <c>state</c> that must come back.</param>
    /// <param name="cancellationToken">The token.</param>
    ValueTask<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken);
}

sealed class LoopbackListener : IAuthorizationCodeListener {
    public ValueTask<IAuthorizationCodeSession> StartAsync(CancellationToken cancellationToken) {
        // Port 0 lets the OS pick a free one, which is what makes two sign-ins on the same machine at
        // the same time work. The application registration must therefore allow the loopback address
        // with any port, which RFC 8252 § 7.3 requires of a native-app registration for this reason.
        var listener = new HttpListener();
        var port = FreePort();

        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        return ValueTask.FromResult<IAuthorizationCodeSession>(new Session(listener, new Uri($"http://127.0.0.1:{port}/")));
    }

    static int FreePort() {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    sealed class Session : IAuthorizationCodeSession {
        readonly HttpListener listener;

        public Session(HttpListener listener, Uri redirectUri) {
            this.listener = listener;
            RedirectUri = redirectUri;
        }

        public Uri RedirectUri { get; }

        public async ValueTask<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken) {
            var contextTask = listener.GetContextAsync();
            using var registration = cancellationToken.Register(listener.Stop);

            HttpListenerContext context;

            try {
                context = await contextTask.ConfigureAwait(false);
            } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) {
                cancellationToken.ThrowIfCancellationRequested();

                throw new AuthenticationFailedException("The sign-in redirect was not received.", e);
            }

            var query = QueryString.Parse(context.Request.Url?.Query);

            await RespondAsync(context, "You can close this window and return to your terminal.").ConfigureAwait(false);

            if (query.GetValueOrDefault("error") is { Length: > 0 } error)
                throw new AuthenticationFailedException($"The sign-in was refused: {error}.") { ErrorCode = error };

            // ⚠ State first. A redirect with the wrong state does not get its code read at all.
            if (!string.Equals(query.GetValueOrDefault("state"), expectedState, StringComparison.Ordinal))
                throw new AuthenticationFailedException(
                    "The sign-in redirect carried the wrong 'state' and was discarded. This is what a cross-site "
                    + "request forgery against the sign-in would look like.");

            return query.GetValueOrDefault("code") is { Length: > 0 } code
                ? code
                : throw new AuthenticationFailedException("The sign-in redirect carried no authorization code.");
        }

        static async Task RespondAsync(HttpListenerContext context, string message) {
            var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><p>{message}");

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }

        public ValueTask DisposeAsync() {
            listener.Close();

            return ValueTask.CompletedTask;
        }
    }
}
