using System.Security.Cryptography;

namespace CyberCloud.Sdk.Tests;

/// <summary>
///     Id-token validation. ⚠ The SDK validates the <c>id_token</c> and never the access token —
///     docs/plan/11 § Protocol keeps roles and permissions out of the access token, and a client that
///     read claims from it would start depending on them.
/// </summary>
public sealed class IdTokenTests {
    static readonly RSA Key = RSA.Create(2048);
    const string KeyId = "key-1";
    const string Issuer = "https://login.cybercloud.test/";
    const string Audience = "cyc";

    static string Jwk() {
        var parameters = Key.ExportParameters(includePrivateParameters: false);

        return $$"""
            {"keys":[{"kty":"RSA","kid":"{{KeyId}}","use":"sig","alg":"RS256",
                      "n":"{{Base64Url(parameters.Modulus!)}}","e":"{{Base64Url(parameters.Exponent!)}}"}]}
            """;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Sign(string header, string payload) {
        var signingInput = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{Base64Url(Encoding.UTF8.GetBytes(payload))}";
        var signature = Key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    static string Payload(string issuer = Issuer, string audience = Audience, int lifetimeSeconds = 600) {
        var now = DateTimeOffset.UtcNow;

        return $$"""
            {"iss":"{{issuer}}","aud":"{{audience}}","sub":"user-1","nbf":{{now.ToUnixTimeSeconds()}},
             "exp":{{now.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds()}}}
            """;
    }

    static (FakeIdentityServer Server, IdentityClient Identity, SigningKeyCache Keys) Fixture() {
        var server = new FakeIdentityServer { KeysResponse = () => Responses.Json(HttpStatusCode.OK, Jwk()) };
        var identity = new IdentityClient(server.Authority, server);

        return (server, identity, new SigningKeyCache(identity));
    }

    [Fact]
    public async Task A_correctly_signed_token_validates_and_its_claims_are_readable() {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var token = Sign($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}""", Payload());

        using var claims = await IdTokenValidator.ValidateAsync(token, keys, Issuer, Audience, DateTimeOffset.UtcNow, Cancel.Token);

        claims.RootElement.GetProperty("sub").GetString().ShouldBe("user-1");
    }

    /// <summary>
    ///     ⚠ <b>The oldest JWT vulnerability there is.</b> <c>alg: none</c> asks the verifier to accept
    ///     a token whose signature is empty. <see cref="IdTokenValidator" />'s algorithm switch has no
    ///     arm that returns true without checking a signature, and must never grow one.
    /// </summary>
    [Fact]
    public async Task The_none_algorithm_is_refused() {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var header = Base64Url(Encoding.UTF8.GetBytes($$"""{"alg":"none","typ":"JWT","kid":"{{KeyId}}"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(Payload()));

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await IdTokenValidator.ValidateAsync($"{header}.{payload}.", keys, Issuer, Audience, DateTimeOffset.UtcNow, Cancel.Token));

        thrown.Message.ShouldContain("unsupported algorithm");
    }

    [Fact]
    public async Task A_tampered_payload_fails_the_signature_check() {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var token = Sign($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}""", Payload());
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{Base64Url(Encoding.UTF8.GetBytes(Payload(audience: "someone-else")))}.{parts[2]}";

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await IdTokenValidator.ValidateAsync(tampered, keys, Issuer, Audience, DateTimeOffset.UtcNow, Cancel.Token));

        thrown.Message.ShouldContain("signature");
    }

    [Theory]
    [InlineData("https://evil.example/", Audience, "issuer")]
    [InlineData(Issuer, "another-client", "issued to this client")]
    public async Task A_token_for_a_different_issuer_or_audience_is_refused(string issuer, string audience, string expected) {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var token = Sign($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}""", Payload(issuer, audience));

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await IdTokenValidator.ValidateAsync(token, keys, Issuer, Audience, DateTimeOffset.UtcNow, Cancel.Token));

        thrown.Message.ShouldContain(expected);
    }

    /// <summary>60 seconds of leeway for clock skew, and no more — a wider window is a longer life for a stolen token.</summary>
    [Fact]
    public async Task An_expired_token_is_refused_once_it_is_past_the_skew_allowance() {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var token = Sign($$"""{"alg":"RS256","typ":"JWT","kid":"{{KeyId}}"}""", Payload(lifetimeSeconds: 1));

        // Inside the allowance: still accepted.
        using var claims = await IdTokenValidator.ValidateAsync(
            token, keys, Issuer, Audience, DateTimeOffset.UtcNow.AddSeconds(30), Cancel.Token);

        claims.RootElement.GetProperty("sub").GetString().ShouldBe("user-1");

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await IdTokenValidator.ValidateAsync(
                token, keys, Issuer, Audience, DateTimeOffset.UtcNow.AddMinutes(5), Cancel.Token));

        thrown.Message.ShouldContain("expired");
    }

    /// <summary>A token naming a key the server does not publish is refused rather than accepted on trust.</summary>
    [Fact]
    public async Task A_token_naming_an_unpublished_key_is_refused() {
        var (server, identity, keys) = Fixture();
        using var _ = server;
        using var __ = identity;

        var token = Sign("""{"alg":"RS256","typ":"JWT","kid":"key-that-does-not-exist"}""", Payload());

        var thrown = await Should.ThrowAsync<AuthenticationFailedException>(
            async () => await IdTokenValidator.ValidateAsync(token, keys, Issuer, Audience, DateTimeOffset.UtcNow, Cancel.Token));

        thrown.Message.ShouldContain("does not publish");
    }
}
