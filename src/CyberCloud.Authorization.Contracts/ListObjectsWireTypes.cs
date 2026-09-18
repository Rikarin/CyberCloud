using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     What a <c>ListObjects</c> concluded, beyond the objects it returned. ⚠
///     <b>
///         Three outcomes,
///         not one, for the reason <see cref="CheckOutcome" /> has four.
///     </b>
/// </summary>
/// <remarks>
///     A walk that hit its cap did not compute an answer. Returning the part it had found would be
///     an under-approximation — safe as a filter, since it never names an object the caller may not
///     see, and silently wrong as a listing, since it hides ones they may. So a capped walk returns
///     <b>no objects</b> and says which cap it hit, and the caller decides what to do with a subject
///     whose reach is that wide. <c>ReBacResourceAuthorizer</c> falls back to a <c>Check</c> per
///     member; a portal would page the resource-graph projection docs/plan/07 § ListObjects says is
///     the fast list.
///     <para>
///         ⚠ <b>There is no breadth outcome, and there was one for a day.</b> <c>Check</c>'s
///         breadth cap is per node — the usersets it will expand on one object — and the walk
///         mirrors it where it crosses that node by not reaching what <c>Check</c> would cut,
///         the way it already handles the depth cap: the page stays exact and complete.
///         A value <c>4</c> that capped something else — the objects one userset is granted on —
///         shipped on the issue #37 branch, was reviewed out before it merged, and is burned here
///         rather than in <c>build/wire</c> because it never crossed a released boundary.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.ListObjectsOutcome")]
public enum ListObjectsOutcome {
    /// <summary>Never assigned. Not an outcome — see <c>Result</c>'s <c>default(T)</c> argument.</summary>
    Unknown = 0,

    /// <summary>The walk completed within every cap. The page is exact.</summary>
    Complete = 1,

    /// <summary>
    ///     The walk reached more objects than <c>AuthorizationLimits.MaxListObjects</c> allows.
    ///     <b>No objects are returned.</b>
    /// </summary>
    ObjectCapExceeded = 2,

    /// <summary>
    ///     The walk needed more hops from the subject than the depth cap allows.
    ///     <b>
    ///         No objects are
    ///         returned
    ///     </b>, because an object past the cap is one <c>Check</c> would deny and the ones
    ///     before it may depend on it.
    /// </summary>
    DepthCapExceeded = 3
}

/// <summary>
///     The question docs/plan/07 § ListObjects asks — "which objects of this type may this subject
///     <c>permission</c>" — with the two bounds that section requires and one it does not name.
/// </summary>
/// <remarks>
///     <para>
///         The subject is the grain's key and is not repeated here, for the reason
///         <see cref="ICheckGrain" /> gives about the object: a request that named one subject and
///         was routed to another would answer about the wrong caller. Only the subject's
///         <i>userset relation</i> travels, because <c>GrainKeys.ListObjects</c> carries the object
///         half alone.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Within" /> is the bound the document does not name, and it is what makes
///             the resource list affordable.
///         </b> § ListObjects warns that serving a list page straight
///         from an unscoped walk "is the single most likely performance mistake in this subsystem":
///         a subscription owner's reach is every resource in the subscription, and a listing of one
///         group would walk all of them to keep ten. <see cref="Within" /> restricts the answer to
///         objects at or below one object in the tupleset hierarchy — the <c>parent</c> chain — and,
///         more to the point, restricts the <i>walk</i>: descending from an ancestor of
///         <see cref="Within" /> follows only the child on the chain toward it, and descending below
///         <see cref="Within" /> stops at <see cref="WithinDepth" />. A group listing passes the
///         group and depth 1, and the walk reads the group's reverse index once rather than a grain
///         per member.
///     </para>
///     <para>
///         ⚠ <b>Scoping makes two assumptions, both stated, both failing in the safe direction.</b>
///         First, the chain is a chain: an object with two <c>parent</c> tuples is reached through
///         whichever one lies under <see cref="Within" />, and a grant that reaches it only through
///         the other is not found — the same reading <c>CheckGrain.WalkAncestorsAsync</c> takes
///         ("one parent. A second is a data error"). Second, no userset is formed on an object the
///         pruned walk never expands: a tuple such as <c>resource:x#reader@resource:y#owner</c>,
///         with <c>y</c> outside the scope's chain or at the requested depth, carries a grant the
///         walk cannot see, because <c>y</c> is exactly what the pruning declined to read. On
///         <c>CyberCloudSchema</c> the only userset the platform writes is <c>group:G#member</c>,
///         and a group has no <c>parent</c>, so it is reached by its own tuples and never by
///         descent. Either failure hides an object; neither can show one. Unscoped requests make
///         no assumption and are exact, and <c>ListObjectsPropertyTests</c> holds the scoped walk
///         to a subset of the exact answer always and to equality whenever both assumptions hold.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.ListObjectsRequest")]
public sealed record ListObjectsRequest {
    /// <summary>The largest page a caller may ask for. Anything above is clamped, not refused.</summary>
    public const int MaxPageSize = 1_000;

    /// <summary>The page size when <see cref="PageSize" /> is left at zero.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The object type to list — <c>resource</c>, <c>resourceGroup</c>.</summary>
    [Id(0)]
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>A permission or relation name defined on <see cref="ObjectType" />.</summary>
    [Id(1)]
    public string Permission { get; init; } = string.Empty;

    /// <summary>
    ///     The subject's userset relation, or empty for a concrete subject. <c>member</c> to list
    ///     what <c>group:eng#member</c> may do; empty to list what <c>user:alice</c> may.
    /// </summary>
    [Id(2)]
    public string SubjectRelation { get; init; } = string.Empty;

    /// <summary>
    ///     Only objects at or below this one in the tupleset hierarchy, or <see langword="null" />
    ///     for every object in the tenant. See the remarks on this type.
    /// </summary>
    [Id(3)]
    public ObjectRef? Within { get; init; }

    /// <summary>
    ///     How many tupleset hops below <see cref="Within" /> to include: <c>0</c> for
    ///     <see cref="Within" /> itself, <c>1</c> for its direct children, and so on.
    ///     <see langword="null" /> means every descendant. Ignored when <see cref="Within" /> is
    ///     <see langword="null" />.
    /// </summary>
    [Id(4)]
    public int? WithinDepth { get; init; }

    /// <summary>
    ///     How many objects one page holds. Zero means <see cref="DefaultPageSize" />; more than
    ///     <see cref="MaxPageSize" /> is clamped to it.
    /// </summary>
    [Id(5)]
    public int PageSize { get; init; }

    /// <summary>
    ///     Resume after this object id, ordinally. Empty starts from the beginning; the value to pass
    ///     is <see cref="ListObjectsPage.Continuation" />.
    /// </summary>
    [Id(6)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>The page size after the clamp — what the grain uses.</summary>
    public int EffectivePageSize =>
        PageSize <= 0 ? DefaultPageSize
        : PageSize > MaxPageSize ? MaxPageSize
        : PageSize;
}

/// <summary>One page of a <c>ListObjects</c> answer, and how it was reached.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.ListObjectsPage")]
public sealed record ListObjectsPage {
    /// <summary>
    ///     The objects on this page, ordered by id, ordinally.
    ///     <b>
    ///         Empty whenever
    ///         <see cref="Outcome" /> is not <see cref="ListObjectsOutcome.Complete" />
    ///     </b>.
    /// </summary>
    [Id(0)]
    public IReadOnlyList<ObjectRef> Objects { get; init; } = [];

    /// <summary>
    ///     The last id on this page when there is another page, or empty when there is not. Pass
    ///     it back as <see cref="ListObjectsRequest.Continuation" />.
    /// </summary>
    [Id(1)]
    public string Continuation { get; init; } = string.Empty;

    /// <summary>Complete, or which cap stopped the walk.</summary>
    [Id(2)]
    public ListObjectsOutcome Outcome { get; init; } = ListObjectsOutcome.Unknown;

    /// <summary>Where a cap was hit, in words, or empty.</summary>
    [Id(3)]
    public string CapDetail { get; init; } = string.Empty;

    /// <summary>The tenant relation version this answer reflects.</summary>
    [Id(4)]
    // See the note on Consistency.Token: a zookie is a version, not a credential.
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification = "A ConsistencyToken is Zanzibar's zookie — a public {tenantId}.{version} "
            + "pair, not a credential. docs/plan/07 § Consistency."
    )]
    public ConsistencyToken Token { get; init; } = new();

    /// <summary>How many <c>(object, name)</c> pairs the walk reached, across every type.</summary>
    [Id(5)]
    public int PairsReached { get; init; }

    /// <summary>How many reverse-index reads the walk made.</summary>
    [Id(6)]
    public int ReverseReads { get; init; }

    /// <summary>How many forward reads the walk made — scope resolution and verification.</summary>
    [Id(7)]
    public int ForwardReads { get; init; }

    /// <summary>
    ///     Whether the candidates were re-checked forward before being returned. True only when
    ///     the rewrites the walk crossed contain an intersection or an exclusion — see
    ///     <c>ListObjectsEvaluator</c>.
    /// </summary>
    [Id(8)]
    public bool Verified { get; init; }

    /// <summary>
    ///     How many Leopard-index reads the walk made — one for the subject's closed usersets, and
    ///     one more for each direct-only userset the walk reached by computation rather than
    ///     through the index. docs/plan/07 § The Leopard index.
    /// </summary>
    [Id(9)]
    public int IndexReads { get; init; }

    /// <summary>Whether there is another page.</summary>
    public bool HasMore => Continuation.Length > 0;
}
