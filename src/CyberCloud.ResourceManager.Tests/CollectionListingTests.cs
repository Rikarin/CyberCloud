using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The collection <c>GET</c> — <c>IResourceManager.ListAsync</c> — driven through the real write
///     path rather than against the group grain on its own.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every case here starts at <see cref="IResourceManager" />, for the reason
///         <see cref="GroupMembershipTests" /> gives.</b> The enumeration source is
///         <c>IResourceGroupGrain</c>'s membership, and until that grain's methods were called by the
///         write path a listing built on it answered "no resources" for a group full of them
///         <i>and was right</i>. A suite that seeded membership by calling the grain would test the
///         listing against a state nothing produces.
///     </para>
///     <para>
///         ⚠ <b>What is <i>not</i> proven here is the filter's verdict, only its shape.</b>
///         <see cref="SwitchableAuthorizer" /> stands in for the enforcement seam — see its remarks —
///         so these cases pin that the filter runs once per member, that a hidden member leaves no
///         trace and that paging advances past one. Whether the real engine hides the right resources
///         is asserted against <c>ReBacResourceAuthorizer</c> and <c>CyberCloudSchema</c> in
///         <c>test/CyberCloud.Isolation</c>, which is where a listing that returns another tenant's
///         names belongs.
///     </para>
///     <para>
///         ⚠ <b><see cref="ResourceManagerCluster.IsolatedSubscription" /></b>, for the reason that
///         property's remarks give: this class creates a dozen resources whose committed quota
///         nothing gives back.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class CollectionListingTests(ResourceManagerCluster cluster) {
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    // ── What a listing returns ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A created resource is in its type's collection, at its own address, before the
    ///     operation converges.</b>
    /// </summary>
    /// <remarks>
    ///     The two halves are the same pair <c>GroupMembershipTests</c> asserts on membership: a
    ///     listing that only showed converged resources would hide every in-flight create, which is
    ///     the window a create spends most of its life in and the one a portal is watching.
    /// </remarks>
    [Fact]
    public async Task ACreatedResourceIsInItsCollectionWhileItIsStillCreating() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("listed-one", "listing-a");
        await Group(address);

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var page = await List(Collection(address));

        page.Resources.Length.ShouldBe(1, "the create is in the group's membership from step 7b");
        page.Resources[0].Path.ShouldBe(address.Path);
        page.Resources[0].ProvisioningState.ShouldBe(ProvisioningState.Creating);
        page.Continuation.ShouldBe("", "one member is not a full page, so there is nothing to resume");
    }

    /// <summary>
    ///     ⚠ <b>A collection holds one type, and a resource of another type in the same group is not
    ///     in it.</b>
    /// </summary>
    /// <remarks>
    ///     Membership is per resource <i>group</i> and holds every type in it; the address being
    ///     listed names one type. A filter that read membership without narrowing to the type would
    ///     return resources at addresses that do not begin with the collection's path — which is not
    ///     a leak, but it is an answer to a question nobody asked, and a generated SDK's
    ///     <c>WidgetResource</c> would be handed a vault.
    /// </remarks>
    [Fact]
    public async Task ACollectionHoldsOneTypeAndNotTheGroupsOtherTypes() {
        ResourceManagerCluster.ResetDoubles();
        var widget = Address("mixed-widget", "listing-b");
        var vault = VaultAddress("mixed-vault", "listing-b");
        await Group(widget);

        (await Create(widget)).IsSuccess.ShouldBeTrue();
        (await CreateVault(vault)).IsSuccess.ShouldBeTrue();

        var widgets = await List(Collection(widget));
        var vaults = await List(Collection(vault));

        widgets.Resources.Select(x => x.Path).ShouldBe([widget.Path]);
        vaults.Resources.Select(x => x.Path).ShouldBe([vault.Path]);
    }

    /// <summary>
    ///     ⚠ <b>A nested collection lists the children of the parent its address names, and not the
    ///     children of a sibling.</b>
    /// </summary>
    /// <remarks>
    ///     <c>ResourceCollectionId</c> interleaves the way <c>ResourceId</c> does, so
    ///     <c>…/widgets/{a}/gadgets</c> and <c>…/widgets/{b}/gadgets</c> are two collections of one
    ///     type. A filter that narrowed on the <i>type</i> alone would merge them — every gadget in
    ///     the group, under whichever parent the caller happened to name — which is the failure the
    ///     interleaved grammar was chosen to make impossible at the address level.
    /// </remarks>
    [Fact]
    public async Task ANestedCollectionListsOneParentsChildrenAndNotASiblings() {
        ResourceManagerCluster.ResetDoubles();
        var first = Address("nest-a", "listing-c");
        var second = Address("nest-b", "listing-c");
        await Group(first);

        (await Create(first)).IsSuccess.ShouldBeTrue();
        (await Create(second)).IsSuccess.ShouldBeTrue();

        var underFirst = Child("nest-a", "gadget-one", "listing-c");
        var underSecond = Child("nest-b", "gadget-two", "listing-c");

        (await CreateChild(underFirst)).IsSuccess.ShouldBeTrue();
        (await CreateChild(underSecond)).IsSuccess.ShouldBeTrue();

        var page = await List(Collection(underFirst));

        page.Resources.Select(x => x.Path).ShouldBe([underFirst.Path]);
    }

    /// <summary>
    ///     ⚠ <b>A resource being torn down is still listed, and it is listed in
    ///     <see cref="ProvisioningState.Deleting" />.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/06 § Two-phase create: a resource whose data-plane teardown has not converged is
    ///     <i>"visible in listings with that state — never silently gone while its pods still run and
    ///     its meter still ticks"</i>. That sentence is about <b>this</b> endpoint, and it was
    ///     unassertable until there was one.
    /// </remarks>
    [Fact]
    public async Task AResourceBeingTornDownIsStillInTheCollectionAndSaysSo() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("being-deleted", "listing-d");
        await Group(address);

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        await Converge(created.GetValueOrThrow());

        var deleted = await Delete(address);
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var page = await List(Collection(address));

        page.Resources.Length.ShouldBe(1, "a delete in flight does not remove the resource from listings");
        page.Resources[0].ProvisioningState.ShouldBe(ProvisioningState.Deleting);
    }

    // ── The filter ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A resource the caller may not read is absent from the page and leaves no trace of
    ///     itself in it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the case the endpoint exists to be safe for. ReBAC's <c>ListObjects</c> is M2,
    ///         so the filter is a <c>Check</c> per member — <c>IResourceManager.ListAsync</c>'s
    ///         remarks cost it — and without one a listing is a way to read the names of resources
    ///         the caller has no permission on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves are asserted: the name is gone, and nothing says a name was
    ///         removed.</b> A page that reported "1 of 2 hidden" would pass the first half and would
    ///         be the same enumeration oracle the enforcement seam closes by answering <c>404</c>
    ///         rather than <c>403</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AMemberTheCallerCannotReadIsNotInThePageAndIsNotCountedInIt() {
        ResourceManagerCluster.ResetDoubles();
        var mine = Address("filter-mine", "listing-e");
        var theirs = Address("filter-theirs", "listing-e");
        await Group(mine);

        var minted = await Create(mine);
        minted.IsSuccess.ShouldBeTrue(minted.Error?.Message);

        var hidden = await Create(theirs);
        hidden.IsSuccess.ShouldBeTrue(hidden.Error?.Message);

        SwitchableAuthorizer.Hidden[hidden.GetValueOrThrow().Resource.Id] = true;

        var page = await List(Collection(mine));

        page.Resources.Select(x => x.Path).ShouldBe([mine.Path]);
        page.Resources.ShouldNotContain(
            x => x.Path == theirs.Path,
            "a listing that returns a resource the caller cannot read is an enumeration oracle"
        );
    }

    /// <summary>
    ///     ⚠ <b>The filter is asked once per member and never once per group.</b>
    /// </summary>
    /// <remarks>
    ///     The cheap wrong implementation is one check against the resource group, which every
    ///     member of a group the caller holds any role on passes. It would look like a working
    ///     filter and would filter nothing —
    ///     <see cref="AMemberTheCallerCannotReadIsNotInThePageAndIsNotCountedInIt" /> catches it, and
    ///     this asserts the mechanism directly so the failure names itself.
    /// </remarks>
    [Fact]
    public async Task TheFilterAsksOncePerMember() {
        ResourceManagerCluster.ResetDoubles();
        var group = "listing-f";
        await Group(Address("count-a", group));

        foreach (var name in new[] { "count-a", "count-b", "count-c" }) {
            (await Create(Address(name, group))).IsSuccess.ShouldBeTrue();
        }

        SwitchableAuthorizer.Asked.Clear();

        var page = await List(Collection(Address("count-a", group)));

        page.Resources.Length.ShouldBe(3);
        SwitchableAuthorizer.Asked.Count.ShouldBe(3, "one Check per member examined, and no more");
    }

    // ── Paging ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A page bounds the members <i>examined</i>, and the continuation advances past ones
    ///     the filter dropped.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The page size is what makes the cost of one request bounded by the platform rather
    ///         than chosen by the caller, so it has to cap the <c>Check</c>s and not the results.
    ///         The consequence is that a page can come back empty and still have a next page, and
    ///         the continuation therefore names the last member <i>examined</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A continuation that named the last member <i>returned</i> is the bug this
    ///         pins.</b> A caller whose first page is entirely filtered out would get an empty
    ///         continuation, either stopping early — losing every resource after the hidden ones — or,
    ///         if the token defaulted to empty meaning "start again", looping on the same page
    ///         forever.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task APageBoundsTheMembersExaminedAndTheContinuationAdvancesPastHiddenOnes() {
        ResourceManagerCluster.ResetDoubles();
        var group = "listing-g";
        await Group(Address("page-a", group));

        var created = new List<Guid>();
        foreach (var name in new[] { "page-a", "page-b", "page-c", "page-d" }) {
            var accepted = await Create(Address(name, group));
            accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
            created.Add(accepted.GetValueOrThrow().Resource.Id);
        }

        // The first two by canonical path — the order the listing walks in.
        SwitchableAuthorizer.Hidden[created[0]] = true;
        SwitchableAuthorizer.Hidden[created[1]] = true;

        var collection = Collection(Address("page-a", group));

        var first = await List(collection, top: 2);

        first.Resources.ShouldBeEmpty("both members on this page are hidden from this caller");
        first.HasMore.ShouldBeTrue("an empty page is not the end of the collection");

        var second = await List(collection, top: 2, continuation: first.Continuation);

        second.Resources.Select(x => x.Name).ShouldBe(["page-c", "page-d"]);
    }

    /// <summary>
    ///     ⚠ <b>A <c>$top</c> above <see cref="ListRequest.MaxPageSize" /> is clamped rather than
    ///     refused.</b>
    /// </summary>
    /// <remarks>
    ///     A refusal would make a client that asked for too much fail rather than page, and the cap
    ///     exists to bound the platform's work and not to correct the caller. Refusing would also put
    ///     the platform's own limit into an error message, which is a number that then cannot change.
    /// </remarks>
    [Fact]
    public void APageSizeAboveTheCapIsClampedAndZeroMeansTheDefault() {
        new ListRequest { Top = 10_000 }.PageSize.ShouldBe(ListRequest.MaxPageSize);
        new ListRequest { Top = 0 }.PageSize.ShouldBe(ListRequest.DefaultPageSize);
        new ListRequest { Top = -3 }.PageSize.ShouldBe(ListRequest.DefaultPageSize);
        new ListRequest { Top = 7 }.PageSize.ShouldBe(7);
    }

    // ── The absences ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>A collection in a group that does not exist is the canonical <c>404</c>, byte for
    ///     byte the one an absent resource gets.</b>
    /// </summary>
    /// <remarks>
    ///     An empty page would be a statement about a group the caller has been told exists, which
    ///     lets them enumerate which group names are live in a subscription one probe at a time —
    ///     docs/plan/07 § The enforcement seam.
    /// </remarks>
    [Fact]
    public async Task ACollectionInAGroupThatDoesNotExistIsTheCanonicalAbsence() {
        ResourceManagerCluster.ResetDoubles();
        var collection = Collection(Address("never-made", "listing-nowhere"));

        var listed = await cluster.Manager.ListAsync(
            new() { Path = collection.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        listed.IsFailure.ShouldBeTrue();
        listed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        listed.Error.Message.ShouldBe($"'{collection.Path}' does not exist.");
    }

    /// <summary>
    ///     ⚠ <b>A collection path naming another tenant is the same <c>404</c>, and the tenant check
    ///     runs before the registry is consulted.</b>
    /// </summary>
    /// <remarks>
    ///     The ordering is the property: everything below the ownership checks describes the platform
    ///     to the caller, and a description handed out through a path the caller does not own is an
    ///     oracle even when no data comes with it. A listing is the largest such description this API
    ///     has.
    /// </remarks>
    [Fact]
    public async Task ACollectionPathInAnotherTenantIsTheSameAbsence() {
        ResourceManagerCluster.ResetDoubles();

        var elsewhere = new ResourceCollectionId(
            Guid.Parse("99999999-9999-4999-8999-999999999999"),
            Subscription,
            "prod",
            ConformingReconciler.TypeName
        );

        var listed = await cluster.Manager.ListAsync(
            new() { Path = elsewhere.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        listed.IsFailure.ShouldBeTrue();
        listed.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        listed.Error.Message.ShouldBe($"'{elsewhere.Path}' does not exist.");
    }

    /// <summary>
    ///     ⚠ <b>A resource path is not a collection path, and the refusal says which grammar it
    ///     failed.</b>
    /// </summary>
    /// <remarks>
    ///     The two are disjoint by construction — an even tail is a resource, an odd one is a
    ///     collection — so this is not an ordering question. It is asserted because the disjointness
    ///     is what lets the gateway decide from the path alone, and a parser that grew tolerant of
    ///     the other shape would move that decision to the verb without anybody noticing.
    /// </remarks>
    [Fact]
    public async Task AResourcePathIsRefusedAsACollection() {
        ResourceManagerCluster.ResetDoubles();
        var address = Address("not-a-collection", "listing-h");

        var listed = await cluster.Manager.ListAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        listed.IsFailure.ShouldBeTrue();
        listed.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        listed.Error.Message.ShouldContain("ends on a name");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static ResourceId Address(string name, string group) =>
        new(ResourceManagerCluster.Tenant, Subscription, group, ConformingReconciler.TypeName, name, Guid.Empty);

    static ResourceId VaultAddress(string name, string group) =>
        new(ResourceManagerCluster.Tenant, Subscription, group, TestingProvider.VaultTypeName, name, Guid.Empty);

    static ResourceId Child(string parent, string name, string group) =>
        new(
            ResourceManagerCluster.Tenant,
            Subscription,
            group,
            TestingProvider.ChildTypeName,
            name,
            Guid.Empty,
            parent
        );

    static ResourceCollectionId Collection(ResourceId address) => ResourceCollectionId.Of(address);

    async Task<ResourceListPage> List(ResourceCollectionId collection, int top = 0, string continuation = "") {
        var listed = await cluster.Manager.ListAsync(
            new() {
                Path = collection.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller(),
                Top = top,
                Continuation = continuation
            },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);

        return listed.GetValueOrThrow();
    }

    /// <summary>Creates the group a case addresses. Idempotent on the same region.</summary>
    /// <remarks>
    ///     ⚠ <b>A group per case, rather than the suite's shared <c>prod</c>.</b> A listing asserts on
    ///     what a group holds, so two cases sharing one group would each see the other's resources —
    ///     and the failure would land in whichever ran second rather than in the one that wrote them.
    ///     The write path refuses a create into a group that does not exist, so this is also what
    ///     makes the create succeed at all.
    /// </remarks>
    async Task Group(ResourceId address) {
        var made = await cluster.Group(address).CreateAsync(address.TenantId, "eu-west-1");
        made.IsSuccess.ShouldBeTrue(made.Error?.Message);
    }

    Task<Result<WriteAccepted>> Create(ResourceId address) => Put(address, TestingProvider.Body());

    Task<Result<WriteAccepted>> CreateVault(ResourceId address) => Put(address, TestingProvider.VaultBody());

    Task<Result<WriteAccepted>> CreateChild(ResourceId address) => Put(address, TestingProvider.ChildBody());

    Task<Result<WriteAccepted>> Put(ResourceId address, string body) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = body,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> Delete(ResourceId address) =>
        cluster.Manager.DeleteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    async Task Converge(WriteAccepted accepted) {
        if (accepted.OperationId == Guid.Empty) {
            return;
        }

        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
