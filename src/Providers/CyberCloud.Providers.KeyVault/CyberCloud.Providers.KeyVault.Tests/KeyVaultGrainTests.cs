using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Providers.KeyVault.Tests;

/// <summary>
///     The vault's own rules, asked of the real grain in an in-memory silo: versions, the validity
///     window, the recovery window, purge protection, and what the durable tier ends up holding.
/// </summary>
public sealed class KeyVaultGrainTests(VaultSilo silo) : IClassFixture<VaultSilo> {
    [Fact]
    public async Task EverySetIsANewVersionAndAnOldVersionStillReadsBack() {
        var (_, vault) = await silo.OpenVaultAsync();

        var first = await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "db-password", value = "hunter2" });
        var second = await vault.OkAsync(
            KeyVaults.SetSecretAction,
            new { secretName = "db-password", value = "correct horse", contentType = "text/plain" }
        );

        var v1 = first.GetProperty("version").GetString()!;
        var v2 = second.GetProperty("version").GetString()!;
        v1.ShouldNotBe(v2);
        v1.ShouldMatch(KeyVaults.VersionPattern);
        first.TryGetProperty("value", out _).ShouldBeFalse("a set answers with metadata and never echoes the value");

        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "db-password" }))
            .GetProperty("value").GetString().ShouldBe("correct horse");
        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "DB-PASSWORD", version = v1 }))
            .GetProperty("value").GetString().ShouldBe("hunter2", "names are case-insensitive, as Azure's are");

        var versions = await vault.OkAsync(KeyVaults.ListSecretVersionsAction, new { secretName = "db-password" });
        versions.GetProperty("count").GetInt32().ShouldBe(2);
        versions.GetProperty("items")[0].GetString()!.ShouldStartWith("db-password " + v2, customMessage: "newest first");
        versions.GetProperty("items")[1].GetString()!.ShouldStartWith("db-password " + v1);
    }

    [Fact]
    public async Task ADisabledOrExpiredVersionIsRefusedAndTheOthersAreNot() {
        var (_, vault) = await silo.OpenVaultAsync();
        var now = VaultSilo.Clock.UtcNow;

        var old = await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "token", value = "a" });
        var expiring = await vault.OkAsync(
            KeyVaults.SetSecretAction,
            new { secretName = "token", value = "b", expiresOn = KeyVaults.Timestamp(now.AddHours(1)) }
        );

        await vault.OkAsync(
            KeyVaults.UpdateSecretAction,
            new { secretName = "token", version = old.GetProperty("version").GetString(), enabled = false }
        );

        var disabled = await vault.RunAsync(KeyVaults.GetSecretAction, new { secretName = "token", version = old.GetProperty("version").GetString() });
        disabled.Error!.Code.ShouldBe(ErrorCode.Conflict);
        disabled.Error.Message.ShouldContain("disabled");

        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "token" })).GetProperty("value").GetString().ShouldBe("b");

        VaultSilo.Clock.Advance(TimeSpan.FromHours(2));

        var expired = await vault.RunAsync(KeyVaults.GetSecretAction, new { secretName = "token" });
        expired.Error!.Code.ShouldBe(ErrorCode.Conflict);
        expired.Error.Message.ShouldContain("expired");
        expiring.GetProperty("expiresOn").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task ADeletedSecretIsGoneFromReadsRecoverableAndPurgeableWithoutProtection() {
        var (_, vault) = await silo.OpenVaultAsync();

        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "api", value = "v1" });
        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "api", value = "v2" });

        var deleted = await vault.OkAsync(KeyVaults.DeleteSecretAction, new { secretName = "api" });
        DateTimeOffset.Parse(deleted.GetProperty("scheduledPurgeDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(VaultSilo.Clock.UtcNow.AddDays(7), TimeSpan.FromSeconds(1));

        (await vault.RunAsync(KeyVaults.GetSecretAction, new { secretName = "api" })).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await vault.OkAsync(KeyVaults.ListSecretsAction)).GetProperty("items").EnumerateArray().ShouldNotContain(x => x.GetString()!.StartsWith("api ", StringComparison.Ordinal));
        (await vault.OkAsync(KeyVaults.ListDeletedSecretsAction)).GetProperty("items")[0].GetString()!.ShouldStartWith("api deleted ");

        var again = await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "api", value = "v3" });
        again.Error!.Code.ShouldBe(ErrorCode.Conflict, "a deleted name is held for its window, as Azure holds it");

        await vault.OkAsync(KeyVaults.RecoverDeletedSecretAction, new { secretName = "api" });
        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "api" })).GetProperty("value").GetString().ShouldBe("v2");
        (await vault.OkAsync(KeyVaults.ListSecretVersionsAction, new { secretName = "api" })).GetProperty("count").GetInt32().ShouldBe(2, "every version came back");

        await vault.OkAsync(KeyVaults.DeleteSecretAction, new { secretName = "api" });
        (await vault.OkAsync(KeyVaults.PurgeDeletedSecretAction, new { secretName = "api" })).GetProperty("purged").GetBoolean().ShouldBeTrue();
        (await vault.RunAsync(KeyVaults.RecoverDeletedSecretAction, new { secretName = "api" })).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task PurgeProtectionRefusesEveryPurgeAndTheWindowEndsItAnyway() {
        var (id, vault) = await silo.OpenVaultAsync(purgeProtection: true);

        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "signing-seed", value = "s" });
        await vault.OkAsync(KeyVaults.CreateKeyAction, new { keyName = "signing", kty = "EC" });
        await vault.OkAsync(KeyVaults.DeleteSecretAction, new { secretName = "signing-seed" });
        await vault.OkAsync(KeyVaults.DeleteKeyAction, new { keyName = "signing" });

        var secretPurge = await vault.RunAsync(KeyVaults.PurgeDeletedSecretAction, new { secretName = "signing-seed" });
        secretPurge.Error!.Code.ShouldBe(ErrorCode.Conflict);
        secretPurge.Error.Message.ShouldContain("purge protection");
        (await vault.RunAsync(KeyVaults.PurgeDeletedKeyAction, new { keyName = "signing" })).Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ⚠ And the flag cannot be taken off to get round it.
        var off = await vault.OpenAsync(new() { VaultId = id, PurgeProtection = false });
        off.Error!.Code.ShouldBe(ErrorCode.Conflict);

        // Still recoverable inside the window, protection or not.
        (await vault.OkAsync(KeyVaults.ListDeletedKeysAction)).GetProperty("count").GetInt32().ShouldBe(1);

        VaultSilo.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

        // The next call, whatever it is, drops what is past its date.
        (await vault.OkAsync(KeyVaults.ListDeletedSecretsAction)).GetProperty("count").GetInt32().ShouldBe(0);
        (await vault.OkAsync(KeyVaults.ListDeletedKeysAction)).GetProperty("count").GetInt32().ShouldBe(0);
        (await vault.RunAsync(KeyVaults.RecoverDeletedKeyAction, new { keyName = "signing" })).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await vault.DescribeAsync()).GetValueOrThrow().DeletedCount.ShouldBe(0);
    }

    [Fact]
    public async Task AWrappedKeyUnwrapsAndAnEcKeyRefusesToWrap() {
        var (_, vault) = await silo.OpenVaultAsync();

        var key = await vault.OkAsync(KeyVaults.CreateKeyAction, new { keyName = "kek", kty = "RSA", keySize = 3072 });
        key.GetProperty("keySize").GetInt32().ShouldBe(3072);
        key.TryGetProperty("pkcs8", out _).ShouldBeFalse();

        var dataKey = RandomNumberGenerator.GetBytes(32);

        foreach (var algorithm in KeyVaults.EncryptionAlgorithms) {
            var wrapped = await vault.OkAsync(KeyVaults.WrapKeyAction, new { keyName = "kek", alg = algorithm, value = VaultCrypto.Encode(dataKey) });
            VaultCrypto.Decode(wrapped.GetProperty("value").GetString()!).Length.ShouldBe(384);

            var unwrapped = await vault.OkAsync(KeyVaults.UnwrapKeyAction, new { keyName = "kek", alg = algorithm, value = wrapped.GetProperty("value").GetString() });
            VaultCrypto.Decode(unwrapped.GetProperty("value").GetString()!).ShouldBe(dataKey);
        }

        await vault.OkAsync(KeyVaults.CreateKeyAction, new { keyName = "signing", kty = "EC", curve = "P-384" });
        var refused = await vault.RunAsync(KeyVaults.WrapKeyAction, new { keyName = "signing", alg = "RSA-OAEP", value = VaultCrypto.Encode(dataKey) });
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict, "an EC key permits sign and verify only, so wrapKey is not one of its operations");
    }

    [Fact]
    public async Task AnExpiredKeyStopsSigningAndStillVerifiesWhatItSigned() {
        var (_, vault) = await silo.OpenVaultAsync();
        var digest = SHA256.HashData("payload"u8);

        await vault.OkAsync(
            KeyVaults.CreateKeyAction,
            new { keyName = "release", kty = "EC", expiresOn = KeyVaults.Timestamp(VaultSilo.Clock.UtcNow.AddDays(1)) }
        );

        var signed = await vault.OkAsync(KeyVaults.SignAction, new { keyName = "release", alg = "ES256", digest = VaultCrypto.Encode(digest) });

        VaultSilo.Clock.Advance(TimeSpan.FromDays(2));

        (await vault.RunAsync(KeyVaults.SignAction, new { keyName = "release", alg = "ES256", digest = VaultCrypto.Encode(digest) }))
            .Error!.Message.ShouldContain("expired");

        (await vault.OkAsync(
                KeyVaults.VerifyAction,
                new { keyName = "release", alg = "ES256", digest = VaultCrypto.Encode(digest), signature = signed.GetProperty("value").GetString() }
            )).GetProperty("value").GetBoolean().ShouldBeTrue("an expired key still verifies what it signed while it was valid");
    }

    [Fact]
    public async Task AKeyOperationTheKeyDoesNotPermitIsRefused() {
        var (_, vault) = await silo.OpenVaultAsync();

        await vault.OkAsync(
            KeyVaults.CreateKeyAction,
            Calls.Json(("keyName", "encrypt-only"), ("kty", "RSA"), ("keyOps", new System.Text.Json.Nodes.JsonArray("encrypt", "decrypt")))
        );

        (await vault.RunAsync(KeyVaults.SignAction, new { keyName = "encrypt-only", alg = "PS256", digest = VaultCrypto.Encode(new byte[32]) }))
            .Error!.Message.ShouldContain("does not permit sign");

        var ec = await vault.RunAsync(
            KeyVaults.CreateKeyAction,
            Calls.Json(("keyName", "ec-encrypt"), ("kty", "EC"), ("keyOps", new System.Text.Json.Nodes.JsonArray("encrypt")))
        );
        ec.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task AnImportedKeyIsTheKeyThatWasImported() {
        var (_, vault) = await silo.OpenVaultAsync();
        using var rsa = RSA.Create(2048);

        var imported = await vault.OkAsync(
            KeyVaults.ImportKeyAction,
            new { keyName = "byok", pkcs8 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()) }
        );

        imported.GetProperty("imported").GetBoolean().ShouldBeTrue();
        VaultCrypto.Decode(imported.GetProperty("n").GetString()!).ShouldBe(rsa.ExportParameters(false).Modulus);

        // Encrypted outside the vault with the caller's own copy, decrypted inside with the imported one.
        var ciphertext = rsa.Encrypt("imported"u8.ToArray(), RSAEncryptionPadding.OaepSHA256);
        var decrypted = await vault.OkAsync(KeyVaults.DecryptAction, new { keyName = "byok", alg = "RSA-OAEP-256", value = VaultCrypto.Encode(ciphertext) });
        VaultCrypto.Decode(decrypted.GetProperty("value").GetString()!).ShouldBe("imported"u8.ToArray());

        using var small = RSA.Create(1024);
        (await vault.RunAsync(KeyVaults.ImportKeyAction, new { keyName = "weak", pkcs8 = Convert.ToBase64String(small.ExportPkcs8PrivateKey()) }))
            .Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task TheDurableStateHoldsNoValueAndNoKeyMaterial() {
        var (id, vault) = await silo.OpenVaultAsync();
        const string value = "a-plaintext-that-must-never-be-stored-9f3c";
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();

        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "canary", value });
        await vault.OkAsync(KeyVaults.ImportKeyAction, new { keyName = "canary-key", pkcs8 = Convert.ToBase64String(pkcs8) });

        var stored = await silo.StoredBytesAsync(id);
        var root = Convert.FromBase64String(VaultSilo.PlatformVault.Peek(KeyVaults.RootPath(VaultSilo.Tenant, id), KeyVaults.RootField)!);

        Contains(stored, Encoding.UTF8.GetBytes(value)).ShouldBeFalse("the secret's value is in the durable tier");
        Contains(stored, rsa.ExportParameters(true).D!).ShouldBeFalse("the private exponent is in the durable tier");
        Contains(stored, pkcs8[^64..]).ShouldBeFalse("the PKCS#8 document is in the durable tier");
        Contains(stored, root).ShouldBeFalse("the vault's root is in the durable tier");

        // And it is not merely absent: it is there, sealed, and opens under the root.
        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "canary" })).GetProperty("value").GetString().ShouldBe(value);
    }

    [Fact]
    public async Task AVaultWhoseRootIsGoneRefusesRatherThanServing() {
        var id = Guid.NewGuid();
        var vault = silo.Vault(id);
        (await vault.OpenAsync(new() { VaultId = id })).IsSuccess.ShouldBeTrue();

        // No root was ever minted for this vault.
        var refused = await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "x", value = "y" });
        refused.IsFailure.ShouldBeTrue();
        (await vault.OkAsync(KeyVaults.ListSecretsAction)).GetProperty("count").GetInt32().ShouldBe(0, "nothing was stored unsealed");
    }

    [Fact]
    public async Task ASealedVaultKeepsItsContentsAndADestroyedOneDoesNot() {
        var (id, vault) = await silo.OpenVaultAsync();
        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "kept", value = "v" });

        (await vault.SealAsync()).GetValueOrThrow().IsSealed.ShouldBeTrue();
        (await vault.RunAsync(KeyVaults.ListSecretsAction)).Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await vault.OpenAsync(new() { VaultId = id })).GetValueOrThrow().SecretCount.ShouldBe(1);
        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "kept" })).GetProperty("value").GetString().ShouldBe("v");

        var destroyed = (await vault.DestroyAsync()).GetValueOrThrow();
        destroyed.IsDestroyed.ShouldBeTrue();
        destroyed.SecretCount.ShouldBe(0);
        (await vault.OpenAsync(new() { VaultId = id })).Error!.Code.ShouldBe(ErrorCode.Conflict, "a purged vault never reopens");
    }

    [Fact]
    public async Task AnUpdateThatMovesOneEndOfTheWindowPastTheOtherIsRefused() {
        // ⚠ The #30 review: Times checked a body carrying both ends, and an update may carry one.
        var (_, vault) = await silo.OpenVaultAsync();
        var now = VaultSilo.Clock.UtcNow;

        var set = await vault.OkAsync(
            KeyVaults.SetSecretAction,
            new { secretName = "windowed", value = "v", notBefore = KeyVaults.Timestamp(now.AddDays(1)) }
        );

        var version = set.GetProperty("version").GetString();

        var earlier = await vault.RunAsync(
            KeyVaults.UpdateSecretAction,
            new { secretName = "windowed", version, expiresOn = KeyVaults.Timestamp(now.AddHours(1)) }
        );

        earlier.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        earlier.Error.Target.ShouldBe("/expiresOn");

        var stored = await vault.OkAsync(KeyVaults.ListSecretVersionsAction, new { secretName = "windowed" });
        stored.GetProperty("items")[0].GetString()!.ShouldEndWith("expires never", customMessage: "a refused update changed the version anyway");

        await vault.OkAsync(
            KeyVaults.UpdateSecretAction,
            new { secretName = "windowed", version, expiresOn = KeyVaults.Timestamp(now.AddDays(2)) }
        );
    }

    [Fact]
    public async Task ASecretsSizeIsCountedInBytesNotCharacters() {
        var (_, vault) = await silo.OpenVaultAsync();

        // 10,000 characters of three bytes each: inside the schema's maxLength, three times the bytes.
        var wide = new string('€', 10_000);

        var refused = await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "wide", value = wide });

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("30000 bytes");

        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "narrow", value = new string('€', 8_000) });
    }

    [Fact]
    public async Task AVaultStopsGrowingAtItsVersionBoundAndAPurgeFreesIt() {
        var (_, vault) = await silo.OpenVaultAsync();

        for (var i = 0; i < KeyVaults.MaxVersionsPerVault; i++) {
            await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "rolling", value = "v" });
        }

        var full = await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "another", value = "v" });

        full.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded, "one Secrets Officer could otherwise grow one durable row without limit");

        await vault.OkAsync(KeyVaults.DeleteSecretAction, new { secretName = "rolling" });

        (await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "another", value = "v" }))
            .Error!.Code.ShouldBe(ErrorCode.QuotaExceeded, "a deleted item's ciphertext is still in the row until it is purged");

        await vault.OkAsync(KeyVaults.PurgeDeletedSecretAction, new { secretName = "rolling" });
        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "another", value = "v" });
    }

    [Fact]
    public async Task AVaultStopsGrowingAtItsByteBound() {
        var (_, vault) = await silo.OpenVaultAsync();
        var largest = new string('x', KeyVaults.MaxSecretLength);
        var fitted = 0;

        while (true) {
            var set = await vault.RunAsync(KeyVaults.SetSecretAction, new { secretName = "large", value = largest });

            if (set.IsFailure) {
                set.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
                break;
            }

            fitted++;
            fitted.ShouldBeLessThan(KeyVaults.MaxVersionsPerVault, "the byte bound never engaged");
        }

        // About 163: 4 MiB over 25,600 bytes, less each seal's nonce and tag.
        var bound = KeyVaults.MaxSealedBytesPerVault / KeyVaults.MaxSecretLength;
        fitted.ShouldBeInRange(bound - 1, bound);
    }

    static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
