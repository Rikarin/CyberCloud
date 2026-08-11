using System.Globalization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;
using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     The memo, the cycle behaviour, and <b>the two caps at and just past their documented
///     values</b>.
/// </summary>
public sealed class CheckEvaluatorTests
{
    /// <summary>A hierarchy schema — the shape everything in docs/plan/07 is written about.</summary>
    static readonly AuthorizationSchema Hierarchy = Schema.DefineType("doc")
        .Relation("parent")
        .Relation("owner", This | From("parent", "owner"))
        .Relation("suspended")
        .Permission("read", Rel("owner"))
        .Permission("act", Rel("owner") & !Rel("suspended"))
        .DefineType("group")
        .Relation("member")
        .Relation("owner", This)
        .DefineType("user")
        .Build();

    static SubjectRef Alice => SubjectRef.Of("user", "alice");

    // ── The memo and cycles ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADirectTupleGrants()
    {
        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice, "doc:one#owner@user:alice");

        result.Allowed.ShouldBeTrue();
        result.Outcome.ShouldBe(CheckOutcome.Allowed);
    }

    [Fact]
    public async Task ASelfLoopOnTheParentRelationTerminatesAndDenies()
    {
        // doc:one is its own parent. Without the memo this never returns.
        var result = await Evaluate(Hierarchy, "doc:one", "read", Alice, "doc:one#parent@doc:one");

        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.Denied, "a self-loop is a genuine deny, not a cap");
    }

    [Fact]
    public async Task ATwoNodeParentCycleTerminatesAndDenies()
    {
        var result = await Evaluate(
            Hierarchy, "doc:a", "read", Alice,
            "doc:a#parent@doc:b",
            "doc:b#parent@doc:a");

        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.Denied);
    }

    [Fact]
    public async Task ACycleWithAGrantSomewhereInItStillGrants()
    {
        // The in-progress false must break the loop WITHOUT hiding the real tuple.
        var result = await Evaluate(
            Hierarchy, "doc:a", "read", Alice,
            "doc:a#parent@doc:b",
            "doc:b#parent@doc:a",
            "doc:b#owner@user:alice");

        result.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task AGroupMembershipCycleTerminates()
    {
        // group:eng#member@group:ops#member and back. docs/plan/07 § The model's fourth example
        // shape, closed into a loop.
        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice,
            "doc:one#owner@group:eng#member",
            "group:eng#member@group:ops#member",
            "group:ops#member@group:eng#member");

        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.Denied);
    }

    [Fact]
    public async Task ADiamondReadsEachObjectOnce()
    {
        // Four documents in a diamond: without the memo the shared ancestor is walked twice, and
        // with 20 levels of that it is exponential. docs/plan/07 § Check names this as the reason
        // the memo exists.
        var reader = InMemoryRelationReader.Parse(
            "doc:top#parent@doc:left",
            "doc:top#parent@doc:right",
            "doc:left#parent@doc:root",
            "doc:right#parent@doc:root",
            "doc:root#owner@user:bob");

        var evaluator = new CheckEvaluator(Hierarchy, reader);
        var result = await evaluator.EvaluateAsync(
            ObjectRef.Of("doc", "top"), "read", Alice, TestContext.Current.CancellationToken);

        result.GetValueOrThrow().Allowed.ShouldBeFalse();
        reader.Reads.ShouldBe(4, "each of top/left/right/root is read exactly once per request");
    }

    [Fact]
    public async Task AnIntersectionAcrossACycleIsNotDefeatedByAMemoizedInProgressFalse()
    {
        // ⚠ THE CASE THAT MADE THE `Cyclic` FLAG NECESSARY, and it is not in docs/plan/07.
        //
        //   a = Rel(b) | This      b = Rel(a)      p = Rel(a) & Rel(b)
        //
        // ⚠ The operand ORDER of `a` is what makes this bite, which is why it is written this way
        // round: `Rel(b)` has to be tried BEFORE the `This` that actually grants. Evaluating `a`
        // descends into `b`, which loops back to `a` and takes the in-progress false, so `b`
        // concludes false; only then does `a`'s `This` succeed. If that false were memoized, `b`
        // would stay false for the rest of the request even though `a` is now true — and the
        // intersection would deny a subject who has both. Least-fixpoint semantics say allow, and
        // so does CheckPropertyTests, which found this shape on its own.
        var schema = Schema.DefineType("doc")
            .Relation("a", Rel("b") | This)
            .Relation("b", Rel("a"))
            .Permission("p", Rel("a") & Rel("b"))
            .Build();

        var result = await Evaluate(schema, "doc:one", "p", Alice, "doc:one#a@user:alice");

        result.Allowed.ShouldBeTrue(
            "an in-progress false is correct for the path it is on and must not be written down");
    }

    // ── ⚠ The depth cap, at 12 and at 13 ───────────────────────────────────────────────────────

    [Fact]
    public async Task AChainOfExactlyTwelveParentHopsResolves()
    {
        var result = await Evaluate(
            Hierarchy, "doc:h0", "read", Alice, [.. ParentChain(hops: 12, grantAtEnd: true)]);

        result.Allowed.ShouldBeTrue();
        result.Outcome.ShouldBe(CheckOutcome.Allowed);
        result.MaxDepthReached.ShouldBe(12, "twelve hops is exactly the documented cap");
    }

    [Fact]
    public async Task AChainOfThirteenParentHopsIsDeniedAndSaysWhy()
    {
        var before = AuthorizationMetrics.DepthCapExceeded;

        var result = await Evaluate(
            Hierarchy, "doc:h0", "read", Alice, [.. ParentChain(hops: 13, grantAtEnd: true)]);

        // ⚠ Fail-closed AND observable — the two halves of the answer to "what does a check that
        // hits a cap return". Denied, because a walk that ran out of budget must not allow. But not
        // *indistinguishable* from a genuine deny: a distinct outcome, a message, and a counter.
        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.DepthCapExceeded);
        result.CapDetail.ShouldContain("depth 13");
        result.CapDetail.ShouldContain("past the cap of 12");
        AuthorizationMetrics.DepthCapExceeded.ShouldBe(before + 1);
    }

    [Fact]
    public async Task ATruncatedResultIsNotCacheable()
    {
        var result = await Evaluate(
            Hierarchy, "doc:h0", "read", Alice, [.. ParentChain(hops: 13, grantAtEnd: true)]);

        result.IsCacheable.ShouldBeFalse(
            "caching 'I gave up' would make one unlucky walk permanent");
    }

    [Fact]
    public async Task AGrantFoundBeforeTheDepthCapStillShortCircuitsToAllowed()
    {
        // The cap only ever turns a would-be deny into a reported truncation. A subject that has
        // access three hops up is allowed however long the chain behind them is.
        List<string> tuples = [.. ParentChain(hops: 30, grantAtEnd: false), "doc:h3#owner@user:alice"];

        var result = await Evaluate(Hierarchy, "doc:h0", "read", Alice, [.. tuples]);

        result.Allowed.ShouldBeTrue();
        result.Outcome.ShouldBe(CheckOutcome.Allowed);
    }

    // ── ⚠ The breadth cap, at 1 000 and at 1 001 ───────────────────────────────────────────────

    [Fact]
    public async Task ExactlyOneThousandUsersetsToExpandIsWithinTheCap()
    {
        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice, [.. UsersetFanOut(1_000)]);

        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.Denied, "1 000 is the cap, not past it");
    }

    [Fact]
    public async Task OneThousandAndOneUsersetsToExpandIsPastTheCap()
    {
        var before = AuthorizationMetrics.BreadthCapExceeded;

        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice, [.. UsersetFanOut(1_001)]);

        result.Allowed.ShouldBeFalse();
        result.Outcome.ShouldBe(CheckOutcome.BreadthCapExceeded);
        result.CapDetail.ShouldContain("more than 1000");
        AuthorizationMetrics.BreadthCapExceeded.ShouldBe(before + 1);
    }

    [Fact]
    public async Task ExactlyOneThousandTuplesetTargetsIsWithinTheCap()
    {
        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice, [.. ParentFanOut(1_000)]);

        result.Outcome.ShouldBe(CheckOutcome.Denied);
    }

    [Fact]
    public async Task OneThousandAndOneTuplesetTargetsIsPastTheCap()
    {
        var result = await Evaluate(
            Hierarchy, "doc:one", "read", Alice, [.. ParentFanOut(1_001)]);

        result.Outcome.ShouldBe(CheckOutcome.BreadthCapExceeded);
        result.CapDetail.ShouldContain("points at more than 1000 objects");
    }

    [Fact]
    public async Task AMatchFoundWithinTheCapShortCircuitsBeforeTheCapIsReached()
    {
        // ⚠ The walk stops AFTER the cap, not at it — see AuthorizationLimits. A subject sitting in
        // position 3 of 5 000 is allowed.
        List<string> tuples = [.. UsersetFanOut(5_000), "group:g3#member@user:alice"];

        var result = await Evaluate(Hierarchy, "doc:one", "read", Alice, [.. tuples]);

        result.Allowed.ShouldBeTrue();
        result.Outcome.ShouldBe(CheckOutcome.Allowed);
    }

    [Fact]
    public async Task ManyDirectConcreteSubjectsAreNotChargedAgainstTheBreadthCap()
    {
        // A concrete match is a set test over tuples already read; it costs no grain call and no
        // recursion. Charging it would deny `read` on an object with 1 001 direct readers, which is
        // a cap doing harm rather than work.
        List<string> tuples =
        [
            .. Enumerable.Range(0, 3_000).Select(i =>
                "doc:one#owner@user:u" + i.ToString(CultureInfo.InvariantCulture)),
        ];

        var result = await Evaluate(
            Hierarchy, "doc:one", "read", SubjectRef.Of("user", "u2999"), [.. tuples]);

        result.Allowed.ShouldBeTrue();
        result.Outcome.ShouldBe(CheckOutcome.Allowed);
    }

    // ── ⚠ A cap must never GRANT through a negation ────────────────────────────────────────────

    [Fact]
    public async Task ACapInsideANegatedOperandDeniesRatherThanGranting()
    {
        // `act = Rel("owner") & !Rel("suspended")`. `suspended` is direct-only, but a tuple on it
        // may name a USERSET — and walking that userset can hit a cap. A truncated operand
        // evaluates to false, and `!false` is true, so the naive reading GRANTS on a walk that ran
        // out of budget. The truncation is propagated instead.
        List<string> tuples =
        [
            "doc:one#owner@user:alice",
            .. Enumerable.Range(0, 1_001).Select(i =>
                "doc:one#suspended@group:s" + i.ToString(CultureInfo.InvariantCulture) + "#member"),
        ];

        var result = await Evaluate(Hierarchy, "doc:one", "act", Alice, [.. tuples]);

        result.Allowed.ShouldBeFalse("a cap must never turn into a grant through a negation");
        result.Outcome.ShouldBe(CheckOutcome.BreadthCapExceeded);
    }

    // ── Negation, evaluated ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADenyAssignmentRemovesAccessThatTheRoleGrants()
    {
        // docs/plan/07 § Azure RBAC, row 4 — the whole reason `!` exists in this engine, and the
        // reason the cache had to be thought about: adding a tuple REMOVED access.
        var granted = await Evaluate(Hierarchy, "doc:one", "act", Alice, "doc:one#owner@user:alice");
        granted.Allowed.ShouldBeTrue();

        var denied = await Evaluate(
            Hierarchy, "doc:one", "act", Alice,
            "doc:one#owner@user:alice",
            "doc:one#suspended@user:alice");

        denied.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ASuspensionAtTheParentDoesNotLeakDownBecauseSuspendedIsDirectOnly()
    {
        // The restriction's observable consequence, stated so it is a decision and not a surprise:
        // `suspended` is not inherited, so suspending a parent does not suspend a child.
        var result = await Evaluate(
            Hierarchy, "doc:child", "act", Alice,
            "doc:child#parent@doc:parent",
            "doc:parent#owner@user:alice",
            "doc:parent#suspended@user:alice");

        result.Allowed.ShouldBeTrue(
            "docs/plan/07 confines negation to the same object; inheriting a deny would need a "
            + "second consistency mechanism, which § Caching across requests refuses");
    }

    // ── Unknown names ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnknownPermissionIsAFailureAndNotADenial()
    {
        var evaluator = new CheckEvaluator(Hierarchy, InMemoryRelationReader.Parse());

        var result = await evaluator.EvaluateAsync(
            ObjectRef.Of("doc", "one"), "reed", Alice, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
        result.Error.Message.ShouldContain("allow-nothing");
    }

    [Fact]
    public async Task AnUnknownObjectTypeIsAFailureAndNotADenial()
    {
        var evaluator = new CheckEvaluator(Hierarchy, InMemoryRelationReader.Parse());

        var result = await evaluator.EvaluateAsync(
            ObjectRef.Of("widget", "one"), "read", Alice, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.SchemaInvalid);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    static async Task<CheckEvaluation> Evaluate(
        AuthorizationSchema schema,
        string @object,
        string permission,
        SubjectRef subject,
        params string[] tuples)
    {
        var evaluator = new CheckEvaluator(schema, new InMemoryRelationReader(
            tuples.Select(x => RelationTuple.Parse(x).GetValueOrThrow())));

        var result = await evaluator.EvaluateAsync(
            ObjectRef.Parse(@object).GetValueOrThrow(),
            permission,
            subject,
            TestContext.Current.CancellationToken);

        return result.GetValueOrThrow();
    }

    /// <summary><c>doc:h0 → doc:h1 → … → doc:hN</c>, optionally granting at the far end.</summary>
    static IEnumerable<string> ParentChain(int hops, bool grantAtEnd)
    {
        for (var i = 0; i < hops; i++)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"doc:h{i}#parent@doc:h{i + 1}");
        }

        if (grantAtEnd)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"doc:h{hops}#owner@user:alice");
        }
    }

    /// <summary><paramref name="count" /> userset subjects on one relation of one object.</summary>
    static IEnumerable<string> UsersetFanOut(int count) =>
        Enumerable.Range(0, count).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"doc:one#owner@group:g{i}#member"));

    /// <summary><paramref name="count" /> tupleset targets on one object.</summary>
    static IEnumerable<string> ParentFanOut(int count) =>
        Enumerable.Range(0, count).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"doc:one#parent@doc:p{i}"));
}
