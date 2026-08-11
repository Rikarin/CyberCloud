using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CyberCloud.Sdk.Tests;

/// <summary>The identity server, scripted. docs/plan/11 § Hosts' <c>CyberCloud.Identity.Host</c> does not exist yet.</summary>
public sealed class FakeIdentityServer : HttpMessageHandler {
    readonly List<(string Path, IReadOnlyDictionary<string, string> Form)> requests = [];

    public Uri Authority { get; } = new("https://login.cybercloud.test/");

    public Queue<HttpResponseMessage> TokenResponses { get; } = new();

    public Func<HttpRequestMessage, HttpResponseMessage>? DeviceResponse { get; set; }

    public Func<HttpResponseMessage>? KeysResponse { get; set; }

    public int DiscoveryReads { get; private set; }

    public IReadOnlyList<(string Path, IReadOnlyDictionary<string, string> Form)> Requests => requests;

    public IReadOnlyDictionary<string, string> LastForm => requests[^1].Form;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.AbsolutePath;

        var form = request.Content is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseForm(await request.Content.ReadAsStringAsync(cancellationToken));

        requests.Add((path, form));

        if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)) {
            DiscoveryReads++;

            return Responses.Json(
                HttpStatusCode.OK,
                """
                {"issuer":"https://login.cybercloud.test/",
                 "token_endpoint":"https://login.cybercloud.test/connect/token",
                 "authorization_endpoint":"https://login.cybercloud.test/connect/authorize",
                 "device_authorization_endpoint":"https://login.cybercloud.test/connect/device",
                 "jwks_uri":"https://login.cybercloud.test/.well-known/jwks"}
                """);
        }

        if (path.EndsWith("/jwks", StringComparison.Ordinal))
            return KeysResponse?.Invoke() ?? Responses.Json(HttpStatusCode.OK, """{"keys":[]}""");

        if (path.EndsWith("/connect/device", StringComparison.Ordinal))
            return DeviceResponse?.Invoke(request) ?? throw new InvalidOperationException("No device response scripted.");

        if (TokenResponses.Count > 0)
            return TokenResponses.Dequeue();

        throw new InvalidOperationException($"No token response scripted for {path}.");
    }

    static Dictionary<string, string> ParseForm(string body) {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            pairs[Uri.UnescapeDataString(part[..separator])] = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
        }

        return pairs;
    }

    public static HttpResponseMessage Token(string accessToken, int expiresIn = 600, string? refreshToken = null)
        => Responses.Json(
            HttpStatusCode.OK,
            $$"""
              {"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":{{expiresIn}}
              {{(refreshToken is null ? "" : $",\"refresh_token\":\"{refreshToken}\"")}}}
              """);

    public static HttpResponseMessage Error(HttpStatusCode status, string code)
        => Responses.Json(status, $$"""{"error":"{{code}}","error_description":"scripted"}""");

    public CyberCloudCredentialOptions Options(ITokenCache? cache = null) => new() {
        AuthorityHost = Authority,
        Transport = this,
        TokenCache = cache ?? TokenCache.CreateInMemory(),
    };
}

/// <summary>Discovery — EmitterContract.cs § 5's first bullet.</summary>
public sealed class DiscoveryTests {
    [Fact]
    public async Task Every_endpoint_comes_from_the_discovery_document_which_is_read_once() {
        using var server = new FakeIdentityServer();
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1"));
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t2"));

        using var credential = new ClientSecretCredential("tenant", "client", "secret", server.Options());

        await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);
        await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        server.DiscoveryReads.ShouldBe(1);
        server.Requests.Count(x => x.Path == "/connect/token").ShouldBe(2);
    }

    [Fact]
    public async Task A_discovery_document_with_no_token_endpoint_fails_with_a_message_that_names_the_problem() {
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, """{"issuer":"https://x/"}"""));

        using var credential = new ClientSecretCredential(
            "tenant",
            "client",
            "secret",
            new CyberCloudCredentialOptions { AuthorityHost = new Uri("https://login.cybercloud.test/"), Transport = transport });

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([]), Cancel.Token));

        thrown.Message.ShouldContain("token_endpoint");
    }
}

/// <summary>The machine grants — docs/plan/11 § Protocol's table.</summary>
public sealed class MachineGrantTests {
    [Fact]
    public async Task Client_credentials_sends_the_grant_the_client_id_and_the_tenant() {
        using var server = new FakeIdentityServer();
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1", expiresIn: 600));

        using var credential = new ClientSecretCredential("tenant-1", "client-1", "secret", server.Options());

        var token = await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        token.Token.ShouldBe("t1");

        // docs/plan/11 § Protocol makes a token ten minutes long; the SDK reads that off expires_in
        // rather than parsing the token, so the expiry must land about ten minutes out.
        (token.ExpiresOn - DateTimeOffset.UtcNow).ShouldBeGreaterThan(TimeSpan.FromMinutes(9));

        server.LastForm["grant_type"].ShouldBe(OAuthGrants.ClientCredentials);
        server.LastForm["client_id"].ShouldBe("client-1");
        server.LastForm["client_secret"].ShouldBe("secret");
        server.LastForm["tenant_id"].ShouldBe("tenant-1");
        server.LastForm["scope"].ShouldBe(CyberCloudScopes.Default);
    }

    /// <summary>
    ///     ⚠ The assertion is signed with the certificate's private key and verifiable with its public
    ///     one, and its <c>aud</c> is the <b>token endpoint</b> — RFC 7523 § 3, so a captured assertion
    ///     cannot be replayed against a different endpoint.
    /// </summary>
    [Fact]
    public async Task A_certificate_credential_sends_a_signed_assertion_and_never_the_key() {
        using var certificate = TestCertificates.CreateRsa();
        using var server = new FakeIdentityServer();
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1"));

        using var credential = new CertificateCredential("tenant-1", "client-1", certificate, server.Options());

        await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        server.LastForm["client_assertion_type"].ShouldBe(OAuthGrants.JwtBearerAssertion);

        var assertion = server.LastForm["client_assertion"];
        var parts = assertion.Split('.');
        parts.Length.ShouldBe(3);

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        header.RootElement.GetProperty("alg").GetString().ShouldBe("RS256");
        header.RootElement.TryGetProperty("x5t#S256", out _).ShouldBeTrue();

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().ShouldBe("client-1");
        payload.RootElement.GetProperty("sub").GetString().ShouldBe("client-1");
        payload.RootElement.GetProperty("aud").GetString().ShouldBe("https://login.cybercloud.test/connect/token");

        var lifetime = payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("nbf").GetInt64();
        lifetime.ShouldBe(120);

        using var publicKey = certificate.GetRSAPublicKey()!;

        publicKey.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)
            .ShouldBeTrue();

        // The private key is not on the wire in any form.
        string.Join("&", server.LastForm.Select(x => $"{x.Key}={x.Value}"))
            .ShouldNotContain(Convert.ToBase64String(certificate.GetRSAPrivateKey()!.ExportRSAPrivateKey())[..32]);
    }

    /// <summary>
    ///     docs/plan/11 § Managed identity, step 4. ⚠ The projected token is re-read from disk every
    ///     time: Kubernetes rotates it in place, and a credential that read it once starts failing an
    ///     hour into a pod's life.
    /// </summary>
    [Fact]
    public async Task Workload_identity_exchanges_the_projected_token_and_re_reads_it_each_time() {
        var path = Path.Combine(Path.GetTempPath(), $"cyc-sa-{Guid.NewGuid():N}");

        try {
            await File.WriteAllTextAsync(path, "sa-token-1", Cancel.Token);

            using var server = new FakeIdentityServer();
            server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1"));
            server.TokenResponses.Enqueue(FakeIdentityServer.Token("t2"));

            using var credential = new WorkloadIdentityCredential(path, "identity-1", "tenant-1", server.Options());

            await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

            server.LastForm["grant_type"].ShouldBe(OAuthGrants.TokenExchange);
            server.LastForm["subject_token"].ShouldBe("sa-token-1");
            server.LastForm["subject_token_type"].ShouldBe(OAuthGrants.JwtTokenType);
            server.LastForm["requested_token_type"].ShouldBe(OAuthGrants.AccessTokenType);

            await File.WriteAllTextAsync(path, "sa-token-2", Cancel.Token);
            await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

            server.LastForm["subject_token"].ShouldBe("sa-token-2");
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Workload_identity_is_unavailable_rather_than_broken_when_nothing_is_projected() {
        var thrown = Should.Throw<CredentialUnavailableException>(
            () => new WorkloadIdentityCredential(tokenFilePath: null, clientId: null, tenantId: null));

        thrown.Message.ShouldContain(WorkloadIdentityCredential.TokenFileVariable);

        await Task.CompletedTask;
    }

    static byte[] Base64UrlDecode(string text) {
        var padded = text.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

/// <summary>Device authorization — RFC 8628, docs/plan/11 § Protocol's <c>cyc login</c> row.</summary>
public sealed class DeviceCodeTests {
    static HttpResponseMessage Device(int interval = 0)
        => Responses.Json(
            HttpStatusCode.OK,
            $$"""
              {"device_code":"dev-1","user_code":"WDJB-MJHT","verification_uri":"https://login.cybercloud.test/device",
               "verification_uri_complete":"https://login.cybercloud.test/device?code=WDJB-MJHT","expires_in":300,"interval":{{interval}}}
              """);

    /// <summary>
    ///     ⚠ <b>The SDK never prints.</b> docs/plan/21 § `cyc` owns the terminal, and this credential
    ///     has no idea whether one exists — the callback is required and is the only way the code
    ///     reaches a human.
    /// </summary>
    [Fact]
    public async Task The_user_code_reaches_the_caller_through_the_callback_and_the_poll_honours_pending_and_slow_down() {
        using var server = new FakeIdentityServer { DeviceResponse = _ => Device() };

        server.TokenResponses.Enqueue(FakeIdentityServer.Error(HttpStatusCode.BadRequest, "authorization_pending"));
        server.TokenResponses.Enqueue(FakeIdentityServer.Error(HttpStatusCode.BadRequest, "slow_down"));
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1", refreshToken: "r1"));

        DeviceCodeInfo? shown = null;
        var cache = TokenCache.CreateInMemory();

        using var credential = new DeviceCodeCredential(
            "cyc",
            (info, cancellationToken) => {
                shown = info;

                return Task.CompletedTask;
            },
            server.Options(cache)) { Delay = (interval, cancellationToken) => Task.CompletedTask };

        var token = await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        token.Token.ShouldBe("t1");

        shown.ShouldNotBeNull();
        shown.UserCode.ShouldBe("WDJB-MJHT");
        shown.VerificationUriComplete!.ToString().ShouldContain("WDJB-MJHT");

        server.Requests.Count(x => x.Path == "/connect/token").ShouldBe(3);

        // The refresh token is kept so the next process finds a sign-in — see ITokenCache's remarks on
        // why the SDK owns both halves.
        var stored = await cache.GetAsync(TokenCache.KeyFor(server.Authority, "cyc", null), Cancel.Token);
        stored!.RefreshToken.ShouldBe("r1");
    }
}

/// <summary>
///     Refresh with rotation and reuse detection — docs/plan/11 § Protocol and § Sessions and
///     revocation.
/// </summary>
public sealed class RefreshTokenTests {
    static async ValueTask<(FakeIdentityServer Server, ITokenCache Cache, string Key)> SignedIn(string refreshToken) {
        var server = new FakeIdentityServer();
        var cache = TokenCache.CreateInMemory();
        var key = TokenCache.KeyFor(server.Authority, "cyc", null);

        await cache.SetAsync(
            key,
            new TokenCacheRecord { RefreshToken = refreshToken, Authority = server.Authority.ToString(), ClientId = "cyc" },
            CancellationToken.None);

        return (server, cache, key);
    }

    static DeviceCodeCredential Credential(FakeIdentityServer server, ITokenCache cache)
        => new("cyc", (info, cancellationToken) => Task.CompletedTask, server.Options(cache));

    [Fact]
    public async Task A_cached_refresh_token_is_redeemed_and_the_rotated_one_is_stored() {
        var (server, cache, key) = await SignedIn("r1");
        using var _ = server;

        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t2", refreshToken: "r2"));

        using var credential = Credential(server, cache);

        var token = await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        token.Token.ShouldBe("t2");
        server.LastForm["grant_type"].ShouldBe(OAuthGrants.RefreshToken);
        server.LastForm["refresh_token"].ShouldBe("r1");

        // ⚠ The rotated token is stored before the access token is handed back. A caller holding a
        // usable token over a cache that still names the spent one is the same ambiguity a dropped
        // packet produces, arrived at through carelessness.
        (await cache.GetAsync(key, Cancel.Token))!.RefreshToken.ShouldBe("r2");
    }

    /// <summary>
    ///     ⚠ <b>The reuse-detection hazard, handled.</b> The request left and no answer came back, so
    ///     the server may or may not have rotated the token. Retrying it is exactly what docs/plan/11
    ///     § Protocol's reuse detection revokes the whole chain for. The entry is poisoned instead and
    ///     the user signs in once.
    /// </summary>
    [Fact]
    public async Task An_answerless_failure_poisons_the_entry_rather_than_retrying_it() {
        var (server, cache, key) = await SignedIn("r1");
        using var _ = server;

        // A 500 with an HTML body — no OAuth error code, so the outcome is unknown.
        server.TokenResponses.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("<html>gateway exploded</html>"),
        });

        // The device flow it falls back to has nothing scripted, so the fall-through fails loudly and
        // the test can assert on the cache rather than on a second sign-in.
        server.DeviceResponse = _ => throw new InvalidOperationException("fell through to a fresh sign-in");

        using var credential = Credential(server, cache);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        var stored = await cache.GetAsync(key, Cancel.Token);

        stored!.Poisoned.ShouldBeTrue();
        stored.RefreshToken.ShouldBe("r1");

        // Exactly one attempt. Not two, not three.
        server.Requests.Count(x => x.Path == "/connect/token").ShouldBe(1);
    }

    [Fact]
    public async Task A_poisoned_entry_is_never_redeemed_again() {
        var (server, cache, key) = await SignedIn("r1");
        using var _ = server;

        await cache.SetAsync(
            key,
            (await cache.GetAsync(key, Cancel.Token))! with { Poisoned = true },
            Cancel.Token);

        server.DeviceResponse = _ => throw new InvalidOperationException("fell through to a fresh sign-in");

        using var credential = Credential(server, cache);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        server.Requests.Count(x => x.Path == "/connect/token").ShouldBe(0);
    }

    /// <summary>An <c>invalid_grant</c> is an answer: the chain is finished, so the entry goes.</summary>
    [Fact]
    public async Task An_invalid_grant_clears_the_entry() {
        var (server, cache, key) = await SignedIn("r1");
        using var _ = server;

        server.TokenResponses.Enqueue(FakeIdentityServer.Error(HttpStatusCode.BadRequest, "invalid_grant"));
        server.DeviceResponse = _ => throw new InvalidOperationException("fell through to a fresh sign-in");

        using var credential = Credential(server, cache);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        (await cache.GetAsync(key, Cancel.Token)).ShouldBeNull();
    }
}

/// <summary>Authorization code + PKCE — docs/plan/11 § Protocol's only interactive flow.</summary>
public sealed class InteractiveFlowTests {
    sealed class FakeListener : IAuthorizationCodeListener, IAuthorizationCodeSession {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:54321/");

        public string? StateToReturn { get; set; }

        public string Code { get; set; } = "auth-code-1";

        public ValueTask<IAuthorizationCodeSession> StartAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IAuthorizationCodeSession>(this);

        public ValueTask<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken) {
            var actual = StateToReturn ?? expectedState;

            return string.Equals(actual, expectedState, StringComparison.Ordinal)
                ? ValueTask.FromResult(Code)
                : throw new AuthenticationFailedException("The sign-in redirect carried the wrong 'state' and was discarded.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_challenge_is_the_sha256_of_the_verifier_and_only_S256_is_offered() {
        using var server = new FakeIdentityServer();
        server.TokenResponses.Enqueue(FakeIdentityServer.Token("t1", refreshToken: "r1"));

        Uri? opened = null;

        using var credential = new InteractiveBrowserCredential(
            "cyc",
            (uri, cancellationToken) => {
                opened = uri;

                return Task.CompletedTask;
            },
            server.Options()) { Listener = new FakeListener() };

        var token = await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        token.Token.ShouldBe("t1");

        opened.ShouldNotBeNull();

        var query = QueryString.Parse(opened);

        query["response_type"].ShouldBe("code");
        query["code_challenge_method"].ShouldBe("S256");
        query["redirect_uri"].ShouldBe("http://127.0.0.1:54321/");

        // ⚠ The verifier is sent only when the code is redeemed, and the challenge must be its SHA-256.
        // A "PKCE" implementation that sent the same value twice would be `plain` with a misleading
        // method name, and every assertion except this one would still pass.
        var verifier = server.LastForm["code_verifier"];
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        query["code_challenge"].ShouldBe(expected);
        server.LastForm["grant_type"].ShouldBe(OAuthGrants.AuthorizationCode);
        server.LastForm["code"].ShouldBe("auth-code-1");
    }

    /// <summary>
    ///     ⚠ A redirect with the wrong <c>state</c> is what a cross-site request forgery against the
    ///     sign-in looks like, and its code is never redeemed.
    /// </summary>
    [Fact]
    public async Task A_redirect_with_the_wrong_state_is_discarded_and_its_code_is_never_redeemed() {
        using var server = new FakeIdentityServer();

        using var credential = new InteractiveBrowserCredential(
            "cyc",
            (uri, cancellationToken) => Task.CompletedTask,
            server.Options()) { Listener = new FakeListener { StateToReturn = "not-the-state-we-sent" } };

        await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        server.Requests.Count(x => x.Path == "/connect/token").ShouldBe(0);
    }
}

/// <summary>
///     Signing keys — docs/plan/11 § Protocol: <i>"a rotating key set (30-day rotation, both keys
///     published for 60)"</i>.
/// </summary>
public sealed class SigningKeyCacheTests {
    static HttpResponseMessage Keys(params string[] keyIds)
        => Responses.Json(
            HttpStatusCode.OK,
            $$"""{"keys":[{{string.Join(",", keyIds.Select(x => $$"""{"kty":"RSA","kid":"{{x}}","n":"AQAB","e":"AQAB"}"""))}}]}""");

    /// <summary>
    ///     ⚠ <b>The failure an infinite cache produces is a client that worked for a month and then
    ///     stopped, everywhere at once, with no deploy to blame.</b> An unknown <c>kid</c> forces one
    ///     re-read.
    /// </summary>
    [Fact]
    public async Task An_unknown_key_id_forces_exactly_one_refetch() {
        var rotated = false;

        using var server = new FakeIdentityServer();
        server.KeysResponse = () => rotated ? Keys("key-1", "key-2") : Keys("key-1");

        using var identity = new IdentityClient(server.Authority, server);
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var cache = new SigningKeyCache(identity, time);

        (await cache.GetKeyAsync("key-1", Cancel.Token)).ShouldNotBeNull();
        cache.FetchCount.ShouldBe(1);

        rotated = true;

        // ⚠ Past the minimum interval. Inside it, an unknown kid deliberately does NOT refetch — see
        // the test below — so the clock has to move for the rotation to be visible.
        time.Advance(SigningKeyCache.MinimumRefreshInterval + TimeSpan.FromMinutes(1));

        (await cache.GetKeyAsync("key-2", Cancel.Token)).ShouldNotBeNull();
        cache.FetchCount.ShouldBe(2);
    }

    /// <summary>
    ///     ⚠ And no more than one. Unbounded, an unknown <c>kid</c> would be a JWKS fetch per
    ///     validation — a denial-of-service amplifier pointed at the identity server.
    /// </summary>
    [Fact]
    public async Task A_key_id_that_does_not_exist_does_not_refetch_on_every_call() {
        using var server = new FakeIdentityServer();
        server.KeysResponse = () => Keys("key-1");

        using var identity = new IdentityClient(server.Authority, server);
        var cache = new SigningKeyCache(identity);

        for (var i = 0; i < 10; i++)
            (await cache.GetKeyAsync("no-such-key", Cancel.Token)).ShouldBeNull();

        cache.FetchCount.ShouldBe(1);
    }
}

/// <summary>The token cache — docs/plan/21 § `cyc`'s token-cache row.</summary>
public sealed class TokenCacheTests {
    [Fact]
    public async Task An_in_memory_cache_round_trips_a_record() {
        var cache = TokenCache.CreateInMemory();
        var record = new TokenCacheRecord { RefreshToken = "r1", AccessToken = "t1", ExpiresOn = DateTimeOffset.UtcNow };

        await cache.SetAsync("key", record, Cancel.Token);
        (await cache.GetAsync("key", Cancel.Token))!.RefreshToken.ShouldBe("r1");

        await cache.RemoveAsync("key", Cancel.Token);
        (await cache.GetAsync("key", Cancel.Token)).ShouldBeNull();
    }

    /// <summary>Two authorities, two client ids or two tenants never read each other's refresh tokens.</summary>
    [Fact]
    public void The_key_separates_authority_client_and_tenant() {
        var a = TokenCache.KeyFor(new Uri("https://login.a/"), "client", "tenant");
        var b = TokenCache.KeyFor(new Uri("https://login.b/"), "client", "tenant");
        var c = TokenCache.KeyFor(new Uri("https://login.a/"), "other", "tenant");
        var d = TokenCache.KeyFor(new Uri("https://login.a/"), "client", null);

        new[] { a, b, c, d }.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
    }

    /// <summary>
    ///     ⚠ A record this SDK cannot read is treated as absent. Throwing would make a stale keychain
    ///     entry an unrecoverable sign-in failure that no error message could explain.
    /// </summary>
    [Fact]
    public void An_unreadable_record_is_absent_rather_than_fatal() {
        TokenCache.Deserialise("this is not json"u8).ShouldBeNull();
        TokenCache.Deserialise([]).ShouldBeNull();
    }

    /// <summary>
    ///     ⚠ docs/plan/21 § `cyc`: <i>"Never a plaintext file — that is how CI credentials leak into
    ///     container images."</i> When no keychain is reachable the answer is no persistence, not a
    ///     file.
    /// </summary>
    [Fact]
    public void The_persistent_cache_never_falls_back_to_a_file() {
        var cache = TokenCache.CreatePersistent();

        // Whatever this machine offers, it is a keychain or it is nothing.
        cache.ShouldNotBeNull();

        if (!cache.IsAvailable)
            cache.ShouldBeSameAs(TokenCache.None);
    }
}

/// <summary>The CLI credential — docs/plan/21 § The .NET SDK's first line.</summary>
public sealed class CliCredentialTests {
    [Fact]
    public async Task A_live_cached_token_is_used_without_launching_the_cli() {
        var cache = TokenCache.CreateInMemory();
        var options = new CyberCloudCredentialOptions { AuthorityHost = new Uri("https://login.cybercloud.test/"), TokenCache = cache };

        await cache.SetAsync(
            TokenCache.KeyFor(options.AuthorityHost, CyberCloudCliCredential.CliClientId, null),
            new TokenCacheRecord { AccessToken = "cached-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) },
            Cancel.Token);

        var credential = new CyberCloudCliCredential(options) {
            Run = (arguments, cancellationToken) => throw new InvalidOperationException("the CLI must not be launched"),
        };

        (await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token)).Token
            .ShouldBe("cached-token");
    }

    [Fact]
    public async Task An_empty_cache_falls_back_to_the_cli() {
        var options = new CyberCloudCredentialOptions { TokenCache = TokenCache.CreateInMemory() };
        IReadOnlyList<string>? seen = null;

        var credential = new CyberCloudCliCredential(options) {
            Run = (arguments, cancellationToken) => {
                seen = arguments;

                return ValueTask.FromResult(new Subprocess.Result(
                    0,
                    $$"""{"accessToken":"cli-token","expiresOn":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""",
                    string.Empty));
            },
        };

        (await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token)).Token
            .ShouldBe("cli-token");

        seen.ShouldNotBeNull();
        string.Join(" ", seen).ShouldContain("account get-access-token --output json");
    }

    /// <summary>docs/plan/21 § Decisions makes exit code 3 mean auth, so that is the one that earns the advice.</summary>
    [Fact]
    public async Task Exit_code_3_says_to_sign_in_and_anything_else_does_not() {
        var credential = new CyberCloudCliCredential(new CyberCloudCredentialOptions { TokenCache = TokenCache.CreateInMemory() }) {
            Run = (arguments, cancellationToken) => ValueTask.FromResult(new Subprocess.Result(3, string.Empty, string.Empty)),
        };

        var thrown = await Should.ThrowAsync<CredentialUnavailableException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([]), Cancel.Token));

        thrown.Message.ShouldContain("cyc login");
    }

    /// <summary>⚠ The CLI's stdout carries an access token, so it never appears in an exception.</summary>
    [Fact]
    public async Task A_malformed_cli_response_never_echoes_its_output() {
        var credential = new CyberCloudCliCredential(new CyberCloudCredentialOptions { TokenCache = TokenCache.CreateInMemory() }) {
            Run = (arguments, cancellationToken) =>
                ValueTask.FromResult(new Subprocess.Result(0, """{"accessToken":"leaked-token", oops""", string.Empty)),
        };

        var thrown = await Should.ThrowAsync<CredentialUnavailableException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([]), Cancel.Token));

        thrown.ToString().ShouldNotContain("leaked-token");
    }
}
