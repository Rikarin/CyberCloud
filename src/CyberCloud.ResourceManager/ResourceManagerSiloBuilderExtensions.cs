using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ResourceManager.Drift;
using CyberCloud.ResourceManager.Grains;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The silo wiring for the resource manager. docs/plan/04 § Silo composition.
/// </summary>
public static class ResourceManagerSiloBuilderExtensions {
    /// <summary>
    ///     Registers the write path, the registry built from every provider in the container, the
    ///     reconcile driver and the defaults for every seam that has no implementation yet.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Call this <i>after</i> every provider has been registered.</b> The registry is
    ///         built once from every <see cref="IResourceProvider" /> in the container, and a provider
    ///         registered afterwards would not be in it — its endpoints would answer <c>404</c> with
    ///         nothing in the log. The registry is a singleton with a factory rather than an instance
    ///         precisely so the build happens at first resolve, which is after wiring finishes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every seam gets a default and every default is honest about what it is.</b>
    ///         <see cref="NotSupportedPolicyEvaluator" /> says no policy engine ran rather than
    ///         allowing; <see cref="UnavailableSecretResolver" /> refuses rather than returning empty;
    ///         <see cref="UnavailableClusterObjectInventory" /> fails rather than reporting an empty
    ///         cluster. Each of those is a place where the plausible default is the dangerous one, and
    ///         the reasons are on the types. <c>TryAdd</c> throughout, so a host that has the real
    ///         thing registers it first and keeps it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reminders must be configured by the host.</b> The operation grain is
    ///         <c>IRemindable</c>, and a silo with no reminder service throws on
    ///         <c>RegisterOrUpdateReminder</c>. This method does not choose one — the production choice
    ///         is Redis (docs/plan/04 § Reminders) and a test's is
    ///         <c>UseInMemoryReminderService</c>, and picking either here would take the decision away
    ///         from the host that knows which it is.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudResourceManager(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => services.AddCyberCloudResourceManager());
    }

    /// <summary>
    ///     The same registrations, for a host that has no <see cref="ISiloBuilder" /> to hang them on.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because the gateway is an Orleans <i>client</i>, and the reason it is
    ///         <i>here</i> rather than in the gateway is the enforcement seam.</b> docs/plan/08 § The
    ///         write path, end to end makes <see cref="IResourceManager" /> "a service held by the
    ///         gateway", and docs/plan/10 § Request pipeline dispatches to it at stage 8 — but
    ///         <see cref="AddCyberCloudResourceManager(ISiloBuilder)" /> takes a silo builder, which a
    ///         client host does not have.
    ///     </para>
    ///     <para>
    ///         The gateway could have repeated these ten lines in its own composition root. It must
    ///         not: one of them names <see cref="ReBacResourceAuthorizer" />, and docs/plan/10 § What
    ///         the gateway must never do puts authorization in one seam, in this assembly.
    ///         <c>GatewayIsolationTests.NoGatewaySourceFileCallsAnAuthorizationEngine</c> reads the
    ///         gateway's source for that name and fails on it — which is how this method came to
    ///         exist rather than by foresight.
    ///     </para>
    ///     <para>
    ///         ⚠ Everything is <c>TryAdd</c>, and the silo-only pieces (<see cref="DriftScanner" />,
    ///         <see cref="ReconcileDriver" />) are registered but never resolved in a client. A
    ///         registration is not an instantiation; splitting the list would mean two lists to keep
    ///         in step.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddCyberCloudResourceManager(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();

        // Built from whatever providers the container holds at first resolve — see the remarks on
        // ordering.
        services.TryAddSingleton<IProviderRegistry>(
            provider => ProviderRegistry.Build(provider.GetServices<IResourceProvider>())
        );

        services.TryAddSingleton<IPolicyEvaluator, NotSupportedPolicyEvaluator>();
        services.TryAddSingleton<IResourceChangedSink, LoggingResourceChangedSink>();
        services.TryAddSingleton<ILockResolver, ResourceScopeLockResolver>();
        services.TryAddSingleton<ISecretResolver, UnavailableSecretResolver>();
        services.TryAddSingleton<IClusterConnectionFactory, NoClusterConnectionFactory>();
        services.TryAddSingleton<IClusterObjectInventory, UnavailableClusterObjectInventory>();
        services.TryAddSingleton<IResourceAuthorizer, ReBacResourceAuthorizer>();

        // Writes the resource -> resourceGroup `parent` edge at step 8 of the write path. It belongs
        // in this list rather than the gateway's for the same reason IResourceAuthorizer does: it
        // names an authorization engine, and docs/plan/10 § What the gateway must never do keeps that
        // in one seam, in this assembly.
        services.TryAddSingleton<IResourceRelationWriter, ReBacResourceRelationWriter>();

        services.TryAddSingleton<DriftScanner>();
        services.TryAddSingleton<ReconcileDriver>();
        services.TryAddSingleton<IResourceManager, ResourceManagerService>();

        // ── The SignalR connection grain's dependencies. docs/plan/10 § SignalR ──────────────────
        //
        // ⚠ IN THIS LIST BECAUSE THE GRAIN IS IN THIS ASSEMBLY, AND IT IS IN THIS ASSEMBLY BECAUSE
        // THE GATEWAY IS A CLIENT. docs/plan/10 hands the connection grain to the gateway and says
        // nothing about where it runs; docs/plan/03 § Hosts makes the gateway an Orleans client, and a
        // client activates no grains. IConnectionGrain shipped in the gateway host, where no silo
        // could load it and only direct instantiation could exercise it — see IConnectionGrain's
        // remarks.
        //
        // ⚠ REGISTERED ON BOTH SIDES, AND ONLY ONE SIDE RESOLVES THEM. On a silo these are the
        // grain's constructor arguments. In the gateway's client container they are registrations
        // nothing asks for — the same arrangement DriftScanner and ReconcileDriver already have, for
        // the same reason: one list that cannot drift beats two that can.
        services.TryAddSingleton<ConnectionLimits>();
        services.TryAddSingleton<IInterestAuthorizer, ResourceManagerInterestAuthorizer>();

        return services;
    }

    /// <summary>
    ///     Registers one provider and its reconcilers.
    /// </summary>
    /// <typeparam name="TProvider">The provider.</typeparam>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     ⚠ <b>Reconcilers are registered as singletons and by their concrete type.</b> Singleton
    ///     because clause 2 makes one instance per process correct and a transient registration would
    ///     hide a field long enough for it to reach production. By concrete type because the registry
    ///     stores <c>ReconcilerType</c> and <c>ReconcileDriver</c> resolves it — a registration only
    ///     against <see cref="IResourceReconciler" /> would give the driver a list to search rather
    ///     than a lookup, and two reconcilers for one type would be found by whichever came first.
    /// </remarks>
    public static ISiloBuilder AddCyberCloudProvider<TProvider>(this ISiloBuilder silo)
        where TProvider : class, IResourceProvider, new() {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => {
                var provider = new TProvider();
                services.AddSingleton<IResourceProvider>(provider);

                var builder = new DiscoveringProviderBuilder();
                provider.Describe(builder);

                foreach (var reconciler in builder.Reconcilers) {
                    services.TryAddSingleton(reconciler);
                }
            }
        );
    }
}
