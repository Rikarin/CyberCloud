using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Providers.KeyVault;

/// <summary>A key's public half, as JWK spells it — base64url, no padding.</summary>
/// <param name="Kty"><c>RSA</c> or <c>EC</c>.</param>
/// <param name="KeySize">An RSA modulus in bits; zero for EC.</param>
/// <param name="Curve">An EC curve's JOSE name; empty for RSA.</param>
/// <param name="N">An RSA modulus.</param>
/// <param name="E">An RSA public exponent.</param>
/// <param name="X">An EC x coordinate.</param>
/// <param name="Y">An EC y coordinate.</param>
public sealed record PublicHalf(string Kty, int KeySize, string Curve, string N, string E, string X, string Y);

/// <summary>A private key as unencrypted PKCS#8 DER, and its public half.</summary>
/// <param name="Pkcs8">⚠ Plaintext key material. Sealed and zeroed by the caller; never stored as is.</param>
/// <param name="Public">The public half.</param>
public sealed record KeyMaterial(byte[] Pkcs8, PublicHalf Public);

/// <summary>
///     The vault's cryptography: sealing under the vault's root, and the RSA and EC operations the
///     data plane offers — all of it <c>System.Security.Cryptography</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Sealing is AES-256-GCM with a fresh 96-bit nonce and associated data that names the
///         item.</b> The stored form is <c>nonce ‖ ciphertext ‖ tag</c>. The associated data is
///         <c>{vaultId}/{secrets|keys}/{name}/{version}</c>, so a ciphertext moved to another item,
///         another version or another vault fails authentication rather than decrypting as the
///         wrong thing — the attack a storage-level editor would otherwise have.
///         ⚠ A random nonce under one key is safe to about 2³² seals, which is far past what one
///         vault stores; the root is per vault, so the bound is too.
///     </para>
///     <para>
///         ⚠ <b>The public operations never unseal.</b> <see cref="Encrypt" /> and
///         <see cref="Verify" /> work from <see cref="PublicHalf" />, which is stored in the clear
///         because it is public; only decrypt, unwrap and sign reach the private half, and each of
///         them holds it for one call and zeroes it.
///     </para>
/// </remarks>
public static class VaultCrypto {
    const int NonceLength = 12;
    const int TagLength = 16;

    /// <summary>Seals a plaintext under a vault's root.</summary>
    /// <param name="root">The vault's 32-byte root.</param>
    /// <param name="plaintext">What to seal.</param>
    /// <param name="associatedData">What the ciphertext is bound to — <see cref="AssociatedData" />.</param>
    /// <returns><c>nonce ‖ ciphertext ‖ tag</c>.</returns>
    public static byte[] Seal(ReadOnlySpan<byte> root, ReadOnlySpan<byte> plaintext, string associatedData) {
        var sealedBytes = new byte[NonceLength + plaintext.Length + TagLength];
        var nonce = sealedBytes.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(root, TagLength);
        aes.Encrypt(
            nonce,
            plaintext,
            sealedBytes.AsSpan(NonceLength, plaintext.Length),
            sealedBytes.AsSpan(NonceLength + plaintext.Length, TagLength),
            Encoding.UTF8.GetBytes(associatedData)
        );

        return sealedBytes;
    }

    /// <summary>Opens what <see cref="Seal" /> produced.</summary>
    /// <param name="root">The vault's 32-byte root.</param>
    /// <param name="sealedBytes">The stored form.</param>
    /// <param name="associatedData">What the ciphertext must be bound to.</param>
    /// <returns>
    ///     The plaintext, or <see cref="ErrorCode.InternalError" /> when the ciphertext does not
    ///     authenticate — a different root, a different item, or tampering. ⚠ The caller zeroes it.
    /// </returns>
    public static Result<byte[]> Open(ReadOnlySpan<byte> root, byte[] sealedBytes, string associatedData) {
        ArgumentNullException.ThrowIfNull(sealedBytes);

        if (sealedBytes.Length < NonceLength + TagLength) {
            return Result<byte[]>.Failure(ErrorCode.InternalError, "A sealed item is shorter than its nonce and tag.");
        }

        var length = sealedBytes.Length - NonceLength - TagLength;
        var plaintext = new byte[length];

        try {
            using var aes = new AesGcm(root, TagLength);
            aes.Decrypt(
                sealedBytes.AsSpan(0, NonceLength),
                sealedBytes.AsSpan(NonceLength, length),
                sealedBytes.AsSpan(NonceLength + length, TagLength),
                plaintext,
                Encoding.UTF8.GetBytes(associatedData)
            );
        } catch (AuthenticationTagMismatchException) {
            // ⚠ No detail about which of the three it was, because the grain cannot know and a
            // message that guessed would send an operator after the wrong one.
            return Result<byte[]>.Failure(
                ErrorCode.InternalError,
                "A sealed item did not authenticate under the vault's root: the root changed, the "
                + "ciphertext was moved from another item, or it was altered in storage."
            );
        }

        return Result<byte[]>.Success(plaintext);
    }

    /// <summary>The associated data a sealed item is bound to.</summary>
    /// <param name="vaultId">The vault's resource GUID.</param>
    /// <param name="kind"><c>secrets</c> or <c>keys</c>.</param>
    /// <param name="name">The item's name, lower-cased — the dictionary key.</param>
    /// <param name="version">The version.</param>
    public static string AssociatedData(Guid vaultId, string kind, string name, string version) =>
        $"{vaultId:N}/{kind}/{name}/{version}";

    // ── Key generation and import ──────────────────────────────────────────────────────────────

    /// <summary>Generates an RSA key.</summary>
    /// <param name="keySize">The modulus in bits — one of <see cref="KeyVaults.RsaKeySizes" />.</param>
    public static KeyMaterial GenerateRsa(int keySize) {
        using var rsa = RSA.Create(keySize);
        return new(rsa.ExportPkcs8PrivateKey(), PublicOf(rsa));
    }

    /// <summary>Generates an EC key.</summary>
    /// <param name="curve">The curve's JOSE name — one of <see cref="KeyVaults.Curves" />.</param>
    public static KeyMaterial GenerateEc(string curve) {
        using var ec = ECDsa.Create(CurveOf(curve));
        return new(ec.ExportPkcs8PrivateKey(), PublicOf(ec, curve));
    }

    /// <summary>Reads an imported PKCS#8 private key, and refuses a type or size the vault does not offer.</summary>
    /// <param name="pkcs8">Unencrypted PKCS#8 DER.</param>
    /// <returns>The key re-exported by the runtime, so what is sealed is a normalised encoding.</returns>
    public static Result<KeyMaterial> Import(byte[] pkcs8) {
        ArgumentNullException.ThrowIfNull(pkcs8);

        // ⚠ Tried as RSA and then as EC rather than by reading the AlgorithmIdentifier by hand: the
        // runtime's own importer is the parser that decides, and a second parser in this file would
        // be a second opinion about DER nobody reviews.
        try {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out var read);

            if (read != pkcs8.Length) {
                return Refused("The PKCS#8 document has bytes after its end.");
            }

            return KeyVaults.RsaKeySizes.Contains(rsa.KeySize)
                ? Result<KeyMaterial>.Success(new(rsa.ExportPkcs8PrivateKey(), PublicOf(rsa)))
                : Refused(
                    $"An RSA key of {rsa.KeySize} bits is not offered; the sizes are "
                    + $"{string.Join(", ", KeyVaults.RsaKeySizes)}."
                );
        } catch (CryptographicException) {
            // Not RSA; try EC below.
        }

        try {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(pkcs8, out var read);

            if (read != pkcs8.Length) {
                return Refused("The PKCS#8 document has bytes after its end.");
            }

            var curve = CurveNameOf(ec.ExportParameters(false).Curve);

            return curve.Length == 0
                ? Refused("The EC key is not on P-256, P-384 or P-521.")
                : Result<KeyMaterial>.Success(new(ec.ExportPkcs8PrivateKey(), PublicOf(ec, curve)));
        } catch (CryptographicException) {
            return Refused("The value is not an unencrypted PKCS#8 RSA or EC private key.");
        }

        static Result<KeyMaterial> Refused(string message) =>
            Result<KeyMaterial>.Failure(ErrorCode.InvalidRequestBody, message);
    }

    // ── Operations ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Encrypts with an RSA key's public half.</summary>
    /// <param name="key">The public half.</param>
    /// <param name="algorithm"><c>RSA-OAEP</c> or <c>RSA-OAEP-256</c>.</param>
    /// <param name="plaintext">What to encrypt.</param>
    public static Result<byte[]> Encrypt(PublicHalf key, string algorithm, byte[] plaintext) {
        ArgumentNullException.ThrowIfNull(key);

        try {
            using var rsa = RSA.Create(new RSAParameters { Modulus = Decode(key.N), Exponent = Decode(key.E) });
            return Result<byte[]>.Success(rsa.Encrypt(plaintext, Padding(algorithm)));
        } catch (CryptographicException) {
            return Result<byte[]>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The value is too long for {algorithm} under a {key.KeySize}-bit key."
            );
        }
    }

    /// <summary>Decrypts with an RSA key's private half.</summary>
    /// <param name="pkcs8">The unsealed private key. ⚠ The caller zeroes it.</param>
    /// <param name="algorithm"><c>RSA-OAEP</c> or <c>RSA-OAEP-256</c>.</param>
    /// <param name="ciphertext">What to decrypt.</param>
    public static Result<byte[]> Decrypt(byte[] pkcs8, string algorithm, byte[] ciphertext) {
        try {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return Result<byte[]>.Success(rsa.Decrypt(ciphertext, Padding(algorithm)));
        } catch (CryptographicException) {
            // ⚠ One message for every failure, because distinguishing a padding failure from a
            // length failure is the oracle OAEP exists to close.
            return Result<byte[]>.Failure(ErrorCode.InvalidRequestBody, "The value could not be decrypted with this key and algorithm.");
        }
    }

    /// <summary>Signs a digest with a private key.</summary>
    /// <param name="pkcs8">The unsealed private key. ⚠ The caller zeroes it.</param>
    /// <param name="key">The key's public half, which says what kind it is.</param>
    /// <param name="algorithm">A JWA signature algorithm the key's type and curve allow.</param>
    /// <param name="digest">The digest, of the algorithm's hash length.</param>
    /// <returns>The signature — r‖s for EC, as JWS spells it.</returns>
    public static byte[] Sign(byte[] pkcs8, PublicHalf key, string algorithm, byte[] digest) {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Kty == "RSA") {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa.SignHash(digest, HashOf(algorithm), SignaturePadding(algorithm));
        }

        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(pkcs8, out _);
        return ec.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Verifies a signature over a digest with a public half.</summary>
    /// <param name="key">The public half.</param>
    /// <param name="algorithm">A JWA signature algorithm the key's type and curve allow.</param>
    /// <param name="digest">The digest.</param>
    /// <param name="signature">The signature — r‖s for EC.</param>
    public static bool Verify(PublicHalf key, string algorithm, byte[] digest, byte[] signature) {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Kty == "RSA") {
            using var rsa = RSA.Create(new RSAParameters { Modulus = Decode(key.N), Exponent = Decode(key.E) });
            return rsa.VerifyHash(digest, signature, HashOf(algorithm), SignaturePadding(algorithm));
        }

        using var ec = ECDsa.Create(
            new ECParameters { Curve = CurveOf(key.Curve), Q = new() { X = Decode(key.X), Y = Decode(key.Y) } }
        );

        return ec.VerifyHash(digest, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    ///     Why an algorithm cannot be used with a key, or <see langword="null" /> when it can.
    /// </summary>
    /// <param name="key">The key's public half.</param>
    /// <param name="algorithm">A JWA signature algorithm.</param>
    /// <remarks>
    ///     ⚠ ES256 is P-256 and only P-256: JWA ties each ES algorithm to one curve, and signing a
    ///     SHA-256 digest with a P-521 key produces a signature no verifier following RFC 7518 accepts.
    /// </remarks>
    public static string? SignatureMismatch(PublicHalf key, string algorithm) {
        ArgumentNullException.ThrowIfNull(key);

        return algorithm switch {
            "ES256" when key.Curve != "P-256" => "ES256 needs an EC key on P-256.",
            "ES384" when key.Curve != "P-384" => "ES384 needs an EC key on P-384.",
            "ES512" when key.Curve != "P-521" => "ES512 needs an EC key on P-521.",
            ['R' or 'P', 'S', ..] when key.Kty != "RSA" => $"{algorithm} needs an RSA key.",
            _ => null
        };
    }

    /// <summary>The digest length, in bytes, an algorithm signs.</summary>
    /// <param name="algorithm">A JWA signature algorithm.</param>
    public static int DigestLength(string algorithm) =>
        algorithm[^3..] switch {
            "256" => 32,
            "384" => 48,
            _ => 64
        };

    // ── Base64url ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Encodes as base64url without padding.</summary>
    /// <param name="bytes">The bytes.</param>
    public static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    /// <summary>Decodes base64url, padded or not.</summary>
    /// <param name="text">The text.</param>
    /// <exception cref="FormatException">The text is not base64url.</exception>
    public static byte[] Decode(string text) => Base64Url.DecodeFromChars(text.TrimEnd('='));

    /// <summary>Decodes base64url, or reports that it is not.</summary>
    /// <param name="text">The text.</param>
    /// <param name="bytes">The bytes, when it is.</param>
    public static bool TryDecode(string text, out byte[] bytes) {
        try {
            bytes = Decode(text);
            return true;
        } catch (FormatException) {
            bytes = [];
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static PublicHalf PublicOf(RSA rsa) {
        var parameters = rsa.ExportParameters(false);
        return new("RSA", rsa.KeySize, "", Encode(parameters.Modulus), Encode(parameters.Exponent), "", "");
    }

    static PublicHalf PublicOf(ECDsa ec, string curve) {
        var parameters = ec.ExportParameters(false);
        return new("EC", 0, curve, "", "", Encode(parameters.Q.X), Encode(parameters.Q.Y));
    }

    static ECCurve CurveOf(string curve) =>
        curve switch {
            "P-256" => ECCurve.NamedCurves.nistP256,
            "P-384" => ECCurve.NamedCurves.nistP384,
            "P-521" => ECCurve.NamedCurves.nistP521,
            _ => throw new ArgumentOutOfRangeException(nameof(curve), curve, "Not a curve this vault offers.")
        };

    /// <summary>The JOSE name of a curve, or empty when it is not one of the three.</summary>
    /// <remarks>
    ///     ⚠ Matched on the OID first and the friendly name second, because the two platforms spell
    ///     the friendly name differently — OpenSSL says <c>nistP256</c>, CNG says <c>ECDSA_P256</c> —
    ///     and a key on secp256k1 is 256 bits too, so the size alone would misfile it as P-256.
    /// </remarks>
    static string CurveNameOf(ECCurve curve) {
        var oid = curve.Oid?.Value ?? "";
        var name = curve.Oid?.FriendlyName ?? "";

        return (oid, name) switch {
            ("1.2.840.10045.3.1.7", _) or (_, "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1") => "P-256",
            ("1.3.132.0.34", _) or (_, "nistP384" or "ECDSA_P384" or "secp384r1") => "P-384",
            ("1.3.132.0.35", _) or (_, "nistP521" or "ECDSA_P521" or "secp521r1") => "P-521",
            _ => ""
        };
    }

    static RSAEncryptionPadding Padding(string algorithm) =>
        algorithm == "RSA-OAEP-256" ? RSAEncryptionPadding.OaepSHA256 : RSAEncryptionPadding.OaepSHA1;

    static RSASignaturePadding SignaturePadding(string algorithm) =>
        algorithm.StartsWith("PS", StringComparison.Ordinal) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;

    static HashAlgorithmName HashOf(string algorithm) =>
        DigestLength(algorithm) switch {
            32 => HashAlgorithmName.SHA256,
            48 => HashAlgorithmName.SHA384,
            _ => HashAlgorithmName.SHA512
        };

    /// <summary>The operations a key of a type may permit when the caller names none.</summary>
    /// <param name="kty"><c>RSA</c> or <c>EC</c>.</param>
    public static ImmutableArray<string> DefaultOperations(string kty) =>
        kty == "RSA" ? KeyVaults.KeyOperations : ["sign", "verify"];
}
