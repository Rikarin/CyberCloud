namespace CyberCloud.Sdk;

/// <summary>
///     The in-process access-token cache the pipeline reads on every attempt. One token, refreshed
///     before it dies, fetched by at most one caller at a time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the piece that makes docs/plan/11 § Protocol's ten-minute tokens invisible.</b>
///         Without it every request would ask the credential — a subprocess for the CLI credential, an
///         HTTP round trip for the rest — and a ten-minute token would cost more than a ten-hour one.
///         With it, a long <see cref="Operation{T}" /> poll crosses an expiry without noticing, which
///         is the failure class <c>TokenRefreshedMidPollTests</c> covers.
///     </para>
///     <para>
///         ⚠ <b>The refresh is single-flight, and that is not just an efficiency.</b> docs/plan/11
///         § Sessions and revocation rotates refresh tokens one-time-use <i>with reuse detection →
///         revoke the whole chain</i>. Two concurrent refreshes would spend the same refresh token
///         twice, the identity server would see a reuse, and the user would be signed out everywhere —
///         from nothing worse than two parallel API calls. So the gate is correctness, not a
///         micro-optimisation, and it is why the cache holds a <see cref="SemaphoreSlim" /> rather
///         than racing on <see cref="Interlocked" />.
///     </para>
///     <para>
///         ⚠ Never disposed, and that is safe: <see cref="SemaphoreSlim" /> allocates a kernel handle
///         only when <c>AvailableWaitHandle</c> is read, which nothing here does. Making it disposable
///         would push <c>IDisposable</c> onto every credential and every client that holds one.
///     </para>
/// </remarks>
sealed class AccessTokenCache {
    /// <summary>
    ///     How long before expiry a token is replaced when the credential expressed no preference.
    ///     ⚠ Two minutes of a ten-minute token: long enough that a request never waits on a refresh,
    ///     short enough that it is not a fifth of the token's life spent refreshing.
    /// </summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(2);

    readonly SemaphoreSlim gate = new(1, 1);
    readonly TokenCredential credential;
    readonly TimeProvider time;

    AccessToken current;
    bool hasToken;

    public AccessTokenCache(TokenCredential credential, TimeProvider? time = null) {
        this.credential = credential;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>How many times the credential was actually asked. Read by the tests that prove a refresh happened.</summary>
    public int FetchCount { get; private set; }

    public async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) {
        if (TryReadFresh(out var cached))
            return cached;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            // Re-check inside the gate: the caller we queued behind has probably just fetched the very
            // token we were about to ask for, and asking again would spend a one-time refresh token.
            if (TryReadFresh(out cached))
                return cached;

            var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);

            current = token;
            hasToken = true;
            FetchCount++;

            return token;
        } finally {
            gate.Release();
        }
    }

    bool TryReadFresh(out AccessToken token) {
        token = current;

        if (!hasToken)
            return false;

        var now = time.GetUtcNow();
        var replaceAt = token.RefreshAfter ?? token.ExpiresOn - RefreshWindow;

        return now < replaceAt && now < token.ExpiresOn;
    }
}
