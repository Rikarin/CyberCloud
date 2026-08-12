using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     Which <see cref="ISecretResolver" /> a silo ends up with, asserted on the real registrations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS IS A SEAM THAT BECAME PERMISSIVE FOR A HOST THAT DID NOT ASK.</b>
///         <see cref="UnavailableSecretResolver" /> refuses by design, and its argument is that an
///         empty password reaching a rendered manifest is a database with no password reported as a
///         successful provision. <c>StubbedSeamTests.TheDefaultSecretResolverRefusesRatherThanReturningEmpty</c>
///         pins what it says. What is asserted here is the half that type cannot assert about
///         itself: that it is still what a silo <i>gets</i>. That breaks when somebody wiring one
///         host reaches for a convenience which changes the default for all of them.
///     </para>
///     <para>
///         ⚠ <b>Shaped after <c>OtpSeamWiringTests</c> on purpose, because that suite exists because
///         of a real defect.</b> Three files claimed <c>UnavailableOtpDelivery</c> was what "every
///         host in this repository" gets while no host registered the seam at all, so the message it
///         existed to hand an operator at 03:00 could not be produced. The claim was checked and was
///         false. Every claim below is checked rather than asserted in prose, including the
///         uncomfortable one — see
///         <see cref="NothingOutsideThisSuiteOptsIn" />.
///     </para>
///     <para>
///         ⚠ <b>No cluster, no grains, no silo started.</b> These are questions about a
///         <see cref="ServiceCollection" />, so they are asked of one.
///     </para>
/// </remarks>
public sealed class VaultSeamWiringTests {
    static readonly VaultOptions Wired = new() { Address = "https://openbao.cc-vault.svc:8200", Role = "cc-silo" };

    [Fact]
    public void ASiloThatOnlyAddsTheResourceManagerGetsTheRefusingResolver() {
        // ⚠ The default, and it must stay the default. This is every silo in the repository today:
        // CyberCloud.Silo.Host composes the resource manager and calls nothing from CyberCloud.Vault,
        // so the refusal is reachable and is what ReconcileDriver hands a reconciler.
        Resolver(silo => silo.AddCyberCloudResourceManager()).ShouldBeOfType<UnavailableSecretResolver>();
    }

    [Fact]
    public void WiringOneSiloDoesNotChangeWhatAnotherSiloGets() {
        // ⚠ THE ROW THAT WOULD CATCH THE TEMPTING FIX. "Nothing resolves a real secret" is repairable
        // two ways: wire the hosts that have a vault, or make the OpenBao resolver the default and
        // let the rest pick it up. The second is silent — a silo with no vault would then fail at the
        // first RESOLVE with a connection error naming an address that is not set, rather than with
        // the sentence UnavailableSecretResolver was written to hand an operator.
        var wired = Resolver(silo => silo.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(Wired));
        var unwired = Resolver(silo => silo.AddCyberCloudResourceManager());

        wired.ShouldBeOfType<OpenBaoSecretResolver>();
        unwired.ShouldBeOfType<UnavailableSecretResolver>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRealResolverWinsWhicheverOrderTheHostWritesTheTwoCallsIn(bool managerFirst) {
        Resolver(silo => Both(silo, managerFirst)).ShouldBeOfType<OpenBaoSecretResolver>(
            "a host that opted in must not be left with the refusing resolver whichever order it "
            + "wrote the two calls in"
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptingInLeavesNoRefusingResolverBehindIt(bool managerFirst) {
        // ⚠ THE ASSERTION THAT SEPARATES Replace FROM Add, AND THE ONLY ONE THAT DOES. Both orders
        // resolve the real thing under a plain Add — TryAdd is a no-op once any descriptor exists,
        // and a single resolve returns the LAST descriptor — so ShouldBeOfType above passes either
        // way. What Replace buys is the count, and the count is what anything taking
        // IEnumerable<ISecretResolver> or calling GetServices would see. Established by the OTP work
        // rather than reasoned about here.
        var services = Compose(silo => Both(silo, managerFirst));

        services.Count(x => x.ServiceType == typeof(ISecretResolver)).ShouldBe(
            1,
            "a host that opted in should have one ISecretResolver registration, not the real one "
            + "stacked on a refusing one that GetServices would still hand out"
        );
    }

    [Fact]
    public void AnUnwiredSiloResolvesItsResolverWithNoVaultOptionsInTheContainerAtAll() {
        // ⚠ NO VaultOptions HERE, AND THE ABSENCE IS THE ASSERTION. If the OpenBao resolver ever
        // became the default, this resolution throws at the first activation with a DI error naming
        // VaultOptions — a stack trace where UnavailableSecretResolver's sentence should be.
        var builder = new ServiceCollectionSiloBuilder();
        builder.AddCyberCloudResourceManager();

        builder.Services.Any(x => x.ServiceType == typeof(VaultOptions)).ShouldBeFalse(
            "AddCyberCloudResourceManager must not drag the vault module into a silo that did not "
            + "ask for it — module-layering.txt has no ResourceManager -> Vault line and could not"
        );

        builder.Services
            .BuildServiceProvider()
            .GetRequiredService<ISecretResolver>()
            .ShouldBeOfType<UnavailableSecretResolver>();
    }

    [Fact]
    public void NothingOutsideThisSuiteOptsIn() {
        // ⚠ THIS ROW RECORDS AN UNCOMFORTABLE FACT RATHER THAN A GUARANTEE, AND IT IS HERE BECAUSE
        // THE ALTERNATIVE IS A COMMENT NOBODY CHECKS.
        //
        // AddOpenBaoSecretResolver has no caller outside this test project. That is exactly the shape
        // UnavailableOtpDelivery was in — a seam whose real implementation nothing installs — with
        // one difference that matters: there, the REFUSING side was also unreachable, so the message
        // an operator needed could not be produced at all. Here the refusing side is what every silo
        // resolves and ReconcileDriver drives it on every provider, which the row above asserts.
        //
        // So what is owed is a host that calls this when CyberCloud:Vault is configured, next to the
        // AddCommunicationOtpDelivery line SiloIdentityOptions already has. This test does not assert
        // the absence — a gate that fails the moment somebody does the right thing is a gate that
        // gets deleted. It asserts that the opt-in WORKS from a bare configuration section, which is
        // the part that would silently rot while nothing called it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["CyberCloud:Vault:Address"] = "https://openbao.cc-vault.svc:8200",
                    ["CyberCloud:Vault:Role"] = "cc-silo",
                    ["CyberCloud:Vault:KvMountPath"] = "platform",
                }
            )
            .Build();

        var builder = new ServiceCollectionSiloBuilder(configuration);
        builder.AddCyberCloudResourceManager().AddOpenBaoSecretResolver();

        var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<ISecretResolver>().ShouldBeOfType<OpenBaoSecretResolver>();
        provider.GetRequiredService<VaultOptions>().KvMountPath.ShouldBe(
            "platform",
            "the configuration-bound overload must bind the whole section, not only the two keys it "
            + "validates"
        );
    }

    static ISecretResolver Resolver(Action<ISiloBuilder> compose) =>
        Compose(compose).BuildServiceProvider().GetRequiredService<ISecretResolver>();

    static IServiceCollection Compose(Action<ISiloBuilder> compose) {
        var builder = new ServiceCollectionSiloBuilder();
        compose(builder);

        return builder.Services;
    }

    static void Both(ISiloBuilder silo, bool managerFirst) {
        if (managerFirst) {
            silo.AddCyberCloudResourceManager().AddOpenBaoSecretResolver(Wired);
        } else {
            silo.AddOpenBaoSecretResolver(Wired).AddCyberCloudResourceManager();
        }
    }

    /// <summary>
    ///     The smallest thing <c>AddCyberCloudResourceManager</c> and
    ///     <c>AddOpenBaoSecretResolver</c> can be called on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both extensions touch <see cref="Services" /> and <see cref="Configuration" /> and
    ///     nothing else, so a real silo builder here would drag in clustering configuration to assert
    ///     a property of a <see cref="ServiceCollection" />. If either grows a call to another member
    ///     of <see cref="ISiloBuilder" />, this stops compiling — which is the right way to find out.
    /// </remarks>
    /// <param name="configuration">What the host's configuration holds.</param>
    sealed class ServiceCollectionSiloBuilder(IConfiguration? configuration = null) : ISiloBuilder {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration ?? new ConfigurationBuilder().Build();
    }
}
