using CyberCloud.Authorization.Contracts;

namespace CyberCloud.Authorization;

/// <summary>
///     The seam between the two halves of the non-transactional tuple write.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This exists so that docs/plan/07 § Storage's central safety claim can be
///             <i>tested</i>, not merely believed.
///         </b>
///         That claim is: "A subject index missing an entry
///         costs a <c>ListObjects</c> a miss, not a <c>Check</c> an incorrect answer, because
///         <c>Check</c> walks forward from the object.
///         <b>
///             That asymmetry is deliberate: the
///             direction that can be stale is the one where staleness is a performance bug, not a
///             security bug.
///         </b>
///         "
///     </para>
///     <para>
///         The only honest way to check it is to have the write actually die between the object
///         write and the subject write and then look at what <c>Check</c> says. A test cannot pull a
///         plug, and reaching the post-crash state by hand would be testing a state we constructed
///         rather than one the code can reach. So the store grain awaits this between the two
///         halves; production registers <see cref="NoRelationWriteInterceptor" />, which does
///         nothing, and <c>TwoGrainWriteTests</c> registers one that throws.
///     </para>
///     <para>
///         It is a seam and it is declared as one. It has exactly one call site, it is not a general
///         extension point, and it must never grow the ability to change the tuple.
///     </para>
/// </remarks>
public interface IRelationWriteInterceptor {
    /// <summary>
    ///     Called after the object-relations half of a write has been persisted and before the
    ///     subject-index half is attempted.
    /// </summary>
    /// <param name="tuple">The tuple. Informational; it cannot be changed.</param>
    /// <param name="isDelete">Whether the write is a delete.</param>
    ValueTask AfterObjectWriteAsync(RelationTuple tuple, bool isDelete);
}

/// <summary>The production interceptor: nothing happens between the two halves.</summary>
public sealed class NoRelationWriteInterceptor : IRelationWriteInterceptor {
    /// <summary>The single instance.</summary>
    public static NoRelationWriteInterceptor Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask AfterObjectWriteAsync(RelationTuple tuple, bool isDelete) => ValueTask.CompletedTask;
}
