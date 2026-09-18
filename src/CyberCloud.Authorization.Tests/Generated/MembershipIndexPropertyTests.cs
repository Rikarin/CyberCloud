using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>
///     docs/plan/07 § Testing, "index equivalence", held on the index itself: after every tuple
///     write and delete the Leopard index equals the closure recomputed from scratch.
/// </summary>
/// <remarks>
///     <para>
///         <b>The brute force is the definition.</b> The index claims to hold, per userset, every
///         subject a chain of direct-only tuples reaches, and per subject, every userset such a
///         chain reaches it from. Both are a breadth-first search over the current tuple set, written
///         here with no memo, no incremental step and no shared code with
///         <see cref="MembershipIndexMaintainer" />. A maintainer that agrees with it after every
///         mutation agrees for reasons that are not a common bug.
///     </para>
///     <para>
///         ⚠ <b>The mutations are the store's, in the store's order.</b> A write reaches the index
///         after the tuple is in the forward reader; a delete reaches it before the tuple leaves,
///         because <c>TupleStoreGrain</c> lands the index first on a delete and the maintainer is
///         what hides the tuple. Every mutation is also applied a second time now and then, which
///         is the sweeper's replay, and the closure must not move.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every slice is unwritten until the first mutation reaches it, and that first
///             mutation is a rebuild over the tuples present.
///         </b> The store refuses to apply a union
///         to an unwritten slice for the reason <c>MembershipIndexGrain</c> does — the tuples may
///         predate the index — so a share of what this test holds to the brute force is the
///         rebuild path, and the rest is the unions on slices the rebuild stamped. The counter at
///         the end says both ran.
///     </para>
///     <para>
///         ⚠ <b>Deletes have to actually shrink something, or the recompute path is untested.</b>
///         The counters at the end demand that a share of deletes removed a member from some
///         closure — a generator whose deletes only ever touched leaves would pass the equality and
///         say nothing about recompute-on-delete.
///     </para>
/// </remarks>
public sealed class MembershipIndexPropertyTests {
    /// <summary>How many graphs the per-PR run covers.</summary>
    public const int Graphs = 2_000;

    [Fact]
    public async Task TheIndexEqualsTheRecomputedClosureAfterEveryWriteAndDelete() {
        var steps = 0;
        var shrinkingDeletes = 0;
        var nestedGraphs = 0;
        var rebuilds = 0;
        var unions = 0;

        for (var seed = 0; seed < Graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var random = new Random(seed ^ 0x5eed);
            var store = new InMemoryMembershipIndexStore();
            List<RelationTuple> present = [];
            var remaining = graph.Tuples.Distinct().ToList();

            if (graph.Tuples.Any(t => IsEdge(graph.Schema, t) && IsExpandable(graph.Schema, t.Subject))) {
                nestedGraphs++;
            }

            for (var step = 0; step < remaining.Count * 2 + 4; step++) {
                var write = present.Count == 0 || (remaining.Count > 0 && random.Next(10) < 6);

                if (write && remaining.Count > 0) {
                    var tuple = remaining[random.Next(remaining.Count)];
                    remaining.Remove(tuple);
                    present.Add(tuple);

                    await ApplyWriteAsync(graph.Schema, present, store, tuple);
                    if (random.Next(4) == 0) {
                        await ApplyWriteAsync(graph.Schema, present, store, tuple);
                    }
                } else if (present.Count > 0) {
                    var tuple = present[random.Next(present.Count)];
                    var before = Closure(graph.Schema, present);

                    await ApplyDeleteAsync(graph.Schema, present, store, tuple);
                    present.Remove(tuple);
                    remaining.Add(tuple);

                    if (random.Next(4) == 0) {
                        await ApplyDeleteAsync(graph.Schema, present, store, tuple);
                    }

                    if (Closure(graph.Schema, present).Members.Sum(static x => x.Value.Count)
                        < before.Members.Sum(static x => x.Value.Count)) {
                        shrinkingDeletes++;
                    }
                } else {
                    continue;
                }

                AssertIndexEquals(graph, store, present, step);
                steps++;
            }

            rebuilds += store.Rebuilds;
            unions += store.Unions;

            // ── Then everything goes, and the index must be empty ─────────────────────────────
            foreach (var tuple in present.ToList()) {
                await ApplyDeleteAsync(graph.Schema, present, store, tuple);
                present.Remove(tuple);
            }

            AssertIndexEquals(graph, store, present, -1);

            foreach (var subjectObject in store.Objects) {
                var slice = store.Snapshot(subjectObject);
                slice.Members.ShouldBeEmpty(
                    $"seed {seed}: {subjectObject} still has members after every tuple was deleted"
                );
                slice.Usersets.ShouldBeEmpty(
                    $"seed {seed}: {subjectObject} is still in a userset after every tuple was deleted"
                );
            }
        }

        steps.ShouldBeGreaterThan(Graphs * 10, "the mutation sequences are too short to mean anything");
        rebuilds.ShouldBeGreaterThan(
            Graphs,
            "fewer than one slice per graph was rebuilt on first touch — the backfill path is untested"
        );
        unions.ShouldBeGreaterThan(
            Graphs,
            "fewer than one union per graph landed on a written slice — the incremental path is untested"
        );
        nestedGraphs.ShouldBeGreaterThan(
            Graphs / 4,
            "fewer than a quarter of graphs nest one userset in another — the closure is barely transitive"
        );
        shrinkingDeletes.ShouldBeGreaterThan(
            Graphs,
            "fewer than one delete per graph removed a member — recompute-on-delete is untested"
        );
    }

    [Fact]
    public async Task ARebuiltSliceEqualsTheOneTheWritesProduced() {
        for (var seed = 0; seed < 500; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var tuples = graph.Tuples.Distinct().ToList();
            var reverse = new InMemoryReverseRelationReader(graph.Schema, tuples);
            var maintainer = new MembershipIndexMaintainer(
                graph.Schema,
                new InMemoryRelationReader(tuples),
                reverse,
                reverse.Store
            );

            foreach (var subjectObject in Universe(tuples)) {
                var rebuilt = await maintainer.RebuildAsync(subjectObject, TestContext.Current.CancellationToken);
                rebuilt.IsSuccess.ShouldBeTrue(rebuilt.Error?.Message);

                var change = rebuilt.GetValueOrThrow();
                change.Reset.ShouldBeTrue();

                var stored = reverse.Store.Snapshot(subjectObject);

                Sets(change.AddMembers).ShouldBe(
                    Sets(stored.Members),
                    $"seed {seed}: a rebuild of {subjectObject}'s members disagrees with the writes"
                );
                Sets(change.AddUsersets).ShouldBe(
                    Sets(stored.Usersets),
                    $"seed {seed}: a rebuild of {subjectObject}'s usersets disagrees with the writes"
                );
            }
        }
    }

    // ── The store's order, mirrored ───────────────────────────────────────────────────────────

    static async Task ApplyWriteAsync(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store,
        RelationTuple tuple
    ) {
        var applied = await Maintainer(schema, present, store).ApplyWriteAsync(
            tuple,
            TestContext.Current.CancellationToken
        );
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
    }

    static async Task ApplyDeleteAsync(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store,
        RelationTuple tuple
    ) {
        // ⚠ Called while the tuple is still in `present`: the store lands the index before the
        // forward half on a delete, and the maintainer hides the tuple itself.
        var applied = await Maintainer(schema, present, store).ApplyDeleteAsync(
            tuple,
            TestContext.Current.CancellationToken
        );
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
    }

    static MembershipIndexMaintainer Maintainer(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store
    ) {
        // The store rebuilds an unwritten slice through the maintainer of the mutation that
        // reached it — over `present`, which is the tuple set the grain's rebuild would read.
        MembershipIndexMaintainer maintainer =
            new(schema, new InMemoryRelationReader(present), new EntriesOnlyReader(present), store);
        store.Rebuild = maintainer.RebuildAsync;
        return maintainer;
    }

    // ── The brute force ───────────────────────────────────────────────────────────────────────

    static void AssertIndexEquals(
        GeneratedGraph graph,
        InMemoryMembershipIndexStore store,
        List<RelationTuple> present,
        int step
    ) {
        var expected = Closure(graph.Schema, present);
        var why =
            $"seed {graph.Seed}, step {step.ToString(CultureInfo.InvariantCulture)}, {present.Count} tuples present:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, present.Select(static x => "  " + x));

        foreach (var subjectObject in Universe(present).Concat(store.Objects).Distinct()) {
            var slice = store.Snapshot(subjectObject);

            var expectedMembers = expected.Members
                .Where(x => x.Key.Object == subjectObject && x.Value.Count > 0)
                .ToDictionary(static x => x.Key.Relation, static x => x.Value, StringComparer.Ordinal);

            var expectedUsersets = expected.Usersets
                .Where(x => x.Key.Object == subjectObject && x.Value.Count > 0)
                .ToDictionary(static x => x.Key.Relation, static x => x.Value, StringComparer.Ordinal);

            Sets(slice.Members).ShouldBe(
                Sets(expectedMembers),
                $"{subjectObject}'s members differ from the recomputed closure — {why}"
            );
            Sets(slice.Usersets).ShouldBe(
                Sets(expectedUsersets),
                $"{subjectObject}'s usersets differ from the recomputed closure — {why}"
            );
        }
    }

    /// <summary>Both closures over a tuple set, by search and nothing cleverer.</summary>
    static (Dictionary<SubjectRef,
        HashSet<SubjectRef>> Members, Dictionary<SubjectRef, HashSet<SubjectRef>> Usersets) Closure(
        AuthorizationSchema schema,
        List<RelationTuple> tuples
    ) {
        Dictionary<SubjectRef, HashSet<SubjectRef>> members = [];
        Dictionary<SubjectRef, HashSet<SubjectRef>> usersets = [];

        var edges = tuples.Where(t => IsEdge(schema, t)).ToList();
        var startingUsersets = edges.Select(static t => SubjectRef.Userset(t.Object.Type, t.Object.Id, t.Relation))
            .Distinct();

        foreach (var userset in startingUsersets) {
            HashSet<SubjectRef> reached = [];
            Queue<SubjectRef> pending = new();
            pending.Enqueue(userset);

            while (pending.Count > 0) {
                var current = pending.Dequeue();

                foreach (var edge in edges.Where(e => e.Object == current.Object && e.Relation == current.Relation)) {
                    if (reached.Add(edge.Subject) && IsExpandable(schema, edge.Subject)) {
                        pending.Enqueue(edge.Subject);
                    }
                }
            }

            members[userset] = reached;

            foreach (var member in reached) {
                if (!usersets.TryGetValue(member, out var containing)) {
                    containing = [];
                    usersets[member] = containing;
                }

                containing.Add(userset);
            }
        }

        return (members, usersets);
    }

    static bool IsEdge(AuthorizationSchema schema, RelationTuple tuple) =>
        schema.Member(tuple.Object.Type, tuple.Relation) is { IsPermission: false, IsDirectOnly: true };

    static bool IsExpandable(AuthorizationSchema schema, SubjectRef subject) =>
        subject.IsUserset
        && schema.Member(subject.Type, subject.Relation) is { IsPermission: false, IsDirectOnly: true };

    static IEnumerable<ObjectRef> Universe(IEnumerable<RelationTuple> tuples) =>
        tuples.Select(static t => t.Object).Concat(tuples.Select(static t => t.Subject.Object)).Distinct();

    /// <summary>
    ///     A comparable shape: one line per relation, its subjects sorted, empty entries dropped
    ///     because an empty closure and no closure mean the same thing.
    /// </summary>
    static string[] Sets<TValue>(IReadOnlyDictionary<string, TValue> lists)
        where TValue : IEnumerable<SubjectRef> =>
        [
            .. lists.Where(static x => x.Value.Any())
                .Select(static x => x.Key
                    + " => "
                    + string.Join(", ", x.Value.Select(static s => s.ToString()).Order(StringComparer.Ordinal))
                )
                .Order(StringComparer.Ordinal)
        ];

    /// <summary>
    ///     The reverse index over a tuple list, entries only — what a rebuild reads. Its closure
    ///     method is never called on this path and says so.
    /// </summary>
    sealed class EntriesOnlyReader(List<RelationTuple> tuples) : IReverseRelationReader {
        public ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
            ObjectRef subjectObject,
            CancellationToken cancellationToken
        ) {
            IReadOnlyList<SubjectIndexEntry> entries = [
                .. tuples
                    .Where(t => t.Subject.Object == subjectObject)
                    .Select(t => new SubjectIndexEntry {
                            Object = t.Object, Relation = t.Relation, SubjectRelation = t.Subject.Relation
                        }
                    )
                    .Distinct()
            ];

            return ValueTask.FromResult(Result<IReadOnlyList<SubjectIndexEntry>>.Success(entries));
        }

        public ValueTask<Result<IReadOnlyList<SubjectRef>>> ReadUsersetsAsync(
            SubjectRef subject,
            CancellationToken cancellationToken
        ) =>
            throw new InvalidOperationException(
                "The maintainer never reads the closure it maintains through the reverse reader."
            );
    }
}
