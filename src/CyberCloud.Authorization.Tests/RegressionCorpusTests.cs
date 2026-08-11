using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Generated;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     Runs <see cref="RegressionCorpus" /> against the evaluator in memory <b>and</b> against the
///     reference evaluator — the fast layer, per case.
/// </summary>
/// <remarks>
///     Running each case against the reference evaluator as well is not redundancy. A corpus entry
///     is written by hand, so its expectations can be wrong; agreeing with an independent evaluator
///     over the same tuple set catches an entry that encodes somebody's misunderstanding rather than
///     the system's behaviour.
/// </remarks>
public sealed class RegressionCorpusTests
{
    /// <summary>Every case, as xUnit theory data.</summary>
    public static TheoryData<string> CaseNames =>
        [.. RegressionCorpus.Cases.Select(x => x.Name)];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task TheCorpusHoldsAgainstTheEvaluator(string name)
    {
        var corpusCase = Find(name);
        var reader = new InMemoryRelationReader(corpusCase.Parsed());

        foreach (var expectation in corpusCase.Expectations)
        {
            var evaluator = new CheckEvaluator(CyberCloudSchema.Instance, reader);

            var result = await evaluator.EvaluateAsync(
                ObjectRef.Parse(expectation.Object).GetValueOrThrow(),
                expectation.Permission,
                SubjectRef.Parse(expectation.Subject).GetValueOrThrow(),
                TestContext.Current.CancellationToken);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);

            result.GetValueOrThrow().Allowed.ShouldBe(
                expectation.Expected,
                $"{corpusCase.Name}: {expectation.Object}#{expectation.Permission}"
                + $"@{expectation.Subject}." + Environment.NewLine + corpusCase.Why);
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TheCorpusAgreesWithTheReferenceEvaluatorToo(string name)
    {
        var corpusCase = Find(name);
        var tuples = corpusCase.Parsed();

        foreach (var expectation in corpusCase.Expectations)
        {
            ReferenceEvaluator.Evaluate(
                    CyberCloudSchema.Instance,
                    tuples,
                    ObjectRef.Parse(expectation.Object).GetValueOrThrow(),
                    expectation.Permission,
                    SubjectRef.Parse(expectation.Subject).GetValueOrThrow())
                .ShouldBe(
                    expectation.Expected,
                    $"{corpusCase.Name} disagrees with the reference evaluator — either the case's "
                    + "expectation is wrong, or both evaluators are.");
        }
    }

    [Fact]
    public void EveryCaseIsWellFormed()
    {
        // A corpus entry that does not parse, has no expectations, or names a permission the shipped
        // schema does not define is an entry that silently tests nothing.
        RegressionCorpus.Cases.ShouldNotBeEmpty();

        RegressionCorpus.Cases.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(RegressionCorpus.Cases.Count, "corpus names are cited; they must be unique");

        foreach (var corpusCase in RegressionCorpus.Cases)
        {
            corpusCase.Why.Length.ShouldBeGreaterThan(
                60, $"{corpusCase.Name} does not say what went wrong");

            corpusCase.Expectations.ShouldNotBeEmpty(corpusCase.Name);

            foreach (var tuple in corpusCase.Tuples)
            {
                RelationTuple.Parse(tuple).IsSuccess.ShouldBeTrue($"{corpusCase.Name}: '{tuple}'");
            }

            foreach (var expectation in corpusCase.Expectations)
            {
                var target = ObjectRef.Parse(expectation.Object);
                target.IsSuccess.ShouldBeTrue($"{corpusCase.Name}: '{expectation.Object}'");

                SubjectRef.Parse(expectation.Subject).IsSuccess.ShouldBeTrue(
                    $"{corpusCase.Name}: '{expectation.Subject}'");

                CyberCloudSchema.Instance
                    .Member(target.GetValueOrThrow().Type, expectation.Permission)
                    .ShouldNotBeNull(
                        $"{corpusCase.Name} names '{expectation.Permission}' on "
                        + $"'{target.GetValueOrThrow().Type}', which the shipped schema does not "
                        + "define");
            }
        }
    }

    [Fact]
    public void EveryCaseHasAtLeastOneAllowAndTheCorpusAsAWholeHasBoth()
    {
        // A corpus of nothing but denials would pass against an engine that denies everything —
        // which is exactly the state a badly-handled outage leaves the platform in.
        var allows = RegressionCorpus.Cases.SelectMany(x => x.Expectations).Count(x => x.Expected);
        var denies = RegressionCorpus.Cases.SelectMany(x => x.Expectations).Count(x => !x.Expected);

        allows.ShouldBeGreaterThan(0);
        denies.ShouldBeGreaterThan(0);
    }

    static CorpusCase Find(string name) =>
        RegressionCorpus.Cases.Single(x => string.Equals(x.Name, name, StringComparison.Ordinal));
}

/// <summary>
///     Runs <see cref="RegressionCorpus" /> against the <b>real grains</b> — one silo, real Redis,
///     real PostgreSQL.
/// </summary>
/// <remarks>
///     ⚠ <b>The second layer is the point of the corpus being a data structure rather than a pile of
///     test methods.</b> The in-memory run tests the evaluator; this one tests everything between a
///     tuple write and a check answer — the two-grain write, the durable round trip, the tenant
///     qualification, the cache. A case added to <see cref="RegressionCorpus.Cases" /> gets both
///     with no further work, which is the property that makes the corpus the asset docs/plan/07
///     § Testing says it is.
/// </remarks>
[Collection(AuthorizationSuite.Name)]
public sealed class RegressionCorpusClusterTests(AuthorizationCluster cluster)
{
    /// <summary>Every case, as xUnit theory data.</summary>
    public static TheoryData<string> CaseNames =>
        [.. RegressionCorpus.Cases.Select(x => x.Name)];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task TheCorpusHoldsAgainstRealGrains(string name)
    {
        var corpusCase = RegressionCorpus.Cases.Single(x =>
            string.Equals(x.Name, name, StringComparison.Ordinal));

        // Each case gets its own tenant, derived from its position, so cases cannot see each
        // other's tuples and the suite has no ordering dependency.
        var tenant = AuthorizationCluster.Tenant(
            7000 + RegressionCorpus.Cases.ToList().FindIndex(x =>
                string.Equals(x.Name, name, StringComparison.Ordinal)));

        foreach (var tuple in corpusCase.Tuples)
        {
            await cluster.WriteAsync(tenant, tuple);
        }

        foreach (var expectation in corpusCase.Expectations)
        {
            var result = await cluster
                .Check(tenant, ObjectRef.Parse(expectation.Object).GetValueOrThrow())
                .CheckAsync(
                    expectation.Permission,
                    SubjectRef.Parse(expectation.Subject).GetValueOrThrow(),
                    Consistency.FullyConsistent);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);

            result.GetValueOrThrow().Allowed.ShouldBe(
                expectation.Expected,
                $"{corpusCase.Name}: {expectation.Object}#{expectation.Permission}"
                + $"@{expectation.Subject}." + Environment.NewLine + corpusCase.Why);
        }
    }
}
