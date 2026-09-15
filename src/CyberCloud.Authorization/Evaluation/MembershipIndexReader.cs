using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     The Leopard index as the two evaluators read it — docs/plan/07 § The Leopard index: "a fast
///     path that is always verifiable, never an authority" — with the one rule that makes a
///     <c>false</c> from it safe.
/// </summary>
/// <remarks>
///     <para>
///         One instance per request, like the evaluators: it caches every slice it reads, so a
///         check against an object with a thousand userset grants reads the subject's slice once
///         and answers the thousand from it.
///     </para>
///     <para>
///         <b>Three answers, and what each one costs.</b> A userset the schema does not index —
///         its relation is not direct-only — is "walk it" without a read. Otherwise the subject's
///         own slice is read first: if the userset is among the usersets the subject is closed
///         into, the answer is <c>true</c>, and that is the one read the common case pays. Failing
///         that, the userset's slice is read: <c>true</c> if the subject is among its members,
///         <c>false</c> if the closure is <i>complete</i>, and "walk it" if it is not.
///     </para>
///     <para>
///         ⚠ <b>Complete means every userset in the closure is one the closure expanded.</b> A
///         member of the form <c>resourceGroup:r#owner</c> — a userset on a relation that is not
///         direct-only — is recorded and not expanded, because what it contains is a
///         <c>From("parent", …)</c> away from anything a closure over tuples can say. A
///         <c>false</c> in its presence would deny a subject who inherits membership through it,
///         so the answer is "walk it" and <c>CheckEvaluator</c> takes the arm it always took. A
///         userset whose relation the schema does not define at all counts as expanded: the
///         evaluator denies that path too, so the two agree.
///     </para>
///     <para>
///         ⚠ <b>A slice stamped with another schema version is not read at all.</b> The closure
///         depends on the schema only through which relations are direct-only, and a slice
///         computed under another version may have followed edges this one would not. Such a
///         slice answers "walk it" here and nothing on the listing side; the maintainer rebuilds
///         it the next time a write touches it.
///     </para>
/// </remarks>
public sealed class MembershipIndexReader : IMembershipIndex {
    readonly AuthorizationSchema schema;
    readonly IMembershipIndexStore store;
    readonly Dictionary<ObjectRef, MembershipIndexSnapshot?> slices = [];

    /// <summary>Creates a reader for one request.</summary>
    /// <param name="schema">The schema — it decides which relations are indexed.</param>
    /// <param name="store">Where the slices live.</param>
    public MembershipIndexReader(AuthorizationSchema schema, IMembershipIndexStore store) {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(store);

        this.schema = schema;
        this.store = store;
    }

    /// <summary>How many slices were read. One per distinct subject object, at most.</summary>
    public int Reads { get; private set; }

    /// <summary>Whether tuples on this relation are edges of the closure — see <see cref="MembershipIndexMaintainer" />.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="relation">The relation.</param>
    public bool IsIndexed(string type, string relation) => MembershipIndexMaintainer.IsIndexed(schema, type, relation);

    /// <summary>
    ///     Every userset the subject is transitively in through indexed relations, or an empty
    ///     list when its slice is unwritten, stale, or the subject is not one the index follows.
    /// </summary>
    /// <param name="subject">The subject — concrete or a userset.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async ValueTask<Result<IReadOnlyList<SubjectRef>>> UsersetsOfAsync(
        SubjectRef subject,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(subject);

        var slice = await SliceAsync(subject.Object, cancellationToken).ConfigureAwait(false);
        if (slice.TryGetError(out var error)) {
            return Result<IReadOnlyList<SubjectRef>>.Failure(error);
        }

        var snapshot = slice.GetValueOrThrow().Snapshot;
        return Result<IReadOnlyList<SubjectRef>>.Success(snapshot is null ? [] : snapshot.UsersetsOf(subject.Relation));
    }

    /// <inheritdoc />
    public async ValueTask<bool?> TryTestMembershipAsync(
        SubjectRef userset,
        SubjectRef subject,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(userset);
        ArgumentNullException.ThrowIfNull(subject);

        if (!userset.IsUserset || !IsIndexed(userset.Type, userset.Relation)) {
            return null;
        }

        // The subject's side first: one read answers every userset it is in.
        var subjectSlice = await SliceAsync(subject.Object, cancellationToken).ConfigureAwait(false);
        if (subjectSlice.IsFailure) {
            // A storage error is the walk's to report, on the read it makes itself.
            return null;
        }

        if (subjectSlice.GetValueOrThrow().Snapshot is { } subjectSnapshot
            && subjectSnapshot.UsersetsOf(subject.Relation).Contains(userset)) {
            AuthorizationMetrics.RecordIndexAnswer();
            return true;
        }

        // Then the userset's side, which is the only one that can say no.
        var usersetSlice = await SliceAsync(userset.Object, cancellationToken).ConfigureAwait(false);
        if (usersetSlice.IsFailure || usersetSlice.GetValueOrThrow().Snapshot is not { } usersetSnapshot) {
            return null;
        }

        var members = usersetSnapshot.MembersOf(userset.Relation);
        if (members.Contains(subject)) {
            AuthorizationMetrics.RecordIndexAnswer();
            return true;
        }

        if (!IsComplete(members)) {
            return null;
        }

        AuthorizationMetrics.RecordIndexAnswer();
        return false;
    }

    /// <summary>
    ///     Whether a closure's every userset member was expanded — see the remarks on this type.
    /// </summary>
    /// <param name="members">The closed members of one userset.</param>
    public bool IsComplete(IReadOnlyList<SubjectRef> members) {
        ArgumentNullException.ThrowIfNull(members);

        foreach (var member in members) {
            if (!member.IsUserset || IsIndexed(member.Type, member.Relation)) {
                continue;
            }

            if (schema.Member(member.Type, member.Relation) is not null) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A slice as read, with <see cref="Slice.Snapshot" /> null for one stamped with another schema version.</summary>
    async ValueTask<Result<Slice>> SliceAsync(ObjectRef subjectObject, CancellationToken cancellationToken) {
        if (slices.TryGetValue(subjectObject, out var cached)) {
            return Result<Slice>.Success(new(cached));
        }

        Reads++;

        var read = await store.ReadAsync(subjectObject, cancellationToken).ConfigureAwait(false);
        if (read.TryGetError(out var error)) {
            return Result<Slice>.Failure(error);
        }

        var snapshot = read.GetValueOrThrow();
        var usable = snapshot.SchemaVersion == 0 || snapshot.SchemaVersion == schema.Version ? snapshot : null;

        slices[subjectObject] = usable;
        return Result<Slice>.Success(new(usable));
    }

    readonly record struct Slice(MembershipIndexSnapshot? Snapshot);
}
