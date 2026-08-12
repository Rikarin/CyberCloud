using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Tenancy.Shards;

/// <summary>
///     The real <see cref="IShardMapCache" /> — an immutable snapshot of
///     <see cref="IShardMapGrain" />'s recorded assignments, replacing
///     <see cref="StaticShardMapCache" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>
///             The four stubbed things in <see cref="StaticShardMapCache" />, and what each becomes
///             here.
///         </b>
///     </para>
///     <list type="number">
///         <item>
///             <b>"There is no <c>IShardMapGrain</c>."</b> There is now, and this type mirrors it.
///             The mirror is filled by <see cref="ShardMapRefresher" />, never by a call on the read
///             path — <see cref="IShardMapCache" />'s remarks say why that path must never do I/O.
///         </item>
///         <item>
///             <b>"Adding a shard here re-places existing tenants."</b> It does not here: a tenant
///             with a recorded assignment reads that assignment, and the map never rewrites one.
///             <c>ShardMapTests.TheCacheNeverRePlacesARecordedTenantWhenTheShardListGrows</c> aims
///             that at this class — a bare cache, given an assignment and then a longer shard list,
///             still answers with the recorded shard — and
///             <c>ShardMapTests.AddingAShardDoesNotMoveASingleAlreadyAssignedTenant</c> aims it at
///             the grain, over forty already-assigned tenants.
///         </item>
///         <item>
///             <b>"<c>Version</c> is always 0."</b> It is the version of the snapshot that is
///             actually loaded, and it advances.
///         </item>
///         <item>
///             <b>"<c>PinAsync</c> is read-only."</b> Still true, and now explicit rather than
///             implicit: <see cref="IShardMapGrain.PinAsync" /> throws
///             <see cref="NotSupportedException" />, and configured pins (the read-only half) are
///             still honoured here and still win over everything.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             The fallback for an unrecorded tenant is the deterministic hash, and that is safe for
///             a reason worth stating.
///         </b>
///         A tenant with no recorded assignment has never been created,
///         so it has no durable state anywhere and there is nothing to move. The moment it <i>is</i>
///         created, <see cref="IShardMapGrain.AssignAsync" /> records the shard this same hash names
///         (see <c>ShardMapGrain.Place</c>) unless that shard is out of the rotation — so in the
///         ordinary case the fallback and the record agree, and in the exceptional case the record is
///         written before the tenant has any state. What is <i>not</i> safe, and is the stub's
///         limit 2, is recomputing the hash for a tenant that already has state; that cannot happen
///         here because a recorded assignment is always preferred.
///     </para>
/// </remarks>
public sealed class GrainBackedShardMapCache : IShardMapCache {
    readonly ImmutableDictionary<string, string> durablePins;
    readonly ImmutableDictionary<string, string> hotOverrides;
    readonly string? nullTenantShard;
    readonly ImmutableArray<string> configuredShards;

    volatile Snapshot current;

    /// <inheritdoc />
    public long Version => current.Version;

    /// <summary>How many tenants the loaded snapshot has a recorded assignment for.</summary>
    public int RecordedAssignments => current.Assignments.Count;

    /// <summary>The shard list the cache is currently placing unassigned tenants over.</summary>
    public IReadOnlyList<string> Shards => current.Shards;

    /// <summary>Builds the cache with configuration only. The grain fills it in afterwards.</summary>
    /// <param name="options">The bound <c>CyberCloud:Storage</c> section.</param>
    /// <exception cref="InvalidOperationException">No durable shards are configured.</exception>
    public GrainBackedShardMapCache(CyberCloudStorageOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        durablePins = options.Durable.Pins.ToImmutableDictionary(StringComparer.Ordinal);
        hotOverrides = options.Hot.HashTagOverrides.ToImmutableDictionary(StringComparer.Ordinal);
        nullTenantShard = options.Durable.NullTenantShard;

        // ⚠ The null-tenant shard is excluded from the initial placement list when there is anything
        // else to place onto, and that is a real correctness detail rather than tidiness. Between
        // process start and the first refresh, this list is what an unassigned tenant hashes over;
        // the shard map grain hashes over ITS configured tenant shards, which do not include a
        // dedicated platform shard. If the two lists differed, a tenant touched in that window would
        // land on one shard and be recorded on another — the exact split-brain the recording is
        // there to prevent. When the platform shard is the ONLY shard, it is kept, because a list of
        // none can place nothing.
        var placeable = options.Durable.Shards.Keys
            .Where(x => options.Durable.NullTenantShard is null
                || !string.Equals(x, options.Durable.NullTenantShard, StringComparison.Ordinal)
            )
            .Order(StringComparer.Ordinal)
            .ToList();

        configuredShards = placeable.Count > 0
            ? [.. placeable]
            : [.. options.Durable.Shards.Keys.Order(StringComparer.Ordinal)];

        if (options.Durable.Shards.Count == 0) {
            throw new InvalidOperationException(
                $"{CyberCloudStorageOptions.SectionName}:Durable:Shards is empty. The durable tier "
                + "is N independent PostgreSQL servers (docs/plan/05 § Durable) and a silo with none "
                + "of them cannot store a tenant, a subscription or a resource."
            );
        }

        foreach (var (tenantId, shard) in durablePins) {
            if (!options.Durable.Shards.ContainsKey(shard)) {
                throw new InvalidOperationException(
                    $"Tenant {tenantId} is pinned to durable shard '{shard}', which is not in "
                    + $"{CyberCloudStorageOptions.SectionName}:Durable:Shards."
                );
            }
        }

        if (nullTenantShard is not null && !options.Durable.Shards.ContainsKey(nullTenantShard)) {
            throw new InvalidOperationException(
                $"{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard is "
                + $"'{nullTenantShard}', which is not in the shard table."
            );
        }

        current = new(0, configuredShards, ImmutableDictionary<Guid, string>.Empty);
    }

    /// <inheritdoc />
    public string DurableShardFor(string tenantId) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        // A configured pin is the operator's word and beats everything, including the map — that is
        // the read-only half of docs/plan/05 § The shard map's PinAsync, and the half that works.
        if (durablePins.TryGetValue(tenantId, out var pinned)) {
            return pinned;
        }

        if (!Guid.TryParse(tenantId, out var id)) {
            // Orleans.Multitenant's null-tenant sentinel — the literal "Null" by default. ⚠ This is
            // the branch docs/plan/05 § Storage provider wiring's `Guid.Parse(tenantId)` throws on,
            // and it is on a live path: every Platform grain is null-tenant AND durable.
            return nullTenantShard ?? Hash(tenantId, current.Shards);
        }

        var snapshot = current;
        if (snapshot.Assignments.TryGetValue(id, out var recorded)) {
            return recorded;
        }

        // Never assigned — so nothing is stored for it anywhere and the deterministic pick cannot
        // move anything. Canonicalised to the "D" form so that two spellings of one tenant id can
        // never land in two databases.
        return Hash(id.ToString("D", CultureInfo.InvariantCulture), snapshot.Shards);
    }

    /// <inheritdoc />
    public string HotHashTagFor(string tenantId) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        if (hotOverrides.TryGetValue(tenantId, out var overridden)) {
            return overridden;
        }

        return StaticShardMapCache.HotTagPrefix
            + (Guid.TryParse(tenantId, out var id)
                ? id.ToString("N", CultureInfo.InvariantCulture)
                : tenantId);
    }

    /// <summary>
    ///     Applies a snapshot from <see cref="IShardMapGrain.GetSnapshotAsync" />.
    /// </summary>
    /// <param name="snapshot">The snapshot or delta.</param>
    /// <returns><see langword="true" /> if anything changed.</returns>
    /// <remarks>
    ///     ⚠ <b>Replaces one immutable object with another</b> rather than mutating a dictionary.
    ///     <see cref="DurableShardFor" /> runs inside <c>Orleans.Multitenant</c>'s per-tenant lock
    ///     while a storage provider is being built; a reader there must never see a half-applied
    ///     delta, and must never take a lock of its own.
    /// </remarks>
    public bool Apply(ShardMapSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var previous = current;
        if (snapshot.Version < previous.Version) {
            // An older snapshot than the one loaded. Discard it rather than going backwards: a cache
            // that regressed would re-place tenants it had already learned about.
            return false;
        }

        var shards = snapshot.DurableShards.Count > 0
            ? [.. snapshot.DurableShards]
            : previous.Shards;

        var builder = snapshot.IsFullSnapshot
            ? ImmutableDictionary.CreateBuilder<Guid, string>()
            : previous.Assignments.ToBuilder();

        foreach (var assignment in snapshot.Assignments) {
            builder[assignment.TenantId] = assignment.DurableShard;
        }

        var next = new Snapshot(snapshot.Version, shards, builder.ToImmutable());
        var changed = next.Version != previous.Version
            || next.Assignments.Count != previous.Assignments.Count
            || !next.Shards.SequenceEqual(previous.Shards, StringComparer.Ordinal);

        current = next;
        return changed;
    }

    static string Hash(string value, ImmutableArray<string> shards) =>
        shards[(int)(StaticShardMapCache.StableHash(value) % (uint)shards.Length)];

    sealed record Snapshot(
        long Version,
        ImmutableArray<string> Shards,
        ImmutableDictionary<Guid, string> Assignments
    );
}
