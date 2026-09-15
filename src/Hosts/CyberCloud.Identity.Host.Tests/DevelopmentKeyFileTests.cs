using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The development key directory: refused outside Development, the same keys on a second start,
///     published as ES256, and ephemeral when unset. docs/plan/11 § Protocol.
/// </summary>
public sealed class DevelopmentKeyFileTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "cyc-identity-keys-tests", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RefusesToLoadOutsideDevelopment() {
        // ⚠ At construction — which is options configuration, which is start-up — and by name. A
        // production host with a key file on disk is a host whose second replica signs with a
        // different key and whose backups carry the signing key; docs/plan/11 § Protocol's key set
        // is the vault's, and the message says so.
        var thrown = Should.Throw<InvalidOperationException>(() =>
            new DevelopmentKeyFile(Microsoft.Extensions.Options.Options.Create(new IdentityHostOptions { DevelopmentKeyDirectory = directory }), TestEnvironment.Production)
        );

        thrown.Message.ShouldContain("CyberCloud.Vault");
        thrown.Message.ShouldContain(nameof(IdentityHostOptions.DevelopmentKeyDirectory));
        Directory.Exists(directory).ShouldBeFalse("nothing was written before the refusal");

        // And the whole server refuses with it: resolving OpenIddict's options runs IdentityHostKeys,
        // which needs the file.
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IHostEnvironment>(TestEnvironment.Production)
            .Configure<IdentityHostOptions>(x => x.DevelopmentKeyDirectory = directory)
            .AddIdentityHostOpenIddict()
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value)
            .Message.ShouldContain("CyberCloud.Vault");
    }

    [Fact]
    public void ASecondStartLoadsTheSameKeys() {
        var first = Options(directory);
        var second = Options(directory);

        // Both keys, because both matter: a persistent signing key beside an ephemeral encryption
        // key would still refuse every refresh cookie after a restart.
        var signingKey = (ECDsaSecurityKey)first.SigningCredentials.Single().Key;
        var signingKeyAgain = (ECDsaSecurityKey)second.SigningCredentials.Single().Key;

        signingKey.KeyId.ShouldNotBeNullOrEmpty();
        signingKeyAgain.KeyId.ShouldBe(signingKey.KeyId, "the second start minted a new signing key");
        signingKeyAgain.ECDsa.ExportSubjectPublicKeyInfo().ShouldBe(signingKey.ECDsa.ExportSubjectPublicKeyInfo());

        var encryptionKey = (SymmetricSecurityKey)first.EncryptionCredentials.Single().Key;
        var encryptionKeyAgain = (SymmetricSecurityKey)second.EncryptionCredentials.Single().Key;

        encryptionKeyAgain.Key.ShouldBe(encryptionKey.Key, "the second start minted a new encryption key");
        encryptionKey.Key.Length.ShouldBe(32);

        File.Exists(Path.Combine(directory, DevelopmentKeyFile.SigningKeyFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(directory, DevelopmentKeyFile.EncryptionKeyFileName)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(directory, DevelopmentKeyFile.SigningKeyFileName)).ShouldStartWith("-----BEGIN PRIVATE KEY-----");

        // A different directory is a different key — the file, not the process, is the identity.
        var elsewhere = Options(Path.Combine(directory, "other"));

        ((ECDsaSecurityKey)elsewhere.SigningCredentials.Single().Key).KeyId.ShouldNotBe(signingKey.KeyId);
    }

    [Fact]
    public void TheJwksPublishesTheFileKeyWithES256() {
        var options = Options(directory);

        // ⚠ The algorithm is the contract's on this path too. A file key registered with no
        // algorithm would default to whatever IdentityModel picks, and the gateway pins ES256.
        options.SigningCredentials.Single().Algorithm.ShouldBe(AccessTokenPolicy.SigningAlgorithm);
        options.SigningCredentials.Single().Key.ShouldBeOfType<ECDsaSecurityKey>()
            .ECDsa.ExportParameters(false).Curve.Oid.FriendlyName!.ShouldContain("256");

        options.EncryptionCredentials.Single().Alg.ShouldBe(SecurityAlgorithms.Aes256KW);
        options.EncryptionCredentials.Single().Enc.ShouldBe(SecurityAlgorithms.Aes256CbcHmacSha512);

        // The published key set is what GrantsOverHttpTests compares across a restart; here, that
        // the kid it will publish is the file's rather than a per-process value.
        var file = new DevelopmentKeyFile(Microsoft.Extensions.Options.Options.Create(new IdentityHostOptions { DevelopmentKeyDirectory = directory }), TestEnvironment.Development);

        file.LoadOrCreateSigningKey().KeyId.ShouldBe(((ECDsaSecurityKey)options.SigningCredentials.Single().Key).KeyId);
    }

    [Fact]
    public void UnsetMeansEphemeral() {
        var first = Options(string.Empty);
        var second = Options(string.Empty);

        // Two processes, two keys — as before this type existed, and what every other suite in this
        // project builds the host with. Still ES256, still one of each.
        first.SigningCredentials.Single().Algorithm.ShouldBe(AccessTokenPolicy.SigningAlgorithm);
        first.EncryptionCredentials.Count.ShouldBe(1);

        ((ECDsaSecurityKey)first.SigningCredentials.Single().Key).ECDsa.ExportSubjectPublicKeyInfo()
            .ShouldNotBe(((ECDsaSecurityKey)second.SigningCredentials.Single().Key).ECDsa.ExportSubjectPublicKeyInfo());

        Directory.Exists(directory).ShouldBeFalse();

        new DevelopmentKeyFile(Microsoft.Extensions.Options.Options.Create(new IdentityHostOptions()), TestEnvironment.Production)
            .IsConfigured.ShouldBeFalse("unset is allowed anywhere, and means ephemeral");
    }

    /// <summary>OpenIddict's options, as the host would configure them for a given key directory.</summary>
    static OpenIddictServerOptions Options(string keyDirectory) =>
        new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IHostEnvironment>(TestEnvironment.Development)
            .Configure<IdentityHostOptions>(x => x.DevelopmentKeyDirectory = keyDirectory)
            .AddIdentityHostOpenIddict()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<OpenIddictServerOptions>>()
            .Value;
}
