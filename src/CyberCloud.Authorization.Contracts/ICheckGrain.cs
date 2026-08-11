using CyberCloud.Core;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     <c>Check</c> — docs/plan/07 § Check. The one question the platform asks about authorization.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Hot · <b>Key</b> <c>rel/check/{type}/{id}</c>,
///         tenant-qualified, and the object is the object being checked.
///     </para>
///     <para>
///         ⚠ <b>Hot, because all this grain owns is a cache.</b> docs/plan/05 § Hot lists "ReBAC
///         check cache" among what the hot tier holds, and losing it costs a walk, not an answer.
///         The tuples it walks are Durable and live in <see cref="IObjectRelationsGrain" />.
///     </para>
///     <para>
///         ⚠ <b>The signature is not quite docs/plan/07 § Check's.</b> The document writes
///         <c>
///             CheckAsync(ObjectRef @object, string permission, SubjectRef subject,
///             ConsistencyToken? token)
///         </c>
///         — a free function with the object as a parameter and
///         consistency expressed as a nullable token. Two changes, both forced:
///     </para>
///     <list type="bullet">
///         <item>
///             The object is the <b>grain key</b>, so passing it again would allow a call to name
///             one object and be routed to another.
///         </item>
///         <item>
///             A nullable token cannot express three modes. <c>null</c> would have to mean both
///             "any cached result" and "bypass every cache", and § Consistency's own table needs
///             those to be different. <see cref="Consistency" /> carries the mode and the token.
///         </item>
///     </list>
/// </remarks>
[Alias("CyberCloud.Authorization.ICheckGrain")]
public interface ICheckGrain : IGrainWithStringKey {
    /// <summary>
    ///     Whether <paramref name="subject" /> has <paramref name="permission" /> on this object.
    /// </summary>
    /// <param name="permission">A permission or relation name in the schema.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="consistency">
    ///     How fresh the answer has to be. Defaults to
    ///     <see cref="ConsistencyMode.MinimizeLatency" /> when null.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Evaluation is the bounded, memoized search of docs/plan/07 § Check: expand the
    ///         permission, test direct tuples, recurse into usersets and <c>From</c> targets,
    ///         short-circuit on the first true, depth cap 12, breadth cap 1 000.
    ///     </para>
    ///     <para>
    ///         ⚠ A <c>Result</c> failure here is <b>not</b> a denial. It means the question was not
    ///         answerable — <see cref="ErrorCode.SchemaInvalid" /> for a permission the schema does
    ///         not define. A denial is a successful <see cref="CheckResult" /> with
    ///         <see cref="CheckResult.Allowed" /> false.
    ///     </para>
    /// </remarks>
    Task<Result<CheckResult>> CheckAsync(
        string permission,
        SubjectRef subject,
        Consistency? consistency
    );

    /// <summary>
    ///     The Azure-shaped role assignments visible at this scope, direct and inherited —
    ///     docs/plan/07 § Azure RBAC, expressed in it.
    /// </summary>
    /// <param name="includeInherited">
    ///     Whether to walk the <c>parent</c> chain. ⚠ When true, the assignments that come back with
    ///     <c>Inherited</c> set have <b>no tuple at this scope</b> — that is the whole claim of the
    ///     document's third table row and it is what <c>RoleAssignmentViewTests</c> asserts.
    /// </param>
    Task<Result<IReadOnlyList<RoleAssignment>>> ListRoleAssignmentsAsync(bool includeInherited);

    /// <summary>How many entries are in this object's hot-tier check cache.</summary>
    /// <remarks>
    ///     Present so that "a tuple write invalidates the tenant's whole check cache" (docs/plan/07
    ///     § Caching across requests) is observable rather than asserted —
    ///     <c>CheckCacheInvalidationTests</c> reads it before and after.
    /// </remarks>
    Task<Result<int>> CachedEntryCountAsync();

    /// <summary>Drops this activation, and with it the in-memory half of the cache.</summary>
    Task DeactivateAsync();
}
