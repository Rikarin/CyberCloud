using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.ResourceManager;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The collection <c>GET</c> now that its filter is one <c>ListObjects</c> per page — driven
///     through <c>ResourceManagerService</c>, <c>ReBacResourceAuthorizer</c>, <c>IListObjectsGrain</c>
///     and <c>CyberCloudSchema</c>, with nothing doubled.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="CollectionListingTests" /> pins what a listing must never show. This file pins
///         what it must show and through which path: a reader on one group sees that group and not
///         a sibling, a revoke through the store empties the page, a resource whose <c>parent</c>
///         edge has moved to the subscription leaves the group's listing while its membership row
///         is still there — and all of it answered by the walk, not by a <c>Check</c> per member,
///         which <see cref="AuthorizationMetrics" /> is read to prove.
///     </para>
///     <para>
///         ⚠ <b>The counters are process-wide and the silo is in-process</b>, which is what makes
///         them readable here at all. They are asserted as deltas across one call, never as totals,
///         because every other class in the collection moves them too.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class ListObjectsListingTests(IsolationCluster cluster) {
    /// <summary>A user granted <c>reader</c> on <c>prod</c> and nothing else.</summary>
    const string Reader = "rita";

    /// <summary>The sibling group, created by this class in the victim's subscription.</summary>
    const string Sibling = "staging";

    [Fact]
    public async Task AReaderOnOneGroupListsItAndNotASiblingAndARevokeEmptiesThePage() {
        var target = IsolationCatalog.Targets[0];

        await EnsureSiblingGroupAsync();

        await cluster.CreateAsync(
            target,
            "listobjects-prod",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        await CreateInSiblingAsync(target, "listobjects-staging");

        // rita reads prod, and only prod.
        var grant = RelationTuple.Create(
            Authorization.Contracts.ObjectRef.Of(ObjectTypes.ResourceGroup, GroupObjectId(IsolationCluster.Group)),
            Relations.Reader,
            SubjectRef.Of(ObjectTypes.User, Reader)
        ).GetValueOrThrow();

        var written = await Store().WriteAsync(grant);
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);

        var walksBefore = AuthorizationMetrics.ListObjects;
        var checksBefore = AuthorizationMetrics.Checks;

        var prod = await ListAsync(target, IsolationCluster.Group, Reader);

        prod.Resources.Select(x => x.Name).ShouldContain("listobjects-prod", "a group reader lists the group");
        prod.Resources.Select(x => x.Name).ShouldNotContain("listobjects-staging");

        // ⚠ THE PATH, NOT ONLY THE VERDICT. One walk for the page; the per-member Check is the
        // fallback and was not taken. A listing that reached the right answer through N checks would
        // pass every other assertion in this file and be the O(N) issue #37 exists to remove.
        (AuthorizationMetrics.ListObjects - walksBefore).ShouldBeGreaterThanOrEqualTo(1, "the page was answered by ListObjects");
        (AuthorizationMetrics.Checks - checksBefore).ShouldBe(0, "no member was Check-ed: the walk answered for the whole page");

        var staging = await ListAsync(target, Sibling, Reader);
        staging.Resources.ShouldBeEmpty("a grant on prod is nothing on staging — the scope is the group the page hangs off");

        // ── The revoke, through the store ───────────────────────────────────────────────────────
        var revoked = await Store().DeleteAsync(grant);
        revoked.IsSuccess.ShouldBeTrue(revoked.Error?.Message);

        var after = await ListAsync(target, IsolationCluster.Group, Reader);
        after.Resources.ShouldBeEmpty("the reverse index lost the entry with the forward one, so the walk finds nothing");
    }

    [Fact]
    public async Task AResourceReparentedToTheSubscriptionLeavesTheGroupListingWhileItsMembershipRemains() {
        var target = IsolationCatalog.Targets[0];
        const string name = "listobjects-parked";

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        (await ListAsync(target, IsolationCluster.Group, IsolationCluster.VictimUser))
            .Resources.Select(x => x.Name)
            .ShouldContain(name, "the control: the group owner lists what they created");

        // ⚠ THE EDGE ALONE, NOT THE WHOLE PARK. OperationGrain.ParkAsync also unlists the member,
        // which would hide the resource from the page before the filter ever ran —
        // SoftDeletePathTests.ASoftDeletedResourceIsInNoListingBecauseItLeftItsGroupsMembership
        // asserts underneath the filter for exactly that reason. Moving only the edge leaves the
        // membership row in place, so what hides the resource here is docs/plan/08 § Soft delete's
        // authorization rule applied by the walk: it now hangs off the subscription, and a walk
        // scoped to the group does not reach it.
        var writer = new ReBacResourceRelationWriter(cluster.Grains, NullLogger<ReBacResourceRelationWriter>.Instance);

        var parked = await writer.ReparentToSubscriptionAsync(address, Guid.Empty, TestContext.Current.CancellationToken);
        parked.IsSuccess.ShouldBeTrue(parked.Error?.Message);

        (await ListAsync(target, IsolationCluster.Group, IsolationCluster.VictimUser))
            .Resources.Select(x => x.Name)
            .ShouldNotContain(name, "a resource whose parent is the subscription is not under the group");

        var restored = await writer.ReparentFromSubscriptionAsync(address, Guid.Empty, TestContext.Current.CancellationToken);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        (await ListAsync(target, IsolationCluster.Group, IsolationCluster.VictimUser))
            .Resources.Select(x => x.Name)
            .ShouldContain(name, "and a restore puts it back in the listing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    async Task<ResourceListPage> ListAsync(IsolationTarget target, string group, string user) {
        var collection = ResourceCollectionId.Of(
            new(IsolationCluster.Victim, IsolationCluster.VictimSubscription, group, target.Type, "probe", Guid.Empty, target.ParentNames)
        );

        var listed = await cluster.Manager.ListAsync(
            new() {
                Path = collection.Path,
                ApiVersion = target.ApiVersion,
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, user)
            },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return listed.GetValueOrThrow();
    }

    /// <summary>Creates <see cref="Sibling" /> in the victim's subscription, and makes the victim its owner.</summary>
    async Task EnsureSiblingGroupAsync() {
        var group = cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(IsolationCluster.VictimSubscription, Sibling));

        if ((await group.GetAsync()).IsSuccess) {
            return;
        }

        var created = await group.CreateAsync(IsolationCluster.Victim, "eu-west-1");
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(ObjectTypes.ResourceGroup, GroupObjectId(Sibling)),
            Relations.Owner,
            SubjectRef.Of(ObjectTypes.User, IsolationCluster.VictimUser)
        );
    }

    async Task CreateInSiblingAsync(IsolationTarget target, string name) {
        ResourceId address = new(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            Sibling,
            target.Type,
            name,
            Guid.Empty,
            target.ParentNames
        );

        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
    }

    static string GroupObjectId(string group) =>
        IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture) + "-" + group;

    ITupleStoreGrain Store() =>
        cluster.For(IsolationCluster.Victim).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(IsolationCluster.Victim));
}
