using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     Where <see cref="ListObjectsEvaluator" /> gets the reverse index and the Leopard index from —
///     docs/plan/07 § Storage, rows 2 and 3, seen from the walk.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A second interface rather than a second method on <see cref="IRelationReader" />,
///             and the split is the asymmetry docs/plan/07 § Storage is built on.
///         </b>
///         <c>CheckEvaluator</c> takes an <see cref="IRelationReader" /> and nothing else, so the
///         check path <i>cannot</i> reach the reverse index — "the direction that can be stale is
///         the one where staleness is a performance bug, not a security bug" is true by
///         construction only while the two readers stay two types. <see cref="ListObjectsEvaluator" />
///         takes both, because a listing starts from the reverse index and verifies against the
///         forward one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Two methods, because the Leopard index answers a different question from the
///             reverse index and the walk asks both.
///         </b> <see cref="ReadAsync" /> is the record: every
///         tuple naming the subject object, which the tupleset rule needs verbatim — a child's
///         <c>parent</c> tuple is an entry here and nowhere else. <see cref="ReadUsersetsAsync" />
///         is the closure: every userset the subject is transitively in, in one read, which is the
///         hop-per-level the walk used to pay for nested groups. The two were once going to be one
///         method — "a reader that answered a subject's closure in one read would satisfy this
///         interface unchanged" — and could not be: an entry carries no subject, so a closed entry
///         <c>c#parent@group:platform#member</c> returned for <c>group:eng</c> would read as
///         <c>eng</c> being <c>c</c>'s parent.
///     </para>
/// </remarks>
public interface IReverseRelationReader {
    /// <summary>
    ///     Every tuple whose subject's <b>object half</b> is <paramref name="subjectObject" /> —
    ///     both the entries for <c>type:id</c> and the ones for <c>type:id#relation</c>, told apart
    ///     by <see cref="SubjectIndexEntry.SubjectRelation" />.
    /// </summary>
    /// <param name="subjectObject">The object half of the subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<Result<IReadOnlyList<SubjectIndexEntry>>> ReadAsync(
        ObjectRef subjectObject,
        CancellationToken cancellationToken
    );

    /// <summary>
    ///     Every userset <paramref name="subject" /> is transitively in through direct-only
    ///     relations — the Leopard index, read up from the subject. Empty when the subject is in
    ///     none, or when the index has nothing usable for it; a walk that gets an empty answer
    ///     finds the same usersets one hop at a time.
    /// </summary>
    /// <param name="subject">The subject, concrete or a userset.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<Result<IReadOnlyList<SubjectRef>>> ReadUsersetsAsync(
        SubjectRef subject,
        CancellationToken cancellationToken
    );
}
