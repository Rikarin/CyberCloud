using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     Keeps the Leopard index a transitive closure across every tuple write and delete —
///     docs/plan/07 § The Leopard index, the "Maintenance" paragraph, done in the write path
///     rather than behind a stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>The graph, stated once.</b> Its edges are the tuples <c>o#r@S</c> whose relation
///         <c>r</c> is <i>direct-only</i> on <c>o</c>'s type — computed from <c>This</c> and nothing
///         else, which is <see cref="SchemaMember.IsDirectOnly" /> — read as "the userset
///         <c>o#r</c> contains <c>S</c>". A chain is followed onward from <c>S</c> only when
///         <c>S</c> is itself a userset on a direct-only relation. The index stores the transitive
///         closure of that graph from both ends: <see cref="MembershipIndexSnapshot.Members" />
///         is everything a userset reaches, <see cref="MembershipIndexSnapshot.Usersets" /> is
///         everything that reaches a subject.
///     </para>
///     <para>
///         ⚠ <b>Why direct-only and not "has a <c>This</c>".</b> A direct tuple on
///         <c>This | From("parent", "owner")</c> does grant, so following it would be sound for a
///         <c>true</c> — but the userset's <i>full</i> membership is a <c>From</c> away, so no
///         closure over tuples could ever say <c>false</c> for it, and a fast path that can only
///         say yes is a fast path a check cannot rely on for the case that costs the most: the
///         user who is <i>not</i> in the ten-thousand-member group. Restricting the graph to
///         direct-only relations is what makes a membership the closure does not contain a
///         membership that does not exist, and on the shipping schema it costs nothing —
///         <c>group#member</c> is direct-only and is the only userset the platform forms.
///     </para>
///     <para>
///         <b>A write is two unions.</b> Adding the edge <c>U → S</c> puts <c>{S} ∪ Members(S)</c>
///         into the members of <c>U</c> and of every userset above <c>U</c>, and puts
///         <c>{U} ∪ Usersets(U)</c> into the usersets of <c>S</c> and of every subject below it.
///         Both operands are read from the index itself, so the cost is one read on each side and
///         one write per userset above plus one per member below — the fan-out docs/plan/07's
///         threshold paragraph exists to cap.
///     </para>
///     <para>
///         <b>A delete recomputes, and it recomputes only what the edge could have carried.</b>
///         Removing <c>U → S</c> can only shorten the members of <c>U</c> and of the usersets above
///         it, and only by subjects that are <c>S</c> or below it. So every userset above <c>U</c>
///         has its members recomputed from the tuples — a walk down the forward index, one read per
///         nested userset, memoized across the batch — and every subject below <c>S</c> drops the
///         usersets above <c>U</c> whose recomputed members no longer include it. That is the
///         "recompute-on-delete … bounded by the member set" the status paragraph describes.
///     </para>
///     <para>
///         ⚠ <b>Both are idempotent, and the sweeper depends on it.</b> The two sets a write or a
///         delete reads — the usersets above <c>U</c>, the members below <c>S</c> — are unchanged
///         by the edge itself: a shortest path into <c>U</c> never leaves through <c>U</c>, and a
///         shortest path out of <c>S</c> never arrives through <c>S</c>. A journalled change
///         replayed after a partial application therefore derives the same unions and the same
///         recomputation, and the second application changes nothing.
///     </para>
///     <para>
///         ⚠ <b>A slice stamped with another schema version is rebuilt before it is used.</b>
///         Which relations are direct-only is the schema's to say, so a closure computed under
///         version 2 is not a closure under version 3. The maintainer notices on read and
///         recomputes that slice from the tuples first; readers refuse such a slice outright.
///         What nobody does is find the slices a schema bump left behind that no write has since
///         touched — see docs/plan/07 § The Leopard index for what that owes.
///     </para>
/// </remarks>
public sealed class MembershipIndexMaintainer {
    /// <summary>How many slice writes are in flight at once. A group of ten thousand is ten thousand writes.</summary>
    const int Concurrency = 32;

    readonly AuthorizationSchema schema;
    readonly IRelationReader forward;
    readonly IReverseRelationReader reverse;
    readonly IMembershipIndexStore store;

    /// <summary>Creates a maintainer over one tenant's tuples and index.</summary>
    /// <param name="schema">The schema — it decides which relations are edges.</param>
    /// <param name="forward">The forward index, for recomputation.</param>
    /// <param name="reverse">The reverse index, for rebuilding a subject's usersets. Only its entries are read.</param>
    /// <param name="store">Where the slices live.</param>
    public MembershipIndexMaintainer(
        AuthorizationSchema schema,
        IRelationReader forward,
        IReverseRelationReader reverse,
        IMembershipIndexStore store
    ) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(forward);
        ArgumentNullException.ThrowIfNull(reverse);
        ArgumentNullException.ThrowIfNull(store);

        this.schema = schema;
        this.forward = forward;
        this.reverse = reverse;
        this.store = store;
    }

    /// <summary>Whether tuples on this relation are edges of the closure graph.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="type">The object type.</param>
    /// <param name="relation">The relation.</param>
    public static bool IsIndexed(AuthorizationSchema schema, string type, string relation) {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Member(type, relation) is { IsPermission: false, IsDirectOnly: true };
    }

    /// <summary>Whether a chain may continue through this subject — a userset on an indexed relation.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="subject">The subject.</param>
    public static bool IsExpandable(AuthorizationSchema schema, SubjectRef subject) {
        ArgumentNullException.ThrowIfNull(subject);
        return subject.IsUserset && IsIndexed(schema, subject.Type, subject.Relation);
    }

    /// <summary>Records a tuple that has been, or is about to be, written to the forward index.</summary>
    /// <param name="tuple">The tuple.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many slices were written. Zero when the tuple is not an edge.</returns>
    public async ValueTask<Result<int>> ApplyWriteAsync(RelationTuple tuple, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(tuple);

        if (!IsIndexed(schema, tuple.Object.Type, tuple.Relation)) {
            return Result<int>.Success(0);
        }

        var userset = SubjectRef.Userset(tuple.Object.Type, tuple.Object.Id, tuple.Relation);
        var subject = tuple.Subject;

        var ends = await EndsAsync(userset, subject, cancellationToken).ConfigureAwait(false);
        if (ends.TryGetError(out var error)) {
            return Result<int>.Failure(error);
        }

        var (above, below) = ends.GetValueOrThrow();
        var changes = new ChangeSet(schema.Version);

        foreach (var upper in above) {
            changes.AddMembers(upper, below);
        }

        foreach (var lower in below) {
            changes.AddUsersets(lower, above);
        }

        return await ApplyAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Records a tuple that is about to be deleted from the forward index. The forward reader
    ///     is consulted as if the tuple were already gone, so the store may — and does — apply this
    ///     before the delete itself.
    /// </summary>
    /// <param name="tuple">The tuple.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many slices were written. Zero when the tuple is not an edge.</returns>
    public async ValueTask<Result<int>> ApplyDeleteAsync(RelationTuple tuple, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(tuple);

        if (!IsIndexed(schema, tuple.Object.Type, tuple.Relation)) {
            return Result<int>.Success(0);
        }

        var userset = SubjectRef.Userset(tuple.Object.Type, tuple.Object.Id, tuple.Relation);
        var subject = tuple.Subject;

        var ends = await EndsAsync(userset, subject, cancellationToken).ConfigureAwait(false);
        if (ends.TryGetError(out var error)) {
            return Result<int>.Failure(error);
        }

        var (above, below) = ends.GetValueOrThrow();
        var walker = new ClosureWalker(schema, new ExcludingReader(forward, tuple), reverse);
        var changes = new ChangeSet(schema.Version);
        Dictionary<SubjectRef, HashSet<SubjectRef>> recomputed = [];

        foreach (var upper in above) {
            var members = await walker.DownAsync(upper, cancellationToken).ConfigureAwait(false);
            if (members.TryGetError(out var walkError)) {
                return Result<int>.Failure(walkError);
            }

            recomputed[upper] = members.GetValueOrThrow();
            changes.ReplaceMembers(upper, recomputed[upper]);
        }

        foreach (var lower in below) {
            var gone = above.Where(upper => !recomputed[upper].Contains(lower)).ToList();
            if (gone.Count > 0) {
                changes.RemoveUsersets(lower, gone);
            }
        }

        return await ApplyAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Recomputes one slice from the tuples — every indexed userset formed on the object, every
    ///     userset the object is in under any of its subject relations — as a whole-slice
    ///     replacement.
    /// </summary>
    /// <param name="subjectObject">The subject object.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async ValueTask<Result<MembershipIndexChange>> RebuildAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(subjectObject);

        var walker = new ClosureWalker(schema, forward, reverse);
        var changes = new ChangeSet(schema.Version) { Reset = true };

        var snapshot = await forward.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        if (snapshot.TryGetError(out var forwardError)) {
            return Result<MembershipIndexChange>.Failure(forwardError);
        }

        foreach (var relation in snapshot.GetValueOrThrow().ByRelation.Keys) {
            if (!IsIndexed(schema, subjectObject.Type, relation)) {
                continue;
            }

            var userset = SubjectRef.Userset(subjectObject.Type, subjectObject.Id, relation);
            var members = await walker.DownAsync(userset, cancellationToken).ConfigureAwait(false);
            if (members.TryGetError(out var downError)) {
                return Result<MembershipIndexChange>.Failure(downError);
            }

            changes.AddMembers(userset, members.GetValueOrThrow());
        }

        var entries = await reverse.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        if (entries.TryGetError(out var reverseError)) {
            return Result<MembershipIndexChange>.Failure(reverseError);
        }

        foreach (var subjectRelation in entries.GetValueOrThrow().Select(x => x.SubjectRelation).Distinct(StringComparer.Ordinal)) {
            var subject = subjectRelation.Length == 0
                ? SubjectRef.Of(subjectObject.Type, subjectObject.Id)
                : SubjectRef.Userset(subjectObject.Type, subjectObject.Id, subjectRelation);

            var usersets = await walker.UpAsync(subject, cancellationToken).ConfigureAwait(false);
            if (usersets.TryGetError(out var upError)) {
                return Result<MembershipIndexChange>.Failure(upError);
            }

            changes.AddUsersets(subject, usersets.GetValueOrThrow());
        }

        return Result<MembershipIndexChange>.Success(changes.For(subjectObject));
    }

    /// <summary>
    ///     The two operands every edge change is built from: the userset and everything above it,
    ///     the subject and everything below it. Both read from the index, both including their
    ///     own end.
    /// </summary>
    async ValueTask<Result<(List<SubjectRef> Above, List<SubjectRef> Below)>> EndsAsync(
        SubjectRef userset,
        SubjectRef subject,
        CancellationToken cancellationToken
    ) {
        var upperSlice = await CurrentAsync(userset.Object, cancellationToken).ConfigureAwait(false);
        if (upperSlice.TryGetError(out var upperError)) {
            return Result<(List<SubjectRef>, List<SubjectRef>)>.Failure(upperError);
        }

        List<SubjectRef> above = [userset];
        foreach (var candidate in upperSlice.GetValueOrThrow().UsersetsOf(userset.Relation)) {
            if (!above.Contains(candidate)) {
                above.Add(candidate);
            }
        }

        List<SubjectRef> below = [subject];

        if (IsExpandable(schema, subject)) {
            var lowerSlice = await CurrentAsync(subject.Object, cancellationToken).ConfigureAwait(false);
            if (lowerSlice.TryGetError(out var lowerError)) {
                return Result<(List<SubjectRef>, List<SubjectRef>)>.Failure(lowerError);
            }

            foreach (var candidate in lowerSlice.GetValueOrThrow().MembersOf(subject.Relation)) {
                if (!below.Contains(candidate)) {
                    below.Add(candidate);
                }
            }
        }

        return Result<(List<SubjectRef>, List<SubjectRef>)>.Success((above, below));
    }

    /// <summary>A slice as of this schema version — rebuilt first if it was written under another.</summary>
    async ValueTask<Result<MembershipIndexSnapshot>> CurrentAsync(ObjectRef subjectObject, CancellationToken cancellationToken) {
        var read = await store.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error)) {
            return Result<MembershipIndexSnapshot>.Failure(error);
        }

        var snapshot = read.GetValueOrThrow();
        if (snapshot.SchemaVersion == 0 || snapshot.SchemaVersion == schema.Version) {
            return read;
        }

        AuthorizationMetrics.RecordIndexRebuild();

        var rebuilt = await RebuildAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        if (rebuilt.TryGetError(out var rebuildError)) {
            return Result<MembershipIndexSnapshot>.Failure(rebuildError);
        }

        var applied = await store.ApplyAsync(subjectObject, rebuilt.GetValueOrThrow(), cancellationToken).ConfigureAwait(false);
        return applied.TryGetError(out var applyError)
            ? Result<MembershipIndexSnapshot>.Failure(applyError)
            : await store.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<Result<int>> ApplyAsync(ChangeSet changes, CancellationToken cancellationToken) {
        var slices = changes.Slices();
        var written = 0;

        foreach (var batch in slices.Chunk(Concurrency)) {
            var results = await Task.WhenAll(
                    batch.Select(x => store.ApplyAsync(x.Object, x.Change, cancellationToken).AsTask())
                )
                .ConfigureAwait(false);

            foreach (var result in results) {
                if (result.TryGetError(out var error)) {
                    return Result<int>.Failure(error);
                }

                written++;
            }
        }

        AuthorizationMetrics.RecordIndexWrites(written);
        return Result<int>.Success(written);
    }

    /// <summary>Changes accumulated per slice, so one grain is written once per tuple.</summary>
    sealed class ChangeSet(int schemaVersion) {
        readonly Dictionary<ObjectRef, Slice> slices = [];

        public bool Reset { get; init; }

        public void AddMembers(SubjectRef userset, IEnumerable<SubjectRef> members) =>
            Union(SliceFor(userset.Object).AddMembers, userset.Relation, members);

        public void ReplaceMembers(SubjectRef userset, IEnumerable<SubjectRef> members) =>
            Union(SliceFor(userset.Object).ReplaceMembers, userset.Relation, members);

        public void AddUsersets(SubjectRef subject, IEnumerable<SubjectRef> usersets) =>
            Union(SliceFor(subject.Object).AddUsersets, subject.Relation, usersets);

        public void RemoveUsersets(SubjectRef subject, IEnumerable<SubjectRef> usersets) =>
            Union(SliceFor(subject.Object).RemoveUsersets, subject.Relation, usersets);

        public MembershipIndexChange For(ObjectRef subjectObject) => Build(SliceFor(subjectObject));

        public List<(ObjectRef Object, MembershipIndexChange Change)> Slices() =>
            slices.Select(x => (x.Key, Build(x.Value))).ToList();

        Slice SliceFor(ObjectRef subjectObject) {
            if (!slices.TryGetValue(subjectObject, out var slice)) {
                slice = new();
                slices[subjectObject] = slice;
            }

            return slice;
        }

        MembershipIndexChange Build(Slice slice) =>
            new() {
                SchemaVersion = schemaVersion,
                Reset = Reset,
                AddMembers = Freeze(slice.AddMembers),
                ReplaceMembers = Freeze(slice.ReplaceMembers),
                AddUsersets = Freeze(slice.AddUsersets),
                RemoveUsersets = Freeze(slice.RemoveUsersets)
            };

        static void Union(Dictionary<string, HashSet<SubjectRef>> into, string key, IEnumerable<SubjectRef> values) {
            if (!into.TryGetValue(key, out var set)) {
                set = [];
                into[key] = set;
            }

            set.UnionWith(values);
        }

        static Dictionary<string, IReadOnlyList<SubjectRef>> Freeze(Dictionary<string, HashSet<SubjectRef>> sets) {
            Dictionary<string, IReadOnlyList<SubjectRef>> frozen = new(StringComparer.Ordinal);
            foreach (var (key, set) in sets) {
                frozen[key] = [.. set.OrderBy(x => x.ToString(), StringComparer.Ordinal)];
            }

            return frozen;
        }

        sealed class Slice {
            public Dictionary<string, HashSet<SubjectRef>> AddMembers { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, HashSet<SubjectRef>> ReplaceMembers { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, HashSet<SubjectRef>> AddUsersets { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, HashSet<SubjectRef>> RemoveUsersets { get; } = new(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     The forward index as it will be once a tuple is deleted. The store applies the index
    ///     step of a delete <i>before</i> the forward half, so a recomputation that read the tuples
    ///     as they are would put the edge being removed straight back.
    /// </summary>
    sealed class ExcludingReader(IRelationReader inner, RelationTuple excluded) : IRelationReader {
        public async ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(ObjectRef target, CancellationToken cancellationToken) {
            var read = await inner.ReadAsync(target, cancellationToken).ConfigureAwait(false);
            if (read.IsFailure || target != excluded.Object) {
                return read;
            }

            var snapshot = read.GetValueOrThrow();
            if (!snapshot.Subjects(excluded.Relation).Contains(excluded.Subject)) {
                return read;
            }

            Dictionary<string, IReadOnlyList<SubjectRef>> filtered = new(StringComparer.Ordinal);
            foreach (var (relation, subjects) in snapshot.ByRelation) {
                filtered[relation] = string.Equals(relation, excluded.Relation, StringComparison.Ordinal)
                    ? [.. subjects.Where(x => x != excluded.Subject)]
                    : subjects;
            }

            return Result<ObjectRelationsSnapshot>.Success(
                snapshot with { ByRelation = filtered, Count = snapshot.Count - 1 }
            );
        }
    }

    /// <summary>
    ///     The closure computed from the tuples rather than from the index — what a delete and a
    ///     rebuild replace stored closures with. Memoizes every read for its lifetime, which is one
    ///     delete or one rebuild.
    /// </summary>
    sealed class ClosureWalker(AuthorizationSchema schema, IRelationReader forward, IReverseRelationReader reverse) {
        readonly Dictionary<ObjectRef, ObjectRelationsSnapshot> snapshots = [];
        readonly Dictionary<ObjectRef, IReadOnlyList<SubjectIndexEntry>> entries = [];

        /// <summary>Every subject the userset reaches: its members, closed.</summary>
        public async ValueTask<Result<HashSet<SubjectRef>>> DownAsync(SubjectRef userset, CancellationToken cancellationToken) {
            HashSet<SubjectRef> reached = [];
            Queue<SubjectRef> pending = new();
            pending.Enqueue(userset);

            while (pending.Count > 0) {
                var current = pending.Dequeue();

                var snapshot = await SnapshotAsync(current.Object, cancellationToken).ConfigureAwait(false);
                if (snapshot.TryGetError(out var error)) {
                    return Result<HashSet<SubjectRef>>.Failure(error);
                }

                foreach (var subject in snapshot.GetValueOrThrow().Subjects(current.Relation)) {
                    if (reached.Add(subject) && IsExpandable(schema, subject)) {
                        pending.Enqueue(subject);
                    }
                }
            }

            return Result<HashSet<SubjectRef>>.Success(reached);
        }

        /// <summary>Every userset that reaches the subject: the usersets it is in, closed.</summary>
        public async ValueTask<Result<HashSet<SubjectRef>>> UpAsync(SubjectRef subject, CancellationToken cancellationToken) {
            HashSet<SubjectRef> reached = [];
            Queue<SubjectRef> pending = new();
            pending.Enqueue(subject);

            while (pending.Count > 0) {
                var current = pending.Dequeue();

                var read = await EntriesAsync(current.Object, cancellationToken).ConfigureAwait(false);
                if (read.TryGetError(out var error)) {
                    return Result<HashSet<SubjectRef>>.Failure(error);
                }

                foreach (var entry in read.GetValueOrThrow()) {
                    if (!string.Equals(entry.SubjectRelation, current.Relation, StringComparison.Ordinal)
                        || !IsIndexed(schema, entry.Object.Type, entry.Relation)) {
                        continue;
                    }

                    var userset = SubjectRef.Userset(entry.Object.Type, entry.Object.Id, entry.Relation);
                    if (reached.Add(userset)) {
                        pending.Enqueue(userset);
                    }
                }
            }

            return Result<HashSet<SubjectRef>>.Success(reached);
        }

        async ValueTask<Result<ObjectRelationsSnapshot>> SnapshotAsync(ObjectRef target, CancellationToken cancellationToken) {
            if (snapshots.TryGetValue(target, out var cached)) {
                return Result<ObjectRelationsSnapshot>.Success(cached);
            }

            var read = await forward.ReadAsync(target, cancellationToken).ConfigureAwait(false);
            if (read.IsSuccess) {
                snapshots[target] = read.GetValueOrThrow();
            }

            return read;
        }

        async ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> EntriesAsync(ObjectRef target, CancellationToken cancellationToken) {
            if (entries.TryGetValue(target, out var cached)) {
                return Result<IReadOnlyList<SubjectIndexEntry>>.Success(cached);
            }

            var read = await reverse.ReadAsync(target, cancellationToken).ConfigureAwait(false);
            if (read.IsSuccess) {
                entries[target] = read.GetValueOrThrow();
            }

            return read;
        }
    }
}
