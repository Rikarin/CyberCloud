using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The sign-in endpoints resolve from the host's own registration.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this exists for is a start-up crash, and it is the one this host has
///         already had.</b> Before <c>Program.cs</c> moved to
///         <c>OrleansApplication.CreateClient</c> there was no <see cref="IGrainFactory" /> in the
///         container at all, so <see cref="SignInService" /> could not be constructed and the
///         endpoints could not exist — which is why they were documented and not mapped. A missing
///         registration reappears as a <c>DI resolution</c> exception on the first request, and
///         nothing else in this project would notice.
///     </para>
///     <para>
///         ⚠ <b><see cref="IGrainFactory" /> and data protection are supplied here rather than
///         asserted.</b> They come from <c>OrleansApplication.CreateClient</c> and from
///         <c>WebApplicationBuilder</c> respectively, so a test that registered neither would be
///         asserting that <c>AddIdentityHostApi</c> provides things it must not. What is under test
///         is that <b>everything else</b> the endpoints need is in that one call.
///     </para>
/// </remarks>
public sealed class IdentityHostServicesTests {
    static ServiceProvider Build(params (string Key, string? Value)[] configuration) {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDataProtection();

        // The two the host builder supplies. See the ⚠ block above.
        services.AddSingleton<IGrainFactory, RefusingGrainFactory>();

        services.AddIdentityHostApi(
            new ConfigurationBuilder().AddInMemoryCollection(configuration.ToDictionary(x => x.Key, x => x.Value)).Build()
        );

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    [Fact]
    public void TheSignInEndpointsResolve() {
        using var provider = Build();

        // ⚠ The whole graph in one line: SignInApi pulls SignInService, which pulls IGrainFactory,
        // ILockoutCounter, IPasswordHasher and ILogger; plus IPasskeyService, ITotpSecretSeam,
        // IClock and the options. A gap anywhere below throws here.
        Should.NotThrow(() => provider.GetRequiredService<SignInApi>());
        Should.NotThrow(() => provider.GetRequiredService<PasskeyChallengeCookie>());
    }

    [Fact]
    public void TheWebAuthnServiceResolvesSoThePasskeyEndpointsAreReal() =>
        // Fido2PasskeyService needs an IFido2, which comes from AddFido2 — a call it is easy to omit
        // because nothing else in the host names it. Without it, /api/signin/passkey/begin is a 500.
        Should.NotThrow(() => Build().GetRequiredService<IPasskeyService>());

    [Fact]
    public void TheTenantComesFromConfiguration() {
        using var provider = Build(($"{IdentityHostOptions.SectionName}:TenantId", "6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10"));

        provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityHostOptions>>()
            .Value.TenantId
            .ShouldBe(Guid.Parse("6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10"));
    }

    [Fact]
    public void TheRelyingPartyComesFromConfiguration() {
        // ⚠ The relying-party id is the whole of a passkey's phishing resistance and it differs
        // between localhost and a cluster, so a binding that silently kept the default would produce
        // a SecurityError inside the authenticator naming nothing useful.
        using var provider = Build(($"{IdentityHostOptions.SectionName}:RelyingPartyId", "id.cybercloud.io"));

        provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityHostOptions>>()
            .Value.RelyingPartyId
            .ShouldBe("id.cybercloud.io");
    }

    [Fact]
    public void TotpSecretsAreStillUnwiredAndSayWhy() {
        using var provider = Build();

        // ⚠ This assertion is meant to FAIL the day somebody wires a vault, and that is the point:
        // it is the marker that /api/signin/totp cannot verify a code today. docs/plan/11
        // § Credentials keeps the shared secret behind a SecretRef, and nothing in this repository
        // resolves one. Recovery codes are unaffected — they are hashed in the grain.
        var seam = provider.GetRequiredService<ITotpSecretSeam>();

        seam.GetType().Name.ShouldBe(
            "UnavailableTotpSecrets",
            "A real ITotpSecretSeam is wired. Delete this assertion, and re-read "
            + "SignInApi.VerifyTotpAsync — its vault-failure branch is no longer the common case."
        );
    }

    [Fact]
    public void ThePasswordHasherIsTheProductionCostSoTheDummyVerificationCostsTheSame() {
        using var provider = Build();

        // ⚠ This host's hasher exists for exactly one value — IPasswordHasher.DummyHash, which
        // SignInService verifies against on the no-such-user branch. Its COST has to match the
        // silo's or the enumeration defence is a pad rather than equal work; its PEPPER does not,
        // because nothing here ever verifies a real password.
        var hasher = provider.GetRequiredService<IPasswordHasher>();

        hasher.DummyHash.ShouldStartWith("$argon2id$");
        hasher.DummyHash.ShouldContain($"m={Argon2idOptions.Default.MemoryKibibytes}");
        hasher.DummyHash.ShouldContain($"t={Argon2idOptions.Default.Iterations}");
    }
}
