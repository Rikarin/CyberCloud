using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Authorization.Evaluation;

/// <summary>
///     Where <see cref="CheckEvaluator" /> gets tuples from.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Only the forward direction exists here, and that is the point of docs/plan/07
///             § Storage's asymmetry.
///         </b>
///         <c>Check</c> walks forward from the object, so the evaluator
///         can only ever read <c>IObjectRelationsGrain</c>. There is deliberately no way for it to
///         reach the reverse index, which is what makes "a subject index missing an entry costs a
///         <c>ListObjects</c> a miss, not a <c>Check</c> an incorrect answer" true by construction
///         rather than by care.
///     </para>
///     <para>
///         The interface exists so the evaluator can be exercised against an in-memory tuple set —
///         which is what makes the 20 000-graph property test against a reference evaluator
///         affordable — while production runs the same evaluator over real grains.
///         <b>The evaluator under test is the evaluator that ships</b>; only the tuple source
///         differs.
///     </para>
/// </remarks>
public interface IRelationReader {
    /// <summary>Every tuple whose object is <paramref name="target" />.</summary>
    /// <param name="target">The object. Named `target` only because CA1716 forbids `object` on an interface member.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<Result<ObjectRelationsSnapshot>> ReadAsync(
        ObjectRef target,
        CancellationToken cancellationToken
    );
}

/// <summary>
///     The M2 seam for the Leopard membership index — docs/plan/07 § The Leopard index.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>STUBBED IN M1, deliberately and completely.</b> docs/plan/07 § Effort and sequencing
///         puts <c>IMembershipIndexGrain</c> and its rebuilder at M2, and says M1 is viable without
///         it "because M1 tenants are small: a walk at depth ≤ 4 over ≤ 100 members is single-digit
///         milliseconds".
///     </para>
///     <para>
///         The seam is declared now because § Check step 3 branches on it — "test membership via
///         <c>IMembershipIndexGrain</c> if the userset is indexed, otherwise recurse" — and a branch
///         that does not exist is a branch nobody remembers to add. <see cref="NoMembershipIndex" />
///         answers "not indexed" for every userset, so the walk is always taken and the answer is
///         always the authoritative one.
///     </para>
///     <para>
///         ⚠ When the real index lands it must obey § Staleness: it is "a fast path that is always
///         verifiable, never an authority", so <see cref="TryTestMembershipAsync" /> must return
///         <see langword="null" /> — meaning "walk it" — whenever the index's version is behind the
///         token a check was given. Nothing in M1 can test that, because nothing in M1 has a version.
///     </para>
/// </remarks>
public interface IMembershipIndex {
    /// <summary>
    ///     Whether <paramref name="subject" /> is in the userset, or <see langword="null" /> when
    ///     the userset is not indexed (or the index is behind) and the walk must be taken.
    /// </summary>
    /// <param name="userset">The userset — <c>group:eng#member</c>.</param>
    /// <param name="subject">The subject being tested.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<bool?> TryTestMembershipAsync(
        SubjectRef userset,
        SubjectRef subject,
        CancellationToken cancellationToken
    );
}

/// <summary>The M1 membership index: there isn't one. Always answers "walk it".</summary>
public sealed class NoMembershipIndex : IMembershipIndex {
    /// <summary>The single instance.</summary>
    public static NoMembershipIndex Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<bool?> TryTestMembershipAsync(
        SubjectRef userset,
        SubjectRef subject,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<bool?>(null);
}
