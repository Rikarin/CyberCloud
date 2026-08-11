using CyberCloud.Tenancy.Contracts;

namespace CyberCloud.Tenancy;

/// <summary>
///     The tenant grain's durable record.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         Every collection member below is <c>{ get; set; }</c> and that is load-bearing, not
///         style.
///     </b>
///     The durable tier serialises state with <c>System.Text.Json</c>
///     (<c>SystemTextJsonGrainStorageSerializer</c> — docs/plan/05 § Serialization chose JSON so that
///     year-two questions are answerable with <c>psql</c>). <c>System.Text.Json</c> <b>writes</b> a
///     get-only collection property and then, on read, <b>does not populate it</b>: the payload in
///     PostgreSQL is correct and the grain comes back with an empty list, silently. Observed here as
///     a tenant whose subscriptions vanished across a deactivation while the JSON in the row still
///     listed them. Orleans' binary serializer does not have this behaviour, which is why the
///     <c>[Id(n)]</c> discipline alone does not catch it and why
///     <c>TenancyStateContractTests</c> is not the whole gate — the round-trip-through-real-storage
///     tests are.
///     <para>
///         For the same reason there is no <c>SortedSet</c> and no comparer-carrying <c>HashSet</c>
///         here: <c>System.Text.Json</c> reconstructs those with the <i>default</i> comparer, so an
///         ordinal set would silently become a culture-sensitive one across a restart.
///     </para>
///     <para>
///         Every type in this file is grain state, so every one of them obeys docs/plan/05
///         § Serialization and schema evolution: a stable <c>[Alias]</c>, explicit <c>[Id(n)]</c> on every
///         member, numbers never reused and never reordered. <c>TenancyStateContractTests</c> is the
///         gate, and it is the same gate <c>WireContractTests</c> applies to
///         <c>CyberCloud.Core.Contracts</c> — state in PostgreSQL outlives a deploy exactly as a wire
///         payload outlives one silo.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.Tenant")]
public sealed class TenantState {
    /// <summary>The tenant's record, or <see langword="null" /> before creation.</summary>
    [Id(0)]
    public TenantDescriptor? Descriptor { get; set; }

    /// <summary>The tenant's subscriptions.</summary>
    [Id(1)]
    public List<Guid> Subscriptions { get; set; } = [];

    /// <summary>Why the status last changed, for the audit trail.</summary>
    [Id(2)]
    public string LastStatusReason { get; set; } = string.Empty;
}

/// <summary>The subscription grain's durable record.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.Subscription")]
public sealed class SubscriptionState {
    /// <summary>The subscription's record, or <see langword="null" /> before creation.</summary>
    [Id(0)]
    public SubscriptionDescriptor? Descriptor { get; set; }

    /// <summary>The resource group names in this subscription, kept sorted ordinally.</summary>
    [Id(1)]
    public List<string> ResourceGroups { get; set; } = [];
}

/// <summary>The resource group grain's durable record.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.ResourceGroup")]
public sealed class ResourceGroupState {
    /// <summary>The group's record, or <see langword="null" /> before creation.</summary>
    [Id(0)]
    public ResourceGroupDescriptor? Descriptor { get; set; }

    /// <summary>
    ///     The members, by resource id. ⚠ Members in <c>Deleting</c> are in here and stay in here
    ///     until teardown succeeds — docs/plan/06 § Two-phase create.
    /// </summary>
    [Id(1)]
    public Dictionary<Guid, ResourceGroupMember> Members { get; set; } = [];

    /// <summary>When each member entered <c>Creating</c>, for the orphan sweep.</summary>
    [Id(2)]
    public Dictionary<Guid, DateTimeOffset> CreatingSince { get; set; } = [];
}

/// <summary>
///     Either index grain's durable record. One type, because a path claim and an email claim are
///     the same state machine over a different digest.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.Index")]
public sealed class IndexState {
    /// <summary>The entry. <c>Free</c> until something claims it.</summary>
    [Id(0)]
    public IndexEntry Entry { get; set; } = new();
}

/// <summary>The tenant directory grain's durable record — the one global thing.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.TenantDirectory")]
public sealed class TenantDirectoryState {
    /// <summary>Every tenant's entry, by tenant id.</summary>
    [Id(0)]
    public Dictionary<Guid, TenantDirectoryEntry> Entries { get; set; } = [];

    /// <summary>Slug → tenant id. Slugs are globally unique — docs/plan/04 § The clusters, plural.</summary>
    [Id(1)]
    public Dictionary<string, Guid> BySlug { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The monotonic directory version. Advances on every write.</summary>
    [Id(2)]
    public long Version { get; set; }

    /// <summary>
    ///     Slugs of purged tenants, which are never reused — docs/plan/06 § Tenant lifecycle,
    ///     "tombstoned forever (never reuse an id)".
    /// </summary>
    [Id(3)]
    public List<string> TombstonedSlugs { get; set; } = [];
}

/// <summary>The shard map grain's durable record.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.ShardMap")]
public sealed class ShardMapState {
    /// <summary>
    ///     Every tenant's assignment. ⚠ <b>Append-only in effect</b>: an entry here is never
    ///     rewritten, which is what makes docs/plan/05 § The shard map's "permanent" true rather
    ///     than aspirational.
    /// </summary>
    [Id(0)]
    public Dictionary<Guid, ShardAssignment> Assignments { get; set; } = [];

    /// <summary>Every known durable shard, and whether it takes new tenants.</summary>
    [Id(1)]
    public Dictionary<string, bool> Shards { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The monotonic map version.</summary>
    [Id(2)]
    public long Version { get; set; }
}

/// <summary>The quota grain's durable record.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.State.Quota")]
public sealed class QuotaState {
    /// <summary>Committed usage per meter — resources that exist.</summary>
    [Id(0)]
    public Dictionary<QuotaMeter, decimal> Committed { get; set; } = [];

    /// <summary>Limits per meter. A meter with no entry uses the tier default.</summary>
    [Id(1)]
    public Dictionary<QuotaMeter, decimal> Limits { get; set; } = [];

    /// <summary>Live leases, by lease id. Expired ones are swept on read.</summary>
    [Id(2)]
    public Dictionary<Guid, QuotaLease> Leases { get; set; } = [];
}
