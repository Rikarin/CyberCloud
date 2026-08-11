using CyberCloud.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Multitenant;
using Orleans.Persistence;
using Orleans.Storage;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Registers the two grain-storage tiers of docs/plan/05 § The two tiers, each behind
///     <c>Orleans.Multitenant</c> so that the tenant id in the grain key is what selects the store.
/// </summary>
public static class SiloBuilderStorageExtensions
{
    /// <summary>
    ///     Orleans' name for the unnamed provider. <c>Orleans.Multitenant</c>'s
    ///     <c>AddMultitenantGrainStorageAsDefault</c> hardcodes this string, and Orleans'
    ///     <c>ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME</c> is not public, so it is restated.
    /// </summary>
    const string DefaultProviderName = "Default";

    /// <summary>
    ///     Wires the hot and durable tiers onto a silo.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="options">The bound <c>CyberCloud:Storage</c> section.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An unreachable store does NOT stop the silo starting, and that was checked
    ///         rather than assumed.</b> <c>Orleans.Multitenant</c> takes a tenant-unaware "bootstrap"
    ///         provider per tier, documented as being initialised at silo start — but the bare
    ///         provider it registers is never actually constructed, because
    ///         <c>Orleans.Multitenant</c> re-registers the same keyed <c>IGrainStorage</c> afterwards
    ///         and the lifecycle participant resolves to the wrapper. A silo whose durable shard is
    ///         down therefore starts, reports ready and takes traffic; the failure lands on the grain
    ///         activations for that shard's tenants. <c>BootstrapProviderLivenessTests</c> is that
    ///         result, and names the consequence: <b>nothing here checks shard reachability before a
    ///         silo is declared ready</b>, and if that is wanted it needs a per-shard health check.
    ///         This is still not called for you —
    ///         <c>OrleansApplication.CreateSilo</c> calls it only when the configuration section is
    ///         present, so a silo with no such section has <b>no grain storage at all</b>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The hot tier is the default provider, and <c>StorageTiers.Hot</c> is an alias for
    ///         it.</b> docs/plan/04 § Silo composition writes
    ///         <c>AddMultitenantGrainStorageAsDefault&lt;…&gt;(StorageTiers.Hot, …)</c>, which does
    ///         not compile: the real <c>AsDefault</c> overload takes no provider name — it registers
    ///         under the literal <c>"Default"</c>, because that is what "default" means to Orleans.
    ///         Left there, <c>[PersistentState("state", StorageTiers.Hot)]</c> would resolve nothing
    ///         and fail at the first activation of a hot grain. The keyed alias below is what makes
    ///         both spellings reach one provider instance and one Redis multiplexer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddMultitenantCommunicationSeparation</c> is not here.</b> docs/plan/04
    ///         § Silo composition calls it "not optional" and it belongs in the same
    ///         <c>UseOrleans</c> block, but its argument is an <c>ICrossTenantAuthorizer</c> and
    ///         authorization is <c>CyberCloud.Authorization</c>'s (ADR-007) — referencing it here
    ///         would invert the assembly graph. It plugs in at the call site, next to this call:
    ///         <c>silo.AddCyberCloudGrainStorage(o).AddMultitenantCommunicationSeparation(sp =&gt; …)</c>.
    ///         Until it is there, storage separation holds (a grain's key selects its store) but
    ///         <i>call</i> separation does not — see
    ///         <c>ARawGrainKeyReachesTheOtherTenantsStateAndOnlyCommunicationSeparationStopsIt</c>,
    ///         which demonstrates exactly what is still open.
    ///     </para>
    /// </remarks>
    /// <param name="map">
    ///     The shard map the two tier configurators will <b>capture</b>. Defaults to a
    ///     <see cref="StaticShardMapCache" />.
    /// </param>
    public static ISiloBuilder AddCyberCloudGrainStorage(
        this ISiloBuilder silo,
        CyberCloudStorageOptions options,
        IShardMapCache? map = null)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(options);

        // ⚠ CAPTURED, not resolved. Orleans.Multitenant's configureTenantOptions callback takes
        // (options, tenantId) and no IServiceProvider — see TenantOptionsConfigurator — so the
        // instance handed in here is the instance the storage layer will use for the life of the
        // silo. Registering a different IShardMapCache in the container afterwards changes what
        // other code resolves and changes nothing about where a grain is stored. That is why the
        // parameter is here rather than a container lookup: swapping the map has to be a decision at
        // this call site, where it is visible.
        var shardMap = map ?? new StaticShardMapCache(options);
        var connections = new ConfiguredShardConnections(options);
        var hot = new HotTierConfigurator(shardMap, connections);
        var durable = new DurableTierConfigurator(shardMap, connections);

        var bootstrapShard = options.Durable.BootstrapShard
            ?? options.Durable.Shards.Keys.Order(StringComparer.Ordinal).First();

        silo.ConfigureServices(services =>
        {
            services.AddSingleton(options);
            services.AddSingleton(shardMap);
            services.AddSingleton<IShardConnections>(connections);
            services.AddSingleton(hot);
            services.AddSingleton(durable);
        });

        // ── Hot ── docs/plan/05 § Hot.
        silo.AddMultitenantGrainStorageAsDefault<RedisGrainStorage, RedisStorageOptions, HotTierStorageOptionsValidator>(
            (builder, name) => builder.AddRedisGrainStorage(name, hot.ConfigureBootstrap),
            hot.ConfigureForTenant,
            HotTierConfigurator.ProviderParameters);

        // ── Durable ── docs/plan/05 § Durable.
        silo.AddMultitenantGrainStorage<AdoNetGrainStorage, AdoNetGrainStorageOptions, AdoNetGrainStorageOptionsValidator>(
            StorageTiers.Durable,
            (builder, name) => builder.AddAdoNetGrainStorage(
                name,
                bootstrap => durable.ConfigureBootstrap(bootstrap, bootstrapShard)),
            durable.ConfigureForTenant,
            DurableTierConfigurator.ProviderParameters);

        silo.ConfigureServices(services =>
        {
            // StorageTiers.Hot → the default provider. AddKeyedSingleton, not TryAdd: registration
            // order decides, and last-one-wins is the only rule here that does not depend on which
            // extension method ran first.
            services.AddKeyedSingleton<IGrainStorage>(
                StorageTiers.Hot,
                (sp, _) => sp.GetRequiredKeyedService<IGrainStorage>(DefaultProviderName));

            // ⚠ And the unkeyed one, for the same reason in the other direction.
            // AddRedisGrainStorage("Default") does a TryAddSingleton of the *plain* provider into the
            // unkeyed slot, and Orleans.Multitenant's own TryAddSingleton then loses the race, so
            // `GetService<IGrainStorage>()` would hand back the tenant-unaware bootstrap instance —
            // one that writes outside every tenant's hash tag. Nothing on the grain path reads the
            // unkeyed registration (Orleans resolves storage by key), but leaving a loaded gun in the
            // container for the next person to resolve is not a saving.
            services.AddSingleton(sp => sp.GetRequiredKeyedService<IGrainStorage>(DefaultProviderName));
        });

        return silo;
    }
}
