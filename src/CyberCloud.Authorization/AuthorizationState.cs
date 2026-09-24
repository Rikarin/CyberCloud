using CyberCloud.Authorization.Contracts;

namespace CyberCloud.Authorization;

/// <summary>
///     <c>IObjectRelationsGrain</c>'s durable record — every tuple whose object is this one.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         Every collection member in this file is <c>{ get; set; }</c> and that is load-bearing,
///         not style.
///     </b>
///     The durable tier serialises state with <c>System.Text.Json</c>
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
public sealed class ObjectRelationsState {
    /// <summary>Relation → the subjects that hold it directly.</summary>
    [Id(0)]
    public Dictionary<string, List<SubjectRef>> ByRelation { get; set; } = [];

    /// <summary>
    ///     <c>relation@subject</c> (<c>TupleExpiry.Key</c>) → when that tuple stops granting. A tuple
    ///     with no entry is permanent.
    /// </summary>
    /// <remarks>
    ///     ⚠ A second dictionary beside <see cref="ByRelation" /> rather than a richer element in
    ///     it, so a row written before issue #49 reads back unchanged — every tuple in it permanent —
    ///     with no migration. An expired tuple stays here until the store's sweep deletes it; the
    ///     grain hides it from every read from the instant it expires.
    /// </remarks>
    [Id(1)]
    public Dictionary<string, DateTimeOffset> Expiries { get; set; } = [];
}

/// <summary><c>ISubjectRelationsGrain</c>'s durable record — the reverse index.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.SubjectRelations")]
public sealed class SubjectRelationsState {
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
public sealed class PendingWrite {
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
public sealed class TupleStoreState {
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

    /// <summary>
    ///     Every tuple the store last wrote with an expiry and hasn't since deleted or rewritten
    ///     without one — what the expiry sweep walks. docs/plan/07 § Time-bounded relations.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Complete because the store is the tenant's one writer.</b> Grains can't be scanned,
    ///     so a sweep that searched the forward index for expired tuples would need an enumeration
    ///     that doesn't exist; the register is that enumeration, kept by the only component that
    ///     writes tuples, in the same durable write that clears the journal entry. A tuple written
    ///     straight into <c>IObjectRelationsGrain</c> is missing here — which is one more reason
    ///     that interface's remarks forbid doing it.
    /// </remarks>
    [Id(3)]
    public List<RelationTuple> Expiring { get; set; } = [];

    /// <summary>
    ///     The ends of grants that a rewrite brought closer, which the check cache must honour —
    ///     <see cref="CacheFence" />'s remarks.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Durable, for <see cref="Version" />'s reason.</b> The cached answers a fence retires
    ///     live in the hot tier and outlive this activation, so a fence that a deactivation forgot
    ///     would let them be served past the grant's new end. Every fence that has taken effect is
    ///     folded into one, so the list holds one fence per shortened grant still running, plus one.
    /// </remarks>
    [Id(4)]
    public List<CacheFence> Fences { get; set; } = [];
}

/// <summary>One cached check answer.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.CheckCacheEntry")]
public sealed class CheckCacheEntry {
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

    /// <summary>
    ///     The instant the answer stops being served, or <see langword="null" /> when no expiring
    ///     tuple bears on it — <c>CheckResult.ValidUntil</c> as it was computed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A memoised allow must never outlive the earliest expiry among the tuples that proved
    ///     it</b> — docs/plan/07 § Time-bounded relations. The relation version can't enforce that,
    ///     because an expiry moves no version, so the entry carries the instant itself and the cache
    ///     compares it against the clock on every hit, in every mode.
    /// </remarks>
    [Id(3)]
    public DateTimeOffset? ValidUntil { get; set; }
}

/// <summary><c>ICheckGrain</c>'s hot-tier record — the check cache for one object.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.CheckCache")]
public sealed class CheckCacheState {
    /// <summary>
    ///     <c>permission|subject</c> → the answer. The tenant and the object are the grain's
    ///     identity, so the two remaining components of docs/plan/07 § Caching across requests'
    ///     cache key are all that is left to spell out here.
    /// </summary>
    [Id(0)]
    public Dictionary<string, CheckCacheEntry> Entries { get; set; } = [];
}

/// <summary>
///     <c>IMembershipIndexGrain</c>'s durable record — one subject object's slice of the Leopard
///     index, both directions. docs/plan/07 § The Leopard index.
/// </summary>
/// <remarks>
///     Plain lists rather than the document's roaring bitmaps over a per-tenant subject dictionary:
///     the closure of a group with ten thousand members is ten thousand <see cref="SubjectRef" />s
///     in one row, which is what the durable tier's JSON can hold and is far from what it can
///     hold well. The bitmap, and the dictionary it needs, are recorded as owed in docs/plan/07
///     § The Leopard index rather than built here, because the first thing to learn is whether
///     the closure is right and the second is how big it gets.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.State.MembershipIndex")]
public sealed class MembershipIndexState {
    /// <summary>
    ///     The schema version both closures were computed under; <c>0</c> until the first write.
    ///     See <see cref="MembershipIndexSnapshot.SchemaVersion" />.
    /// </summary>
    [Id(0)]
    public int SchemaVersion { get; set; }

    /// <summary>Relation → the closed members of the userset <c>{self}#{relation}</c>.</summary>
    [Id(1)]
    public Dictionary<string, List<SubjectRef>> Members { get; set; } = [];

    /// <summary>
    ///     Subject relation (empty for the concrete object) → every userset that subject is
    ///     closed into.
    /// </summary>
    [Id(2)]
    public Dictionary<string, List<SubjectRef>> Usersets { get; set; } = [];

    /// <summary>
    ///     The relations whose closure leaves out an expiring edge. See
    ///     <see cref="MembershipIndexSnapshot.Unclosed" />.
    /// </summary>
    [Id(3)]
    public List<string> Unclosed { get; set; } = [];
}
