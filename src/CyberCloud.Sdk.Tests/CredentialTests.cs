namespace CyberCloud.Sdk.Tests;

/// <summary>
///     ⚠ <b>The failure class: a long <see cref="Operation{T}" /> poll must not die at minute
///     eleven.</b> docs/plan/11 § Protocol makes access tokens ten minutes long, and docs/plan/09's
///     cluster creation takes nine; the two numbers are close enough that a poll crossing an expiry is
///     the normal case rather than the edge case.
/// </summary>
public sealed class TokenRefreshedMidPollTests {
    [Fact]
    public async Task A_token_that_expires_during_a_poll_is_replaced_without_the_operation_failing() {
        // Token 1 expires 250 ms out; token 2 is good for an hour. The poll interval is 150 ms and the
        // operation takes five polls, so the expiry lands in the middle of the poll loop.
        var credential = new FakeCredential(call => call == 1
            ? new AccessToken("token-expiring", DateTimeOffset.UtcNow.AddMilliseconds(250))
            : new AccessToken("token-fresh", DateTimeOffset.UtcNow.AddHours(1)));

        var transport = new ScriptedTransport((request, index) => index switch {
            0 => Responses.Accepted(TestClient.OperationUri),
            < 5 => Responses.Operation("Running", [("etcd", "still going", 20)]),
            5 => Responses.Operation("Succeeded", [("etcd", "still going", 20), ("ready", "done", 100)]),
            _ => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody),
        });

        using var client = TestClient.Create(
            transport,
            credential,
            options => options.PollingInterval = TimeSpan.FromMilliseconds(150));

        var operation = await client.Widgets().CreateOrUpdateAsync(WaitUntil.Started, "main", TestClient.SampleData(), Cancel.Token);
        var result = await operation.WaitForCompletionAsync(Cancel.Token);

        result.Value.Data.Location.ShouldBe("eu-central");

        // The credential was asked more than once — the expiry was noticed.
        credential.Calls.ShouldBeGreaterThan(1);

        var authorizations = transport.Requests.Select(x => x.Authorization).ToList();

        authorizations.ShouldContain("Bearer token-expiring");
        authorizations.ShouldContain("Bearer token-fresh");

        // ⚠ And the last request carried the fresh one. An implementation that noticed the expiry but
        // kept sending the dead token would satisfy every assertion above this line.
        authorizations[^1].ShouldBe("Bearer token-fresh");
    }

    /// <summary>The cache is what keeps a ten-minute token from costing a token request per call.</summary>
    [Fact]
    public async Task A_live_token_is_reused_across_calls() {
        var credential = new FakeCredential("token-1", TimeSpan.FromHours(1));
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport, credential);

        for (var i = 0; i < 5; i++)
            await client.Widgets().GetAsync("main", Cancel.Token);

        transport.RequestCount.ShouldBe(5);
        credential.Calls.ShouldBe(1);
    }

    /// <summary>
    ///     ⚠ Single-flight, and the reason is docs/plan/11 § Sessions and revocation rather than
    ///     efficiency: two concurrent refreshes would spend the same one-time refresh token, the server
    ///     would see a reuse, and the whole chain would be revoked.
    /// </summary>
    [Fact]
    public async Task Concurrent_calls_ask_the_credential_once() {
        var credential = new FakeCredential(async: true);
        var transport = new ScriptedTransport((request, index) => Responses.Json(HttpStatusCode.OK, TestClient.WidgetBody));

        using var client = TestClient.Create(transport, credential);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.Widgets().GetAsync("main", Cancel.Token)));

        credential.Calls.ShouldBe(1);
    }
}

/// <summary>
///     ⚠ <b>No credential material in any exception message, log or diagnostic scope.</b>
///     docs/plan/08 § Errors' <i>"no exception details, ever"</i>, read from the client side.
/// </summary>
public sealed class CredentialsNeverLeakTests {
    const string Secret = "s3cr3t-client-secret-do-not-log";

    static ScriptedTransport Discovery(Func<HttpRequestMessage, int, HttpResponseMessage> tokenResponse)
        => new((request, index) => request.RequestUri!.AbsolutePath.Contains(".well-known", StringComparison.Ordinal)
            ? Responses.Json(
                HttpStatusCode.OK,
                """
                {"issuer":"https://login.cybercloud.test/",
                 "token_endpoint":"https://login.cybercloud.test/connect/token",
                 "authorization_endpoint":"https://login.cybercloud.test/connect/authorize",
                 "device_authorization_endpoint":"https://login.cybercloud.test/connect/device",
                 "jwks_uri":"https://login.cybercloud.test/.well-known/jwks"}
                """)
            : tokenResponse(request, index));

    static CyberCloudCredentialOptions Options(ScriptedTransport transport) => new() {
        AuthorityHost = new Uri("https://login.cybercloud.test/"),
        Transport = transport,
        TokenCache = TokenCache.CreateInMemory(),
    };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"The client credentials are wrong."}""")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>oh no</html>")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    public async Task A_failing_token_endpoint_never_echoes_the_client_secret(HttpStatusCode status, string body) {
        var transport = Discovery((request, index) => Responses.Json(status, body));

        using var credential = new ClientSecretCredential("tenant", "client", Secret, Options(transport));

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        Describe(thrown).ShouldNotContain(Secret);
    }

    /// <summary>
    ///     ⚠ <see cref="AccessToken.ToString" /> is overridden precisely so that the reflex of
    ///     interpolating a value into a log line cannot leak the token.
    /// </summary>
    [Fact]
    public void An_access_token_does_not_print_itself() {
        var token = new AccessToken("bearer-value-do-not-log", DateTimeOffset.UtcNow.AddMinutes(10));

        token.ToString().ShouldNotContain("bearer-value-do-not-log");
        $"{token}".ShouldNotContain("bearer-value-do-not-log");
    }

    /// <summary>
    ///     A request failure carries the status, the code, the message and the service request id —
    ///     and nothing about the request that produced it, including its <c>Authorization</c> header.
    /// </summary>
    [Fact]
    public async Task A_request_failure_never_carries_the_authorization_header() {
        var transport = new ScriptedTransport((request, index) =>
            Responses.Json(HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"Not permitted."}}"""));

        using var client = TestClient.Create(transport, new FakeCredential("token-do-not-log"));

        var thrown = await Should.ThrowAsync<CyberCloudRequestFailedException>(async () => await client.Widgets().GetAsync("main", Cancel.Token));

        Describe(thrown).ShouldNotContain("token-do-not-log");
        thrown.ErrorCode.ShouldBe("AuthorizationFailed");
    }

    [Fact]
    public async Task A_certificate_credentials_assertion_never_appears_in_a_failure() {
        using var certificate = TestCertificates.CreateRsa();

        var transport = Discovery((request, index) =>
            Responses.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Unknown certificate."}"""));

        using var credential = new CertificateCredential("tenant", "client", certificate, Options(transport));

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        var described = Describe(thrown);

        described.ShouldNotContain("client_assertion");
        described.ShouldNotContain(Convert.ToBase64String(certificate.GetCertHash())[..16]);
    }

    /// <summary>Everything an operator would ever see: the message chain plus the stack trace.</summary>
    static string Describe(Exception exception) {
        var builder = new StringBuilder();

        for (Exception? current = exception; current is not null; current = current.InnerException)
            builder.AppendLine(current.ToString());

        foreach (var entry in exception.Data.Keys)
            builder.AppendLine($"{entry}={exception.Data[entry]}");

        return builder.ToString();
    }
}

/// <summary>The chain — docs/plan/10 § Authentication inputs, and the shape a <c>DefaultAzureCredential</c> user knows.</summary>
public sealed class ChainedCredentialTests {
    sealed class Unavailable(string why) : TokenCredential {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default)
            => throw new CredentialUnavailableException(why);
    }

    sealed class Broken : TokenCredential {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default)
            => throw new AuthenticationFailedException("The client secret is wrong.");
    }

    [Fact]
    public async Task An_unavailable_credential_falls_through_to_the_next() {
        using var chain = new ChainedTokenCredential(new Unavailable("no CLI"), new FakeCredential("token-2"));

        var token = await chain.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token);

        token.Token.ShouldBe("token-2");
    }

    /// <summary>
    ///     ⚠ A wrong client secret that fell through to the next credential would be a chain that hides
    ///     its own misconfiguration — the developer sees a browser prompt and never learns CI is broken.
    /// </summary>
    [Fact]
    public async Task A_failed_credential_stops_the_chain() {
        using var chain = new ChainedTokenCredential(new Broken(), new FakeCredential("token-2"));

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await chain.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        thrown.ShouldNotBeOfType<CredentialUnavailableException>();
        thrown.Message.ShouldContain("client secret");
    }

    /// <summary>"No credential found" without saying what was tried is the least answerable support question there is.</summary>
    [Fact]
    public async Task An_exhausted_chain_names_every_credential_it_tried() {
        using var chain = new ChainedTokenCredential(new Unavailable("no CLI on the path"), new Unavailable("no federated token file"));

        var thrown = await Should.ThrowAsync<CredentialUnavailableException>(
            async () => await chain.GetTokenAsync(new TokenRequestContext([CyberCloudScopes.Default]), Cancel.Token));

        thrown.Message.ShouldContain("no CLI on the path");
        thrown.Message.ShouldContain("no federated token file");
    }
}
