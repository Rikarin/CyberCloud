using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     Where <see cref="ListObjectsEvaluator" /> gets the reverse index from — docs/plan/07
///     § Storage, row 2, seen from the walk.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A second interface rather than a second method on <see cref="IRelationReader" />,
///         and the split is the asymmetry docs/plan/07 § Storage is built on.</b>
///         <c>CheckEvaluator</c> takes an <see cref="IRelationReader" /> and nothing else, so the
///         check path <i>cannot</i> reach the reverse index — "the direction that can be stale is
///         the one where staleness is a performance bug, not a security bug" is true by
///         construction only while the two readers stay two types. <see cref="ListObjectsEvaluator" />
///         takes both, because a listing starts from the reverse index and verifies against the
///         forward one.
///     </para>
///     <para>
///         ⚠ <b>This is also the seam the Leopard index will stand behind.</b> docs/plan/07 § The
///         Leopard index materializes, per userset, the transitively closed set of subjects in it;
///         the reverse of that — per subject, every userset it is in, closed — is what a walk would
///         read here instead of hopping the userset graph one grain at a time. A reader that
///         answered a subject's closure in one read would satisfy this interface unchanged; the
///         walk's userset hop (<c>ListObjectsEvaluator.ExpandAsync</c>) is the only code that
///         would stop being needed. Nothing implements that reader today, and no seam is declared
///         for it beyond this one, because an interface nothing implements is a promise nothing
///         keeps.
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
}
