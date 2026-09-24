using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="PolicyAddress" /> — the policy engine's three objects as extensions on a scope,
///     issue #46.
/// </summary>
/// <remarks>
///     ⚠ <b>The overlap is the point, as for <c>RoleAssignmentIdTests</c>.</b> An assignment on a
///     resource group is a well-formed resource path, so the gateway asks this grammar first; what makes
///     that a design rather than a precedence rule is that the overlap is exactly the reserved namespace.
/// </remarks>
public class PolicyAddressTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");

    static string TenantPath => $"/tenants/{Tenant:D}";

    static string SubscriptionPath => $"{TenantPath}/subscriptions/{Subscription:D}";

    static string GroupPath => $"{SubscriptionPath}/resourceGroups/prod";

    static string ManagementGroupPath => $"{TenantPath}/managementGroups/platform";

    [Fact]
    public void EveryAllowedShapeParsesAndRendersCanonically() {
        (string Path, PolicyObjectKind Kind, ScopeKind Scope, string Name)[] cases = [
            ($"{TenantPath}/providers/CyberCloud.Policy/policyDefinitions/no-public-ip", PolicyObjectKind.Definition, ScopeKind.Tenant, "no-public-ip"),
            ($"{ManagementGroupPath}/providers/CyberCloud.Policy/policyDefinitions/tags", PolicyObjectKind.Definition, ScopeKind.ManagementGroup, "tags"),
            ($"{SubscriptionPath}/providers/CyberCloud.Policy/policyDefinitions/sku", PolicyObjectKind.Definition, ScopeKind.Subscription, "sku"),
            ($"{ManagementGroupPath}/providers/CyberCloud.Policy/policyAssignments/a", PolicyObjectKind.Assignment, ScopeKind.ManagementGroup, "a"),
            ($"{SubscriptionPath}/providers/CyberCloud.Policy/policyAssignments/b", PolicyObjectKind.Assignment, ScopeKind.Subscription, "b"),
            ($"{GroupPath}/providers/CyberCloud.Policy/policyAssignments/c", PolicyObjectKind.Assignment, ScopeKind.ResourceGroup, "c"),
            ($"{GroupPath}/providers/CyberCloud.Policy/policyStates", PolicyObjectKind.State, ScopeKind.ResourceGroup, ""),
            ($"{ManagementGroupPath}/providers/CyberCloud.Policy/policyStates", PolicyObjectKind.State, ScopeKind.ManagementGroup, ""),
            ($"{TenantPath}/providers/CyberCloud.Policy/policyDefinitions", PolicyObjectKind.Definition, ScopeKind.Tenant, "")
        ];

        foreach (var (path, kind, scope, name) in cases) {
            var parsed = PolicyAddress.ParsePath(path);
            parsed.IsSuccess.ShouldBeTrue($"{path}: {parsed.Error?.Message}");

            var address = parsed.GetValueOrThrow();
            address.Kind.ShouldBe(kind);
            address.Scope.Kind.ShouldBe(scope);
            address.Name.ShouldBe(name);
            address.IsCollection.ShouldBe(name.Length == 0);
            address.Path.ShouldBe(path);
            address.TenantId.ShouldBe(Tenant);
        }
    }

    [Fact]
    public void TheNamespaceAndTypeAreMatchedCaseInsensitivelyAndRenderedCanonically() {
        var address = PolicyAddress.ParsePath($"{GroupPath}/providers/cybercloud.policy/POLICYASSIGNMENTS/c").GetValueOrThrow();

        address.Path.ShouldBe($"{GroupPath}/providers/CyberCloud.Policy/policyAssignments/c");
    }

    [Theory]
    [InlineData("/providers/CyberCloud.Policy/policyAssignments/a", "tenant")]
    [InlineData("/providers/CyberCloud.Policy/policyDefinitions/d", "resource group")]
    [InlineData("/providers/CyberCloud.Policy/policyStates/x", "resource group")]
    [InlineData("/providers/CyberCloud.Policy/policyExemptions/x", "resource group")]
    [InlineData("/providers/CyberCloud.Policy/policyAssignments/Bad_Name", "resource group")]
    [InlineData("/providers/CyberCloud.Policy/policyAssignments/a/b", "resource group")]
    [InlineData("/providers/CyberCloud.Policy/policyAssignments/", "resource group")]
    public void AShapeTheScopeDoesNotTakeIsRefused(string suffix, string scope) {
        var prefix = scope == "tenant" ? TenantPath : GroupPath;
        var parsed = PolicyAddress.ParsePath(prefix + suffix);

        parsed.IsFailure.ShouldBeTrue(prefix + suffix);
        PolicyAddress.IsUnderNamespace(prefix + suffix).ShouldBeTrue("under the namespace, so the router answers a 400 and never falls through");
    }

    [Fact]
    public void AnAssignmentOnAResourceGroupIsAlsoAWellFormedResourcePathWhichIsWhyTheNamespaceIsReserved() {
        var path = $"{GroupPath}/providers/CyberCloud.Policy/policyAssignments/c";

        ResourceId.ParsePath(path).IsSuccess.ShouldBeTrue("the overlap this grammar exists to claim first");
        PolicyAddress.IsUnderNamespace(path).ShouldBeTrue();
    }

    [Fact]
    public void NoScopeOrOtherResourcePathIsUnderTheNamespace() {
        string[] others = [
            TenantPath,
            SubscriptionPath,
            GroupPath,
            ManagementGroupPath,
            $"{GroupPath}/providers/CyberCloud.Cache/redis/main",
            $"{GroupPath}/providers/CyberCloud.Authorization/roleAssignments/reader-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d",
            $"{TenantPath}/providers/CyberCloud.ResourceGraph/resources",
            // A provider whose namespace merely starts with the reserved one is not under it.
            $"{GroupPath}/providers/CyberCloud.PolicyX/things/a"
        ];

        foreach (var path in others) {
            PolicyAddress.IsUnderNamespace(path).ShouldBeFalse(path);
            PolicyAddress.TryParsePath(path, out _).ShouldBeFalse(path);
        }
    }

    [Fact]
    public void WithTenantRebuildsTheScopeAndOnlyTheScope() {
        var other = Guid.Parse("11111111-2222-4333-8444-555555555555");
        var address = PolicyAddress.ParsePath($"{GroupPath}/providers/CyberCloud.Policy/policyAssignments/c").GetValueOrThrow();

        var rebuilt = address.WithTenant(other);

        rebuilt.TenantId.ShouldBe(other);
        rebuilt.Name.ShouldBe("c");
        rebuilt.Path.ShouldStartWith($"/tenants/{other:D}/subscriptions/{Subscription:D}/resourceGroups/prod/");
    }
}
