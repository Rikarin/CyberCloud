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

    /// <summary>Every subject object with a slice.</summary>
    public IEnumerable<ObjectRef> Objects => slices.Keys;

    /// <inheritdoc />
    public ValueTask<Result<MembershipIndexSnapshot>> ReadAsync(ObjectRef subjectObject, CancellationToken cancellationToken) {
        Reads++;
        return ValueTask.FromResult(Result<MembershipIndexSnapshot>.Success(Snapshot(subjectObject)));
    }

    /// <inheritdoc />
    public ValueTask<Result> ApplyAsync(ObjectRef subjectObject, MembershipIndexChange change, CancellationToken cancellationToken) {
        Writes++;

        if (!slices.TryGetValue(subjectObject, out var slice)) {
            slice = new();
            slices[subjectObject] = slice;
        }

        if (change.Reset) {
            slice.Members.Clear();
            slice.Usersets.Clear();
        }

        foreach (var (relation, members) in change.ReplaceMembers) {
            if (members.Count == 0) {
                slice.Members.Remove(relation);
            } else {
                slice.Members[relation] = [.. members];
            }
        }

        foreach (var (relation, members) in change.AddMembers) {
            if (!slice.Members.TryGetValue(relation, out var set)) {
                set = [];
                slice.Members[relation] = set;
            }

            set.UnionWith(members);
        }

        foreach (var (subjectRelation, usersets) in change.AddUsersets) {
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
        return ValueTask.FromResult(Result.Success);
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
            Members = slice.Members.ToDictionary(x => x.Key, x => (IReadOnlyList<SubjectRef>)[.. x.Value], StringComparer.Ordinal),
            Usersets = slice.Usersets.ToDictionary(x => x.Key, x => (IReadOnlyList<SubjectRef>)[.. x.Value], StringComparer.Ordinal)
        };
    }

    sealed class Slice {
        public int SchemaVersion { get; set; }

        public Dictionary<string, HashSet<SubjectRef>> Members { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HashSet<SubjectRef>> Usersets { get; } = new(StringComparer.Ordinal);
    }
}
