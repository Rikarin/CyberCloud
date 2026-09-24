using CyberCloud.Billing;
using CyberCloud.Communication;
using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.Principals;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.Identity.Validation;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tunnel;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults.RateLimiting;
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
    ///         ⚠
    ///         <b>
    ///             The stages are registered in document order and the order is checked
    ///             anyway.
    ///         </b> Registration order is what <c>IEnumerable&lt;IGatewayStage&gt;</c> resolves
    ///         to, so keeping these lines in order is <i>necessary</i> — and
    ///         <see cref="GatewayPipeline" /> sorts and validates regardless, because a rule upheld
    ///         by the order of eight lines in a file is a rule one merge away from being wrong.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The resource manager is composed by <b>one call into its own assembly</b> and not
    ///             by a list repeated here.
    ///         </b> <c>AddCyberCloudResourceManager</c> has an
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
        if (services.Any(static x => x.ServiceType == typeof(IConnectionMultiplexer))) {
            services.TryAddSingleton<IRateLimitCounters, RedisRateLimitCounters>();
        } else {
            services.TryAddSingleton<IRateLimitCounters, InMemoryRateLimitCounters>();
        }

        services.TryAddSingleton<GatewayRateLimiter>();
        services.TryAddSingleton(new ConcurrencyLimits());
        services.TryAddSingleton<IConcurrencyLimiter, ProcessConcurrencyLimiter>();

        // ── The hub ticket a browser opens a WebSocket with — docs/plan/10 § SignalR, HubTickets.
        //    Redis when configured and in-process otherwise, by the rule stage 5's counters use and
        //    for the same reason: a ticket minted on one pod must be redeemable on the pod the
        //    upgrade lands on, and only a shared store makes that true. ──
        if (services.Any(static x => x.ServiceType == typeof(IConnectionMultiplexer))) {
            services.TryAddSingleton<IHubTicketStore, RedisHubTicketStore>();
        } else {
            services.TryAddSingleton<IHubTicketStore, InMemoryHubTicketStore>();
        }

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

        // ⚠ THE PRINCIPAL DIRECTORY IS Replace, NOT TryAdd, AND THAT IS WHAT MAKES THE ORDER FREE.
        // AddCyberCloudResourceManager TryAdds a refusing IPrincipalDirectory. This line used to be
        // a TryAdd placed BEFORE that call, which was correct and held by nothing: swap the two lines
        // and the refusal stays, every PUT on a role assignment answers 500 naming that call, and
        // both host suites stay green — the review of #86 ran exactly that sabotage. Replace is the
        // shape the vault (AddOpenBaoSecretResolver) and the OTP seam (AddCommunicationOtpDelivery)
        // already use over the same kind of default: it wins in either order and leaves exactly one
        // descriptor, so nothing taking IEnumerable<IPrincipalDirectory> can meet the refusal behind
        // the real one. HostCompositionTests.TheGatewayWiresTheDirectoryAndTheSiloKeepsTheRefusal
        // pins the composed result, which is the half a comment cannot. GrainPrincipalDirectory is
        // the adapter over the identity grains that only a host referencing both assemblies can
        // write — its remarks say why it is here and not in either module.
        services.Replace(ServiceDescriptor.Singleton<IPrincipalDirectory, GrainPrincipalDirectory>());

        // #43: the invitation issuer, Replace for the directory's reason — the manager TryAdds a
        // refusing one, and an invite on a gateway that kept it would be checked and then refused.
        services.Replace(ServiceDescriptor.Singleton<IInvitationIssuer, GrainInvitationIssuer>());

        // #41: the identity directory behind the administration API, Replace for the same reason —
        // the manager TryAdds a refusing one, and every page would be checked and then refused.
        services.Replace(ServiceDescriptor.Singleton<IIdentityDirectory, GrainIdentityDirectory>());

        // ⚠ THE SEAMS CyberCloud.Communication/services' SYNCHRONOUS ACTIONS HOLD, AND THIS HOST IS
        // WHERE THEY RUN. A synchronous action is served inside ResourceManagerService.ActionAsync,
        // in this process; `send`, `status`, `checkSuppression` and `listSuppressions` reach the
        // sending domain's grains through IMessageSender and ICommunicationControlPlane, and this
        // is the one registration that provides them — over this host's cluster client, hosting no
        // grain. The silo gets the same two through AddCyberCloudCommunication. HostCompositionTests
        // resolves both here so that a host that forgot this line fails in a test rather than on the
        // first send.
        services.AddCyberCloudCommunicationClient();

        // ── The cost query, docs/plan/22 § Cost visibility (#38). ──
        //
        // ⚠ DISPATCH HOLDS ICostQuery AND NOTHING ELSE OF BILLING. The grain behind it prices the
        // subscription's usage and filters every row by ReBAC on the silo; this host copies the caller
        // across and renders what comes back, and checks nothing — docs/plan/10 § Request pipeline's one
        // enforcement seam. IBudgetControlPlane comes with it because the budget reconciler is
        // registered in this container as well as the silo's (the registry is built the same way in
        // both), and a type whose reconciler cannot be constructed here would fail the first resolve.
        services.AddCyberCloudBillingClient();

        // ── SignalR. docs/plan/10 § SignalR — no backplane product, by design. ──
        //
        // ⚠ NOTHING TO REGISTER HERE ANY MORE, AND THE ABSENCE IS THE POINT. IConnectionGrain and its
        // interest authorizer are activated by a SILO, not by this client, so their registrations are
        // in AddCyberCloudResourceManager above — the same list that already registers the write
        // path's grain-side dependencies. This host builds the key, holds the hubs, and takes grain
        // references through ForTenant; it activates nothing.

        // ── The nine stages, in the order docs/plan/10 § Request pipeline gives them. ──
        // ── The agent tunnel (#36) — docs/plan/09 § Cluster connections, the AgentInitiated row ──
        //
        // The relay admits an agent through IAgentTunnelGrain and pumps its socket; the seam is what
        // listInstallCommand mints through, served inline in this process like every other action.
        // ⚠ BEFORE AddCyberCloudResourceManager above? No — after, and it still wins, because that
        // method registers UnavailableAgentTunnels with TryAdd and this is an AddSingleton that
        // REPLACES nothing: TryAdd left the refusing default in place, so this one is a second
        // registration and the last one wins on resolve. Registered before would also work; what
        // would NOT work is TryAdd here.
        services.AddSingleton<AgentTunnelRelay>();
        services.AddSingleton<IAgentTunnels, GrainAgentTunnels>();
        services.AddOptions<AgentTunnelOptions>()
            .BindConfiguration(AgentTunnelOptions.SectionName)
            .PostConfigure(agent => {
                    // The install command points at the gateway that minted it unless a
                    // deployment says otherwise.
                    if (string.IsNullOrWhiteSpace(agent.TunnelEndpoint)) {
                        agent.TunnelEndpoint = new Uri(new(options.PublicBaseUri), TunnelCodec.TunnelPath)
                            .ToString()
                            .Replace("https://", "wss://", StringComparison.Ordinal)
                            .Replace("http://", "ws://", StringComparison.Ordinal);
                    }
                }
            );

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
    ///     Registers the production identity seam: bearer JWTs validated against the identity host's
    ///     published key set. docs/plan/11 § Protocol, docs/plan/10 § Request pipeline, stage 2.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="identity">Which identity host to trust — the issuer and the audience to pin.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Separate from <see cref="AddCyberCloudGateway" />, and called by
    ///             <c>GatewayComposition.BuildAsync</c> only when <c>CyberCloud:Gateway:Identity</c>
    ///             names an issuer.
    ///         </b> A gateway told no issuer has nothing to validate against, and registering a
    ///         resolver anyway would mean choosing a default origin for it — see
    ///         <see cref="GatewayIdentityOptions" /> for why there is none. What the composition does
    ///         instead is refuse to build, naming the section, which is the loud start-up failure
    ///         https://github.com/Rikarin/CyberCloud/issues/68 asked for in place of the silent
    ///         <c>500</c> that a missing <see cref="ICallerContextResolver" /> used to produce.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>OpenIddict.Validation.SystemNetHttp</c> and deliberately not
    ///             <c>OpenIddict.Server.*</c> — the package-level expression of docs/plan/11
    ///             § Hosts' boundary.
    ///         </b> The gateway serves bearer tokens and mints none; a gateway that could issue a
    ///         token would be a second authorization server on the origin whose entire job is to
    ///         accept them. Nor is <c>OpenIddict.Validation.AspNetCore</c> here: that package is an
    ///         ASP.NET Core authentication handler, and stage 2 is not one — the pipeline resolves
    ///         <see cref="ICallerContextResolver" /> itself, so the handler would register a scheme
    ///         nothing consults. <see cref="JwksCallerContextResolver" /> reads the header and hands
    ///         the bytes to <c>IBearerTokenValidator</c>, which calls the validation service directly.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The validator is <c>CyberCloud.Identity.Validation</c>'s, shared with the feeds
    ///             host, and this method is what makes it the gateway's stage 2.
    ///         </b> <c>AddJwksBearerTokenValidation</c> registers OpenIddict and the JWKS validator;
    ///         the line after it registers the one adapter that is this host's — the resolver that
    ///         reads the <c>Authorization</c> header and nothing else. A host that composed the
    ///         validator without the adapter would have a token validator nothing calls, which is
    ///         the shape #68 was.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The in-process token table this method used to be about is gone from every host
    ///             and every composition.
    ///         </b> <c>AddIssuedTokenAuthentication</c> registered
    ///         <see cref="IssuedTokenCallerContextResolver" />, whose tokens come from a dictionary
    ///         that is empty in every replica and different in each; its one caller was a test, and
    ///         that test now takes a real token from the real identity host. The resolver itself
    ///         stays, <c>internal</c>, as the sibling suite's stage-2 double — a test that drives the
    ///         nine stages against a substituted manager has no reason to sign anything.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddJwksAuthentication(
        this IServiceCollection services,
        GatewayIdentityOptions identity
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(identity);

        if (!identity.IsConfigured) {
            throw new ArgumentException(
                $"{GatewayIdentityOptions.SectionName}:Issuer is empty. This method registers a "
                + "resolver that validates against that host's key set, and there is no default "
                + "host to validate against.",
                nameof(identity)
            );
        }

        // Pins the issuer and the audience, registers OpenIddict's validation over HttpClient and
        // the JWKS validator — items 1 and 2 of ICallerContextResolver's list, met once for every
        // host that accepts a platform token.
        services.AddJwksBearerTokenValidation(identity.ToBearerTokenOptions(), GatewayIdentityOptions.SectionName);

        services.TryAddSingleton<ICallerContextResolver, JwksCallerContextResolver>();

        return services;
    }
}
