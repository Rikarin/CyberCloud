using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     The global tenant directory — <b>the one global thing</b> (docs/plan/05 § The tenant
///     directory).
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Platform · <b>Tier</b> Durable · <b>Key</b> <c>platform/tenant-directory</c>,
///         <b>null tenant</b>, in the global cluster (docs/plan/04 § Grain taxonomy, the Platform
///         row). Build it with <c>GrainKeys.TenantDirectory</c> and reach it with a plain
///         <c>IGrainFactory.GetGrain</c> — <b>not</b> <c>ForTenant</c>.
///     </para>
///     <para>
///         ⚠ <b>The <c>"Null"</c> trap lives on this grain's path.</b> Because it is null-tenant and
///         durable, <c>Orleans.Multitenant</c> hands the storage tier
///         <c>MultitenantStorageOptions.TenantIdForNullTenant</c> — the literal string <c>"Null"</c>
///         — as the tenant id, and docs/plan/05 § Storage provider wiring's five-line body opens with
///         <c>Guid.Parse(tenantId)</c>, which throws on it. The repair is
///         <c>IShardMapCache.DurableShardFor(string)</c> plus
///         <c>DurableTierOptions.NullTenantShard</c>, and <c>NullTenantGrainTests</c> is the
///         assertion that this grain actually round-trips through PostgreSQL.
///     </para>
///     <para>
///         <b>Why reads never leave the process.</b> docs/plan/05 § The tenant directory: every
///         gateway holds the whole thing as an immutable snapshot; "a cache miss (a tenant created
///         200 ms ago in another region) falls back to a grain call — measured, alerted on, and
///         expected to be a handful per second worldwide"; and "if the global cluster is unreachable,
///         no <i>new</i> tenants can be created and no directory changes propagate — but every
///         existing tenant keeps working from cache, in every region, indefinitely." That last
///         sentence is chaos-invariant 5 in docs/plan/23, and it is a property of
///         <c>ITenantDirectoryCache</c>, not of this grain.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.ITenantDirectoryGrain")]
public interface ITenantDirectoryGrain : IGrainWithStringKey {
    /// <summary>
    ///     Writes or updates a tenant's directory entry, claiming its slug. One of the four things
    ///     that write here — docs/plan/05 § The tenant directory puts the rate at 0.12/s.
    /// </summary>
    /// <param name="entry">The entry. Its <c>DirectoryVersion</c> is assigned here and ignored on input.</param>
    Task<Result<TenantDirectoryEntry>> RegisterAsync(TenantDirectoryEntry entry);

    /// <summary>Looks a tenant up by id, or <c>TenantNotFound</c>.</summary>
    /// <param name="tenantId">The tenant's GUID, which never changes — unlike its slug.</param>
    Task<Result<TenantDirectoryEntry>> LookupAsync(Guid tenantId);

    /// <summary>Looks a tenant up by its globally unique slug, or <c>TenantNotFound</c>.</summary>
    /// <param name="slug">
    ///     The slug as registered. Unique platform-wide, which is why this lookup lives on the one
    ///     global grain rather than in a region.
    /// </param>
    Task<Result<TenantDirectoryEntry>> LookupBySlugAsync(string slug);

    /// <summary>Changes a tenant's status. Purged is terminal.</summary>
    /// <param name="tenantId">The tenant whose entry moves.</param>
    /// <param name="status">
    ///     The status to move to. <see cref="TenantStatus.Purged" /> cannot be moved away from.
    /// </param>
    Task<Result<TenantDirectoryEntry>> SetStatusAsync(Guid tenantId, TenantStatus status);

    /// <summary>
    ///     Everything that changed at or after <paramref name="knownVersion" /> — what an
    ///     in-process cache polls.
    /// </summary>
    /// <param name="knownVersion">The caller's cursor. <c>0</c> asks for the whole directory.</param>
    Task<Result<TenantDirectoryDelta>> GetDeltaAsync(long knownVersion);

    /// <summary>How many tenants the directory holds. For the sizing assertion in docs/plan/05.</summary>
    Task<Result<int>> CountAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     The shard map — docs/plan/05 § The shard map, "a null-tenant grain in the global cluster,
///     mirrored into every silo and gateway alongside the tenant directory".
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Platform · <b>Tier</b> Durable · <b>Key</b> <c>platform/shard-map</c>,
///         <b>null tenant</b>. Build it with <c>GrainKeys.ShardMap</c>.
///     </para>
///     <para>
///         ⚠ <b>There is no bootstrap cycle here and it is worth saying why not.</b> This grain's own
///         durable state has to be stored <i>somewhere</i>, and it cannot ask itself where. It does
///         not have to: it is a null-tenant grain, and null-tenant grains go to the configured
///         <c>DurableTierOptions.NullTenantShard</c>, which is a constant in configuration rather
///         than a lookup. That is what that option is for.
///     </para>
///     <para>
///         ⚠ <b>Assignment is permanent and there is no rebalancing.</b> docs/plan/05 § The shard
///         map is explicit that this is "a decision rather than an omission" — rebalancing means
///         moving live durable state, which is "a quarter of work for a problem that does not exist
///         until a shard is genuinely full — at which point the answer is to stop assigning new
///         tenants to it, which costs nothing.
///         <b>
///             Capacity is added at the front, not redistributed
///             at the back.
///         </b>
///         " So <see cref="AssignAsync" /> on a tenant that already has an assignment
///         returns the original, unchanged, even after the shard list grows.
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.IShardMapGrain")]
public interface IShardMapGrain : IGrainWithStringKey {
    /// <summary>
    ///     The shard list this map places into. Idempotent; adding a shard never moves a tenant.
    /// </summary>
    /// <param name="durableShards">Every durable shard id, in any order.</param>
    /// <remarks>
    ///     Shards are added, never removed: removing one would orphan every tenant recorded against
    ///     it. A shard that is being drained is taken out of the <i>placement</i> rotation by
    ///     <see cref="SetAcceptingNewTenantsAsync" />, which is docs/plan/05 § The shard map's "stop
    ///     assigning new tenants to it".
    /// </remarks>
    Task<Result<ShardMapSnapshot>> ConfigureShardsAsync(IReadOnlyList<string> durableShards);

    /// <summary>
    ///     Assigns a tenant to a shard, permanently — docs/plan/05 § The shard map.
    /// </summary>
    /// <param name="tenantId">
    ///     The tenant to place. Also the tie-breaker in the weighted pick, so the same tenant lands
    ///     on the same shard given the same shard list.
    /// </param>
    /// <param name="region">The region the tenant is homed to.</param>
    /// <returns>
    ///     The tenant's assignment. <b>The original one</b> if it already has one, regardless of what
    ///     the shard list looks like now.
    /// </returns>
    Task<Result<ShardAssignment>> AssignAsync(Guid tenantId, string region);

    /// <summary>The tenant's assignment, or <c>TenantNotFound</c> if it has never been assigned.</summary>
    /// <param name="tenantId">The tenant to look up. Read-only — this never assigns.</param>
    Task<Result<ShardAssignment>> GetAssignmentAsync(Guid tenantId);

    /// <summary>
    ///     Everything that changed at or after <paramref name="knownVersion" /> — what
    ///     <c>IShardMapCache</c> polls.
    /// </summary>
    /// <param name="knownVersion">The caller's cursor. <c>0</c> asks for the whole map.</param>
    Task<Result<ShardMapSnapshot>> GetSnapshotAsync(long knownVersion);

    /// <summary>
    ///     Takes a shard in or out of the placement rotation. Existing tenants are unaffected.
    /// </summary>
    /// <param name="shard">
    ///     A shard id from <see cref="ConfigureShardsAsync" />. One that is not in the map is a
    ///     failure, not a silent no-op.
    /// </param>
    /// <param name="accepting">Whether new tenants may be placed on it.</param>
    Task<Result<ShardMapSnapshot>> SetAcceptingNewTenantsAsync(string shard, bool accepting);

    /// <summary>
    ///     ⚠ <b>NOT IMPLEMENTED — throws <see cref="NotSupportedException" />.</b> The operator-run
    ///     move of one outsized tenant, budgeted at 0.5 EM in M2 by docs/plan/05 § The shard map.
    /// </summary>
    /// <param name="tenantId">The tenant to move.</param>
    /// <param name="durableShard">The shard to move it to.</param>
    /// <param name="hotOverride">The hash-tag override, or <see langword="null" />.</param>
    /// <remarks>
    ///     <para>
    ///         The signature is here because docs/plan/05 § The shard map declares it and because a
    ///         method that does not exist cannot be planned against. The <i>body</i> is not, because
    ///         what makes <c>PinAsync</c> safe is not the map edit — it is the four steps around it:
    ///         "It quiesces the tenant (rejects writes with <c>503 Retry-After</c>), copies the grain
    ///         rows, flips the map, and un-quiesces." Shipping the map edit without the copy would
    ///         repoint a live tenant at an empty database, which is worse than not having the method.
    ///     </para>
    ///     <para>
    ///         The read-only half — an operator-configured pin honoured at wiring time — already
    ///         works, through <c>DurableTierOptions.Pins</c>.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">Always.</exception>
    Task<Result> PinAsync(Guid tenantId, string durableShard, string? hotOverride);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}
