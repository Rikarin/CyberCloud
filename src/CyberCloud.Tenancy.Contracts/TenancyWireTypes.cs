using CyberCloud.Core.Resources;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     A tenant, as the tenant's own grain holds it. docs/plan/06 § The hierarchy — the customer, the
///     identity boundary, homed to a region.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.TenantDescriptor")]
public sealed record TenantDescriptor {
    /// <summary>The tenant id. The brief's GUID — docs/plan/06 § Identifiers.</summary>
    [Id(0)]
    public Guid Id { get; init; }

    /// <summary>The DNS-1123 slug. Globally unique — docs/plan/04 § The clusters, plural.</summary>
    [Id(1)]
    public string Slug { get; init; } = string.Empty;

    /// <summary>The human-facing name. Not an identifier and not unique.</summary>
    [Id(2)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The region the tenant is homed to. Exactly one — docs/plan/04 § The clusters, plural.</summary>
    [Id(3)]
    public string HomeRegion { get; init; } = string.Empty;

    /// <summary>The lifecycle state — docs/plan/06 § Tenant lifecycle.</summary>
    [Id(4)]
    public TenantStatus Status { get; init; } = TenantStatus.Unknown;

    /// <summary>When the tenant was created.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the tenant last changed.</summary>
    [Id(6)]
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>
    ///     The concurrency stamp. docs/plan/06 § Tags, locks — "<c>etag</c> enables <c>If-Match</c>
    ///     and is the only way to make concurrent portal edits safe".
    /// </summary>
    [Id(7)]
    public long Version { get; init; }
}

/// <summary>
///     What the global tenant directory holds for one tenant — docs/plan/05 § The tenant directory,
///     "id, slug, home region, hot-shard id, durable-shard id, status, directory version. About 200
///     bytes."
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.TenantDirectoryEntry")]
public sealed record TenantDirectoryEntry {
    /// <summary>The tenant id.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The globally unique slug.</summary>
    [Id(1)]
    public string Slug { get; init; } = string.Empty;

    /// <summary>The region the tenant is homed to.</summary>
    [Id(2)]
    public string HomeRegion { get; init; } = string.Empty;

    /// <summary>The Redis Cluster hash-tag body — docs/plan/05 § Hot.</summary>
    [Id(3)]
    public string HotShard { get; init; } = string.Empty;

    /// <summary>The PostgreSQL shard id — docs/plan/05 § Durable.</summary>
    [Id(4)]
    public string DurableShard { get; init; } = string.Empty;

    /// <summary>The lifecycle state.</summary>
    [Id(5)]
    public TenantStatus Status { get; init; } = TenantStatus.Unknown;

    /// <summary>
    ///     The directory version this entry was written at. Monotonic across the whole directory,
    ///     not per entry — it is the cursor a cache passes as <c>knownVersion</c>.
    /// </summary>
    [Id(6)]
    public long DirectoryVersion { get; init; }
}

/// <summary>
///     A batch of directory changes at or after a known version, plus whether the reader had to be
///     reset.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.TenantDirectoryDelta")]
public sealed record TenantDirectoryDelta {
    /// <summary>The version the directory is at now.</summary>
    [Id(0)]
    public long Version { get; init; }

    /// <summary>The entries that changed at or after the caller's known version.</summary>
    [Id(1)]
    public IReadOnlyList<TenantDirectoryEntry> Entries { get; init; } = [];

    /// <summary>
    ///     Whether <see cref="Entries" /> is the whole directory rather than a delta. True on the
    ///     first call and whenever the caller's cursor is older than the retained change window.
    /// </summary>
    [Id(2)]
    public bool IsFullSnapshot { get; init; }
}

/// <summary>
///     One tenant's shard placement — docs/plan/05 § The shard map.
/// </summary>
/// <remarks>
///     ⚠ <b>Permanent.</b> docs/plan/05 § The shard map: "Assignment is at tenant creation and it is
///     permanent … There is <b>no automatic rebalancing</b>, and that is a decision rather than an
///     omission." Re-assigning a tenant that already has an assignment returns the original.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ShardAssignment")]
public sealed record ShardAssignment {
    /// <summary>The tenant.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The durable (PostgreSQL) shard id.</summary>
    [Id(1)]
    public string DurableShard { get; init; } = string.Empty;

    /// <summary>The Redis Cluster hash-tag body, which is the hot "shard" — docs/plan/05 § Hot.</summary>
    [Id(2)]
    public string HotHashTag { get; init; } = string.Empty;

    /// <summary>The region the assignment was made in.</summary>
    [Id(3)]
    public string Region { get; init; } = string.Empty;

    /// <summary>When it was made. Never changes.</summary>
    [Id(4)]
    public DateTimeOffset AssignedAt { get; init; }

    /// <summary>The map version this assignment was recorded at.</summary>
    [Id(5)]
    public long Version { get; init; }
}

/// <summary>
///     The whole shard map as a cache mirrors it — docs/plan/05 § The shard map, "mirrored into every
///     silo and gateway alongside the tenant directory".
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ShardMapSnapshot")]
public sealed record ShardMapSnapshot {
    /// <summary>The map version this snapshot was taken at.</summary>
    [Id(0)]
    public long Version { get; init; }

    /// <summary>Every durable shard the map knows about, in the order it will place into.</summary>
    [Id(1)]
    public IReadOnlyList<string> DurableShards { get; init; } = [];

    /// <summary>
    ///     The assignments at or after the caller's known version, or all of them when
    ///     <see cref="IsFullSnapshot" />.
    /// </summary>
    [Id(2)]
    public IReadOnlyList<ShardAssignment> Assignments { get; init; } = [];

    /// <summary>Whether <see cref="Assignments" /> is the whole map rather than a delta.</summary>
    [Id(3)]
    public bool IsFullSnapshot { get; init; }
}

/// <summary>
///     A subscription — docs/plan/06 § The hierarchy, "the quota and billing boundary, not the
///     tenant".
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.SubscriptionDescriptor")]
public sealed record SubscriptionDescriptor {
    /// <summary>The subscription id.</summary>
    [Id(0)]
    public Guid Id { get; init; }

    /// <summary>The owning tenant.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The display name.</summary>
    [Id(2)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The lifecycle state.</summary>
    [Id(3)]
    public ProvisioningState State { get; init; } = ProvisioningState.Unknown;

    /// <summary>The names of the resource groups in this subscription, sorted.</summary>
    [Id(4)]
    public IReadOnlyList<string> ResourceGroups { get; init; } = [];

    /// <summary>When it was created.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The concurrency stamp.</summary>
    [Id(6)]
    public long Version { get; init; }

    /// <summary>
    ///     The lock set <b>at this subscription</b> — docs/plan/06 § Tags, locks, "inherited down the
    ///     hierarchy".
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the lock set <i>here</i>, not the effective one. Combining it with the resource
    ///     group's and the resource's own is <c>ILockResolver</c>'s job and happens in the write
    ///     path's lock step, because inheritance is a walk and the walk crosses assemblies.
    /// </remarks>
    [Id(7)]
    public LockLevel Lock { get; init; } = LockLevel.None;
}

/// <summary>
///     A resource group — docs/plan/06 § The hierarchy, "a lifecycle unit, not a folder".
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ResourceGroupDescriptor")]
public sealed record ResourceGroupDescriptor {
    /// <summary>The name, unique within the subscription.</summary>
    [Id(0)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The owning subscription.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The owning tenant.</summary>
    [Id(2)]
    public Guid TenantId { get; init; }

    /// <summary>The region the group's resources default to.</summary>
    [Id(3)]
    public string Region { get; init; } = string.Empty;

    /// <summary>The lifecycle state.</summary>
    [Id(4)]
    public ProvisioningState State { get; init; } = ProvisioningState.Unknown;

    /// <summary>When it was created.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The concurrency stamp.</summary>
    [Id(6)]
    public long Version { get; init; }

    /// <summary>
    ///     The lock set <b>at this resource group</b> — docs/plan/06 § Tags, locks, "inherited down
    ///     the hierarchy".
    /// </summary>
    /// <remarks>
    ///     ⚠ The lock set <i>here</i>, not the effective one; the subscription's is inherited on top
    ///     of it by <c>ILockResolver</c>.
    /// </remarks>
    [Id(7)]
    public LockLevel Lock { get; init; } = LockLevel.None;
}

/// <summary>
///     One resource's membership of a resource group: the id, the address and the lifecycle state.
/// </summary>
/// <remarks>
///     ⚠ <b>This is not the resource.</b> <c>IResourceGrain</c> and its desired/observed state are
///     the resource manager's (docs/plan/08) and are deliberately not built here. What a resource
///     group must know — because it is the <i>lifecycle</i> boundary — is which resources are in it
///     and what state each is in, which is exactly this record and no more.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ResourceGroupMember")]
public sealed record ResourceGroupMember {
    /// <summary>The resource's GUID — its identity, stable across renames.</summary>
    [Id(0)]
    public Guid ResourceId { get; init; }

    /// <summary>The resource's canonical path — its address.</summary>
    [Id(1)]
    public string CanonicalPath { get; init; } = string.Empty;

    /// <summary>
    ///     The lifecycle state. ⚠ A member in <see cref="ProvisioningState.Deleting" /> is
    ///     <b>still listed</b> — docs/plan/06 § Two-phase create.
    /// </summary>
    [Id(2)]
    public ProvisioningState State { get; init; } = ProvisioningState.Unknown;

    /// <summary>Why the last teardown failed, when it did. Empty otherwise.</summary>
    [Id(3)]
    public string LastFailure { get; init; } = string.Empty;

    /// <summary>How many teardown attempts have been made.</summary>
    [Id(4)]
    public int TeardownAttempts { get; init; }
}

/// <summary>
///     What an index grain currently holds — the answer to "is this name taken, and by what".
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.IndexEntry")]
public sealed record IndexEntry {
    /// <summary>Free, claimed under a lease, or a permanent binding.</summary>
    [Id(0)]
    public IndexEntryState State { get; init; } = IndexEntryState.Free;

    /// <summary>What the name is bound to. <see cref="Guid.Empty" /> when free.</summary>
    [Id(1)]
    public Guid BoundTo { get; init; }

    /// <summary>
    ///     The value that was indexed, kept as state because the key is a one-way digest — see
    ///     <c>GrainKeys.PathIndex</c>. Lets a claim be audited and a collision be told from a
    ///     duplicate.
    /// </summary>
    [Id(2)]
    public string IndexedValue { get; init; } = string.Empty;

    /// <summary>When the lease expires. Meaningless unless <see cref="IndexEntryState.Claimed" />.</summary>
    [Id(3)]
    public DateTimeOffset LeaseExpiresAt { get; init; }

    /// <summary>When the entry last changed.</summary>
    [Id(4)]
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>
    ///     The end of the recovery window. Meaningless unless
    ///     <see cref="IndexEntryState.SoftDeleted" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Deliberately not <see cref="LeaseExpiresAt" /> under a second name.</b> A lease
    ///         expiring frees the name; this expiring does not — see
    ///         <see cref="IndexEntryState.SoftDeleted" />. Two deadlines with opposite consequences
    ///         sharing one member is how a restore past its window would silently become a name
    ///         released to whoever asked next.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Stamped from the type's declared retention at the moment of the delete and never
    ///         rewritten.</b> docs/plan/08 § Soft delete: <i>"retention is set at creation and
    ///         immutable afterwards — a window a caller can shorten under their own resource is not a
    ///         recovery window."</i>
    ///     </para>
    /// </remarks>
    [Id(5)]
    public DateTimeOffset RecoverableUntil { get; init; }
}

/// <summary>
///     How many children of one type hang off a parent resource's address — the count
///     <c>IResourceIndexGrain.ChildrenAsync</c> returns.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A count per type rather than a list of names, and the size is the reason.</b>
///         docs/plan/08 § Deleting a parent resource that has children asks for "a per-parent child
///         counter maintained transactionally where the index claim and release already happen". A
///         list would put one entry per child on the parent's durable row and rewrite the whole row
///         on every child create — a storage account with five thousand buckets would carry a
///         several-hundred-kilobyte index entry — where a count per type is a handful of bytes
///         whatever the fan-out. The refusal needs "how many, and of what", and that is exactly this.
///     </para>
///     <para>
///         ⚠ <b><see cref="Count" /> is never negative and a type at zero is not reported at all</b>,
///         so an empty array means "no children" without a caller having to sum anything.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.ChildTypeCount")]
public sealed record ChildTypeCount {
    /// <summary>The child type, for example <c>CyberCloud.Storage/accounts/buckets</c>.</summary>
    [Id(0)]
    public ResourceTypeName Type { get; init; }

    /// <summary>How many children of that type this address has. Always positive.</summary>
    [Id(1)]
    public int Count { get; init; }
}

/// <summary>
///     A quota reservation — docs/plan/06 § Quota, "Reservation, not a counter increment".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The distinction is the whole design.</b> A counter increment has no owner and no
///         expiry, so an operation that dies after incrementing leaks the quota forever and the only
///         repair is a manual reconciliation nobody will run. A lease is owned by an operation and
///         carries an expiry, so the two failure paths — the operation fails, or the operation grain
///         dies — are <i>released</i> and <i>expired</i> respectively, and both are automatic.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.QuotaLease")]
public sealed record QuotaLease {
    /// <summary>The lease's own id — what <c>ReleaseAsync</c> and <c>CommitAsync</c> take.</summary>
    [Id(0)]
    public Guid LeaseId { get; init; }

    /// <summary>The subscription the quota belongs to.</summary>
    [Id(1)]
    public Guid SubscriptionId { get; init; }

    /// <summary>The meter reserved against.</summary>
    [Id(2)]
    public QuotaMeter Meter { get; init; } = QuotaMeter.Unknown;

    /// <summary>How much is reserved.</summary>
    [Id(3)]
    public decimal Amount { get; init; }

    /// <summary>
    ///     The operation that owns the lease. docs/plan/06 § Quota: the lease "expires on its own if
    ///     the operation grain dies".
    /// </summary>
    [Id(4)]
    public Guid OperationId { get; init; }

    /// <summary>When the lease was taken.</summary>
    [Id(5)]
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary>When it expires unless committed.</summary>
    [Id(6)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>One meter's committed, reserved and limit figures.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Tenancy.QuotaUsage")]
public sealed record QuotaUsage {
    /// <summary>The meter.</summary>
    [Id(0)]
    public QuotaMeter Meter { get; init; } = QuotaMeter.Unknown;

    /// <summary>What has been committed — resources that exist.</summary>
    [Id(1)]
    public decimal Committed { get; init; }

    /// <summary>What is reserved under unexpired leases.</summary>
    [Id(2)]
    public decimal Reserved { get; init; }

    /// <summary>The ceiling. <see cref="Committed" /> + <see cref="Reserved" /> may not exceed it.</summary>
    [Id(3)]
    public decimal Limit { get; init; }
}
