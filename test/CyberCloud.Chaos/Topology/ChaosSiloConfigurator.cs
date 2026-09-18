using CyberCloud.Authorization;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Providers.Sample;
using CyberCloud.ResourceManager;
using CyberCloud.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.TestingHost;
using StackExchange.Redis;

namespace CyberCloud.Chaos.Topology;

/// <summary>
///     A silo wired as <c>CyberCloud.Silo.Host</c> wires one — both storage tiers real, reminders in
///     Redis, the real authorization engine, the real cluster-connection grain over k3s, the Sample
///     provider — with the two knobs a chaos run has to turn set where a deployment would set them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two deployment knobs are turned — the cluster health window here, the pool size in
///         <see cref="ChaosTopology" /> — and they are the only deviations from the shipped
///         defaults. Both are named in the results file's topology block
///         (<c>ChaosTopology.Facts</c>: <c>clusterHealthWindow</c>, <c>clusterPingInterval</c>,
///         <c>npgsqlPoolSize</c>) so a reader of the dated table in docs/plan/23 knows what the
///         numbers were measured under. ⚠ The pool size was not in the block until the review of
///         the branch read this sentence against the file.</b>
///     </para>
///     <para>
///         ⚠ <b>The membership probes are the shipped defaults, and the first version of this file
///         tuned them and paid for it.</b> <see cref="ClusterMembershipOptions" /> was set to a 2 s
///         probe, two misses and one vote so a killed silo would be declared dead in seconds rather
///         than a minute. Two things were wrong with that. The testing host's kill announces its own
///         death — the dying silo writes its Dead row — so the probes were never what noticed a kill
///         and the tuning bought nothing. And on a laptop thirteen agents share, a silo that was
///         still starting missed two probes, one vote declared it dead, it read its own Dead row,
///         and Orleans' fatal-error handler called <c>Environment.FailFast</c> on the process every
///         silo and every test were in. The shipped defaults are what a production cluster runs
///         under, and a false death declaration is the failure they are shaped to avoid.
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Cluster health window</b> — <see cref="KubernetesOptions.HealthStalenessWindow" /> is
///             20 s and the ping 5 s, against the shipped 90 s and 30 s. Same argument: invariant 4
///             asks what a Degraded cluster does to reconciles, and docs/plan/09 § Connection health
///             gives 90 s as the number a deployment configures, not a property of the code.
///         </item>
///         <item>
///             <b>Pool size</b> — the durable tier's Npgsql pool is 20 per shard rather than 5, because
///             a storm of two dozen concurrent creates against one shard at 5 is a test of the pool.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The response timeouts are the shipped defaults</b>, on purpose, even though a call to a
///         grain on a silo that has just been killed waits the full 30 s before it fails. That wait is
///         what a gateway experiences in production, and the storm driver is written to survive it
///         rather than to hide it.
///     </para>
/// </remarks>
public sealed class ChaosSiloConfigurator : ISiloConfigurator {
    /// <summary>The health-check period a chaos silo pings its clusters at.</summary>
    public static TimeSpan PingInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a cluster may go unanswered before it is Degraded, here.</summary>
    public static TimeSpan HealthStalenessWindow { get; } = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public void Configure(ISiloBuilder siloBuilder) {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        var silo = siloBuilder;

        // ⚠ A provider, not only a level. Every silo appends to ChaosSiloLog.Path — CyberCloud.*
        // at Information, Orleans and Microsoft at Warning — because a run whose silos log nowhere
        // is a run that cannot say why an operation stayed Running after its shard came back, which
        // is the question the review of the first version of this suite could not get answered.
        //
        // ⚠ The silo's name is stamped on the provider by a startup task, NOT resolved from the
        // container inside the provider's factory. The first draft resolved IOptions<EndpointOptions>
        // there, and anything resolved from inside a logger provider that itself wants a logger comes
        // back through the provider list — an unbounded recursion the DI container runs on fresh
        // stacks forever, and the silos never start. By the time a startup task runs, the logger
        // factory exists and ILocalSiloDetails is safe to ask.
        var log = new ChaosSiloLog();

        silo.ConfigureLogging(logging => {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("Orleans", LogLevel.Warning);
                logging.AddFilter("Microsoft", LogLevel.Warning);
                logging.AddProvider(log);
            }
        );

        silo.AddStartupTask((provider, _) => {
                log.Silo = provider.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToString();
                return Task.CompletedTask;
            }
        );

        // ── Both storage tiers, the shard map, the tenant directory and the separation filter —
        //    docs/plan/05 § The two tiers, through the one call CyberCloud.Silo.Host makes. ─────────
        silo.AddCyberCloudTenancy(ChaosState.Storage);

        // ── Reminders, in the same Redis as the hot tier — SiloComposition.ConfigureStorage. ─────
        silo.UseRedisReminderService(reminders =>
            reminders.ConfigurationOptions = ConfigurationOptions.Parse(ChaosState.RedisConnectionString)
        );

        silo.ConfigureServices(services => {
                services.AddSingleton(ChaosState.Clock);

                // ⚠ Registered BEFORE AddCyberCloudKubernetes so its TryAdd keeps this one, which is
                // the same contract SiloComposition.ConfigureKubeconfigResolver relies on. The
                // resolver answers every credential reference with the k3s kubeconfig: there is no
                // vault here, and the invariants are about what happens to a reachable cluster that
                // stops answering, not about where its credential came from.
                services.AddSingleton<IKubeApiClientFactory>(provider =>
                    new KubeApiClientFactory(
                        provider.GetRequiredService<IClock>(),
                        provider.GetService<ILogger<KubeApiClientFactory>>(),
                        provider.GetRequiredService<IGrainFactory>()
                    ) {
                        ResolveKubeconfig = (_, _) => Task.FromResult(Result<string>.Success(ChaosState.Kubeconfig))
                    }
                );

                services.Configure<KubernetesOptions>(options => {
                        options.PingInterval = PingInterval;
                        options.HealthStalenessWindow = HealthStalenessWindow;
                    }
                );
            }
        );

        silo.AddCyberCloudAuthorization()
            .AddCyberCloudKubernetes()
            .ConfigureServices(services => {
                    services.AddSingleton<IClusterConnectionFactory, GrainClusterConnectionFactory>();
                    services.AddSingleton<IClusterConnectionRegistrar, GrainClusterConnectionRegistrar>();
                    services.AddCyberCloudProvider(new SampleProvider());
                }
            )
            .AddCyberCloudResourceManager();
    }
}
