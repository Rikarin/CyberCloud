using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     ⚠ <b>STUB.</b> An <see cref="IShardMapCache" /> built once from
///     <see cref="CyberCloudStorageOptions" /> and never refreshed.
/// </summary>
/// <remarks>
///     <para>
///         <b>What is real here.</b> The placement function, the pin table, the hash-tag override
///         table, and the null-tenant handling. A tenant's shard is a pure function of its id and the
///         configured shard list, so two silos with the same configuration agree without talking to
///         each other, and a tenant's placement never moves — which is docs/plan/05 § The shard map's
///         "assignment is at tenant creation and it is permanent".
///     </para>
///     <para>
///         <b>What is stubbed, precisely.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>There is no <c>IShardMapGrain</c>.</b> docs/plan/05 § The shard map puts the
///             authority in a null-tenant grain in the global cluster, and its
///             <c>AssignAsync(tenantId, region)</c> does a "simple weighted pick" over shard
///             <i>load</i>. This does an unweighted hash. For a fixed shard list the two agree on
///             "spread tenants over shards" and disagree on "prefer the empty shard", which only
///             matters once a shard is added — see the next item.
///         </item>
///         <item>
///             <b>Adding a shard here re-places existing tenants.</b> <c>hash mod n</c> moves roughly
///             <c>1 - 1/n</c> of tenants when <c>n</c> changes, and docs/plan/05 § The shard map says
///             flatly that a tenant's durable state is never moved. So
///             <b>
///                 the configured shard list
///                 must not change
///             </b>
///             while this stub is the implementation. Capacity is added by the
///             real thing, which records each assignment rather than recomputing it. This is the one
///             sharp edge of the stub and it is why it is a stub.
///         </item>
///         <item>
///             <b><see cref="Version" /> is always 0 and nothing advances it.</b> The version stamp
///             is the contract with <c>GetSnapshotAsync(knownVersion)</c>; a real cache bumps it on
///             every applied delta. Callers that want to log or assert "which map did this decision
///             come from" get a constant here, which is honest: there is only ever one map.
///         </item>
///         <item>
///             <b><c>PinAsync</c> is read-only.</b> The pins in
///             <see cref="DurableTierOptions.Pins" /> and
///             <see cref="HotTierOptions.HashTagOverrides" /> are honoured, so the operator move
///             docs/plan/05 § The shard map describes works — but only as far as configuration. The
///             quiesce/copy/flip/un-quiesce that makes it safe on a live tenant is not here and is
///             explicitly M2 in the plan.
///         </item>
///     </list>
///     <para>
///         The seam the real one plugs into is <see cref="IShardMapCache" /> itself: this type is
///         registered as that interface and nothing else references it by name, so task 8 replaces
///         one <c>AddSingleton</c>.
///     </para>
/// </remarks>
public sealed class StaticShardMapCache : IShardMapCache {
    /// <summary>
    ///     The fixed prefix of every hot-tier hash tag. docs/plan/05 § Hot fixes the layout as
    ///     <c>{cc:t:&lt;tenantId&gt;}:&lt;grainType&gt;:&lt;keyWithinTenant&gt;</c>.
    /// </summary>
    public const string HotTagPrefix = "cc:t:";

    readonly IReadOnlyList<string> durableShards;
    readonly Dictionary<string, string> durablePins;
    readonly Dictionary<string, string> hotOverrides;
    readonly string? nullTenantShard;

    /// <inheritdoc />
    /// <remarks>Always 0 — see the class remarks, item 3.</remarks>
    public long Version => 0;

    /// <summary>Builds the map from configuration.</summary>
    /// <param name="options">The bound <c>CyberCloud:Storage</c> section.</param>
    /// <exception cref="InvalidOperationException">
    ///     No durable shards are configured, or a pin names a shard that is not in the table. Both
    ///     are fatal at wiring time on purpose: a pin that silently falls back to the hash is a
    ///     tenant quietly reading an empty database.
    /// </exception>
    public StaticShardMapCache(CyberCloudStorageOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        durableShards = [.. options.Durable.Shards.Keys.OrderBy(x => x, StringComparer.Ordinal)];
        durablePins = options.Durable.Pins;
        hotOverrides = options.Hot.HashTagOverrides;
        nullTenantShard = options.Durable.NullTenantShard;

        if (durableShards.Count == 0) {
            throw new InvalidOperationException(
                $"{CyberCloudStorageOptions.SectionName}:Durable:Shards is empty. The durable tier is "
                + "N independent PostgreSQL servers (docs/plan/05 § Durable) and a silo with none of "
                + "them cannot store a tenant, a subscription or a resource."
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
                $"{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard is '{nullTenantShard}', "
                + "which is not in the shard table."
            );
        }
    }

    /// <inheritdoc />
    public string DurableShardFor(string tenantId) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        if (durablePins.TryGetValue(tenantId, out var pinned)) {
            return pinned;
        }

        if (nullTenantShard is not null && IsNullTenant(tenantId)) {
            return nullTenantShard;
        }

        return durableShards[(int)(StableHash(tenantId) % (uint)durableShards.Count)];
    }

    /// <inheritdoc />
    public string HotHashTagFor(string tenantId) {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        if (hotOverrides.TryGetValue(tenantId, out var overridden)) {
            return overridden;
        }

        // A GUID has five textual spellings and they are the same tenant. Canonicalising to "N"
        // means `D`-formatted and `N`-formatted ids cannot end up in two different slots holding two
        // halves of one tenant's session state — which is a bug with no error message.
        return HotTagPrefix
            + (Guid.TryParse(tenantId, out var id)
                ? id.ToString("N", CultureInfo.InvariantCulture)
                : tenantId);
    }

    /// <summary>
    ///     A hash that is stable across processes, machines and .NET releases.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="string.GetHashCode()" />.</b> That is randomised per process, so two
    ///     silos would place the same tenant on two different shards and the tenant's rows would be
    ///     split across two databases with no error anywhere. SHA-256 truncated to 32 bits is
    ///     overkill for the job and costs nothing on a path that runs once per tenant per silo.
    ///     <para>
    ///         ⚠ <b>Public because the real shard map has to agree with it exactly.</b>
    ///         <c>CyberCloud.Tenancy.ShardMapGrain</c> places a brand-new tenant on the shard this
    ///         function names, so that the answer a cache computes before the assignment has reached
    ///         it and the answer the map recorded are the <i>same</i> shard. A second copy of this
    ///         function in another assembly would be a second definition of "which database is this
    ///         tenant in", and the day the two drifted a tenant's rows would split silently.
    ///     </para>
    /// </remarks>
    public static uint StableHash(string value) {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
        return BitConverter.ToUInt32(digest[..4]);
    }

    /// <summary>
    ///     Whether this is <c>Orleans.Multitenant</c>'s null-tenant sentinel rather than a real
    ///     tenant id.
    /// </summary>
    /// <remarks>
    ///     The sentinel is <c>MultitenantStorageOptions.TenantIdForNullTenant</c>, default
    ///     <c>"Null"</c>. It is compared as "not a GUID" rather than against the literal so that a
    ///     silo which changed the sentinel still routes its platform grains to the platform shard.
    /// </remarks>
    static bool IsNullTenant(string tenantId) => !Guid.TryParse(tenantId, out _);
}
