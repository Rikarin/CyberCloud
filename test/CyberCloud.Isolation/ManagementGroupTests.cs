using CyberCloud.Authorization.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Isolation;

/// <summary>
///     The management group's one promise —
///     <i>
///         a role assigned at a group is inherited by its
///         subscriptions
///     </i> (docs/plan/06 § The hierarchy, issue #39) — driven through the real
///     <c>ScopeManagerService</c>, the real <c>RoleAssignmentService</c>, the real
///     <c>ReBacScopeRelationWriter</c> and the real <c>CyberCloudSchema</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Written as grants and verbs, never as tuple reads</b>, for the reason
///         <see cref="ScopeCreationTests" /> gives: a tuple read would confirm an edge exists, and
///         what has to be true is that the evaluator <i>follows</i> it — resource group → subscription
///         → management group → the grant, and on to the tenant for the tenant's owner.
///     </para>
///     <para>
///         ⚠ <b>The move is the half that bites.</b> A subscription taken out of a group must stop
///         being reachable through it in the same call, which is what
///         <c>IScopeRelationWriter.RelinkParentAsync</c>'s delete-then-write exists for. A harness with
///         a doubled writer proves the manager asked for the relink
///         (<c>ManagementGroupScopeTests</c>); only this one proves the engine stopped answering.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class ManagementGroupTests(IsolationCluster cluster) {
    /// <summary>A tenant the fixture does not touch, so these tests own its whole tree.</summary>
    static Guid Tree { get; } = Guid.Parse("99999999-0000-4000-8000-000000000009");

    /// <summary>The tenant's owner — granted at the tenant and nowhere else.</summary>
    const string TenantOwner = "theo";

    /// <summary>A user granted nothing anywhere.</summary>
    const string Nobody = "nemo";

    static readonly SemaphoreSlim Seeding = new(1, 1);
    static bool seeded;

    [Fact]
    public async Task AnOwnerAtAGroupReachesAResourceGroupInASubscriptionAssignedToItAndNoOther() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var group = ScopeId.ManagementGroupOf(Tree, "platform-a");
        var inside = Guid.Parse("99999999-0000-4000-8000-0000000000a1");
        var outside = Guid.Parse("99999999-0000-4000-8000-0000000000a2");

        // ── The tenant owner builds the tree: a group, a subscription in it, one outside it ──────
        (await Put(group.Path, """{"displayName":"Platform A"}""", owner)).IsSuccess.ShouldBeTrue();

        var assigned = await Put(
            ScopeId.Subscription(Tree, inside).Path,
            """{"displayName":"Inside","managementGroup":"platform-a"}""",
            owner
        );

        assigned.IsSuccess.ShouldBeTrue(
            "a tenant owner could not assign a subscription to a group they created: " + assigned.Error?.Message
        );

        (await Put(
                ScopeId.Subscription(Tree, outside).Path,
                """{"displayName":"Outside"}""",
                owner
            )).IsSuccess.ShouldBeTrue();

        // ── The grant: owner on the GROUP, through #70's assignment path at the new scope ────────
        var gwen = await UserAsync("gwen");

        var granted = await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(group, new(Relations.Owner, SubjectTypes.User, gwen)).Path,
                Body = "{}",
                Caller = owner
            },
            TestContext.Current.CancellationToken
        );

        granted.IsSuccess.ShouldBeTrue(
            "a tenant owner could not grant Owner on a management group in their own tenant — "
            + "`assignRole` on the group resolves through managementGroup → parent → tenant: "
            + granted.Error?.Message
        );

        // ── #86's collection at the new scope lists the grant ────────────────────────────────────
        var listed = await cluster.Roles.ListAsync(
            new() { Path = RoleAssignmentCollectionId.OnScope(group).Path, Caller = owner },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        listed.GetValueOrThrow()
            .Assignments.ShouldContain(x => x.PrincipalId == gwen && x.RoleDefinitionId == Relations.Owner);

        // ── THE PROMISE. gwen holds one tuple, on the group, and creates a resource group inside ──
        //
        // `write` on the group's parent is checked on subscription:{inside}, whose only path to
        // user:gwen is subscription --parent--> managementGroup:platform-a --owner--> gwen. Two hops
        // the scope path wrote; delete either and this is a 404.
        var madeInside = await Put(
            ScopeId.Group(Tree, inside, "gwen-rg").Path,
            """{"location":"eu-west-1"}""",
            Gwen(gwen)
        );

        madeInside.IsSuccess.ShouldBeTrue(
            "an owner at a management group could not create a resource group in a subscription "
            + "assigned to it — the subscription's parent edge does not reach the group: "
            + madeInside.Error?.Message
        );

        // ── And nothing in the subscription that is NOT in the group — the canonical 404 ─────────
        var refusedOutside = await Put(
            ScopeId.Group(Tree, outside, "gwen-rg").Path,
            """{"location":"eu-west-1"}""",
            Gwen(gwen)
        );

        refusedOutside.IsFailure.ShouldBeTrue("a group grant reached a subscription outside the group");
        refusedOutside.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "404, never 403");

        // ── The tenant owner still reaches the assigned subscription, one hop longer ─────────────
        //
        // ⚠ The subscription's parent is now the GROUP and not the tenant — the chain stayed a chain
        // — so theo's path is subscription → group → tenant → owner. If assignment had ADDED an edge
        // rather than replaced one, this would still pass; ParentsOfAsync below is what pins the
        // replacement.
        var ownerInside = await Put(ScopeId.Group(Tree, inside, "theo-rg").Path, """{"location":"eu-west-1"}""", owner);
        ownerInside.IsSuccess.ShouldBeTrue(
            "the tenant owner lost a subscription by assigning it to a group: " + ownerInside.Error?.Message
        );

        var parents = await ParentsOfAsync(
            ObjectTypes.Subscription,
            inside.ToString("N", System.Globalization.CultureInfo.InvariantCulture)
        );
        parents.ShouldHaveSingleItem("the chain is not a chain: a subscription in a group carries two parent tuples");
        parents[0].Object.Type.ShouldBe(ObjectTypes.ManagementGroup);
        parents[0].Object.Id.ShouldBe("platform-a");

        // ── Nobody is nobody, at every level ─────────────────────────────────────────────────────
        var nobody = await Put(
            ScopeId.Group(Tree, inside, "nemo-rg").Path,
            """{"location":"eu-west-1"}""",
            IsolationCluster.Caller(Tree, Nobody)
        );
        nobody.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    /// <summary>
    ///     ⚠ Moving a subscription out of a group revokes the group's reach in the same call — the
    ///     delete half of the relink — and the tenant owner's reach is unbroken across the move.
    /// </summary>
    [Fact]
    public async Task MovingASubscriptionOutOfAGroupRevokesTheGroupOwnersReachAtOnce() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var from = ScopeId.ManagementGroupOf(Tree, "move-from");
        var to = ScopeId.ManagementGroupOf(Tree, "move-to");
        var subscription = Guid.Parse("99999999-0000-4000-8000-0000000000b1");

        (await Put(from.Path, "{}", owner)).IsSuccess.ShouldBeTrue();
        (await Put(to.Path, "{}", owner)).IsSuccess.ShouldBeTrue();
        (await Put(
                ScopeId.Subscription(Tree, subscription).Path,
                """{"displayName":"Mover","managementGroup":"move-from"}""",
                owner
            ))
            .IsSuccess.ShouldBeTrue();

        var fran = await UserAsync("fran");
        (await cluster.Roles.AssignAsync(
                new() {
                    Path = RoleAssignmentId.OnScope(from, new(Relations.Owner, SubjectTypes.User, fran)).Path,
                    Body = "{}",
                    Caller = owner
                },
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();

        // Before: fran reaches the subscription through move-from.
        (await Put(ScopeId.Group(Tree, subscription, "before").Path, """{"location":"eu-west-1"}""", Gwen(fran)))
            .IsSuccess.ShouldBeTrue("the fixture's grant did not reach");

        // ── The move, by the tenant owner: one PUT naming the other group ────────────────────────
        var moved = await Put(
            ScopeId.Subscription(Tree, subscription).Path,
            """{"displayName":"Mover","managementGroup":"move-to"}""",
            owner
        );
        moved.IsSuccess.ShouldBeTrue(moved.Error?.Message);
        moved.GetValueOrThrow().ManagementGroup.ShouldBe("move-to");

        // After: fran's grant on move-from reaches nothing in the subscription — a fully consistent
        // check, because docs/plan/07 § Consistency makes a revocation the half that is never late,
        // and this assertion is about the tuple store rather than the cache.
        var check = await cluster.For(Tree)
            .GetGrain<ICheckGrain>(
                GrainKeys.CheckCache(
                    ObjectTypes.Subscription,
                    subscription.ToString("N", System.Globalization.CultureInfo.InvariantCulture)
                )
            )
            .CheckAsync(Permissions.Write, SubjectRef.Of(ObjectTypes.User, fran), Consistency.FullyConsistent);

        check.GetValueOrThrow()
            .Allowed.ShouldBeFalse("a grant on the old group still reaches a subscription moved out of it");

        var refused = await Put(
            ScopeId.Group(Tree, subscription, "after").Path,
            """{"location":"eu-west-1"}""",
            Gwen(fran)
        );
        refused.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "404 after the move, never 403: " + refused.Error.Message
        );

        // The tenant owner is unbroken across the move.
        (await Put(ScopeId.Group(Tree, subscription, "still-mine").Path, """{"location":"eu-west-1"}""", owner))
            .IsSuccess.ShouldBeTrue();

        // And the chain is still a chain.
        var parents = await ParentsOfAsync(
            ObjectTypes.Subscription,
            subscription.ToString("N", System.Globalization.CultureInfo.InvariantCulture)
        );
        parents.ShouldHaveSingleItem();
        parents[0].Object.Id.ShouldBe("move-to");
    }

    /// <summary>
    ///     ⚠ A grant at a nested group reaches through the levels above it to the tenant's owner and
    ///     down to a subscription two groups deep — the tree, not just one edge.
    /// </summary>
    [Fact]
    public async Task ANestedGroupIsReachedFromAboveAndReachesBelow() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var subscription = Guid.Parse("99999999-0000-4000-8000-0000000000c1");

        (await Put(ScopeId.ManagementGroupOf(Tree, "nest-1").Path, "{}", owner)).IsSuccess.ShouldBeTrue();
        (await Put(
                ScopeId.ManagementGroupOf(Tree, "nest-2").Path,
                """{"managementGroup":"nest-1"}""",
                owner
            )).IsSuccess.ShouldBeTrue();

        // ⚠ The nested group's parent is nest-1, so `write` for its create was checked on nest-1,
        // which theo reaches only through nest-1 --parent--> tenant.
        (await Put(
                ScopeId.Subscription(Tree, subscription).Path,
                """{"displayName":"Deep","managementGroup":"nest-2"}""",
                owner
            ))
            .IsSuccess.ShouldBeTrue();

        var hal = await UserAsync("hal");
        (await cluster.Roles.AssignAsync(
                new() {
                    Path = RoleAssignmentId.OnScope(
                        ScopeId.ManagementGroupOf(Tree, "nest-1"),
                        new(Relations.Contributor, SubjectTypes.User, hal)
                    ).Path,
                    Body = "{}",
                    Caller = owner
                },
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();

        // hal is a contributor at the TOP group and creates a resource group two levels down:
        // resourceGroup → subscription → nest-2 → nest-1 → hal.
        var made = await Put(
            ScopeId.Group(Tree, subscription, "hal-rg").Path,
            """{"location":"eu-west-1"}""",
            Gwen(hal)
        );
        made.IsSuccess.ShouldBeTrue(
            "a contributor at a top-level group could not reach a subscription two groups down: " + made.Error?.Message
        );

        // And a contributor cannot grant — `assignRole` is owner-only — which is the same rule the
        // other scopes have and proves the group type carries the whole permission set, not a copy.
        var refused = await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(
                    ScopeId.ManagementGroupOf(Tree, "nest-2"),
                    new(Relations.Reader, SubjectTypes.User, hal)
                ).Path,
                Body = "{}",
                Caller = Gwen(hal)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a contributor granted a role on a management group");
    }

    /// <summary>
    ///     ⚠ <b>A deleted group's grants do not survive under its name.</b> The group's ReBAC object is
    ///     <c>managementGroup:{name}</c> — the bare name — so a group re-created under a deleted
    ///     group's name has the deleted group's object, and every tuple left on it would be a grant on
    ///     the new group that nobody made, reaching every subscription placed under it. The review of
    ///     issue #39 found the delete swept the edge and the listings and not the grants;
    ///     <c>IScopeRelationWriter.ClearAsync</c> is the sweep this drives through the real schema.
    /// </summary>
    [Fact]
    public async Task ADeletedGroupRecreatedUnderTheSameNameCarriesNoneOfItsOldGrants() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var group = ScopeId.ManagementGroupOf(Tree, "phoenix");
        var subscription = Guid.Parse("99999999-0000-4000-8000-0000000000d1");

        (await Put(group.Path, "{}", owner)).IsSuccess.ShouldBeTrue();

        var ivy = await UserAsync("ivy");
        (await cluster.Roles.AssignAsync(
                new() {
                    Path = RoleAssignmentId.OnScope(group, new(Relations.Owner, SubjectTypes.User, ivy)).Path,
                    Body = "{}",
                    Caller = owner
                },
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();

        (await CheckAsync(ObjectTypes.ManagementGroup, "phoenix", ivy)).ShouldBeTrue(
            "the fixture's grant did not reach"
        );

        // ── The delete, by the tenant owner, of an empty group ───────────────────────────────────
        var deleted = await cluster.Scopes.DeleteAsync(
            new() { Path = group.Path, Caller = owner },
            TestContext.Current.CancellationToken
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        // Nothing is left on the object — not the parent edge, not ivy's owner tuple.
        (await RelationsOnAsync(ObjectTypes.ManagementGroup, "phoenix")).ShouldBeEmpty(
            "a deleted group's object still carries tuples, and the next group of that name inherits them"
        );

        // ── Re-created under the same name by the tenant owner, with a subscription placed in it ─
        (await Put(group.Path, """{"displayName":"Phoenix, again"}""", owner)).IsSuccess.ShouldBeTrue();
        (await Put(
                ScopeId.Subscription(Tree, subscription).Path,
                """{"displayName":"Reborn","managementGroup":"phoenix"}""",
                owner
            ))
            .IsSuccess.ShouldBeTrue();

        // ivy holds nothing on the new group and reaches nothing under it — a fully consistent check
        // on the tuple store, and the canonical 404 on the write path.
        (await CheckAsync(ObjectTypes.ManagementGroup, "phoenix", ivy))
            .ShouldBeFalse("a grant on a deleted group revived on the group that took its name");

        var refused = await Put(
            ScopeId.Group(Tree, subscription, "ivy-rg").Path,
            """{"location":"eu-west-1"}""",
            Gwen(ivy)
        );
        refused.IsFailure.ShouldBeTrue(
            "a deleted group's owner created a resource group under the group that took its name"
        );
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "404, never 403: " + refused.Error.Message);

        // The new group is a whole group: its edge is to the tenant and the tenant owner reaches it.
        var parents = await ParentsOfAsync(ObjectTypes.ManagementGroup, "phoenix");
        parents.ShouldHaveSingleItem();
        parents[0].Object.Type.ShouldBe(ObjectTypes.Tenant);
        (await RelationsOnAsync(ObjectTypes.ManagementGroup, "phoenix")).ShouldBe([Relations.Parent]);
        (await Put(
                ScopeId.Group(Tree, subscription, "theo-rg").Path,
                """{"location":"eu-west-1"}""",
                owner
            )).IsSuccess.ShouldBeTrue();
    }

    /// <summary>
    ///     ⚠ <b>A deleted group's policy does not survive under its name either.</b> The tenant's policy
    ///     catalog keys an assignment by its scope's path, and a management group's path is its name —
    ///     so before the review of issue #46 the old owner's deny was in force on the group re-created
    ///     under that name, readable there, and undeletable while the scope was gone (the policy
    ///     manager answers 404 for a scope that doesn't exist). The reviewer's probe, made permanent.
    /// </summary>
    [Fact]
    public async Task ADeletedGroupRecreatedUnderTheSameNameCarriesNoneOfItsOldPolicy() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var group = ScopeId.ManagementGroupOf(Tree, "residue");
        var definition = PolicyAddress.Definition(group, "residue-deny");
        var assignment = PolicyAddress.Assignment(group, "residue-asg");

        (await Put(group.Path, "{}", owner)).IsSuccess.ShouldBeTrue();
        (await PutPolicyAsync(definition, DenyAll, owner)).IsSuccess.ShouldBeTrue();
        (await PutPolicyAsync(assignment, AssignmentBody(definition), owner)).IsSuccess.ShouldBeTrue();

        var deleted = await cluster.Scopes.DeleteAsync(new() { Path = group.Path, Caller = owner }, TestContext.Current.CancellationToken);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        (await CatalogAssignmentsAsync(group)).ShouldBeEmpty("a deleted group's assignment is still in the tenant's catalog");

        // ── Re-created under the same name: nothing of the old owner's is there ───────────────────
        (await Put(group.Path, """{"displayName":"Residue, again"}""", owner)).IsSuccess.ShouldBeTrue();

        var read = await cluster.Policies.ReadAsync(
            new() { Path = assignment.Path, Caller = owner },
            TestContext.Current.CancellationToken
        );
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "the old group's assignment governs the group that took its name");

        (await cluster.Policies.ReadAsync(new() { Path = definition.Path, Caller = owner }, TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "the old group's definition is still there");

        // The name is free for the new owner's own policy, created rather than replaced.
        var fresh = await PutPolicyAsync(definition, DenyAll, owner);
        fresh.IsSuccess.ShouldBeTrue(fresh.Error?.Message);
        (await cluster.Policies.DeleteAsync(new() { Path = definition.Path, Body = "{}", Caller = owner }, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue("a definition nothing assigns is deletable — no orphaned assignment holds it");
    }

    /// <summary>
    ///     The same residue one level down: a resource group's path is its subscription and its name,
    ///     so an assignment left by a deleted group governed the group re-created under that name.
    /// </summary>
    [Fact]
    public async Task ADeletedResourceGroupRecreatedUnderTheSameNameCarriesNoneOfItsOldPolicy() {
        await SeedTenantAsync();
        var owner = IsolationCluster.Caller(Tree, TenantOwner);
        var subscription = Guid.Parse("99999999-0000-4000-8000-0000000000d6");
        var subscriptionScope = ScopeId.Subscription(Tree, subscription);
        var group = ScopeId.Group(Tree, subscription, "residue-rg");
        var definition = PolicyAddress.Definition(subscriptionScope, "rg-residue-deny");
        var assignment = PolicyAddress.Assignment(group, "rg-residue-asg");

        (await Put(subscriptionScope.Path, """{"displayName":"Residue"}""", owner)).IsSuccess.ShouldBeTrue();
        (await Put(group.Path, """{"location":"eu-west-1"}""", owner)).IsSuccess.ShouldBeTrue();
        (await PutPolicyAsync(definition, DenyAll, owner)).IsSuccess.ShouldBeTrue();
        (await PutPolicyAsync(assignment, AssignmentBody(definition), owner)).IsSuccess.ShouldBeTrue();

        var deleted = await cluster.Scopes.DeleteAsync(new() { Path = group.Path, Caller = owner }, TestContext.Current.CancellationToken);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        (await Put(group.Path, """{"location":"eu-west-1"}""", owner)).IsSuccess.ShouldBeTrue();

        (await cluster.Policies.ReadAsync(new() { Path = assignment.Path, Caller = owner }, TestContext.Current.CancellationToken))
            .Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "the old group's assignment governs the group that took its name");

        // ⚠ The subscription's definition was the subscription's, and it stays — only what the deleted
        // scope held goes. With its one assignment gone it's deletable.
        (await cluster.Policies.DeleteAsync(new() { Path = definition.Path, Body = "{}", Caller = owner }, TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue("the orphaned assignment still holds the subscription's definition");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    const string DenyAll = """{ "properties": { "policyRule": { "if": { "field": "type", "like": "*" }, "then": { "effect": "deny" } } } }""";

    static string AssignmentBody(PolicyAddress definition) =>
        $$"""{ "properties": { "policyDefinitionId": "{{definition.Path}}" } }""";

    Task<Result<PolicyObjectSnapshot>> PutPolicyAsync(PolicyAddress address, string body, CallerContext caller) =>
        cluster.Policies.PutAsync(new() { Path = address.Path, Body = body, Caller = caller }, TestContext.Current.CancellationToken);

    /// <summary>What the catalog holds at a scope, asked of the grain directly, since the manager answers 404 for a scope that is gone.</summary>
    async Task<IReadOnlyList<PolicyAssignmentRecord>> CatalogAssignmentsAsync(ScopeId scope) =>
        (await cluster.For(Tree)
            .GetGrain<IPolicyCatalogGrain>(GrainKeys.PolicyCatalog(Tree))
            .ListAssignmentsAsync(scope.Path)).GetValueOrThrow();

    static CallerContext Gwen(string user) => IsolationCluster.Caller(Tree, user);

    async Task<bool> CheckAsync(string type, string id, string user) {
        var check = await cluster.For(Tree)
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, id))
            .CheckAsync(Permissions.Write, SubjectRef.Of(ObjectTypes.User, user), Consistency.FullyConsistent);

        return check.GetValueOrThrow().Allowed;
    }

    /// <summary>The relations that have at least one tuple on the object, ordered.</summary>
    async Task<IReadOnlyList<string>> RelationsOnAsync(string type, string id) {
        var snapshot = await cluster.For(Tree)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(type, id))
            .ReadDurableAsync();

        return snapshot.IsSuccess
            ? snapshot.GetValueOrThrow()
                .ByRelation
                    .Where(static x => x.Value.Count > 0)
                    .Select(static x => x.Key)
                    .Order(StringComparer.Ordinal)
                    .ToList()
            : [];
    }

    Task<Result<ScopeSnapshot>> Put(string path, string body, CallerContext caller) =>
        cluster.Scopes.CreateAsync(
            new() { Path = path, Body = body, Caller = caller },
            TestContext.Current.CancellationToken
        );

    async Task SeedTenantAsync() {
        await Seeding.WaitAsync(TestContext.Current.CancellationToken);

        try {
            if (seeded) {
                return;
            }

            var created = await cluster.For(Tree)
                .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tree))
                .CreateAsync("tree-tenant", "Tree Tenant", "eu-west-1");

            created.IsSuccess.ShouldBeTrue(created.Error?.Message);
            await cluster.GrantTenantOwnerAsync(Tree, TenantOwner);
            seeded = true;
        } finally {
            Seeding.Release();
        }
    }

    Task<string> UserAsync(string label) => cluster.CreateUserAsync(Tree, GuidFor("user:" + label));

    static Guid GuidFor(string label) {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("tree:" + label));
        var bytes = digest[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new(bytes);
    }

    async Task<IReadOnlyList<SubjectRef>> ParentsOfAsync(string type, string id) {
        var snapshot = await cluster.For(Tree)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(type, id))
            .ReadDurableAsync();

        return snapshot.IsSuccess
            && snapshot.GetValueOrThrow().ByRelation.TryGetValue(Relations.Parent, out var parents)
                ? parents
                : [];
    }
}
