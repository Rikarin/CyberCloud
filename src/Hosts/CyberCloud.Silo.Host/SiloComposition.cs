using CyberCloud.Authorization;
using CyberCloud.Communication;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CyberCloud.Silo.Host;

/// <summary>
///     Builds the silo, module graph and all. docs/plan/04 § Silo composition.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists so a test can compose the real host rather than a lookalike, and the reason
///         is the defect it was written to close.</b> Every conformance and resource-manager suite in
///         the repository builds its own <c>TestCluster</c>, registers the resource manager and
///         registers a provider — so all of them stayed green through the whole period in which
///         <c>CyberCloud.Silo.Host</c> referenced no provider module, composed no resource manager, and
///         could not have reconciled anything. A green conformance run said nothing about this process.
///         <c>CyberCloud.Silo.Host.Tests</c> calls <see cref="BuildAsync" /> and asserts against what it
///         produced, so the thing under test is the thing that ships.
///     </para>
///     <para>
///         <c>Program.cs</c> keeps what is not composition: the one-shot <c>--apply-durable-schema</c>
///         mode, ABP's initialization and <c>RunAsync</c>.
///     </para>
/// </remarks>
public static class SiloComposition {
    /// <summary>
    ///     Composes the silo and returns the built host, ready to initialize and run.
    /// </summary>
    /// <param name="args">The process arguments, passed through to configuration.</param>
    /// <returns>The built host. Nothing has started.</returns>
    public static async Task<WebApplication> BuildAsync(string[] args) {
        ArgumentNullException.ThrowIfNull(args);

        var builder = OrleansApplication.CreateSilo(
            args,
            configureCluster: ConfigureCluster,
            configureStorage: ConfigureStorage
        );

        // ── ⚠ REQUIRED THE MOMENT A PROVIDER MODULE JOINS THE GRAPH, AND ONLY THEN ────────────────
        //
        // A silo serves nothing but health endpoints and authorizes nobody, so this line reads like
        // it does not belong. It is what keeps the process alive. Each provider's *.Application
        // module depends on AbpDddApplicationModule, whose graph brings in Volo.Abp.Authorization;
        // that registers enough of ASP.NET Core's authorization surface for WebApplication to decide
        // the app wants authorization and to insert UseAuthorization into the pipeline for itself.
        // UseAuthorization then calls VerifyServicesRegistered, which looks for the marker only
        // AddAuthorization() adds, does not find it, and throws
        //
        //     InvalidOperationException: Unable to find the required services. Please add all the
        //     required services by calling 'IServiceCollection.AddAuthorization' …
        //
        // out of app.RunAsync() — at START-UP, on a host that composes and builds perfectly.
        //
        // ⚠ THIS IS WHY Build() IS NOT ENOUGH TO TEST A HOST, AND IT COST A WHOLE DEBUGGING SESSION
        // TO LEARN. CyberCloud.Hosts.Tests composes both hosts and asserts against the container they
        // produced; every one of those tests stayed green while this host could not start at all,
        // because the failure is in ConfigureApplication, which runs on StartAsync and not on Build.
        // CyberCloud.AppHost.Tests was the only suite in the repository that starts the real silo,
        // and it was the only one that failed. HostCompositionTests now starts both hosts too.
        //
        // CyberCloud.Identity.Host reached the same line by the same route — see
        // IdentityHostAuthentication, where it sits under a real authentication scheme rather than
        // under an empty one.
        builder.Services.AddAuthorization();

        // ⚠ Required, and not optional. CreateSilo calls builder.Host.UseAutofac(), and ABP's
        // service-provider factory resolves IModuleContainer during Build(). Without a module the host
        // dies with "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer" — a
        // message naming neither UseAutofac nor the missing call.
        await builder.Services.AddApplicationAsync<SiloHostModule>();

        return builder.Build();
    }

    /// <summary>
    ///     The cluster wiring: the sending domain, identity and the resource manager.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>AddCyberCloudCommunication</c> adds seven grain types and no new start-up
    ///         requirement</b> — docs/plan/17. It registers services only: the clock, the five refusing
    ///         channel providers, the provider registry, the client-side sender and the webhook router.
    ///         It configures no reminder service, no stream provider and no storage, because its grains
    ///         bind <c>StorageTiers.Hot</c> and <c>StorageTiers.Durable</c>, which are the two
    ///         <c>AddCyberCloudTenancy</c> already wires. With no carrier configured every send fails
    ///         with a sentence saying so, which is the designed state of a silo with no Twilio client
    ///         rather than a defect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddSiloIdentity</c> goes beside it and not instead of it</b> — docs/plan/11. The
    ///         <c>ProjectReference</c> in the .csproj is what puts the identity grains in the silo; this
    ///         call is what registers the services their constructors ask for, including the
    ///         <c>IOtpDeliverySeam</c> that <c>UserGrain.IssueOtpAsync</c> reaches. The seam adapter
    ///         resolves <c>IMessageSender</c>, which the call above is what provides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddCyberCloudResourceManager</c> is the call that had no caller anywhere</b> —
    ///         docs/plan/08, docs/plan/04 § Silo composition. The providers themselves arrive through
    ///         <see cref="SiloHostModule" />'s <c>[DependsOn]</c> list, whose <c>ConfigureServices</c>
    ///         runs later than anything here, and the ordering does not matter:
    ///         <c>IProviderRegistry</c> is a factory registration, built at first resolve from whatever
    ///         providers the container ended up holding. A host that composed the manager and
    ///         registered no provider used to get an empty registry and answer <c>404</c> to every
    ///         resource path; <c>ProviderRegistry.Build</c> now refuses to build one.
    ///     </para>
    ///     <para>
    ///         Reminders are wired in <see cref="ConfigureStorage" />, which is where this host is
    ///         handed the storage options they are configured from.
    ///     </para>
    /// </remarks>
    static void ConfigureCluster(ISiloBuilder silo) {
        silo.AddCyberCloudCommunication()
            .AddSiloIdentity()
            // ── docs/plan/07's ReBAC engine — step 3 of every write ────────────────────────────────
            //
            // ⚠ WITHOUT THIS THE ENFORCEMENT SEAM ANSWERED 404 TO EVERYBODY, INCLUDING ITSELF.
            // AddCyberCloudResourceManager below registers ReBacResourceAuthorizer, which asks
            // ICheckGrain whether the caller may write. The .csproj reference is what puts CheckGrain,
            // TupleStoreGrain and the two relation grains in the silo; THIS call is what gives
            // CheckGrain the schema it evaluates against, because a schema is a decision the host
            // makes rather than something Orleans can discover. A silo with the grains and no schema
            // fails to activate the check grain, and a silo with neither fails the check itself — and
            // both arrive at the caller as the canonical 404 that docs/plan/07 § The enforcement seam
            // requires, which is why this was invisible.
            //
            // ⚠ The default schema is CyberCloudSchema.Instance — docs/plan/07 § Azure RBAC, expressed
            // in it. A test silo may pass its own; a production one has exactly one.
            .AddCyberCloudAuthorization()
            // ── The kubeconfig resolver, BEFORE AddCyberCloudKubernetes ────────────────────────────
            //
            // ⚠ ORDER, AND FOR THE SAME REASON THE TWO CLUSTER SEAMS ARE ORDERED BELOW.
            // AddCyberCloudKubernetes registers IKubeApiClientFactory with TryAddSingleton, so the
            // FIRST registration wins and a resolver added afterwards would never be resolved.
            .ConfigureServices(ConfigureKubeconfigResolver(silo.Configuration))
            // ⚠ BEFORE AddCyberCloudResourceManager, and here the order does matter. The manager's
            // registrations are TryAdd, so the two cluster seams below have to be in the container
            // first or the refusing defaults win — NoClusterConnectionFactory, which answers null to
            // every Connect, and UnavailableClusterConnectionRegistrar, which refuses every attach.
            .AddCyberCloudKubernetes()
            .ConfigureServices(services => {
                    // ── The cluster fabric, docs/plan/09 ────────────────────────────────────────
                    //
                    // ⚠ THE ONLY IMPLEMENTATIONS OF THESE TWO OUTSIDE A TEST, AND UNTIL THEY EXISTED
                    // NO PRODUCTION HOST COULD REACH A CLUSTER AT ALL. Every host registered
                    // NoClusterConnectionFactory, so a resource type declaring RequiresCluster was
                    // refused by ReconcileDriver by name — the right refusal, and not a connection.
                    //
                    // AddCyberCloudKubernetes above is the other half: it puts ClusterConnectionGrain
                    // in the silo and registers ClusterConnectionTenantFilter, which is what
                    // establishes the caller tenant the null-tenant grain checks against.
                    services.AddSingleton<IClusterConnectionFactory, GrainClusterConnectionFactory>();
                    services.AddSingleton<IClusterConnectionRegistrar, GrainClusterConnectionRegistrar>();
                }
            )
            .AddCyberCloudResourceManager();
    }

    /// <summary>
    ///     Registers a kubeconfig resolver when this silo has been given a directory to read from.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <returns>The registration, which does nothing when no root is configured.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doing nothing is the correct behaviour for a silo with no root, and it is not the
    ///         same as doing nothing at all.</b> <c>KubeApiClientFactory</c> with no
    ///         <c>ResolveKubeconfig</c> refuses every connect with a sentence naming this seam, which
    ///         is what a production silo should say until <c>CyberCloud.KeyVault</c> can answer for it.
    ///         What was wrong before was that <i>every</i> silo was in that state and nothing could
    ///         leave it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The whole factory is re-registered rather than the delegate configured</b>, because
    ///         <c>ResolveKubeconfig</c> is an <c>init</c>-only property: it is settable at construction
    ///         and nowhere else, which is the shape that keeps a running silo's credential path from
    ///         being moved underneath it.
    ///     </para>
    /// </remarks>
    static Action<IServiceCollection> ConfigureKubeconfigResolver(IConfiguration configuration) {
        var root = configuration[LocalKubeconfigFiles.RootKey];

        return services => {
            if (string.IsNullOrWhiteSpace(root)) {
                return;
            }

            services.TryAddSingleton<IClock, SystemClock>();

            services.TryAddSingleton<IKubeApiClientFactory>(provider =>
                new KubeApiClientFactory(
                    provider.GetRequiredService<IClock>(),
                    provider.GetService<ILogger<KubeApiClientFactory>>()
                ) {
                    ResolveKubeconfig = LocalKubeconfigFiles.ResolverFor(root)
                }
            );
        };
    }

    /// <summary>
    ///     The two storage tiers, and the reminder table that shares the hot one's Redis.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="storage">The bound <c>CyberCloud:Storage</c> section.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reconcile loop is a reminder, so without this the resource manager composes and
    ///         then throws on the first create.</b> <c>OperationGrain</c> is <c>IRemindable</c> and
    ///         calls <c>RegisterOrUpdateReminder</c>, which throws on a silo with no reminder service —
    ///         late, inside a grain call, rather than at start-up. <c>UsageSamplerGrain</c> and
    ///         <c>UsageRollupGrain</c> are the same. Every test fixture in the tree wires
    ///         <c>UseInMemoryReminderService</c> for exactly this reason, and no production host wired
    ///         anything at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Redis, from the hot tier's own connection string</b> — docs/plan/04 § Reminders,
    ///         "sharded with the hot tier", read literally. A reminder table on a different Redis would
    ///         be a second thing to provision, a second thing to lose, and a second answer to "did this
    ///         resource's reconcile tick survive the restart".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>In the HOST and not in <c>OrleansApplication.CreateSilo</c>, which is where it was
    ///         first written and where it was wrong.</b> <c>CreateSilo</c> wires the tiers whenever
    ///         <c>CyberCloud:Storage</c> is configured, and "configured" is not "reachable":
    ///         <c>CyberCloud.ServiceDefaults.Tests</c>' unreachable-shard fixture sets a hot-tier
    ///         connection string and points it at nothing on purpose, so a reminder service on the same
    ///         condition failed that fixture at start-up with a Redis connection error. A silo that
    ///         means to serve tenants wants exactly this failure; a fixture asserting readiness
    ///         behaviour does not, and only a host knows which it is.
    ///     </para>
    /// </remarks>
    static void ConfigureStorage(ISiloBuilder silo, CyberCloudStorageOptions storage) {
        silo.AddCyberCloudTenancy(storage);

        if (string.IsNullOrWhiteSpace(storage.Hot.ConnectionString)) {
            return;
        }

        silo.UseRedisReminderService(reminders =>
            reminders.ConfigurationOptions = ConfigurationOptions.Parse(storage.Hot.ConnectionString)
        );
    }
}
