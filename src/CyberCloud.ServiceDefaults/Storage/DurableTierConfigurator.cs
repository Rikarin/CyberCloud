using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Storage;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The durable tier's <c>configureTenantOptions</c> body — the five lines docs/plan/05 § Storage
///     provider wiring calls load-bearing, with the three corrections they need to compile and run.
/// </summary>
/// <remarks>
///     <para>
///         The document's version, for comparison:
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
///     <list type="number">
///         <item>
///             <b>There is no <c>IServiceProvider</c> parameter.</b> The real callback is
///             <c>Action&lt;TOptions, string&gt;</c> — two arguments. Verified against
///             Orleans.Multitenant 4.0.0 by reflection and by decompilation; docs/plan/04 § Silo
///             composition and docs/plan/05 § Storage provider wiring both show three. The
///             dependencies are therefore captured in the instance rather than resolved per call,
///             which is also strictly better: it is one field read instead of a container lookup on
///             a path that runs under a per-tenant lock.
///         </item>
///         <item>
///             <b><c>Guid.Parse(tenantId)</c> throws for platform grains.</b> Every grain in the
///             Platform row of docs/plan/04 § Grain taxonomy — the tenant directory, the shard map,
///             the provider registry — is null-tenant and durable, and the tenant id
///             <c>Orleans.Multitenant</c> passes for those is the literal
///             <c>MultitenantStorageOptions.TenantIdForNullTenant</c>, <c>"Null"</c> by default.
///             <see cref="IShardMapCache" /> therefore takes the string.
///         </item>
///         <item>
///             <b><c>OrleansJsonGrainStorageSerializer</c> does not exist</b> in Orleans 10.2.2 —
///             see <see cref="SystemTextJsonGrainStorageSerializer" />.
///         </item>
///     </list>
/// </remarks>
public sealed class DurableTierConfigurator
{
    static readonly SystemTextJsonGrainStorageSerializer Serializer = new();

    /// <summary>
    ///     The grain-id hasher the ADO.NET provider uses to fill <c>GrainIdHash</c> and
    ///     <c>GrainTypeHash</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This has to be set by hand, and the reason generalises.</b> Orleans normally
    ///         supplies it through <c>DefaultAdoNetGrainStorageOptionsHashPickerConfigurator</c>, an
    ///         <c>IPostConfigureOptions&lt;AdoNetGrainStorageOptions&gt;</c> registered by
    ///         <c>AddAdoNetGrainStorage</c>. <c>Orleans.Multitenant</c> does not go through the
    ///         options system for per-tenant providers at all — it <c>new</c>s the options object and
    ///         hands it to <c>configureTenantOptions</c> — so <b>every</b> Orleans default that
    ///         arrives via <c>IPostConfigureOptions</c> or <c>IConfigureNamedOptions</c> is simply
    ///         absent on the tenant instances. The observed failure is
    ///         <c>OrleansConfigurationException: Invalid AdoNetGrainStorageOptions values for
    ///         AdoNetGrainStorage &lt;tenantId&gt;. HashPicker is required.</c> on the first durable
    ///         write of the first tenant.
    ///     </para>
    ///     <para>
    ///         Neither docs/plan/04 § Silo composition nor docs/plan/05 § Storage provider wiring
    ///         mentions it, and the five-line body in the latter would hit it immediately.
    ///     </para>
    ///     <para>
    ///         The value matches Orleans' default exactly — <c>OrleansDefaultHasher</c>, which is
    ///         Jenkins one-at-a-time — so rows written here are readable by a plain
    ///         <c>AdoNetGrainStorage</c> and vice versa. Changing it is a re-hash of every row.
    ///     </para>
    /// </remarks>
    static readonly IStorageHasherPicker HashPicker = new StorageHasherPicker([new OrleansDefaultHasher()]);

    /// <summary>
    ///     The ADO.NET invariant name for Npgsql. docs/plan/05 § Storage provider wiring spells it
    ///     as a literal and so does this, because Orleans'
    ///     <c>AdoNetInvariants.InvariantNamePostgreSql</c> is <c>internal</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ The provider resolves this by <b>reflection over an assembly name</b>, not by a
    ///     compile-time reference: <c>DbConnectionFactory.GetFactory("Npgsql")</c> does
    ///     <c>Assembly.Load("Npgsql")</c> and reads the static <c>Instance</c> field of
    ///     <c>Npgsql.NpgsqlFactory</c>. So the <c>Npgsql</c> package must be in the host's output
    ///     even though nothing in the storage path binds to a type from it, and a typo in this string
    ///     is a runtime <c>AggregateException</c> at first grain write rather than a compile error.
    /// </remarks>
    public const string NpgsqlInvariant = "Npgsql";

    readonly IShardMapCache shardMap;
    readonly IShardConnections connections;
    readonly ConcurrentDictionary<string, int> invocationsPerTenant = new(StringComparer.Ordinal);

    long invocations;

    /// <summary>Creates the configurator.</summary>
    /// <param name="shardMap">The in-process shard map — supplies the shard id.</param>
    /// <param name="connections">The connection table — supplies the shard's connection string.</param>
    public DurableTierConfigurator(IShardMapCache shardMap, IShardConnections connections)
    {
        ArgumentNullException.ThrowIfNull(shardMap);
        ArgumentNullException.ThrowIfNull(connections);

        this.shardMap = shardMap;
        this.connections = connections;
    }

    /// <summary>How many times <see cref="ConfigureForTenant" /> has run since the silo started.</summary>
    public long Invocations => Interlocked.Read(ref invocations);

    /// <summary>Per-tenant invocation counts.</summary>
    public IReadOnlyDictionary<string, int> InvocationsPerTenant => invocationsPerTenant;

    /// <summary>The shard each tenant was routed to, for diagnostics and tests.</summary>
    public ConcurrentDictionary<string, string> ShardPerTenant { get; } = new(StringComparer.Ordinal);

    /// <summary>Fills in one tenant's <c>AdoNetGrainStorageOptions</c>.</summary>
    /// <param name="options">The options instance for this tenant only.</param>
    /// <param name="tenantId">The tenant id extracted from the grain key.</param>
    public void ConfigureForTenant(AdoNetGrainStorageOptions options, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(options);

        Interlocked.Increment(ref invocations);
        invocationsPerTenant.AddOrUpdate(tenantId, 1, (_, previous) => previous + 1);

        var shard = shardMap.DurableShardFor(tenantId);
        ShardPerTenant[tenantId] = shard;

        options.ConnectionString = connections.Durable(shard);
        options.Invariant = NpgsqlInvariant;
        options.GrainStorageSerializer = Serializer;
        options.HashPicker = HashPicker;

        // ⚠ Run by hand, because Orleans.Multitenant will not run it for this tier. Its
        // TenantGrainStorageFactory only invokes TGrainStorageOptionsValidator when the constructor
        // argument list it built contains a raw TGrainStorageOptions — and ProviderParameters below
        // has to replace the raw options with IOptions<>, so it never does. Silently skipping
        // validation is not the same as validating, and an empty connection string here is a tenant
        // whose durable writes go nowhere.
        new AdoNetGrainStorageOptionsValidator(options, tenantId).ValidateConfiguration();
    }

    /// <summary>
    ///     Configures the tenant-unaware provider <c>Orleans.Multitenant</c> requires at silo start.
    /// </summary>
    /// <param name="options">The bootstrap provider's options.</param>
    /// <param name="bootstrapShard">The shard to point it at.</param>
    public void ConfigureBootstrap(AdoNetGrainStorageOptions options, string bootstrapShard)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ConnectionString = connections.Durable(bootstrapShard);
        options.Invariant = NpgsqlInvariant;
        options.GrainStorageSerializer = Serializer;
        options.HashPicker = HashPicker;
    }

    /// <summary>
    ///     The constructor arguments the per-tenant <c>AdoNetGrainStorage</c> needs.
    /// </summary>
    /// <param name="services">The silo services.</param>
    /// <param name="providerName">The provider name without the tenant id.</param>
    /// <param name="tenantProviderName">The provider name including the tenant id.</param>
    /// <param name="options">The options, already passed through <see cref="ConfigureForTenant" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this, every tenant silently shares the bootstrap shard.</b>
    ///         <c>AdoNetGrainStorage</c>'s constructor does not take an
    ///         <c>AdoNetGrainStorageOptions</c> — it takes an
    ///         <c>IOptions&lt;AdoNetGrainStorageOptions&gt;</c>. <c>Orleans.Multitenant</c>'s default
    ///         behaviour is to pass the raw options object, which does not satisfy that parameter, so
    ///         <c>ActivatorUtilities</c> resolves <c>IOptions&lt;AdoNetGrainStorageOptions&gt;</c>
    ///         from the container instead and the per-tenant connection string is discarded — every
    ///         tenant then reads and writes the same database, with no error and no log line. Wrapping
    ///         it here is the entire difference between sharded and not.
    ///     </para>
    /// </remarks>
    public static object[] ProviderParameters(
        IServiceProvider services,
        string providerName,
        string tenantProviderName,
        AdoNetGrainStorageOptions options) =>
        [Options.Create(options)];
}
