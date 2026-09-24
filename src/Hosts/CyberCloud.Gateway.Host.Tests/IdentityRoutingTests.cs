using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds the identity addresses to — #43's invitations and #41's members,
///     applications and own sessions — over HTTP.
/// </summary>
/// <remarks>
///     ⚠ Against a SUBSTITUTED <c>IInvitationManager</c> and <c>IIdentityAdministration</c>, so these
///     prove the routing and no more: stage 6 admits the shapes under the reserved namespace and
///     refuses every other shape there, stage 8 hands the token's caller, the address's id and the
///     body to the right call and asks nothing itself, a verb an address doesn't take is a
///     <c>405</c> with <c>Allow</c>, and only the two answers that issue a client secret carry one.
///     Who may do each thing is <c>InvitationTests</c> and <c>IdentityAdministrationTests</c> in
///     <c>CyberCloud.Isolation</c>, through the real engine; the route with nothing substituted —
///     a real token, the real managers and grains — is <c>InvitationThroughTheGatewayTests</c> and
///     <c>IdentityAdministrationThroughTheGatewayTests</c> in <c>CyberCloud.Identity.Host.Tests</c>.
/// </remarks>
public sealed class IdentityRoutingTests {
    static readonly Guid Item = Guid.Parse("5e5e5e5e-0000-4000-8000-00000000beef");

    static string Prefix(Guid tenant) => ScopeId.Tenant(tenant).Path + IdentityAddress.NamespaceSegment;

    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    // ── #43: the invitation ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APostReachesTheInvitationManagerWithTheTokensCallerAndNothingElse() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            IdentityAddress.Invitations(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: """{"email":"Colleague@Contoso.example"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);

        var request = gateway.Invitations.Requests.ShouldHaveSingleItem();

        request.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Email.ShouldBe("Colleague@Contoso.example", "normalizing is the grain's");
        request.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Caller.SubjectId.ShouldBe("alice");

        gateway.Identity.Calls.ShouldBeEmpty("an invitation reached the administration API as well");
        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for an invitation");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for an invitation");
        gateway.Roles.Paths.ShouldBeEmpty("the role assignment manager was reached for an invitation");

        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;

        root.GetProperty("type").GetString().ShouldBe("CyberCloud.Identity/invitations");
        root.GetProperty("id").GetString().ShouldStartWith(IdentityAddress.Invitations(GatewayHarness.TenantA).Path + "/");
        root.GetProperty("properties").GetProperty("userId").GetString().ShouldBe("66666666777748888999aaaaaaaaaaaa");
        root.GetProperty("properties").GetProperty("status").GetString().ShouldBe("pending");

        // ⚠ Never the link: its secret went to the invited address and nowhere else.
        response.Body.ShouldNotContain("token", Case.Insensitive);
    }

    [Theory]
    [InlineData("invitations")]
    [InlineData("members")]
    [InlineData("applications")]
    [InlineData("sessions")]
    public async Task TheAddressIsTheTokensTenantAndAnotherTenantsIsRefusedBeforeDispatch(string collection) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            collection == "invitations" ? "POST" : "GET",
            Prefix(GatewayHarness.TenantB) + collection,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: collection == "invitations" ? """{"email":"colleague@contoso.example"}""" : ""
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Invitations.Requests.ShouldBeEmpty();
        gateway.Identity.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"email":""}""")]
    [InlineData("""{"email":42}""")]
    [InlineData("not json")]
    public async Task AnInvitationBodyWithNoAddressIsA400ThatSaysWhatTheBodyIs(string body) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            IdentityAddress.Invitations(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: body
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    // ── #41: every address, every verb ────────────────────────────────────────────────────────

    /// <summary>
    ///     Each address with the verb it takes, and the call that verb must reach — the whole
    ///     table the dispatcher implements, so a crossed wire (a DELETE on a member reaching the
    ///     application delete, say) fails here by name.
    /// </summary>
    [Theory]
    [InlineData("GET", "invitations", "ListInvitationsAsync", false)]
    [InlineData("DELETE", "invitations/{id}", "RevokeInvitationAsync", true)]
    [InlineData("POST", "invitations/{id}/resend", "ResendInvitationAsync", true)]
    [InlineData("GET", "members", "ListMembersAsync", false)]
    [InlineData("DELETE", "members/{id}", "RemoveMemberAsync", true)]
    [InlineData("GET", "applications", "ListApplicationsAsync", false)]
    [InlineData("GET", "applications/{id}", "GetApplicationAsync", true)]
    [InlineData("DELETE", "applications/{id}", "DeleteApplicationAsync", true)]
    [InlineData("POST", "applications/{id}/rotateSecret", "RotateApplicationSecretAsync", true)]
    [InlineData("GET", "sessions", "ListOwnSessionsAsync", false)]
    [InlineData("DELETE", "sessions/{id}", "RevokeOwnSessionAsync", true)]
    public async Task EachVerbOnEachAddressReachesItsOneCallWithTheTokensCaller(
        string method,
        string suffix,
        string operation,
        bool carriesId
    ) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            Prefix(GatewayHarness.TenantA) + suffix.Replace("{id}", N(Item), StringComparison.Ordinal),
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        response.Status.ShouldBeOneOf([StatusCodes.Status200OK, StatusCodes.Status204NoContent], response.Body);

        var call = gateway.Identity.Calls.ShouldHaveSingleItem();

        call.Operation.ShouldBe(operation);
        call.Request.TenantId.ShouldBe(GatewayHarness.TenantA);
        call.Request.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        call.Request.Caller.SubjectId.ShouldBe("alice");
        call.Id.ShouldBe(carriesId ? Item : Guid.Empty);

        gateway.Invitations.Requests.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Roles.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PUT", "invitations", "GET, POST")]
    [InlineData("DELETE", "invitations", "GET, POST")]
    [InlineData("GET", "invitations/{id}", "DELETE")]
    [InlineData("GET", "invitations/{id}/resend", "POST")]
    [InlineData("POST", "members", "GET")]
    [InlineData("PUT", "members/{id}", "DELETE")]
    [InlineData("DELETE", "applications", "GET, POST")]
    [InlineData("PUT", "applications/{id}", "GET, DELETE")]
    [InlineData("GET", "applications/{id}/rotateSecret", "POST")]
    [InlineData("DELETE", "sessions", "GET")]
    [InlineData("GET", "sessions/{id}", "DELETE")]
    public async Task AVerbAnAddressDoesNotTakeIsA405ThatNamesTheOnesItDoes(string method, string suffix, string allow) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            Prefix(GatewayHarness.TenantA) + suffix.Replace("{id}", N(Item), StringComparison.Ordinal),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: method is "PUT" or "POST" ? "{}" : ""
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Header("Allow").ShouldBe(allow);
        gateway.Identity.Calls.ShouldBeEmpty();
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/providers/CyberCloud.Identity/users")]
    [InlineData("/providers/CyberCloud.Identity/Members")]
    [InlineData("/providers/CyberCloud.Identity/invitations/extra")]
    [InlineData("/providers/CyberCloud.Identity/members/5e5e5e5e-0000-4000-8000-00000000beef")]
    [InlineData("/providers/CyberCloud.Identity/members/5e5e5e5e0000400080000000000beef/extra")]
    [InlineData("/providers/CyberCloud.Identity/applications/5e5e5e5e00004000800000000000beef/resend")]
    [InlineData("/providers/CyberCloud.Identity/invitations/5e5e5e5e00004000800000000000beef/rotateSecret")]
    [InlineData("/providers/CyberCloud.Identity/sessions/5e5e5e5e00004000800000000000beef/a/b")]
    [InlineData("/subscriptions/00000000-0000-4000-8000-000000000001/providers/CyberCloud.Identity/invitations")]
    public async Task AnyOtherShapeUnderTheNamespaceIsA400ListingTheAddresses(string suffix) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            ScopeId.Tenant(GatewayHarness.TenantA).Path + suffix,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain("identity address");
        gateway.Identity.Calls.ShouldBeEmpty();
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    // ── #41: the application body, and the one answer with a secret ───────────────────────────

    [Fact]
    public async Task ARegistrationHandsTheBodyOnAndTheSecretComesBackOnceAndUncached() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            IdentityAddress.Applications(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: """{"displayName":"Acme dashboard","redirectUris":["https://acme.example/cb"],"scopes":["openid","profile"],"publicClient":false}"""
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);
        response.Header("Cache-Control").ShouldBe("no-store");

        var draft = gateway.Identity.Calls.ShouldHaveSingleItem().Draft.ShouldNotBeNull();

        draft.DisplayName.ShouldBe("Acme dashboard");
        draft.RedirectUris.ShouldBe(["https://acme.example/cb"]);
        draft.Scopes.ShouldBe(["openid", "profile"]);
        draft.IsPublicClient.ShouldBeFalse();

        using var created = JsonDocument.Parse(response.Body);

        created.RootElement.GetProperty("type").GetString().ShouldBe("CyberCloud.Identity/applications");
        created.RootElement.GetProperty("properties").GetProperty("clientSecret").GetString().ShouldBe("the-secret-shown-once");

        // A read of the same application carries no secret at all — not an empty one, none.
        var read = await gateway.SendAsync(
            "GET",
            IdentityAddress.Applications(GatewayHarness.TenantA).Item(Item).Path,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        read.Status.ShouldBe(StatusCodes.Status200OK, read.Body);
        read.Body.ShouldNotContain("clientSecret\"");
        read.Header("Cache-Control").ShouldNotBe("no-store", "only the answers that issue a secret are no-store");

        var listed = await gateway.SendAsync(
            "GET",
            IdentityAddress.Applications(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        listed.Body.ShouldNotContain("clientSecret\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"displayName":"x","redirectUris":["https://a.example/cb"],"scopes":["openid"]}""")]
    [InlineData("""{"displayName":"x","redirectUris":"https://a.example/cb","scopes":["openid"],"publicClient":true}""")]
    [InlineData("""{"displayName":"x","redirectUris":["https://a.example/cb"],"scopes":[1],"publicClient":true}""")]
    [InlineData("""{"displayName":"x","redirectUris":["https://a.example/cb"],"scopes":["openid"],"publicClient":"yes"}""")]
    [InlineData("not json")]
    public async Task ARegistrationBodyOfTheWrongShapeIsA400AndPublicClientIsNeverAssumed(string body) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            IdentityAddress.Applications(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: body
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Identity.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARotationIsTheOtherAnswerWithASecretAndIsNoStoreToo() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            new IdentityAddress(GatewayHarness.TenantA, IdentityAddressKind.ApplicationSecret, Item).Path,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Header("Cache-Control").ShouldBe("no-store");
        response.Body.ShouldContain("the-rotated-secret");
    }

    // ── #41: the sessions are the caller's, and "this one" is the token's sid ─────────────────

    [Fact]
    public async Task TheSessionListMarksTheOneTheTokenCameFrom() {
        var gateway = new GatewayHarness();

        var mine = await gateway.SendAsync(
            "GET",
            IdentityAddress.Sessions(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice", sessionId: N(RecordingIdentityAdministration.ItemId))
        );

        mine.Status.ShouldBe(StatusCodes.Status200OK, mine.Body);
        gateway.Identity.Calls.ShouldHaveSingleItem().Request.CurrentSessionId.ShouldBe(RecordingIdentityAdministration.ItemId);

        using (var body = JsonDocument.Parse(mine.Body)) {
            body.RootElement.GetProperty("value")[0].GetProperty("properties").GetProperty("current").GetBoolean().ShouldBeTrue();
        }

        // A token with no sid — a client-credentials grant — marks nothing and is not refused here:
        // whether it may list sessions at all is the manager's to say.
        var other = await gateway.SendAsync(
            "GET",
            IdentityAddress.Sessions(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        other.Status.ShouldBe(StatusCodes.Status200OK, other.Body);
        gateway.Identity.Calls.Last().Request.CurrentSessionId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task AManagerRefusalIsShapedLikeEveryOtherError() {
        var gateway = new GatewayHarness();

        gateway.Identity.Refuse = new(ErrorCode.AuthorizationFailed, "Not an owner.");

        var response = await gateway.SendAsync(
            "GET",
            IdentityAddress.Members(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, "alice")
        );

        response.Status.ShouldBe(StatusCodes.Status403Forbidden, response.Body);
        response.Body.ShouldContain("AuthorizationFailed");
    }
}
