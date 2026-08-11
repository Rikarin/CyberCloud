using System.Security.Cryptography;
using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     The identity server's signing keys, cached for a bounded time and re-read when a token names a
///     key the cache has not seen.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A client that caches JWKS forever breaks on day 31, and this is why.</b> docs/plan/11
///         § Protocol: access tokens are <i>"signed with a rotating key set (30-day rotation, both keys
///         published for 60)"</i>. So a key that verified every token last month verifies none this
///         month, and the failure mode of an infinite cache is a client that worked for a month and
///         then stopped, everywhere at once, with no deploy to blame.
///     </para>
///     <para>Two mechanisms, and both are needed:</para>
///     <list type="number">
///         <item>
///             <b>A time bound.</b> <see cref="Lifetime" /> is 24 hours — comfortably inside the
///             60-day overlap, so a rotation is picked up long before the old key is withdrawn.
///         </item>
///         <item>
///             <b>An unknown <c>kid</c> forces a re-read</b>, at most once per
///             <see cref="MinimumRefreshInterval" />. Without it a rotation is invisible for up to a
///             day; with it, unbounded, a token signed by a key that genuinely does not exist becomes
///             a JWKS fetch per validation — which is a denial-of-service amplifier pointed at the
///             identity server, exactly the shape docs/plan/11 § Credentials warns about for the
///             lockout counter.
///         </item>
///     </list>
/// </remarks>
public sealed class SigningKeyCache {
    /// <summary>How long a fetched key set is used before it is re-read.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The shortest interval between two fetches provoked by an unknown key id.</summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim gate = new(1, 1);
    readonly IdentityClient identity;
    readonly TimeProvider time;

    JsonWebKeySet? keys;
    DateTimeOffset fetchedAt;

    /// <summary>Creates the cache.</summary>
    /// <param name="identity">The client that fetches the key set.</param>
    /// <param name="time">The clock. Substituted by the tests.</param>
    public SigningKeyCache(IdentityClient identity, TimeProvider? time = null) {
        ArgumentNullException.ThrowIfNull(identity);

        this.identity = identity;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>How many times the key set was actually fetched. Read by the rotation test.</summary>
    public int FetchCount { get; private set; }

    /// <summary>Finds the key a token was signed with, re-reading the set when it is not known.</summary>
    /// <param name="keyId">The token header's <c>kid</c>.</param>
    /// <param name="cancellationToken">The token.</param>
    public async ValueTask<JsonWebKey?> GetKeyAsync(string? keyId, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();

        if (keys is { } cached && now - fetchedAt < Lifetime && Find(cached, keyId) is { } hit)
            return hit;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            now = time.GetUtcNow();

            if (keys is { } raced && Find(raced, keyId) is { } found && now - fetchedAt < Lifetime)
                return found;

            // The rate limit on provoked refetches. Inside it, an unknown kid answers "no key" rather
            // than fetching — the token is refused either way, and a refusal that costs the identity
            // server nothing is the one to prefer.
            if (keys is not null && now - fetchedAt < MinimumRefreshInterval)
                return Find(keys, keyId);

            keys = await identity.RequestKeysAsync(cancellationToken).ConfigureAwait(false);
            fetchedAt = now;
            FetchCount++;

            return Find(keys, keyId);
        } finally {
            gate.Release();
        }
    }

    static JsonWebKey? Find(JsonWebKeySet set, string? keyId) {
        foreach (var key in set.Keys) {
            if (keyId is null || string.Equals(key.Kid, keyId, StringComparison.Ordinal))
                return key;
        }

        return null;
    }
}

/// <summary>
///     Verifies an <c>id_token</c>'s signature, issuer, audience and expiry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The SDK validates the id token and never the access token.</b> An id token is a
///         statement <i>to this client</i> about who signed in, so this client is the party that has
///         to check it. An access token is a bearer credential for the gateway; a client that read
///         claims out of it would start depending on a shape docs/plan/11 § Protocol deliberately keeps
///         out of reach — <i>"Roles and permissions are not in the token"</i> — and would be checking
///         a signature nobody asked it to check.
///     </para>
///     <para>
///         ⚠ No <c>none</c> algorithm, ever. It is the oldest JWT vulnerability there is, and the
///         switch below has no arm for it.
///     </para>
/// </remarks>
public static class IdTokenValidator {
    /// <summary>Validates a token and returns its payload as raw JSON.</summary>
    /// <param name="token">The compact JWS.</param>
    /// <param name="keys">The key cache.</param>
    /// <param name="expectedIssuer">The discovery document's <c>issuer</c>.</param>
    /// <param name="expectedAudience">The client id.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <exception cref="AuthenticationFailedException">The token is not valid.</exception>
    public static async ValueTask<JsonDocument> ValidateAsync(
        string token,
        SigningKeyCache keys,
        string expectedIssuer,
        string expectedAudience,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(keys);

        var parts = token.Split('.');

        if (parts.Length != 3)
            throw new AuthenticationFailedException("The id token is not a compact JWS.");

        using var header = Parse(parts[0], "header");
        var algorithm = header.RootElement.TryGetProperty("alg", out var alg) ? alg.GetString() : null;
        var keyId = header.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() : null;

        var key = await keys.GetKeyAsync(keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new AuthenticationFailedException(
                $"The id token names signing key '{keyId}', which the identity server does not publish.");

        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64Url.Decode(parts[2]);

        if (!Verify(algorithm, key, signed, signature))
            throw new AuthenticationFailedException("The id token's signature is not valid.");

        var payload = Parse(parts[1], "payload");

        try {
            Check(payload.RootElement, expectedIssuer, expectedAudience, now);
        } catch {
            payload.Dispose();

            throw;
        }

        return payload;
    }

    static void Check(JsonElement claims, string expectedIssuer, string expectedAudience, DateTimeOffset now) {
        if (!claims.TryGetProperty("iss", out var issuer) || !string.Equals(issuer.GetString(), expectedIssuer, StringComparison.Ordinal))
            throw new AuthenticationFailedException("The id token was issued by a different issuer than the discovery document names.");

        if (!HasAudience(claims, expectedAudience))
            throw new AuthenticationFailedException("The id token was not issued to this client.");

        // ⚠ 60 seconds of leeway, and no more. Clock skew between a developer's laptop and a server is
        // real; a wider window is a longer life for a stolen token.
        var leeway = TimeSpan.FromSeconds(60);

        if (claims.TryGetProperty("exp", out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) + leeway < now)
            throw new AuthenticationFailedException("The id token has expired.");

        if (claims.TryGetProperty("nbf", out var nbf) && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()) - leeway > now)
            throw new AuthenticationFailedException("The id token is not valid yet.");
    }

    static bool HasAudience(JsonElement claims, string expected) {
        if (!claims.TryGetProperty("aud", out var audience))
            return false;

        if (audience.ValueKind is JsonValueKind.String)
            return string.Equals(audience.GetString(), expected, StringComparison.Ordinal);

        if (audience.ValueKind is not JsonValueKind.Array)
            return false;

        foreach (var item in audience.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.String && string.Equals(item.GetString(), expected, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static bool Verify(string? algorithm, JsonWebKey key, byte[] signed, byte[] signature)
        => algorithm switch {
            "RS256" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA256),
            "RS384" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA384),
            "RS512" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA512),
            "ES256" => VerifyEcdsa(key, signed, signature, HashAlgorithmName.SHA256),
            "ES384" => VerifyEcdsa(key, signed, signature, HashAlgorithmName.SHA384),
            // ⚠ Everything else, including "none", lands here. There is no arm that returns true
            // without checking a signature and there must never be one.
            _ => throw new AuthenticationFailedException($"The id token is signed with an unsupported algorithm '{algorithm}'."),
        };

    static bool VerifyRsa(JsonWebKey key, byte[] signed, byte[] signature, HashAlgorithmName hash) {
        if (key.N is null || key.E is null)
            return false;

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Base64Url.Decode(key.N), Exponent = Base64Url.Decode(key.E) });

        return rsa.VerifyData(signed, signature, hash, RSASignaturePadding.Pkcs1);
    }

    static bool VerifyEcdsa(JsonWebKey key, byte[] signed, byte[] signature, HashAlgorithmName hash) {
        if (key.X is null || key.Y is null)
            return false;

        var curve = key.Crv switch {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new AuthenticationFailedException($"The id token names an unsupported curve '{key.Crv}'."),
        };

        using var ecdsa = ECDsa.Create(new ECParameters {
            Curve = curve,
            Q = new ECPoint { X = Base64Url.Decode(key.X), Y = Base64Url.Decode(key.Y) },
        });

        return ecdsa.VerifyData(signed, signature, hash);
    }

    static JsonDocument Parse(string segment, string what) {
        try {
            return JsonDocument.Parse(Base64Url.Decode(segment));
        } catch (Exception e) when (e is JsonException or FormatException) {
            throw new AuthenticationFailedException($"The id token's {what} is not valid base64url JSON.", e);
        }
    }
}
