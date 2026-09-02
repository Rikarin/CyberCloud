using CyberCloud.Gateway.Host.Tests.Infrastructure;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway binds a collection path to, and what it refuses on one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite proves routing and nothing about what a listing may contain.</b> The
///         manager here is <c>RecordingResourceManager</c>, so a <c>200</c> below is a body this test
///         assembly wrote. The filter that decides what a listing holds is
///         <c>ResourceManagerService.ListAsync</c>'s, and it is asserted in
///         <c>CyberCloud.ResourceManager.Tests.CollectionListingTests</c> and, against the real ReBAC
///         engine, in <c>CyberCloud.Isolation.CollectionListingTests</c>. The two halves meet at
///         <see cref="IResourceManager" /> — see the remarks on
///         <c>ReconcileThroughTheRealHostTests</c> — and a route added only on this side would prove
///         nothing at all.
///     </para>
///     <para>
///         ⚠ <b>Every assertion is on what was <i>dispatched</i>, not on the status code.</b> The
///         fake answers a page for any list, so a status assertion would hold for a gateway that
///         routed a collection <c>GET</c> to <c>ReadAsync</c> and got a resource back.
///         <see cref="RecordingResourceManager.Collections" /> is the only honest evidence that the
///         collection path was taken.
///     </para>
/// </remarks>
public sealed class CollectionRoutingTests {
    /// <summary>The collection the happy-path resource lives in.</summary>
    static string CollectionPath(Guid tenantId) =>
        $"/tenants/{tenantId:D}/subscriptions/{GatewayHarness.Subscription:D}/resourceGroups/prod"
        + "/providers/CyberCloud.DBforPostgreSQL/servers";

    [Fact]
    public async Task ACollectionIsReachableOnGet() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);

        gateway.Manager.Collections.ShouldContain(
            CollectionPath(GatewayHarness.TenantA),
            "the router did not bind GET on a collection path to ListAsync at all"
        );

        gateway.Manager.Paths.ShouldBeEmpty(
            "a collection GET reached the single-resource path, which parses a different grammar"
        );
    }

    /// <summary>
    ///     ⚠ <b>The dispatched address carries the <i>token's</i> tenant, never the URL's.</b>
    /// </summary>
    /// <remarks>
    ///     Stage 3 has already refused a disagreement; this is the second defence and it is the one
    ///     that still holds if somebody deletes the first. On a listing the stakes are higher than on
    ///     a read: the failure mode of a regression has to stay "the group is not found" and never
    ///     "another tenant's resource names are returned".
    /// </remarks>
    [Fact]
    public async Task ACollectionPathNamingAnotherTenantIsNeverDispatchedAtThatTenant() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantB),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);

        gateway.Manager.Collections.ShouldNotContain(
            x => x.Contains(GatewayHarness.TenantB.ToString("D"), StringComparison.Ordinal),
            "a collection path naming another tenant was dispatched at that tenant"
        );
    }

    /// <summary>
    ///     ⚠ <b>There is no bulk write and no bulk delete: a collection path is a <c>GET</c> and
    ///     nothing else.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The refusal is the resource parser's, because on a write verb the path is only ever
    ///         read as a resource address — and a collection path is not one. That is a <c>400</c>
    ///         and deliberately not a <c>405</c>: a <c>405</c> would say the address exists and the
    ///         verb does not, which is one more fact than a malformed path earns.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted on dispatch, because a status assertion would pass for the wrong
    ///         reason.</b> A <c>DELETE</c> routed to a collection would reach the fake's
    ///         <c>OnWrite</c> and answer <c>202</c> — which is neither 200 nor 404, so a
    ///         <c>ShouldNotBe</c> on either would hold while a bulk delete was wide open.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task AWriteVerbOnACollectionPathIsRefusedAndReachesNothing(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: """{"location":"eu-west-1"}"""
        );

        response.Status.ShouldBe(StatusCodes.Status400BadRequest, response.Body);

        gateway.Manager.Collections.ShouldBeEmpty($"{method} on a collection path reached ListAsync");
        gateway.Manager.Paths.ShouldBeEmpty($"{method} on a collection path reached the write path");
    }

    /// <summary>
    ///     ⚠ <b>A collection of a type the registry does not serve is the canonical <c>404</c>, and
    ///     it is refused at stage 6 rather than by the manager.</b>
    /// </summary>
    /// <remarks>
    ///     Stage 6 owns "is this a path this gateway serves", and it looks a collection's type up the
    ///     way it looks a resource's up. Skipping that would resolve the api-version against no
    ///     registration — so a retired version would be accepted here and refused two stages later —
    ///     and would move an unknown type's <c>404</c> into the resource manager.
    /// </remarks>
    [Fact]
    public async Task ACollectionOfAnUnknownTypeIsRefusedBeforeDispatch() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            $"/tenants/{GatewayHarness.TenantA:D}/subscriptions/{GatewayHarness.Subscription:D}"
            + "/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/nosuchtype",
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Manager.Collections.ShouldBeEmpty("an unknown type reached the resource manager");
    }

    /// <summary>
    ///     ⚠ <b><c>$top</c> and <c>$skipToken</c> reach the manager, and a <c>$top</c> that is not a
    ///     number is ignored rather than refused.</b>
    /// </summary>
    /// <remarks>
    ///     The page size is a hint the platform clamps — <c>ListRequest.PageSize</c> — so refusing a
    ///     malformed one would put the platform's own limit into an error message and make a client
    ///     that guessed wrong fail rather than page.
    /// </remarks>
    [Fact]
    public async Task ThePagingParametersReachTheManagerAndAMalformedTopIsIgnored() {
        var gateway = new GatewayHarness();
        ListRequest? seen = null;

        gateway.Manager.OnList = request => {
            seen = request;

            return Result<ResourceListPage>.Success(new());
        };

        var response = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            query: "api-version=" + OneTypeRegistry.TheVersion + "&$top=not-a-number&$skipToken=abc"
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        seen.ShouldNotBeNull();
        seen.Top.ShouldBe(0, "a $top that is not a number is ignored, not refused");
        seen.PageSize.ShouldBe(ListRequest.DefaultPageSize);
        seen.Continuation.ShouldBe("abc");
    }

    /// <summary>
    ///     ⚠ <b>A page with a continuation renders a <c>nextLink</c>; a page without one renders no
    ///     <c>nextLink</c> member at all.</b>
    /// </summary>
    /// <remarks>
    ///     An empty-string <c>nextLink</c> is a URL a polite client will happily request, which is
    ///     how a paging client loops. The Azure shape an <c>AsyncPageable&lt;T&gt;</c> stops on is the
    ///     member's absence.
    /// </remarks>
    [Fact]
    public async Task ANextLinkIsPresentOnlyWhenThereIsANextPage() {
        var gateway = new GatewayHarness();

        gateway.Manager.OnList = _ => Result<ResourceListPage>.Success(new() { Continuation = "some/path" });

        var more = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        more.Body.ShouldContain("nextLink");
        more.Body.ShouldContain("%2F", Case.Insensitive);

        gateway.Manager.OnList = _ => Result<ResourceListPage>.Success(new());

        var last = await gateway.SendAsync(
            "GET",
            CollectionPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        last.Body.ShouldNotContain("nextLink");
        last.Body.ShouldContain("\"value\"");
    }
}
