using CyberCloud.Sdk;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc login</c> — the user experience over the SDK's grants.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         What is asserted here is what the CLI owns: the flags, the prompt, the browser and the
///         report.
///     </b> The device-authorization request, RFC 8628's polling and back-off, the token
///     exchange and the keychain write are <c>CyberCloud.Sdk</c>'s, and the identity server they talk
///     to is scripted through <c>CyberCloudCredentialOptions.Transport</c>. The token cache is an
///     in-memory one, so no test touches a real keychain.
/// </remarks>
public sealed class LoginTests {
    [Fact]
    public async Task DeviceCodePrintsTheCodeAndOpensTheBrowser() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        var code = await host.RunAsync("login", "--device-code", "--tenant", "contoso", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);

        // ⚠ On stderr. The user code is not a secret — RFC 8628 makes it a one-time value typed into
        // a page — but it is not the answer either, and `cyc login --output json | jq` has to work.
        host.Stderr.ShouldContain("ABCD-EFGH");
        host.Stderr.ShouldContain("https://login.cybercloud.io/device");
        host.Stderr.ShouldContain("Signed in.");

        host.Browsed.ShouldContain(x => x.ToString().Contains("device", StringComparison.Ordinal));

        using var document = host.StdoutAsJson();
        document.RootElement.GetProperty("tenant").GetString().ShouldBe("contoso");
        document.RootElement.TryGetProperty("accessToken", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task TheAccessTokenNeverReachesEitherStream() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        await host.RunAsync("login", "--device-code", "--output", "json");

        host.Stdout.ShouldNotContain(SignedInToken);
        host.Stderr.ShouldNotContain(SignedInToken);
    }

    [Fact]
    public async Task SigningInWritesNothingToTheStateDirectory() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        await host.RunAsync("login", "--device-code", "--output", "none");

        // ⚠ docs/plan/21 § Decisions: "Never a plaintext file — that is how CI credentials leak into
        // container images." The refresh token went to the SDK's cache. The two files that may be
        // here are the update-check stamp and `config`, which holds the telemetry answer this run
        // recorded — and no token.
        var files = Directory.GetFiles(host.StateDirectory, "*", SearchOption.AllDirectories);

        files.ShouldAllBe(path => Path.GetFileName(path) == "update-check" || Path.GetFileName(path) == "config");

        foreach (var file in files) {
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

            text.ShouldNotContain(SignedInToken);
            text.ShouldNotContain(RefreshToken);
        }
    }

    [Fact]
    public async Task AServicePrincipalWithoutACredentialSaysWhereToPutOne() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("login", "--service-principal", "--client-id", "app-1", "--tenant", "contoso");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("CYC_CLIENT_SECRET");

        // ⚠ The message says why, not just what: a secret passed as an argument is in the shell
        // history and in ps output, so there is deliberately no --client-secret flag to suggest.
        host.Stderr.ShouldContain("shell history");
    }

    [Fact]
    public async Task AServicePrincipalReadsItsSecretFromTheEnvironment() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));

        using var host = TestHost.Create(
            environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["CYC_CLIENT_SECRET"] = "s3cret"
            },
            credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        var code = await host.RunAsync(
            "login",
            "--service-principal",
            "--client-id",
            "app-1",
            "--tenant",
            "contoso",
            "--output",
            "none"
        );

        code.ShouldBe((int)ExitCode.Ok);
        host.Stderr.ShouldNotContain("s3cret");
        host.Stdout.ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task AFailedSignInIsExitThree() {
        var identity = new ScriptedTransport(static (request, _) => request.RequestUri!.AbsolutePath.EndsWith(
                "token",
                StringComparison.Ordinal
            )
                ? Responses.Json(
                    HttpStatusCode.BadRequest,
                    """{"error":"invalid_client","error_description":"Unknown client."}"""
                )
                : Identity(request)
        );

        using var host = TestHost.Create(
            environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["CYC_CLIENT_SECRET"] = "s3cret"
            },
            credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        var code = await host.RunAsync("login", "--service-principal", "--client-id", "app-1", "--tenant", "contoso");

        code.ShouldBe((int)ExitCode.Auth);
        host.Stderr.ShouldContain("invalid_client");
    }

    [Fact]
    public async Task TheDeviceFlowAsksForARefreshTokenAndPollsWithoutAScope() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = TokenCache.CreateInMemory()
            }
        );

        (await host.RunAsync("login", "--device-code", "--output", "none")).ShouldBe((int)ExitCode.Ok);

        var device = identity.Requests.Single(static x => x.Uri.AbsolutePath.EndsWith("devicecode", StringComparison.Ordinal));
        var poll = identity.Requests.Last(static x => x.Uri.AbsolutePath.EndsWith("token", StringComparison.Ordinal));

        // ⚠ The registered client and the scopes the identity host knows — #43 found the SDK sending
        // `cyc` and an Azure-shaped `.default` scope, both refused by the real host — and
        // offline_access, without which nothing is cached and `cyc login` persists nothing.
        device.Body.ShouldContain("client_id=" + FirstPartyClientId);
        device.Body.ShouldContain("scope=cyc.api+offline_access");

        // ⚠ RFC 8628 § 3.4's three parameters and no scope: OpenIddict refuses a device-code
        // token request that carries one.
        poll.Body.ShouldContain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code");
        poll.Body.ShouldNotContain("scope=");
    }

    [Fact]
    public async Task LogoutRevokesTheCachedSignInAtTheIdentityHostAndForgetsIt() {
        var identity = new ScriptedTransport(static (request, _) => Identity(request));
        var cache = TokenCache.CreateInMemory();

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = cache
            }
        );

        (await host.RunAsync("login", "--device-code", "--output", "none")).ShouldBe((int)ExitCode.Ok);
        (await cache.GetAsync(Key(), TestContext.Current.CancellationToken)).ShouldNotBeNull("login cached nothing");

        (await host.RunAsync("logout")).ShouldBe((int)ExitCode.Ok);

        var revoke = identity.Requests.Single(static x => x.Uri.AbsolutePath.EndsWith("revoke", StringComparison.Ordinal));

        revoke.Method.ShouldBe(HttpMethod.Post);
        revoke.Body.ShouldContain("token=" + RefreshToken);
        revoke.Body.ShouldContain("client_id=" + FirstPartyClientId);
        host.Stderr.ShouldContain("revoked at the identity host");
        host.Stderr.ShouldNotContain(RefreshToken);

        (await cache.GetAsync(Key(), TestContext.Current.CancellationToken)).ShouldBeNull("logout left the sign-in cached");
    }

    [Fact]
    public async Task LogoutForgetsTheSignInEvenWhenTheIdentityHostCannotBeReached() {
        var reachable = true;
        var identity = new ScriptedTransport((request, _) => reachable
            ? Identity(request)
            : throw new HttpRequestException("connection refused")
        );
        var cache = TokenCache.CreateInMemory();

        using var host = TestHost.Create(credentialOptions: () => new CyberCloudCredentialOptions {
                Transport = identity, TokenCache = cache
            }
        );

        (await host.RunAsync("login", "--device-code", "--output", "none")).ShouldBe((int)ExitCode.Ok);

        reachable = false;

        // ⚠ Exit 0 and the entry gone: a logout on a machine that lost its network still signs it
        // out here, and says the server-side half is owed rather than pretending it happened.
        (await host.RunAsync("logout")).ShouldBe((int)ExitCode.Ok);
        host.Stderr.ShouldContain("could not be revoked");
        (await cache.GetAsync(Key(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>The client id the SDK signs in as — the identity host's <c>cyc-cli</c>.</summary>
    const string FirstPartyClientId = "cyc-cli";

    static string Key() => TokenCache.KeyFor(new Uri("https://login.cybercloud.io/"), CyberCloudCliCredential.CliClientId, null);

    const string SignedInToken = "signed-in-token-9a3f";

    const string RefreshToken = "refresh-token-4b2e";

    /// <summary>
    ///     A scripted identity server — the four endpoints EmitterContract.cs § "What the SDK needs
    ///     from the identity server" lists.
    /// </summary>
    static HttpResponseMessage Identity(HttpRequestMessage request) {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("openid-configuration", StringComparison.Ordinal)) {
            return Responses.Json(
                HttpStatusCode.OK,
                """
                {
                  "issuer": "https://login.cybercloud.io/",
                  "authorization_endpoint": "https://login.cybercloud.io/authorize",
                  "token_endpoint": "https://login.cybercloud.io/token",
                  "device_authorization_endpoint": "https://login.cybercloud.io/devicecode",
                  "revocation_endpoint": "https://login.cybercloud.io/revoke",
                  "jwks_uri": "https://login.cybercloud.io/jwks"
                }
                """
            );
        }

        if (path.EndsWith("devicecode", StringComparison.Ordinal)) {
            return Responses.Json(
                HttpStatusCode.OK,
                """
                {
                  "device_code": "secret-device-code",
                  "user_code": "ABCD-EFGH",
                  "verification_uri": "https://login.cybercloud.io/device",
                  "verification_uri_complete": "https://login.cybercloud.io/device?code=ABCD-EFGH",
                  "expires_in": 300,
                  "interval": 0
                }
                """
            );
        }

        if (path.EndsWith("revoke", StringComparison.Ordinal)) {
            return Responses.Json(HttpStatusCode.OK, "{}");
        }

        return Responses.Json(
            HttpStatusCode.OK,
            $$"""
            {"access_token":"{{SignedInToken}}","token_type":"Bearer","expires_in":600,"refresh_token":"{{RefreshToken}}"}
            """
        );
    }
}
