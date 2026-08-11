using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.ResourceManager;
using CyberCloud.Tenancy;
using CyberCloud.Tenancy.Directory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace CyberCloud.Gateway.Host;

/// <summary>
///     Everything the gateway registers. docs/plan/10.
/// </summary>
static class GatewayServiceCollectionExtensions {
    /// <summary>
    ///     Registers the nine stages, the rate limiter, the tenant directory and the resource
    ///     manager the gateway dispatches to.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The pod's configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The stages are registered in document order and the order is checked
    ///         anyway.</b> Registration order is what <c>IEnumerable&lt;IGatewayStage&gt;</c> resolves
    ///         to, so keeping these lines in order is <i>necessary</i> — and
    ///         <see cref="GatewayPipeline" /> sorts and validates regardless, because a rule upheld
    ///         by the order of eight lines in a file is a rule one merge away from being wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The resource manager is composed here rather than through
    ///         <c>AddCyberCloudResourceManager</c>.</b> That extension takes an <c>ISiloBuilder</c>
    ///         (docs/plan/04 § Silo composition) and the gateway is a client, so there is no builder
    ///         to hand it. The registrations below are the subset a <i>caller</i> needs — the write
    ///         path and its seams, without the reconcile driver or the drift scanner, which are
    ///         silo-side. A client-side counterpart in <c>CyberCloud.ResourceManager</c> would be
    ///         better than this duplication and is owed.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddCyberCloudGateway(
        this IServiceCollection services,
        GatewayOptions options
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton<IClock, SystemClock>();

        // ── The tenant directory. docs/plan/05 § The tenant directory — the in-process snapshot
        //    stage 3 reads, plus the background delta feed that keeps it fresh. ──
        services.TryAddSingleton<TenantDirectoryCache>();
        services.AddOptions<TenancyRefreshOptions>();
        services.AddHostedService<TenantDirectoryRefreshService>();

        // ── Stage 5. Redis when configured, in-process otherwise; see InMemoryRateLimitCounters on
        //    exactly what the second one is and is not. ──
        if (services.Any(x => x.ServiceType == typeof(IConnectionMultiplexer))) {
            services.TryAddSingleton<IRateLimitCounters, RedisRateLimitCounters>();
        } else {
            services.TryAddSingleton<IRateLimitCounters, InMemoryRateLimitCounters>();
        }

        services.TryAddSingleton<GatewayRateLimiter>();
        services.TryAddSingleton(new ConcurrencyLimits());
        services.TryAddSingleton<IConcurrencyLimiter, ProcessConcurrencyLimiter>();

        // ── Stage 4's seam. docs/plan/10 § Request pipeline — the decision is implemented; the hop
        //    needs two regions and is configuration. ──
        services.TryAddSingleton<IRegionProxy, UnconfiguredRegionProxy>();

        // ── Stage 8. ──
        //
        // ⚠ ONE CALL, INTO THE ASSEMBLY THAT OWNS THE SEAM, AND THAT IS NOT TIDINESS.
        // Composing the manager here would mean naming ReBacResourceAuthorizer in gateway source —
        // docs/plan/10 § What the gateway must never do puts authorization in one place, inside the
        // resource manager, and a registration line is still a line that has to change when the
        // engine changes. GatewayIsolationTests reads this project's source for that name.
        services.AddCyberCloudResourceManager();
        services.TryAddSingleton<IOperationReader, TenantScopedOperationReader>();

        // ── SignalR. docs/plan/10 § SignalR — no backplane product, by design. ──
        services.TryAddSingleton<IInterestAuthorizer, ResourceManagerInterestAuthorizer>();

        // ── The nine stages, in the order docs/plan/10 § Request pipeline gives them. ──
        services.AddSingleton<IGatewayStage, CorrelationStage>();
        services.AddSingleton<IGatewayStage, AuthenticateStage>();
        services.AddSingleton<IGatewayStage, ResolveTenantStage>();
        services.AddSingleton<IGatewayStage, RegionRoutingStage>();
        services.AddSingleton<IGatewayStage, RateLimitStage>();
        services.AddSingleton<IGatewayStage, RouteStage>();
        services.AddSingleton<IGatewayStage, ValidateStage>();
        services.AddSingleton<IGatewayStage, DispatchStage>();
        services.AddSingleton<GatewayPipeline>();

        return services;
    }

    /// <summary>
    ///     Registers the test implementation of the identity seam.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="AddCyberCloudGateway" /> so that a production host cannot
    ///     get it by accident.</b> A host that calls only <c>AddCyberCloudGateway</c> has no
    ///     <see cref="ICallerContextResolver" /> registered and fails to resolve the pipeline at
    ///     startup — which is the failure you want, rather than a gateway that authenticates nobody
    ///     and serves anyway.
    /// </remarks>
    public static IServiceCollection AddIssuedTokenAuthentication(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IssuedTokenCallerContextResolver>();
        services.TryAddSingleton<ICallerContextResolver>(
            provider => provider.GetRequiredService<IssuedTokenCallerContextResolver>()
        );

        return services;
    }
}
