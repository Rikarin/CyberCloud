using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds the invitations address to — issue #43, step 7, over HTTP.
/// </summary>
/// <remarks>
///     ⚠ Against a SUBSTITUTED <c>IInvitationManager</c>, so these prove the routing and no more:
///     stage 6 admits the one shape under the reserved namespace and refuses every other shape
///     there, stage 8 hands the token's caller and the body's address to the manager and asks
///     nothing itself, and the answer is a <c>201</c> without the link. Who may invite is
///     <c>InvitationService</c>'s, driven through the real engine in <c>CyberCloud.Isolation</c>'s
///     <c>InvitationTests</c>; what the mail says is <c>Identity.Host.Tests</c>' <c>InvitationsOverHttpTests</c>.
/// </remarks>
public sealed class InvitationRoutingTests {
    static string Address(Guid tenant) => new InvitationAddress(tenant).Path;

    [Fact]
    public async Task APostReachesTheInvitationManagerWithTheTokensCallerAndNothingElse() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: """{"email":"Colleague@Contoso.example"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);

        var request = gateway.Invitations.Requests.ShouldHaveSingleItem();

        request.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Email.ShouldBe("Colleague@Contoso.example", "normalizing is the grain's");
        request.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Caller.SubjectId.ShouldBe("alice");

        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for an invitation");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for an invitation");
        gateway.Roles.Paths.ShouldBeEmpty("the role assignment manager was reached for an invitation");

        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;

        root.GetProperty("type").GetString().ShouldBe("CyberCloud.Identity/invitations");
        root.GetProperty("id").GetString().ShouldStartWith(Address(GatewayHarness.TenantA) + "/");
        root.GetProperty("properties").GetProperty("userId").GetString().ShouldBe("66666666777748888999aaaaaaaaaaaa");
        root.GetProperty("properties").GetProperty("status").GetString().ShouldBe("pending");

        // ⚠ Never the link: its secret went to the invited address and nowhere else.
        response.Body.ShouldNotContain("token", Case.Insensitive);
    }

    [Fact]
    public async Task TheAddressIsTheTokensTenantAndAnotherTenantsIsRefusedBeforeDispatch() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: """{"email":"colleague@contoso.example"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task EveryOtherVerbIsA405ThatNamesThePost(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: method == "GET" || method == "DELETE" ? "" : """{"email":"colleague@contoso.example"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Body.ShouldContain("POST");
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/providers/CyberCloud.Identity/invitations/extra")]
    [InlineData("/providers/CyberCloud.Identity/users")]
    [InlineData("/subscriptions/00000000-0000-4000-8000-000000000001/providers/CyberCloud.Identity/invitations")]
    public async Task AnyOtherShapeUnderTheNamespaceIsA400NamingTheAddress(string suffix) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            ScopeId.Tenant(GatewayHarness.TenantA).Path + suffix,
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: """{"email":"colleague@contoso.example"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain("invitations address");
        gateway.Invitations.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"email":""}""")]
    [InlineData("""{"email":42}""")]
    [InlineData("not json")]
    public async Task ABodyWithNoAddressIsA400ThatSaysWhatTheBodyIs(string body) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: body
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Invitations.Requests.ShouldBeEmpty();
    }
}
