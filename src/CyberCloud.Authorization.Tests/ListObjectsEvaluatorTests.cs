using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;
using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     The reverse walk of docs/plan/07 § ListObjects, over <see cref="CyberCloudSchema" /> in
///     memory: what a group grant reaches, what it does not, what a scope prunes, and what a cap
///     does.
/// </summary>
/// <remarks>
///     ⚠ <b>The schema is the shipping one, not a fixture.</b> Every case here is a sentence from
///     docs/plan/07 or docs/plan/08 that the resource list depends on — "a subject granted
///     <c>reader</c> on a resource group sees exactly that group's resources and nothing from a
///     sibling group" is the one issue #37 asks for by name — and a schema built for the test
///     would let the walk agree with itself. The hand-built cases are the shapes the platform
///     writes; <c>ListObjectsPropertyTests</c> is the shapes it does not.
/// </remarks>
public sealed class ListObjectsEvaluatorTests {
    static SubjectRef Alice => SubjectRef.Of(ObjectTypes.User, "alice");

    const string Sub = "subscription:sub1";
    const string GroupA = "resourceGroup:sub1-alpha";
    const string GroupB = "resourceGroup:sub1-beta";

    /// <summary>The tuples the platform writes for two groups of two resources each, and a tenant above.</summary>
    static readonly string[] TwoGroups = [
        $"{Sub}#parent@tenant:t1",
        $"{GroupA}#parent@{Sub}",
        $"{GroupB}#parent@{Sub}",
        $"resource:a1#parent@{GroupA}",
        $"resource:a2#parent@{GroupA}",
        $"resource:b1#parent@{GroupB}",
        $"resource:b2#parent@{GroupB}"
    ];

    // ── The question issue #37 asks ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AReaderOnAGroupSeesExactlyThatGroupsResourcesAndNothingFromASibling() {
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Read,
            [.. TwoGroups, $"{GroupA}#reader@user:alice"]
        );

        page.Outcome.ShouldBe(ListObjectsOutcome.Complete);
        Ids(page).ShouldBe(["a1", "a2"]);
        page.Verified.ShouldBeFalse("the read path is union-only, so the walk is exact and nothing is re-checked");
    }

    [Fact]
    public async Task AGrantThroughAGroupUsersetReachesTheSameResources() {
        // docs/plan/07 § Azure RBAC: "Contributor on resource group R for group G" is one tuple with
        // a userset subject, and nesting is a second one.
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Read,
            [
                .. TwoGroups,
                "group:eng#member@group:platform#member",
                "group:platform#member@user:alice",
                $"{GroupB}#contributor@group:eng#member"
            ]
        );

        Ids(page).ShouldBe(["b1", "b2"]);
    }

    [Fact]
    public async Task ASubscriptionOwnerSeesEveryResourceInEveryGroup() {
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Read,
            [.. TwoGroups, $"{Sub}#owner@user:alice"]
        );

        Ids(page).ShouldBe(["a1", "a2", "b1", "b2"]);
    }

    [Fact]
    public async Task ADirectGrantOnOneResourceReachesThatResourceAlone() {
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Read,
            [.. TwoGroups, "resource:b2#reader@user:alice"]
        );

        Ids(page).ShouldBe(["b2"]);
    }

    [Fact]
    public async Task NothingGrantedIsNothingListed() {
        var page = await List(Alice, ObjectTypes.Resource, Permissions.Read, TwoGroups);

        page.Outcome.ShouldBe(ListObjectsOutcome.Complete);
        page.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTypeAskedForIsTheTypeAnswered() {
        // The same grant reaches groups and resources; the caller names which.
        var groups = await List(
            Alice,
            ObjectTypes.ResourceGroup,
            Permissions.Read,
            [.. TwoGroups, $"{Sub}#reader@user:alice"]
        );

        Ids(groups).ShouldBe(["sub1-alpha", "sub1-beta"]);
    }

    // ── The scope ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithinAGroupASubscriptionOwnerSeesOnlyThatGroupAndTheWalkReadsNoSibling() {
        var reverse = InMemoryReverseRelationReader.Parse([.. TwoGroups, $"{Sub}#owner@user:alice"]);
        var forward = InMemoryRelationReader.Parse([.. TwoGroups, $"{Sub}#owner@user:alice"]);

        var page = await Evaluate(
            forward,
            reverse,
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse(GroupA).GetValueOrThrow(),
                WithinDepth = 1
            }
        );

        Ids(page).ShouldBe(["a1", "a2"]);

        // ⚠ THE BOUND ON THE WALK, NOT ONLY ON THE ANSWER. Descending from the subscription follows
        // alpha alone, and descending from alpha stops at depth 1 — so beta's reverse index is never
        // read and neither is any resource's. Reads: alice, the subscription, alpha. The tenant is
        // placed by a forward read and never reached, because nothing was granted on it.
        reverse.Reads.ShouldBe(3, "alice, subscription, alpha — and not beta, and not a resource");
    }

    [Fact]
    public async Task WithinAGroupADirectGrantElsewhereIsNotListedAndOneInsideIs() {
        string[] tuples = [
            .. TwoGroups, "resource:b2#reader@user:alice", "resource:a1#reader@user:alice"
        ];

        var page = await Evaluate(
            InMemoryRelationReader.Parse(tuples),
            InMemoryReverseRelationReader.Parse(tuples),
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse(GroupA).GetValueOrThrow(),
                WithinDepth = 1
            }
        );

        Ids(page).ShouldBe(["a1"], "b2 is readable and is in the other group; a1 was placed by walking its own chain");
    }

    [Fact]
    public async Task WithinDepthOneListsChildrenAndNotGrandchildren() {
        // A child resource's parent edge names its PARENT RESOURCE, not the group — the 2026-08-12
        // change ReBacResourceRelationWriter's remarks record. Listing the servers collection must
        // not walk every server for its databases.
        string[] tuples = [
            .. TwoGroups, $"resource:db1#parent@resource:a1", $"{GroupA}#reader@user:alice"
        ];

        var reverse = InMemoryReverseRelationReader.Parse(tuples);

        var servers = await Evaluate(
            InMemoryRelationReader.Parse(tuples),
            reverse,
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse(GroupA).GetValueOrThrow(),
                WithinDepth = 1
            }
        );

        Ids(servers).ShouldBe(["a1", "a2"]);
        reverse.Reads.ShouldBe(2, "alice and alpha — no resource was expanded for its children");

        var databases = await Evaluate(
            InMemoryRelationReader.Parse(tuples),
            InMemoryReverseRelationReader.Parse(tuples),
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse("resource:a1").GetValueOrThrow(),
                WithinDepth = 1
            }
        );

        Ids(databases).ShouldBe(["a1", "db1"], "at or below the scope: the server itself at depth 0, its database at depth 1");

        var everything = await Evaluate(
            InMemoryRelationReader.Parse(tuples),
            InMemoryReverseRelationReader.Parse(tuples),
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse(GroupA).GetValueOrThrow()
            }
        );

        Ids(everything).ShouldBe(["a1", "a2", "db1"], "no depth means every descendant");
    }

    // ── docs/plan/08 § Soft delete ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AParkedResourceHangsOffTheSubscriptionSoItLeavesTheGroupAndReachesSubscriptionReaders() {
        // What the writer does at a park: the edge moves from the group to the subscription and the
        // direct assignments are dropped. "The people who can see a deleted resource become the
        // people who hold subscription-scoped rights."
        string[] parked = [
            .. TwoGroups.Where(x => !x.StartsWith("resource:a2#", StringComparison.Ordinal)),
            $"resource:a2#parent@{Sub}",
            $"{GroupA}#reader@user:alice",
            $"{Sub}#reader@user:bob"
        ];

        var groupReader = await Evaluate(
            InMemoryRelationReader.Parse(parked),
            InMemoryReverseRelationReader.Parse(parked),
            Alice,
            new() {
                ObjectType = ObjectTypes.Resource,
                Permission = Permissions.Read,
                Within = ObjectRef.Parse(GroupA).GetValueOrThrow(),
                WithinDepth = 1
            }
        );

        Ids(groupReader).ShouldBe(["a1"], "the parked resource left the group, so the group's reader no longer reaches it");

        var subscriptionReader = await List(SubjectRef.Of(ObjectTypes.User, "bob"), ObjectTypes.Resource, Permissions.Read, parked);

        Ids(subscriptionReader).ShouldBe(["a1", "a2", "b1", "b2"], "a subscription reader sees the parked resource as well");
    }

    // ── Verification ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANegatedPermissionIsVerifiedAndASuspendedResourceIsNotListed() {
        // purge is Rel(owner) & !Rel(suspended). The walk reaches a2 through owner; the exclusion
        // is what the forward re-check removes.
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Purge,
            [.. TwoGroups, $"{GroupA}#owner@user:alice", "resource:a2#suspended@user:alice"]
        );

        page.Verified.ShouldBeTrue("an exclusion is in reach, so every candidate is re-checked forward");
        Ids(page).ShouldBe(["a1"]);
    }

    [Fact]
    public async Task ATupleOnTheNegatedRelationReachesNothingOnItsOwn() {
        // Being suspended on a resource grants nothing: the node under the `!` never triggers.
        var page = await List(
            Alice,
            ObjectTypes.Resource,
            Permissions.Purge,
            [.. TwoGroups, "resource:a2#suspended@user:alice"]
        );

        page.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIntersectionIsOverApproximatedAndThenVerified() {
        var schema = Schema.DefineType("doc")
            .Relation("a")
            .Relation("b")
            .Permission("both", Rel("a") & Rel("b"))
            .DefineType("user")
            .Build();

        var tuples = new[] { "doc:one#a@user:alice", "doc:two#a@user:alice", "doc:two#b@user:alice" };

        var page = await Evaluate(
            schema,
            InMemoryRelationReader.Parse(tuples),
            InMemoryReverseRelationReader.Parse(tuples),
            Alice,
            new() { ObjectType = "doc", Permission = "both" }
        );

        page.Verified.ShouldBeTrue();
        Ids(page).ShouldBe(["two"]);
    }

    // ── The caps ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PastTheObjectCapTheWalkAnswersNothingAndSaysWhich() {
        var tuples = new List<string>(TwoGroups) { $"{Sub}#owner@user:alice" };

        var page = await Evaluate(
            InMemoryRelationReader.Parse([.. tuples]),
            InMemoryReverseRelationReader.Parse([.. tuples]),
            Alice,
            new() { ObjectType = ObjectTypes.Resource, Permission = Permissions.Read },
            new() { MaxListObjects = 3 }
        );

        page.Outcome.ShouldBe(ListObjectsOutcome.ObjectCapExceeded);
        page.Objects.ShouldBeEmpty("a capped walk hands back nothing rather than the part it found — ListObjectsOutcome");
        page.CapDetail.ShouldContain("more than 3 objects");
    }

    [Fact]
    public async Task ThirteenHopsIsPastTheDepthCapAndTwelveIsNot() {
        // The same numbers CheckEvaluatorTests pins for Check: a chain of twelve parent edges
        // resolves, thirteen does not, so the two evaluators agree on Allowed.
        // Every object on the chain is a resource alice reads; the leaf is the one at the far end.
        var page = await List(Alice, ObjectTypes.Resource, Permissions.Read, Chain(12));
        Ids(page).ShouldContain("leaf");
        page.Objects.Count.ShouldBe(13);
        page.DepthCapHit.ShouldBeFalse();

        page = await List(Alice, ObjectTypes.Resource, Permissions.Read, Chain(13));
        Ids(page).ShouldNotContain("leaf");
        page.Objects.Count.ShouldBe(13, "the twelve within the cap and the top, and not the leaf");
        page.Outcome.ShouldBe(ListObjectsOutcome.Complete, "past the depth cap is a deny Check would also make, not a cap on the answer");
        page.DepthCapHit.ShouldBeTrue();
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownPermissionIsARefusalAndNotAnEmptyPage() {
        var evaluator = new ListObjectsEvaluator(
            CyberCloudSchema.Instance,
            InMemoryRelationReader.Parse(TwoGroups),
            InMemoryReverseRelationReader.Parse(TwoGroups)
        );

        var result = await evaluator.EvaluateAsync(
            Alice,
            new() { ObjectType = ObjectTypes.Resource, Permission = "raed" },
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A parent chain <paramref name="hops" /> long, with alice a reader at the top.</summary>
    static string[] Chain(int hops) {
        List<string> tuples = [];
        var child = "resource:leaf";

        for (var i = 0; i < hops; i++) {
            var parent = $"resource:p{i}";
            tuples.Add($"{child}#parent@{parent}");
            child = parent;
        }

        tuples.Add($"{child}#reader@user:alice");
        return [.. tuples];
    }

    static Task<ListObjectsEvaluation> List(SubjectRef subject, string type, string permission, string[] tuples) =>
        Evaluate(
            InMemoryRelationReader.Parse(tuples),
            InMemoryReverseRelationReader.Parse(tuples),
            subject,
            new() { ObjectType = type, Permission = permission }
        );

    static Task<ListObjectsEvaluation> Evaluate(
        InMemoryRelationReader forward,
        InMemoryReverseRelationReader reverse,
        SubjectRef subject,
        ListObjectsRequest request,
        AuthorizationLimits? limits = null
    ) =>
        Evaluate(CyberCloudSchema.Instance, forward, reverse, subject, request, limits);

    static async Task<ListObjectsEvaluation> Evaluate(
        AuthorizationSchema schema,
        InMemoryRelationReader forward,
        InMemoryReverseRelationReader reverse,
        SubjectRef subject,
        ListObjectsRequest request,
        AuthorizationLimits? limits = null
    ) {
        var evaluator = new ListObjectsEvaluator(schema, forward, reverse, limits);
        var result = await evaluator.EvaluateAsync(subject, request, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.GetValueOrThrow();
    }

    static string[] Ids(ListObjectsEvaluation page) => [.. page.Objects.Select(x => x.Id)];
}
