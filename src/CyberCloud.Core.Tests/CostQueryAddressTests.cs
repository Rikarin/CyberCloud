using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="CostQueryAddress" /> — the cost query's address, docs/plan/22 § Cost visibility, and
///     the third reserved namespace a path can name.
/// </summary>
/// <remarks>
///     ⚠ <b>The overlap sweep is the point, as it is for <c>ResourceGraphAddressTests</c>.</b> On a
///     resource group the address is also a well-formed resource collection path, so what has to hold
///     is that the router's namespace test catches it first and that no scope, resource or role
///     assignment path is under the namespace.
/// </remarks>
public class CostQueryAddressTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    static string Fill(string template) =>
        template.Replace("{t}", Tenant.ToString("D"), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D"), StringComparison.Ordinal);

    [Fact]
    public void BothScopesRoundTrip() {
        foreach (var scope in new[] { ScopeId.Subscription(Tenant, Subscription), ScopeId.Group(Tenant, Subscription, "prod") }) {
            var address = new CostQueryAddress(scope);

            address.Path.ShouldBe(scope.Path + "/providers/CyberCloud.CostManagement/query");
            CostQueryAddress.IsUnderNamespace(address.Path).ShouldBeTrue();
            CostQueryAddress.ParsePath(address.Path).GetValueOrThrow().ShouldBe(address);
        }
    }

    [Fact]
    public void TheNamespaceIsRecognisedCaseInsensitivelyAndRenderedCanonically() {
        var pasted = $"/Tenants/{Tenant:D}/Subscriptions/{Subscription:D}/Providers/cybercloud.costmanagement/Query";

        CostQueryAddress.IsUnderNamespace(pasted).ShouldBeTrue();
        CostQueryAddress.ParsePath(pasted)
            .GetValueOrThrow()
            .Path
            .ShouldBe($"/tenants/{Tenant:D}/subscriptions/{Subscription:D}{CostQueryAddress.Suffix}");
    }

    [Theory]
    // A tenant: usage is per subscription, so a tenant-wide query is a fan-out and not an address.
    [InlineData("/tenants/{t}/providers/CyberCloud.CostManagement/query")]
    // A management group, for the same reason.
    [InlineData("/tenants/{t}/managementGroups/finance/providers/CyberCloud.CostManagement/query")]
    // A trailing slash, a name after the type, another type, and the namespace alone.
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/query/")]
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/query/main")]
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/forecast")]
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.CostManagement/")]
    // A resource in front of the namespace.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Sample/widgets/a/providers/CyberCloud.CostManagement/query")]
    public void EveryOtherShapeUnderTheNamespaceIsRefusedByName(string template) {
        var path = Fill(template);

        CostQueryAddress.IsUnderNamespace(path).ShouldBeTrue();

        var refused = CostQueryAddress.ParsePath(path);
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        refused.Error.Message.ShouldContain(CostQueryAddress.Suffix);
    }

    [Theory]
    [InlineData("/tenants/{t}/subscriptions/{s}")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Sample/widgets")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Billing/budgets/monthly")]
    [InlineData("/tenants/{t}/providers/CyberCloud.ResourceGraph/resources")]
    [InlineData("/tenants/{t}/subscriptions/{s}/providers/CyberCloud.Authorization/roleAssignments")]
    public void NoOtherGrammarsPathIsUnderTheNamespace(string template) =>
        CostQueryAddress.IsUnderNamespace(Fill(template)).ShouldBeFalse();

    [Fact]
    public void OnAResourceGroupTheAddressIsAlsoACollectionPathWhichIsWhyTheNamespaceIsReserved() {
        // The fact the reservation exists for: without it, this path would reach the resource
        // manager as a listing of a type called `query`.
        var path = new CostQueryAddress(ScopeId.Group(Tenant, Subscription, "prod")).Path;

        ResourceCollectionId.TryParsePath(path, out _).ShouldBeTrue();
        ResourceGraphAddress.IsUnderNamespace(path).ShouldBeFalse();
        RoleAssignmentId.IsUnderNamespace(path).ShouldBeFalse();
    }

    [Fact]
    public void TheGatewaysRebuildReplacesThePathsTenant() {
        var other = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var address = new CostQueryAddress(ScopeId.Group(Tenant, Subscription, "prod")).WithTenant(other);

        address.Scope.TenantId.ShouldBe(other);
        address.Scope.ResourceGroup.ShouldBe("prod");
    }
}
