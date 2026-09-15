using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="RoleAssignmentId" /> and <see cref="RoleAssignmentName" /> — docs/plan/07 § Azure
///     RBAC, expressed in it, addressed the way § Identifiers addresses a resource.
/// </summary>
/// <remarks>
///     ⚠ <b>The overlap sweep is the reason this file is not four round-trip assertions.</b> A
///     group-scoped assignment <i>is</i> a well-formed resource path, so
///     <c>GatewayRouter.Resolve</c> asks this grammar first and, under the reserved namespace, asks
///     nothing else. What has to hold for that to be a design rather than a precedence rule is that
///     the overlap is exactly the reserved namespace and nothing wider: no scope path parses as an
///     assignment, no assignment parses as a scope, and a resource path under any <i>other</i>
///     namespace is untouched.
/// </remarks>
public class RoleAssignmentIdTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Subscription = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
    static readonly RoleAssignmentName Reader = new("reader", "user", "7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d");

    static ResourceId Resource { get; } =
        new(
            Tenant,
            Subscription,
            "prod",
            ResourceTypeName.Create("CyberCloud.Cache", "redis").GetValueOrThrow(),
            "main",
            Guid.Empty
        );

    // ── Round trips, one per scope kind ────────────────────────────────────────────────────────

    [Fact]
    public void ATenantScopedAssignmentRoundTrips() {
        var id = RoleAssignmentId.OnScope(ScopeId.Tenant(Tenant), Reader);

        id.Path.ShouldBe($"/tenants/{Tenant:D}/providers/CyberCloud.Authorization/roleAssignments/{Reader.Render()}");
        RoleAssignmentId.ParsePath(id.Path).GetValueOrThrow().ShouldBe(id);
        id.IsResourceScoped.ShouldBeFalse();
        id.TenantId.ShouldBe(Tenant);
    }

    [Fact]
    public void ASubscriptionScopedAssignmentRoundTrips() {
        var id = RoleAssignmentId.OnScope(ScopeId.Subscription(Tenant, Subscription), Reader);

        RoleAssignmentId.ParsePath(id.Path).GetValueOrThrow().ShouldBe(id);
        id.ScopePath.ShouldBe(ScopeId.Subscription(Tenant, Subscription).Path);
    }

    [Fact]
    public void AResourceGroupScopedAssignmentRoundTripsAndIsAlsoAResourcePath() {
        var id = RoleAssignmentId.OnScope(ScopeId.Group(Tenant, Subscription, "prod"), Reader);

        RoleAssignmentId.ParsePath(id.Path).GetValueOrThrow().ShouldBe(id);

        // ⚠ THE OVERLAP, STATED RATHER THAN DISCOVERED. Ten segments, /providers/ at offset six: the
        // resource grammar accepts this as a resource of type CyberCloud.Authorization/
        // roleAssignments named after the tuple. That is why the router asks this grammar first
        // and why ProviderRegistry.Build refuses a provider claiming the namespace.
        ResourceId.TryParsePath(id.Path, out var asResource).ShouldBeTrue();
        asResource.Type.Namespace.ShouldBe(RoleAssignmentId.ProviderNamespace);
    }

    [Fact]
    public void AResourceScopedAssignmentRoundTrips() {
        var id = RoleAssignmentId.OnResource(Resource, Reader);

        id.Path.ShouldBe(Resource.Path + RoleAssignmentId.Suffix + Reader.Render());

        var parsed = RoleAssignmentId.ParsePath(id.Path).GetValueOrThrow();

        parsed.IsResourceScoped.ShouldBeTrue();
        parsed.Resource.ShouldBe(Resource);
        parsed.Name.ShouldBe(Reader);
        parsed.TenantId.ShouldBe(Tenant);

        // ⚠ And the GUID is what a parsed path always yields — docs/plan/06 § Identifiers. The
        // manager resolves it through the index before any tuple is written; a tuple on
        // resource:0000… would be a grant on nothing.
        parsed.Resource.Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void AChildResourceScopedAssignmentKeepsTheParentInTheScope() {
        var child = new ResourceId(
            Tenant,
            Subscription,
            "prod",
            ResourceTypeName.Create("CyberCloud.DBforPostgreSQL", "servers/databases").GetValueOrThrow(),
            "orders",
            Guid.Empty,
            "pg"
        );

        var id = RoleAssignmentId.OnResource(child, Reader);
        var parsed = RoleAssignmentId.ParsePath(id.Path).GetValueOrThrow();

        parsed.Resource.ShouldBe(child);
        parsed.Resource.ParentNames.ShouldBe("pg");
    }

    // ── The tenant rebuild the router does ─────────────────────────────────────────────────────

    [Fact]
    public void WithTenantRewritesWhicheverScopeMemberIsSet() {
        var other = Guid.Parse("99999999-0000-4000-8000-000000000009");

        var onScope = RoleAssignmentId.OnScope(ScopeId.Group(Tenant, Subscription, "prod"), Reader).WithTenant(other);
        onScope.TenantId.ShouldBe(other);
        onScope.Scope.TenantId.ShouldBe(other);
        onScope.IsResourceScoped.ShouldBeFalse();

        var onResource = RoleAssignmentId.OnResource(Resource, Reader).WithTenant(other);
        onResource.TenantId.ShouldBe(other);
        onResource.Resource.TenantId.ShouldBe(other);
        onResource.IsResourceScoped.ShouldBeTrue();
    }

    // ── The name: the tuple, spelled as one segment ────────────────────────────────────────────

    [Theory]
    [InlineData("reader-user-alice", "reader", "user", "alice")]
    [InlineData("owner-servicePrincipal-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d", "owner", "servicePrincipal", "7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d")]
    [InlineData("contributor-group-eng", "contributor", "group", "eng")]
    // ⚠ The id may carry hyphens; only the first two are structural.
    [InlineData("reader-user-2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "reader", "user", "2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3")]
    public void ANameSplitsIntoRolePrincipalTypeAndPrincipalId(string name, string role, string type, string id) {
        var parsed = RoleAssignmentName.Parse(name).GetValueOrThrow();

        parsed.Role.ShouldBe(role);
        parsed.PrincipalType.ShouldBe(type);
        parsed.PrincipalId.ShouldBe(id);
        parsed.Render().ShouldBe(name);
    }

    [Fact]
    public void ThePrincipalTypeIsTheReBacSpellingAndIsNotFolded() {
        // ⚠ ONE VOCABULARY. 'servicePrincipal' is how the tuple store spells the subject type, and a
        // lower-cased copy in the address would be a second spelling agreeing with the first by
        // hand. So the segment is not DNS-1123, the case survives the round trip, and this grammar
        // does not decide whether the type exists — that is the manager's, against the closed set.
        var parsed = RoleAssignmentName.Parse("reader-servicePrincipal-x").GetValueOrThrow();

        parsed.PrincipalType.ShouldBe("servicePrincipal");

        // A relation name starts with a lower-case letter (RelationNaming.NamePattern), so the
        // Azure spelling 'ServicePrincipal' is refused by the grammar rather than folded into ours.
        RoleAssignmentName.Parse("reader-ServicePrincipal-x").IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("reader")]
    [InlineData("reader-user")]
    [InlineData("reader-user-")]
    [InlineData("-user-alice")]
    [InlineData("reader--alice")]
    // The id is a ReBAC object id: lower case, digits and hyphens, no leading or trailing hyphen.
    [InlineData("reader-user-Alice")]
    [InlineData("reader-user-alice-")]
    [InlineData("reader-user-al_ice")]
    // The role and the type are relation names: a leading letter, then letters and digits.
    [InlineData("1reader-user-alice")]
    [InlineData("reader-1user-alice")]
    public void AMalformedNameIsRefused(string name) =>
        RoleAssignmentName.Parse(name).IsFailure.ShouldBeTrue($"'{name}' was accepted");

    // ── Disjointness from the two grammars this one sits between ───────────────────────────────

    [Fact]
    public void NoAssignmentPathIsAlsoAScopePath() {
        foreach (var path in EveryShape()) {
            ScopeId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' parses as a scope");
        }
    }

    [Fact]
    public void NoScopePathIsAnAssignmentPath() {
        foreach (var path in new[] {
                     ScopeId.Tenant(Tenant).Path,
                     ScopeId.Subscription(Tenant, Subscription).Path,
                     ScopeId.Group(Tenant, Subscription, "prod").Path
                 }) {
            RoleAssignmentId.IsUnderNamespace(path).ShouldBeFalse();
            RoleAssignmentId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' parses as an assignment");
        }
    }

    [Theory]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis/main")]
    [InlineData(
        "/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers/pg/databases/orders"
    )]
    // ⚠ A resource whose NAME is 'roleAssignments' under another namespace is that provider's.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/roleAssignments/reader-user-alice")]
    public void AResourcePathUnderAnyOtherNamespaceIsNotUnderThisOne(string template) {
        var path = Fill(template);

        ResourceId.TryParsePath(path, out _).ShouldBeTrue("the fixture is not a resource path");
        RoleAssignmentId.IsUnderNamespace(path).ShouldBeFalse();
        RoleAssignmentId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' parses as an assignment");
    }

    [Fact]
    public void TheReservedNamespaceIsRecognisedCaseInsensitivelyAndRenderedCanonically() {
        // The same rule ResourceId applies to its structural literals: a support engineer pasting
        // '/Providers/cybercloud.authorization/RoleAssignments/' is not told their URL is wrong, and
        // the rendered path is the one spelling.
        var pasted = $"/tenants/{Tenant:D}/Providers/cybercloud.authorization/RoleAssignments/{Reader.Render()}";

        RoleAssignmentId.IsUnderNamespace(pasted).ShouldBeTrue();

        var parsed = RoleAssignmentId.ParsePath(pasted).GetValueOrThrow();

        parsed.Path.ShouldBe($"/tenants/{Tenant:D}{RoleAssignmentId.Suffix}{Reader.Render()}");
    }

    // ── Refusals under the namespace ───────────────────────────────────────────────────────────

    [Theory]
    // No name at all — the collection, which is the OTHER grammar (RoleAssignmentCollectionId) and not this one.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments")]
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/")]
    // A segment after the name.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice/x")]
    // Another type under the reserved namespace.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleDefinitions/reader")]
    // No scope in front of it.
    [InlineData("/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice")]
    // A scope that is neither a scope nor a resource.
    [InlineData("/tenants/{t}/subscriptions/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice")]
    // A malformed name.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader")]
    public void AMalformedAssignmentPathIsRefusedAndIsStillUnderTheNamespace(string template) {
        var path = Fill(template);

        RoleAssignmentId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' was accepted");

        // ⚠ Both halves matter to the router: under the namespace, a parse failure is the 400 the
        // caller gets, and NOT a fall-through into the resource grammar — which would accept most
        // of these as a type no provider serves and answer 404.
        RoleAssignmentId.IsUnderNamespace(path).ShouldBeTrue();
    }

    [Fact]
    public void AnAssignmentOnAnUnknownScopeCannotBeBuilt() =>
        Should.Throw<ArgumentException>(() => RoleAssignmentId.OnScope(default, Reader));

    // ── The collection (issue #86) ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryScopeKindsCollectionRoundTripsAndIsTheMembersInverse() {
        foreach (var assignment in EveryShape().Select(path => RoleAssignmentId.ParsePath(path).GetValueOrThrow())) {
            var collection = RoleAssignmentCollectionId.Of(assignment);

            collection.Path.ShouldBe(assignment.ScopePath + RoleAssignmentId.CollectionSuffix);
            collection.TenantId.ShouldBe(assignment.TenantId);
            collection.IsResourceScoped.ShouldBe(assignment.IsResourceScoped);

            var parsed = RoleAssignmentCollectionId.ParsePath(collection.Path).GetValueOrThrow();
            parsed.ShouldBe(collection);

            // Member is Of's inverse: the collection plus the name is the assignment again.
            parsed.Member(assignment.Name).ShouldBe(assignment);
            parsed.Member(assignment.Name).Path.ShouldBe(assignment.Path);
        }
    }

    [Fact]
    public void TheTwoAssignmentGrammarsAreDisjoint() {
        // ⚠ THE PROPERTY THE ROUTER'S ORDER RESTS ON. Under the reserved namespace it asks the
        // assignment grammar and then the collection grammar; if either accepted the other's path
        // the order would decide the route rather than the message, and that would be a precedence
        // rule nobody wrote down.
        foreach (var path in EveryShape()) {
            RoleAssignmentCollectionId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' parsed as a collection");

            var collection = RoleAssignmentCollectionId.Of(RoleAssignmentId.ParsePath(path).GetValueOrThrow()).Path;
            RoleAssignmentId.TryParsePath(collection, out _).ShouldBeFalse($"'{collection}' parsed as an assignment");
            RoleAssignmentId.IsUnderNamespace(collection).ShouldBeTrue();
        }
    }

    [Fact]
    public void TheCollectionSuffixIsMatchedCaseInsensitivelyLikeEveryStructuralLiteral() {
        var spelled = ScopeId.Group(Tenant, Subscription, "prod").Path + "/PROVIDERS/cybercloud.authorization/ROLEASSIGNMENTS";

        var parsed = RoleAssignmentCollectionId.ParsePath(spelled).GetValueOrThrow();

        parsed.Scope.ShouldBe(ScopeId.Group(Tenant, Subscription, "prod"));
        parsed.Path.ShouldBe(ScopeId.Group(Tenant, Subscription, "prod").Path + RoleAssignmentId.CollectionSuffix);
    }

    [Fact]
    public void WithTenantRewritesWhicheverScopeMemberOfTheCollectionIsSet() {
        var other = Guid.Parse("99999999-0000-4000-8000-000000000009");

        var onScope = RoleAssignmentCollectionId.OnScope(ScopeId.Subscription(Tenant, Subscription)).WithTenant(other);
        onScope.TenantId.ShouldBe(other);
        onScope.Scope.TenantId.ShouldBe(other);

        var onResource = RoleAssignmentCollectionId.OnResource(Resource).WithTenant(other);
        onResource.TenantId.ShouldBe(other);
        onResource.Resource.TenantId.ShouldBe(other);
    }

    [Theory]
    // A trailing slash — neither an assignment nor the collection.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/")]
    // A name after the suffix is an assignment, not the collection.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleAssignments/reader-user-alice")]
    // Another type under the reserved namespace.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Authorization/roleDefinitions")]
    // No scope in front of it.
    [InlineData("/providers/CyberCloud.Authorization/roleAssignments")]
    // A scope that is neither a scope nor a resource.
    [InlineData("/tenants/{t}/subscriptions/providers/CyberCloud.Authorization/roleAssignments")]
    // A resource collection is not a scope.
    [InlineData("/tenants/{t}/subscriptions/{s}/resourceGroups/prod/providers/CyberCloud.Cache/redis/providers/CyberCloud.Authorization/roleAssignments")]
    [InlineData("")]
    public void AMalformedCollectionPathIsRefused(string template) {
        var path = Fill(template);

        RoleAssignmentCollectionId.TryParsePath(path, out _).ShouldBeFalse($"'{path}' was accepted as a collection");
        RoleAssignmentCollectionId.ParsePath(path).Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public void ACollectionOnAnUnknownScopeCannotBeBuilt() =>
        Should.Throw<ArgumentException>(() => RoleAssignmentCollectionId.OnScope(default));

    static IEnumerable<string> EveryShape() {
        yield return RoleAssignmentId.OnScope(ScopeId.Tenant(Tenant), Reader).Path;
        yield return RoleAssignmentId.OnScope(ScopeId.Subscription(Tenant, Subscription), Reader).Path;
        yield return RoleAssignmentId.OnScope(ScopeId.Group(Tenant, Subscription, "prod"), Reader).Path;
        yield return RoleAssignmentId.OnResource(Resource, Reader).Path;
    }

    static string Fill(string template) =>
        template
            .Replace("{t}", Tenant.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s}", Subscription.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
