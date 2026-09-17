using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The management group through <c>IScopeManager</c> — create, read, list, delete, and the
///     subscription assignment that is the reason the scope exists — over the real tenancy grains,
///     with the authorization seam and the relation writer doubled. docs/plan/06 § The hierarchy,
///     issue #39.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What the doubles let this class pin is the ORDER and the SHAPE of the parent edge</b>,
///         which is the security property: <see cref="NoOpScopeRelationWriter.Edges" /> records every
///         edge the manager asked for, so an assertion that a subscription's edge was <i>relinked</i>
///         from the tenant to the group — delete-then-write, never a second parent — reads what the
///         manager did rather than what the engine made of it. Whether a grant at a group then
///         reaches a resource is <c>test/CyberCloud.Isolation</c>'s <c>ManagementGroupTests</c>,
///         against <c>CyberCloudSchema</c>.
///     </para>
///     <para>
///         ⚠ <b>Its own tenants</b>, for the reason <see cref="ScopeManagerServiceTests" /> uses its
///         own: the fixture's subscriptions were not created through the scope path, and a listing
///         seeded by hand tests a state nothing produces.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ManagementGroupScopeTests {
    readonly ResourceManagerCluster cluster;
    readonly ScopeManagerService scopes;

    public ManagementGroupScopeTests(ResourceManagerCluster cluster) {
        this.cluster = cluster;

        scopes = new ScopeManagerService(
            new SwitchableScopeAuthorizer(),
            new NoOpScopeRelationWriter(),
            cluster.Grains,
            new ResourceGroupReclaimer(
                cluster.Grains,
                new NoClusterConnectionFactory(),
                new ConnectionNamespaceInventory(new NoClusterConnectionFactory()),
                new NamespaceEnsurer(TestClock.Instance),
                NullLogger<ResourceGroupReclaimer>.Instance
            ),
            NullLogger<ScopeManagerService>.Instance
        );
    }

    // ── Create and read ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARootGroupIsCreatedUnderTheTenantAndReadsBackWithNoParent() {
        Reset();
        var tenant = await NewTenantAsync();
        var group = ScopeId.ManagementGroupOf(tenant, "platform");

        var created = await CreateGroupAsync(group, """{"displayName":"Platform"}""");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        var snapshot = created.GetValueOrThrow();
        snapshot.Created.ShouldBeTrue();
        snapshot.Kind.ShouldBe(ScopeKind.ManagementGroup);
        snapshot.Type.ShouldBe(ScopeTypeNames.ManagementGroup);
        snapshot.Name.ShouldBe("Platform");
        snapshot.ManagementGroup.ShouldBeEmpty("a root group hangs off the tenant, spelled by absence");
        snapshot.Path.ShouldBe(group.Path);

        // ⚠ THE EDGE: to the tenant, written once, before the record — the type's remarks.
        NoOpScopeRelationWriter.Edges.ShouldContain((group, (ScopeId?)null, (ScopeId?)ScopeId.Tenant(tenant)));

        // ⚠ THE CHECK IS `write` ON THE PARENT, which for a root group is the tenant.
        SwitchableScopeAuthorizer.Asked.ShouldContain(ScopeId.Tenant(tenant));

        var read = await scopes.ReadAsync(
            new() { Path = group.Path, Caller = ResourceManagerCluster.Caller(tenant) },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().ShouldBe(snapshot with { Created = false }, "a read renders differently from the create");

        // Idempotent: 200 the second time, same record.
        var again = await CreateGroupAsync(group, """{"displayName":"Platform"}""");
        again.GetValueOrThrow().Created.ShouldBeFalse();
        again.GetValueOrThrow().Version.ShouldBe(snapshot.Version);
    }

    [Fact]
    public async Task ANestedGroupHangsOffItsParentAndTheParentListsIt() {
        Reset();
        var tenant = await NewTenantAsync();
        var root = ScopeId.ManagementGroupOf(tenant, "root");
        var child = ScopeId.ManagementGroupOf(tenant, "child");

        (await CreateGroupAsync(root, "{}")).IsSuccess.ShouldBeTrue();
        NoOpScopeRelationWriter.Edges.Clear();
        SwitchableScopeAuthorizer.Asked.Clear();

        var created = await CreateGroupAsync(child, """{"managementGroup":"root"}""");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.GetValueOrThrow().ManagementGroup.ShouldBe("root");

        // The edge is to the PARENT GROUP, not the tenant, and the check was on the parent group.
        NoOpScopeRelationWriter.Edges.ShouldContain((child, (ScopeId?)null, (ScopeId?)root));
        NoOpScopeRelationWriter.Edges.ShouldNotContain((child, (ScopeId?)null, (ScopeId?)ScopeId.Tenant(tenant)));
        SwitchableScopeAuthorizer.Asked.ShouldContain(root);

        var record = await cluster.For(tenant)
            .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("root"))
            .GetAsync();

        record.GetValueOrThrow().Children.ShouldBe(["child"]);

        var childRecord = await cluster.For(tenant)
            .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("child"))
            .GetAsync();

        childRecord.GetValueOrThrow().Depth.ShouldBe(2);
    }

    /// <summary>
    ///     ⚠ Refused by the manager before any edge is written — a second parent tuple is exactly the
    ///     two-parent chain the schema comment forbids, and the grain's own refusal would come after
    ///     it.
    /// </summary>
    [Fact]
    public async Task AGroupCannotBeMovedByRePuttingItUnderAnotherParentAndNoEdgeIsWritten() {
        Reset();
        var tenant = await NewTenantAsync();
        (await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "a"), "{}")).IsSuccess.ShouldBeTrue();
        (await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "b"), "{}")).IsSuccess.ShouldBeTrue();

        var leaf = ScopeId.ManagementGroupOf(tenant, "leaf");
        (await CreateGroupAsync(leaf, """{"managementGroup":"a"}""")).IsSuccess.ShouldBeTrue();
        NoOpScopeRelationWriter.Edges.Clear();

        var moved = await CreateGroupAsync(leaf, """{"managementGroup":"b"}""");

        moved.IsFailure.ShouldBeTrue("a group was moved by re-PUT");
        moved.Error!.Code.ShouldBe(ErrorCode.Conflict);
        moved.Error.Message.ShouldContain("move");
        NoOpScopeRelationWriter.Edges.ShouldBeEmpty("an edge was written for a refused move");
    }

    [Fact]
    public async Task AParentThatDoesNotExistIsABodyProblemNotAnAddressProblem() {
        Reset();
        var tenant = await NewTenantAsync();

        var refused = await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "orphan"), """{"managementGroup":"nowhere"}""");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("nowhere");
        NoOpScopeRelationWriter.Edges.ShouldBeEmpty();
    }

    [Fact]
    public async Task AGroupCannotBeItsOwnParent() {
        Reset();
        var tenant = await NewTenantAsync();

        var refused = await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "loop"), """{"managementGroup":"loop"}""");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    // ── The subscription assignment ────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The property the scope exists for, at the manager's level</b>: a new subscription
    ///     naming a group hangs off the group and not the tenant — one edge, to the group — and the
    ///     caller is checked on the group as well as on the tenant.
    /// </summary>
    [Fact]
    public async Task ANewSubscriptionNamingAGroupHangsOffTheGroup() {
        Reset();
        var tenant = await NewTenantAsync();
        var group = ScopeId.ManagementGroupOf(tenant, "platform");
        (await CreateGroupAsync(group, "{}")).IsSuccess.ShouldBeTrue();
        NoOpScopeRelationWriter.Edges.Clear();
        SwitchableScopeAuthorizer.Asked.Clear();

        var subscription = ScopeId.Subscription(tenant, Guid.NewGuid());

        var created = await scopes.CreateAsync(
            new() {
                Path = subscription.Path,
                Body = """{"displayName":"Prod","managementGroup":"platform"}""",
                Caller = ResourceManagerCluster.Caller(tenant)
            },
            TestContext.Current.CancellationToken
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.GetValueOrThrow().ManagementGroup.ShouldBe("platform");

        var edges = NoOpScopeRelationWriter.Edges.Where(x => x.Scope == subscription).ToList();
        edges.ShouldBe([(subscription, (ScopeId?)null, (ScopeId?)group)], "the subscription's one edge is not to the group");

        SwitchableScopeAuthorizer.Asked.ShouldContain(group, "the group was not asked whether the caller may place a subscription under it");
        SwitchableScopeAuthorizer.Asked.ShouldContain(ScopeId.Tenant(tenant));

        var record = await cluster.For(tenant)
            .GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup("platform"))
            .GetAsync();

        record.GetValueOrThrow().Subscriptions.ShouldBe([subscription.SubscriptionId]);

        var read = await scopes.ReadAsync(
            new() { Path = subscription.Path, Caller = ResourceManagerCluster.Caller(tenant) },
            TestContext.Current.CancellationToken
        );

        read.GetValueOrThrow().ManagementGroup.ShouldBe("platform");
    }

    /// <summary>
    ///     ⚠ A move is a RELINK — the old edge deleted, the new one written, in one call that never
    ///     leaves two parents — and both groups' membership lists follow.
    /// </summary>
    [Fact]
    public async Task MovingASubscriptionRelinksTheEdgeAndBothGroupsFollow() {
        Reset();
        var tenant = await NewTenantAsync();
        var a = ScopeId.ManagementGroupOf(tenant, "a");
        var b = ScopeId.ManagementGroupOf(tenant, "b");
        (await CreateGroupAsync(a, "{}")).IsSuccess.ShouldBeTrue();
        (await CreateGroupAsync(b, "{}")).IsSuccess.ShouldBeTrue();

        var subscription = ScopeId.Subscription(tenant, Guid.NewGuid());
        (await PutSubscriptionAsync(subscription, """{"displayName":"Prod","managementGroup":"a"}""")).IsSuccess.ShouldBeTrue();
        NoOpScopeRelationWriter.Edges.Clear();

        var moved = await PutSubscriptionAsync(subscription, """{"displayName":"Prod","managementGroup":"b"}""");

        moved.IsSuccess.ShouldBeTrue(moved.Error?.Message);
        moved.GetValueOrThrow().Created.ShouldBeFalse();
        moved.GetValueOrThrow().ManagementGroup.ShouldBe("b");

        NoOpScopeRelationWriter.Edges.Where(x => x.Scope == subscription)
            .ShouldBe([(subscription, (ScopeId?)a, (ScopeId?)b)], "the move was not one relink from a to b");

        (await Group(tenant, "a")).Subscriptions.ShouldBeEmpty("the old group still lists the moved subscription");
        (await Group(tenant, "b")).Subscriptions.ShouldBe([subscription.SubscriptionId]);

        // Back to the root with the empty string; absent leaves it alone.
        NoOpScopeRelationWriter.Edges.Clear();
        (await PutSubscriptionAsync(subscription, """{"displayName":"Prod"}""")).GetValueOrThrow().ManagementGroup.ShouldBe("b");
        NoOpScopeRelationWriter.Edges.Where(x => x.Scope == subscription)
            .ShouldBe([(subscription, (ScopeId?)null, (ScopeId?)b)], "a body that did not mention the group moved the subscription");

        NoOpScopeRelationWriter.Edges.Clear();
        var rooted = await PutSubscriptionAsync(subscription, """{"displayName":"Prod","managementGroup":""}""");
        rooted.GetValueOrThrow().ManagementGroup.ShouldBeEmpty();
        NoOpScopeRelationWriter.Edges.Where(x => x.Scope == subscription)
            .ShouldBe([(subscription, (ScopeId?)b, (ScopeId?)ScopeId.Tenant(tenant))]);
        (await Group(tenant, "b")).Subscriptions.ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ A group the caller may not see and a group that is not there get the same sentence, and
    ///     it is a <c>400</c>: the group is named in the body, not the URL.
    /// </summary>
    [Fact]
    public async Task AGroupTheCallerCannotSeeIsIndistinguishableFromOneThatIsNotThere() {
        Reset();
        var tenant = await NewTenantAsync();
        var hidden = ScopeId.ManagementGroupOf(tenant, "hidden");
        (await CreateGroupAsync(hidden, "{}")).IsSuccess.ShouldBeTrue();
        SwitchableScopeAuthorizer.Hidden[hidden] = true;

        var unseen = await PutSubscriptionAsync(ScopeId.Subscription(tenant, Guid.NewGuid()), """{"displayName":"x","managementGroup":"hidden"}""");
        var absent = await PutSubscriptionAsync(ScopeId.Subscription(tenant, Guid.NewGuid()), """{"displayName":"x","managementGroup":"absent"}""");

        unseen.IsFailure.ShouldBeTrue();
        absent.IsFailure.ShouldBeTrue();
        unseen.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        absent.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        unseen.Error.Message.Replace("hidden", "X", StringComparison.Ordinal)
            .ShouldBe(absent.Error.Message.Replace("absent", "X", StringComparison.Ordinal), "the two refusals differ — an oracle");
    }

    // ── List ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheTenantListsItsGroupsFlatFilteredAndOrderedByName() {
        Reset();
        var tenant = await NewTenantAsync();
        (await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "zeta"), "{}")).IsSuccess.ShouldBeTrue();
        (await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "alpha"), "{}")).IsSuccess.ShouldBeTrue();
        (await CreateGroupAsync(ScopeId.ManagementGroupOf(tenant, "alpha-child"), """{"managementGroup":"alpha"}""")).IsSuccess.ShouldBeTrue();

        SwitchableScopeAuthorizer.Hidden[ScopeId.ManagementGroupOf(tenant, "zeta")] = true;

        var page = await ListAsync(tenant, ScopeKind.ManagementGroup);

        page.Items.Select(x => x.Path).ShouldBe([
            ScopeId.ManagementGroupOf(tenant, "alpha").Path,
            ScopeId.ManagementGroupOf(tenant, "alpha-child").Path
        ]);

        // Flat: the nested group is in the page, carrying its parent.
        page.Items[1].ManagementGroup.ShouldBe("alpha");

        // ⚠ And the same tenant's SUBSCRIPTION collection is a different page.
        var subscriptions = await ListAsync(tenant, ScopeKind.Subscription);
        subscriptions.Items.ShouldBeEmpty("the subscription collection listed management groups");

        // The default member kind is the one the parent had before #39.
        var unstated = await ListAsync(tenant, ScopeKind.Unknown);
        unstated.Items.ShouldBeEmpty();
    }

    // ── Delete ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRefusesAGroupThatHoldsAnythingAndOtherwiseSweepsTheTreeAndTheEdge() {
        Reset();
        var tenant = await NewTenantAsync();
        var root = ScopeId.ManagementGroupOf(tenant, "root");
        var child = ScopeId.ManagementGroupOf(tenant, "child");
        (await CreateGroupAsync(root, "{}")).IsSuccess.ShouldBeTrue();
        (await CreateGroupAsync(child, """{"managementGroup":"root"}""")).IsSuccess.ShouldBeTrue();

        var held = await DeleteGroupAsync(root);
        held.IsFailure.ShouldBeTrue("a group holding a child was deleted");
        held.Error!.Code.ShouldBe(ErrorCode.Conflict);
        held.Error.Message.ShouldContain("child");

        NoOpScopeRelationWriter.Edges.Clear();

        (await DeleteGroupAsync(child)).IsSuccess.ShouldBeTrue();

        // The edge to the parent group is gone, the parent no longer lists it, the tenant neither.
        NoOpScopeRelationWriter.Edges.ShouldContain((child, (ScopeId?)root, (ScopeId?)null));
        (await Group(tenant, "root")).Children.ShouldBeEmpty();
        (await ListAsync(tenant, ScopeKind.ManagementGroup)).Items.Select(x => x.Path).ShouldBe([root.Path]);

        // Idempotent, and the sweep still knows the parent.
        NoOpScopeRelationWriter.Edges.Clear();
        (await DeleteGroupAsync(child)).IsSuccess.ShouldBeTrue();
        NoOpScopeRelationWriter.Edges.ShouldContain((child, (ScopeId?)root, (ScopeId?)null));

        (await DeleteGroupAsync(root)).IsSuccess.ShouldBeTrue();
        (await ListAsync(tenant, ScopeKind.ManagementGroup)).Items.ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static void Reset() {
        ResourceManagerCluster.ResetDoubles();
        NoOpScopeRelationWriter.Edges.Clear();
    }

    async Task<Guid> NewTenantAsync() {
        var tenant = Guid.NewGuid();

        var created = await cluster.For(tenant)
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(tenant))
            .CreateAsync("mg-" + tenant.ToString("N", CultureInfo.InvariantCulture)[..8], "Groups", "eu-west-1");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        return tenant;
    }

    Task<Result<ScopeSnapshot>> CreateGroupAsync(ScopeId group, string body) =>
        scopes.CreateAsync(
            new() { Path = group.Path, Body = body, Caller = ResourceManagerCluster.Caller(group.TenantId) },
            TestContext.Current.CancellationToken
        );

    Task<Result> DeleteGroupAsync(ScopeId group) =>
        scopes.DeleteAsync(
            new() { Path = group.Path, Caller = ResourceManagerCluster.Caller(group.TenantId) },
            TestContext.Current.CancellationToken
        );

    Task<Result<ScopeSnapshot>> PutSubscriptionAsync(ScopeId subscription, string body) =>
        scopes.CreateAsync(
            new() { Path = subscription.Path, Body = body, Caller = ResourceManagerCluster.Caller(subscription.TenantId) },
            TestContext.Current.CancellationToken
        );

    async Task<ManagementGroupDescriptor> Group(Guid tenant, string name) =>
        (await cluster.For(tenant).GetGrain<IManagementGroupGrain>(GrainKeys.ManagementGroup(name)).GetAsync())
        .GetValueOrThrow();

    async Task<ScopeListPage> ListAsync(Guid tenant, ScopeKind members) {
        var listed = await scopes.ListAsync(
            new() {
                ParentPath = ScopeId.Tenant(tenant).Path,
                MemberKind = members,
                Caller = ResourceManagerCluster.Caller(tenant)
            },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return listed.GetValueOrThrow();
    }
}
