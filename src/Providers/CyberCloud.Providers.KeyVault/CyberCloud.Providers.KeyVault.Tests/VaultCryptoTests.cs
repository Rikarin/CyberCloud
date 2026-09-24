using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;

namespace CyberCloud.Providers.KeyVault.Tests;

/// <summary>
///     <see cref="VaultCrypto" /> against an implementation it shares no code with.
/// </summary>
/// <remarks>
///     ⚠ <b>BouncyCastle, because a round trip through one library proves nothing about the wire.</b>
///     Sign-then-verify with <c>System.Security.Cryptography</c> on both ends passes for a DER
///     signature where JWS wants r‖s, a PSS salt of the wrong length, or OAEP's MGF1 on the wrong
///     hash — each of which a real client would reject. BouncyCastle verifies what the vault signed
///     over the <i>message</i>, hashing it itself, and encrypts what the vault must decrypt.
/// </remarks>
public sealed class VaultCryptoTests {
    static readonly byte[] Message = "the release manifest, v1.2.3"u8.ToArray();

    [Theory]
    [InlineData("RS256", "SHA-256withRSA")]
    [InlineData("RS512", "SHA-512withRSA")]
    [InlineData("PS256", "SHA-256withRSAandMGF1")]
    [InlineData("PS384", "SHA-384withRSAandMGF1")]
    public void AnRsaSignatureVerifiesUnderBouncyCastle(string algorithm, string bouncyCastle) {
        var key = VaultCrypto.GenerateRsa(2048);
        var signature = VaultCrypto.Sign(key.Pkcs8, key.Public, algorithm, Digest(algorithm, Message));

        var verifier = SignerUtilities.GetSigner(bouncyCastle);
        verifier.Init(false, new RsaKeyParameters(false, Big(key.Public.N), Big(key.Public.E)));
        verifier.BlockUpdate(Message, 0, Message.Length);

        verifier.VerifySignature(signature).ShouldBeTrue($"BouncyCastle refused the vault's {algorithm} signature");
    }

    [Theory]
    [InlineData("ES256", "P-256", "SHA-256withPLAIN-ECDSA")]
    [InlineData("ES384", "P-384", "SHA-384withPLAIN-ECDSA")]
    [InlineData("ES512", "P-521", "SHA-512withPLAIN-ECDSA")]
    public void AnEcSignatureIsJwsShapedAndVerifiesUnderBouncyCastle(string algorithm, string curve, string bouncyCastle) {
        var key = VaultCrypto.GenerateEc(curve);
        var signature = VaultCrypto.Sign(key.Pkcs8, key.Public, algorithm, Digest(algorithm, Message));

        // PLAIN-ECDSA is r‖s, which is what JWS (RFC 7518 § 3.4) requires and what DER is not.
        var verifier = SignerUtilities.GetSigner(bouncyCastle);
        verifier.Init(false, EcPublic(key.Public));
        verifier.BlockUpdate(Message, 0, Message.Length);

        verifier.VerifySignature(signature).ShouldBeTrue($"BouncyCastle refused the vault's {algorithm} signature");
        VaultCrypto.Verify(key.Public, algorithm, Digest(algorithm, Message), signature).ShouldBeTrue();
    }

    [Fact]
    public void ABouncyCastleSignatureVerifiesInTheVault() {
        var key = VaultCrypto.GenerateEc("P-256");

        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(key.Pkcs8, out _);
        var d = new BigInteger(1, ec.ExportParameters(true).D);

        var signer = SignerUtilities.GetSigner("SHA-256withPLAIN-ECDSA");
        var named = ECNamedCurveTable.GetByName("P-256");
        signer.Init(true, new ECPrivateKeyParameters(d, new ECDomainParameters(named)));
        signer.BlockUpdate(Message, 0, Message.Length);
        var signature = signer.GenerateSignature();

        VaultCrypto.Verify(key.Public, "ES256", SHA256.HashData(Message), signature).ShouldBeTrue();
        signature[5] ^= 1;
        VaultCrypto.Verify(key.Public, "ES256", SHA256.HashData(Message), signature).ShouldBeFalse();
    }

    [Theory]
    [InlineData("RSA-OAEP")]
    [InlineData("RSA-OAEP-256")]
    public void WhatBouncyCastleEncryptsTheVaultDecrypts(string algorithm) {
        var key = VaultCrypto.GenerateRsa(2048);
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var oaep = algorithm == "RSA-OAEP-256"
            ? new OaepEncoding(new RsaEngine(), new Sha256Digest(), new Sha256Digest(), null)
            : new OaepEncoding(new RsaEngine(), new Sha1Digest(), new Sha1Digest(), null);

        oaep.Init(true, new RsaKeyParameters(false, Big(key.Public.N), Big(key.Public.E)));
        var wrapped = oaep.ProcessBlock(dataKey, 0, dataKey.Length);

        VaultCrypto.Decrypt(key.Pkcs8, algorithm, wrapped).GetValueOrThrow().ShouldBe(dataKey);
    }

    [Fact]
    public void ASealedItemMovedToAnotherItemDoesNotOpen() {
        var root = RandomNumberGenerator.GetBytes(32);
        var vault = Guid.NewGuid();
        var sealedBytes = VaultCrypto.Seal(root, "value"u8, VaultCrypto.AssociatedData(vault, "secrets", "a", "v1"));

        VaultCrypto.Open(root, sealedBytes, VaultCrypto.AssociatedData(vault, "secrets", "a", "v1")).GetValueOrThrow().ShouldBe("value"u8.ToArray());
        VaultCrypto.Open(root, sealedBytes, VaultCrypto.AssociatedData(vault, "secrets", "b", "v1")).IsFailure.ShouldBeTrue("moved to another name");
        VaultCrypto.Open(root, sealedBytes, VaultCrypto.AssociatedData(Guid.NewGuid(), "secrets", "a", "v1")).IsFailure.ShouldBeTrue("moved to another vault");
        VaultCrypto.Open(RandomNumberGenerator.GetBytes(32), sealedBytes, VaultCrypto.AssociatedData(vault, "secrets", "a", "v1")).IsFailure.ShouldBeTrue("another root");

        sealedBytes[14] ^= 1;
        VaultCrypto.Open(root, sealedBytes, VaultCrypto.AssociatedData(vault, "secrets", "a", "v1")).IsFailure.ShouldBeTrue("altered in storage");
    }

    [Fact]
    public void AnImportRefusesAKeyTheVaultDoesNotOffer() {
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        VaultCrypto.Import(brainpool.ExportPkcs8PrivateKey()).Error!.Message.ShouldContain("P-256, P-384 or P-521");

        VaultCrypto.Import([1, 2, 3]).Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        VaultCrypto.Import(p384.ExportPkcs8PrivateKey()).GetValueOrThrow().Public.Curve.ShouldBe("P-384");
    }

    [Fact]
    public void AnAlgorithmIsTiedToItsCurve() {
        var p521 = VaultCrypto.GenerateEc("P-521").Public;

        VaultCrypto.SignatureMismatch(p521, "ES256").ShouldNotBeNull();
        VaultCrypto.SignatureMismatch(p521, "RS256").ShouldNotBeNull();
        VaultCrypto.SignatureMismatch(p521, "ES512").ShouldBeNull();
    }

    static byte[] Digest(string algorithm, byte[] message) =>
        VaultCrypto.DigestLength(algorithm) switch {
            32 => SHA256.HashData(message),
            48 => SHA384.HashData(message),
            _ => SHA512.HashData(message)
        };

    static BigInteger Big(string base64Url) => new(1, VaultCrypto.Decode(base64Url));

    static ECPublicKeyParameters EcPublic(PublicHalf key) {
        var named = ECNamedCurveTable.GetByName(key.Curve);
        var point = named.Curve.CreatePoint(Big(key.X), Big(key.Y));
        return new(point, new ECDomainParameters(named));
    }
}
