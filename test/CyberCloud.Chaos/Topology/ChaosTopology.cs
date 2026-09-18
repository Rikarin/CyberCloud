using CyberCloud.Authorization.Contracts;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Sample;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Grains;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Shards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Orleans.Multitenant;
using Orleans.TestingHost;
using StackExchange.Redis;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Testcontainers.K3s;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using k8s;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.Chaos.Topology;

/// <summary>
///     One tenant with everything a widget needs: a subscription, a resource group, lifted quotas, an
///     owner grant, and its own connection to the k3s.
/// </summary>
/// <param name="Tenant">The tenant id. Its durable shard is <see cref="ChaosTopology.ShardOf" />.</param>
/// <param name="Subscription">The one subscription.</param>
/// <param name="Group">The one resource group's name.</param>
/// <param name="Cluster">The cluster-connection id this tenant's widgets are applied through.</param>
/// <param name="Caller">The tenant owner, as the write path sees them.</param>
public sealed record TenantWorld(Guid Tenant, Guid Subscription, string Group, Guid Cluster, CallerContext Caller) {
    /// <summary>The address a widget of this name has in this tenant.</summary>
    /// <param name="name">The widget's name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    public ResourceId Widget(string name) => new(Tenant, Subscription, Group, SampleWidgets.Type, name, Guid.Empty);

    /// <summary>The widget collection — what a GET of the type lists.</summary>
    public ResourceCollectionId Widgets => ResourceCollectionId.Of(Widget("any"));

    /// <summary>The k3s namespace the tenant's widgets land in — ReconcileDriver derives it.</summary>
    public string Namespace => ReconcileDriver.NamespaceFor(Widget("any"));
}

/// <summary>
///     The real infrastructure the seven invariants break: a Redis hot tier, three PostgreSQL shards,
///     a k3s, and a three-silo Orleans cluster wired as the platform wires one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Started once per process and shared by every invariant's class</b>, because each
///         start is close to a minute of containers and silos, and because the point is a cluster
///         with state in it when the fault lands. The price is the rule in <c>AssemblyInfo.cs</c>:
///         one test at a time, and each puts back what it broke.
///     </para>
///     <para>
///         ⚠ <b>Three shards, not one, and two of them hold tenants.</b> Invariant 3 is "other
///         tenants unaffected", which is vacuous with one shard; invariant 5 is the global directory
///         going away while tenant shards stay, which is <c>platform-00</c> — the shard every
///         null-tenant grain lives on (docs/plan/05 § Durable) — stopped on its own. The same layout
///         <c>CyberCloud.Tenancy.Tests</c>' <c>TenancyCluster</c> uses, for the same reasons.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The write path is the real <c>ResourceManagerService</c> over the real
///             <c>ReBacResourceAuthorizer</c>, composed on the client exactly as the gateway composes it
///         </b>
///         — <c>AddCyberCloudResourceManager()</c> on a service collection holding the cluster
///         client. Tenants are created through <c>IScopeManager.CreateTenantAsync</c>, the platform
///         administration path, under an operator grant written the way
///         <c>CyberCloud.Silo.Host</c>'s <c>PlatformBootstrapTask</c> writes it. Nothing here goes
///         around a check the platform would make.
///     </para>
/// </remarks>
public sealed class ChaosTopology : IAsyncLifetime {
    /// <summary>The first tenant shard.</summary>
    public const string ShardA = "durable-00";

    /// <summary>The second tenant shard.</summary>
    public const string ShardB = "durable-01";

    /// <summary>The shard every null-tenant grain lives on: the shard map, the tenant directory, cluster connections.</summary>
    public const string PlatformShard = "platform-00";

    /// <summary>
    ///     How many silos the cluster starts with. Three, so killing one leaves a cluster and a second kill is still a
    ///     kill.
    /// </summary>
    public const int InitialSilos = 3;

    /// <summary>The operator every tenant here is created by — a service principal holding <c>platform:root#operator</c>.</summary>
    public const string OperatorId = "chaos-operator";

    /// <summary>
    ///     The durable tier's Npgsql pool per shard — a deployment knob turned, and named in the results file's topology
    ///     block.
    /// </summary>
    public const int PoolSize = 20;

    const string Region = "eu-central";

    readonly RedisContainer redis = new RedisBuilder(ClusterInfrastructure.RedisImage)
        // docs/plan/05 § Hot: noeviction, and an AOF so a Redis restart is not a FLUSHALL of its own.
            .WithCommand("--maxmemory-policy", "noeviction", "--appendonly", "yes")
            .Build();

    readonly Dictionary<string, PostgreSqlContainer> shards = new(StringComparer.Ordinal) {
        [ShardA] = NewShard(), [ShardB] = NewShard(), [PlatformShard] = NewShard()
    };

    // ⚠ On a FIXED host port, unlike every other k3s in this repository, and the shards below the
    // same — because this suite stops containers and starts them again. Docker re-allocates a
    // random published port on `docker start`, so a container Testcontainers mapped to a random
    // port comes back on a different one and every connection string and kubeconfig the silos hold
    // is stale; the fourth run found the platform shard "actively refused" for two minutes after
    // its restart for exactly that reason. A port chosen here and bound explicitly survives the
    // restart. The choice is a free port at construction, which a second process could take in the
    // window before the container binds it; the topology fails loudly at start if so.
    readonly K3sContainer k3s = ClusterInfrastructure.K3s().WithPortBinding(FreePort(), 6443).Build();

    ServiceProvider client = null!;

    /// <summary>The Orleans cluster. Kill, stop and start silos through it.</summary>
    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>The write path, as the gateway holds it.</summary>
    public IResourceManager Manager { get; private set; } = null!;

    /// <summary>The scope path — subscriptions, resource groups, and tenant creation.</summary>
    public IScopeManager Scopes { get; private set; } = null!;

    /// <summary>The raw Kubernetes client over the k3s, for the half of every assertion that is not our code.</summary>
    public IKubernetes Raw { get; private set; } = null!;

    /// <summary>The k3s API server's URL as the silos reach it.</summary>
    public string K3sEndpoint { get; private set; } = string.Empty;

    /// <summary>The seven outcomes, written when the topology is disposed.</summary>
    public ChaosReport Report { get; } = new();

    /// <summary>The tenant-unaware grain factory — what the gateway has.</summary>
    public IGrainFactory Grains => Cluster.Client;

    /// <summary>A grain factory for one tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(D(tenant));

    /// <summary>The management grain, for activation statistics and forced collection.</summary>
    public IManagementGrain Management => Grains.GetGrain<IManagementGrain>(0);

    /// <summary>The shard a tenant's durable state is on, by the same placement the silos use.</summary>
    /// <param name="tenant">The tenant.</param>
    public static string ShardOf(Guid tenant) =>
        new GrainBackedShardMapCache(ChaosState.Storage).DurableShardFor(D(tenant));

    /// <summary>A fresh tenant id whose durable state lands on <paramref name="shard" />.</summary>
    /// <param name="shard"><see cref="ShardA" /> or <see cref="ShardB" />.</param>
    public static Guid TenantOn(string shard) {
        for (var attempt = 0; attempt < 1000; attempt++) {
            var candidate = Guid.NewGuid();

            if (string.Equals(ShardOf(candidate), shard, StringComparison.Ordinal)) {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"1000 fresh tenant ids and none hashed to '{shard}' — the placement function is broken."
        );
    }

    /// <summary>Facts about what the invariants ran against, for the results file and the dated table.</summary>
    public IReadOnlyDictionary<string, string> Facts =>
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["silos"] = InitialSilos.ToString(CultureInfo.InvariantCulture),
            ["tenantShards"] = "2",
            ["platformShard"] = PlatformShard,
            ["hotTier"] = ClusterInfrastructure.RedisImage,
            ["durableTier"] = ClusterInfrastructure.PostgresImage,
            ["cluster"] = ClusterInfrastructure.K3sImage,
            ["clusterHealthWindow"] = ChaosSiloConfigurator.HealthStalenessWindow.TotalSeconds.ToString(
                "0",
                CultureInfo.InvariantCulture
            )
                + " s",
            ["clusterPingInterval"] = ChaosSiloConfigurator.PingInterval.TotalSeconds.ToString(
                "0",
                CultureInfo.InvariantCulture
            )
                + " s",
            ["membershipProbes"] = "shipped defaults",
            ["npgsqlPoolSize"] = PoolSize.ToString(CultureInfo.InvariantCulture),
            ["reminderPeriod"] = OperationGrain.ReminderPeriod.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                + " s"
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        // ⚠ Taken BEFORE the containers, released when the process exits — ClusterInfrastructure's remarks.
        ClusterSlot.Acquire();

        await Task.WhenAll(
            [redis.StartAsync(token), k3s.StartAsync(token), .. shards.Values.Select(x => x.StartAsync(token))]
        );

        foreach (var shard in shards.Values) {
            // ⚠ The SHIPPED applier — `--apply-durable-schema` runs this exact call.
            await OrleansAdoNetSchema.ApplyAsync(ConnectionString(shard), token);
        }

        var storage = new CyberCloudStorageOptions {
            Hot = { ConnectionString = redis.GetConnectionString() },
            Durable = { NullTenantShard = PlatformShard, BootstrapShard = ShardA, MaxPoolSize = PoolSize }
        };

        foreach (var (name, shard) in shards) {
            storage.Durable.Shards[name] = ConnectionString(shard);
        }

        ChaosState.Storage = storage;
        ChaosState.RedisConnectionString = redis.GetConnectionString();
        ChaosState.Kubeconfig = await k3s.GetKubeconfigAsync();

        using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(ChaosState.Kubeconfig));
        var config = await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml);
        K3sEndpoint = config.Host;
        Raw = new k8s.Kubernetes(config);

        var builder = new TestClusterBuilder(InitialSilos);

        // ⚠ CyberCloudClusterOptions' defaults, not the testing host's, because CyberCloud.Load
        // starts the real gateway against this cluster and OrleansApplication.CreateClient binds
        // the ids from CyberCloud:Cluster before UseLocalhostClustering — a Development gateway
        // expects "cybercloud", and the run that guessed the testing host's "dev" was refused with
        // Orleans' `Unexpected cluster id "dev", expected "cybercloud"` on every connection. The
        // Redis and the shards are this process's own, so the ids collide with nothing.
        builder.Options.ServiceId = new CyberCloudClusterOptions().ServiceId;
        builder.Options.ClusterId = new CyberCloudClusterOptions().ClusterId;

        // ⚠ Real sockets. The testing host's default transport is in-memory: the silos and the
        // Cluster.Client talk through a hub in this process and nothing listens on the silo or
        // gateway ports, so a gateway process pointed at BaseGatewayPort found the port refused
        // (the first load run did). TCP is also what the deployment uses, so every row here is
        // measured over the wire it ships on.
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        builder.AddSiloBuilderConfigurator<ChaosSiloConfigurator>();

        Cluster = builder.Build();
        await Cluster.DeployAsync();

        // ── What PlatformBootstrapTask does at a real silo's start: the shard map, and the operator. ─
        (await Grains.GetGrain<IShardMapGrain>(GrainKeys.ShardMap()).ConfigureShardsAsync([ShardA, ShardB]))
            .IsSuccess.ShouldBeTrue("the shard map could not be configured.");

        await RefreshShardMapsAsync(token);
        await GrantOperatorAsync();

        // ── The write path, composed as the gateway composes it. ──────────────────────────────────
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IGrainFactory>(Cluster.Client);
        services.AddSingleton(Cluster.Client);
        services.AddSingleton(ChaosState.Clock);
        services.AddCyberCloudProvider(new SampleProvider());
        services.AddCyberCloudResourceManager();

        client = services.BuildServiceProvider();
        Manager = client.GetRequiredService<IResourceManager>();
        Scopes = client.GetRequiredService<IScopeManager>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        try {
            Report.Write(Facts);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.WriteLine($"[CyberCloud.Chaos] the results file could not be written: {ex.Message}");
        }

        if (Cluster is not null) {
            try {
                await Cluster.StopAllSilosAsync();
            } catch (Exception) {
                // Already killed by a test. Disposing what is gone is not a result.
            }

            await Cluster.DisposeAsync();
        }

        client?.Dispose();
        Raw?.Dispose();

        await Task.WhenAll(
            [
                redis.DisposeAsync().AsTask(), k3s.DisposeAsync().AsTask(),
                .. shards.Values.Select(static x => x.DisposeAsync().AsTask())
            ]
        );
    }

    // ── Tenants and widgets ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Creates a tenant through the platform's own path and gives it everything a widget needs.
    /// </summary>
    /// <param name="tenant">The tenant id; <see cref="TenantOn" /> picks one for a shard.</param>
    /// <param name="label">A short DNS-1123 label to build the slug and group name from.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task<TenantWorld> CreateTenantAsync(Guid tenant, string label, CancellationToken cancellationToken) {
        var operatorCaller = new CallerContext {
            TenantId = ReBacScopeAuthorizer.PlatformTenant,
            SubjectType = SubjectTypes.ServicePrincipal,
            SubjectId = OperatorId,
            CorrelationId = "chaos-" + label
        };

        var created = await Scopes.CreateTenantAsync(
            new() {
                TenantId = tenant,
                Slug = label + "-" + N(tenant)[..8],
                DisplayName = "Chaos " + label,
                HomeRegion = Region,
                OwnerSubjectType = SubjectTypes.User,
                OwnerSubjectId = "owner-" + label
            },
            operatorCaller,
            cancellationToken
        );

        created.IsSuccess.ShouldBeTrue($"the tenant for '{label}' could not be created: {created.Error?.Message}");

        var caller = new CallerContext {
            TenantId = tenant,
            SubjectType = SubjectTypes.User,
            SubjectId = "owner-" + label,
            CorrelationId = "chaos-" + label
        };

        var subscription = Guid.NewGuid();
        const string group = "chaos";

        var sub = await Scopes.CreateAsync(
            new() {
                Path = ScopeId.Subscription(tenant, subscription).Path,
                Body = """{"displayName":"chaos"}""",
                Caller = caller
            },
            cancellationToken
        );

        sub.IsSuccess.ShouldBeTrue($"the subscription for '{label}' could not be created: {sub.Error?.Message}");

        var rg = await Scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(tenant, subscription, group).Path,
                Body = $$"""{"location":"{{Region}}"}""",
                Caller = caller
            },
            cancellationToken
        );

        rg.IsSuccess.ShouldBeTrue($"the resource group for '{label}' could not be created: {rg.Error?.Message}");

        // ⚠ The quota limits are lifted for the reason ClusterConformanceHarness.LiftQuotaAsync gives:
        // a storm creates dozens against one subscription and the default Resources limit is not
        // what any invariant here is about.
        var quota = For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

        foreach (var meter in Enum.GetValues<QuotaMeter>().Where(static x => x != QuotaMeter.Unknown)) {
            (await quota.SetLimitAsync(meter, 1_000_000m)).IsSuccess.ShouldBeTrue();
        }

        var cluster = Guid.NewGuid();
        await AttachClusterAsync(cluster, tenant, label);

        return new(tenant, subscription, group, cluster, caller);
    }

    /// <summary>Adds a second subscription, with its own group and lifted quotas, to a tenant that has one.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="label">A short DNS-1123 label for the group name.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The same tenant, addressed through the new subscription.</returns>
    public async Task<TenantWorld> AddSubscriptionAsync(
        TenantWorld world,
        string label,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(world);

        var subscription = Guid.NewGuid();

        var sub = await Scopes.CreateAsync(
            new() {
                Path = ScopeId.Subscription(world.Tenant, subscription).Path,
                Body = $$"""{"displayName":"{{label}}"}""",
                Caller = world.Caller
            },
            cancellationToken
        );

        sub.IsSuccess.ShouldBeTrue($"the subscription '{label}' could not be created: {sub.Error?.Message}");

        var rg = await Scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(world.Tenant, subscription, label).Path,
                Body = $$"""{"location":"{{Region}}"}""",
                Caller = world.Caller
            },
            cancellationToken
        );

        rg.IsSuccess.ShouldBeTrue($"the resource group '{label}' could not be created: {rg.Error?.Message}");

        var quota = For(world.Tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

        foreach (var meter in Enum.GetValues<QuotaMeter>().Where(static x => x != QuotaMeter.Unknown)) {
            (await quota.SetLimitAsync(meter, 1_000_000m)).IsSuccess.ShouldBeTrue();
        }

        return world with { Subscription = subscription, Group = label };
    }

    /// <summary>Makes another subject an owner of the tenant, the way the tenant's first owner was made one.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="subjectType">A <c>SubjectTypes</c> value.</param>
    /// <param name="subjectId">The subject's id.</param>
    public async Task GrantOwnerAsync(TenantWorld world, string subjectType, string subjectId) {
        ArgumentNullException.ThrowIfNull(world);

        var tuple = RelationTuple.Create(
            AuthObjectRef.Create(ObjectTypes.Tenant, N(world.Tenant)).GetValueOrThrow(),
            Relations.Owner,
            SubjectRef.Create(subjectType, subjectId).GetValueOrThrow()
        )
            .GetValueOrThrow();

        var written = await For(world.Tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(world.Tenant))
            .WriteAsync(tuple);
        written.IsSuccess.ShouldBeTrue(
            $"the owner grant for {subjectType}:{subjectId} could not be written: {written.Error?.Message}"
        );
    }

    /// <summary>The primary silo's gateway port, for a client outside the testing host — the real gateway.</summary>
    /// <remarks>
    ///     Listening on loopback only because <see cref="ConnectionTransportType.TcpSocket" /> is
    ///     asked for above; the primary is the silo <see cref="KillASecondarySiloAsync" /> and
    ///     <see cref="RestartSiloAsync" /> leave alone, so the port outlives every fault.
    /// </remarks>
    public int GatewayPort => Cluster.Primary.GatewayAddress.Endpoint.Port;

    /// <summary>Attaches the k3s to a tenant as a kubeconfig-kind cluster connection, through the real grain.</summary>
    /// <param name="cluster">The connection id.</param>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="label">For the display name.</param>
    public async Task AttachClusterAsync(Guid cluster, Guid tenant, string label) {
        var attached = await Connection(cluster)
            .AttachAsync(
                new() {
                    ClusterId = cluster,
                    OwningTenantId = tenant,
                    Kind = ClusterConnectionKind.Kubeconfig,
                    CredentialRef = "chaos:k3s",
                    Endpoint = K3sEndpoint,
                    DisplayName = "the chaos k3s, for " + label
                }
            );

        attached.IsSuccess.ShouldBeTrue($"the cluster for '{label}' could not be attached: {attached.Error?.Message}");
    }

    /// <summary>The cluster-connection grain, reached as the gateway reaches it.</summary>
    /// <param name="cluster">The connection id.</param>
    public IClusterConnectionGrain Connection(Guid cluster) =>
        Grains.GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(cluster));

    /// <summary>The operation grain for one tenant's operation.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="operationId">The operation.</param>
    public IOperationGrain Operation(Guid tenant, Guid operationId) =>
        For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

    /// <summary>A PUT of a widget through the real write path.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="name">The widget's name.</param>
    /// <param name="message">What the ConfigMap's <c>message</c> key says; change it to make a PUT an update.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<Result<WriteAccepted>> PutWidgetAsync(
        TenantWorld world,
        string name,
        string message,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(world);

        return Manager.WriteAsync(
            new() {
                Path = world.Widget(name).Path,
                ApiVersion = SampleWidgets.V2026,
                Verb = WriteVerb.Put,
                Body = SampleWidgets.Body(world.Cluster, message),
                Caller = world.Caller
            },
            cancellationToken
        );
    }

    /// <summary>Reads a widget back through the real read path.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="name">The widget's name.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<Result<ResourceSnapshot>> ReadWidgetAsync(
        TenantWorld world,
        string name,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(world);

        return Manager.ReadAsync(
            new() { Path = world.Widget(name).Path, ApiVersion = SampleWidgets.V2026, Caller = world.Caller },
            cancellationToken
        );
    }

    /// <summary>
    ///     Drives an operation until it is terminal or the budget runs out, tolerating the calls that
    ///     fail while a silo is dying.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="budget">How long to keep driving.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The last status read, and how many drive calls threw rather than answered.</returns>
    /// <remarks>
    ///     ⚠ Driven from the client rather than left to the reminder, for the reason
    ///     <c>OperationGrain</c>'s remarks give: the reminder is the safety net that survives a silo
    ///     loss and ticks once a minute; nothing else in the platform drives a pass, so a test that
    ///     waited for it would take a minute per pass. What the invariants are about is what the
    ///     operation does <i>when</i> it is driven after the fault — and a drive that throws because
    ///     its silo just died is retried, exactly as a reminder tick would be.
    /// </remarks>
    public async Task<(OperationStatus? Last, int Faults)> DriveUntilTerminalAsync(
        Guid tenant,
        Guid operationId,
        TimeSpan budget,
        CancellationToken cancellationToken
    ) {
        var clock = Stopwatch.StartNew();
        var faults = 0;
        OperationStatus? last = null;

        while (clock.Elapsed < budget) {
            try {
                var driven = await Operation(tenant, operationId).DriveAsync();

                if (driven.TryGetError(out var error)) {
                    throw new InvalidOperationException(
                        $"operation {operationId:D} cannot be driven: {error.Code} — {error.Message}"
                    );
                }

                last = driven.GetValueOrThrow();

                if (last.IsTerminal) {
                    return (last, faults);
                }
            } catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException)) {
                // ⚠ A silo died under the call, or the call raced its membership change. Counted,
                // and retried — the invariant is about where the operation ends up, and this is the
                // path a reminder tick takes after a silo loss.
                faults++;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }

        return (last, faults);
    }

    /// <summary>
    ///     Watches an operation until the platform's own driver — its reminder — has taken it to a
    ///     terminal state, or the budget runs out, without touching the grain.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="operationId">The operation.</param>
    /// <param name="budget">
    ///     How long to watch. ⚠ At least two of <see cref="OperationGrain.ReminderPeriod" />; the first pass
    ///     is a reminder tick away.
    /// </param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The state, attempt count, and activation count the durable row last showed, and when it turned terminal.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Passive, and that is the point.</b> <see cref="DriveUntilTerminalAsync" /> proves
    ///         what an operation does <i>when driven</i>; the rows about whether an acknowledged write
    ///         survives a fault are about whether anything drives it at all once the test's hands are
    ///         off it. So this reads the operation's durable row straight out of its PostgreSQL shard
    ///         — the JSON the durable tier serializes — every two seconds. Neither the read nor the
    ///         parse goes through a grain: a status call would activate the grain, and
    ///         <c>OperationGrain.OnActivateAsync</c> re-registers the reminder on activation, so a
    ///         poll through the grain would be the test arming the very driver it is watching for.
    ///     </para>
    ///     <para>
    ///         The attempt count is the grain's <c>Attempts</c>, which only <c>DriveAsync</c> moves.
    ///         Every attempt this returns is the platform's.
    ///     </para>
    /// </remarks>
    public async Task<
        (OperationState State, int Attempts, int Activations, TimeSpan? TerminalAt)> ObserveUntilTerminalAsync(
        Guid tenant,
        Guid operationId,
        TimeSpan budget,
        CancellationToken cancellationToken
    ) {
        var clock = Stopwatch.StartNew();
        var shard = ShardOf(tenant);
        var fragment = N(operationId);
        var state = OperationState.Unknown;
        var attempts = 0;
        var activations = 0;

        while (clock.Elapsed < budget) {
            foreach (var row in await DurableRowsAsync(shard, fragment, cancellationToken)) {
                // The operation's own row is the one whose payload is an OperationGrainState — a
                // Spec and an attempt count; nothing else keyed by this GUID has both.
                if (row.Payload.Length == 0
                    || System.Text.Json.Nodes.JsonNode.Parse(
                        row.Payload
                    ) is not System.Text.Json.Nodes.JsonObject payload
                    || payload["Spec"] is null
                    || payload["Attempts"] is null) {
                    continue;
                }

                state = (OperationState)(payload["Status"]?.GetValue<int>() ?? 0);
                attempts = payload["Attempts"]?.GetValue<int>() ?? 0;
                activations = payload["Activations"]?.GetValue<int>() ?? 0;
            }

            if (state is OperationState.Succeeded or OperationState.Failed or OperationState.Canceled) {
                return (state, attempts, activations, clock.Elapsed);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return (state, attempts, activations, null);
    }

    /// <summary>Whether a widget's ConfigMap is in the k3s, read with the raw client around every line of our code.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="name">The widget's name, which is the ConfigMap's.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task<bool> ConfigMapExistsAsync(TenantWorld world, string name, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(world);

        try {
            var found = await Raw.CoreV1.ListNamespacedConfigMapAsync(
                world.Namespace,
                fieldSelector: "metadata.name=" + name,
                cancellationToken: cancellationToken
            );

            return found.Items.Count == 1;
        } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode
                                                               == System.Net.HttpStatusCode.NotFound) {
            return false;
        }
    }

    /// <summary>What a widget's ConfigMap says under <c>message</c>, or <see langword="null" /> when it is not in the cluster.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="name">The widget's name, which is the ConfigMap's.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task<string?> ConfigMapMessageAsync(
        TenantWorld world,
        string name,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(world);

        try {
            var found = await Raw.CoreV1.ReadNamespacedConfigMapAsync(
                name,
                world.Namespace,
                cancellationToken: cancellationToken
            );
            return found.Data is not null && found.Data.TryGetValue("message", out var message)
                ? message
                : string.Empty;
        } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode
                                                               == System.Net.HttpStatusCode.NotFound) {
            return null;
        }
    }

    /// <summary>Reads a scope — the resource group, in every invariant that asks — through the real scope path.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<Result<ScopeSnapshot>> ReadGroupAsync(TenantWorld world, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(world);

        return Scopes.ReadAsync(
            new() { Path = ScopeId.Group(world.Tenant, world.Subscription, world.Group).Path, Caller = world.Caller },
            cancellationToken
        );
    }

    /// <summary>
    ///     Creates a tenant through the platform's own path and reports what came back, without
    ///     asserting — for the invariant that wants to know how the path fails.
    /// </summary>
    /// <param name="tenant">The tenant id.</param>
    /// <param name="label">A short DNS-1123 label for the slug.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<Result<ScopeSnapshot>> TryCreateTenantAsync(
        Guid tenant,
        string label,
        CancellationToken cancellationToken
    ) =>
        Scopes.CreateTenantAsync(
            new() {
                TenantId = tenant,
                Slug = label + "-" + N(tenant)[..8],
                DisplayName = "Chaos " + label,
                HomeRegion = Region,
                OwnerSubjectType = SubjectTypes.User,
                OwnerSubjectId = "owner-" + label
            },
            new() {
                TenantId = ReBacScopeAuthorizer.PlatformTenant,
                SubjectType = SubjectTypes.ServicePrincipal,
                SubjectId = OperatorId,
                CorrelationId = "chaos-" + label
            },
            cancellationToken
        );

    /// <summary>Deletes a widget's ConfigMap behind the reconciler's back — the drift case's <c>kubectl delete</c>.</summary>
    /// <param name="world">The tenant.</param>
    /// <param name="name">The widget's name.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task DeleteConfigMapBehindTheReconcilersBackAsync(
        TenantWorld world,
        string name,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(world);

        return Raw.CoreV1.DeleteNamespacedConfigMapAsync(name, world.Namespace, cancellationToken: cancellationToken);
    }

    // ── Faults ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Kills one secondary silo the way a pod that went away dies: no graceful stop, no deactivation, nothing
    ///     flushed.
    /// </summary>
    /// <returns>The address that died, for the log.</returns>
    /// <remarks>
    ///     ⚠ A secondary, never the primary: under <c>UseLocalhostClustering</c> and the testing host's
    ///     membership the primary hosts the membership table, so killing it is not "a silo died", it
    ///     is "the cluster lost its directory" — a different fault docs/plan/23 does not list.
    /// </remarks>
    public async Task<SiloAddress> KillASecondarySiloAsync() {
        var victim = Cluster.SecondarySilos[0];
        var address = victim.SiloAddress;
        ChaosSiloLog.Mark($"killing {address}");
        await Cluster.KillSiloAsync(victim);
        return address;
    }

    /// <summary>Starts a replacement silo, wired like the others, on a port nothing has used.</summary>
    public async Task<SiloAddress> StartASiloAsync() {
        var handle = await Cluster.StartAdditionalSiloAsync(true);
        ChaosSiloLog.Mark($"started {handle.SiloAddress}");
        return handle.SiloAddress;
    }

    /// <summary>
    ///     Brings the cluster back to <see cref="InitialSilos" /> silos — the restore step of every
    ///     test that kills or stops one, in its <c>finally</c>, so a test that threw mid-storm does
    ///     not hand the next one a two-silo cluster.
    /// </summary>
    /// <returns>How many silos were started.</returns>
    public async Task<int> RestoreClusterStrengthAsync() {
        var started = 0;

        while (Cluster.Silos.Count < InitialSilos) {
            await StartASiloAsync();
            started++;
        }

        return started;
    }

    /// <summary>Restarts one silo gracefully — stop, then start — the shape of one step of a rolling upgrade.</summary>
    /// <param name="silo">Which one. A secondary; the primary hosts the membership table.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>StopSiloAsync</c> then <c>StartAdditionalSiloAsync</c>, not the testing host's
    ///         <c>RestartSiloAsync</c>. The first run of invariant 7 used the latter and the silo was
    ///         back in 0.7 s, which is not a graceful stop — a silo that shuts down properly finishes
    ///         its in-flight requests and deactivates every grain first, and that takes seconds. The
    ///         requests that timed out in that run were the ones the abrupt path dropped on the floor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The replacement starts on a NEW port, and the second run of invariant 7 is why.</b>
    ///         Started on the stopped silo's port, the replacement was a "newer version of myself" to
    ///         the old silo — whose membership refresh was still ticking after <c>StopSiloAsync</c>
    ///         returned — and Orleans' answer to finding a newer clone of yourself in the table is
    ///         <c>Environment.FailFast</c>, which took the test process with it. A pod that comes back
    ///         gets a new address too; this is the same shape. The old address is also waited out of
    ///         the active membership before the replacement starts, so "graceful stop took" measures
    ///         the whole of it.
    ///     </para>
    /// </remarks>
    /// <returns>The replacement's address, and how long the graceful stop took on its own.</returns>
    public async Task<(SiloAddress Replacement, TimeSpan StopTook)> RestartSiloAsync(SiloAddress silo) {
        var handle = Cluster.SecondarySilos.Single(x => x.SiloAddress.Equals(silo));
        var stopping = Stopwatch.StartNew();
        ChaosSiloLog.Mark($"stopping {silo} gracefully");
        await Cluster.StopSiloAsync(handle);

        while (stopping.Elapsed < TimeSpan.FromMinutes(2) && (await Management.GetHosts(true)).ContainsKey(silo)) {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        var stopTook = stopping.Elapsed;
        var replacement = await Cluster.StartAdditionalSiloAsync(true);
        return (replacement.SiloAddress, stopTook);
    }

    /// <summary>The secondary silos' addresses — the ones a rolling restart walks.</summary>
    public IReadOnlyList<SiloAddress> SecondarySilos => [.. Cluster.SecondarySilos.Select(static x => x.SiloAddress)];

    /// <summary>The silos currently in the cluster, by address.</summary>
    public IReadOnlyList<SiloAddress> Silos => [.. Cluster.Silos.Select(static x => x.SiloAddress)];

    /// <summary>Stops a PostgreSQL shard's container. Its port mapping survives, so a restart is the same shard.</summary>
    /// <param name="shard">The shard's name.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task StopShardAsync(string shard, CancellationToken cancellationToken) {
        ChaosSiloLog.Mark($"stopping shard {shard}");
        return shards[shard].StopAsync(cancellationToken);
    }

    /// <summary>Starts a stopped shard again and waits until it accepts a connection.</summary>
    /// <param name="shard">The shard's name.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task StartShardAsync(string shard, CancellationToken cancellationToken) {
        ChaosSiloLog.Mark($"starting shard {shard}");
        await shards[shard].StartAsync(cancellationToken);
        await WaitForShardAsync(ChaosState.Storage.Durable.Shards[shard], cancellationToken);
        ChaosSiloLog.Mark($"shard {shard} accepts connections again");
    }

    /// <summary>Stops the k3s container — the managed cluster goes away.</summary>
    /// <param name="cancellationToken">The test's token.</param>
    public Task StopClusterAsync(CancellationToken cancellationToken) {
        ChaosSiloLog.Mark("stopping k3s");
        return k3s.StopAsync(cancellationToken);
    }

    /// <summary>Starts the k3s again and waits until its API server answers.</summary>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task StartClusterAsync(CancellationToken cancellationToken) {
        ChaosSiloLog.Mark("starting k3s");
        await k3s.StartAsync(cancellationToken);

        var clock = Stopwatch.StartNew();
        Exception? last = null;

        while (clock.Elapsed < TimeSpan.FromMinutes(3)) {
            try {
                _ = await Raw.CoreV1.ListNamespaceAsync(limit: 1, cancellationToken: cancellationToken);
                return;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "the k3s did not answer within three minutes of being started again.",
            last
        );
    }

    /// <summary>FLUSHALL on the hot tier's Redis. Returns how many keys there were to lose.</summary>
    /// <param name="cancellationToken">The test's token.</param>
    public async Task<long> FlushHotTierAsync(CancellationToken cancellationToken) {
        var options = ConfigurationOptions.Parse(ChaosState.RedisConnectionString);
        options.AllowAdmin = true;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        var server = multiplexer.GetServers()[0];
        var keys = await server.DatabaseSizeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        ChaosSiloLog.Mark($"FLUSHALL ({keys} keys)");
        await server.FlushAllDatabasesAsync();

        return keys;
    }

    /// <summary>How many keys the hot tier's Redis holds right now.</summary>
    public async Task<long> HotTierKeysAsync() {
        var options = ConfigurationOptions.Parse(ChaosState.RedisConnectionString);
        options.AllowAdmin = true;

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
        return await multiplexer.GetServers()[0].DatabaseSizeAsync();
    }

    /// <summary>The reminder rows a grain has, read out of the primary silo's own <see cref="IReminderTable" />.</summary>
    /// <param name="grain">The grain.</param>
    public async Task<int> ReminderRowsAsync(IAddressable grain) {
        ArgumentNullException.ThrowIfNull(grain);

        var table = Cluster.GetSiloServiceProvider().GetRequiredService<IReminderTable>();
        return (await table.ReadRows(grain.GetGrainId())).Reminders.Count;
    }

    /// <summary>Every reminder row in the table, whoever owns it.</summary>
    public async Task<int> AllReminderRowsAsync() {
        var table = Cluster.GetSiloServiceProvider().GetRequiredService<IReminderTable>();
        // ⚠ begin == end is Orleans' spelling of "the whole ring".
        return (await table.ReadRows(0, 0)).Reminders.Count;
    }

    /// <summary>
    ///     Deactivates every idle grain on every silo, so the next read of anything comes out of
    ///     storage rather than out of an activation's memory.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without this, "durable state survived" would be asserting that an in-memory object
    ///     survived a Redis flush, which nobody doubted. <c>TenantDirectoryBlackholeTests</c> makes the
    ///     same move for the same reason, one grain at a time.
    /// </remarks>
    public async Task DeactivateEverythingAsync() {
        await Management.ForceActivationCollection(TimeSpan.Zero);
        // The collection is asynchronous on each silo; a short wait is what makes the next read a storage read.
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    /// <summary>Total activations across the cluster, from the management grain's statistics.</summary>
    public async Task<int> ActivationCountAsync() {
        var stats = await Management.GetRuntimeStatistics(null);
        return stats.Sum(static x => x.ActivationCount);
    }

    /// <summary>The durable rows on one shard whose grain id carries <paramref name="fragment" />.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="fragment">A substring of the Orleans grain id — an operation's or a resource's GUID in <c>N</c> form.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public Task<List<(string GrainId, string GrainType, string Payload)>> DurableRowsAsync(
        string shard,
        string fragment,
        CancellationToken cancellationToken
    ) =>
        DurableRows.ReadAsync(ChaosState.Storage.Durable.Shards[shard], fragment, cancellationToken);

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    async Task GrantOperatorAsync() {
        var tuple = RelationTuple.Create(
            AuthObjectRef.Create(ObjectTypes.Platform, ReBacScopeAuthorizer.PlatformObjectId).GetValueOrThrow(),
            Relations.Operator,
            SubjectRef.Create(SubjectTypes.ServicePrincipal, OperatorId).GetValueOrThrow()
        )
            .GetValueOrThrow();

        var written = await For(ReBacScopeAuthorizer.PlatformTenant)
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(ReBacScopeAuthorizer.PlatformTenant))
            .WriteAsync(tuple);

        written.IsSuccess.ShouldBeTrue($"the operator grant could not be written: {written.Error?.Message}");
    }

    async Task RefreshShardMapsAsync(CancellationToken cancellationToken) {
        foreach (var silo in Cluster.Silos) {
            var refresher = Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<ShardMapRefresher>();
            (await refresher.RefreshAsync(cancellationToken)).ShouldBeTrue(
                "a silo could not read the shard map it was just given."
            );
        }
    }

    static async Task WaitForShardAsync(string connectionString, CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();
        Exception? last = null;

        while (clock.Elapsed < TimeSpan.FromMinutes(2)) {
            try {
                await using var connection = new NpgsqlConnection(
                    new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString
                );

                await connection.OpenAsync(cancellationToken);
                return;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "the shard did not accept a connection within two minutes of being started again.",
            last
        );
    }

    static PostgreSqlContainer NewShard() =>
        new PostgreSqlBuilder(ClusterInfrastructure.PostgresImage)
            .WithDatabase("cybercloud")
            .WithUsername("cybercloud")
            .WithPassword("cybercloud")
            // Fixed, so a stopped shard comes back where the silos' connection strings point — see k3s.
            .WithPortBinding(FreePort(), PostgreSqlBuilder.PostgreSqlPort)
            .Build();

    /// <summary>A host port nothing is listening on right now.</summary>
    static int FreePort() {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp
        );

        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
    }

    // ⚠ Short connect and command timeouts, as TenancyCluster sets them: a stopped shard must fail a
    // write in seconds, not hang it for Npgsql's default fifteen — the invariant is about writes that
    // PAUSE, and a pause that looks like a hang is indistinguishable from a lost cluster.
    static string ConnectionString(PostgreSqlContainer container) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Timeout = 3, CommandTimeout = 10 }
            .ConnectionString;

    static string D(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    static string N(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Binds <see cref="ChaosTopology" /> to every invariant's class.</summary>
[CollectionDefinition(Name)]
public sealed class ChaosSuite : ICollectionFixture<ChaosTopology> {
    /// <summary>The collection's name.</summary>
    public const string Name = "chaos-topology";
}
