using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Directory;
using CyberCloud.Tenancy.Separation;
using CyberCloud.Tenancy.Shards;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Multitenant;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Volo.Abp.Modularity;

namespace CyberCloud.Tenancy.Tests.Infrastructure;

/// <summary>The empty ABP module <c>CreateSilo</c> insists on.</summary>
sealed class TenancySiloModule : AbpModule;

/// <summary>
///     A clock a test can move. Registered in place of <see cref="SystemClock" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The only way the lease tests are honest.</b> The index claim's lease is five minutes and
///     the quota lease is sixty; a test that waited for either would be a test nobody runs. Advancing
///     an injected clock exercises the <i>real</i> expiry code — the sweep on read, the
///     <c>Effective</c> collapse — rather than a shortened copy of it. <see cref="IClock" />'s own
///     remarks make this the reason the interface exists.
/// </remarks>
public sealed class TestClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>An <see cref="IPlatformOperatorAuthority" /> a test can switch on and off.</summary>
public sealed class SwitchablePlatformOperatorAuthority : IPlatformOperatorAuthority {
    /// <summary>The operator id to report, or <see langword="null" /> to deny.</summary>
    public string? Operator { get; set; }

    /// <inheritdoc />
    public string? OperatorFor(string targetTenantId) => Operator;
}

/// <summary>An <see cref="ICrossTenantDelegationStore" /> a test can add pairs to.</summary>
public sealed class SwitchableDelegationStore : ICrossTenantDelegationStore {
    /// <summary>The (source, target) pairs currently delegated.</summary>
    public HashSet<(string Source, string Target)> Delegations { get; } = [];

    /// <inheritdoc />
    public bool IsDelegated(string source, string target) => Delegations.Contains((source, target));
}

/// <summary>
///     One real silo, one real Redis, and <b>three</b> real PostgreSQL servers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three, not two, and the third is the point of several tests.</b> Two tenant shards
///         make "sharded by tenant" a claim that can be false. The third is the <b>platform</b>
///         shard — <c>CyberCloud:Storage:Durable:NullTenantShard</c> — which carries every
///         null-tenant grain: the tenant directory and the shard map (docs/plan/04 § Grain taxonomy,
///         the Platform row). Separating it is what makes docs/plan/05 § The tenant directory's
///         failure mode testable with real infrastructure rather than with a stub: stop that one
///         container and the global directory is genuinely unreachable while both tenant shards are
///         genuinely fine, which is exactly the blast radius the document claims and
///         chaos-invariant 5 in docs/plan/23 asserts.
///     </para>
///     <para>
///         Nothing here is mocked — ADR-018. The silo is a real <c>WebApplication</c> built by
///         <c>OrleansApplication.CreateSilo</c>, with the production
///         <c>AddCyberCloudTenancy</c> wiring, so the separation under test is the separation a
///         deployed silo would have.
///     </para>
/// </remarks>
public sealed class TenancyCluster : IAsyncLifetime {
    /// <summary>The first tenant-carrying durable shard.</summary>
    public const string ShardA = "durable-00";

    /// <summary>The second tenant-carrying durable shard.</summary>
    public const string ShardB = "durable-01";

    /// <summary>The shard that carries every null-tenant platform grain.</summary>
    public const string PlatformShard = "platform-00";

    readonly RedisContainer redis = new RedisBuilder("redis:8-alpine")
        .WithCommand("--maxmemory-policy", "noeviction", "--appendonly", "yes", "--appendfsync", "everysec")
        .Build();

    readonly PostgreSqlContainer shardA = NewShard();
    readonly PostgreSqlContainer shardB = NewShard();
    readonly PostgreSqlContainer platform = NewShard();

    WebApplication silo = null!;

    /// <summary>The running silo's service provider.</summary>
    public IServiceProvider Services => silo.Services;

    /// <summary>The silo's grain factory. Tenant-unaware — see route 7.</summary>
    public IGrainFactory Grains => silo.Services.GetRequiredService<IGrainFactory>();

    /// <summary>The clock every grain in the silo reads.</summary>
    public TestClock Clock => (TestClock)silo.Services.GetRequiredService<IClock>();

    /// <summary>The shard map the storage layer is actually routing with.</summary>
    public GrainBackedShardMapCache ShardMap => silo.Services.GetRequiredService<GrainBackedShardMapCache>();

    /// <summary>The shard-map refresher, so a test can pull a delta on demand.</summary>
    public ShardMapRefresher ShardMapRefresher => silo.Services.GetRequiredService<ShardMapRefresher>();

    /// <summary>The in-process tenant directory mirror.</summary>
    public TenantDirectoryCache Directory => silo.Services.GetRequiredService<TenantDirectoryCache>();

    /// <summary>The cross-tenant authorizer the silo is using.</summary>
    public PlatformCrossTenantAuthorizer Authorizer =>
        silo.Services.GetRequiredService<PlatformCrossTenantAuthorizer>();

    /// <summary>The platform-operator switch, for the platform → tenant edge.</summary>
    public SwitchablePlatformOperatorAuthority Operators =>
        (SwitchablePlatformOperatorAuthority)silo.Services.GetRequiredService<IPlatformOperatorAuthority>();

    /// <summary>The delegation switch, for the tenant → tenant edge.</summary>
    public SwitchableDelegationStore Delegations =>
        (SwitchableDelegationStore)silo.Services.GetRequiredService<ICrossTenantDelegationStore>();

    /// <summary>The connection table.</summary>
    public IShardConnections Connections => silo.Services.GetRequiredService<IShardConnections>();

    /// <summary>The Redis endpoint, for tests that look at the keyspace directly.</summary>
    public string RedisConnectionString => redis.GetConnectionString();

    /// <summary>A tenant on <see cref="ShardA" /> and a tenant on <see cref="ShardB" />.</summary>
    public (Guid OnShardA, Guid OnShardB) SplitTenants { get; private set; }

    /// <summary>Two tenants the placement function puts on the <i>same</i> shard.</summary>
    public (Guid First, Guid Second) CotenantsOnOneShard { get; private set; }

    /// <summary>Every configured durable shard id.</summary>
    public static IReadOnlyList<string> AllShards => [ShardA, ShardB, PlatformShard];

    /// <summary>The tenant shard ids — the ones the map places tenants over.</summary>
    public static IReadOnlyList<string> TenantShards => [ShardA, ShardB];

    /// <summary>A GUID that is a pure function of its index, so ids are stable across runs.</summary>
    public static Guid Tenant(int index) {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BitConverter.TryWriteBytes(bytes, index);
        bytes[15] = 0x5C;
        return new(bytes);
    }

    /// <summary>A tenant id in the form every call site uses to qualify a grain.</summary>
    public static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A tenant-qualified grain factory.</summary>
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(Id(tenant));

    /// <summary>The tenant's own grain.</summary>
    public ITenantGrain TenantGrain(Guid tenant) => For(tenant).GetGrain<ITenantGrain>(GrainKeys.Tenant(tenant));

    /// <summary>A subscription grain in a tenant.</summary>
    public ISubscriptionGrain SubscriptionGrain(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription));

    /// <summary>A resource group grain in a tenant.</summary>
    public IResourceGroupGrain ResourceGroupGrain(Guid tenant, Guid subscription, string name) =>
        For(tenant).GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, name));

    /// <summary>A quota grain in a tenant.</summary>
    public IQuotaGrain QuotaGrain(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The path-index grain for an address, in that address's tenant.</summary>
    public IResourceIndexGrain ResourceIndexGrain(ResourceId address) =>
        For(address.TenantId).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address));

    /// <summary>The email-index grain for an address, in a tenant.</summary>
    public IEmailIndexGrain EmailIndexGrain(Guid tenant, string email) =>
        For(tenant).GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(tenant, email));

    /// <summary>The shard map grain — null tenant, one worldwide.</summary>
    public IShardMapGrain ShardMapGrain() => Grains.GetGrain<IShardMapGrain>(GrainKeys.ShardMap());

    /// <summary>The tenant directory grain — null tenant, one worldwide.</summary>
    public ITenantDirectoryGrain DirectoryGrain() =>
        Grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory());

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            redis.StartAsync(token),
            shardA.StartAsync(token),
            shardB.StartAsync(token),
            platform.StartAsync(token)
        );

        var connections = new Dictionary<string, string>(StringComparer.Ordinal) {
            [ShardA] = Shard(shardA), [ShardB] = Shard(shardB), [PlatformShard] = Shard(platform)
        };

        foreach (var connectionString in connections.Values) {
            await OrleansAdoNetSchema.CreateAsync(connectionString, token);
        }

        SplitTenants = FindPair(true);
        CotenantsOnOneShard = FindPair(false);

        silo = await StartSiloAsync(connections);

        // The map has to know its shards before it can place a tenant, and the cache has to have
        // pulled them before the storage layer routes anything by them. Production does this from
        // configuration at deploy time; here it is explicit so the ordering is visible.
        (await ShardMapGrain().ConfigureShardsAsync([.. TenantShards])).IsSuccess.ShouldBeTrueOrThrow();
        await ShardMapRefresher.RefreshAsync(token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (silo is not null) {
            try {
                await silo.StopAsync();
            } catch (InvalidOperationException) {
                // Never started.
            }

            await silo.DisposeAsync();
        }

        await Task.WhenAll(
            redis.DisposeAsync().AsTask(),
            shardA.DisposeAsync().AsTask(),
            shardB.DisposeAsync().AsTask(),
            platform.DisposeAsync().AsTask()
        );
    }

    /// <summary>Opens a connection to one shard, for reading rows back with plain SQL.</summary>
    /// <param name="shard">The shard id.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<NpgsqlConnection> OpenShardAsync(string shard, CancellationToken cancellationToken) {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Connections.Durable(shard)) { Pooling = false }
                .ConnectionString
        );

        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    ///     Stops the PostgreSQL server that carries every null-tenant platform grain — the
    ///     "blackhole the global cluster" of docs/plan/23's chaos-invariant 5.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Stopped, not paused.</b> A paused container drops packets, so every call waits out
    ///     the full connection timeout and the test measures Npgsql's patience rather than the
    ///     cache's behaviour. A stopped one has its port unmapped, so the connection is refused
    ///     immediately and the failure the cache has to survive arrives in milliseconds — which is
    ///     also the more honest simulation of a cluster that is gone rather than slow.
    /// </remarks>
    public Task BlackholeThePlatformShardAsync(CancellationToken cancellationToken) =>
        platform.StopAsync(cancellationToken);

    static PostgreSqlContainer NewShard() =>
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("cybercloud")
            .WithUsername("cybercloud")
            .WithPassword("cybercloud")
            .Build();

    static string Shard(PostgreSqlContainer container) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) {
            // Short, so that a blackholed shard fails fast rather than making the suite wait.
            Timeout = 3, CommandTimeout = 5
        }.ConnectionString;

    async Task<WebApplication> StartSiloAsync(Dictionary<string, string> connections) {
        List<string> args = [
            "--environment", "Development",
            "--urls", "http://127.0.0.1:0",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
            $"--{CyberCloudStorageOptions.SectionName}:Hot:ConnectionString={redis.GetConnectionString()}",
            $"--{CyberCloudStorageOptions.SectionName}:Durable:MaxPoolSize=5",
            $"--{CyberCloudStorageOptions.SectionName}:Durable:BootstrapShard={ShardA}",
            // ⚠ THE "Null" TENANT TRAP, configured. Every Platform grain is null-tenant AND durable,
            // so without this the storage layer would hash the literal string "Null" into the tenant
            // shard list — deterministic but arbitrary. docs/plan/05 § Storage provider wiring's
            // Guid.Parse(tenantId) would not even get that far.
            $"--{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard={PlatformShard}"
        ];

        args.AddRange(
            connections.Select(x =>
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:{x.Key}={x.Value}"
            )
        );

        var builder = OrleansApplication.CreateSilo(
            [.. args],
            silo => silo.ConfigureServices(services => {
                    // Registered BEFORE AddCyberCloudTenancy's TryAdd calls run, so these win.
                    services.AddSingleton<IClock, TestClock>();
                    services.AddSingleton<SwitchablePlatformOperatorAuthority>();
                    services.AddSingleton<SwitchableDelegationStore>();
                    services.AddSingleton<IPlatformOperatorAuthority>(sp =>
                        sp.GetRequiredService<SwitchablePlatformOperatorAuthority>()
                    );
                    services.AddSingleton<ICrossTenantDelegationStore>(sp =>
                        sp.GetRequiredService<SwitchableDelegationStore>()
                    );

                    // ⚠ The background refresh loops are OFF and every test drives RefreshAsync by
                    // hand. A test that asserts on cache MISSES cannot share a process with a loop that
                    // is quietly filling the cache — the assertion would pass or fail on timing, which
                    // is how a suite earns a `[Skip]`. The method the tests call is the method the loop
                    // calls, so nothing is stubbed out; only the schedule is.
                    services.Configure<TenancyRefreshOptions>(o => o.RunBackgroundRefresh = false);
                }
            ),
            (silo, options) => silo.AddCyberCloudTenancy(options)
        );

        await builder.Services.AddApplicationAsync<TenancySiloModule>();

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    /// <summary>
    ///     Two tenants the <i>real</i> placement function puts on different shards (or the same
    ///     one). Deterministic, so a failure is reproducible on every machine.
    /// </summary>
    static (Guid, Guid) FindPair(bool different) {
        var map = MapOverTenantShards();
        var first = Tenant(0);
        var firstShard = map.DurableShardFor(Id(first));

        for (var i = 1; i < 1000; i++) {
            var candidate = Tenant(i);
            var same = string.Equals(map.DurableShardFor(Id(candidate)), firstShard, StringComparison.Ordinal);

            if (same != different) {
                return (first, candidate);
            }
        }

        throw new InvalidOperationException(
            "1000 tenants and no pair with the wanted placement — the placement function is broken."
        );
    }

    static GrainBackedShardMapCache MapOverTenantShards() {
        var options = new CyberCloudStorageOptions();
        foreach (var shard in AllShards) {
            options.Durable.Shards[shard] = "Host=unused";
        }

        options.Durable.NullTenantShard = PlatformShard;

        var map = new GrainBackedShardMapCache(options);

        // The fallback must place over the TENANT shards only, which is what the shard map grain is
        // configured with, so the pair this picks is the pair the running silo will agree with.
        map.Apply(new() { Version = 1, DurableShards = [.. TenantShards] });

        return map;
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>Binds <see cref="TenancyCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class TenancySuite : ICollectionFixture<TenancyCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "tenancy-cluster";
}

/// <summary>
///     A <b>second, private</b> cluster for the tests that break their own infrastructure.
/// </summary>
/// <remarks>
///     xUnit gives each collection its own fixture instance, so this is a separate silo with its own
///     four containers. It costs a second bring-up and it buys the only honest version of
///     docs/plan/23's chaos-invariant 5: <c>TenantDirectoryBlackholeTests</c>
///     <b>
///         stops the
///         PostgreSQL server
///     </b>
///     that carries the global directory, which is not something a test can do
///     to a fixture other tests are still using.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DestructiveTenancySuite : ICollectionFixture<TenancyCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "tenancy-cluster-destructive";
}

/// <summary>
///     A third cluster, for the shard-map tests.
/// </summary>
/// <remarks>
///     Those tests add shard ids to the map that have no PostgreSQL server behind them — the only
///     way to exercise "what happens when the shard list grows" — and the refresher pushes that list
///     into the silo's cache within seconds. In a shared cluster that would move other tests'
///     unassigned tenants onto a shard with no connection string, which is the very hazard those
///     tests exist to rule out.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ShardMapSuite : ICollectionFixture<TenancyCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "tenancy-cluster-shardmap";
}

/// <summary>Tiny assertion helper, so the fixture does not depend on Shouldly's namespace.</summary>
static class FixtureAssertions {
    /// <summary>Throws if the value is false.</summary>
    public static void ShouldBeTrueOrThrow(this bool value) {
        if (!value) {
            throw new InvalidOperationException("A fixture precondition failed.");
        }
    }
}
