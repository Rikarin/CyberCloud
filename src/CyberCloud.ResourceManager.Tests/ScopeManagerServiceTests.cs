using CyberCloud.ResourceManager.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The scope collections — <c>IScopeManager.ListAsync</c> — driven through the real create path
///     rather than against the parent grains on their own.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Every case here creates its scopes through <see cref="IScopeManager.CreateAsync" />,
///             for the reason <see cref="CollectionListingTests" /> gives.
///         </b> The enumeration source is
///         the parent grain's own listing — <c>ITenantGrain.ListSubscriptionsAsync</c>, which
///         <c>ScopeManagerService.CreateSubscriptionAsync</c> appends to, and
///         <c>ISubscriptionGrain.ListResourceGroupsAsync</c>, which the subscription grain's own
///         create appends to. A suite that seeded those lists by calling the grains would test the
///         listing against a state nothing produces; the fixture's own subscriptions are created
///         that way, which is why this class uses tenants of its own.
///     </para>
///     <para>
///         ⚠ <b>What is <i>not</i> proven here is the filter's verdict, only its shape.</b>
///         <see cref="SwitchableScopeAuthorizer" /> stands in for the enforcement seam, so these
///         cases pin that the filter runs once per page and once per member on the fallback, that a
///         hidden member leaves no trace, that paging advances past one, and that an element is the
///         by-id read's own bytes. Whether the real engine hides the right subscriptions is
///         <c>ReBacScopeAuthorizerTests</c>' and, against <c>CyberCloudSchema</c>,
///         <c>test/CyberCloud.Isolation</c>'s.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ScopeManagerServiceTests {
    readonly ResourceManagerCluster cluster;
    readonly ScopeManagerService scopes;

    public ScopeManagerServiceTests(ResourceManagerCluster cluster) {
        this.cluster = cluster;

        scopes = new(
            new SwitchableScopeAuthorizer(),
            new NoOpScopeRelationWriter(),
            cluster.Grains,
            // Wired with no cluster connections: nothing here deletes a group, and a reclaim that
            // reached a namespace would be reading a k3s that is not here.
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

    // ── What a listing returns ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A subscription the caller cannot read is not in the page, and leaves no trace.</b>
    /// </summary>
    [Fact]
    public async Task ListSubscriptionsReturnsOnlyWhatTheCallerReads() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 3);

        SwitchableScopeAuthorizer.Hidden[created[1]] = true;

        var page = await ListAsync(ScopeId.Tenant(tenant));

        page.Items.Select(static x => x.Path).ShouldBe([created[0].Path, created[2].Path]);
        page.Continuation.ShouldBe("", "three members is not a full page, so there is nothing to resume");
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The subscription collection asks nothing about the tenant: a caller holding a role
    ///         on one subscription and nothing on the tenant sees that subscription.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     The parent is the tenant the token names, whose existence is not news to the caller, and
    ///     a delegated grant — <c>reader</c> on one subscription — is the ordinary shape a check on
    ///     the tenant would hide. Azure's <c>GET /subscriptions</c> answers the same way.
    /// </remarks>
    [Fact]
    public async Task ListSubscriptionsNeedsNoTenantPermission() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 2);

        // Hidden AFTER the creates, which do check `write` on the tenant — the create's own rule.
        SwitchableScopeAuthorizer.Hidden[ScopeId.Tenant(tenant)] = true;
        SwitchableScopeAuthorizer.Asked.Clear();

        var page = await ListAsync(ScopeId.Tenant(tenant));

        page.Items.Select(static x => x.Path).ShouldBe(created.Select(static x => x.Path));

        SwitchableScopeAuthorizer.Asked.ShouldNotContain(
            ScopeId.Tenant(tenant),
            "the subscription collection checked a permission on the tenant, which would hide every "
            + "subscription from a caller with a delegated grant on one"
        );
    }

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         The resource-group collection is the canonical <c>404</c> for a subscription the
    ///         caller may not read — the same sentence as for one that does not exist.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     An empty page under an unreadable subscription would confirm the subscription exists,
    ///     and a subscription id leaks more than a resource name because it is the billing
    ///     boundary. The two sentences are compared rather than each pinned: their identity is the
    ///     property.
    /// </remarks>
    [Fact]
    public async Task ListResourceGroupsIs404ForAnUnreadableSubscription() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var subscription = (await CreateSubscriptionsAsync(tenant, 1))[0];
        (await CreateGroupAsync(subscription, "prod")).IsSuccess.ShouldBeTrue();

        SwitchableScopeAuthorizer.Hidden[subscription] = true;

        var refused = await scopes.ListAsync(
            new() { ParentPath = subscription.Path, Caller = ResourceManagerCluster.Caller(tenant) },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("an unreadable subscription's groups were listed");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var missing = ScopeId.Subscription(tenant, Guid.NewGuid());

        var absent = await scopes.ListAsync(
            new() { ParentPath = missing.Path, Caller = ResourceManagerCluster.Caller(tenant) },
            TestContext.Current.CancellationToken
        );

        absent.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // The same sentence, differing only in the id — "does not exist" for both.
        refused.Error.Message.ShouldBe($"'{subscription.Path}' does not exist.");
        absent.Error.Message.ShouldBe($"'{missing.Path}' does not exist.");
    }

    /// <summary>
    ///     ⚠ <b>A parent in another tenant is the canonical <c>404</c>, before any grain is asked.</b>
    /// </summary>
    [Fact]
    public async Task ACrossTenantParentIs404() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        await CreateSubscriptionsAsync(tenant, 1);

        var refused = await scopes.ListAsync(
            new() { ParentPath = ScopeId.Tenant(tenant).Path, Caller = ResourceManagerCluster.Caller(Guid.NewGuid()) },
            TestContext.Current.CancellationToken
        );

        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        SwitchableScopeAuthorizer.CollectionsAsked.ShouldBeEmpty("a cross-tenant listing reached the filter");
    }

    // ── Paging ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         Pages are cut by the id in its <c>N</c> form, ordinally; the continuation is the last
    ///         id examined; and walking every page visits each subscription exactly once.
    ///     </b>
    /// </summary>
    [Fact]
    public async Task PagesByOrdinalIdWithAContinuationAndNoOverlap() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 5);

        var seen = new List<string>();
        var continuation = "";
        var pages = 0;

        do {
            var page = await ListAsync(ScopeId.Tenant(tenant), 2, continuation);

            pages++;
            seen.AddRange(page.Items.Select(static x => x.Path));

            if (page.HasMore) {
                // ⚠ The last member EXAMINED, in the form the next request resumes after.
                page.Continuation.ShouldBe(
                    ScopeId.ParsePath(page.Items[^1].Path)
                        .GetValueOrThrow()
                        .SubscriptionId.ToString("N", CultureInfo.InvariantCulture)
                );
            }

            continuation = page.Continuation;
        } while (continuation.Length > 0);

        pages.ShouldBe(3, "five members at two per page is three pages");
        seen.ShouldBe(created.Select(static x => x.Path), "the pages are the ordinal walk, each member once");
        seen.Distinct(StringComparer.Ordinal).Count().ShouldBe(5);
    }

    /// <summary>
    ///     ⚠ <b>A page made entirely of hidden members still advances.</b>
    /// </summary>
    /// <remarks>
    ///     The continuation is the last member examined and not the last one returned; otherwise a
    ///     caller with narrow rights in a wide tenant would loop on the same page forever.
    /// </remarks>
    [Fact]
    public async Task AFullyHiddenPageStillAdvances() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 3);

        SwitchableScopeAuthorizer.Hidden[created[0]] = true;
        SwitchableScopeAuthorizer.Hidden[created[1]] = true;

        var first = await ListAsync(ScopeId.Tenant(tenant), 2);

        first.Items.ShouldBeEmpty();
        first.HasMore.ShouldBeTrue("an empty page with more behind it must still carry a continuation");

        var second = await ListAsync(ScopeId.Tenant(tenant), 2, first.Continuation);

        second.Items.Select(static x => x.Path).ShouldBe([created[2].Path]);
        second.HasMore.ShouldBeFalse();
    }

    /// <summary>
    ///     ⚠ <b><c>$top</c> is a cap the platform clamps and not a promise.</b>
    /// </summary>
    [Fact]
    public async Task TopAboveTheCapIsClampedToMaxPageSize() {
        new ScopeListRequest { Top = ScopeListRequest.MaxPageSize + 1 }.PageSize.ShouldBe(ScopeListRequest.MaxPageSize);
        new ScopeListRequest { Top = 0 }.PageSize.ShouldBe(ScopeListRequest.DefaultPageSize);
        new ScopeListRequest { Top = -7 }.PageSize.ShouldBe(ScopeListRequest.DefaultPageSize);
        new ScopeListRequest { Top = 3 }.PageSize.ShouldBe(3);

        // …and the clamp is what reaches the filter: an over-cap Top examines the whole (small)
        // tenant in one page, and the page is not a "full" one, so nothing is left to resume.
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 3);

        var page = await ListAsync(ScopeId.Tenant(tenant), ScopeListRequest.MaxPageSize + 500);

        page.Items.Select(static x => x.Path).ShouldBe(created.Select(static x => x.Path));
        page.HasMore.ShouldBeFalse();
        SwitchableScopeAuthorizer.CollectionsAsked.ShouldBe([(ScopeId.Tenant(tenant), 3)]);
    }

    // ── The filter's two paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         When the engine declines the page, the listing asks per member; when it answers, no
    ///         member is asked about — and the hidden member is out either way.
    ///     </b>
    /// </summary>
    [Fact]
    public async Task FallsBackToPerItemChecksWhenListObjectsDeclines() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var created = await CreateSubscriptionsAsync(tenant, 3);

        SwitchableScopeAuthorizer.Hidden[created[2]] = true;
        SwitchableScopeAuthorizer.Asked.Clear();

        // Declined: one collection question, then one per member.
        SwitchableScopeAuthorizer.AnswersCollections = false;

        var fallback = await ListAsync(ScopeId.Tenant(tenant));

        fallback.Items.Select(static x => x.Path).ShouldBe([created[0].Path, created[1].Path]);
        SwitchableScopeAuthorizer.CollectionsAsked.ShouldBe([(ScopeId.Tenant(tenant), 3)]);
        SwitchableScopeAuthorizer.Asked.ToList()
            .ShouldBe(created, "the fallback asks about every candidate, once, in order");

        // Answered: one collection question and no member asked about at all.
        SwitchableScopeAuthorizer.CollectionsAsked.Clear();
        SwitchableScopeAuthorizer.Asked.Clear();
        SwitchableScopeAuthorizer.AnswersCollections = true;

        var batched = await ListAsync(ScopeId.Tenant(tenant));

        batched.Items.Select(static x => x.Path).ShouldBe([created[0].Path, created[1].Path]);
        SwitchableScopeAuthorizer.CollectionsAsked.ShouldBe([(ScopeId.Tenant(tenant), 3)]);
        SwitchableScopeAuthorizer.Asked.ShouldBeEmpty(
            "the engine answered, so no member should have been checked on its own"
        );
    }

    /// <summary>
    ///     ⚠ <b>The resource-group collection runs the same filter over group scopes.</b>
    /// </summary>
    [Fact]
    public async Task ListResourceGroupsFiltersByGroupAndOrdersByName() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var subscription = (await CreateSubscriptionsAsync(tenant, 1))[0];

        foreach (var name in new[] { "prod", "dev", "staging" }) {
            (await CreateGroupAsync(subscription, name)).IsSuccess.ShouldBeTrue();
        }

        SwitchableScopeAuthorizer.Hidden[ScopeId.Group(tenant, subscription.SubscriptionId, "prod")] = true;

        var page = await ListAsync(subscription);

        page.Items.Select(static x => x.Name).ShouldBe(["dev", "staging"]);
        page.Items.ShouldAllBe(x => x.Type == ScopeTypeNames.ResourceGroup && x.Location == "eu-west-1");
        SwitchableScopeAuthorizer.CollectionsAsked.ShouldBe([(subscription, 3)]);
    }

    // ── The element's shape ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠
    ///     <b>
    ///         An element of the page is what a <c>GET</c> of that scope answers, member for
    ///         member.
    ///     </b>
    /// </summary>
    /// <remarks>
    ///     A list that rendered a thinner scope than a read would make a generated client's page
    ///     element and its scope type two shapes with one name. Asserted as record equality, so a
    ///     field added to one path and not the other fails here.
    /// </remarks>
    [Fact]
    public async Task AnItemRendersExactlyAsItsByIdGet() {
        ResourceManagerCluster.ResetDoubles();
        var tenant = await NewTenantAsync();
        var subscription = (await CreateSubscriptionsAsync(tenant, 2))[1];
        (await CreateGroupAsync(subscription, "prod")).IsSuccess.ShouldBeTrue();

        foreach (var parent in new[] { ScopeId.Tenant(tenant), subscription }) {
            var page = await ListAsync(parent);

            page.Items.ShouldNotBeEmpty();

            foreach (var item in page.Items) {
                var read = await scopes.ReadAsync(
                    new() { Path = item.Path, Caller = ResourceManagerCluster.Caller(tenant) },
                    TestContext.Current.CancellationToken
                );

                read.IsSuccess.ShouldBeTrue(read.Error?.Message);
                item.ShouldBe(read.GetValueOrThrow(), $"'{item.Path}' renders differently in the page and by id");
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh tenant record, so the class owns its whole listing.</summary>
    async Task<Guid> NewTenantAsync() {
        var tenant = Guid.NewGuid();

        var created = await cluster.For(tenant)
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(tenant))
            .CreateAsync("listing-" + tenant.ToString("N", CultureInfo.InvariantCulture)[..8], "Listing", "eu-west-1");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        return tenant;
    }

    /// <summary>
    ///     Creates <paramref name="count" /> subscriptions through the real path and returns them in
    ///     the order a listing walks them — by id in the <c>N</c> form, ordinally.
    /// </summary>
    async Task<List<ScopeId>> CreateSubscriptionsAsync(Guid tenant, int count) {
        var made = new List<ScopeId>();

        for (var i = 0; i < count; i++) {
            var scope = ScopeId.Subscription(tenant, Guid.NewGuid());

            var created = await scopes.CreateAsync(
                new() {
                    Path = scope.Path,
                    Body = $$"""{"displayName":"Subscription {{i}}"}""",
                    Caller = ResourceManagerCluster.Caller(tenant)
                },
                TestContext.Current.CancellationToken
            );

            created.IsSuccess.ShouldBeTrue(created.Error?.Message);
            made.Add(scope);
        }

        return [
            .. made.OrderBy(
                static x => x.SubscriptionId.ToString("N", CultureInfo.InvariantCulture),
                StringComparer.Ordinal
            )
        ];
    }

    Task<Result<ScopeSnapshot>> CreateGroupAsync(ScopeId subscription, string name) =>
        scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(subscription.TenantId, subscription.SubscriptionId, name).Path,
                Body = """{"location":"eu-west-1"}""",
                Caller = ResourceManagerCluster.Caller(subscription.TenantId)
            },
            TestContext.Current.CancellationToken
        );

    async Task<ScopeListPage> ListAsync(ScopeId parent, int top = 0, string continuation = "") {
        var listed = await scopes.ListAsync(
            new() {
                ParentPath = parent.Path,
                Caller = ResourceManagerCluster.Caller(parent.TenantId),
                Top = top,
                Continuation = continuation
            },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return listed.GetValueOrThrow();
    }
}
