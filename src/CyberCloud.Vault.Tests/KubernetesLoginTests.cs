using CyberCloud.Vault.Tests.Infrastructure;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     How the platform authenticates to OpenBao: the pod's own projected service-account token,
///     exchanged for a lease.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT THIS SUITE CANNOT PROVE, SAID FIRST BECAUSE IT IS THE IMPORTANT PART.</b>
///         OpenBao's Kubernetes auth method calls the cluster's <c>TokenReview</c> API on <b>every</b>
///         login — its <c>pathLogin</c> runs <c>serviceAccount.lookup</c> unconditionally after
///         <c>parseAndValidateJWT</c> returns, and configuring <c>pem_keys</c> changes only whether
///         the JWT's signature is checked against a static key set instead of being skipped. That was
///         established by reading OpenBao's source after a login against a real container with
///         <c>pem_keys</c> configured came back <c>403</c>. So <b>no test here logs in
///         successfully</b>: the success path is driven against a stubbed handler that answers the
///         documented envelope, and the refusal path against a real OpenBao whose <c>kubernetes</c>
///         mount has no cluster behind it.
///     </para>
///     <para>
///         What that leaves owed is one test: a real projected token from a real kubelet accepted by
///         a real OpenBao. This repository already has <c>Testcontainers.K3s</c> and a
///         <c>K3sFixture</c>, so it is buildable — OpenBao configured against the k3s API server,
///         with its reviewer service account — and it is two containers and a network hop, which
///         <c>build/README.md § failed to bind host port</c> is about.
///     </para>
///     <para>
///         ⚠ <b>Kubernetes auth rather than a token in configuration, and the argument is that a
///         credential in configuration is the problem a vault exists to solve.</b> The pod already
///         holds one it did not have to be given. <see cref="VaultOptions" /> has no token member so
///         that the shortcut cannot be taken by editing a values file —
///         <see cref="NoOptionCanCarryACredential" /> is the assertion.
///     </para>
/// </remarks>
public sealed class KubernetesLoginTests {
    static readonly DateTimeOffset Now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheLoginSendsTheRoleAndThePodsOwnTokenAndNothingElse() {
        var file = TokenFile("a-projected-service-account-token");
        var handler = new StubHandler(Envelope(3600));

        var source = new KubernetesVaultTokenSource(new(handler), Options(file), new MovableClock(Now));

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.IsSuccess.ShouldBeTrue(token.Error?.Message);
        token.GetValueOrThrow().Value.ShouldBe("s.leased-token");

        handler.Url.ShouldBe("https://openbao.test/v1/auth/kubernetes/login");

        using var body = JsonDocument.Parse(handler.Body!);

        body.RootElement.GetProperty("role").GetString().ShouldBe("cc-silo");
        body.RootElement.GetProperty("jwt").GetString().ShouldBe("a-projected-service-account-token");
        body.RootElement.EnumerateObject().Count().ShouldBe(
            2,
            "the login payload is a role and an assertion; anything else is something this client "
            + "invented"
        );
    }

    [Fact]
    public async Task TheLeaseIsShortenedByTheSkewSoAReadCannotRaceItsOwnExpiry() {
        var clock = new MovableClock(Now);
        var options = Options(TokenFile("t"));
        options.TokenExpirySkew = TimeSpan.FromSeconds(60);

        var source = new KubernetesVaultTokenSource(new(new StubHandler(Envelope(3600))), options, clock);

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.GetValueOrThrow().ExpiresAt.ShouldBe(Now.AddSeconds(3600 - 60));
    }

    [Fact]
    public async Task ALeaseShorterThanTheSkewExpiresImmediatelyRatherThanInThePast() {
        // ⚠ A role configured with a 30-second TTL against a 60-second skew. Subtracting gives an
        // expiry in the past, which is not merely odd — a token that is already expired when it is
        // cached means a fresh login on every single resolve, which is a self-inflicted denial of
        // service against the auth mount and a TokenReview call per secret.
        var clock = new MovableClock(Now);
        var options = Options(TokenFile("t"));
        options.TokenExpirySkew = TimeSpan.FromSeconds(60);

        var source = new KubernetesVaultTokenSource(new(new StubHandler(Envelope(30))), options, clock);

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.GetValueOrThrow().ExpiresAt.ShouldBe(Now, "clamped at zero rather than allowed to go negative");
    }

    [Fact]
    public async Task ACachedTokenIsReusedUntilItExpiresAndThenReplaced() {
        var clock = new MovableClock(Now);
        var handler = new StubHandler(Envelope(3600));
        var source = new KubernetesVaultTokenSource(new(handler), Options(TokenFile("t")), clock);

        await source.GetAsync(TestContext.Current.CancellationToken);
        await source.GetAsync(TestContext.Current.CancellationToken);

        handler.Calls.ShouldBe(
            1,
            "a login per resolve would be an audit entry and a TokenReview call per secret read"
        );

        clock.Advance(TimeSpan.FromHours(2));
        await source.GetAsync(TestContext.Current.CancellationToken);

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task InvalidatingSomethingOtherThanTheCachedTokenChangesNothing() {
        // ⚠ Two resolves racing on a revoked token both call Invalidate. The first drops it; the
        // second must not drop whatever the first has since fetched, or a busy silo re-logs-in once
        // per concurrent reconcile pass forever.
        var clock = new MovableClock(Now);
        var handler = new StubHandler(Envelope(3600));
        var source = new KubernetesVaultTokenSource(new(handler), Options(TokenFile("t")), clock);

        var first = (await source.GetAsync(TestContext.Current.CancellationToken)).GetValueOrThrow();

        source.Invalidate(first);
        var second = (await source.GetAsync(TestContext.Current.CancellationToken)).GetValueOrThrow();

        handler.Calls.ShouldBe(2, "the token it was actually holding was thrown away");

        source.Invalidate(first);
        await source.GetAsync(TestContext.Current.CancellationToken);

        handler.Calls.ShouldBe(2, "a stale handle must not evict the token that replaced it");
        second.ShouldNotBeSameAs(first);
    }

    [Fact]
    public async Task NoTokenFileIsAWiringFaultAndSaysWhichFile() {
        var options = Options("/no/such/projected/token");

        var source = new KubernetesVaultTokenSource(new(new StubHandler(Envelope(3600))), options, new MovableClock(Now));

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.IsFailure.ShouldBeTrue();
        token.Error!.Message.ShouldContain(
            "/no/such/projected/token",
            Case.Sensitive,
            "the operator half of this names the file, because a silo outside Kubernetes and a silo "
            + "with automountServiceAccountToken disabled look identical without it"
        );
    }

    [Fact]
    public async Task AnEmptyTokenFileIsRefusedRatherThanSentAsAnEmptyAssertion() {
        var source = new KubernetesVaultTokenSource(
            new(new StubHandler(Envelope(3600))),
            Options(TokenFile(string.Empty)),
            new MovableClock(Now)
        );

        (await source.GetAsync(TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ALoginAnsweringWithNoClientTokenIsRefusedRatherThanCarriedOn() {
        // ⚠ THE SHAPE THAT WOULD TURN ONE AUTHENTICATION FAULT INTO A PERMISSION DENIAL ON EVERY
        // SECRET. An empty client_token sails through into an X-Vault-Token header, OpenBao answers
        // 403 to every read, and an operator goes and audits policies that are fine.
        var source = new KubernetesVaultTokenSource(
            new(new StubHandler("""{"auth":{"lease_duration":3600}}""")),
            Options(TokenFile("t")),
            new MovableClock(Now)
        );

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.IsFailure.ShouldBeTrue();
        token.Error!.Message.ShouldContain("client_token");
    }

    [Fact]
    public async Task ARefusedLoginNeverReproducesTheAssertionItSent() {
        var handler = new StubHandler("""{"errors":["permission denied"]}""", HttpStatusCode.Forbidden);

        var source = new KubernetesVaultTokenSource(
            new(handler),
            Options(TokenFile("a-projected-service-account-token")),
            new MovableClock(Now)
        );

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.IsFailure.ShouldBeTrue();
        token.Error!.Message.ShouldNotContain(
            "a-projected-service-account-token",
            Case.Insensitive,
            "the pod's token is a credential, and a login failure is exactly when somebody is "
            + "tempted to log what was sent"
        );

        token.Error.Message.ShouldContain("403");
    }

    [Fact]
    public void NoOptionCanCarryACredential() {
        // ⚠ THE ANSWER TO "WHAT CREDENTIAL DOES THE PLATFORM USE" IS "NONE THAT ANYBODY CONFIGURES",
        // AND THIS IS WHERE THAT STOPS BEING A SENTENCE. A Token property here — added for local
        // development, kept forever — is a deployment that can put a vault token in a values file,
        // which is the thing the vault exists to stop.
        //
        // Names rather than types, because the type of the shortcut would be `string` like everything
        // else here. The four suffixes are CC1005's, which does not run over this assembly.
        foreach (var property in typeof(VaultOptions).GetProperties()) {
            foreach (var suffix in new[] { "Password", "Secret", "Token", "Key" }) {
                if (!property.Name.EndsWith(suffix, StringComparison.Ordinal)) {
                    continue;
                }

                // TokenFilePath and TokenExpirySkew end in neither; anything that DOES is a value.
                property.Name.ShouldBe(
                    "impossible",
                    $"VaultOptions.{property.Name} ends in '{suffix}'. Every member of this type is "
                    + "an address or a name — the silo's credential is the projected token its own "
                    + "pod carries, and there is deliberately nothing here to paste one into"
                );
            }
        }
    }

    static VaultOptions Options(string tokenFilePath) =>
        new() {
            Address = "https://openbao.test",
            Role = "cc-silo",
            TokenFilePath = tokenFilePath,
            TokenExpirySkew = TimeSpan.FromSeconds(60),
        };

    static string Envelope(int leaseSeconds) =>
        """{"auth":{"client_token":"s.leased-token","accessor":"acc","lease_duration":LEASE,"renewable":true}}"""
            .Replace("LEASE", leaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Writes a token file the way a kubelet would, into the test's own temporary directory.</summary>
    /// <param name="contents">What the file holds.</param>
    static string TokenFile(string contents) {
        var path = Path.Combine(Path.GetTempPath(), $"cc-vault-{Guid.NewGuid():N}.token");

        File.WriteAllText(path, contents);

        return path;
    }

    /// <summary>Answers one canned response and records what was asked.</summary>
    /// <param name="body">The response body.</param>
    /// <param name="status">The status to answer with.</param>
    /// <remarks>
    ///     ⚠ A stub rather than a container for these rows, and the reason is in the class remarks:
    ///     no container can make a Kubernetes login succeed. What is under test here is this client's
    ///     own arithmetic — the payload it builds, the lease it computes, what it caches and what it
    ///     refuses — and none of that is OpenBao's behaviour.
    /// </remarks>
    sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
        public int Calls { get; private set; }

        public string? Url { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            Calls++;
            Url = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}

/// <summary>
///     What a real OpenBao does with a Kubernetes login it cannot review.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the production failure, not a contrived one.</b> A silo whose vault cannot reach
///     the cluster's API server — a NetworkPolicy, a rotated CA, a reviewer service account whose
///     binding was removed — sees exactly this, and it is the difference between "the platform cannot
///     authenticate" and "this secret is missing".
/// </remarks>
[Collection(OpenBaoSuite.Name)]
public sealed class KubernetesLoginAgainstRealOpenBaoTests(OpenBaoFixture vault) {
    [Fact]
    public async Task ALoginAgainstAMountWithNoClusterBehindItFailsAsAnAuthenticationFault() {
        await vault.EnableKubernetesAuthAsync();

        var options = vault.Options();
        options.TokenFilePath = Path.Combine(Path.GetTempPath(), $"cc-vault-{Guid.NewGuid():N}.token");

        await File.WriteAllTextAsync(
            options.TokenFilePath,
            "not-a-real-projected-token",
            TestContext.Current.CancellationToken
        );

        using var source = new KubernetesVaultTokenSource(
            new() { Timeout = options.RequestTimeout },
            options,
            new MovableClock(DateTimeOffset.UtcNow)
        );

        var token = await source.GetAsync(TestContext.Current.CancellationToken);

        token.IsFailure.ShouldBeTrue("no cluster can review that token, so no login can succeed");
        token.Error!.Code.ShouldBe(
            ErrorCode.InternalError,
            "the platform failing to authenticate to its own vault is not a tenant's authorization "
            + "problem"
        );

        token.Error.Message.ShouldNotContain("not-a-real-projected-token", Case.Insensitive);
    }

    [Fact]
    public async Task AResolveOnASiloThatCannotLogInSaysSoAndNamesNoVaultInternals() {
        await vault.EnableKubernetesAuthAsync();

        var options = vault.Options();
        options.TokenFilePath = "/no/such/projected/token";

        using var source = new KubernetesVaultTokenSource(
            new() { Timeout = options.RequestTimeout },
            options,
            new MovableClock(DateTimeOffset.UtcNow)
        );

        var resolver = new OpenBaoSecretResolver(new() { Timeout = options.RequestTimeout }, source, options);

        var resolved = await resolver.ResolveAsync(
            new() { Path = "tenants/x/postgres/main", Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();

        // ⚠ The token source's own failures are operator detail — they name the token file, the
        // address and the role. The resolver logs them and hands the caller the tenant-safe half; a
        // pass-through here would put the platform's deployment layout into a tenant's portal.
        resolved.Error!.Message.ShouldNotContain("/no/such/projected/token");
        resolved.Error.Message.ShouldNotContain(vault.Address);
        resolved.Error.Message.ShouldContain(VaultFailures.Escalation);
    }
}
