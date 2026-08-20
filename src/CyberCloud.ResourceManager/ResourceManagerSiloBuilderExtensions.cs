using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Actions;
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
    ///         ⚠ <b>The order of this call and the provider registrations does not matter, and it is
    ///         worth knowing why, because the remark here used to say the opposite.</b> The registry is
    ///         a singleton with a <i>factory</i> rather than an instance, so it is built from every
    ///         <see cref="IResourceProvider" /> in the container at first resolve — after all wiring
    ///         has finished. That is what lets a provider be registered from an ABP module's
    ///         <c>ConfigureServices</c>, which runs later than anything on <see cref="ISiloBuilder" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What does matter is that the host registers providers at all.</b> A host that calls
    ///         this and no <c>AddCyberCloudProvider</c> would get a registry with no types, and
    ///         resolving <see cref="IProviderRegistry" /> throws rather than serving one: an empty
    ///         registry answers the canonical <c>404</c> to every resource and action path, which is
    ///         indistinguishable from a caller asking for a type that does not exist.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every seam gets a default and every default is honest about what it is.</b>
    ///         <see cref="NotSupportedPolicyEvaluator" /> says no policy engine ran rather than
    ///         allowing; <see cref="UnavailableSecretResolver" /> refuses rather than returning empty;
    ///         <see cref="UnavailableClusterObjectInventory" /> fails rather than reporting an empty
    ///         cluster; <see cref="UnavailableNamespaceInventory" /> fails rather than reporting an
    ///         empty namespace, which is the answer that would authorize deleting it. Each of those is
    ///         a place where the plausible default is the dangerous one, and
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
            provider => BuildRegistry(provider.GetServices<IResourceProvider>())
        );

        services.TryAddSingleton<IPolicyEvaluator, NotSupportedPolicyEvaluator>();
        services.TryAddSingleton<IResourceChangedSink, LoggingResourceChangedSink>();
        services.TryAddSingleton<ILockResolver, ResourceScopeLockResolver>();
        services.TryAddSingleton<ISecretResolver, UnavailableSecretResolver>();
        services.TryAddSingleton<ISecretWriter, UnavailableSecretWriter>();
        services.TryAddSingleton<IClusterConnectionFactory, NoClusterConnectionFactory>();
        services.TryAddSingleton<IClusterConnectionRegistrar, UnavailableClusterConnectionRegistrar>();
        services.TryAddSingleton<IClusterObjectInventory, UnavailableClusterObjectInventory>();
        services.TryAddSingleton<INamespaceInventory, UnavailableNamespaceInventory>();
        services.TryAddSingleton<IResourceAuthorizer, ReBacResourceAuthorizer>();

        // Writes the resource -> resourceGroup `parent` edge at step 8 of the write path. It belongs
        // in this list rather than the gateway's for the same reason IResourceAuthorizer does: it
        // names an authorization engine, and docs/plan/10 § What the gateway must never do keeps that
        // in one seam, in this assembly.
        services.TryAddSingleton<IResourceRelationWriter, ReBacResourceRelationWriter>();

        services.TryAddSingleton<DriftScanner>();

        // ⚠ A SINGLETON BECAUSE ITS MEMO IS THE POINT. NamespaceEnsurer applies a namespace once per
        // (cluster, namespace) per NamespaceEnsurer.RecheckAfter; registered per-scope or transient it
        // would remember nothing and every reconcile pass of every resource would cost a read and a
        // patch against a namespace that has existed for months.
        services.TryAddSingleton<NamespaceEnsurer>();
        services.TryAddSingleton<ReconcileDriver>();

        // ⚠ Resolved in the GATEWAY as well as in a silo, and unlike DriftScanner and ReconcileDriver
        // it is actually used there. IResourceManager is "a service held by the gateway"
        // (docs/plan/08 § The write path, end to end), a synchronous action runs inside ActionAsync,
        // so a `listKeys` executes in the gateway's process and reads the gateway's ISecretResolver.
        services.TryAddSingleton<ActionDispatcher>();
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

        return silo.ConfigureServices(services => services.AddCyberCloudProvider(new TProvider()));
    }

    /// <summary>
    ///     Registers one provider and its reconcilers into a container.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="provider">The provider instance. One per process — see the remarks.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the overload a provider's <c>*.Application</c> ABP module calls, and its
    ///         existence is what closed the "one provider, one registration" split.</b> Every
    ///         <c>*ApplicationModule</c> in the tree used to carry the same paragraph: registering a
    ///         provider is a call on <see cref="ISiloBuilder" />, which runs before the container is
    ///         built, while an ABP module's <c>ConfigureServices</c> runs after — so a host had to load
    ///         the module <i>and</i> make a second, separate call, and could do the first without the
    ///         second. It could and did: <c>AddCyberCloudProvider&lt;TProvider&gt;</c> had no caller
    ///         anywhere in the repository, so no silo served a single resource type.
    ///     </para>
    ///     <para>
    ///         The paragraph named the way out and it had already been built: the registry is
    ///         registered as a factory over <c>GetServices&lt;IResourceProvider&gt;()</c>, so it is
    ///         constructed at first resolve rather than at wiring time, and <i>when</i> a provider is
    ///         added stopped mattering. Only the container does, which is what an ABP module has.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reconcilers and handlers are registered as singletons and by their concrete
    ///         type.</b> Singleton because clause 2 of docs/plan/08 § The reconcile loop makes one
    ///         instance per process correct and a transient registration would hide a field long enough
    ///         for it to reach production. By concrete type because the registry stores
    ///         <c>ReconcilerType</c> and <c>ActionRegistration.HandlerType</c>, and
    ///         <c>ReconcileDriver</c> and <c>ActionDispatcher</c> resolve those — a registration only
    ///         against <see cref="IResourceReconciler" /> would give each a list to search rather than
    ///         a lookup, and two reconcilers for one type would be found by whichever came first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddSingleton</c> for the provider and <c>TryAdd</c> for what it declares.</b>
    ///         The provider is additive because the registry is the union of every provider in the
    ///         process and a <c>TryAdd</c> would silently drop the second one; the reconcilers are not,
    ///         because a host that registered a substitute first is entitled to keep it.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddCyberCloudProvider(
        this IServiceCollection services,
        IResourceProvider provider
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);

        services.AddSingleton(provider);

        var builder = new DiscoveringProviderBuilder();
        provider.Describe(builder);

        foreach (var reconciler in builder.Reconcilers) {
            services.TryAddSingleton(reconciler);
        }

        foreach (var handler in builder.Handlers) {
            services.TryAddSingleton(handler);
        }

        return services;
    }

    /// <summary>
    ///     Builds the registry a <b>host</b> serves from, refusing an empty provider set.
    /// </summary>
    /// <param name="providers">Every provider the container holds.</param>
    /// <returns>The built registry.</returns>
    /// <exception cref="InvalidOperationException">There is no provider at all.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The check is here rather than in <c>ProviderRegistry.Build</c>, and the difference
    ///         between the two callers is what decides it.</b> A host with no providers is a wiring
    ///         mistake: the registry is the platform's whole API surface, so every resource and action
    ///         path answers the canonical <c>404</c> — the same answer a caller gets for a type that
    ///         genuinely does not exist, with nothing in the log to tell them apart. That is what
    ///         <c>CyberCloud.Gateway.Host</c> shipped as, and no test could see it because every
    ///         harness registers a provider of its own.
    ///     </para>
    ///     <para>
    ///         The other caller is <c>Build.Generate</c>'s generator, for which zero providers is an
    ///         ordinary state it reports on rather than an error — its vacuity tripwire needs a run
    ///         over no assemblies to succeed so it can be told apart from a run over an assembly that
    ///         declared nothing. Refusing inside <c>Build</c> broke that, which is how the two callers
    ///         turned out to want different answers.
    ///     </para>
    /// </remarks>
    static ProviderRegistry BuildRegistry(IEnumerable<IResourceProvider> providers) {
        var all = providers as IReadOnlyCollection<IResourceProvider> ?? [.. providers];

        if (all.Count == 0) {
            throw new InvalidOperationException(
                "No IResourceProvider is registered, so this host's provider registry would describe "
                + "a platform that serves no resource type at all. Every resource and action path "
                + "would answer 404 and nothing would say why. A host that calls "
                + "AddCyberCloudResourceManager registers its providers too — "
                + "docs/plan/04 § Silo composition, \"every silo loads every provider module\"."
            );
        }

        return ProviderRegistry.Build(all);
    }
}
