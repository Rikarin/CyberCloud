namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Everything the two grain-storage tiers of docs/plan/05 § The two tiers need in order to be
///     wired: the hot Redis Cluster, the durable Postgres shards, and the pins that override the
///     default placement for one outsized tenant.
/// </summary>
/// <remarks>
///     <para>
///         Bound from <c>CyberCloud:Storage</c>. A silo with no such section gets
///         <b>no grain storage at all</b> — see
///         <see cref="SiloBuilderStorageExtensions.AddCyberCloudGrainStorage" /> and
///         <c>OrleansApplication.CreateSilo</c>, where that is a deliberate property rather than an
///         omission.
///     </para>
/// </remarks>
public sealed class CyberCloudStorageOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Storage";

    /// <summary>The hot tier — Redis Cluster (docs/plan/05 § Hot).</summary>
    public HotTierOptions Hot { get; } = new();

    /// <summary>The durable tier — N independent PostgreSQL servers (docs/plan/05 § Durable).</summary>
    public DurableTierOptions Durable { get; } = new();
}

/// <summary>
///     The hot tier. <b>One connection string for the whole Redis Cluster</b>, not one per shard.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the part of docs/plan/05 § Hot that is easy to read the wrong way.</b> The
///         document says the hot tier is "sharded by tenant, 12+ shards", which reads as though the
///         wiring picks a shard the way the durable tier does. It does not, and the document says so
///         two sentences later: the hash tag "makes shard assignment automatic from the tenant id
///         rather than something the shard map has to store". A Redis <i>Cluster</i> client connects
///         to the cluster and the cluster routes by slot. So there is exactly one
///         <see cref="ConnectionString" /> here, and the per-tenant decision is the hash tag, which
///         is <see cref="IShardMapCache.HotHashTagFor" />.
///     </para>
/// </remarks>
public sealed class HotTierOptions
{
    /// <summary>
    ///     The StackExchange.Redis configuration string for the cluster — every node, comma
    ///     separated, exactly as a client would be given it.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     Hash-tag overrides, tenant id → tag body, for the "pin a named large tenant to a
    ///     dedicated shard by overriding the hash tag" case in docs/plan/05 § Hot.
    /// </summary>
    /// <remarks>
    ///     docs/plan/05 is explicit that this exists "from day one because retrofitting it means
    ///     rewriting keys". The value replaces the whole <c>cc:t:&lt;tenantId&gt;</c> body, so an
    ///     operator can move a tenant to any slot they like; nothing else about the key changes.
    /// </remarks>
    public Dictionary<string, string> HashTagOverrides { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     The durable tier — the shard table and the connection-pool arithmetic that docs/plan/05
///     § Storage provider wiring says is "obvious in retrospect and fatal in production".
/// </summary>
public sealed class DurableTierOptions
{
    /// <summary>
    ///     Shard id → Npgsql connection string, one entry per PostgreSQL server. docs/plan/05
    ///     § Durable: "N plain Postgres servers that do not know about each other".
    /// </summary>
    public Dictionary<string, string> Shards { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Tenant id → shard id, for tenants whose placement is not the default hash. This is the
    ///     read side of <c>IShardMapGrain.PinAsync</c> (docs/plan/05 § The shard map).
    /// </summary>
    public Dictionary<string, string> Pins { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The shard that carries <b>null-tenant</b> grains — the tenant directory, the shard map,
    ///     the provider registry (docs/plan/04 § Grain taxonomy, the Platform row).
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Null-tenant grains are the case docs/plan/05 § Storage provider wiring gets wrong.</b>
    ///     Its five-line body opens with <c>Guid.Parse(tenantId)</c>, and the tenant id
    ///     <c>Orleans.Multitenant</c> passes for a null-tenant grain is
    ///     <c>MultitenantStorageOptions.TenantIdForNullTenant</c>, whose default value is the literal
    ///     string <c>"Null"</c>. <c>Guid.Parse("Null")</c> throws. Verified against Orleans.Multitenant
    ///     4.0.0. Every platform grain in docs/plan/04 § Grain taxonomy is null-tenant and durable, so
    ///     that line would fail on the first activation of the tenant directory.
    ///     <para>
    ///         When unset, null-tenant grains hash like any other tenant, using the sentinel string
    ///         as the hash input — deterministic, but arbitrary. Set it explicitly in the global
    ///         cluster.
    ///     </para>
    /// </remarks>
    public string? NullTenantShard { get; set; }

    /// <summary>
    ///     The shard whose connection is opened at silo start to prove the tier is reachable.
    ///     Defaults to the lexicographically first shard.
    /// </summary>
    /// <remarks>
    ///     <c>Orleans.Multitenant</c> requires a "regular (tenant unaware) storage provider" that it
    ///     initialises at silo start and never stores state in. For the ADO.NET provider that means
    ///     one real connection and one <c>SELECT</c> against <c>OrleansQuery</c>, which is a useful
    ///     accident: a silo that cannot reach the durable tier at all fails to start rather than
    ///     failing on the first grain write.
    /// </remarks>
    public string? BootstrapShard { get; set; }

    /// <summary>
    ///     <c>MaxPoolSize</c> per silo per shard. docs/plan/05 § Storage provider wiring fixes this
    ///     at 5 and shows the arithmetic that makes 20 fatal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The doc's arithmetic is right and its unit is subtler than it looks.</b> 30 silos
    ///         × 16 shards × 20 = 9 600. The reason the number is <i>per shard</i> and not
    ///         <i>per tenant</i> is that Npgsql pools by connection string: every tenant on one shard
    ///         must therefore be handed a <b>byte-identical</b> connection string, or the pool count
    ///         multiplies by the tenant count instead of the shard count and the arithmetic is out by
    ///         three or four orders of magnitude. <see cref="ConfiguredShardConnections" /> builds one
    ///         canonical string per shard exactly once for that reason, and
    ///         <c>DurableShardConnectionStringsAreIdenticalAcrossTenantsOnAShard</c> is the test.
    ///     </para>
    /// </remarks>
    public int MaxPoolSize { get; set; } = 5;

    /// <summary>
    ///     Whether the shard is reached through PgBouncer in <b>transaction</b> pooling mode —
    ///     "non-negotiable" per docs/plan/05 § Storage provider wiring.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This flag exists because transaction-mode pooling is not transparent.</b> It
    ///         changes two Npgsql behaviours that are otherwise silent corruptness or silent
    ///         breakage:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <b>Server-side prepared statements.</b> Npgsql 10's <c>Max Auto Prepare</c>
    ///             defaults to <c>0</c> — verified against Npgsql 10.0.3, auto-preparation is
    ///             <i>off</i> unless asked for — and the Orleans ADO.NET provider never calls
    ///             <c>DbCommand.Prepare()</c> (verified by decompiling
    ///             Orleans.Persistence.AdoNet 10.2.2). So the usual PgBouncer/prepared-statement
    ///             collision does not apply here. This setter pins <c>Max Auto Prepare=0</c>
    ///             explicitly anyway, so that a future connection-string edit cannot turn it on by
    ///             accident.
    ///         </item>
    ///         <item>
    ///             <b><c>DISCARD ALL</c> on connection return.</b> Npgsql resets a pooled connection
    ///             by default (<c>No Reset On Close</c> = <c>false</c>), and under transaction-mode
    ///             pooling that reset is both unnecessary — PgBouncer already hands each transaction
    ///             a clean server connection — and, on some PgBouncer versions, an error. Npgsql's
    ///             own guidance is to set <c>No Reset On Close=true</c> behind a transaction-mode
    ///             pooler, which this flag does.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         What the provider does <i>not</i> use, checked one by one against the decompiled
    ///         provider so the compatibility claim is not a guess: no <c>LISTEN</c>/<c>NOTIFY</c>, no
    ///         advisory locks, no session <c>SET</c>, no multi-statement transaction (every operation
    ///         is a single <c>SELECT</c>, <c>UPDATE</c>, <c>DELETE</c> or one call to the
    ///         <c>writetostorage</c> function), and no cursors. Grain storage is therefore compatible
    ///         with transaction-mode pooling — which is a real result, not an assumption, because it
    ///         is the assumption on which the whole 9 600-connection mitigation rests.
    ///     </para>
    /// </remarks>
    public bool PgBouncerTransactionMode { get; set; }
}
