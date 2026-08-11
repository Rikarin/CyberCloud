namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The in-process, version-stamped tenant → shard lookup that docs/plan/05 § Storage provider
///     wiring resolves on the first touch of a tenant's storage.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every method here is on a path that must never do I/O.</b> It is called from
///         <c>Orleans.Multitenant</c>'s <c>configureTenantOptions</c>, which runs while the storage
///         provider for a tenant is being constructed, under a per-tenant lock, inside a grain
///         activation. A blocking network call here would put a round trip on the first activation of
///         every tenant on every silo, and a slow one would trip
///         <c>MultitenantStorageOptions.TenantStorageProviderInitTimeout</c> (20 s by default). The
///         lookup is a dictionary read, and the refresh that fills the dictionary happens elsewhere.
///     </para>
///     <para>
///         ⚠ <b>The refresher is not built here — that is task 8.</b> docs/plan/05 § The shard map
///         puts the authority in <c>IShardMapGrain</c>, a null-tenant grain in the global cluster
///         with <c>AssignAsync</c> / <c>GetSnapshotAsync(knownVersion)</c> / <c>PinAsync</c>. This
///         interface is the mirror side of <c>GetSnapshotAsync</c>: <see cref="Version" /> is the
///         stamp a refresher passes as <c>knownVersion</c>, and
///         <see cref="StaticShardMapCache" /> is a fixed-configuration stand-in that never advances
///         it. See that type's remarks for exactly what is stubbed.
///     </para>
/// </remarks>
public interface IShardMapCache
{
    /// <summary>
    ///     The directory version this snapshot was built from. <c>0</c> means "never refreshed from
    ///     the shard map grain" — which is what <see cref="StaticShardMapCache" /> always reports.
    /// </summary>
    long Version { get; }

    /// <summary>
    ///     The durable shard id for a tenant. docs/plan/05 § The shard map: assignment happens at
    ///     tenant creation and is permanent; there is no rebalancing.
    /// </summary>
    /// <param name="tenantId">
    ///     The tenant id <b>as <c>Orleans.Multitenant</c> supplies it</b> — the string extracted from
    ///     the grain key, or <c>MultitenantStorageOptions.TenantIdForNullTenant</c> for a
    ///     null-tenant grain. It is deliberately <c>string</c> and not <c>Guid</c>; see
    ///     <see cref="DurableTierOptions.NullTenantShard" /> for why parsing it as a GUID is a bug.
    /// </param>
    string DurableShardFor(string tenantId);

    /// <summary>
    ///     The Redis Cluster hash-tag <i>body</i> for a tenant — the part inside the braces of
    ///     <c>{cc:t:&lt;tenantId&gt;}:&lt;grainType&gt;:&lt;keyWithinTenant&gt;</c>.
    /// </summary>
    /// <remarks>
    ///     Returning the body rather than the whole key is what keeps the override in
    ///     docs/plan/05 § Hot ("pin a named large tenant to a dedicated shard by overriding the hash
    ///     tag") a one-line map entry instead of a second key format.
    /// </remarks>
    string HotHashTagFor(string tenantId);
}
