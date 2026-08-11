using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § The object model: <i>"Groups hold no member list. This is the decision that
///     makes the identity module small: membership is <c>group:X#member@user:Y</c>, so 'is Alice in
///     Eng' is a <c>Check</c>, 'who is in Eng' is an <c>Expand</c>, nested groups work with no extra
///     code, and revoking a group's access is a tuple write."</i>
/// </summary>
/// <remarks>
///     ⚠ These run against the <b>real</b> tuple store and check evaluator from
///     <c>CyberCloud.Authorization</c>. Substituting a double would test that this module calls
///     something, and the claim being made is stronger than that: it is that nesting and revocation
///     work <i>with no extra code here</i>, which is only observable through the real engine.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class GroupMembershipTests(IdentityCluster cluster) {
    [Fact]
    public void TheGrainStateHasNowhereToPutAMemberList() {
        // ⚠ THE STRUCTURAL ASSERTION. A rule saying "do not add a member list" is a review comment;
        // this is the test that fails when somebody adds one. Any collection-typed member on the
        // group's state is the second source of truth docs/plan/11 § The object model forbids.
        var collections = typeof(GroupGrainState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.PropertyType != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(x.PropertyType))
            .Select(x => x.Name)
            .ToList();

        collections.ShouldBeEmpty(
            "GroupGrainState must hold the group's identity and nothing else. Membership is "
            + "group:X#member@user:Y in the tuple store — a list here would be a second source of "
            + "truth and a hot spot for large groups (docs/plan/11 § The object model). If a listing "
            + "needs to be faster, the thing to add is docs/plan/07 § Storage's Leopard index, in the "
            + "authorization module."
        );
    }

    [Fact]
    public async Task AddingAMemberIsATupleWriteAndIsMemberIsACheck() {
        var groupId = Guid.NewGuid();
        var userId = await cluster.CreateUserAsync("member@example.com");

        var group = cluster.Group(groupId);
        (await group.CreateAsync("Engineering", "The people who build it")).IsSuccess.ShouldBeTrue();

        var alice = SubjectRef.Of(ObjectTypes.User, userId);

        (await group.IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeFalse();

        (await group.AddMemberAsync(alice)).IsSuccess.ShouldBeTrue();
        (await group.IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeTrue();

        // Revoking is a tuple delete, and it takes effect at the next check rather than by rewriting
        // a list somebody may be holding.
        (await group.RemoveMemberAsync(alice)).IsSuccess.ShouldBeTrue();
        (await group.IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task NestedGroupsWorkWithNoExtraCode() {
        var everyone = Guid.NewGuid();
        var engineering = Guid.NewGuid();
        var userId = await cluster.CreateUserAsync("nested@example.com");

        (await cluster.Group(everyone).CreateAsync("Everyone", "")).IsSuccess.ShouldBeTrue();
        (await cluster.Group(engineering).CreateAsync("Engineering", "")).IsSuccess.ShouldBeTrue();

        var alice = SubjectRef.Of(ObjectTypes.User, userId);

        // Alice is in Engineering…
        (await cluster.Group(engineering).AddMemberAsync(alice)).IsSuccess.ShouldBeTrue();

        // …and Engineering's `member` USERSET is a member of Everyone. That is what nesting IS: the
        // subject of a membership tuple is itself a userset. No code in CyberCloud.Identity knows
        // about nesting — the evaluator walks it.
        var engineeringMembers = SubjectRef.Userset(
            ObjectTypes.Group,
            engineering.ToString("N", CultureInfo.InvariantCulture),
            Relations.Member
        );

        (await cluster.Group(everyone).AddMemberAsync(engineeringMembers)).IsSuccess.ShouldBeTrue();

        (await cluster.Group(everyone).IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeTrue(
            "nesting must work through the evaluator without any code in the identity module"
        );

        // Removing Alice from the inner group removes her from the outer one, with one tuple delete.
        (await cluster.Group(engineering).RemoveMemberAsync(alice)).IsSuccess.ShouldBeTrue();
        (await cluster.Group(everyone).IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task AServicePrincipalCanBeAGroupMemberBecauseASubjectIsJustASubject() {
        var groupId = Guid.NewGuid();
        (await cluster.Group(groupId).CreateAsync("Automation", "")).IsSuccess.ShouldBeTrue();

        // ⚠ Nothing in IGroupGrain knows what a service principal is. That is the point of taking a
        // SubjectRef rather than a user id: a machine identity is a subject like any other, and a
        // member-list design would have needed a second column or a second list.
        var robot = SubjectRef.Of("servicePrincipal", Guid.NewGuid());

        (await cluster.Group(groupId).AddMemberAsync(robot)).IsSuccess.ShouldBeTrue();
        (await cluster.Group(groupId).IsMemberAsync(robot, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task RenamingAGroupIsFreeBecauseTheKeyIsAGuid() {
        var groupId = Guid.NewGuid();
        var userId = await cluster.CreateUserAsync("rename@example.com");
        var alice = SubjectRef.Of(ObjectTypes.User, userId);

        var group = cluster.Group(groupId);
        (await group.CreateAsync("Old Name", "")).IsSuccess.ShouldBeTrue();
        (await group.AddMemberAsync(alice)).IsSuccess.ShouldBeTrue();

        (await group.RenameAsync("New Name", "A better description")).IsSuccess.ShouldBeTrue();

        var descriptor = (await group.GetAsync()).GetValueOrThrow();
        descriptor.Name.ShouldBe("New Name");
        descriptor.GroupId.ShouldBe(groupId);

        // ⚠ Membership survives, because the ReBAC object id is the GUID and not the name. Keying by
        // name would have made a rename a re-tupling of every membership and every role assignment.
        (await group.IsMemberAsync(alice, Consistency.FullyConsistent)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task AGroupInOneTenantIsNotAGroupInAnother() {
        var groupId = Guid.NewGuid();

        (await cluster.Group(groupId, IdentityCluster.Tenant).CreateAsync("Shared Id", "")).IsSuccess.ShouldBeTrue();

        // The same GUID in another tenant is a different grain and a different ReBAC object.
        var elsewhere = await cluster.Group(groupId, IdentityCluster.OtherTenant).GetAsync();

        elsewhere.IsFailure.ShouldBeTrue();
        elsewhere.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task MembershipOfAGroupThatDoesNotExistIsNotFound() {
        var missing = cluster.Group(Guid.NewGuid());
        var alice = SubjectRef.Of(ObjectTypes.User, Guid.NewGuid());

        (await missing.IsMemberAsync(alice, null)).IsFailure.ShouldBeTrue();
        (await missing.AddMemberAsync(alice)).IsFailure.ShouldBeTrue();
    }
}
