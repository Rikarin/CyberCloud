using System.Collections.Concurrent;
using Orleans.Persistence;
using StackExchange.Redis;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The hot tier's <c>configureTenantOptions</c> body — Redis Cluster, one hash tag per tenant
///     (docs/plan/05 § Hot).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One multiplexer for the whole silo, not one per tenant.</b>
///         <c>Orleans.Multitenant</c> builds a separate <c>RedisGrainStorage</c> per tenant, and
///         <c>RedisStorageOptions.CreateMultiplexer</c> defaults to
///         <c>ConnectionMultiplexer.ConnectAsync</c> — so the obvious wiring opens one multiplexer,
///         and therefore one TCP connection per cluster node, <i>per tenant per silo</i>. At the
///         plan's 30 silos and a five-figure tenant count that is the hot tier's version of the
///         9 600-Postgres-connections problem in docs/plan/05 § Storage provider wiring, and the
///         document does not mention it because the document only does the arithmetic for the
///         durable tier. The delegate's second return value exists for exactly this:
///         <c>IsShared: true</c> tells the provider not to dispose what it did not create.
///     </para>
/// </remarks>
public sealed class HotTierConfigurator : IDisposable
{
    readonly IShardMapCache shardMap;
    readonly IShardConnections connections;
    readonly Lazy<Task<IConnectionMultiplexer>> multiplexer;
    readonly ConcurrentDictionary<string, int> invocationsPerTenant = new(StringComparer.Ordinal);

    long invocations;

    /// <summary>Creates the configurator.</summary>
    /// <param name="shardMap">The in-process shard map — supplies the hash tag.</param>
    /// <param name="connections">The connection table — supplies the cluster configuration.</param>
    public HotTierConfigurator(IShardMapCache shardMap, IShardConnections connections)
    {
        ArgumentNullException.ThrowIfNull(shardMap);
        ArgumentNullException.ThrowIfNull(connections);

        this.shardMap = shardMap;
        this.connections = connections;

        multiplexer = new Lazy<Task<IConnectionMultiplexer>>(
            () => ConnectionMultiplexer.ConnectAsync(connections.HotCluster()).ContinueWith(
                t => (IConnectionMultiplexer)t.GetAwaiter().GetResult(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    ///     How many times <see cref="ConfigureForTenant" /> has run since the silo started.
    /// </summary>
    /// <remarks>
    ///     docs/plan/05 § Storage provider wiring claims the callback is "called once per tenant per
    ///     silo, at first touch, and cached by the multitenant storage provider". That claim is what
    ///     keeps the shard lookup off the hot path, so it is counted rather than believed —
    ///     <c>ConfigureTenantOptionsRunsOncePerTenantPerSiloUnderLoad</c> asserts against this.
    /// </remarks>
    public long Invocations => Interlocked.Read(ref invocations);

    /// <summary>Per-tenant invocation counts, for the same reason.</summary>
    public IReadOnlyDictionary<string, int> InvocationsPerTenant => invocationsPerTenant;

    /// <summary>The hash tag each tenant was given, for diagnostics and tests.</summary>
    public ConcurrentDictionary<string, string> HashTagPerTenant { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Fills in one tenant's <c>RedisStorageOptions</c>. This is the callback
    ///     <c>Orleans.Multitenant</c> invokes.
    /// </summary>
    /// <param name="options">The options instance for this tenant only.</param>
    /// <param name="tenantId">
    ///     The tenant id, extracted by <c>Orleans.Multitenant</c> from the grain key — or
    ///     <c>MultitenantStorageOptions.TenantIdForNullTenant</c> for a null-tenant grain.
    /// </param>
    public void ConfigureForTenant(RedisStorageOptions options, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(options);

        Interlocked.Increment(ref invocations);
        invocationsPerTenant.AddOrUpdate(tenantId, 1, (_, previous) => previous + 1);

        var keys = TenantHotKeys.For(shardMap, tenantId);
        HashTagPerTenant[tenantId] = keys.HashTag;

        options.ConfigurationOptions = connections.HotCluster();
        options.GetStorageKey = keys.Key;
        options.CreateMultiplexer = SharedMultiplexerAsync;

        // ⚠ GrainStorageSerializer is deliberately NOT set. Orleans.Multitenant fills an unset one
        // from DI, which gives the Orleans binary serializer. docs/plan/05 § Serialization is
        // explicit that JSON is a *durable*-tier decision — "the hot tier may use MemoryPack where a
        // profile justifies it, because nobody debugs a session by reading it" — so paying JSON's
        // 2-3x on session state would be spending the cost without buying the reason.
    }

    /// <summary>
    ///     Configures the tenant-unaware provider <c>Orleans.Multitenant</c> requires at silo start
    ///     and never stores state in.
    /// </summary>
    /// <param name="options">The bootstrap provider's options.</param>
    /// <remarks>
    ///     It gets the cluster configuration and the shared multiplexer, and a key function that
    ///     throws: nothing should ever store state through this instance, and a key it could
    ///     compute would be a key outside every tenant's hash tag.
    /// </remarks>
    public void ConfigureBootstrap(RedisStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ConfigurationOptions = connections.HotCluster();
        options.CreateMultiplexer = SharedMultiplexerAsync;
        options.GetStorageKey = ThrowingKey;
    }

    /// <summary>
    ///     The extra constructor arguments the per-tenant <c>RedisGrainStorage</c> needs.
    /// </summary>
    /// <param name="services">The silo services.</param>
    /// <param name="providerName">The provider name without the tenant id.</param>
    /// <param name="tenantProviderName">The provider name including the tenant id.</param>
    /// <param name="options">The options, already passed through <see cref="ConfigureForTenant" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Passing the serializer explicitly is not redundant.</b>
    ///         <c>RedisGrainStorage</c>'s constructor takes both a <c>RedisStorageOptions</c> and a
    ///         separate <c>IGrainStorageSerializer</c>. Without this,
    ///         <c>ActivatorUtilities.CreateInstance</c> resolves the second one from the container —
    ///         the silo-wide default — while <c>options.GrainStorageSerializer</c>, which
    ///         <c>Orleans.Multitenant</c> has just filled in for this provider, is ignored. The two
    ///         are the same today; they stop being the same the moment one tier changes format, and
    ///         the symptom then is state that writes in one encoding and reads in another.
    ///     </para>
    /// </remarks>
    public static object[] ProviderParameters(
        IServiceProvider services,
        string providerName,
        string tenantProviderName,
        RedisStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return [options, options.GrainStorageSerializer];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (multiplexer.IsValueCreated && multiplexer.Value.IsCompletedSuccessfully)
        {
            multiplexer.Value.Result.Dispose();
        }
    }

    async Task<(IConnectionMultiplexer Multiplexer, bool IsShared)> SharedMultiplexerAsync(RedisStorageOptions _) =>
        (await multiplexer.Value, true);

    static RedisKey ThrowingKey(string grainType, GrainId grainId) =>
        throw new InvalidOperationException(
            $"The tenant-unaware bootstrap hot-tier provider was asked for a storage key for "
            + $"{grainType}/{grainId}. It exists only to initialise shared dependencies at silo "
            + "start (Orleans.Multitenant's addStorageProvider) and must never store state — a key "
            + "from here would sit outside every tenant's hash tag.");
}
