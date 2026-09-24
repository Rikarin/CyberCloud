using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     ADR-015:
///     <i>
///         "OpenIddict is a library: it handles the protocol, we own the stores, and the
///         stores are grains."
///     </i> <see cref="IApplicationGrain" /> is the store OpenIddict's application
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
        (await application.CreateAsync(Valid("portal-redirect"))).IsSuccess.ShouldBeTrue();

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

    [Theory]
    [InlineData("http://app.example.com/callback")]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("myapp:/callback")]
    public async Task ARedirectUriOAuthDoesNotAllowIsRefusedAtRegistration(string uri) {
        // ⚠ #41's review: #94's check refused a relative URI and a fragment and let every one of
        // these through, which mattered once an owner could register a client over HTTP. A plain
        // http host carries the code in the clear; a script scheme runs it in the page.
        var created = await cluster.Application(Guid.NewGuid()).CreateAsync(Valid() with { RedirectUris = [uri] });

        created.IsSuccess.ShouldBeFalse($"'{uri}' was registered");
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8123/callback")]
    [InlineData("http://[::1]:8123/callback")]
    [InlineData("http://localhost:8123/callback")]
    public async Task ANativeClientsLoopbackRedirectIsAllowedOverPlainHttp(string uri) {
        var created = await cluster.Application(Guid.NewGuid()).CreateAsync(Valid($"loopback-{Guid.NewGuid():N}") with { RedirectUris = [uri] });

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
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
            IsPublicClient = true, ClientSecretRef = new() { Path = "tenants/x/clients/portal", Field = "secret" }
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
            Valid("portal-identity") with { ApplicationId = Guid.NewGuid(), TenantId = Guid.NewGuid() }
        );

        var registration = created.GetValueOrThrow();
        registration.ApplicationId.ShouldBe(applicationId);
        registration.TenantId.ShouldBe(IdentityCluster.Tenant);
    }

    [Fact]
    public async Task ARegisteredClientIdSurvivesAnUpdateAndTheRestDoesNot() {
        var application = cluster.Application(Guid.NewGuid());
        var created = (await application.CreateAsync(Valid("portal-update"))).GetValueOrThrow();

        var updated = await application.UpdateAsync(
            Valid("someone-elses-client-id") with {
                DisplayName = "The portal, renamed", RedirectUris = ["https://app.example.com/callback2"]
            }
        );

        var registration = updated.GetValueOrThrow();

        // ⚠ The client id is the name other systems know this registration by, and an update that
        // could change it would let a tenant take over an identifier someone else's token was
        // issued to.
        registration.ClientId.ShouldBe("portal-update");
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

        (await application.CreateAsync(Valid("portal-grant"))).IsSuccess.ShouldBeTrue();

        (await application.AllowsGrantAsync(GrantType.AuthorizationCode)).GetValueOrThrow().ShouldBeTrue();
        (await application.AllowsGrantAsync(GrantType.ClientCredentials))
            .GetValueOrThrow()
            .ShouldBeFalse("anything absent from AllowedGrants is refused at /token");
    }

    [Fact]
    public async Task RegisteringTwiceIsAConflictAndLeavesTheFirstRegistrationAlone() {
        var application = cluster.Application(Guid.NewGuid());
        (await application.CreateAsync(Valid("portal-twice"))).IsSuccess.ShouldBeTrue();

        var again = await application.CreateAsync(Valid("portal-twice-again"));
        again.IsSuccess.ShouldBeFalse();
        again.Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await application.GetAsync()).GetValueOrThrow().ClientId.ShouldBe("portal-twice");
    }

    [Fact]
    public async Task DeletingLeavesNothingBehindAndDeletingAgainIsNotFound() {
        var application = cluster.Application(Guid.NewGuid());
        (await application.CreateAsync(Valid("portal-delete"))).IsSuccess.ShouldBeTrue();

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

        (await cluster.Application(applicationId).CreateAsync(Valid("portal-tenant"))).IsSuccess.ShouldBeTrue();

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

    /// <summary>Client ids <c>GrainKeys.EnsureValidClientId</c> refuses, one per rule.</summary>
    public static TheoryData<string> ClientIdsNoKeyCanCarry =>
        new() {
            "my portal",
            " portal",
            "portal\n",
            "por\ttal",
            new string('a', GrainKeys.MaxClientIdLength + 1)
        };

    [Theory]
    [MemberData(nameof(ClientIdsNoKeyCanCarry))]
    public async Task AClientIdTheIndexCannotCarryIsABadRequestAndNotAThrow(string clientId) {
        // ⚠ THE REVIEW OF #88 FOUND EVERY ONE OF THESE THROWING OUT OF THE GRAIN CALL. Validate only
        // asked IsNullOrWhiteSpace, and GrainKeys.ClientIndex — reached one line later to build the
        // index key — refuses internal white space, a control character and more than 254
        // characters by throwing. A throw out of a grain is an exception at the caller rather than a
        // Result, so a body the tenant typed turned into a 500 instead of the 400 it is.
        var application = cluster.Application(Guid.NewGuid());

        var created = await application.CreateAsync(Valid(clientId));

        created.IsSuccess.ShouldBeFalse($"'{clientId}' is a client id no index key can carry");
        created.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        (await application.GetAsync()).IsSuccess.ShouldBeFalse("a refused create leaves nothing behind");
    }

    [Fact]
    public async Task DeletingAnApplicationWhoseClientIdTheIndexNoLongerHoldsStillDeletesIt() {
        // The shape docs/plan/06 § Two-phase create leaves behind when a silo dies between the write
        // and the confirm: the registration is durable, the lease expires, and a second application
        // takes the client id. From the outside that is an index entry that names another
        // application while this one's state still says the id is its own — reproduced here by
        // releasing the binding behind the first application's back, which is what an expired
        // lease amounts to.
        var first = cluster.Application(Guid.NewGuid());
        var firstId = (await first.CreateAsync(Valid("orphaned-client"))).GetValueOrThrow().ApplicationId;
        (await cluster.ClientIndex("orphaned-client").ReleaseAsync(firstId)).IsSuccess.ShouldBeTrue();

        var secondId = Guid.NewGuid();
        (await cluster.Application(secondId).CreateAsync(Valid("orphaned-client"))).IsSuccess.ShouldBeTrue();

        // ⚠ THE DELETE MUST SUCCEED, AND THE SECOND APPLICATION MUST KEEP THE ID. A release refused
        // because the index names somebody else is the index saying this registration is reachable
        // by its GUID and nothing else; refusing the delete on top of that would leave it that way
        // for good. Handing the id away instead would be worse — that is the second application's
        // live registration.
        var deleted = await first.DeleteAsync();
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
        (await first.GetAsync()).IsSuccess.ShouldBeFalse("the orphan is gone");

        (await cluster.ClientIndex("orphaned-client").ResolveAsync())
            .GetValueOrThrow()
            .ShouldBe(secondId, "the delete of an orphan must not release a binding it does not own");
        (await cluster.Application(secondId).GetAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoApplicationsCannotShareAClientIdInOneTenantButCanAcrossTenants() {
        // ⚠ THE GUARANTEE THE CLIENT INDEX ADDS, AND THE ONE THE AUTHORIZATION-CODE FLOW RELIES ON.
        // Two registrations sharing a client_id in one tenant is two applications an authorization
        // request cannot tell apart; ApplicationGrain.CreateAsync claims IClientIndexGrain before it
        // writes, so the second create is a 409. Across tenants the id is free, because the index is
        // per tenant — docs/plan/11 § Protocol.
        (await cluster.Application(Guid.NewGuid()).CreateAsync(Valid("shared-client")))
            .IsSuccess.ShouldBeTrue();

        var second = await cluster.Application(Guid.NewGuid()).CreateAsync(Valid("shared-client"));
        second.IsSuccess.ShouldBeFalse("a second application took a client id already registered in this tenant");
        second.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        // The same client id in another tenant is a different index entry and free to take.
        (await cluster.Application(Guid.NewGuid(), IdentityCluster.OtherTenant).CreateAsync(Valid("shared-client")))
            .IsSuccess.ShouldBeTrue("a client id is unique per tenant, not globally");
    }

    [Fact]
    public async Task DeletingAnApplicationFreesItsClientIdForReuse() {
        // ⚠ Delete releases the index before dropping the state, so the client id is immediately
        // reusable — otherwise a mistyped registration would burn a client id for good.
        var first = cluster.Application(Guid.NewGuid());
        (await first.CreateAsync(Valid("reusable-client"))).IsSuccess.ShouldBeTrue();
        (await first.DeleteAsync()).IsSuccess.ShouldBeTrue();

        (await cluster.Application(Guid.NewGuid()).CreateAsync(Valid("reusable-client")))
            .IsSuccess.ShouldBeTrue("a released client id must be free for another application to take");
    }
}
