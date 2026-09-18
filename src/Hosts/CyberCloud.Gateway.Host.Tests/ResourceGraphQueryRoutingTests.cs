using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds the resource graph's address to — docs/plan/08 § The resource-graph
///     projection, the query half of #54, over HTTP.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Against a SUBSTITUTED <c>IResourceGraphQuery</c>, so these prove three things and
///             no fourth
///         </b>: that stage 6 admits the shape under its reserved namespace and refuses
///         every other shape there; that stage 8 hands the token's caller, the KQL and the page
///         parameters to the service; and that the page comes back in the collection envelope with
///         a <c>nextLink</c> that carries the page parameters. Whether the KQL translates and what
///         the caller may read is the service's, in <c>CyberCloud.ResourceGraph.Tests</c> and in
///         <c>ResourceGraphQueryEndToEndTests</c> here.
///     </para>
/// </remarks>
public sealed class ResourceGraphQueryRoutingTests {
    const string Kql = "resources | where type =~ 'cybercloud.dbforpostgresql/servers' | project name, location";

    static string Address(Guid tenant) => new ResourceGraphAddress(tenant).Path;

    static string Body(string query = Kql, int? top = null, string? skipToken = null) {
        var members = new List<string> { $"\"query\":{JsonSerializer.Serialize(query)}" };

        if (top is { } size) {
            members.Add($"\"$top\":{size}");
        }

        if (skipToken is not null) {
            members.Add($"\"$skipToken\":{JsonSerializer.Serialize(skipToken)}");
        }

        return "{" + string.Join(",", members) + "}";
    }

    // ── The shape, and where it goes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task APostWithAQueryReachesTheQueryServiceWithTheTokensCallerAndNotTheResourceManager() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "alice"),
            body: Body(top: 25, skipToken: "25.abc")
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        var request = gateway.Graph.Requests.ShouldHaveSingleItem();
        request.Query.ShouldBe(Kql);
        request.Top.ShouldBe(25);
        request.Continuation.ShouldBe("25.abc");
        request.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Caller.SubjectType.ShouldBe("user");
        request.Caller.SubjectId.ShouldBe("alice");

        // ⚠ The whole point of the reserved namespace: a POST with a five-segment path would have
        // been read by ResolveAction as the action `resources` on a malformed resource id, and a
        // provider called CyberCloud.ResourceGraph would have been shadowed. Neither manager sees it.
        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for a graph query");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for a graph query");
        gateway.Roles.Paths.ShouldBeEmpty("the role assignment manager was reached for a graph query");
    }

    [Fact]
    public async Task ThePageIsRenderedInTheCollectionEnvelopeWithItsColumnsAndANextLinkThatCarriesThePageParameters() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: Body(top: 2)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;

        root.GetProperty("columns")
            .EnumerateArray()
            .Select(static x => x.GetProperty("name").GetString())
            .ShouldBe(["name", "location"]);
        root.GetProperty("columns")[0].GetProperty("type").GetString().ShouldBe("string");
        root.GetProperty("value").GetArrayLength().ShouldBe(2);
        root.GetProperty("value")[0].GetProperty("name").GetString().ShouldBe("pg-main");
        root.GetProperty("value")[1].GetProperty("location").GetString().ShouldBe("eu-west");

        // ⚠ The link is the whole next request but the body (#76): the address, the api-version,
        // the caller's own $top and the token — a client POSTs the same body to it.
        root.GetProperty("nextLink")
            .GetString()
            .ShouldBe(
                $"https://api.cybercloud.io{Address(GatewayHarness.TenantA)}?api-version={OneTypeRegistry.TheVersion}&$top=2&$skipToken=2.0123456789abcdef"
            );

        root.TryGetProperty("count", out _).ShouldBeFalse("a total is the enumeration oracle");
        root.TryGetProperty("totalRecords", out _).ShouldBeFalse("a total is the enumeration oracle");
    }

    [Fact]
    public async Task ALastPageOmitsNextLinkRatherThanWritingItEmpty() {
        var gateway = new GatewayHarness();
        gateway.Graph.OnQuery = _ => Result<ResourceGraphQueryPage>.Success(
            new() { Columns = [new("Count", "long")], Rows = ["""{"Count":3}"""] }
        );

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: Body("resources | count")
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Body.ShouldNotContain("nextLink");
        response.Body.ShouldContain("\"Count\":3");
    }

    [Fact]
    public async Task ANextLinkIsFollowedByPostingTheSameBodyAndTheUrlsPageParametersAreRead() {
        // ⚠ The other half of the nextLink contract: the URL's $top and $skipToken are read when
        // the body does not repeat them, so a client that follows the link with the body it sent
        // pages; and the body wins when it does repeat them.
        var gateway = new GatewayHarness();

        var followed = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            $"api-version={OneTypeRegistry.TheVersion}&$top=2&$skipToken=2.0123456789abcdef",
            Body()
        );

        followed.Status.ShouldBe(StatusCodes.Status200OK, followed.Body);
        gateway.Graph.Requests.Last().Top.ShouldBe(2);
        gateway.Graph.Requests.Last().Continuation.ShouldBe("2.0123456789abcdef");

        var overridden = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            $"api-version={OneTypeRegistry.TheVersion}&$top=2&$skipToken=2.0123456789abcdef",
            Body(top: 7, skipToken: "7.fedcba9876543210")
        );

        overridden.Status.ShouldBe(StatusCodes.Status200OK, overridden.Body);
        gateway.Graph.Requests.Last().Top.ShouldBe(7);
        gateway.Graph.Requests.Last().Continuation.ShouldBe("7.fedcba9876543210");
    }

    // ── The tenant ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTenantsAddressIsNotFoundAndTheServiceIsNeverAsked() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA),
            body: Body()
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Graph.Requests.ShouldBeEmpty(
            "stage 3 refuses a tenant that disagrees with the token before anything is queried"
        );
    }

    // ── The verbs and the body ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AnyVerbButPostIs405WithAllowPost(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: method is "PUT" or "PATCH" ? Body() : ""
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Header(GatewayHeaders.Allow).ShouldBe("POST");
        response.Body.ShouldContain("query");
        gateway.Graph.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"query":42}""")]
    [InlineData("""{"kql":"resources"}""")]
    public async Task ABodyWithoutTheQueryIs400ThatSpellsTheShape(string body) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: body
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain("non-empty");
        response.Body.ShouldContain("$skipToken");
        gateway.Graph.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AServiceRefusalIsTheServicesSentenceAs400() {
        var gateway = new GatewayHarness();
        gateway.Graph.OnQuery = static _ => Result<ResourceGraphQueryPage>.Failure(
            ErrorCode.InvalidRequestBody,
            "'mv-expand' is not in the resource graph's KQL subset."
        );

        var response = await gateway.SendAsync(
            "POST",
            Address(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: Body("resources | mv-expand tags")
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain("mv-expand");
        response.Body.ShouldContain("InvalidRequestBody");
    }

    // ── Everything else under the namespace ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/resources/")]
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/resources/main")]
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/queries")]
    [InlineData(
        "/tenants/{t}/subscriptions/cccccccc-0000-0000-0000-000000000003/providers/CyberCloud.ResourceGraph/resources"
    )]
    [InlineData(
        "/tenants/{t}/subscriptions/cccccccc-0000-0000-0000-000000000003/resourceGroups/prod/providers/CyberCloud.ResourceGraph/resources"
    )]
    public async Task AnyOtherPathUnderTheNamespaceIs400NamingTheOneAddressAndReachesNoManager(string template) {
        var gateway = new GatewayHarness();
        var path = template.Replace("{t}", GatewayHarness.TenantA.ToString("D"), StringComparison.Ordinal);

        var response = await gateway.SendAsync("POST", path, gateway.Token(GatewayHarness.TenantA), body: Body());

        // ⚠ 400 and not 404: under the reserved namespace the grammar is asked and nothing else
        // is, so a wrong URL is told so rather than sent looking for a resource that is not there.
        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain(ResourceGraphAddress.Suffix);
        gateway.Graph.Requests.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Scopes.Paths.ShouldBeEmpty();
        gateway.Roles.Paths.ShouldBeEmpty();
    }

    [Fact]
    public void TheQueryIsCountedAsAReadByTheRateLimiter() {
        // A POST that is a read: the write bucket is the small one, sized for creates.
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());

        GatewayRouter.Classify(Address(GatewayHarness.TenantA), "POST", query).ShouldBe(RequestClass.Read);
        GatewayRouter.Classify(GatewayHarness.ResourcePath(GatewayHarness.TenantA) + "/restart", "POST", query)
            .ShouldBe(RequestClass.Write);
    }
}
