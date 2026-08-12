using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     What <c>AddOpenBaoSecretResolver</c> refuses to compose, and when it refuses.
/// </summary>
/// <remarks>
///     ⚠ <b>At composition rather than at the first resolve, and the difference is who finds out.</b>
///     An unwired silo is a supported shape and answers with <c>UnavailableSecretResolver</c>'s
///     sentence. A silo that opted in and got the address wrong is not a supported shape, and
///     deferring that to the first resolve means finding out during a provision at 03:00 with a
///     message about a vault rather than at start-up with a message about a configuration key.
/// </remarks>
public sealed class VaultWiringValidationTests {
    [Fact]
    public void AVaultWithNoAddressIsRefusedAtComposition() {
        var thrown = Should.Throw<InvalidOperationException>(
            () => Compose(new() { Role = "cc-silo" })
        );

        thrown.Message.ShouldContain("CyberCloud:Vault:Address");
        thrown.Message.ShouldContain(
            "UnavailableSecretResolver",
            Case.Sensitive,
            "the message has to say what a silo with no vault should do instead, or the obvious fix "
            + "is to invent an address"
        );
    }

    [Fact]
    public void AVaultWithNoRoleIsRefusedAtComposition() {
        Should.Throw<InvalidOperationException>(
            () => Compose(new() { Address = "https://openbao.cc-vault.svc:8200" })
        ).Message.ShouldContain("CyberCloud:Vault:Role");
    }

    [Theory]
    [InlineData("openbao.cc-vault.svc:8200")]
    [InlineData("/v1/secret")]
    public void AnAddressThatIsNotAbsoluteIsRefused(string address) {
        Should.Throw<InvalidOperationException>(() => Compose(new() { Address = address, Role = "r" }));
    }

    [Fact]
    public void APlaintextAddressIsRefusedUnlessTheInsecureFlagIsSet() {
        // ⚠ docs/plan/18 § Platform security opens with "TLS 1.3 everywhere". A vault reached over
        // plaintext hands every secret it serves to anything on the path, and that is a worse
        // exposure than not having a vault at all — the unwired silo at least fails loudly.
        Should.Throw<InvalidOperationException>(
            () => Compose(new() { Address = "http://openbao.cc-vault.svc:8200", Role = "r" })
        ).Message.ShouldContain("AllowInsecureTransport");

        Should.NotThrow(
            () => Compose(
                new() {
                    Address = "http://openbao.cc-vault.svc:8200",
                    Role = "r",
                    AllowInsecureTransport = true,
                }
            )
        );
    }

    [Fact]
    public void AnHttpsAddressComposesCleanly() {
        Should.NotThrow(() => Compose(new() { Address = "https://openbao.cc-vault.svc:8200", Role = "r" }));
    }

    [Fact]
    public void TheConfigurationOverloadRefusesAnEmptySectionRatherThanComposingHalfAVault() {
        // ⚠ A host that calls AddOpenBaoSecretResolver() with nothing under CyberCloud:Vault has
        // asked for a vault and has not said where it is. The alternative — quietly falling back to
        // the refusing resolver — is worse than it sounds: the host wrote the call, so nobody would
        // look at it again, and the silo would refuse every secret while its wiring looked correct.
        var builder = new Builder(new ConfigurationBuilder().Build());

        Should.Throw<InvalidOperationException>(() => builder.AddOpenBaoSecretResolver());
    }

    static void Compose(VaultOptions options) => new Builder(null).AddOpenBaoSecretResolver(options);

    sealed class Builder(IConfiguration? configuration) : ISiloBuilder {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration ?? new ConfigurationBuilder().Build();
    }
}
