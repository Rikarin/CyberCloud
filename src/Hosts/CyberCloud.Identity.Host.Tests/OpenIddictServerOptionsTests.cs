using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The authorization server's own configuration, read back from the options OpenIddict was
///     actually handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             <see cref="IdentityHostAuthenticationTests" /> asserts the path <em>constants</em>,
///             which is not the same claim.
///         </b> A constant that says <c>/connect/token</c> proves nothing
///         about whether the token endpoint is at that path, whether PKCE is required, or whether the
///         client-credentials flow the constant's neighbours describe is actually allowed. This file
///         calls <see cref="IdentityHostOpenIddict.AddIdentityHostOpenIddict" /> and reads
///         <see cref="OpenIddictServerOptions" /> back out, so every assertion is about what the
///         server will do.
///     </para>
///     <para>
///         ⚠ These are OAuth 2.1 requirements rather than preferences. A server that quietly stopped
///         requiring PKCE, or that grew the implicit flow, or that started encrypting access tokens,
///         would keep passing every other test in this project — the endpoints would still map, the
///         claims would still be filtered, the cookie would still be <c>__Host-</c> prefixed — and
///         would be a different security posture.
///     </para>
/// </remarks>
public sealed class OpenIddictServerOptionsTests {
    static OpenIddictServerOptions Options() =>
        new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IHostEnvironment>(TestEnvironment.Development)
            .AddIdentityHostOpenIddict()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<OpenIddictServerOptions>>()
            .Value;

    [Fact]
    public void ProofKeyForCodeExchangeIsRequired() {
        // ⚠ REQUIRED, not merely supported. OAuth 2.1 makes PKCE mandatory for every client, and
        // "supported" means an attacker who has a leaked confidential-client secret can simply omit
        // the challenge — which is the authorization-code injection PKCE exists to close.
        Options().RequireProofKeyForCodeExchange.ShouldBeTrue();
    }

    [Fact]
    public void TheFlowsAreTheFourInTheProtocolTableAndNoOthers() {
        var grants = Options().GrantTypes;

        grants.ShouldBe(
            [
                OpenIddictConstants.GrantTypes.AuthorizationCode,
                OpenIddictConstants.GrantTypes.RefreshToken,
                OpenIddictConstants.GrantTypes.ClientCredentials,
                OpenIddictConstants.GrantTypes.DeviceCode
            ],
            true,
            "docs/plan/11 § Protocol's flow table, and nothing outside it"
        );

        // Named separately because their absence is the point rather than a consequence: the
        // implicit and password grants both hand a credential or a token to a party OAuth 2.1 says
        // must not have one, and both are one method call away from coming back.
        grants.ShouldNotContain(OpenIddictConstants.GrantTypes.Implicit);
        grants.ShouldNotContain(OpenIddictConstants.GrantTypes.Password);
    }

    [Fact]
    public void AccessTokensAreNotEncryptedBecauseTheGatewayValidatesThemLocally() {
        // ⚠ OpenIddict encrypts by default, and an encrypted access token is opaque to anything but
        // OpenIddict's own validation handler. docs/plan/11 § Protocol says access tokens are JWTs
        // because the gateway validates them against the published key set — encrypting them would
        // force the introspection call the same document forbids, and AccessTokenPolicy advertises
        // that introspection is not available.
        Options().DisableAccessTokenEncryption.ShouldBeTrue();
        AccessTokenPolicy.SupportsIntrospection.ShouldBeFalse();
    }

    [Fact]
    public void TheLifetimesComeFromTheContractTheGatewayAlsoReads() {
        var options = Options();

        // ⚠ The one number both sides depend on, read from the shared contract rather than written
        // twice. A gateway that cached for longer than the server issues for is a revocation story
        // that does not hold.
        options.AccessTokenLifetime.ShouldBe(AccessTokenPolicy.AccessTokenLifetime);
        options.RefreshTokenLifetime.ShouldBe(AccessTokenPolicy.RefreshTokenLifetime);
    }

    [Fact]
    public void TheEndpointsAreAtThePathsTheConstantsName() {
        var options = Options();

        // ⚠ Compared as "/" + the URI OpenIddict holds, because it normalises a configured "/token"
        // to a relative Uri whose ToString() is "token". Comparing the raw strings would pass for
        // the wrong reason on a value that had lost its path entirely.
        static void ShouldBeAt(ICollection<Uri> configured, string constant) =>
            configured.Select(static x => "/" + x.ToString().TrimStart('/'))
                .ShouldContain(constant, $"the constant says {constant}");

        ShouldBeAt(options.AuthorizationEndpointUris, IdentityHostOpenIddict.AuthorizationPath);
        ShouldBeAt(options.TokenEndpointUris, IdentityHostOpenIddict.TokenPath);
        ShouldBeAt(options.UserInfoEndpointUris, IdentityHostOpenIddict.UserInfoPath);
        ShouldBeAt(options.DeviceAuthorizationEndpointUris, IdentityHostOpenIddict.DeviceAuthorizationPath);
        ShouldBeAt(options.EndUserVerificationEndpointUris, IdentityHostOpenIddict.EndUserVerificationPath);
        ShouldBeAt(options.EndSessionEndpointUris, IdentityHostOpenIddict.EndSessionPath);
    }

    [Fact]
    public void TheOptionsCanBeMaterialisedAtAll() {
        // ⚠ THE ASSERTION THE OTHERS ARE A CONSEQUENCE OF, AND IT FAILED WHEN IT WAS FIRST WRITTEN.
        // OpenIddict's post-configuration refuses the whole server with "The end-user verification
        // endpoint must be enabled to use the device authorization flow" when the device flow is
        // allowed and no verification endpoint is set, so resolving these options threw — and every
        // OIDC request with them. Nothing noticed because nothing in this project had ever called
        // AddIdentityHostOpenIddict; the path constants were asserted instead, and a constant proves
        // nothing about whether the server it names will start.
        Should.NotThrow(Options);
    }

    [Fact]
    public void ThereIsNoIntrospectionAndNoRevocationEndpoint() {
        var options = Options();

        // ⚠ Both absent on purpose, and both are what AccessTokenPolicy tells the gateway. An
        // introspection endpoint that appeared here would make the "validate locally" contract
        // optional, and the first caller to use it would turn a ten-minute token into a per-request
        // round trip nobody measured.
        options.IntrospectionEndpointUris.ShouldBeEmpty();
        options.RevocationEndpointUris.ShouldBeEmpty();
        AccessTokenPolicy.AccessTokensAreRevocable.ShouldBeFalse();
    }

    [Fact]
    public void ThereIsASigningKeyAndAnEncryptionKey() {
        var options = Options();

        // ⚠ Ephemeral, which is a hole with a name — the production key set is the vault's and does
        // not exist yet, so every restart invalidates every issued token. What this asserts is that
        // there is a key at all: with none, the server starts and then fails on the first token
        // request, which is a start-up problem discovered by a user.
        options.SigningCredentials.ShouldNotBeEmpty();
        options.EncryptionCredentials.ShouldNotBeEmpty();
    }

    [Fact]
    public void TheSigningKeyIsTheAlgorithmTheContractNames() {
        // ⚠ ES256 by name, from the contract. AddEphemeralSigningKey() with no argument mints RSA,
        // and a server publishing an RS256 key beside AccessTokenPolicy.SigningAlgorithm = "ES256"
        // would have every token refused by a gateway that honoured the contract — which is the
        // gateway this repository ships.
        var credentials = Options().SigningCredentials;

        credentials.Count.ShouldBe(1, "one signing key; a set is the weakest member of the set");
        credentials[0].Algorithm.ShouldBe(AccessTokenPolicy.SigningAlgorithm);
    }

    [Fact]
    public void DegradedModeIsOnBecauseTheStoresAreGrains() {
        // ⚠ ADR-015 at the protocol layer. Without this, OpenIddict's built-in validation resolves
        // the client through a core manager that wants a store, throws "The core services must be
        // registered" on the first token request, and the host answers 500 — which is what it did
        // for as long as nothing called /token. DegradedModeHandlers carries the account.
        Options().EnableDegradedMode.ShouldBeTrue();
    }

    [Fact]
    public void EveryEndpointHasTheValidatorDegradedModeDemands() {
        // ⚠ THE FAILURE FOR A MISSING REQUEST VALIDATOR IS A 500 AT REQUEST TIME, NOT A REFUSAL AT
        // START-UP. OpenIddict checks for a custom request validator only when a request for that
        // endpoint arrives, with "No custom … request validation handler was found" — so a server
        // missing one starts, publishes the endpoint in its discovery document, and throws on the
        // first caller. Each enabled endpoint's validator is asserted here by the context type
        // OpenIddict names in that message.
        //
        // ⚠ The two token handlers are the exception, and TheOptionsCanBeMaterialisedAtAll is what
        // found them: with the device flow allowed, OpenIddict's post-configuration refuses the
        // options themselves unless a custom ValidateTokenContext and GenerateTokenContext handler
        // exists to hold device and user codes. They are listed here anyway, so this test names the
        // whole set the degraded mode demands rather than the part that fails late.
        var custom = Options().Handlers
            .Where(static x => x.Type == OpenIddictServerHandlerType.Custom)
            .Select(static x => x.ContextType)
            .ToHashSet();

        foreach (var required in new[] {
                     typeof(OpenIddictServerEvents.ValidateTokenRequestContext),
                     typeof(OpenIddictServerEvents.ValidateAuthorizationRequestContext),
                     typeof(OpenIddictServerEvents.ValidateDeviceAuthorizationRequestContext),
                     typeof(OpenIddictServerEvents.ValidateEndUserVerificationRequestContext),
                     typeof(OpenIddictServerEvents.ValidateEndSessionRequestContext),
                     typeof(OpenIddictServerEvents.ValidateTokenContext),
                     typeof(OpenIddictServerEvents.GenerateTokenContext)
                 }) {
            custom.ShouldContain(
                required,
                $"no custom IOpenIddictServerHandler<{required.Name}> is registered; in degraded "
                + "mode the corresponding endpoint answers 500 to its first request"
            );
        }

        // ⚠ And the three that are ours by choice rather than by demand, by type: the validators
        // that serve a flow rather than refuse one, so a refusing handler put back in their place
        // would fail here by name rather than by a 400 in a browser.
        var handlers = Options().Handlers.Where(static x => x.Type == OpenIddictServerHandlerType.Custom).ToList();

        handlers.ShouldContain(x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.ValidateAuthorizationRequest)
        );
        handlers.ShouldContain(x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.ValidateTokenRequest)
        );
        handlers.ShouldContain(x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.ValidateEndSessionRequest)
        );
    }

    [Fact]
    public void TheTwoCookieHandlersAreRegisteredExactlyOnce() {
        // ⚠ Exactly once, on the two contexts they belong to. Registered twice, the extract handler
        // would run the Origin check twice (harmless) and the response handler would move the
        // refresh token into the cookie and then find nothing to move (also harmless) — the failure
        // is the other direction: a refactor that drops one of them from DegradedModeHandlers.All
        // leaves the refresh token in the portal's response body, which every assertion on the
        // principal passes and only an HTTP test sees. Counting here makes it a start-up-shaped
        // failure.
        var handlers = Options().Handlers.Where(static x => x.Type == OpenIddictServerHandlerType.Custom).ToList();

        handlers.Count(static x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.ExtractRefreshTokenFromCookie)
        )
            .ShouldBe(1);

        handlers.Single(static x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.ExtractRefreshTokenFromCookie)
        )
            .ContextType.ShouldBe(typeof(OpenIddictServerEvents.ExtractTokenRequestContext));

        handlers.Count(static x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.MoveRefreshTokenToCookie)
        )
            .ShouldBe(1);

        handlers.Single(static x => x.ServiceDescriptor.ImplementationType
            == typeof(DegradedModeHandlers.MoveRefreshTokenToCookie)
        )
            .ContextType.ShouldBe(typeof(OpenIddictServerEvents.ApplyTokenResponseContext));
    }

    [Fact]
    public void AnAuthorizationCodeLivesFiveMinutes() {
        // Long enough for a slow redirect chain, short enough that a code that leaked through a
        // Referer or a log is stale before anybody reads it — and, with no token store in degraded
        // mode, the only lifetime a code has.
        Options().AuthorizationCodeLifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void TheIssuerIsTheHostOptionsIssuerWhenOneIsSet() {
        // ⚠ Pinned on both sides: the gateway refuses a discovery document whose issuer differs from
        // the one it was configured with, so a host that inferred its issuer from the Host header
        // behind a proxy would mint tokens the gateway refuses. IdentityHostOptions.Issuer is the
        // one place a deployment says what this host is called.
        var configured = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IHostEnvironment>(TestEnvironment.Development)
            .Configure<IdentityHostOptions>(static x => x.Issuer = "https://id.example.test")
            .AddIdentityHostOpenIddict()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<OpenIddictServerOptions>>()
            .Value;

        configured.Issuer.ShouldBe(new Uri("https://id.example.test"));

        // And unset means inferred from the request, which is what a developer's 127.0.0.1:port
        // needs and what a production deployment must not rely on.
        Options().Issuer.ShouldBeNull();
    }

    [Fact]
    public void TheScopesSayWhatKindOfTokenItIsAndNeverWhatItMayDo() {
        // ⚠ No `admin`, no `write`, no `*.readwrite`. A scope in a token is a permission in a token,
        // and what a subject may do is a ReBAC Check at the point of use — the same decision as the
        // missing role claim in NoRolesInTokenTests.
        string[] scopes = [
            IdentityHostOpenIddict.Scopes.OpenId,
            IdentityHostOpenIddict.Scopes.Profile,
            IdentityHostOpenIddict.Scopes.OfflineAccess,
            IdentityHostOpenIddict.Scopes.Api
        ];

        foreach (var scope in scopes) {
            scope.ShouldNotContain("admin");
            scope.ShouldNotContain("write");
            scope.ShouldNotContain("delete");
        }

        scopes.Distinct(StringComparer.Ordinal).Count().ShouldBe(scopes.Length);

        // ⚠ And the server has been TOLD about them. OpenIddict validates every requested scope
        // against its registered set and answers invalid_scope for a stranger — which is what the
        // first real client-credentials request for `cyc.api` got, because the nested class declared
        // the four and nothing registered them. A constant is not a registration.
        Options().Scopes.ShouldBe(scopes, true);
    }
}
