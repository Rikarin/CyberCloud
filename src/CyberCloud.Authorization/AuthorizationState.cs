using CyberCloud.Authorization.Contracts;

namespace CyberCloud.Authorization;

/// <summary>
///     <c>IObjectRelationsGrain</c>'s durable record — every tuple whose object is this one.
/// </summary>
/// <remarks>
///     ⚠ <b>Every collection member in this file is <c>{ get; set; }</c> and that is load-bearing,
///     not style.</b> The durable tier serialises state with <c>System.Text.Json</c>
///     (docs/plan/05 § Serialization). <c>System.Text.Json</c> <i>writes</i> a get-only collection
///     property and then, on read, <b>does not populate it</b> — the payload in PostgreSQL is
///     correct and the grain comes back empty, silently. <c>CyberCloud.Tenancy</c> already lost a
///     tenant's subscriptions to exactly that. For an authorization store the same bug is every
///     tuple in a tenant vanishing across a deactivation, which is a platform-wide outage that
///     looks like a permissions problem.
///     <para>
///         For the same reason there is no <c>SortedSet</c> and no comparer-carrying
///         <c>HashSet</c>: <c>System.Text.Json</c> reconstructs those with the <i>default</i>
///         comparer. The dictionaries below are keyed by <c>string</c> and use the default
///         comparer deliberately, which for <c>string</c> <i>is</i> ordinal — so what comes back is
///         what went in.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.ObjectRelations")]
public sealed class ObjectRelationsState
{
    /// <summary>Relation → the subjects that hold it directly.</summary>
    [Id(0)]
    public Dictionary<string, List<SubjectRef>> ByRelation { get; set; } = [];
}

/// <summary><c>ISubjectRelationsGrain</c>'s durable record — the reverse index.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.SubjectRelations")]
public sealed class SubjectRelationsState
{
    /// <summary>Every tuple this subject appears in.</summary>
    [Id(0)]
    public List<SubjectIndexEntry> Entries { get; set; } = [];
}

/// <summary>
///     One journalled tuple write whose two halves have not both landed yet — docs/plan/07
///     § Storage's "reconciled by a sweeper", made reconcilable.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.PendingWrite")]
public sealed class PendingWrite
{
    /// <summary>The tuple.</summary>
    [Id(0)]
    public RelationTuple Tuple { get; set; } = new();

    /// <summary>Whether it is a delete rather than a write.</summary>
    [Id(1)]
    public bool IsDelete { get; set; }

    /// <summary>A monotonic sequence number, so a replay is ordered.</summary>
    [Id(2)]
    public long Sequence { get; set; }
}

/// <summary><c>ITupleStoreGrain</c>'s durable record.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.TupleStore")]
public sealed class TupleStoreState
{
    /// <summary>
    ///     The tenant's relation version — the monotonic number a <c>ConsistencyToken</c> carries.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Durable, not Hot.</b> docs/plan/05 § Hot lists the ReBAC <i>check cache</i> in the
    ///     hot tier, not this. A version that reset to zero after a Redis flush would make every
    ///     outstanding token appear to be from the future, so <c>AtLeastAsFresh</c> would either
    ///     never be satisfiable or — worse, depending on the comparison — always be.
    /// </remarks>
    [Id(0)]
    public long Version { get; set; }

    /// <summary>Writes journalled but not yet reconciled.</summary>
    [Id(1)]
    public List<PendingWrite> Pending { get; set; } = [];

    /// <summary>The next journal sequence number.</summary>
    [Id(2)]
    public long NextSequence { get; set; }
}

/// <summary>One cached check answer.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.CheckCacheEntry")]
public sealed class CheckCacheEntry
{
    /// <summary>The decision.</summary>
    [Id(0)]
    public bool Allowed { get; set; }

    /// <summary>
    ///     The tenant relation version this answer was computed at — the <i>stamp</i>, not part of
    ///     the lookup key. See <c>ConsistencyMode</c> for why that distinction is the whole of
    ///     docs/plan/07's consistency story.
    /// </summary>
    [Id(1)]
    public long Version { get; set; }

    /// <summary>The schema version it was computed under.</summary>
    [Id(2)]
    public int SchemaVersion { get; set; }
}

/// <summary><c>ICheckGrain</c>'s hot-tier record — the check cache for one object.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.CheckCache")]
public sealed class CheckCacheState
{
    /// <summary>
    ///     <c>permission|subject</c> → the answer. The tenant and the object are the grain's
    ///     identity, so the two remaining components of docs/plan/07 § Caching across requests'
    ///     cache key are all that is left to spell out here.
    /// </summary>
    [Id(0)]
    public Dictionary<string, CheckCacheEntry> Entries { get; set; } = [];
}
