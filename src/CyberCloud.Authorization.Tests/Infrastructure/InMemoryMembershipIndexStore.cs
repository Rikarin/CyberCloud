using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     The Leopard index's slices in a dictionary, behind the same <see cref="IMembershipIndexStore" />
///     the grains implement.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The maintainer under test is the maintainer that ships.</b> This store applies a
///         <see cref="MembershipIndexChange" /> with the same four operations
///         <c>MembershipIndexGrain.ApplyAsync</c> applies, so what <c>MembershipIndexPropertyTests</c>
///         holds to a brute-force closure is the algorithm and not a copy of it. The grain tests
///         are what shows the grain's own application agrees with this one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An incremental change lands on an unwritten or stale slice only after a rebuild,
///             here as in the grain.
///         </b> <c>MembershipIndexGrain.ApplyAsync</c> recomputes such a
///         slice from the two indexes before applying a union to it, because a union over nothing
///         stamped with the current version is a closure that omits every tuple older than the
///         index. A dictionary cannot reach the indexes, so whoever builds the maintainer hands
///         this store the maintainer's own <see cref="MembershipIndexMaintainer.RebuildAsync" />
///         as <see cref="Rebuild" />; a store asked to do it without one throws rather than apply
///         the union, so no test can pass by the shortcut the grain refuses. A whole-slice
///         replacement (<see cref="MembershipIndexChange.Reset" />) needs no rebuild, which is how
///         a test writes a slice by hand.
///     </para>
///     <para>
///         Every read returns a fresh snapshot with copied lists, because the real store does: a
///         maintainer that mutated a returned list would pass here and silently corrupt nothing in
///         production, which is the wrong way round.
///     </para>
/// </remarks>
public sealed class InMemoryMembershipIndexStore : IMembershipIndexStore {
    readonly Dictionary<ObjectRef, Slice> slices = [];

    /// <summary>How many slice reads have been made.</summary>
    public int Reads { get; private set; }

    /// <summary>How many slice writes have been made.</summary>
    public int Writes { get; private set; }

    /// <summary>How many slices were rebuilt before an incremental change could land on them.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>How many incremental changes landed on a slice that was already a closure under their schema version.</summary>
    public int Unions { get; private set; }

    /// <summary>
    ///     What recomputes a slice from the tuples — the maintainer's <see cref="MembershipIndexMaintainer.RebuildAsync" />,
    ///     which is what the grain runs for itself.
    /// </summary>
    public Func<ObjectRef, CancellationToken, ValueTask<Result<MembershipIndexChange>>>? Rebuild { get; set; }

    /// <summary>Every subject object with a slice.</summary>
    public IEnumerable<ObjectRef> Objects => slices.Keys;

    /// <inheritdoc />
    public ValueTask<Result<MembershipIndexSnapshot>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) {
        Reads++;
        return ValueTask.FromResult(Result<MembershipIndexSnapshot>.Success(Snapshot(subjectObject)));
    }

    /// <inheritdoc />
    public async ValueTask<Result> ApplyAsync(
        ObjectRef subjectObject,
        MembershipIndexChange change,
        CancellationToken cancellationToken
    ) {
        Writes++;

        // A whole-slice replacement is a closure in itself; anything else needs one to land on.
        if (!change.Reset) {
            if (slices.TryGetValue(subjectObject, out var current) && current.SchemaVersion == change.SchemaVersion) {
                Unions++;
            } else {
                var rebuilt = await RebuildAsync(subjectObject, cancellationToken);
                if (rebuilt.TryGetError(out var error)) {
                    return Result.Failure(error);
                }

                Apply(subjectObject, rebuilt.GetValueOrThrow());
            }
        }

        Apply(subjectObject, change);
        return Result.Success;
    }

    async ValueTask<Result<MembershipIndexChange>> RebuildAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    ) {
        if (Rebuild is null) {
            throw new InvalidOperationException(
                $"An incremental change reached {subjectObject}, whose slice is unwritten or stale, and this store "
                + "has no Rebuild. The grain rebuilds such a slice from the tuples before applying anything to it; "
                + "hand the store the maintainer's RebuildAsync, or write the slice whole with Reset."
            );
        }

        Rebuilds++;
        return await Rebuild(subjectObject, cancellationToken);
    }

    void Apply(ObjectRef subjectObject, MembershipIndexChange change) {
        if (!slices.TryGetValue(subjectObject, out var slice)) {
            slice = new();
            slices[subjectObject] = slice;
        }

        if (change.Reset) {
            slice.Members.Clear();
            slice.Usersets.Clear();
            slice.Unclosed.Clear();
        }

        foreach (var (relation, members) in change.ReplaceMembers) {
            if (members.Count == 0) {
                slice.Members.Remove(relation);
            } else {
                slice.Members[relation] = [.. members];
            }

            // A replaced closure's mark is replaced with it — MembershipIndexChange.Unclosed.
            if (change.Unclosed.Contains(relation, StringComparer.Ordinal)) {
                slice.Unclosed.Add(relation);
            } else {
                slice.Unclosed.Remove(relation);
            }
        }

        foreach (var relation in change.Unclosed.Where(x => !change.ReplaceMembers.ContainsKey(x))) {
            slice.Unclosed.Add(relation);
        }

        // An empty union creates no entry, as the grain's Add does not — a rebuild names every
        // subject relation it saw, closed or not.
        foreach (var (relation, members) in change.AddMembers.Where(static x => x.Value.Count > 0)) {
            if (!slice.Members.TryGetValue(relation, out var set)) {
                set = [];
                slice.Members[relation] = set;
            }

            set.UnionWith(members);
        }

        foreach (var (subjectRelation, usersets) in change.AddUsersets.Where(static x => x.Value.Count > 0)) {
            if (!slice.Usersets.TryGetValue(subjectRelation, out var set)) {
                set = [];
                slice.Usersets[subjectRelation] = set;
            }

            set.UnionWith(usersets);
        }

        foreach (var (subjectRelation, usersets) in change.RemoveUsersets) {
            if (slice.Usersets.TryGetValue(subjectRelation, out var set)) {
                set.ExceptWith(usersets);
                if (set.Count == 0) {
                    slice.Usersets.Remove(subjectRelation);
                }
            }
        }

        slice.SchemaVersion = change.SchemaVersion;
    }

    /// <summary>The slice as the store holds it, without counting a read.</summary>
    /// <param name="subjectObject">The subject object.</param>
    public MembershipIndexSnapshot Snapshot(ObjectRef subjectObject) {
        if (!slices.TryGetValue(subjectObject, out var slice)) {
            return new() { Object = subjectObject };
        }

        return new() {
            Object = subjectObject,
            SchemaVersion = slice.SchemaVersion,
            Members = slice.Members.ToDictionary(
                static x => x.Key,
                static x => (IReadOnlyList<SubjectRef>)[.. x.Value],
                StringComparer.Ordinal
            ),
            Usersets = slice.Usersets.ToDictionary(
                static x => x.Key,
                static x => (IReadOnlyList<SubjectRef>)[.. x.Value],
                StringComparer.Ordinal
            ),
            Unclosed = [.. slice.Unclosed.Order(StringComparer.Ordinal)]
        };
    }

    sealed class Slice {
        public int SchemaVersion { get; set; }

        public HashSet<string> Unclosed { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<SubjectRef>> Members { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<SubjectRef>> Usersets { get; } = new(StringComparer.Ordinal);
    }
}
