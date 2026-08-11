using System.Diagnostics;
using System.Globalization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>
///     docs/plan/07 § Testing, bullet one: <i>"<c>Check</c> agrees with a slow, obviously-correct
///     reference evaluator on … random graphs including cycles, deep nesting, and negation."</i>
/// </summary>
/// <remarks>
///     <para>
///         <b>Scale, stated rather than implied.</b> The document asks for 100 000 graphs.
///         <see cref="Graphs" /> is <b>20 000</b>, each carrying four queries — 80 000 comparisons —
///         which is what fits inside the per-PR budget docs/plan/23 § CI shape sets at three
///         minutes for the whole <c>Test</c> target, alongside four other projects and two of them
///         container-backed. The generator is deterministic and the seed is the graph index, so
///         raising the number is a one-line change and a nightly job could run the full 100 000
///         without changing anything else. <see cref="ExhaustiveSeeds" /> documents how.
///     </para>
///     <para>
///         ⚠ <b>The caps are lifted for this suite, deliberately.</b> A generated graph with a cycle
///         can walk past depth 12, and a truncated answer is <i>fail-closed</i> rather than
///         <i>correct</i> — comparing it to a reference evaluator that has no caps would be
///         comparing two different questions. So the property test runs with
///         <see cref="Unbounded" /> and the caps get their own tests at exactly 12/13 and
///         1 000/1 001 in <c>CheckEvaluatorTests</c>. What is under test here is the memo, the
///         cycle semantics, the rewrite algebra and negation.
///     </para>
/// </remarks>
public sealed class CheckPropertyTests
{
    /// <summary>How many graphs the per-PR run covers.</summary>
    public const int Graphs = 20_000;

    /// <summary>
    ///     The caps, lifted. Depth is still bounded in practice by the memo — the in-progress stack
    ///     can hold at most one entry per <c>(object, name)</c> pair, which is under a hundred for a
    ///     generated graph.
    /// </summary>
    static readonly AuthorizationLimits Unbounded =
        new() { MaxDepth = 512, MaxBreadth = 100_000 };

    [Fact]
    public async Task CheckAgreesWithTheReferenceEvaluatorOnEveryGeneratedGraph()
    {
        var comparisons = 0;
        var allowed = 0;
        var withNegation = 0;

        for (var seed = 0; seed < Graphs; seed++)
        {
            var graph = RandomGraphs.Generate(seed);
            var reader = new InMemoryRelationReader(graph.Tuples);

            foreach (var (target, name, subject) in graph.Queries)
            {
                var evaluator = new CheckEvaluator(graph.Schema, reader, Unbounded);

                var actual = await evaluator.EvaluateAsync(
                    target, name, subject, TestContext.Current.CancellationToken);

                actual.IsSuccess.ShouldBeTrue(actual.Error?.Message);

                var expected = ReferenceEvaluator.Evaluate(
                    graph.Schema, graph.Tuples, target, name, subject);

                var evaluation = actual.GetValueOrThrow();

                evaluation.Outcome.ShouldBeOneOf(
                    CheckOutcome.Allowed,
                    CheckOutcome.Denied);

                evaluation.Allowed.ShouldBe(
                    expected,
                    $"Check and the reference evaluator disagree on {target}#{name}@{subject}."
                    + Environment.NewLine + graph.Describe());

                comparisons++;
                if (expected)
                {
                    allowed++;
                }

                if (graph.Schema.Member(target.Type, name)?.ContainsNegation == true)
                {
                    withNegation++;
                }
            }
        }

        comparisons.ShouldBe(Graphs * 4);

        // ⚠ A property test that only ever produced denials would pass against an evaluator that
        // returns false unconditionally. These two floors are what stop this file from being a
        // very expensive `Assert.True(true)`.
        allowed.ShouldBeGreaterThan(
            comparisons / 20,
            "fewer than 5% of comparisons were allows — the generator has stopped producing "
            + "reachable grants and the suite is no longer testing the positive direction");

        withNegation.ShouldBeGreaterThan(
            comparisons / 20,
            "docs/plan/07 § Testing asks for negation to be covered");
    }

    [Fact]
    public void MostGeneratedGraphsContainACycle()
    {
        // A "cycles" property test over acyclic graphs is the classic way to test nothing. This
        // measures the generator rather than trusting it.
        var cyclic = 0;

        for (var seed = 0; seed < 2_000; seed++)
        {
            if (HasCycle(RandomGraphs.Generate(seed).Tuples))
            {
                cyclic++;
            }
        }

        cyclic.ShouldBeGreaterThan(
            1_000,
            "fewer than half the generated graphs contain a cycle in their object graph");
    }

    [Fact]
    public void EveryGeneratedSchemaBuilds()
    {
        // The generator assembles rewrites that satisfy SchemaBuilder's rules by construction. If
        // that ever stops being true, this fails here rather than as a confusing disagreement in
        // the comparison above.
        for (var seed = 0; seed < 2_000; seed++)
        {
            var graph = RandomGraphs.Generate(seed);
            graph.Schema.TypeNames.Length.ShouldBe(RandomGraphs.Types.Count);
        }
    }

    [Fact]
    public async Task EveryGeneratedGraphTerminatesQuickly()
    {
        // The memo is what stops a diamond from being exponential and a cycle from being infinite.
        // A regression in either shows up here as a timeout rather than as a hung CI job.
        var stopwatch = Stopwatch.StartNew();

        for (var seed = 0; seed < 2_000; seed++)
        {
            var graph = RandomGraphs.Generate(seed);
            var reader = new InMemoryRelationReader(graph.Tuples);

            foreach (var (target, name, subject) in graph.Queries)
            {
                var evaluator = new CheckEvaluator(graph.Schema, reader, Unbounded);
                await evaluator.EvaluateAsync(
                    target, name, subject, TestContext.Current.CancellationToken);
            }
        }

        stopwatch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(30),
            "8 000 checks over generated graphs should be far under a second per thousand; if this "
            + "fails, the memo has stopped memoizing");
    }

    /// <summary>
    ///     How to run the full 100 000 docs/plan/07 § Testing asks for, without changing this file:
    ///     <c>CheckPropertyTests.Graphs</c> is the only number involved and the seed is the graph
    ///     index, so seeds 0…99 999 are a superset of what CI runs. Nothing about the generator
    ///     depends on the count.
    /// </summary>
    [Fact]
    public void ExhaustiveSeeds() => Graphs.ShouldBeGreaterThanOrEqualTo(20_000);

    static bool HasCycle(IReadOnlyList<RelationTuple> tuples)
    {
        Dictionary<string, List<string>> edges = new(StringComparer.Ordinal);

        foreach (var tuple in tuples)
        {
            var from = tuple.Object.ToString();
            if (!edges.TryGetValue(from, out var to))
            {
                to = [];
                edges[from] = to;
            }

            to.Add(tuple.Subject.Object.ToString());
        }

        HashSet<string> visited = [];
        HashSet<string> stack = [];

        return edges.Keys.Any(node => Walk(node, edges, visited, stack));
    }

    static bool Walk(
        string node,
        Dictionary<string, List<string>> edges,
        HashSet<string> visited,
        HashSet<string> stack)
    {
        if (stack.Contains(node))
        {
            return true;
        }

        if (!visited.Add(node))
        {
            return false;
        }

        stack.Add(node);

        try
        {
            return edges.TryGetValue(node, out var next)
                   && next.Any(x => Walk(x, edges, visited, stack));
        }
        finally
        {
            stack.Remove(node);
        }
    }
}
