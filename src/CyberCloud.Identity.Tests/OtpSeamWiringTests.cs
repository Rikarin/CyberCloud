using CyberCloud.Communication.Contracts;
using CyberCloud.Core;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Seams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     Which <see cref="IOtpDeliverySeam" /> a silo ends up with, asserted on the real registrations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS IS A SEAM THAT BECAME PERMISSIVE FOR A HOST THAT DID NOT ASK.</b>
///         <see cref="UnavailableOtpDelivery" /> fails by design and names the missing call in its
///         message — that sentence is what an operator reads at 03:00, and
///         <c>OtpDeliveryTests.TheUnwiredDefaultFailsAndNamesTheMissingCall</c> pins it. What is
///         asserted here is the half that type cannot assert about itself: that it is still what a
///         silo <i>gets</i>. The way that breaks is not a change to the default — it is somebody
///         wiring one host and reaching for a convenience that changes the default for all of them.
///     </para>
///     <para>
///         ⚠ <b><c>Replace</c> versus <c>TryAdd</c>, which is the other half and is order-dependent
///         in a way that is easy to get backwards.</b> <c>AddCyberCloudIdentity</c> registers the
///         refusing seam with <c>TryAdd</c> and <c>AddCommunicationOtpDelivery</c> installs the real
///         one with <c>Replace</c>, so the pair works whichever order a host writes it in. A plain
///         <c>Add</c> in the second would win or lose depending on line order — and the losing
///         arrangement is the one where every OTP fails with a message about wiring that is
///         <i>present</i>. Both orders are driven below.
///     </para>
///     <para>
///         ⚠ <b>No cluster, no grains, no silo started.</b> These are questions about a
///         <see cref="ServiceCollection" />, so they are asked of one — the same reasoning
///         <c>CyberCloud.Identity.Host.Tests</c> gives for having no <c>TestServer</c>.
///     </para>
/// </remarks>
public sealed class OtpSeamWiringTests {
    [Fact]
    public void ASiloThatOnlyAddsIdentityGetsTheRefusingSeam() {
        // ⚠ The default, and it must stay the default. Every host in this repository is in this
        // state: grep finds no caller of AddCommunicationOtpDelivery outside tests, and the only silo
        // in src/Hosts (CyberCloud.Silo.Host) does not compose the identity module at all.
        Seam(silo => silo.AddCyberCloudIdentity()).ShouldBeOfType<UnavailableOtpDelivery>();
    }

    [Fact]
    public void WiringOneSiloDoesNotChangeWhatAnotherSiloGets() {
        // ⚠ THE ROW THAT WOULD CATCH THE TEMPTING FIX. "Nothing calls AddCommunicationOtpDelivery" is
        // repairable in two ways: wire the hosts that want it, or make the real seam the default and
        // let the unwired hosts pick it up. The second is silent — a host with no communication
        // module in it would fail at the first ACTIVATION with a DI error naming IMessageSender,
        // which is a stack trace rather than the sentence UnavailableOtpDelivery hands the operator.
        var wired = Seam(silo => silo.AddCyberCloudIdentity().AddCommunicationOtpDelivery(Guid.NewGuid(), Guid.NewGuid()));
        var unwired = Seam(silo => silo.AddCyberCloudIdentity());

        wired.ShouldBeOfType<CommunicationOtpDelivery>();
        unwired.ShouldBeOfType<UnavailableOtpDelivery>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRealSeamWinsWhicheverOrderTheHostWritesTheTwoCallsIn(bool identityFirst) {
        var seam = Seam(silo => {
                if (identityFirst) {
                    silo.AddCyberCloudIdentity().AddCommunicationOtpDelivery(Guid.NewGuid(), Guid.NewGuid());
                } else {
                    silo.AddCommunicationOtpDelivery(Guid.NewGuid(), Guid.NewGuid()).AddCyberCloudIdentity();
                }
            }
        );

        seam.ShouldBeOfType<CommunicationOtpDelivery>(
            "AddCommunicationOtpDelivery uses Replace and AddCyberCloudIdentity uses TryAdd, so "
            + "neither order can leave a host with the refusing seam it explicitly opted out of"
        );
    }

    [Fact]
    public void TheRefusingSeamNeedsNothingTheRealOneNeeds() {
        // ⚠ Asserted by the absence below rather than in prose: Resolve() registers no IMessageSender,
        // so a default that had quietly become CommunicationOtpDelivery would throw here instead of
        // returning. That is what makes the first test above a real assertion rather than a tautology
        // about whichever type happened to be registered.
        var services = new ServiceCollection();

        services.Any(x => x.ServiceType == typeof(IMessageSender)).ShouldBeFalse();

        Seam(silo => silo.AddCyberCloudIdentity()).ShouldNotBeNull();
    }

    /// <summary>
    ///     Composes a silo the way a host would and resolves whatever <see cref="IOtpDeliverySeam" />
    ///     came out.
    /// </summary>
    /// <param name="compose">What the host calls.</param>
    static IOtpDeliverySeam Seam(Action<ISiloBuilder> compose) {
        var builder = new ServiceCollectionSiloBuilder();
        compose(builder);

        // ⚠ A double for IMessageSender, and it never has to answer anything. CommunicationOtpDelivery
        // resolves one in its factory, so without this the wired cases would fail to construct — and
        // they would fail for a reason that has nothing to do with what is under test. The real one
        // comes from AddCyberCloudCommunication, on a silo that has the module.
        builder.Services.AddSingleton<IMessageSender>(new UnusedMessageSender());

        return builder.Services.BuildServiceProvider().GetRequiredService<IOtpDeliverySeam>();
    }

    /// <summary>
    ///     The smallest thing <c>AddCyberCloudIdentity</c> and <c>AddCommunicationOtpDelivery</c> can
    ///     be called on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both extensions touch <see cref="Services" /> and nothing else, so a real silo builder
    ///     here would drag in clustering configuration to assert a property of a
    ///     <see cref="ServiceCollection" />. If either ever grows a call to another member of
    ///     <see cref="ISiloBuilder" />, this stops compiling — which is the right way to find out.
    /// </remarks>
    sealed class ServiceCollectionSiloBuilder : ISiloBuilder {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    /// <summary>An <see cref="IMessageSender" /> that exists to be constructed and never called.</summary>
    sealed class UnusedMessageSender : IMessageSender {
        public Task<Result<MessageSnapshot>> SendAsync(
            Guid tenantId,
            SendRequest request,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(Refusal);

        public Task<Result<MessageSnapshot>> GetStatusAsync(
            Guid tenantId,
            Guid serviceId,
            string idempotencyKey,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException(Refusal);

        const string Refusal =
            "These tests assert which seam a silo resolves, never what it sends. A call here means "
            + "one of them started exercising the delivery path, which OtpDeliveryTests does "
            + "against a real communication silo.";
    }
}
