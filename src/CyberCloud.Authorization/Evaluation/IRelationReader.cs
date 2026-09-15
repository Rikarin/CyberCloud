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
///     The Leopard membership index, as <c>Check</c> consults it — docs/plan/07 § Check, step 3:
///     "test membership via <c>IMembershipIndexGrain</c> if the userset is indexed, otherwise
///     recurse".
/// </summary>
/// <remarks>
///     <para>
///         <see cref="MembershipIndexReader" /> is the implementation a silo runs, over the slices
///         <c>IMembershipIndexGrain</c> holds; <see cref="NoMembershipIndex" /> is what an
///         evaluator gets when nobody hands it one, and what <c>CheckGrain</c> hands it for a
///         <c>FullyConsistent</c> check, whose contract is the durable rows and nothing derived
///         from them.
///     </para>
///     <para>
///         ⚠ <b>A <c>false</c> from this interface is taken without a walk, so it must be a
///         <c>false</c> the walk would have reached.</b> § Staleness's "always verifiable, never an
///         authority" is met differently from the way the document sketches: the index is not
///         behind a token, because the store writes it before the version moves, so there is no
///         version to compare. What is compared instead is completeness — a closure that recorded
///         a userset it could not expand answers <see langword="null" /> rather than
///         <c>false</c>. See <see cref="MembershipIndexReader" /> for the rule, and
///         <c>CheckPropertyTests.CheckAgreesWithTheReferenceEvaluatorThroughTheLeopardIndex</c>
///         for the twenty thousand graphs that hold the indexed evaluator to the reference one.
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

/// <summary>
///     No index: always answers "walk it". The evaluator's default, and what a
///     <c>FullyConsistent</c> check runs with. Until issue #37 it was also what every silo ran.
/// </summary>
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
