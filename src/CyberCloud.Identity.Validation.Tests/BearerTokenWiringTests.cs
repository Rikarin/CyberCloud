using CyberCloud.Core.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Validation;

namespace CyberCloud.Identity.Validation.Tests;

/// <summary>
///     What <see cref="BearerTokenServiceCollectionExtensions.AddJwksBearerTokenValidation" />
///     registers, asserted on a real container.
/// </summary>
public sealed class BearerTokenWiringTests {
    [Fact]
    public void AConfiguredIssuerRegistersTheJwksValidatorAndOpenIddictsValidationService() {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddJwksBearerTokenValidation(
            new() { Issuer = "https://id.example.test" },
            "CyberCloud:Example:Identity"
        );

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBearerTokenValidator>().ShouldBeOfType<JwksBearerTokenValidator>();

        // Scoped, and resolved from a scope — the validator reads it from the request's own scope
        // because that is the one OpenIddict expects to be resolved from.
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<OpenIddictValidationService>().ShouldNotBeNull();
    }

    [Fact]
    public void AnEmptyIssuerIsRefusedByTheSectionNameRatherThanDefaulted() {
        var services = new ServiceCollection();

        var thrown = Should.Throw<ArgumentException>(() =>
            services.AddJwksBearerTokenValidation(new() { Issuer = " " }, "CyberCloud:Example:Identity")
        );

        thrown.Message.ShouldContain("CyberCloud:Example:Identity:Issuer");
        services.ShouldNotContain(x => x.ServiceType == typeof(IBearerTokenValidator));
    }

    [Fact]
    public void ARegistrationMadeFirstWins() {
        // TryAdd for the validator: a test with no key set registers a table of tokens it issued
        // itself, and the production registration must not displace it.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBearerTokenValidator, RefusingValidator>();

        services.AddJwksBearerTokenValidation(new() { Issuer = "https://id.example.test" }, "x");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBearerTokenValidator>().ShouldBeOfType<RefusingValidator>();
    }

    [Fact]
    public async Task AnEmptyTokenIsRefusedBeforeAnyServiceIsResolved() {
        // The validator resolves OpenIddict from the request's scope only once it has a token to
        // hand it; an empty string is refused first, so a context with no services at all is enough.
        var validator = new JwksBearerTokenValidator(new SystemClock(), NullLogger<JwksBearerTokenValidator>.Instance);

        var refused = await validator.ValidateAsync("", new DefaultHttpContext(), TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        refused.Error.Message.ShouldContain("empty");
    }

    sealed class RefusingValidator : IBearerTokenValidator {
        public Task<Result<TokenClaims>> ValidateAsync(
            string token,
            HttpContext http,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<TokenClaims>.Failure(BearerTokenErrors.Unauthenticated("refused")));
    }
}
