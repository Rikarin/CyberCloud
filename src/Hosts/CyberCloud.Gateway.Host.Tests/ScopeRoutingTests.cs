using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds a <i>scope</i> address to — docs/plan/06 § The hierarchy's subscription
///     and resource group, over HTTP.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ADDING <c>RouteKind.Scope</c> CHANGED WHAT SOME PATHS ANSWER, AND HALF OF THIS SUITE
///         IS ABOUT THE PATHS THAT DID NOT CHANGE.</b> <c>GatewayRouter.Resolve</c> is a total
///         function over a closed set of route kinds, so a sixth member moves the boundary of
///         <c>RouteKind.Unknown</c>: three shapes that used to be a <c>400</c> now route. Every other
///         shape must still refuse, and a scope grammar that quietly swallowed a malformed resource
///         path would turn an <c>InvalidResourceId</c> <c>400</c> — which tells a client their URL is
///         wrong — into a <c>404</c>, which sends them looking for a resource.
///     </para>
///     <para>
///         ⚠ <b>These assertions are against a SUBSTITUTED <c>IScopeManager</c> and therefore prove
///         exactly two things</b>: that stage 6 admits the shape, and that stage 8 hands it to the
///         scope manager rather than the resource manager, with the token's tenant. Whether a
///         permission is checked, whether a parent edge is written and whether anything is created is
///         <c>ScopeManagerService</c>'s, driven through the real engine in
///         <c>test/CyberCloud.Isolation</c>. Neither suite covers the join, which is the standing
///         hazard <c>RecordingScopeManager</c>'s remarks name.
///     </para>
/// </remarks>
public sealed class ScopeRoutingTests {
    // ── The three shapes that now route ────────────────────────────────────────────────────────

    [Fact]
    public async Task AResourceGroupIsCreatedWithAPut() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-central"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);

        gateway.Scopes.Paths.ShouldContain(
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            "the router did not bind PUT on a resource-group address to the scope manager"
        );

        // ⚠ And not to the RESOURCE manager. Two managers, one dispatch stage: a scope arriving at
        // IResourceManager would be a create that ran the twelve-step write path against an address
        // with no provider, and the first thing it would do is refuse — which reads as a routing bug
        // that is really a dispatch bug.
        gateway.Manager.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASubscriptionIsCreatedWithAPut() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.SubscriptionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"displayName":"Production"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status201Created, response.Body);
        gateway.Scopes.Paths.ShouldContain(GatewayHarness.SubscriptionPath(GatewayHarness.TenantA));
    }

    [Fact]
    public async Task AScopeIsReadableWithAGet() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Body.ShouldContain("\"type\":\"" + ScopeTypeNames.ResourceGroup + "\"");
    }

    [Fact]
    public async Task ARepeatedCreateIsTwoHundredAndNotTwoHundredAndOne() {
        // ⚠ Both are successes and the difference is the whole of what PUT promises. A client that
        // retried a create because the first answer was lost must not be told it conflicted, and a
        // client that meant to create must be able to tell that it did.
        var gateway = new GatewayHarness();
        gateway.Scopes.OnCreate = request => Result<ScopeSnapshot>.Success(
            new() { Path = request.Path, Kind = ScopeKind.ResourceGroup, Name = "prod", Created = false }
        );

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-central"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
    }

    // ── The tenant boundary, which is the reason a tenant is not creatable here ─────────────────

    [Fact]
    public async Task AScopeInAnotherTenantIsNotFoundAndReachesNoManager() {
        // ⚠ THE SAME ASSERTION TenantFromTokenTests MAKES FOR A RESOURCE, MADE AGAIN FOR THE NEW
        // ROUTE KIND, BECAUSE A ROUTE KIND IS EXACTLY WHERE THAT BOUNDARY WOULD BE MISSED. Stage 3
        // reads the tenant out of the '/tenants/{id}' prefix — which a scope path has too — and
        // refuses before routing runs, so the scope manager is never reached at all.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.GroupPath(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-central"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Scopes.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATenantAddressIsRoutedButNamesTheTenantFromTheToken() {
        // ⚠ '/tenants/{own}' routes — a caller may read the tenant they hold a token for — and the
        // address that reaches the manager is rebuilt from the token, never taken from the URL. That
        // rebuild is the second of the two defences GatewayRoute's remarks describe, and it is the
        // one that still holds if stage 3 is deleted.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            $"/tenants/{GatewayHarness.TenantA:D}",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        gateway.Scopes.Paths.ShouldContain($"/tenants/{GatewayHarness.TenantA:D}");
    }

    // ── The verbs a scope does not serve ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    public async Task AScopeRefusesTheVerbsItDoesNotServeAndSaysWhichItDoes(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: method == "PATCH" ? """{"location":"eu-central"}""" : ""
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);

        // RFC 9110 § 15.5.6 requires it, and without it a 405 is a dead end.
        response.Headers.ShouldContainKey("Allow");
    }

    [Fact]
    public async Task DeletingAResourceGroupIs204AndNot202BecauseThereIsNothingToPoll() {
        // ⚠ DELETE used to be in the theory above, because a scope delete was the reverse of
        // docs/plan/06 § Two-phase create and was not built. It is now served for a resource GROUP —
        // and it answers 204 rather than the 202 a resource delete gives, because by the time it runs
        // the group is already empty: IScopeManager.DeleteAsync refuses a group that still holds
        // resources rather than cascading, so what the call did — seal a grain, reclaim a namespace
        // per cluster, drop a listing entry — is finished when it returns. A 202 and an Operation-Id
        // that resolves to nothing would be a poll loop for every client polite enough to follow it.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "DELETE",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status204NoContent, response.Body);
        gateway.Scopes.Paths.ShouldContain(GatewayHarness.GroupPath(GatewayHarness.TenantA));
    }

    [Fact]
    public async Task AGroupDeleteThatIsRefusedIsShapedLikeEveryOtherRefusal() {
        // The manager refuses a group that still holds resources. What matters here is that the
        // gateway shapes that refusal rather than swallowing it into the 204 — a delete reported
        // successful over a group whose resources are still running is the billing-dispute clause
        // docs/plan/06 § Two-phase create is careful about, one scope up.
        var gateway = new GatewayHarness();

        gateway.Scopes.OnDelete = _ => Result.Failure(
            ErrorCode.Conflict,
            "the resource group still holds 2 resource(s)"
        );

        var response = await gateway.SendAsync(
            "DELETE",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status409Conflict, response.Body);
        response.Body.ShouldContain("still holds 2 resource(s)");
    }

    [Fact]
    public async Task APostToAScopeIsRefusedAndIsNoLongerReadAsAnActionOnAMalformedResource() {
        // ⚠ THIS IS THE ONE BEHAVIOUR THE NEW ROUTE KIND CHANGED FOR AN EXISTING PATH, AND IT IS AN
        // IMPROVEMENT RATHER THAN A REGRESSION. GatewayRouter.ResolveAction strips the last segment
        // unconditionally, so POST on a group address used to be read as an action named after the
        // group, on the resource '/tenants/{t}/subscriptions/{s}/resourceGroups' — which is not a
        // resource path, so the answer was InvalidResourceId. It is now a 405 that names the verbs a
        // scope serves. Both refuse; only one tells the caller what to do.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            GatewayHarness.GroupPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status405MethodNotAllowed, response.Body);
        gateway.Manager.Actions.ShouldBeEmpty();
    }

    // ── The shapes that must still refuse ──────────────────────────────────────────────────────

    [Theory]
    // ⚠ A CASE WAS REMOVED HERE AND ITS REMOVAL IS THE POINT.
    // `/…/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers` was asserted as a 400 —
    // "a resource path with a broken tail must not be swallowed by the scope grammar". That address
    // is now a COLLECTION: `ResourceCollectionId` defines a collection as exactly the odd-tail
    // complement `ResourceId.ParsePath` refuses, and the two landed in the same batch. The scope
    // grammar still does not swallow it, which is what this theory was really guarding, and
    // `CollectionRoutingTests` is where that address is asserted positively now.
    // Three segments: not a scope shape and not a resource one.
    [InlineData("/tenants/{t}/subscriptions")]
    // The literal is wrong.
    [InlineData("/tenant/{t}")]
    // A trailing slash is an empty segment, which both grammars refuse.
    [InlineData("/tenants/{t}/")]
    // An illegal group name — upper case is not DNS-1123, and folding it would be the mangling
    // docs/plan/06 § Identifiers forbids.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/PROD")]
    public async Task AnAddressThatIsNeitherAScopeNorAResourceIsStillAFourHundred(string template) {
        var gateway = new GatewayHarness();

        var path = template
            .Replace("{t}", GatewayHarness.TenantA.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", GatewayHarness.Subscription.ToString("D"), StringComparison.Ordinal);

        var response = await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);
        gateway.Scopes.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AGuidThatIsNotTheDFormIsRefused() {
        // ⚠ The same rule ResourceId applies and for the same reason: five spellings of one address
        // are five cache entries and five audit rows. The 'N' form parses as a GUID for
        // Guid.TryParse and must not here.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            $"/tenants/{GatewayHarness.TenantA:N}",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldNotBe(StatusCodes.Status200OK, response.Body);
        gateway.Scopes.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task AResourcePathStillReachesTheResourceManager() {
        // The regression guard the sixth route kind most needs: the five that existed must be
        // untouched.
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"properties":{}}"""
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted, response.Body);
        gateway.Manager.Paths.ShouldContain(GatewayHarness.ResourcePath(GatewayHarness.TenantA));
        gateway.Scopes.Paths.ShouldBeEmpty();
    }
}
