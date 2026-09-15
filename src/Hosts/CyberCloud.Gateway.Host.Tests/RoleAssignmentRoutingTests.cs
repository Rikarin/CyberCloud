using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds a <i>role assignment</i> address to — docs/plan/07 § Azure RBAC,
///     expressed in it, over HTTP.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE ADDRESS OVERLAPS THE RESOURCE GRAMMAR, AND THE FIRST TEST IS ABOUT WHICH MANAGER
///             WINS.
///         </b> On a resource group,
///         <c>…/resourceGroups/{g}/providers/CyberCloud.Authorization/roleAssignments/{n}</c> is a
///         well-formed ten-segment resource path. <c>GatewayRouter.Resolve</c> tries the assignment
///         grammar first, and if that order were ever reversed the request would reach the resource
///         manager as a type no provider serves — a <c>404</c> from the wrong component, which
///         reads as "no such role assignment" and is really "no such route".
///     </para>
///     <para>
///         ⚠
///         <b>
///             Against a SUBSTITUTED <c>IRoleAssignmentManager</c>, so these prove two things and no
///             third
///         </b>: that stage 6 admits the shape, and that stage 8 hands it to the assignment
///         manager with the token's tenant and the body. Whether <c>assignRole</c> is checked and
///         whether a tuple lands is <c>RoleAssignmentService</c>'s, driven through the real engine
///         in <c>test/CyberCloud.Isolation</c>.
///     </para>
/// </remarks>
public sealed class RoleAssignmentRoutingTests {
    const string Name = "reader-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d";

    static string OnGroup(Guid tenant) => GatewayHarness.GroupPath(tenant) + RoleAssignmentId.Suffix + Name;

    static string OnSubscription(Guid tenant) => GatewayHarness.SubscriptionPath(tenant) + RoleAssignmentId.Suffix + Name;

    static string OnResource(Guid tenant) => GatewayHarness.ResourcePath(tenant) + RoleAssignmentId.Suffix + Name;

    // ── The overlap, and which manager wins it ─────────────────────────────────────────────────

    [Fact]
    public async Task AGroupScopedAssignmentReachesTheRoleAssignmentManagerAndNotTheResourceManager() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            OnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{}"
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);

        gateway.Roles.Paths.ShouldContain(
            OnGroup(GatewayHarness.TenantA),
            "the router did not bind a group-scoped role assignment to the role assignment manager"
        );

        // ⚠ The whole point. This path IS a resource path — ten segments, /providers/ in the right
        // place — and a resource route would have run it through the twelve-step write path against
        // a type the registry does not serve.
        gateway.Manager.Paths.ShouldBeEmpty("the resource manager was reached for a role assignment");
        gateway.Scopes.Paths.ShouldBeEmpty("the scope manager was reached for a role assignment");
    }

    [Fact]
    public async Task ASubscriptionScopedAssignmentRoutes() {
        // ⚠ Eight segments with 'providers' where 'resourceGroups' would be: neither a scope nor a
        // resource, so before RouteKind.RoleAssignment this was a 400.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            OnSubscription(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        gateway.Roles.Paths.ShouldContain(OnSubscription(GatewayHarness.TenantA));
    }

    [Fact]
    public async Task AResourceScopedAssignmentRoutesWithoutARegistryLookupOnTheResourceType() {
        // ⚠ The resource in the scope is a type the harness's registry does NOT serve
        // (CyberCloud.DBforPostgreSQL/servers against OneTypeRegistry). A resource route would have
        // stopped at stage 6 with a 404 for the unknown type; the assignment route must not, because
        // whether the resource exists is the manager's question and not the registry's.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "DELETE",
            OnResource(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status204NoContent, response.Body);
        gateway.Roles.Paths.ShouldContain(OnResource(GatewayHarness.TenantA));
    }

    // ── The verbs ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGetRendersAzuresEnvelope() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            OnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Body.ShouldContain("\"type\":\"" + RoleAssignmentId.TypeName + "\"");
        response.Body.ShouldContain("\"name\":\"" + Name + "\"");
        response.Body.ShouldContain("\"" + RoleAssignmentBodyProperties.RoleDefinitionId + "\":\"reader\"");
        response.Body.ShouldContain("\"" + RoleAssignmentBodyProperties.PrincipalType + "\":\"user\"");
        response.Body.ShouldContain(
            "\"" + RoleAssignmentBodyProperties.PrincipalId + "\":\"7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d\""
        );
        response.Body.ShouldContain("\"scope\":\"" + GatewayHarness.GroupPath(GatewayHarness.TenantA) + "\"");
    }

    [Fact]
    public async Task ARepeatedGrantIsTwoHundredAndNotTwoHundredAndOne() {
        // ⚠ Both are successes and the difference is what PUT promises: a client that retried
        // because the first answer was lost must not be told it conflicted.
        var gateway = new GatewayHarness();
        gateway.Roles.OnAssign = request => Result<RoleAssignmentSnapshot>.Success(
            new() { Path = request.Path, Name = Name, Created = false }
        );

        var response = await gateway.SendAsync(
            "PUT",
            OnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{}"
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
    }

    [Fact]
    public async Task ThePutBodyReachesTheManagerVerbatim() {
        // The manager is what checks that a body agrees with the address; the gateway's job is to
        // hand it over unchanged, so a disagreement is refused by the component that knows both.
        var gateway = new GatewayHarness();
        const string body = """{"principalId":"7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d","roleDefinitionId":"reader"}""";

        await gateway.SendAsync("PUT", OnGroup(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA), body: body);

        gateway.Roles.Bodies.ShouldContain(body);
    }

    [Fact]
    public async Task ADeleteIsTwoHundredAndFour() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "DELETE",
            OnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status204NoContent, response.Body);
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("POST")]
    public async Task PatchAndPostAreMethodNotAllowedAndNameTheVerbsThatAre(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            OnGroup(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: "{}"
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        response.Header("Allow").ShouldBe("GET, PUT, DELETE");
        gateway.Roles.Paths.ShouldBeEmpty("a refused verb reached the manager");
    }

    // ── The tenant boundary ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAssignmentInAnotherTenantIsNotFoundAndReachesNoManager() {
        // ⚠ THE SAME ASSERTION ScopeRoutingTests MAKES, MADE AGAIN FOR THE NEW ROUTE KIND, because a
        // route kind is exactly where that boundary would be missed. Stage 3 reads the tenant out
        // of the '/tenants/{id}' prefix and refuses before routing runs.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            OnGroup(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA),
            body: "{}"
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Roles.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheAddressThatReachesTheManagerCarriesTheTokensTenant() {
        var gateway = new GatewayHarness();

        await gateway.SendAsync("GET", OnGroup(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA));

        var caller = gateway.Roles.Callers.ShouldHaveSingleItem();
        caller.TenantId.ShouldBe(GatewayHarness.TenantA);

        RoleAssignmentId.ParsePath(gateway.Roles.Paths.Single()).GetValueOrThrow().TenantId.ShouldBe(GatewayHarness.TenantA);
    }

    // ── The shapes that still answer 400 ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheCollectionIsNotServedAndIsRefusedByTheAssignmentGrammarRatherThanTheRegistry() {
        // ⚠ A GET on …/roleAssignments with no name is, to the collection grammar, a COLLECTION of a
        // type the registry does not serve — and before the router asked
        // RoleAssignmentId.IsUnderNamespace first, that is exactly where it went: stage 6 answered
        // the canonical 404, which reads as "no such role assignments" and is really "no such
        // route". Under the reserved namespace only the assignment grammar answers, and its answer
        // is a 400 that names the missing segment. There is no collection read on this address yet;
        // ICheckGrain.ListRoleAssignmentsAsync is the listing and it is not on the wire.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.GroupPath(GatewayHarness.TenantA) + RoleAssignmentId.Suffix.TrimEnd('/'),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        response.Body.ShouldContain(RoleAssignmentId.Suffix);
        gateway.Roles.Paths.ShouldBeEmpty();
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/somethingElse/x")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice/extra")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader-user-")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader-user-Alice")]
    public async Task AMalformedAssignmentPathIsABadRequestAndReachesNoManager(string template) {
        // ⚠ 400 and never 404: every one of these is a client's URL being wrong, and a 404 would
        // send them looking for a missing assignment. The grammar is the manager's contract too —
        // RoleAssignmentIdTests owns the parser; this is the gateway's half of the same promise.
        var gateway = new GatewayHarness();

        var path = template
            .Replace("{t}", GatewayHarness.TenantA.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", GatewayHarness.Subscription.ToString("D"), StringComparison.Ordinal);

        var response = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Roles.Paths.ShouldBeEmpty();
    }
}
