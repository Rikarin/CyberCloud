using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds a <i>policy</i> address to — docs/plan/08 § Policy, issue #46, over
///     the nine stages.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The address overlaps the resource grammar, and the first test is about which manager
///         wins</b>, as it is for <c>RoleAssignmentRoutingTests</c>: an assignment on a resource group is
///         a well-formed ten-segment resource path, and reversed precedence would send it to the
///         resource manager as a type no provider serves.
///     </para>
///     <para>
///         ⚠ <b>Against a substituted <c>IPolicyManager</c></b>, so these prove that stage 6 admits the
///         shapes and that stage 8 hands them over with the token's tenant, the caller and the body.
///         What the manager decides is <c>PolicyEnforcementTests</c> through real grains, and who may
///         decide it is <c>PolicyIsolationTests</c> through the real authorization engine.
///     </para>
/// </remarks>
public sealed class PolicyRoutingTests {
    static string AssignmentOnGroup(Guid tenant) =>
        GatewayHarness.GroupPath(tenant) + PolicyAddress.NamespaceSegment + PolicyAddress.AssignmentsSegment + "/no-public-ip";

    static string DefinitionOnSubscription(Guid tenant) =>
        GatewayHarness.SubscriptionPath(tenant) + PolicyAddress.NamespaceSegment + PolicyAddress.DefinitionsSegment + "/no-public-ip";

    static string DefinitionOnTenant(Guid tenant) =>
        $"/tenants/{tenant:D}" + PolicyAddress.NamespaceSegment + PolicyAddress.DefinitionsSegment + "/tagging";

    static string StatesOnManagementGroup(Guid tenant) =>
        GatewayHarness.ManagementGroupPath(tenant) + PolicyAddress.NamespaceSegment + PolicyAddress.StatesSegment;

    [Fact]
    public async Task AGroupScopedAssignmentReachesThePolicyManagerAndNotTheResourceManager() {
        var gateway = new GatewayHarness();
        const string body = """{ "properties": { "policyDefinitionId": "x" } }""";

        var response = await gateway.SendAsync(
            "PUT",
            AssignmentOnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA, "owner-1"),
            body: body
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);

        var (verb, request) = gateway.Policies.Requests.ShouldHaveSingleItem();
        verb.ShouldBe("PUT");
        request.Path.ShouldBe(AssignmentOnGroup(GatewayHarness.TenantA));
        request.Body.ShouldBe(body, "the body reaches the manager verbatim");
        request.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        request.Caller.SubjectId.ShouldBe("owner-1");

        // ⚠ The whole point: this path IS a resource path, and the resource manager never saw it.
        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for a policy assignment");
        gateway.Roles.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task DefinitionsRouteOnATenantAndASubscriptionAndRenderAzuresEnvelope() {
        var gateway = new GatewayHarness();

        foreach (var path in new[] { DefinitionOnTenant(GatewayHarness.TenantA), DefinitionOnSubscription(GatewayHarness.TenantA) }) {
            var response = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

            response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
            response.Body.ShouldContain("\"id\":\"" + path + "\"");
            response.Body.ShouldContain("\"type\":\"" + PolicyAddress.DefinitionTypeName + "\"");
            response.Body.ShouldContain("\"properties\":{\"displayName\":\"recorded\"}");
        }
    }

    [Fact]
    public async Task TheStatesCollectionRoutesAsAListingWithTheTokensTenantAndPagesByTheUsualRules() {
        var gateway = new GatewayHarness();

        gateway.Policies.OnList = static _ => Result<PolicyListPage>.Success(
            new() {
                States = [
                    new() {
                        ResourcePath = "/tenants/x/r",
                        ResourceType = "CyberCloud.Sample/widgets",
                        AssignmentPath = "/a",
                        DefinitionPath = "/d",
                        State = PolicyComplianceState.NonCompliant,
                        Since = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero)
                    }
                ],
                Continuation = "/tenants/x/r|/a"
            }
        );

        var response = await gateway.SendAsync(
            "GET",
            StatesOnManagementGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            query: "api-version=" + OneTypeRegistry.TheVersion + "&$top=1"
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Body.ShouldContain("\"complianceState\":\"NonCompliant\"");
        response.Body.ShouldContain("\"policyAssignmentId\":\"/a\"");
        response.Body.ShouldContain("\"nextLink\":");

        var listing = gateway.Policies.Listings.ShouldHaveSingleItem();
        listing.Path.ShouldBe(StatesOnManagementGroup(GatewayHarness.TenantA));
        listing.Top.ShouldBe(1);
        gateway.Policies.Requests.ShouldBeEmpty("a collection is not an item");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task AWriteToACollectionIsA400NamingTheItemAddress(string method) {
        var gateway = new GatewayHarness();
        var collection = GatewayHarness.GroupPath(GatewayHarness.TenantA) + PolicyAddress.NamespaceSegment + PolicyAddress.AssignmentsSegment;

        var response = await gateway.SendAsync(method, collection, gateway.Token(GatewayHarness.TenantA), body: "{}");

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain("{policyDefinitions|policyAssignments}/{name}");
        gateway.Policies.Listings.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("POST")]
    public async Task APatchOrAPostOnAPolicyObjectIsA405WithAnAllowHeader(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(method, AssignmentOnGroup(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA), body: "{}");

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Header("Allow").ShouldBe("GET, PUT, DELETE");
        gateway.Policies.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/providers/CyberCloud.Policy/policyExemptions/x")]
    [InlineData("/providers/CyberCloud.Policy/policyAssignments/Bad_Name")]
    [InlineData("/providers/CyberCloud.Policy/policyStates/one")]
    [InlineData("/providers/cybercloud.policy/policyDefinitions/on-a-group")]
    public async Task AMalformedPathUnderTheNamespaceIsA400AndNeverFallsThroughToA404(string suffix) {
        // ⚠ Under the reserved namespace only PolicyAddress's grammar counts. A fall-through into the
        // resource grammar would answer the canonical 404 of a type no provider serves, and send the
        // caller looking for a missing policy when their URL is wrong. The last row is a definition on
        // a resource group, which the grammar does not take: 400, whatever the casing.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", GatewayHarness.GroupPath(GatewayHarness.TenantA) + suffix, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Policies.Requests.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnotherTenantsPolicyPathIsTheCanonical404BeforeAnyManagerIsAsked() {
        // Stage 3 refuses a path naming a tenant the token does not carry; the rebuild at stage 6 is the
        // second defence, and neither lets tenant A's token reach tenant B's catalog.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", AssignmentOnGroup(GatewayHarness.TenantB), gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Policies.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task APolicyViolationFromTheResourceManagerIsA403CarryingTheStructuredDetails() {
        // The deny itself is decided inside the resource manager at step 5; what the gateway owes is to
        // render it: 403, the target, and both names in the details.
        var gateway = new GatewayHarness();

        gateway.Manager.OnWrite = static _ => Result<WriteAccepted>.Failure(
            new Error(
                ErrorCode.PolicyViolation,
                "Policy assignment 'labels' denies this request.",
                "/properties/sku",
                [
                    new(ErrorCode.PolicyViolation, "policyAssignmentId: /a"),
                    new(ErrorCode.PolicyViolation, "policyDefinitionId: /d")
                ]
            )
        );

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-central","properties":{"sku":"gp1"}}"""
        );

        response.Status.ShouldBe(StatusCodes.Status403Forbidden, response.Body);
        response.Body.ShouldContain("\"code\":\"PolicyViolation\"");
        response.Body.ShouldContain("\"target\":\"/properties/sku\"");
        response.Body.ShouldContain("policyAssignmentId: /a");
        response.Body.ShouldContain("policyDefinitionId: /d");
    }
}
