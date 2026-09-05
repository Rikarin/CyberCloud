using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <see cref="IParkedResourceRegistryGrain" /> on its own — docs/plan/08 § Soft delete's
///     per-resource-group registry of parked resources, issue #71.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The grain is driven directly here and through the write path in
///         <see cref="SoftDeletePathTests" />, and the split is deliberate.</b> The end-to-end cases
///         prove the three call sites are wired — that a delete writes an entry, a restore clears it
///         and a purge clears it — which is the half that would silently stop being true if somebody
///         moved a line. This file proves the grain's own rules, several of which the write path
///         cannot reach at all: an unresolved address, an address in another group, and a nested type
///         whose ancestor name is what separates one collection from another. Reaching those through
///         a create would mean building a resource the platform refuses to build.
///     </para>
///     <para>
///         ⚠ <b>Nothing here creates a resource, so nothing here spends the shared subscription's
///         quota</b> — see <c>ResourceManagerCluster.IsolatedSubscription</c> on why that budget is a
///         coupling between unrelated classes. Each case uses a resource group name of its own, which
///         is what keeps two cases in one grain from being one case.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ParkedResourceRegistryTests(ResourceManagerCluster cluster) {
    static Guid Tenant => ResourceManagerCluster.Tenant;

    static Guid Subscription => ResourceManagerCluster.Subscription;

    [Fact]
    public async Task AParkedResourceIsListedAndAnUnparkedOneIsNot() {
        var address = Vault("registry-basic", "kept");

        (await Registry(address).ParkAsync(address)).IsSuccess.ShouldBeTrue();

        var listed = (await Registry(address).ListAsync()).GetValueOrThrow();

        listed.Select(x => x.ResourceId).ShouldBe([address.Id]);
        listed[0].Path.ShouldBe(address.Path);
        listed[0].AddressOf().Name.ShouldBe("kept");
        listed[0].AddressOf().CanonicalPath.ShouldBe(address.CanonicalPath);
        listed[0].ParkedAt.ShouldNotBe(default);

        (await Registry(address).UnparkAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await Registry(address).ListAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task ParkingTwiceKeepsTheFirstParkedAtBecauseTheCallerIsReDrivenFromAReminder() {
        // ⚠ THE ASSERTION IS THAT THE TIMESTAMP DID NOT MOVE, not that the second call succeeded.
        // OperationGrain.ParkAsync is re-driven from a durable reminder, so a second call is the
        // ordinary path; restamping would make "when was this deleted" a function of how many times
        // the teardown was interrupted. The same rule as IResourceIndexGrain.SoftDeleteAsync not
        // restamping its deadline on a re-drive.
        //
        // ⚠ AND THE CLOCK IS ADVANCED BETWEEN THE TWO CALLS, WITHOUT WHICH THIS CASE ASSERTS
        // NOTHING. TestClock only moves when a test moves it, so a grain that restamped on every
        // park would write the same instant twice and pass. One minute is enough to be visible and
        // far too little to touch the seven-day window the vault type declares, which other cases in
        // this collection depend on not having expired.
        ResourceManagerCluster.ResetDoubles();

        var address = Vault("registry-idempotent", "twice");

        (await Registry(address).ParkAsync(address)).IsSuccess.ShouldBeTrue();
        var first = (await Registry(address).ListAsync()).GetValueOrThrow()[0].ParkedAt;

        TestClock.Instance.Advance(TimeSpan.FromMinutes(1));

        (await Registry(address).ParkAsync(address)).IsSuccess.ShouldBeTrue();

        var listed = (await Registry(address).ListAsync()).GetValueOrThrow();

        listed.Count.ShouldBe(1, "a re-park is the same entry rather than a second one");
        listed[0].ParkedAt.ShouldBe(first);
    }

    [Fact]
    public async Task UnparkingSomethingThatIsNotThereSucceedsBecauseAbsenceIsTheGoal() {
        // Both callers are retried — a restore by its caller, a purge by whichever front drove it —
        // so a refusal here would turn an operation whose work landed into one that never converges.
        var address = Vault("registry-absent", "never-parked");

        (await Registry(address).UnparkAsync(address.Id)).IsSuccess.ShouldBeTrue();
        (await Registry(address).UnparkAsync(Guid.NewGuid())).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnresolvedAddressIsRefusedBecauseNothingCouldEverUnparkIt() {
        // docs/plan/06 § Identifiers keeps GUIDs out of paths, so an address parsed from one carries
        // Guid.Empty. Recording it would be a name with no way out of the registry — and Guid.Empty
        // is also default(ResourceId)'s id, so a second such entry would overwrite the first.
        var address = Vault("registry-unresolved", "no-guid") with { Id = Guid.Empty };

        var refused = await Registry(address).ParkAsync(address);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public async Task AnAddressInAnotherGroupIsRefusedRatherThanRecorded() {
        // ⚠ The quiet version of this is a resource recorded as recoverable in a group it is not in,
        // where the restore that would find it is a restore nobody is entitled to make. The grain
        // checks its own key for the same reason IResourceGroupGrain.BeginCreateAsync does.
        var here = Vault("registry-here", "mine");
        var elsewhere = Vault("registry-elsewhere", "theirs");

        var refusedByGroup = await Registry(here).ParkAsync(elsewhere);

        refusedByGroup.IsFailure.ShouldBeTrue();
        refusedByGroup.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        var otherTenant = elsewhere with { TenantId = ResourceManagerCluster.OtherTenant };
        var refusedByTenant = await Registry(here).ParkAsync(otherTenant with { ResourceGroup = here.ResourceGroup });

        refusedByTenant.IsFailure.ShouldBeTrue();
        refusedByTenant.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        (await Registry(here).ListAsync()).GetValueOrThrow().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTypeFilterSeparatesTwoCollectionsOfTheSameNestedTypeUnderTwoParents() {
        // ⚠ THE CASE THE WRITE PATH CANNOT REACH, AND THE REASON ListOfTypeAsync TAKES A
        // ResourceCollectionId RATHER THAN A ResourceTypeName. A collection of `widgets/gadgets` is
        // addressed `…/widgets/{widgetName}/gadgets`, so two widgets in one group have two gadget
        // collections; a filter on the type alone would answer both at once and hand a caller the
        // parked children of a parent they did not ask about.
        const string group = "registry-nested";

        var underFirst = Child(group, "pg-main", "one");
        var underSecond = Child(group, "pg-spare", "two");
        var topLevel = Vault(group, "a-vault");

        foreach (var address in new[] { underFirst, underSecond, topLevel }) {
            (await Registry(address).ParkAsync(address)).IsSuccess.ShouldBeTrue();
        }

        var first = (await Registry(underFirst).ListOfTypeAsync(ResourceCollectionId.Of(underFirst)))
            .GetValueOrThrow();

        var second = (await Registry(underSecond).ListOfTypeAsync(ResourceCollectionId.Of(underSecond)))
            .GetValueOrThrow();

        var vaults = (await Registry(topLevel).ListOfTypeAsync(ResourceCollectionId.Of(topLevel)))
            .GetValueOrThrow();

        first.Select(x => x.AddressOf().Name).ShouldBe(["one"]);
        second.Select(x => x.AddressOf().Name).ShouldBe(["two"]);
        vaults.Select(x => x.AddressOf().Name).ShouldBe(["a-vault"]);

        // …and the calibration: all three are in the group, so the three short answers above are the
        // filter working rather than three empty registries.
        (await Registry(topLevel).ListAsync()).GetValueOrThrow().Count.ShouldBe(3);
    }

    [Fact]
    public async Task ACollectionInAnotherGroupIsRefusedRatherThanAnsweredEmpty() {
        // ⚠ Refused rather than empty, because empty is an answer a caller would believe. A registry
        // that answered "nothing recoverable" for a group it does not hold would be a listing that
        // lies in exactly the direction a restore acts on.
        var here = Vault("registry-scope", "kept");
        (await Registry(here).ParkAsync(here)).IsSuccess.ShouldBeTrue();

        var elsewhere = ResourceCollectionId.Of(Vault("registry-scope-other", "kept"));

        var refused = await Registry(here).ListOfTypeAsync(elsewhere);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public async Task TheListingIsOrderedByCanonicalPathSoAPageCanResumeOnIt() {
        // ⚠ The same ordering IResourceManager.ListAsync pages on, so a listing built over this
        // registry can carry the same continuation shape — "the next entry whose canonical path
        // sorts after this one" — without a second definition of what "next" means. A Dictionary's
        // own order is not one, and it is stable enough per run to make an unordered grain look
        // ordered, which is why this parks them in the reverse of the expected order.
        const string group = "registry-order";

        foreach (var name in new[] { "cherry", "apple", "banana" }) {
            var address = Vault(group, name);
            (await Registry(address).ParkAsync(address)).IsSuccess.ShouldBeTrue();
        }

        var listed = (await Registry(Vault(group, "apple")).ListAsync()).GetValueOrThrow();

        listed.Select(x => x.AddressOf().Name).ShouldBe(["apple", "banana", "cherry"]);
        listed.Select(x => x.AddressOf().CanonicalPath)
            .ShouldBe(listed.Select(x => x.AddressOf().CanonicalPath).Order(StringComparer.Ordinal));
    }

    IParkedResourceRegistryGrain Registry(ResourceId address) => cluster.Parked(address);

    /// <summary>A resolved vault address in <paramref name="group" />.</summary>
    /// <remarks>
    ///     ⚠ The GUID is real rather than <see cref="Guid.Empty" />, because the registry refuses an
    ///     unresolved address — see
    ///     <see cref="AnUnresolvedAddressIsRefusedBecauseNothingCouldEverUnparkIt" />. Nothing here
    ///     resolves through the index, so the GUID names no resource grain; this file tests the
    ///     registry's own bookkeeping and never follows an entry anywhere.
    /// </remarks>
    static ResourceId Vault(string group, string name) =>
        new(Tenant, Subscription, group, TestingProvider.VaultTypeName, name, Guid.NewGuid());

    /// <summary>A resolved address of the nested type, under <paramref name="parent" />.</summary>
    static ResourceId Child(string group, string parent, string name) =>
        new(Tenant, Subscription, group, TestingProvider.ChildTypeName, name, Guid.NewGuid(), parent);
}
