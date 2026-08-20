using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
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
///         ⚠ <b><see cref="IdentityHostAuthenticationTests" /> asserts the path <em>constants</em>,
///         which is not the same claim.</b> A constant that says <c>/connect/token</c> proves nothing
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
            ignoreOrder: true,
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
            configured.Select(x => "/" + x.ToString().TrimStart('/'))
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
            scope.ShouldNotContain("admin", Case.Insensitive);
            scope.ShouldNotContain("write", Case.Insensitive);
            scope.ShouldNotContain("delete", Case.Insensitive);
        }

        scopes.Distinct(StringComparer.Ordinal).Count().ShouldBe(scopes.Length);
    }
}
