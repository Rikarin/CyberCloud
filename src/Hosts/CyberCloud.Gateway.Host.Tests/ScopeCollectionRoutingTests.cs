using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds a <i>scope collection</i> path to — a tenant's subscriptions and a
///     subscription's resource groups, over HTTP — and what it refuses on one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite proves routing and nothing about what a page may contain.</b> The manager
///         is <c>RecordingScopeManager</c>, so a <c>200</c> below is a body this test assembly wrote.
///         The filter that decides what a page holds is <c>ScopeManagerService.ListAsync</c>'s,
///         asserted in <c>CyberCloud.ResourceManager.Tests.ScopeManagerServiceTests</c>; the two
///         halves meet at <see cref="IScopeManager" /> and are driven together over HTTP in
///         <c>CyberCloud.AppHost.Tests.TenantOverHttpTests</c>.
///     </para>
///     <para>
///         ⚠ <b>Every assertion is on what was <i>dispatched</i>, not only on the status code</b>,
///         for the reason <c>CollectionRoutingTests</c> gives: the fake answers a page for any list,
///         so a status assertion would hold for a gateway that routed a collection <c>GET</c> to
///         <c>ReadAsync</c> and got a scope back. <c>RecordingScopeManager.Collections</c> — the
///         parent paths the listing was addressed at — is the only honest evidence.
///     </para>
/// </remarks>
public sealed class ScopeCollectionRoutingTests {
    static string SubscriptionsPath(Guid tenantId) => $"/tenants/{tenantId:D}/subscriptions";

    static string Message(string body) {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
    }

    static string ResourceGroupsPath(Guid tenantId) =>
        $"/tenants/{tenantId:D}/subscriptions/{GatewayHarness.Subscription:D}/resourceGroups";

    // ── The two shapes that route ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FourSegmentGetIsTheSubscriptionCollection() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            SubscriptionsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        // ⚠ Addressed at the PARENT — the tenant — which is what the manager's ListAsync takes.
        gateway.Scopes.Collections.ShouldBe([$"/tenants/{GatewayHarness.TenantA:D}"]);
        gateway.Scopes.Paths.ShouldBeEmpty("a collection GET reached the scope item path");
        gateway.Manager.Collections.ShouldBeEmpty("a scope collection reached the resource manager");

        using var page = JsonDocument.Parse(response.Body);
        page.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
        page.RootElement.TryGetProperty("nextLink", out _).ShouldBeFalse("one page, no next");
    }

    [Fact]
    public async Task SixSegmentGetIsTheResourceGroupCollection() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            ResourceGroupsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        gateway.Scopes.Collections.ShouldBe([GatewayHarness.SubscriptionPath(GatewayHarness.TenantA)]);
        gateway.Scopes.Paths.ShouldBeEmpty();
        gateway.Manager.Collections.ShouldBeEmpty();
    }

    // ── What is refused, and how ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         A write on the collection path is a <c>400</c> that says where the door is, and
    ///         reaches nothing.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     <c>400</c> and not <c>405</c>, for the reason the resource collection gives: a <c>405</c>
    ///     says the address exists and the verb does not, which is one more fact than a malformed
    ///     write earns. The sentence names the item address a scope is created at, because the
    ///     client this serves is one that dropped the id from <c>PUT /tenants/{t}/subscriptions/{s}</c>.
    /// </remarks>
    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AWriteOnTheCollectionIs400WithTheDerivedAddressSentence(string method) {
        var gateway = new GatewayHarness();

        var subscriptions = await gateway.SendAsync(
            method,
            SubscriptionsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"displayName":"x"}"""
        );

        subscriptions.Status.ShouldBe(StatusCodes.Status400BadRequest, subscriptions.Body);

        // ⚠ Read back through the parser rather than searched for in the text: the sentence carries
        // an em dash and a section sign, which Utf8JsonWriter escapes on the wire.
        Message(subscriptions.Body)
            .ShouldBe(GatewayRouter.ScopeCollectionWriteRefusal(ScopeCollectionId.SubscriptionsOf(GatewayHarness.TenantA)));

        Message(subscriptions.Body).ShouldContain("/tenants/{t}/subscriptions/{s}.");

        var groups = await gateway.SendAsync(
            method,
            ResourceGroupsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-central"}"""
        );

        groups.Status.ShouldBe(StatusCodes.Status400BadRequest, groups.Body);
        Message(groups.Body).ShouldContain("/tenants/{t}/subscriptions/{s}/resourceGroups/{rg}.");

        gateway.Scopes.Collections.ShouldBeEmpty($"{method} on a scope collection reached ListAsync");
        gateway.Scopes.Paths.ShouldBeEmpty($"{method} on a scope collection reached the scope item path");
        gateway.Manager.Paths.ShouldBeEmpty($"{method} on a scope collection reached the resource manager");
    }

    /// <summary>
    ///     ⚠ <b>Anything under <c>/providers/</c> is never a scope collection</b>, however many
    ///     segments it has.
    /// </summary>
    /// <remarks>
    ///     A resource collection is at least nine segments and a resource at least ten, so the
    ///     segment counts alone keep the grammars apart; this pins that a path carrying the
    ///     resource marker reaches the resource grammar and not this one, whatever else is true of
    ///     it — the same disjointness <c>ScopeCollectionIdTests</c> sweeps, seen from the router.
    /// </remarks>
    [Theory]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers/main")]
    [InlineData("/tenants/{t}/providers/CyberCloud.Authorization/roleAssignments")]
    public async Task AnAddressContainingProvidersNeverMatches(string template) {
        var gateway = new GatewayHarness();

        var path = template
            .Replace("{t}", GatewayHarness.TenantA.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", GatewayHarness.Subscription.ToString("D"), StringComparison.Ordinal);

        await gateway.SendAsync("GET", path, gateway.Token(GatewayHarness.TenantA));

        gateway.Scopes.Collections.ShouldBeEmpty($"'{path}' was dispatched as a scope collection");
        gateway.Scopes.Paths.ShouldBeEmpty($"'{path}' was dispatched as a scope");
    }

    /// <summary>
    ///     ⚠ <b>A collection path naming another tenant is <c>404</c> at stage 3 and reaches no manager.</b>
    /// </summary>
    /// <remarks>
    ///     On a listing the stakes are higher than on a read: the failure mode of a regression has to
    ///     stay "the tenant is not found" and never "another tenant's subscription ids are returned".
    ///     The second defence — the rebuilt parent carrying the token's tenant — is asserted in
    ///     <see cref="FourSegmentGetIsTheSubscriptionCollection" /> by the path the manager saw.
    /// </remarks>
    [Fact]
    public async Task ATenantOtherThanTheTokensIs404() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            SubscriptionsPath(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Scopes.Collections.ShouldBeEmpty("a collection path naming another tenant was dispatched");
    }

    // ── Paging ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         <c>$top</c> and <c>$skipToken</c> reach the manager, and the <c>nextLink</c> carries
    ///         the continuation, the caller's <c>$top</c> and the api-version, on the collection's
    ///         own path (#76).
    ///     </b>
    /// </summary>
    [Fact]
    public async Task NextLinkCarriesSkipTokenAndApiVersion() {
        var gateway = new GatewayHarness();

        gateway.Scopes.OnList = request => Result<ScopeListPage>.Success(new() { Continuation = "after-this" });

        var response = await gateway.SendAsync(
            "GET",
            SubscriptionsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            query: "api-version=" + OneTypeRegistry.TheVersion + "&$top=7&$skipToken=start"
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        var seen = gateway.Scopes.ListRequests.ShouldHaveSingleItem();
        seen.Top.ShouldBe(7);
        seen.Continuation.ShouldBe("start");

        using var page = JsonDocument.Parse(response.Body);
        var link = page.RootElement.GetProperty("nextLink").GetString()!;

        link.ShouldStartWith("https://api.cybercloud.io" + SubscriptionsPath(GatewayHarness.TenantA) + "?");
        link.ShouldContain("api-version=" + OneTypeRegistry.TheVersion);
        link.ShouldContain("$top=7");
        link.ShouldContain("$skipToken=after-this");

        // …and no nextLink at all on the last page — an empty string is a URL a polite client requests.
        gateway.Scopes.OnList = _ => Result<ScopeListPage>.Success(new());

        var last = await gateway.SendAsync("GET", SubscriptionsPath(GatewayHarness.TenantA), gateway.Token(GatewayHarness.TenantA));

        last.Body.ShouldNotContain("nextLink");
        last.Body.ShouldContain("\"value\":[]");
    }

    /// <summary>
    ///     ⚠ <b>The manager's refusal is shaped like every other: a <c>404</c> for an unreadable parent.</b>
    /// </summary>
    [Fact]
    public async Task AManagerNotFoundIsServedAs404() {
        var gateway = new GatewayHarness();

        gateway.Scopes.OnList = request =>
            Result<ScopeListPage>.Failure(ErrorCode.ResourceNotFound, $"'{request.ParentPath}' does not exist.");

        var response = await gateway.SendAsync(
            "GET",
            ResourceGroupsPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
    }
}
