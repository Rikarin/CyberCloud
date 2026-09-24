using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Authorization.Tests.Infrastructure;
using CyberCloud.Core;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Authorization.Tests.Generated;

/// <summary>
///     docs/plan/07 § Time-bounded relations, held on generated graphs: an expired tuple grants
///     nothing, and no answer claims to hold past an instant at which it would change.
/// </summary>
/// <remarks>
///     <para>
///         <b>The definition is the reference evaluator over the tuples still live.</b> Expiry adds
///         nothing to the model but a filter: at instant <c>t</c> the tenant's tuples are the ones
///         whose expiry is later than <c>t</c>, and every answer is the answer over those. So each
///         graph here is <see cref="RandomGraphs" />' graph with an expiry on roughly a third of its
///         tuples, drawn from three instants, and every comparison runs
///         <see cref="ReferenceEvaluator" /> — which has never heard of expiry — over the live
///         subset. Nothing below shares code with the evaluator's instant arithmetic.
///     </para>
///     <para>
///         ⚠
///         <b>
///             "Never late" is the property that matters, and it's checked at every instant the
///             answer could change.
///         </b> The live set is constant between two expiry instants, so an answer
///         that the reference still gives at every expiry instant before the one the evaluator
///         named is an answer that held the whole way. An evaluator whose <c>ValidUntil</c> were
///         one instant too late would be caught at that instant; one that is early is allowed —
///         the cache re-walks — and the floors below make sure early isn't all it ever is.
///     </para>
/// </remarks>
public sealed class ExpiryPropertyTests {
    /// <summary>How many graphs each comparison covers.</summary>
    public const int Graphs = 4_000;

    static readonly AuthorizationLimits Unbounded =
        new() { MaxDepth = 512, MaxBreadth = 100_000, MaxListObjects = 100_000 };

    static readonly DateTimeOffset Start = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The instants a generated tuple can expire at.</summary>
    static readonly DateTimeOffset[] Expiries = [Start.AddHours(1), Start.AddHours(2), Start.AddHours(3)];

    /// <summary>The instants a check is made at: before anything expires, and after the first has.</summary>
    static readonly DateTimeOffset[] CheckedAt = [Start, Start.AddMinutes(90)];

    [Fact]
    public async Task NoCheckClaimsToHoldPastAnInstantItWouldChangeAt() {
        var stats = new Stats();

        for (var seed = 0; seed < Graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var tuples = WithExpiries(graph);

            foreach (var now in CheckedAt) {
                var reader = new InMemoryRelationReader(tuples) { Now = now };

                foreach (var (target, name, subject) in graph.Queries) {
                    var evaluated = await new CheckEvaluator(graph.Schema, reader, Unbounded)
                        .EvaluateAsync(target, name, subject, TestContext.Current.CancellationToken);

                    evaluated.IsSuccess.ShouldBeTrue(evaluated.Error?.Message);
                    Hold(graph, tuples, target, name, subject, now, evaluated.GetValueOrThrow(), stats);
                }
            }
        }

        stats.AssertFloors(Graphs * CheckedAt.Length * 4);
    }

    [Fact]
    public async Task NoCheckThroughTheLeopardIndexClaimsToHoldPastAnInstantItWouldChangeAt() {
        var stats = new Stats();
        var answersBefore = AuthorizationMetrics.IndexAnswers;

        for (var seed = 0; seed < Graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var tuples = WithExpiries(graph);

            foreach (var now in CheckedAt) {
                // ⚠ The index is built as the store would have built it — every tuple live when it
                // was written — and read after some have expired. A closure that had taken an
                // expiring edge in would answer "yes" here for a membership the reference refuses.
                var reader = new InMemoryRelationReader(tuples) { Now = now };
                var reverse = new InMemoryReverseRelationReader(graph.Schema, tuples) { Now = now };

                foreach (var (target, name, subject) in graph.Queries) {
                    var evaluated = await new CheckEvaluator(graph.Schema, reader, Unbounded, reverse.Index)
                        .EvaluateAsync(target, name, subject, TestContext.Current.CancellationToken);

                    evaluated.IsSuccess.ShouldBeTrue(evaluated.Error?.Message);
                    Hold(graph, tuples, target, name, subject, now, evaluated.GetValueOrThrow(), stats);
                }
            }
        }

        stats.AssertFloors(Graphs * CheckedAt.Length * 4);

        (AuthorizationMetrics.IndexAnswers - answersBefore).ShouldBeGreaterThan(
            Graphs / 4,
            "the index answered fewer than one userset test in four graphs (one in two measured) — it "
            + "is declining everything, and this is the walk run twice"
        );
    }

    [Fact]
    public async Task ListObjectsListsExactlyWhatIsLiveAtTheInstantItRuns() {
        const int graphs = 1_000;
        var comparisons = 0;
        var dropped = 0;

        SubjectRef[] subjects = [SubjectRef.Of("ta", "u0"), SubjectRef.Of("tb", "u1")];

        for (var seed = 0; seed < graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var tuples = WithExpiries(graph);
            var objects = tuples.Select(static t => t.Object)
                .Concat(tuples.Select(static t => t.Subject.Object))
                .Concat(subjects.Select(static s => s.Object))
                .Distinct()
                .ToList();

            foreach (var subject in subjects) {
                foreach (var name in RandomGraphs.PermissionNames.Concat(RandomGraphs.ComputedRelations)) {
                    foreach (var type in RandomGraphs.Types) {
                        string[]? before = null;

                        foreach (var now in CheckedAt) {
                            var live = LiveAt(tuples, now);
                            var expected = objects
                                .Where(o => o.Type == type)
                                .Where(o => ReferenceEvaluator.Evaluate(graph.Schema, live, o, name, subject))
                                .Select(static o => o.Id)
                                .Order(StringComparer.Ordinal)
                                .ToArray();

                            var reverse = new InMemoryReverseRelationReader(graph.Schema, tuples) { Now = now };
                            var evaluated = await new ListObjectsEvaluator(
                                graph.Schema,
                                new InMemoryRelationReader(tuples) { Now = now },
                                reverse,
                                Unbounded,
                                reverse.Index
                            ).EvaluateAsync(
                                subject,
                                new() { ObjectType = type, Permission = name },
                                TestContext.Current.CancellationToken
                            );

                            evaluated.IsSuccess.ShouldBeTrue(evaluated.Error?.Message);

                            evaluated.GetValueOrThrow()
                                .Objects.Select(static x => x.Id)
                                .ToArray()
                                .ShouldBe(
                                    expected,
                                    $"ListObjects at {now:O} disagrees with the brute force over the live tuples on "
                                    + $"{type}#{name}@{subject}."
                                    + Environment.NewLine
                                    + Describe(graph, tuples)
                                );

                            if (before is not null && before.Except(expected, StringComparer.Ordinal).Any()) {
                                dropped++;
                            }

                            before = expected;
                            comparisons++;
                        }
                    }
                }
            }
        }

        dropped.ShouldBeGreaterThan(
            comparisons / 200,
            "fewer than 0.5% of listings lost an object between the two instants (0.75% measured) — the "
            + "expiries are not reaching anything a listing returns"
        );
    }

    [Fact]
    public async Task TheIndexClosesOverPermanentEdgesOnlyAndMarksEveryUsersetAboveAnExpiringOne() {
        const int graphs = 2_000;
        var steps = 0;
        var marked = 0;
        var shortened = 0;

        for (var seed = 0; seed < graphs; seed++) {
            var graph = RandomGraphs.Generate(seed);
            var random = new Random(seed ^ 0x49_49);
            var store = new InMemoryMembershipIndexStore();
            List<RelationTuple> present = [];
            var absent = Identities(graph.Tuples);

            for (var step = 0; step < absent.Count * 3 + 4; step++) {
                var roll = random.Next(10);

                if (absent.Count > 0 && (present.Count == 0 || roll < 5)) {
                    // ── A write ────────────────────────────────────────────────────────────
                    var tuple = absent[random.Next(absent.Count)];
                    absent.Remove(tuple);

                    var written = random.Next(10) < 4 ? tuple with { ExpiresOn = Expiries[random.Next(3)] } : tuple;
                    present.Add(written);

                    await WriteAsync(graph.Schema, present, store, written);
                    if (random.Next(4) == 0) {
                        await WriteAsync(graph.Schema, present, store, written);
                    }
                } else if (present.Count > 0 && roll < 8) {
                    // ── A rewrite with another expiry, in the store's order ─────────────────
                    var old = present[random.Next(present.Count)];
                    var rewritten = old.ExpiresOn is null
                        ? old with { ExpiresOn = Expiries[random.Next(3)] }
                        : random.Next(2) == 0
                            ? old with { ExpiresOn = null }
                            : old with { ExpiresOn = old.ExpiresOn.Value.AddMinutes(1) };

                    var membersBefore = Closure(graph.Schema, present).Members.Sum(static x => x.Value.Count);

                    if (IsEdge(graph.Schema, old)) {
                        // TupleStoreGrain step 2 for a rewrite whose expiry differs: recompute as if
                        // the tuple were gone, while it is still present.
                        await DeleteAsync(graph.Schema, present, store, old);
                    }

                    present[present.IndexOf(old)] = rewritten;
                    await WriteAsync(graph.Schema, present, store, rewritten);

                    if (Closure(graph.Schema, present).Members.Sum(static x => x.Value.Count) < membersBefore) {
                        shortened++;
                    }
                } else if (present.Count > 0) {
                    // ── A delete ───────────────────────────────────────────────────────────
                    var tuple = present[random.Next(present.Count)];

                    await DeleteAsync(graph.Schema, present, store, tuple);
                    present.Remove(tuple);
                    absent.Add(tuple with { ExpiresOn = null });

                    if (random.Next(4) == 0) {
                        await DeleteAsync(graph.Schema, present, store, tuple);
                    }
                } else {
                    continue;
                }

                marked += AssertIndexEquals(graph, store, present, step);
                steps++;
            }
        }

        steps.ShouldBeGreaterThan(graphs * 10, "the mutation sequences are too short to mean anything");
        marked.ShouldBeGreaterThan(
            graphs,
            "fewer than one unclosed mark per graph was asserted — expiring edges are not reaching the index"
        );
        shortened.ShouldBeGreaterThan(
            graphs / 4,
            "rewriting a permanent edge as an expiring one rarely took a member out of a closure — the "
            + "store's step-2 recompute on a rewrite is barely exercised"
        );
    }

    // ── Holding one answer to the reference at every instant it claims ─────────────────────────

    static void Hold(
        GeneratedGraph graph,
        IReadOnlyList<RelationTuple> tuples,
        ObjectRef target,
        string name,
        SubjectRef subject,
        DateTimeOffset now,
        CheckEvaluation evaluation,
        Stats stats
    ) {
        evaluation.Outcome.ShouldBeOneOf(CheckOutcome.Allowed, CheckOutcome.Denied);

        var expected = ReferenceEvaluator.Evaluate(graph.Schema, LiveAt(tuples, now), target, name, subject);
        evaluation.Allowed.ShouldBe(
            expected,
            $"Check at {now:O} and the reference over the live tuples disagree on {target}#{name}@{subject}."
            + Environment.NewLine
            + Describe(graph, tuples)
        );

        if (evaluation.ValidUntil is { } until) {
            until.ShouldBeGreaterThan(now, "an answer that is already past its own instant could never be cached");
        }

        foreach (var instant in Expiries.Where(x => x > now && (evaluation.ValidUntil is not { } until || x < until))) {
            ReferenceEvaluator.Evaluate(graph.Schema, LiveAt(tuples, instant), target, name, subject)
                .ShouldBe(
                    evaluation.Allowed,
                    $"Check at {now:O} said {target}#{name}@{subject} = {evaluation.Allowed} until "
                    + $"{(evaluation.ValidUntil is { } u ? u.ToString("O", CultureInfo.InvariantCulture) : "never")}, "
                    + $"and at {instant:O} the answer over the live tuples is not that. A cache would serve "
                    + "the stale answer."
                    + Environment.NewLine
                    + Describe(graph, tuples)
                );
        }

        stats.Record(evaluation, graph, tuples, target, name, subject);
    }

    sealed class Stats {
        int comparisons;
        int allowedUntil;
        int allowedForever;
        int deniedUntil;
        int changedAtTheInstant;

        public void Record(
            CheckEvaluation evaluation,
            GeneratedGraph graph,
            IReadOnlyList<RelationTuple> tuples,
            ObjectRef target,
            string name,
            SubjectRef subject
        ) {
            comparisons++;

            if (evaluation.ValidUntil is not { } until) {
                if (evaluation.Allowed) {
                    allowedForever++;
                }

                return;
            }

            if (evaluation.Allowed) {
                allowedUntil++;
            } else {
                deniedUntil++;
            }

            if (ReferenceEvaluator.Evaluate(graph.Schema, LiveAt(tuples, until), target, name, subject)
                != evaluation.Allowed) {
                changedAtTheInstant++;
            }
        }

        public void AssertFloors(int expectedComparisons) {
            comparisons.ShouldBe(expectedComparisons);

            // ⚠ The floors that keep "never late" from passing against an evaluator that says
            // "never" for everything — or "now + 1 tick" for everything.
            allowedUntil.ShouldBeGreaterThan(
                comparisons / 50,
                "fewer than 2% of allows rested on an expiring tuple — the generator's expiries are not "
                + "reaching the derivations"
            );
            allowedForever.ShouldBeGreaterThan(
                comparisons / 50,
                "fewer than 2% of allows were permanent — every allow is being bounded, which would pass "
                + "the property by re-walking everything"
            );
            changedAtTheInstant.ShouldBeGreaterThan(
                allowedUntil / 3,
                "fewer than a third of the instants named were instants the answer actually changes at — "
                + "the evaluator is naming instants that are needlessly early"
            );
            deniedUntil.ShouldBeGreaterThan(
                0,
                "no deny ever carried an instant — an expiring tuple under a negation is untested"
            );
        }
    }

    // ── The index's brute force ─────────────────────────────────────────────────────────────────

    /// <summary>Asserts every slice against the brute force and returns how many marks it compared.</summary>
    static int AssertIndexEquals(GeneratedGraph graph, InMemoryMembershipIndexStore store, List<RelationTuple> present, int step) {
        var (members, usersets, unclosed) = Closure(graph.Schema, present);
        var why =
            $"seed {graph.Seed}, step {step.ToString(CultureInfo.InvariantCulture)}:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                present.Select(static x => "  " + x + (x.ExpiresOn is { } e ? $"  (expires {e:O})" : ""))
            );

        var marks = 0;

        foreach (var subjectObject in Objects(present).Concat(store.Objects).Distinct()) {
            var slice = store.Snapshot(subjectObject);

            Sets(slice.Members).ShouldBe(
                Sets(members.Where(x => x.Key.Object == subjectObject).ToDictionary(static x => x.Key.Relation, static x => x.Value)),
                $"{subjectObject}'s members differ from the closure over permanent edges — {why}"
            );
            Sets(slice.Usersets).ShouldBe(
                Sets(usersets.Where(x => x.Key.Object == subjectObject).ToDictionary(static x => x.Key.Relation, static x => x.Value)),
                $"{subjectObject}'s usersets differ from the closure over permanent edges — {why}"
            );

            var expectedMarks = unclosed.Where(x => x.Object == subjectObject)
                .Select(static x => x.Relation)
                .Order(StringComparer.Ordinal)
                .ToArray();

            slice.Unclosed.ShouldBe(expectedMarks, $"{subjectObject}'s unclosed marks differ — {why}");
            marks += expectedMarks.Length;
        }

        return marks;
    }

    /// <summary>
    ///     Both closures over the permanent edges, and the usersets that reach an expiring edge
    ///     through them — by search and nothing cleverer.
    /// </summary>
    static (Dictionary<SubjectRef, HashSet<SubjectRef>> Members, Dictionary<SubjectRef, HashSet<SubjectRef>> Usersets,
        HashSet<SubjectRef> Unclosed) Closure(AuthorizationSchema schema, List<RelationTuple> tuples) {
        var permanent = tuples.Where(t => IsEdge(schema, t) && t.ExpiresOn is null).ToList();
        var expiringFrom = tuples.Where(t => IsEdge(schema, t) && t.ExpiresOn is not null)
            .Select(static t => SubjectRef.Userset(t.Object.Type, t.Object.Id, t.Relation))
            .ToHashSet();

        Dictionary<SubjectRef, HashSet<SubjectRef>> members = [];
        Dictionary<SubjectRef, HashSet<SubjectRef>> usersets = [];
        HashSet<SubjectRef> unclosed = [];

        var starts = permanent.Select(static t => SubjectRef.Userset(t.Object.Type, t.Object.Id, t.Relation))
            .Concat(expiringFrom)
            .Distinct();

        foreach (var userset in starts) {
            HashSet<SubjectRef> reached = [];
            HashSet<SubjectRef> expanded = [userset];
            Queue<SubjectRef> pending = new();
            pending.Enqueue(userset);

            while (pending.Count > 0) {
                var current = pending.Dequeue();

                foreach (var edge in permanent.Where(e => e.Object == current.Object && e.Relation == current.Relation)) {
                    reached.Add(edge.Subject);

                    if (IsExpandable(schema, edge.Subject) && expanded.Add(edge.Subject)) {
                        pending.Enqueue(edge.Subject);
                    }
                }
            }

            if (reached.Count > 0) {
                members[userset] = reached;
            }

            if (expanded.Overlaps(expiringFrom)) {
                unclosed.Add(userset);
            }

            foreach (var member in reached) {
                if (!usersets.TryGetValue(member, out var containing)) {
                    containing = [];
                    usersets[member] = containing;
                }

                containing.Add(userset);
            }
        }

        return (members, usersets, unclosed);
    }

    static async Task WriteAsync(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store,
        RelationTuple tuple
    ) {
        var applied = await Maintainer(schema, present, store).ApplyWriteAsync(tuple, TestContext.Current.CancellationToken);
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
    }

    static async Task DeleteAsync(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store,
        RelationTuple tuple
    ) {
        var applied = await Maintainer(schema, present, store).ApplyDeleteAsync(tuple, TestContext.Current.CancellationToken);
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
    }

    static MembershipIndexMaintainer Maintainer(
        AuthorizationSchema schema,
        List<RelationTuple> present,
        InMemoryMembershipIndexStore store
    ) {
        // The store rebuilds an unwritten slice through the maintainer of the mutation that reached
        // it — over `present`, which is the tuple set the grain's rebuild would read.
        MembershipIndexMaintainer maintainer = new(
            schema,
            new InMemoryRelationReader(present),
            new EntriesReader(present),
            store
        );

        store.Rebuild = maintainer.RebuildAsync;
        return maintainer;
    }

    // ── Shared ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The generated tuples, one per identity, about a third of them expiring.</summary>
    static List<RelationTuple> WithExpiries(GeneratedGraph graph) {
        var random = new Random(graph.Seed ^ 0x49);

        return [
            .. Identities(graph.Tuples)
                .Select(t => random.Next(100) < 35 ? t with { ExpiresOn = Expiries[random.Next(Expiries.Length)] } : t)
        ];
    }

    static List<RelationTuple> Identities(IEnumerable<RelationTuple> tuples) {
        List<RelationTuple> distinct = [];

        foreach (var tuple in tuples) {
            if (!distinct.Exists(x => x.IsSameTupleAs(tuple))) {
                distinct.Add(tuple with { ExpiresOn = null });
            }
        }

        return distinct;
    }

    static List<RelationTuple> LiveAt(IEnumerable<RelationTuple> tuples, DateTimeOffset now) =>
        [.. tuples.Where(t => TupleExpiry.IsLive(t.ExpiresOn, now))];

    static bool IsEdge(AuthorizationSchema schema, RelationTuple tuple) =>
        schema.Member(tuple.Object.Type, tuple.Relation) is { IsPermission: false, IsDirectOnly: true };

    static bool IsExpandable(AuthorizationSchema schema, SubjectRef subject) =>
        subject.IsUserset
        && schema.Member(subject.Type, subject.Relation) is { IsPermission: false, IsDirectOnly: true };

    static IEnumerable<ObjectRef> Objects(IEnumerable<RelationTuple> tuples) =>
        tuples.Select(static t => t.Object).Concat(tuples.Select(static t => t.Subject.Object)).Distinct();

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

    static string Describe(GeneratedGraph graph, IEnumerable<RelationTuple> tuples) =>
        graph.Describe()
        + Environment.NewLine
        + "expiries:"
        + Environment.NewLine
        + string.Join(
            Environment.NewLine,
            tuples.Where(static t => t.ExpiresOn is not null).Select(static t => $"  {t} expires {t.ExpiresOn:O}")
        );

    /// <summary>The reverse index over a tuple list, entries only — what a rebuild reads.</summary>
    sealed class EntriesReader(List<RelationTuple> tuples) : IReverseRelationReader {
        public ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
            ObjectRef subjectObject,
            CancellationToken cancellationToken
        ) {
            IReadOnlyList<SubjectIndexEntry> entries = [
                .. tuples
                    .Where(t => t.Subject.Object == subjectObject)
                    .Select(static t => new SubjectIndexEntry {
                            Object = t.Object,
                            Relation = t.Relation,
                            SubjectRelation = t.Subject.Relation,
                            ExpiresOn = t.ExpiresOn
                        }
                    )
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
