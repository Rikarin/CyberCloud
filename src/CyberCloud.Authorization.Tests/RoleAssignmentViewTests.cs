using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     ⚠ <b>Azure RBAC as a view over tuples</b> — docs/plan/07 § Azure RBAC, expressed in it, all
///     four rows.
/// </summary>
/// <remarks>
///     The load-bearing one is row 3:
///     <i>
///         "Inheritance sub → rg → resource | The
///         <c>From("parent", …)</c> rewrites; <b>no tuples written</b>"
///     </i>
///     . Every test below that
///     touches inheritance asserts the "no tuples written" half explicitly, because that is the
///     whole argument for <c>From(…)</c> — and it is the difference between this engine and a role
///     table, which would need one row per resource.
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class RoleAssignmentViewTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    static SubjectRef EngMembers => SubjectRef.Userset(ObjectTypes.Group, "eng", Relations.Member);

    [Fact]
    public async Task ARoleAssignedAtTheSubscriptionGrantsOnAResourceWithNoTupleWrittenForIt() {
        var tenant = await SeedHierarchyAsync(400, "a");
        var resource = ObjectRef.Of(ObjectTypes.Resource, "resa");

        var check = await cluster.Check(tenant, resource)
            .CheckAsync(Permissions.Delete, Alice, Consistency.FullyConsistent);

        check.GetValueOrThrow().Allowed.ShouldBeTrue();

        // ⚠ AND THERE IS NO TUPLE ON THE RESOURCE except the parent edge that makes it a child.
        var tuples = (await cluster.Objects(tenant, resource).ReadAsync()).GetValueOrThrow();

        tuples.Count.ShouldBe(1);
        tuples.Subjects(Relations.Owner)
            .ShouldBeEmpty("the whole argument for From(…) is that inheritance writes nothing per resource");
        tuples.Subjects(Relations.Parent).Count.ShouldBe(1);

        // Same at the intermediate scope.
        var group = ObjectRef.Of(ObjectTypes.ResourceGroup, "rga");
        (await cluster.Objects(tenant, group).ReadAsync()).GetValueOrThrow()
            .Subjects(Relations.Owner)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDirectViewAtAResourceIsEmptyAndTheInheritedViewNamesTheSubscription() {
        var tenant = await SeedHierarchyAsync(401, "b");
        var resource = ObjectRef.Of(ObjectTypes.Resource, "resb");

        var direct = (await cluster.Check(tenant, resource)
            .ListRoleAssignmentsAsync(false)).GetValueOrThrow();

        direct.ShouldBeEmpty("no role tuple is written at the resource");

        var effective = (await cluster.Check(tenant, resource)
            .ListRoleAssignmentsAsync(true)).GetValueOrThrow();

        var owner = effective.ShouldHaveSingleItem();
        owner.RoleName.ShouldBe(Relations.Owner);
        owner.Principal.ShouldBe(Alice);
        owner.Inherited.ShouldBeTrue();
        owner.Scope.ShouldBe(resource);
        owner.InheritedFrom.ShouldBe(ObjectRef.Of(ObjectTypes.Subscription, "subb"));
    }

    [Fact]
    public async Task ADirectAssignmentAtTheScopeIsNotMarkedInherited() {
        var tenant = AuthorizationCluster.Tenant(402);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "rgc");

        await cluster.WriteAsync(tenant, "resourceGroup:rgc#owner@user:alice");

        var view = (await cluster.Check(tenant, scope)
            .ListRoleAssignmentsAsync(true)).GetValueOrThrow();

        var assignment = view.ShouldHaveSingleItem();
        assignment.Inherited.ShouldBeFalse();
        assignment.InheritedFrom.ShouldBe(scope);
    }

    [Fact]
    public async Task ContributorOnAResourceGroupForAGroupIsAUsersetSubject() {
        // docs/plan/07 § Azure RBAC, row 2 — and the sentence right after the table: expressing
        // `resourceGroup:prod#reader@group:eng#member` in Azure RBAC "is not possible, which is the
        // argument for building this rather than a role table".
        var tenant = AuthorizationCluster.Tenant(403);
        var group = ObjectRef.Of(ObjectTypes.ResourceGroup, "rgd");

        await cluster.WriteAsync(tenant, "resourceGroup:rgd#contributor@group:eng#member");
        await cluster.WriteAsync(tenant, "group:eng#member@user:bob");

        var check = await cluster.Check(tenant, group)
            .CheckAsync(Permissions.Write, SubjectRef.Of(ObjectTypes.User, "bob"), Consistency.FullyConsistent);

        check.GetValueOrThrow().Allowed.ShouldBeTrue();

        var view = (await cluster.Check(tenant, group)
            .ListRoleAssignmentsAsync(false)).GetValueOrThrow();

        view.ShouldHaveSingleItem().Principal.ShouldBe(EngMembers);
    }

    [Fact]
    public async Task NestedGroupMembershipIsWalkedBecauseThereIsNoIndexInM1() {
        // docs/plan/07 § The Leopard index is M2. Until then the walk is the answer, and it is
        // correct — but not fast at ten thousand members. This asserts the correctness half.
        var tenant = AuthorizationCluster.Tenant(404);
        var scope = ObjectRef.Of(ObjectTypes.ResourceGroup, "rge");

        await cluster.WriteAsync(tenant, "resourceGroup:rge#reader@group:eng#member");
        await cluster.WriteAsync(tenant, "group:eng#member@group:platform#member");
        await cluster.WriteAsync(tenant, "group:platform#member@user:carol");

        var check = await cluster.Check(tenant, scope)
            .CheckAsync(
                Permissions.Read,
                SubjectRef.Of(ObjectTypes.User, "carol"),
                Consistency.FullyConsistent
            );

        check.GetValueOrThrow().Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ADenyAssignmentRemovesAssignRoleAndLeavesDeleteAlone() {
        // docs/plan/07 § Azure RBAC, row 4: "Deny assignment | `#suspended`, and the
        // `& !Rel("suspended")` in the permission". Only `assignRole` carries it — see
        // CyberCloudSchema — so `delete` is deliberately unaffected.
        var tenant = AuthorizationCluster.Tenant(405);
        var scope = ObjectRef.Of(ObjectTypes.Subscription, "subf");

        await cluster.WriteAsync(tenant, "subscription:subf#owner@user:alice");

        (await Ask(tenant, scope, Permissions.AssignRole)).Allowed.ShouldBeTrue();

        await cluster.WriteAsync(tenant, "subscription:subf#suspended@user:alice");

        // ⚠ ADDING A TUPLE REMOVED ACCESS. That is the non-monotonicity docs/plan/07 § Caching
        // across requests warns about, and the reason for the schema builder's negation rules.
        (await Ask(tenant, scope, Permissions.AssignRole)).Allowed.ShouldBeFalse();
        (await Ask(tenant, scope, Permissions.Delete)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task ARevokeAtTheSubscriptionRemovesAccessOnEveryResourceUnderIt() {
        var tenant = await SeedHierarchyAsync(406, "g");
        var resource = ObjectRef.Of(ObjectTypes.Resource, "resg");

        (await Ask(tenant, resource, Permissions.Delete)).Allowed.ShouldBeTrue();

        await cluster.RevokeAsync(tenant, "subscription:subg#owner@user:alice");

        (await Ask(tenant, resource, Permissions.Delete)).Allowed.ShouldBeFalse(
            "one revoke at the subscription, and every resource under it loses access — the same "
            + "property, read in the other direction"
        );
    }

    [Fact]
    public async Task AWriteAgainstAPermissionRatherThanARelationIsRefused() {
        // A tuple on `delete` would be a grant nothing evaluates and nobody can find.
        var tenant = AuthorizationCluster.Tenant(407);

        var result = await cluster.Store(tenant)
            .WriteAsync(RelationTuple.Parse("resourceGroup:rgh#delete@user:alice").GetValueOrThrow());

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
        result.Error.Message.ShouldContain("is a permission");
    }

    [Fact]
    public async Task AWriteAgainstAnUnknownTypeIsRefused() {
        var tenant = AuthorizationCluster.Tenant(408);

        var result = await cluster.Store(tenant)
            .WriteAsync(RelationTuple.Parse("widget:w1#owner@user:alice").GetValueOrThrow());

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
    }

    /// <summary>
    ///     subscription:sub1 → resourceGroup:rg1 → resource:res1, and the ONLY role tuple is at the
    ///     subscription.
    /// </summary>
    async Task<Guid> SeedHierarchyAsync(int index, string suffix) {
        var tenant = AuthorizationCluster.Tenant(index);

        await cluster.WriteAsync(tenant, $"resourceGroup:rg{suffix}#parent@subscription:sub{suffix}");
        await cluster.WriteAsync(tenant, $"resource:res{suffix}#parent@resourceGroup:rg{suffix}");

        // docs/plan/07 § Azure RBAC, row 1: `Owner` on subscription S for user U.
        await cluster.WriteAsync(tenant, $"subscription:sub{suffix}#owner@user:alice");

        return tenant;
    }

    async Task<CheckResult> Ask(Guid tenant, ObjectRef scope, string permission) {
        var result = await cluster.Check(tenant, scope)
            .CheckAsync(permission, Alice, Consistency.FullyConsistent);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }
}
