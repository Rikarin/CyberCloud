using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ResourceGraphAddress" /> — the resource graph's one address, docs/plan/08 § The
///     resource-graph projection, and the second reserved namespace.
/// </summary>
/// <remarks>
///     ⚠ <b>The overlap sweep is the point, as it is for <c>RoleAssignmentIdTests</c>.</b> The router
///     asks this grammar under its namespace and nothing else there, so what has to hold is that no
///     scope, scope collection, resource, collection or role assignment path is under this
///     namespace, and that nothing this grammar accepts parses as any of those.
/// </remarks>
public class ResourceGraphAddressTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    static string Fill(string template) =>
        template.Replace("{t}", Tenant.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D"), StringComparison.Ordinal);

    [Fact]
    public void TheAddressRoundTrips() {
        var address = new ResourceGraphAddress(Tenant);

        address.Path.ShouldBe($"/tenants/{Tenant:D}/providers/CyberCloud.ResourceGraph/resources");
        ResourceGraphAddress.IsUnderNamespace(address.Path).ShouldBeTrue();
        ResourceGraphAddress.TryParsePath(address.Path, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(address);
        parsed.ToString().ShouldBe(address.Path);
    }

    [Fact]
    public void TheNamespaceIsRecognisedCaseInsensitivelyAndRenderedCanonically() {
        // The same rule ResourceId applies to its structural literals: a support engineer pasting a
        // lower-cased URL is not told it is wrong, and the rendered path is the one spelling.
        var pasted = $"/Tenants/{Tenant:D}/Providers/cybercloud.resourcegraph/Resources";

        ResourceGraphAddress.IsUnderNamespace(pasted).ShouldBeTrue();
        ResourceGraphAddress.ParsePath(pasted)
            .GetValueOrThrow()
            .Path
                .ShouldBe($"/tenants/{Tenant:D}{ResourceGraphAddress.Suffix}");
    }

    // ── Refusals under the namespace ───────────────────────────────────────────────────────────

    [Theory]
    // A trailing slash.
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/resources/")]
    // A name after the type — there is no resource to address, only the table.
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/resources/main")]
    // Another type under the reserved namespace.
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/queries")]
    // The namespace and nothing after it.
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/")]
    // A subscription or a group in front of the namespace: the query is over the whole tenant.
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.ResourceGraph/resources")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.ResourceGraph/resources")]
    // No tenant in front of it.
    [InlineData("/providers/CyberCloud.ResourceGraph/resources")]
    // A tenant id that is not the D form.
    [InlineData("/tenants/not-a-guid/providers/CyberCloud.ResourceGraph/resources")]
    public void AMalformedPathIsRefusedAndIsStillUnderTheNamespace(string template) {
        var path = Fill(template);

        var parsed = ResourceGraphAddress.ParsePath(path);

        parsed.IsFailure.ShouldBeTrue($"'{path}' was accepted");
        parsed.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        parsed.Error.Message.ShouldContain(
            ResourceGraphAddress.Suffix,
            customMessage: "the refusal names the one address"
        );

        // ⚠ Both halves matter to the router: under the namespace, a parse failure is the 400 the
        // caller gets, and NOT a fall-through into the scope, collection or resource grammars —
        // which would accept several of these and answer 404.
        ResourceGraphAddress.IsUnderNamespace(path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NothingIsRefusedWithTheShape(string? path) {
        ResourceGraphAddress.TryParsePath(path, out _).ShouldBeFalse();
        ResourceGraphAddress.ParsePath(path).Error!.Message.ShouldContain(ResourceGraphAddress.Suffix);
    }

    // ── Disjointness from every other grammar ──────────────────────────────────────────────────

    [Fact]
    public void TheAddressParsesAsNothingElse() {
        var path = new ResourceGraphAddress(Tenant).Path;

        ScopeId.TryParsePath(path, out _).ShouldBeFalse("parses as a scope");
        ScopeCollectionId.TryParsePath(path, out _).ShouldBeFalse("parses as a scope collection");
        ResourceId.TryParsePath(path, out _).ShouldBeFalse("parses as a resource");
        ResourceCollectionId.TryParsePath(path, out _).ShouldBeFalse("parses as a resource collection");
        RoleAssignmentId.IsUnderNamespace(path).ShouldBeFalse("is under the authorization namespace");
    }

    [Theory]
    [InlineData("/tenants/{t}")]
    [InlineData("/tenants/{t}/subscriptions")]
    [InlineData("/tenants/{t}/subscriptions/{s}")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis/main")]
    // ⚠ A resource whose TYPE is 'resources' under another namespace is that provider's.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/resources/main")]
    [InlineData(
        "/tenants/{t}/providers/CyberCloud.Authorization/roleAssignments/reader-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d"
    )]
    [InlineData("/tenants/{t}/providers/CyberCloud.Authorization/roleAssignments")]
    public void NoOtherGrammarsPathIsUnderThisNamespace(string template) {
        var path = Fill(template);

        ResourceGraphAddress.IsUnderNamespace(path).ShouldBeFalse($"'{path}' is under the namespace");
        ResourceGraphAddress.TryParsePath(path, out _).ShouldBeFalse($"'{path}' parses as the address");
    }
}
