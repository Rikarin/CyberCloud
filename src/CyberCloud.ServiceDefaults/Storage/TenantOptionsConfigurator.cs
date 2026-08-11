namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The shape of <c>Orleans.Multitenant</c>'s <c>configureTenantOptions</c> callback — the seam
///     the sharded storage wiring plugs into.
/// </summary>
/// <typeparam name="TOptions">
///     The storage provider's options type: <c>RedisStorageOptions</c> for the hot tier,
///     <c>AdoNetGrainStorageOptions</c> for the durable one.
/// </typeparam>
/// <param name="services">The silo's service provider.</param>
/// <param name="options">The options instance to fill in, for this tenant only.</param>
/// <param name="tenantId">
///     The tenant, <b>extracted from the grain key</b> by <c>Orleans.Multitenant</c>. This is the
///     load-bearing property of the whole scheme (docs/plan/05:158-160): the id arrives from the
///     address of the grain being stored, so a grain physically cannot be written to another
///     tenant's shard — the key is what selects the connection.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>NOT IMPLEMENTED HERE, AND THAT IS THE SCOPE LINE.</b> This delegate is the
///         signature; the body docs/plan/05:147-156 shows —
///     </para>
///     <code>
///     static void ConfigureForTenant(IServiceProvider sp, AdoNetGrainStorageOptions o, string tenantId)
///     {
///         var map = sp.GetRequiredService&lt;IShardMapCache&gt;();
///         var shard = map.DurableShardFor(Guid.Parse(tenantId));
///         o.ConnectionString = sp.GetRequiredService&lt;IShardConnections&gt;().Durable(shard);
///         o.Invariant = "Npgsql";
///         o.GrainStorageSerializer = new OrleansJsonGrainStorageSerializer();
///     }
///     </code>
///     <para>
///         — belongs with <c>IShardMapCache</c> and <c>IShardConnections</c>, which are a separate
///         task. Declaring the delegate now costs nothing and pins the one thing that must not drift:
///         the callback is <b>per tenant</b>, called once per tenant per silo at first touch, and it
///         receives the tenant id rather than reading one from ambient state.
///     </para>
///     <para>
///         ⚠ <b>Two numbers that belong to whoever writes the body</b>, recorded here because they
///         are invisible from the call site (docs/plan/05:162-166): 30 silos × 16 shards × a pool of
///         20 is 9 600 Postgres connections, which no Postgres survives. PgBouncer in transaction
///         mode in front of every shard is non-negotiable, and <c>MaxPoolSize</c> is 5 per silo per
///         shard.
///     </para>
/// </remarks>
public delegate void TenantOptionsConfigurator<in TOptions>(
    IServiceProvider services,
    TOptions options,
    string tenantId);
