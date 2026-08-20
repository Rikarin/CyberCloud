using CyberCloud.Core.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     ADR-015: <i>"OpenIddict is a library: it handles the protocol, we own the stores, and the
///     stores are grains."</i> <see cref="IApplicationGrain" /> is the store OpenIddict's application
///     store reads through, so these are assertions about the answers OpenIddict will get.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about a way an authorization server stops being one.</b> A
///     redirect URI matched by prefix, a fragment that makes the compared value and the navigated
///     value differ, a public client holding a secret, a registration body that names a different
///     application than the key it was written under — each turns the <c>/authorize</c> endpoint into
///     something that hands an authorization code to the wrong party, and none of them is visible in
///     a test that only checks that a registration round-trips.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class ApplicationRegistrationTests(IdentityCluster cluster) {
    static ApplicationRegistration Valid(string clientId = "portal") =>
        new() {
            ClientId = clientId,
            DisplayName = "The portal",
            RedirectUris = ["https://app.example.com/callback"],
            AllowedGrants = [GrantType.AuthorizationCode],
            AllowedScopes = ["openid"],
            IsPublicClient = true
        };

    [Fact]
    public async Task ARedirectUriMatchesWholeAndOrdinallyOrNotAtAll() {
        var application = cluster.Application(Guid.NewGuid());
        (await application.CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        (await application.IsRegisteredRedirectUriAsync("https://app.example.com/callback"))
            .GetValueOrThrow()
            .ShouldBeTrue("the exact registered value must match");

        // ⚠ THE ONE THAT MATTERS. A StartsWith comparison accepts every one of these, and each one
        // is an authorization code delivered to a host the tenant never registered.
        string[] hostile = [
            "https://app.example.com/callback.attacker.test",
            "https://app.example.com/callback/../../evil",
            "https://app.example.com/callback?next=https://evil.test",
            "https://app.example.com/callback#x",
            "https://app.example.com.attacker.test/callback",
            "https://app.example.com/CALLBACK",
            "HTTPS://APP.EXAMPLE.COM/callback",
            "https://app.example.com/callbac",
            "http://app.example.com/callback"
        ];

        foreach (var uri in hostile) {
            (await application.IsRegisteredRedirectUriAsync(uri))
                .GetValueOrThrow()
                .ShouldBeFalse(
                    $"'{uri}' is not the registered redirect URI. It is only refused because the "
                    + "comparison is whole-string and ordinal — a prefix match, a case-insensitive "
                    + "compare or any normalisation accepts at least one of these, and the "
                    + "authorization code goes to whoever asked."
                );
        }
    }

    [Fact]
    public async Task ARedirectUriWithAFragmentIsRefusedAtRegistration() {
        var application = cluster.Application(Guid.NewGuid());

        var registration = Valid() with { RedirectUris = ["https://app.example.com/cb#done"] };
        var created = await application.CreateAsync(registration);

        created.IsSuccess.ShouldBeFalse(
            "OAuth 2.1 forbids a fragment in a redirect URI because the authorization response "
            + "appends its own, so the registered value and the value the browser is sent to differ. "
            + "Refusing at registration is the only place the tenant can be told."
        );
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        // Nothing was written: a refused registration must not half-exist.
        (await application.GetAsync()).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/callback")]
    [InlineData("//evil.example/callback")]
    [InlineData("callback")]
    [InlineData("../callback")]
    public async Task ARedirectUriWithNoSchemeOfItsOwnIsRefused(string uri) {
        // ⚠ THE SECOND CASE IS THE ONE THIS TEST WAS WRITTEN FOR, AND IT USED TO BE ACCEPTED.
        // `Uri.TryCreate(s, UriKind.Absolute, …)` is true for both `/callback` and
        // `//evil.example/callback` on macOS and Linux — they parse as `file:` URIs — and false for
        // both on Windows. The "must be absolute" guard therefore held only on the platform this
        // does not run on, and `//evil.example/callback` is the protocol-relative open redirect that
        // ReturnUrl.Sanitize refuses by name a few files away.
        var application = cluster.Application(Guid.NewGuid());

        var created = await application.CreateAsync(Valid() with { RedirectUris = [uri] });

        created.IsSuccess.ShouldBeFalse(
            $"'{uri}' has no scheme of its own, so what it resolves against is the browser's context "
            + "and therefore the attacker's choice"
        );
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task ANativeClientsCustomSchemeIsStillAllowed() {
        // The counterpart the refusal above needs: OAuth 2.1 registers a native client with a
        // private-use scheme, and a guard written as "https only" would break every desktop and
        // mobile client while fixing the file: hole.
        var created = await cluster
            .Application(Guid.NewGuid())
            .CreateAsync(Valid("native") with { RedirectUris = ["com.example.app:/oauth2redirect"] });

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
    }

    [Fact]
    public async Task APublicClientCannotHoldAClientSecret() {
        var application = cluster.Application(Guid.NewGuid());

        var registration = Valid() with {
            IsPublicClient = true,
            ClientSecretRef = new SecretRef { Path = "tenants/x/clients/portal", Field = "secret" }
        };

        var created = await application.CreateAsync(registration);

        created.IsSuccess.ShouldBeFalse(
            "a secret shipped in a browser or a CLI is public. Resolving the contradiction either "
            + "way leaves somebody's threat model wrong, so it fails instead."
        );
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task AConfidentialClientMayHoldOne() {
        var application = cluster.Application(Guid.NewGuid());

        var secret = new SecretRef { Path = "tenants/x/clients/backend", Field = "secret" };
        var created = await application.CreateAsync(
            Valid("backend") with { IsPublicClient = false, ClientSecretRef = secret }
        );

        created.IsSuccess.ShouldBeTrue();
        created.GetValueOrThrow().ClientSecretRef.ShouldBe(secret);
    }

    [Fact]
    public async Task TheIdentityComesFromTheKeyAndNotFromTheBody() {
        var applicationId = Guid.NewGuid();
        var application = cluster.Application(applicationId);

        // The body names a different application and a different tenant. It is caller-supplied on a
        // control-plane endpoint, so believing it would write one grain's state under another's
        // identity.
        var created = await application.CreateAsync(
            Valid() with { ApplicationId = Guid.NewGuid(), TenantId = Guid.NewGuid() }
        );

        var registration = created.GetValueOrThrow();
        registration.ApplicationId.ShouldBe(applicationId);
        registration.TenantId.ShouldBe(IdentityCluster.Tenant);
    }

    [Fact]
    public async Task ARegisteredClientIdSurvivesAnUpdateAndTheRestDoesNot() {
        var application = cluster.Application(Guid.NewGuid());
        var created = (await application.CreateAsync(Valid("portal"))).GetValueOrThrow();

        var updated = await application.UpdateAsync(
            Valid("someone-elses-client-id") with {
                DisplayName = "The portal, renamed",
                RedirectUris = ["https://app.example.com/callback2"]
            }
        );

        var registration = updated.GetValueOrThrow();

        // ⚠ The client id is the name other systems know this registration by, and an update that
        // could change it would let a tenant take over an identifier someone else's token was
        // issued to.
        registration.ClientId.ShouldBe("portal");
        registration.CreatedAt.ShouldBe(created.CreatedAt);

        registration.DisplayName.ShouldBe("The portal, renamed");
        (await application.IsRegisteredRedirectUriAsync("https://app.example.com/callback"))
            .GetValueOrThrow()
            .ShouldBeFalse("an update replaces the redirect URIs rather than adding to them");
        (await application.IsRegisteredRedirectUriAsync("https://app.example.com/callback2"))
            .GetValueOrThrow()
            .ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnregisteredGrantIsRefusedAndAnUnknownApplicationIsNotFound() {
        var application = cluster.Application(Guid.NewGuid());

        var missing = await application.AllowsGrantAsync(GrantType.ClientCredentials);
        missing.IsSuccess.ShouldBeFalse("an application that does not exist allows nothing");
        missing.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await application.CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        (await application.AllowsGrantAsync(GrantType.AuthorizationCode)).GetValueOrThrow().ShouldBeTrue();
        (await application.AllowsGrantAsync(GrantType.ClientCredentials))
            .GetValueOrThrow()
            .ShouldBeFalse("anything absent from AllowedGrants is refused at /token");
    }

    [Fact]
    public async Task RegisteringTwiceIsAConflictAndLeavesTheFirstRegistrationAlone() {
        var application = cluster.Application(Guid.NewGuid());
        (await application.CreateAsync(Valid("portal"))).IsSuccess.ShouldBeTrue();

        var again = await application.CreateAsync(Valid("portal-again"));
        again.IsSuccess.ShouldBeFalse();
        again.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await application.GetAsync()).GetValueOrThrow().ClientId.ShouldBe("portal");
    }

    [Fact]
    public async Task DeletingLeavesNothingBehindAndDeletingAgainIsNotFound() {
        var application = cluster.Application(Guid.NewGuid());
        (await application.CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        (await application.DeleteAsync()).IsSuccess.ShouldBeTrue();

        // ⚠ Every read has to agree that it is gone. A redirect URI that still matched after a
        // deletion would keep an unregistered client working.
        (await application.GetAsync()).IsSuccess.ShouldBeFalse();
        (await application.IsRegisteredRedirectUriAsync("https://app.example.com/callback"))
            .IsSuccess
            .ShouldBeFalse();
        (await application.AllowsGrantAsync(GrantType.AuthorizationCode)).IsSuccess.ShouldBeFalse();

        var again = await application.DeleteAsync();
        again.IsSuccess.ShouldBeFalse();
        again.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task ARegistrationIsNotVisibleToAnotherTenant() {
        var applicationId = Guid.NewGuid();

        (await cluster.Application(applicationId).CreateAsync(Valid())).IsSuccess.ShouldBeTrue();

        // Same application GUID, different tenant. ADR-002 makes the tenant part of the grain
        // identity, so this is a different entity and has no registration.
        (await cluster.Application(applicationId, IdentityCluster.OtherTenant).GetAsync())
            .IsSuccess
            .ShouldBeFalse(
                "an application id is unique within a tenant, and a tenant that guessed another "
                + "tenant's application GUID must learn nothing from it"
            );
    }

    [Fact]
    public async Task AClientIdIsRequired() {
        var application = cluster.Application(Guid.NewGuid());

        var created = await application.CreateAsync(Valid(" "));

        created.IsSuccess.ShouldBeFalse("whitespace is not a client id");
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }
}
