using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>
///     docs/plan/07 § Testing, bullet two, read for the reverse direction: the walk's answer equals a
///     brute force that asks the reference evaluator about every object in the graph.
/// </summary>
/// <remarks>
///     <para>
///         <b>The brute force is the definition.</b> "Which objects of type T may S <c>name</c>" is,
///         by definition, every object of type T for which <c>Check(o, name, S)</c> holds; the
///         reference evaluator answers that with no memo, no caps and no shared code, so a walk that
///         agrees with it agrees for reasons that are not a common bug. Two thousand graphs, two
///         subjects, every <c>(type, name)</c> pair — 108 000 listings, each compared as a set —
///         which fits the per-PR budget beside <see cref="CheckPropertyTests" />' 80 000
///         comparisons: measured at 18 seconds for 3 000 graphs on the unscoped test and 25 on the
///         scoped one, so 2 000 keeps the pair under half a minute.
///     </para>
///     <para>
///         ⚠ <b>The generated schemas have every node kind, so this is also the test of
///         verification.</b> A relation rewrite can be an intersection and a permission can carry
///         a top-level exclusion, so a good share of walks over-approximate and re-check; the
///         counters at the end are what stop that share from silently becoming zero.
///     </para>
///     <para>
///         ⚠ <b>Scoping is held to a weaker property, and the weakening is stated exactly.</b> A
///         scoped answer must always be a subset of the brute force restricted to the scope — a
///         listing may hide, never show — and must equal it whenever the graph satisfies the two
///         assumptions <c>ListObjectsRequest</c> records: every object has at most one tupleset
///         parent, and no userset is formed on an object at the requested depth. Generated graphs
///         violate both often, which is what makes the subset half worth asserting on its own.
///     </para>
/// </remarks>
public sealed class ListObjectsPropertyTests {
    /// <summary>How many graphs the per-PR run covers.</summary>
    public const int Graphs = 2_000;

    static readonly AuthorizationLimits Unbounded =
        new() { MaxDepth = 512, MaxBreadth = 100_000, MaxListObjects = 100_000 };

    static IEnumerable<SubjectRef> Subjects => [SubjectRef.Of("ta", "u0"), SubjectRef.Of("tb", "u1")];

    static IEnumerable<string> Names => [
        .. RandomGraphs.DirectRelations, .. RandomGraphs.ComputedRelations, .. RandomGraphs.PermissionNames
    ];

    [Fact]
    public async Task TheWalkAgreesWithABruteForceOverEveryObjectOnEveryGeneratedGraph() {
        var comparisons = 0;
        var nonEmpty = 0;
        var verified = 0;

        for (var seed = 0; seed < Graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var forward = new InMemoryRelationReader(graph.Tuples);
            var reverse = new InMemoryReverseRelationReader(graph.Tuples);
            var objects = Universe(graph);

            foreach (var subject in Subjects) {
                foreach (var type in RandomGraphs.Types) {
                    foreach (var name in Names) {
                        var expected = objects
                            .Where(o => o.Type == type)
                            .Where(o => ReferenceEvaluator.Evaluate(graph.Schema, graph.Tuples, o, name, subject))
                            .Select(o => o.Id)
                            .Order(StringComparer.Ordinal)
                            .ToArray();

                        var evaluator = new ListObjectsEvaluator(graph.Schema, forward, reverse, Unbounded);

                        var actual = await evaluator.EvaluateAsync(
                            subject,
                            new() { ObjectType = type, Permission = name },
                            TestContext.Current.CancellationToken
                        );

                        actual.IsSuccess.ShouldBeTrue(actual.Error?.Message);

                        var evaluation = actual.GetValueOrThrow();
                        evaluation.Outcome.ShouldBe(ListObjectsOutcome.Complete);

                        evaluation.Objects.Select(x => x.Id)
                            .ToArray()
                            .ShouldBe(
                                expected,
                                $"ListObjects and the brute force disagree on {type}#{name}@{subject}."
                                + Environment.NewLine
                                + graph.Describe()
                            );

                        comparisons++;
                        if (expected.Length > 0) {
                            nonEmpty++;
                        }

                        if (evaluation.Verified) {
                            verified++;
                        }
                    }
                }
            }
        }

        comparisons.ShouldBe(Graphs * 2 * RandomGraphs.Types.Count * Names.Count());

        // ⚠ The two floors that keep this from being an expensive `Assert.True(true)`: the walk has
        // to find things, and it has to have been made to verify.
        nonEmpty.ShouldBeGreaterThan(
            comparisons / 20,
            "fewer than 5% of listings were non-empty — the generator has stopped producing reachable grants"
        );

        verified.ShouldBeGreaterThan(
            comparisons / 20,
            "fewer than 5% of walks crossed an intersection or exclusion, so verification is untested"
        );
    }

    [Fact]
    public async Task AScopedWalkNeverShowsMoreThanTheBruteForceAndShowsExactlyItWhenTheChainAssumptionsHold() {
        var comparisons = 0;
        var exact = 0;
        var nonEmpty = 0;

        for (var seed = 0; seed < Graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var forward = new InMemoryRelationReader(graph.Tuples);
            var reverse = new InMemoryReverseRelationReader(graph.Tuples);
            var objects = Universe(graph);
            var parents = Parents(graph, objects);
            var isChain = parents.Values.All(x => x.Count <= 1);

            // One scope per graph — the object with the most descendants, so scoping has something
            // to do — and the two depths that matter: one level, and every level.
            var scope = objects
                .OrderByDescending(o => Descendants(parents, o).Count)
                .ThenBy(o => o.ToString(), StringComparer.Ordinal)
                .First();

            var descendants = Descendants(parents, scope);
            var ancestors = Ancestors(parents, scope);

            foreach (var withinDepth in new int?[] { 1, null }) {
                var inScope = descendants
                    .Where(x => withinDepth is null || x.Value <= withinDepth)
                    .Select(x => x.Key)
                    .ToHashSet();

                // The second assumption: every userset any tuple names is formed on an object the
                // scoped walk still expands — the scope's ancestors, or a descendant above the
                // requested depth. A userset on anything else may carry a grant into the scope
                // through an object the pruned walk never reached.
                var usersetOffTheWalk = graph.Tuples.Any(t => t.Subject.IsUserset
                    && !ancestors.Contains(t.Subject.Object)
                    && !(descendants.TryGetValue(t.Subject.Object, out var d) && (withinDepth is null || d < withinDepth))
                );

                foreach (var subject in Subjects) {
                    foreach (var type in RandomGraphs.Types) {
                        foreach (var name in Names) {
                            var expected = inScope
                                .Where(o => o.Type == type)
                                .Where(o => ReferenceEvaluator.Evaluate(graph.Schema, graph.Tuples, o, name, subject))
                                .Select(o => o.Id)
                                .Order(StringComparer.Ordinal)
                                .ToArray();

                            var evaluator = new ListObjectsEvaluator(graph.Schema, forward, reverse, Unbounded);

                            var actual = await evaluator.EvaluateAsync(
                                subject,
                                new() {
                                    ObjectType = type, Permission = name, Within = scope, WithinDepth = withinDepth
                                },
                                TestContext.Current.CancellationToken
                            );

                            actual.IsSuccess.ShouldBeTrue(actual.Error?.Message);

                            var ids = actual.GetValueOrThrow().Objects.Select(x => x.Id).ToArray();
                            var why = $"scoped to {scope} at depth {withinDepth?.ToString() ?? "∞"}: "
                                + $"{type}#{name}@{subject}."
                                + Environment.NewLine
                                + graph.Describe();

                            ids.ShouldBeSubsetOf(expected, "a scoped listing showed an object it may not: " + why);

                            if (isChain && !usersetOffTheWalk) {
                                ids.ShouldBe(expected, "the chain assumptions hold and the scoped listing is short: " + why);
                                exact++;
                            }

                            comparisons++;
                            if (expected.Length > 0) {
                                nonEmpty++;
                            }
                        }
                    }
                }
            }
        }

        nonEmpty.ShouldBeGreaterThan(comparisons / 50, "scoped listings were almost all empty — the scope choice is not exercising the walk");
        exact.ShouldBeGreaterThan(comparisons / 20, "too few graphs satisfied the chain assumptions for the exactness half to mean anything");
    }

    /// <summary>Every object a tuple mentions, as object or as subject, plus the subjects' objects.</summary>
    static List<ObjectRef> Universe(GeneratedGraph graph) =>
        graph.Tuples.Select(t => t.Object)
            .Concat(graph.Tuples.Select(t => t.Subject.Object))
            .Concat(Subjects.Select(s => s.Object))
            .Distinct()
            .ToList();

    /// <summary>
    ///     Each object's tupleset parents — the subjects of its tuples on any relation a
    ///     <c>From</c> on <i>its own type</i> names. A <c>da</c> tuple on a type whose rewrites never
    ///     say <c>From("da", …)</c> is a plain relation, not a hop in the hierarchy.
    /// </summary>
    static Dictionary<ObjectRef, List<ObjectRef>> Parents(GeneratedGraph graph, List<ObjectRef> objects) {
        var tuplesetsByType = graph.Schema.TypeNames.ToDictionary(
            t => t,
            t => graph.Schema.Type(t)!
                .Members
                .SelectMany(m => m.Expression.DescendantsAndSelf())
                .OfType<TuplesetExpression>()
                .Select(x => x.Tupleset)
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal
        );

        return objects.ToDictionary(
            o => o,
            o => graph.Tuples
                .Where(t => t.Object == o && tuplesetsByType[o.Type].Contains(t.Relation))
                .Select(t => t.Subject.Object)
                .Distinct()
                .ToList()
        );
    }

    /// <summary>Every object above <paramref name="scope" />, and the scope itself.</summary>
    static HashSet<ObjectRef> Ancestors(Dictionary<ObjectRef, List<ObjectRef>> parents, ObjectRef scope) {
        HashSet<ObjectRef> seen = [scope];
        Queue<ObjectRef> pending = new();
        pending.Enqueue(scope);

        while (pending.Count > 0) {
            var current = pending.Dequeue();
            foreach (var parent in parents.TryGetValue(current, out var found) ? found : []) {
                if (seen.Add(parent)) {
                    pending.Enqueue(parent);
                }
            }
        }

        return seen;
    }

    /// <summary>Every object at or below <paramref name="scope" />, with its shortest depth.</summary>
    static Dictionary<ObjectRef, int> Descendants(Dictionary<ObjectRef, List<ObjectRef>> parents, ObjectRef scope) {
        Dictionary<ObjectRef, int> depths = new() { [scope] = 0 };
        Queue<ObjectRef> pending = new();
        pending.Enqueue(scope);

        while (pending.Count > 0) {
            var current = pending.Dequeue();
            foreach (var (child, itsParents) in parents) {
                if (itsParents.Contains(current) && !depths.ContainsKey(child)) {
                    depths[child] = depths[current] + 1;
                    pending.Enqueue(child);
                }
            }
        }

        return depths;
    }
}
