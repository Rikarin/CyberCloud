using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Authentication;
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
    ///         ⚠ <b>The resource manager is composed by <b>one call into its own assembly</b> and not
    ///         by a list repeated here.</b> <c>AddCyberCloudResourceManager</c> has an
    ///         <c>IServiceCollection</c> overload for exactly this: the silo overload takes an
    ///         <c>ISiloBuilder</c> (docs/plan/04 § Silo composition) and the gateway is a client, so
    ///         there is no builder to hand it. Repeating the registrations here would mean naming
    ///         <c>ReBacResourceAuthorizer</c> in gateway source, which
    ///         <c>GatewayIsolationTests.NoGatewaySourceFileCallsAnAuthorizationEngine</c> refuses.
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
        //
        // ⚠ NOTHING TO REGISTER HERE ANY MORE, AND THE ABSENCE IS THE POINT. IConnectionGrain and its
        // interest authorizer are activated by a SILO, not by this client, so their registrations are
        // in AddCyberCloudResourceManager above — the same list that already registers the write
        // path's grain-side dependencies. This host builds the key, holds the hubs, and takes grain
        // references through ForTenant; it activates nothing.

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
    ///     <para>
    ///         ⚠ <b>Separate from <see cref="AddCyberCloudGateway" /> so that a production host
    ///         cannot get it by accident.</b> A host that calls only <c>AddCyberCloudGateway</c> has
    ///         no <see cref="ICallerContextResolver" /> registered and cannot resolve the pipeline —
    ///         which is the failure you want, rather than a gateway that authenticates nobody and
    ///         serves anyway.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THAT FAILURE ARRIVES AT THE FIRST REQUEST AND NOT AT START-UP, and this remark
    ///         used to say the opposite.</b> The pipeline is a singleton the one middleware resolves
    ///         per request, and <c>OrleansApplication.CreateClient</c> calls
    ///         <c>builder.Host.UseAutofac()</c> — so ASP.NET Core's <c>ValidateOnBuild</c>, which
    ///         belongs to the default provider factory, never runs and cannot catch it. A gateway
    ///         with no identity implementation therefore starts, passes its health checks, and
    ///         answers <c>500</c> to everything else. ⚠ <b>And no host in this tree calls this
    ///         method</b>: its only caller is <c>CyberCloud.AppHost.Tests</c>'
    ///         <c>TenantOverHttpTests</c>, through <c>GatewayComposition.BuildAsync</c>'s
    ///         <c>configure</c> parameter. Until <c>CyberCloud.Identity.Host</c> issues real tokens
    ///         (docs/plan/11), the shipping gateway can serve no authenticated request at all — which
    ///         is a state to leave deliberately rather than to discover. Tracked as
    ///         https://github.com/Rikarin/CyberCloud/issues/68.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Calling this method from a host is NOT the fix, and it is the tempting one.</b>
    ///         The tokens below come from an in-process dictionary, so a gateway that registered
    ///         this would authenticate against a table that is empty in every replica and different
    ///         in each — worse than the <c>500</c> precisely because it would <i>work</i>, and would
    ///         answer <c>401</c> rather than failing. #68 carries the two real options: a
    ///         composition-time refusal that names the missing registration, or the identity host
    ///         and JWKS validation that docs/plan/11 budgets.
    ///     </para>
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
