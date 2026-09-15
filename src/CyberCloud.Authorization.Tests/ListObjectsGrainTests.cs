using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     <see cref="IListObjectsGrain" /> against real grains: the tuple store writes both halves,
///     the walk reads the reverse half, and a revoke through the store is a revoke here.
/// </summary>
/// <remarks>
///     <para>
///         <c>ListObjectsEvaluatorTests</c> pins the algorithm over an in-memory tuple set; this
///         file pins what the in-memory reader cannot — that the entries the walk reads are the
///         ones <c>TupleStoreGrain</c>'s step 4 wrote, that a delete through the store removes them
///         (<c>TwoGrainWriteTests</c> is what proves the two halves stay together), that the page's
///         token is the store's version, and that paging is stable across calls to an activation
///         that holds no state.
///     </para>
///     <para>
///         ⚠ <b>Each test owns a tenant</b>, for the reason every other class in this suite does: a
///         tenant's relation version is one counter, and a class sharing it with another would see
///         that class's writes in its tokens.
///     </para>
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class ListObjectsGrainTests(AuthorizationCluster cluster) {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    [Fact]
    public async Task AReaderOnAGroupListsThatGroupsResourcesAndNotASiblingsAndARevokeEmptiesIt() {
        var tenant = AuthorizationCluster.Tenant(500);
        await SeedTwoGroupsAsync(tenant, "l1");

        var granted = await cluster.WriteAsync(tenant, "resourceGroup:l1-alpha#reader@user:alice");

        var page = await ListAsync(tenant, Alice, new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read });

        page.Outcome.ShouldBe(ListObjectsOutcome.Complete);
        page.Objects.Select(x => x.Id).ShouldBe(["l1-a1", "l1-a2"]);
        page.Objects.ShouldAllBe(x => x.Type == ObjectTypes.Resource);
        page.Token.Version.ShouldBeGreaterThanOrEqualTo(granted.Version, "the walk ran at or after the grant landed");
        page.Verified.ShouldBeFalse();
        page.HasMore.ShouldBeFalse();

        // ⚠ THE REVOKE, THROUGH THE STORE. A delete that removed the forward half and left the
        // reverse half would keep listing a resource the caller can no longer read — which is the
        // direction docs/plan/07 § Storage says is only ever a miss, never a leak, and this is where
        // that sentence is held to.
        var revoked = await cluster.RevokeAsync(tenant, "resourceGroup:l1-alpha#reader@user:alice");

        var after = await ListAsync(tenant, Alice, new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read });

        after.Objects.ShouldBeEmpty("the grant is gone from both halves");
        after.Token.Version.ShouldBeGreaterThanOrEqualTo(revoked.Version);
    }

    [Fact]
    public async Task ScopedToOneGroupASubscriptionOwnerSeesOnlyThatGroupAndTheWalkStaysSmall() {
        var tenant = AuthorizationCluster.Tenant(501);
        await SeedTwoGroupsAsync(tenant, "l2");
        await cluster.WriteAsync(tenant, "subscription:l2-sub#owner@user:alice");

        var everything = await ListAsync(tenant, Alice, new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read });
        everything.Objects.Select(x => x.Id).ShouldBe(["l2-a1", "l2-a2", "l2-b1", "l2-b2"]);

        var scoped = await ListAsync(
            tenant,
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Of(ObjectTypes.ResourceGroup, "l2-alpha"),
                WithinDepth = 1
            }
        );

        scoped.Objects.Select(x => x.Id).ShouldBe(["l2-a1", "l2-a2"]);

        // alice, the subscription, alpha — beta's index and every resource's are never activated.
        scoped.ReverseReads.ShouldBe(3, "the scope bounds the walk, not only the answer");
    }

    [Fact]
    public async Task PagesResumeWhereTheLastOneStoppedAndAgreeWithTheWholeAnswer() {
        var tenant = AuthorizationCluster.Tenant(502);
        await SeedTwoGroupsAsync(tenant, "l3");
        await cluster.WriteAsync(tenant, "subscription:l3-sub#reader@user:alice");

        List<string> collected = [];
        var continuation = string.Empty;
        var pages = 0;

        do {
            var page = await ListAsync(
                tenant,
                Alice,
                new() {
                    ObjectType = ObjectTypes.Resource,
                    Permission = Permissions.Read,
                    PageSize = 3,
                    Continuation = continuation
                }
            );

            page.Objects.Count.ShouldBeLessThanOrEqualTo(3);
            collected.AddRange(page.Objects.Select(x => x.Id));
            continuation = page.Continuation;
            pages++;
        } while (continuation.Length > 0);

        pages.ShouldBe(2, "four resources at three a page");
        collected.ShouldBe(["l3-a1", "l3-a2", "l3-b1", "l3-b2"]);
    }

    [Fact]
    public async Task AUsersetSubjectIsListedThroughItsObjectsGrainWithTheRelationInTheRequest() {
        // GrainKeys.ListObjects carries no userset relation, so "what may group:eng#member read" is
        // rel/list/group/eng with SubjectRelation = member — the same split the reverse index makes.
        var tenant = AuthorizationCluster.Tenant(503);
        await SeedTwoGroupsAsync(tenant, "l4");
        await cluster.WriteAsync(tenant, "resourceGroup:l4-beta#contributor@group:l4-eng#member");

        var members = await ListAsync(
            tenant,
            SubjectRef.Userset(ObjectTypes.Group, "l4-eng", Relations.Member),
            new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read }
        );

        members.Objects.Select(x => x.Id).ShouldBe(["l4-b1", "l4-b2"]);

        var theGroupItself = await ListAsync(
            tenant,
            SubjectRef.Of(ObjectTypes.Group, "l4-eng"),
            new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read }
        );

        theGroupItself.Objects.ShouldBeEmpty("group:l4-eng and group:l4-eng#member are two subjects sharing one grain");
    }

    [Fact]
    public async Task AnUnknownPermissionIsARefusalFromTheGrainToo() {
        var tenant = AuthorizationCluster.Tenant(504);

        var refused = await Grain(tenant, Alice)
            .ListObjectsAsync(new() { ObjectType = ObjectTypes.Resource, Permission = "raed" });

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
    }

    /// <summary>
    ///     Two groups of two resources under one subscription under one tenant, with every edge the
    ///     platform's own writers would produce.
    /// </summary>
    async Task SeedTwoGroupsAsync(Guid tenant, string prefix) {
        foreach (var tuple in new[] {
                     $"subscription:{prefix}-sub#parent@tenant:{prefix}-tenant",
                     $"resourceGroup:{prefix}-alpha#parent@subscription:{prefix}-sub",
                     $"resourceGroup:{prefix}-beta#parent@subscription:{prefix}-sub",
                     $"resource:{prefix}-a1#parent@resourceGroup:{prefix}-alpha",
                     $"resource:{prefix}-a2#parent@resourceGroup:{prefix}-alpha",
                     $"resource:{prefix}-b1#parent@resourceGroup:{prefix}-beta",
                     $"resource:{prefix}-b2#parent@resourceGroup:{prefix}-beta"
                 }) {
            await cluster.WriteAsync(tenant, tuple);
        }
    }

    IListObjectsGrain Grain(Guid tenant, SubjectRef subject) =>
        cluster.For(tenant).GetGrain<IListObjectsGrain>(GrainKeys.ListObjects(subject.Type, subject.Id));

    async Task<ListObjectsPage> ListAsync(Guid tenant, SubjectRef subject, ListObjectsRequest request) {
        var listed = await Grain(tenant, subject).ListObjectsAsync(request with { SubjectRelation = subject.Relation });

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        return listed.GetValueOrThrow();
    }
}
