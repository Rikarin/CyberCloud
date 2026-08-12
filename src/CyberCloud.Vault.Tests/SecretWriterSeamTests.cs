using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     Which <see cref="ISecretWriter" /> a host ends up with, asserted on the real registrations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS IS A WRITE SEAM THAT BECAME PERMISSIVE — AND ON THIS SEAM
///         "PERMISSIVE" MEANS SILENTLY DOING NOTHING.</b> A resolver that went wrong hands back a
///         value or refuses. A writer that went wrong can report success and write nothing, and the
///         reconciler then renders a data plane against a credential that does not exist. On
///         <c>CyberCloud.Storage/accounts</c> that is an S3 gateway with no identities file, which
///         <c>weed/s3api/auth_credentials.go</c> treats as "authentication disabled" and answers as
///         an administrator.
///     </para>
///     <para>
///         ⚠ <b>Shaped after <c>VaultSeamWiringTests</c>, which is itself shaped after
///         <c>OtpSeamWiringTests</c>, because that suite exists because of a real defect.</b> Three
///         files claimed <c>UnavailableOtpDelivery</c> was what "every host in this repository" gets
///         while no host registered the seam at all. Every claim below is checked rather than
///         asserted in prose.
///     </para>
/// </remarks>
public sealed class SecretWriterSeamTests {
    static readonly VaultOptions Wired =
        new() { Address = "https://openbao.cc-vault.svc:8200", Role = "cc-silo" };

    // ── Failure class (f): the refusing default stays the default ─────────────────────────────

    [Fact]
    public void AHostThatOnlyAddsTheResourceManagerGetsTheRefusingWriter() {
        Writer(services => services.AddCyberCloudResourceManager())
            .ShouldBeOfType<UnavailableSecretWriter>();
    }

    [Fact]
    public void WiringOneHostDoesNotChangeWhatAnotherHostGets() {
        var wired = Writer(
            services => services.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(Wired)
        );

        var unwired = Writer(services => services.AddCyberCloudResourceManager());

        wired.ShouldBeOfType<OpenBaoSecretWriter>();
        unwired.ShouldBeOfType<UnavailableSecretWriter>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRealWriterWinsWhicheverOrderTheHostWritesTheTwoCallsIn(bool managerFirst) {
        Writer(services => Both(services, managerFirst)).ShouldBeOfType<OpenBaoSecretWriter>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptingInLeavesNoRefusingWriterBehindIt(bool managerFirst) {
        // ⚠ The count, not the resolution — the assertion that separates Replace from Add. A single
        // resolve returns the last descriptor either way; what Replace buys is that anything taking
        // IEnumerable<ISecretWriter> does not get a refusing one sitting behind the real one.
        var services = new ServiceCollection();
        Both(services, managerFirst);

        services.Count(x => x.ServiceType == typeof(ISecretWriter)).ShouldBe(1);
    }

    [Fact]
    public void OneCallInstallsBOTHHALVES() {
        // ⚠ A host that could read a credential it could not create would provision resources whose
        // data plane has no password and report success. The mint and the read are one integration,
        // against one address, under one role — so they are one call.
        var services = new ServiceCollection();
        services.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(Wired);

        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISecretResolver>().ShouldBeOfType<OpenBaoSecretResolver>();
        provider.GetRequiredService<ISecretWriter>().ShouldBeOfType<OpenBaoSecretWriter>();
    }

    // ── Failure class (f): and it refuses legibly rather than returning nothing ────────────────

    [Fact]
    public async Task TheRefusingWriterFailsRatherThanReportingAMintItDidNotMake() {
        var minted = await new UnavailableSecretWriter().MintAsync(
            "tenants/x/CyberCloud.Storage/accounts/y",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessKeyId"] = "AKIA" },
            TestContext.Current.CancellationToken
        );

        minted.IsFailure.ShouldBeTrue(
            "a no-op mint reports that the credential exists, and the reconciler then renders a data "
            + "plane against a secret nobody wrote"
        );
    }

    [Fact]
    public async Task TheRefusalNamesTheCallAndTheSectionAnOperatorHasToAdd() {
        var minted = await new UnavailableSecretWriter().MintAsync(
            "tenants/x/CyberCloud.Storage/accounts/y",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["accessKeyId"] = "AKIA" },
            TestContext.Current.CancellationToken
        );

        // The message is the whole product of this type. An operator meeting it at 03:00 has to be
        // able to act on it without reading the source.
        minted.Error!.Message.ShouldContain("AddOpenBaoSecretResolver");
        minted.Error.Message.ShouldContain("CyberCloud:Vault:Address");
        minted.Error.Message.ShouldContain("CyberCloud:Vault:Role");
    }

    [Fact]
    public void TheConfigurationBoundOverloadIsWhatAHostActuallyCalls() {
        // ⚠ Asserts the opt-in WORKS from a bare configuration section, which is the part that would
        // silently rot. CyberCloud.Gateway.Host is the caller — it composes the resource manager, and
        // a synchronous action runs in that process.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["CyberCloud:Vault:Address"] = "https://openbao.cc-vault.svc:8200",
                    ["CyberCloud:Vault:Role"] = "cc-silo"
                }
            )
            .Build();

        var options = new VaultOptions();
        configuration.GetSection(VaultOptions.SectionName).Bind(options);

        options.IsConfigured.ShouldBeTrue();

        var services = new ServiceCollection();
        services.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(options);

        services.BuildServiceProvider()
            .GetRequiredService<ISecretWriter>()
            .ShouldBeOfType<OpenBaoSecretWriter>();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("https://openbao.cc-vault.svc:8200", "")]
    [InlineData("", "cc-silo")]
    public void AnIncompleteSectionIsNotConfiguredAndTheHostLeavesTheSeamsAlone(string address, string role) {
        // ⚠ The conditional a host writes reads exactly this, the way SiloIdentityOptions.IsConfigured
        // gates AddCommunicationOtpDelivery. Half a section is not an opt-in — it is somebody who
        // started and stopped, and guessing the rest would point a silo at a vault nobody chose.
        new VaultOptions { Address = address, Role = role }.IsConfigured.ShouldBeFalse();
    }

    static ISecretWriter Writer(Action<IServiceCollection> compose) {
        var services = new ServiceCollection();
        compose(services);

        return services.BuildServiceProvider().GetRequiredService<ISecretWriter>();
    }

    static void Both(IServiceCollection services, bool managerFirst) {
        if (managerFirst) {
            services.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(Wired);
        } else {
            services.AddOpenBaoSecretResolver(Wired).AddCyberCloudResourceManager();
        }
    }
}
