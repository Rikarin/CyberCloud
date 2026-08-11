using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Identity.ManagedIdentity;

/// <summary>
///     Verifies a Kubernetes projected service-account token against a cluster's recorded key set.
///     docs/plan/11 § Managed identity, step 5.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NO <c>alg: none</c>, EVER, AND THE SHAPE IS WHAT GUARANTEES IT.</b> The oldest JWT
///         vulnerability there is: a token whose header says the algorithm is <c>none</c>, accepted by
///         a validator that switched on the header and had an arm for it. <see cref="Verify" /> has no
///         arm that returns <see langword="true" /> without checking a signature, and its default arm
///         refuses. The second-oldest — handing an RSA public key to an HMAC verifier as its shared
///         secret — cannot happen either, because there is no HMAC arm at all: a cluster signs
///         service-account tokens asymmetrically and a symmetric algorithm here could only ever mean
///         somebody is confusing a public key for a secret.
///     </para>
///     <para>
///         ⚠ <b>The issuer is an argument and is never read out of the token to decide anything.</b>
///         The token's <c>iss</c> is compared to <see cref="ClusterOidcIssuer.Issuer" />; it does not
///         select a key set. A validator that looked up keys by the token's own claim would accept
///         anything signed by anyone who can publish a JWKS, which is everyone.
///     </para>
///     <para>
///         ⚠ <b>This is ~200 lines of in-house JWS verification, which is the same call docs/plan/11
///         § Credentials makes for TOTP</b> ("RFC 6238 in-house, ~200 lines"). The alternative is a
///         JWT library in <c>CyberCloud.Identity</c>, which that project's <c>.csproj</c> keeps free
///         of ASP.NET and of the protocol; the SDK reaches the same conclusion independently in
///         <c>IdTokenValidator</c>. ⚠ Two in-house verifiers is one more than there should be — the
///         owed fix is to lift the shared part into <c>CyberCloud.Core</c>, and it is not done here
///         because that assembly is another module's.
///     </para>
/// </remarks>
public sealed class ProjectedTokenValidator : IProjectedTokenValidator {
    /// <summary>
    ///     How much clock skew is tolerated on <c>exp</c> and <c>nbf</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Sixty seconds and no more. Skew between a tenant's cluster and a platform silo is real;
    ///     a wider window is a longer life for a token belonging to a service account that has since
    ///     been deleted.
    /// </remarks>
    public static TimeSpan Leeway { get; } = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public Result<ValidatedServiceAccount> Validate(
        string subjectToken,
        ClusterOidcIssuer issuer,
        DateTimeOffset now
    ) {
        ArgumentNullException.ThrowIfNull(issuer);

        if (string.IsNullOrEmpty(subjectToken) || issuer.IsEmpty) {
            return Reject("there is no token, or no trusted issuer to check it against");
        }

        var parts = subjectToken.Split('.');
        if (parts.Length != 3) {
            return Reject("it is not a compact JWS");
        }

        var header = Decode(parts[0]);
        if (header.TryGetError(out var headerError)) {
            return Result<ValidatedServiceAccount>.Failure(headerError);
        }

        using var headerJson = header.GetValueOrThrow();

        var algorithm = Text(headerJson.RootElement, "alg");
        var keyId = Text(headerJson.RootElement, "kid");

        var key = FindKey(issuer.PublicKeySetJson, keyId);
        if (key.TryGetError(out var keyError)) {
            return Result<ValidatedServiceAccount>.Failure(keyError);
        }

        using var jwk = key.GetValueOrThrow();

        // ⚠ The signed input is the two encoded segments and the '.' between them, byte for byte as
        // they arrived. Re-encoding the decoded JSON would verify a different string — and would
        // accept a token whose payload re-serializes to something else, which is the whole family of
        // JSON-canonicalisation bugs.
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);

        var signature = DecodeBytes(parts[2]);
        if (signature is null) {
            return Reject("its signature is not base64url");
        }

        if (!Verify(algorithm, jwk.RootElement, signed, signature)) {
            return Reject("its signature does not verify against the cluster's key set");
        }

        var payload = Decode(parts[1]);
        if (payload.TryGetError(out var payloadError)) {
            return Result<ValidatedServiceAccount>.Failure(payloadError);
        }

        using var claims = payload.GetValueOrThrow();

        return Check(claims.RootElement, issuer.Issuer, now);
    }

    static Result<ValidatedServiceAccount> Check(JsonElement claims, string trustedIssuer, DateTimeOffset now) {
        // ⚠ Ordinal and whole-string. An issuer compared by prefix would let
        // https://issuer.example.evil.test satisfy a binding to https://issuer.example.
        if (!string.Equals(Text(claims, "iss"), trustedIssuer, StringComparison.Ordinal)) {
            return Reject("it was signed for a different issuer than this identity is bound to");
        }

        if (Seconds(claims, "exp") is not { } expiresAt) {
            return Reject("it has no expiry");
        }

        if (expiresAt + Leeway < now) {
            return Reject("it has expired");
        }

        if (Seconds(claims, "nbf") is { } notBefore && notBefore - Leeway > now) {
            return Reject("it is not valid yet");
        }

        var subject = Text(claims, "sub");
        if (subject is null || !subject.StartsWith(TokenExchange.ServiceAccountSubjectPrefix, StringComparison.Ordinal)) {
            return Reject("its subject is not a Kubernetes service account");
        }

        // ⚠ Exactly two segments after the prefix, checked rather than assumed. Splitting on the
        // first colon and taking the rest would let a service account named `a:b` in namespace `x`
        // produce the same subject as one named `b` in namespace `x:a` — one workload's token
        // satisfying another workload's binding. WorkloadBinding.Create refuses a ':' on the way in;
        // this is the same rule enforced on the way out, on the side of the trust boundary that
        // cannot assume the cluster agrees.
        var rest = subject[TokenExchange.ServiceAccountSubjectPrefix.Length..].Split(':');
        if (rest.Length != 2 || rest[0].Length == 0 || rest[1].Length == 0) {
            return Reject("its subject is not 'system:serviceaccount:{namespace}:{name}'");
        }

        return Result<ValidatedServiceAccount>.Success(new(trustedIssuer, rest[0], rest[1], expiresAt));
    }

    /// <summary>
    ///     The key the token's <c>kid</c> names, from the recorded key set.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An unknown <c>kid</c> is a refusal here and not a fetch.</b> The client-side
    ///     <c>SigningKeyCache</c> re-reads on an unknown key id because it is the party that would
    ///     otherwise break on a rotation; this runs inside a grain on an <i>unauthenticated</i> path,
    ///     where a fetch per unrecognised token is a denial-of-service amplifier pointed at the
    ///     tenant's own cluster. Rotation is handled by
    ///     <c>IManagedIdentityGrain.RefreshIssuerAsync</c>, which is a control-plane action rather
    ///     than something an attacker can provoke.
    /// </remarks>
    static Result<JsonDocument> FindKey(string keySetJson, string? keyId) {
        JsonDocument set;

        try {
            set = JsonDocument.Parse(keySetJson);
        } catch (JsonException) {
            return Result<JsonDocument>.Failure(
                ErrorCode.AuthorizationFailed,
                ManagedIdentityFailures.Exchange
            );
        }

        using (set) {
            if (!set.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array) {
                return Result<JsonDocument>.Failure(
                    ErrorCode.AuthorizationFailed,
                    ManagedIdentityFailures.Exchange
                );
            }

            foreach (var candidate in keys.EnumerateArray()) {
                if (keyId is not null && !string.Equals(Text(candidate, "kid"), keyId, StringComparison.Ordinal)) {
                    continue;
                }

                // Cloned out of the enclosing document so the caller can dispose it independently.
                return Result<JsonDocument>.Success(JsonDocument.Parse(candidate.GetRawText()));
            }
        }

        return Result<JsonDocument>.Failure(ErrorCode.AuthorizationFailed, ManagedIdentityFailures.Exchange);
    }

    /// <summary>
    ///     Whether the signature is good, for the algorithms a Kubernetes API server actually signs
    ///     service-account tokens with.
    /// </summary>
    /// <remarks>
    ///     ⚠ Everything not named here — including <c>none</c>, including every <c>HS*</c> — falls to
    ///     the default arm and is refused. There is no arm that returns <see langword="true" />
    ///     without verifying, and there must never be one.
    /// </remarks>
    static bool Verify(string? algorithm, JsonElement key, byte[] signed, byte[] signature) =>
        algorithm switch {
            "RS256" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA256),
            "RS384" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA384),
            "RS512" => VerifyRsa(key, signed, signature, HashAlgorithmName.SHA512),
            "ES256" => VerifyEcdsa(key, signed, signature, HashAlgorithmName.SHA256, ECCurve.NamedCurves.nistP256),
            "ES384" => VerifyEcdsa(key, signed, signature, HashAlgorithmName.SHA384, ECCurve.NamedCurves.nistP384),
            "ES512" => VerifyEcdsa(key, signed, signature, HashAlgorithmName.SHA512, ECCurve.NamedCurves.nistP521),
            _ => false
        };

    static bool VerifyRsa(JsonElement key, byte[] signed, byte[] signature, HashAlgorithmName hash) {
        if (DecodeBytes(Text(key, "n")) is not { } modulus || DecodeBytes(Text(key, "e")) is not { } exponent) {
            return false;
        }

        using var rsa = RSA.Create();

        try {
            rsa.ImportParameters(new() { Modulus = modulus, Exponent = exponent });
        } catch (CryptographicException) {
            return false;
        }

        return rsa.VerifyData(signed, signature, hash, RSASignaturePadding.Pkcs1);
    }

    static bool VerifyEcdsa(
        JsonElement key,
        byte[] signed,
        byte[] signature,
        HashAlgorithmName hash,
        ECCurve curve
    ) {
        if (DecodeBytes(Text(key, "x")) is not { } x || DecodeBytes(Text(key, "y")) is not { } y) {
            return false;
        }

        // ⚠ The curve comes from the ALGORITHM, not from the key's own `crv`. A key claiming P-256
        // under an ES512 header would otherwise pick the header's hash and the key's curve, which is
        // a mismatch a verifier should refuse rather than reconcile.
        try {
            using var ecdsa = ECDsa.Create(new ECParameters { Curve = curve, Q = new() { X = x, Y = y } });

            // .NET's default signature format for this overload is IEEE P1363 fixed-field
            // concatenation, which is exactly what a JWS carries — r‖s, not a DER sequence.
            return ecdsa.VerifyData(signed, signature, hash);
        } catch (CryptographicException) {
            return false;
        }
    }

    static Result<JsonDocument> Decode(string segment) {
        var bytes = DecodeBytes(segment);
        if (bytes is null) {
            return Result<JsonDocument>.Failure(ErrorCode.AuthorizationFailed, ManagedIdentityFailures.Exchange);
        }

        try {
            return Result<JsonDocument>.Success(JsonDocument.Parse(bytes));
        } catch (JsonException) {
            return Result<JsonDocument>.Failure(ErrorCode.AuthorizationFailed, ManagedIdentityFailures.Exchange);
        }
    }

    static byte[]? DecodeBytes(string? segment) {
        if (segment is null) {
            return null;
        }

        try {
            return Base64Url.DecodeFromChars(segment);
        } catch (FormatException) {
            return null;
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static DateTimeOffset? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    ///     The uniform refusal. ⚠ <paramref name="because" /> is <b>not</b> in the message — it is
    ///     here so the reason is written down next to the branch that produced it, and so a future
    ///     audit event has something to carry. The caller is an unauthenticated workload and every
    ///     refusal it sees is identical.
    /// </summary>
    static Result<ValidatedServiceAccount> Reject(string because) {
        _ = because;
        return Result<ValidatedServiceAccount>.Failure(
            ErrorCode.AuthorizationFailed,
            ManagedIdentityFailures.Exchange
        );
    }
}
