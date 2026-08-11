using CyberCloud.Core.Time;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Directory;
using CyberCloud.Tenancy.Separation;
using CyberCloud.Tenancy.Shards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy;

/// <summary>
///     The silo wiring for tenancy — the call docs/plan/04 § Silo composition marks "not optional",
///     plus the services the tenancy grains need.
/// </summary>
public static class TenancySiloBuilderExtensions
{
    /// <summary>
    ///     Wires <c>AddMultitenantCommunicationSeparation</c> with
    ///     <see cref="PlatformCrossTenantAuthorizer" />.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         docs/plan/04 § Silo composition: "<c>AddMultitenantCommunicationSeparation</c> is not
    ///         optional. Without it, a bug in one provider can read another tenant's grain and
    ///         nothing complains. With it, that is an <c>UnauthorizedAccessException</c> with both
    ///         tenant ids in the message."
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read the second sentence of that promise precisely, because it is exactly true
    ///         and no more.</b> "A bug in one provider" is code running <i>inside a grain</i>, and
    ///         that is the case this closes: <c>Orleans.Multitenant</c>'s
    ///         <c>TenantSeparatingCallFilter</c> is an <c>IIncomingGrainCallFilter</c> that reads
    ///         <c>context.SourceId</c>, and when the source is absent, a client or a system target it
    ///         <b>returns without asking the authorizer at all</b>. So a caller that is not a grain —
    ///         a cluster client, the gateway, a hosted service, a test holding the silo's
    ///         <c>IGrainFactory</c> — can still address any tenant's grain by a raw physical key, and
    ///         this call does not change that.
    ///     </para>
    ///     <para>
    ///         That residue is not a defect in this wiring and cannot be closed here: from an
    ///         incoming-call filter's position there is nothing to attribute a client call to. The
    ///         boundary that closes it is the gateway, which is where a request's tenant is
    ///         established from its token and where <c>ForTenant</c> is chosen — docs/plan/10. Until
    ///         that exists, <b>anything holding an <c>IGrainFactory</c> outside a grain is inside the
    ///         trust boundary.</b> <c>CrossTenantReachabilityTests</c> § route 7 asserts both halves
    ///         so neither can be assumed.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudTenantSeparation(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);

        silo.ConfigureServices(services =>
        {
            // Defaults that DENY. See IPlatformOperatorAuthority — a stand-in that allowed would be
            // a hole with a comment on it. A host that wants the platform edge registers a real one
            // before this call.
            services.TryAddSingleton<IPlatformOperatorAuthority, DenyPlatformOperatorAuthority>();
            services.TryAddSingleton<ICrossTenantDelegationStore, NoCrossTenantDelegations>();
            services.TryAddSingleton<PlatformCrossTenantAuthorizer>();
            services.TryAddSingleton<CyberCloudGrainCallTenantSeparator>();
        });

        return silo.AddMultitenantCommunicationSeparation(
            sp => sp.GetRequiredService<PlatformCrossTenantAuthorizer>(),
            sp => sp.GetRequiredService<CyberCloudGrainCallTenantSeparator>());
    }

    /// <summary>
    ///     Registers the services the tenancy grains and the two in-process mirrors need, and makes
    ///     <see cref="GrainBackedShardMapCache" /> the <see cref="IShardMapCache" />.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="options">The bound <c>CyberCloud:Storage</c> section.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Order matters and it is the reason this method exists at all.</b>
    ///         <c>AddCyberCloudGrainStorage</c> constructs a <c>StaticShardMapCache</c> and captures
    ///         it in the two tier configurators — <c>Orleans.Multitenant</c>'s
    ///         <c>configureTenantOptions</c> has no service provider (see
    ///         <c>TenantOptionsConfigurator</c>), so the dependency is a captured field rather than a
    ///         container lookup. Registering a different <c>IShardMapCache</c> afterwards would
    ///         therefore change what the container hands out and change <b>nothing</b> about what the
    ///         storage layer actually uses. This method must be called <i>instead of</i> the
    ///         configuration-only path, and it calls the storage wiring itself so the cache it builds
    ///         is the cache the configurators capture.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudTenancy(
        this ISiloBuilder silo,
        CyberCloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(options);

        var shardMap = new GrainBackedShardMapCache(options);

        silo.AddCyberCloudGrainStorage(options, shardMap);

        silo.ConfigureServices(services =>
        {
            services.TryAddSingleton<IClock, SystemClock>();
            services.AddOptions<TenancyRefreshOptions>();

            services.AddSingleton(shardMap);
            services.AddSingleton<ShardMapRefresher>();
            services.AddSingleton<TenantDirectoryCache>();

            services.AddHostedService<ShardMapRefreshService>();
            services.AddHostedService<TenantDirectoryRefreshService>();
        });

        return silo.AddCyberCloudTenantSeparation();
    }
}
