using CyberCloud.Authorization.Contracts;
using CyberCloud.Providers.KeyVault;
using CyberCloud.Providers.KeyVault.Contracts;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The key vault's data plane over HTTP: the nine stages, the real resource manager deciding with
///     the real ReBAC schema, the real grain, and a real OpenBao holding each vault's root.
///     docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The owner of the vault is the caller refused, and that is the design rather than a
///         gap.</b> The fixture's owner holds <c>owner</c> on the tenant and creates every vault;
///         every data-plane action checks one of the six data-plane permissions, none of which is
///         rewritten from a control-plane role — so the owner gets <c>403</c> (they can read the vault,
///         so the refusal is honest rather than an oracle) until they grant a data-plane role, which
///         <c>assignRole</c> lets them do. Dana holds data-plane roles and nothing on the control plane.
///     </para>
///     <para>
///         ⚠ Tests share the fixture's vaults and use their own item names, so they are
///         independent of order.
///     </para>
/// </remarks>
/// <param name="gateway">The fixture.</param>
public sealed class KeyVaultOverTheGatewayTests(KeyVaultGateway gateway) : IClassFixture<KeyVaultGateway>, IAsyncLifetime {
    const string Vault = "app-secrets";
    const string Protected = "signing-keys";

    static bool seeded;
    static readonly SemaphoreSlim Seeding = new(1, 1);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        await Seeding.WaitAsync(TestContext.Current.CancellationToken);

        try {
            if (seeded) {
                return;
            }

            await gateway.CreateVaultAsync(Vault);
            await gateway.CreateVaultAsync(Protected, purgeProtection: true);

            // Dana: both Officer roles on both vaults, granted by the owner on each vault's own scope.
            foreach (var vault in new[] { Vault, Protected }) {
                await gateway.GrantAsync(KeyVaultGateway.VaultPath(vault), Relations.KeyVaultSecretsOfficer, SubjectTypes.User, gateway.Dana);
                await gateway.GrantAsync(KeyVaultGateway.VaultPath(vault), Relations.KeyVaultCryptoOfficer, SubjectTypes.User, gateway.Dana);
            }

            // The workload: Secrets User on one vault, and nothing else anywhere.
            await gateway.GrantAsync(KeyVaultGateway.VaultPath(Vault), Relations.KeyVaultSecretsUser, SubjectTypes.ManagedIdentity, gateway.Workload);

            seeded = true;
        } finally {
            Seeding.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    string Dana => gateway.Token(gateway.Dana);

    [Fact]
    public async Task TheOwnerOfTheVaultIsRefusedEveryDataPlaneAction() {
        var owner = gateway.Token(gateway.Owner);

        // ⚠ The owner can READ the vault — the control plane is theirs — which is what makes 403 the
        // honest answer rather than the enumeration oracle (docs/plan/07 § The enforcement seam).
        (await gateway.SendAsync(HttpMethod.Get, KeyVaultGateway.VaultPath(Vault), owner)).Status.ShouldBe(200);

        foreach (var (action, body) in new (string, object)[] {
                     (KeyVaults.SetSecretAction, new { secretName = "owner-try", value = "x" }),
                     (KeyVaults.GetSecretAction, new { secretName = "anything" }),
                     (KeyVaults.ListSecretsAction, new { }),
                     (KeyVaults.CreateKeyAction, new { keyName = "owner-key", kty = "EC" }),
                     (KeyVaults.SignAction, new { keyName = "anything", alg = "ES256", digest = VaultCrypto.Encode(new byte[32]) })
                 }) {
            var (status, response) = await gateway.ActionAsync(Vault, action, owner, body);

            status.ShouldBe(403, $"the vault's owner reached {action} with no data-plane role: {response}");
            response.GetProperty("error").GetProperty("code").GetString().ShouldBe("AuthorizationFailed");
        }
    }

    [Fact]
    public async Task EverySetIsAVersionAndTheHistoryReadsBackOverTheGateway() {
        var first = await Ok(Vault, KeyVaults.SetSecretAction, new { secretName = "db-password", value = "first", contentType = "text/plain" });
        var second = await Ok(Vault, KeyVaults.SetSecretAction, new { secretName = "db-password", value = "second" });

        var versions = await Ok(Vault, KeyVaults.ListSecretVersionsAction, new { secretName = "db-password" });
        versions.GetProperty("count").GetInt32().ShouldBe(2);
        versions.GetProperty("items")[0].GetString()!.ShouldContain(second.GetProperty("version").GetString()!);

        (await Ok(Vault, KeyVaults.GetSecretAction, new { secretName = "db-password" })).GetProperty("value").GetString().ShouldBe("second");

        var old = await Ok(Vault, KeyVaults.GetSecretAction, new { secretName = "db-password", version = first.GetProperty("version").GetString() });
        old.GetProperty("value").GetString().ShouldBe("first");
        old.GetProperty("contentType").GetString().ShouldBe("text/plain");
    }

    [Fact]
    public async Task ASoftDeletedSecretRecoversAndItsPurgeIsRefusedUnderProtection() {
        await Ok(Protected, KeyVaults.SetSecretAction, new { secretName = "release-token", value = "keep-me" });
        await Ok(Protected, KeyVaults.DeleteSecretAction, new { secretName = "release-token" });

        (await gateway.ActionAsync(Protected, KeyVaults.GetSecretAction, Dana, new { secretName = "release-token" })).Status.ShouldBe(404);
        (await Ok(Protected, KeyVaults.ListDeletedSecretsAction)).GetProperty("items").EnumerateArray()
            .ShouldContain(x => x.GetString()!.StartsWith("release-token deleted ", StringComparison.Ordinal));

        await Ok(Protected, KeyVaults.RecoverDeletedSecretAction, new { secretName = "release-token" });
        (await Ok(Protected, KeyVaults.GetSecretAction, new { secretName = "release-token" })).GetProperty("value").GetString().ShouldBe("keep-me");

        await Ok(Protected, KeyVaults.DeleteSecretAction, new { secretName = "release-token" });

        // ⚠ Dana holds purgeSecrets — the refusal is the vault's protection, not the role's absence.
        var (status, refused) = await gateway.ActionAsync(Protected, KeyVaults.PurgeDeletedSecretAction, Dana, new { secretName = "release-token" });
        status.ShouldBe(409, refused.ToString());
        refused.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("purge protection");

        // And the flag cannot be turned off to get round it: the resource manager refuses the write.
        var (off, offBody) = await gateway.SendAsync(
            HttpMethod.Put,
            KeyVaultGateway.VaultPath(Protected),
            gateway.Token(gateway.Owner),
            KeyVaults.Body(false, location: KeyVaultGateway.Region)
        );
        off.ShouldBe(409, offBody.ToString());

        // Without protection the same purge goes through.
        await Ok(Vault, KeyVaults.SetSecretAction, new { secretName = "scratch", value = "x" });
        await Ok(Vault, KeyVaults.DeleteSecretAction, new { secretName = "scratch" });
        (await Ok(Vault, KeyVaults.PurgeDeletedSecretAction, new { secretName = "scratch" })).GetProperty("purged").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AKeyWrapsAndUnwrapsADataKeyAndBouncyCastleOpensNothingItShouldNot() {
        await Ok(Vault, KeyVaults.CreateKeyAction, new { keyName = "kek", kty = "RSA", keySize = 2048 });
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await Ok(Vault, KeyVaults.WrapKeyAction, new { keyName = "kek", alg = "RSA-OAEP-256", value = VaultCrypto.Encode(dataKey) });
        var unwrapped = await Ok(Vault, KeyVaults.UnwrapKeyAction, new { keyName = "kek", alg = "RSA-OAEP-256", value = wrapped.GetProperty("value").GetString() });

        VaultCrypto.Decode(unwrapped.GetProperty("value").GetString()!).ShouldBe(dataKey);

        // Tampered ciphertext is refused, with the one message every OAEP failure gets.
        var tampered = VaultCrypto.Decode(wrapped.GetProperty("value").GetString()!);
        tampered[10] ^= 1;
        var (status, _) = await gateway.ActionAsync(Vault, KeyVaults.UnwrapKeyAction, Dana, new { keyName = "kek", alg = "RSA-OAEP-256", value = VaultCrypto.Encode(tampered) });
        status.ShouldBe(400);
    }

    [Theory]
    [InlineData("EC", "ES256", "SHA-256withPLAIN-ECDSA")]
    [InlineData("RSA", "PS256", "SHA-256withRSAandMGF1")]
    [InlineData("RSA", "RS256", "SHA-256withRSA")]
    public async Task ASignatureMadeOverTheGatewayVerifiesUnderBouncyCastle(string kty, string algorithm, string bouncyCastle) {
        var name = "sign-" + algorithm.ToLowerInvariant();
        await Ok(Vault, KeyVaults.CreateKeyAction, new { keyName = name, kty });

        var message = "release 1.2.3, sha256 of every artefact"u8.ToArray();
        var signed = await Ok(Vault, KeyVaults.SignAction, new { keyName = name, alg = algorithm, digest = VaultCrypto.Encode(SHA256.HashData(message)) });
        var signature = VaultCrypto.Decode(signed.GetProperty("value").GetString()!);

        // The public half is what getKey answered over the gateway, and nothing else.
        var key = await Ok(Vault, KeyVaults.GetKeyAction, new { keyName = name });

        var verifier = SignerUtilities.GetSigner(bouncyCastle);
        verifier.Init(false, PublicOf(key));
        verifier.BlockUpdate(message, 0, message.Length);
        verifier.VerifySignature(signature).ShouldBeTrue($"BouncyCastle refused a {algorithm} signature the vault made");

        (await Ok(
                Vault,
                KeyVaults.VerifyAction,
                new { keyName = name, alg = algorithm, digest = VaultCrypto.Encode(SHA256.HashData(message)), signature = signed.GetProperty("value").GetString() }
            )).GetProperty("value").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AnotherTenantsCallerIsAnsweredNotFound() {
        await Ok(Vault, KeyVaults.SetSecretAction, new { secretName = "cross-tenant", value = "mine" });

        // Mallory owns her own tenant outright and holds nothing in this one.
        var mallory = gateway.Token(gateway.Mallory, tenant: KeyVaultGateway.OtherTenant);

        foreach (var action in new[] { KeyVaults.GetSecretAction, KeyVaults.SetSecretAction }) {
            var (status, body) = await gateway.SendAsync(
                HttpMethod.Post,
                KeyVaultGateway.VaultPath(Vault) + "/" + action,
                mallory,
                new { secretName = "cross-tenant", value = "theirs" }
            );

            status.ShouldBe(404, $"another tenant's owner got {status} for {action}: {body}");
        }

        (await Ok(Vault, KeyVaults.GetSecretAction, new { secretName = "cross-tenant" })).GetProperty("value").GetString().ShouldBe("mine");
    }

    [Fact]
    public async Task AManagedIdentityWithSecretsUserReadsAndDoesNothingElse() {
        await Ok(Vault, KeyVaults.SetSecretAction, new { secretName = "connection-string", value = "Host=db;Password=p" });

        var workload = gateway.Token(gateway.Workload, SubjectTypes.ManagedIdentity);

        var (read, value) = await gateway.ActionAsync(Vault, KeyVaults.GetSecretAction, workload, new { secretName = "connection-string" });
        read.ShouldBe(200, value.ToString());
        value.GetProperty("value").GetString().ShouldBe("Host=db;Password=p");

        // ⚠ No control-plane role at all, so a refused write is 404 — the workload cannot even read
        // the vault resource, and a 403 would confirm it exists.
        (await gateway.ActionAsync(Vault, KeyVaults.SetSecretAction, workload, new { secretName = "connection-string", value = "overwritten" })).Status.ShouldBe(404);
        (await gateway.ActionAsync(Vault, KeyVaults.CreateKeyAction, workload, new { keyName = "wl", kty = "EC" })).Status.ShouldBe(404);
        (await gateway.SendAsync(HttpMethod.Get, KeyVaultGateway.VaultPath(Vault), workload)).Status.ShouldBe(404);

        // And its grant is on one vault, not the group.
        (await gateway.ActionAsync(Protected, KeyVaults.ListSecretsAction, workload)).Status.ShouldBe(404);
    }

    [Fact]
    public async Task TheRootIsInOpenBaoAndNowhereAResponseCanReach() {
        var vault = await gateway.SendAsync(HttpMethod.Get, KeyVaultGateway.VaultPath(Vault), gateway.Token(gateway.Owner));
        var id = await gateway.ResourceIdAsync(Vault);

        var stored = await gateway.ReadOpenBaoAsync(KeyVaults.RootPath(KeyVaultGateway.Tenant, id));
        stored.ShouldNotBeNull("the reconciler converged and OpenBao holds no root for the vault");
        Convert.FromBase64String(stored.Value.GetProperty(KeyVaults.RootField).GetString()!).Length.ShouldBe(32);

        vault.Body.ToString().ShouldNotContain(stored.Value.GetProperty(KeyVaults.RootField).GetString()!);
    }

    async Task<JsonElement> Ok(string vault, string action, object? body = null) {
        var (status, response) = await gateway.ActionAsync(vault, action, Dana, body);
        status.ShouldBe(200, $"{action} on '{vault}' answered {status}: {response}");
        return response;
    }

    /// <summary>A JWK read back from <c>getKey</c>, as BouncyCastle key parameters.</summary>
    static Org.BouncyCastle.Crypto.AsymmetricKeyParameter PublicOf(JsonElement key) {
        BigInteger Big(string name) => new(1, VaultCrypto.Decode(key.GetProperty(name).GetString()!));

        if (key.GetProperty("kty").GetString() == "RSA") {
            return new RsaKeyParameters(false, Big("n"), Big("e"));
        }

        var named = ECNamedCurveTable.GetByName(key.GetProperty("crv").GetString());
        return new ECPublicKeyParameters(named.Curve.CreatePoint(Big("x"), Big("y")), new ECDomainParameters(named));
    }
}
