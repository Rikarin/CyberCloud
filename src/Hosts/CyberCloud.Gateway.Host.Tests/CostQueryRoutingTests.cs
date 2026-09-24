using CyberCloud.Billing.Contracts;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The cost query's route through the whole pipeline — docs/plan/22 § Cost visibility, issue #38.
/// </summary>
/// <remarks>
///     ⚠ The grain behind <c>ICostQuery</c> — pricing and the ReBAC filter — is
///     <c>CyberCloud.Billing.Tests.CostVisibilityTests</c>'s to prove. These assert the gateway's half:
///     the address wins over the collection grammar it overlaps, the caller and the token's tenant are
///     what reach the seam, the verb and the body are refused by name, and nothing else is reached.
/// </remarks>
public sealed class CostQueryRoutingTests {
    const string Body = """{ "from": "2026-08-01T00:00:00Z", "to": "2026-09-01T00:00:00Z", "groupBy": "resourceGroup" }""";

    static string OnSubscription(Guid tenant) =>
        new CostQueryAddress(ScopeId.Subscription(tenant, GatewayHarness.Subscription)).Path;

    static string OnGroup(Guid tenant) =>
        new CostQueryAddress(ScopeId.Group(tenant, GatewayHarness.Subscription, "prod")).Path;

    [Fact]
    public async Task APostOnASubscriptionReachesTheCostQueryWithTheTokensCallerAndTenant() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("POST", OnSubscription(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA, "alice"), body: Body);

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        var (tenant, request) = gateway.Costs.Calls.ShouldHaveSingleItem();
        tenant.ShouldBe(GatewayHarness.TenantA);
        request.Caller.SubjectType.ShouldBe("user");
        request.Caller.SubjectId.ShouldBe("alice");
        request.SubscriptionId.ShouldBe(GatewayHarness.Subscription);
        request.ResourceGroup.ShouldBeEmpty();
        request.Grouping.ShouldBe(CostGrouping.ResourceGroup);
        request.From.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        request.To.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        root.GetProperty("currency").GetString().ShouldBe("EUR");
        root.GetProperty("total").GetDecimal().ShouldBe(2.75m);
        root.GetProperty("groupBy").GetString().ShouldBe("resourceGroup");
        root.GetProperty("rows").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).ShouldBe(["prod", "dev"]);

        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for a cost query");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for a cost query");
    }

    /// <summary>
    ///     ⚠ On a resource group the address is also a resource collection path. The namespace test is
    ///     what keeps it from being a listing of a type called <c>query</c>.
    /// </summary>
    [Fact]
    public async Task OnAResourceGroupTheAddressIsTheCostQueryAndNotACollection() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("POST", OnGroup(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA), body: Body);

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        gateway.Costs.Calls.ShouldHaveSingleItem().Request.ResourceGroup.ShouldBe("prod");
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task APathNamingAnotherTenantIsTheTokensTenantOrNothing() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("POST", OnSubscription(GatewayHarness.TenantB), gateway.Token(GatewayHarness.TenantA), body: Body);

        response.Status.ShouldBe(StatusCodes.Status404NotFound, "stage 3 refuses a path in another tenant before routing");
        gateway.Costs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AGetIsA405ThatNamesTheVerb() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", OnGroup(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed);
        response.Header("Allow").ShouldBe("POST");
        gateway.Costs.Calls.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty("a GET on the group's address must not become a collection listing");
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("""{ "to": "2026-09-01T00:00:00Z", "groupBy": "day" }""", "/from")]
    [InlineData("""{ "from": "2026-08-01T00:00:00Z", "to": "yesterday", "groupBy": "day" }""", "/to")]
    [InlineData("""{ "from": "2026-08-01T00:00:00Z", "to": "2026-09-01T00:00:00Z", "groupBy": "tag" }""", "/groupBy")]
    public async Task AMalformedBodyIsA400ThatNamesTheMember(string body, string target) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("POST", OnSubscription(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA), body: body);

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain(target);
        gateway.Costs.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAddressOnATenantIsA400ThatNamesTheScopesItServes() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            $"/tenants/{GatewayHarness.TenantA:D}{CostQueryAddress.Suffix}",
            gateway.Token(GatewayHarness.TenantA),
            body: Body
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("subscription or a resource group");
    }

    [Fact]
    public async Task TheSeamsRefusalIsTheCallersAnswer() {
        var gateway = new GatewayHarness();
        gateway.Costs.OnQuery = request => Result<CostQueryResult>.Failure(
            ErrorCode.ResourceNotFound,
            $"'{ScopeId.Subscription(GatewayHarness.TenantA, request.SubscriptionId).Path}' does not exist."
        );

        var response = await gateway.SendAsync("POST", OnSubscription(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA, "mallory"), body: Body);

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
