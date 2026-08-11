namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     The shape of <c>Orleans.Multitenant</c>'s <c>configureTenantOptions</c> callback — the seam
///     the sharded storage wiring plugs into.
/// </summary>
/// <typeparam name="TOptions">
///     The storage provider's options type: <c>RedisStorageOptions</c> for the hot tier,
///     <c>AdoNetGrainStorageOptions</c> for the durable one.
/// </typeparam>
/// <param name="options">The options instance to fill in, for this tenant only.</param>
/// <param name="tenantId">
///     The tenant, <b>extracted from the grain key</b> by <c>Orleans.Multitenant</c>. This is the
///     load-bearing property of the whole scheme (docs/plan/05 § Storage provider wiring): the id
///     arrives from the address of the grain being stored, so a grain physically cannot be written to
///     another tenant's shard — the key is what selects the connection.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>CORRECTED: the callback takes two arguments, not three.</b> This delegate previously
///         declared <c>(IServiceProvider services, TOptions options, string tenantId)</c>, copying
///         docs/plan/04 § Silo composition and docs/plan/05 § Storage provider wiring, which both
///         write the body as <c>(sp, o, tenantId)</c> and resolve <c>IShardMapCache</c> out of
///         <c>sp</c>. The real <c>Orleans.Multitenant</c> 4.0.0 parameter is
///         <c>Action&lt;TGrainStorageOptions, string&gt;</c>. Checked three ways: reflection over the
///         shipped <c>Orleans.Multitenant.dll</c>, the XML documentation in the package, and
///         decompilation of <c>TenantGrainStorageFactory&lt;,,&gt;.Create</c>, which calls
///         <c>configureTenantOptions?.Invoke(options, tenantId)</c>.
///     </para>
///     <para>
///         <b>Where the service provider actually is.</b> There is one, and it reaches the provider
///         through a different parameter: <c>getProviderParameters</c>, of type
///         <c>GrainStorageProviderParametersFactory&lt;TOptions&gt;</c>, is
///         <c>(IServiceProvider services, string providerName, string tenantProviderName, TOptions
///         options) =&gt; object[]</c>. It runs <i>after</i> this callback, so it cannot feed it. The
///         dependencies this callback needs are therefore captured — see
///         <see cref="HotTierConfigurator" /> and <see cref="DurableTierConfigurator" />, which hold
///         <see cref="IShardMapCache" /> and <see cref="IShardConnections" /> as fields and are
///         registered as singletons so that the shard-map refresher of task 8 mutates the same
///         instance the callback reads.
///     </para>
///     <para>
///         <b>Called once per tenant per silo</b>, at first touch. That is not a convention —
///         <c>Orleans.Multitenant</c>'s <c>MultitenantStorage</c> keeps a
///         <c>ConcurrentDictionary&lt;string, IGrainStorage&gt;</c> keyed by
///         <c>grainId.GetTenantId()</c> and builds a provider only on a miss, under a per-tenant
///         <c>AsyncLock</c>. <see cref="HotTierConfigurator.Invocations" /> counts the calls so the
///         claim is asserted rather than trusted.
///     </para>
///     <para>
///         ⚠ <b>The tenant id is a <c>string</c> and it is not always a GUID.</b> For a null-tenant
///         grain — the tenant directory, the shard map, the provider registry — it is
///         <c>MultitenantStorageOptions.TenantIdForNullTenant</c>, <c>"Null"</c> by default. The
///         <c>Guid.Parse(tenantId)</c> in docs/plan/05 § Storage provider wiring throws on those.
///     </para>
/// </remarks>
public delegate void TenantOptionsConfigurator<in TOptions>(TOptions options, string tenantId);
