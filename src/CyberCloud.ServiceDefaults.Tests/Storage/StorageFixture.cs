using CyberCloud.ServiceDefaults.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Volo.Abp.Modularity;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>The empty ABP module <c>CreateSilo</c> insists on.</summary>
sealed class StorageSiloModule : AbpModule;

/// <summary>
///     A real silo, a real Redis and <b>two</b> real PostgreSQL servers — the smallest arrangement in
///     which "sharded by tenant" is a claim that can be false.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two Postgres containers, not one database with two schemas.</b> docs/plan/05
///         § Durable is explicit that the durable tier is "N plain Postgres servers that do not know
///         about each other", and the whole cross-tenant argument is that tenant A's rows are not
///         merely filtered out of tenant B's view but are <i>in a different server</i>. One container
///         with two databases would still prove routing, and would quietly stop proving the thing
///         that makes a cross-tenant <c>JOIN</c> impossible.
///     </para>
///     <para>
///         The Redis here is a plain single node. Cluster mode — where the hash tag actually changes
///         behaviour — needs a differently-launched server and lives in
///         <see cref="RedisClusterHashTagTests" />.
///     </para>
/// </remarks>
public sealed class StorageFixture : IAsyncLifetime {
    /// <summary>The first durable shard's id.</summary>
    public const string ShardA = "durable-00";

    /// <summary>The second durable shard's id.</summary>
    public const string ShardB = "durable-01";

    /// <summary>The <c>application_name</c> the shard connections carry, so backends can be counted.</summary>
    public const string ApplicationNamePrefix = "cc-durable-";

    /// <summary>The grain-type string the provider-level tests use.</summary>
    public const string ProviderTestGrainType = "CyberCloud.Tests.ProviderLevel";

    readonly RedisContainer redis = new RedisBuilder("redis:8-alpine")
        // docs/plan/05 § Hot: "maxmemory-policy noeviction (a hot-tier eviction is a correctness
        // bug, not a capacity event — it must page, not silently drop state)". Set explicitly rather
        // than relying on the image default, because relying on a default is how a tier ends up with
        // allkeys-lru in one environment and noeviction in another.
        .WithCommand("--maxmemory-policy", "noeviction", "--appendonly", "yes", "--appendfsync", "everysec")
        .Build();

    readonly PostgreSqlContainer shardA = NewShard();
    readonly PostgreSqlContainer shardB = NewShard();

    WebApplication silo = null!;

    /// <summary>The running silo's service provider.</summary>
    public IServiceProvider Services => silo.Services;

    /// <summary>The silo's grain factory.</summary>
    public IGrainFactory Grains => silo.Services.GetRequiredService<IGrainFactory>();

    /// <summary>The hot tier's <c>configureTenantOptions</c> body, with its invocation counters.</summary>
    public HotTierConfigurator Hot => silo.Services.GetRequiredService<HotTierConfigurator>();

    /// <summary>The durable tier's <c>configureTenantOptions</c> body.</summary>
    public DurableTierConfigurator Durable => silo.Services.GetRequiredService<DurableTierConfigurator>();

    /// <summary>The shard map the silo is actually using.</summary>
    public IShardMapCache ShardMap => silo.Services.GetRequiredService<IShardMapCache>();

    /// <summary>The connection table the silo is actually using.</summary>
    public IShardConnections Connections => silo.Services.GetRequiredService<IShardConnections>();

    /// <summary>The Redis endpoint, for tests that want to look at the keyspace directly.</summary>
    public string RedisConnectionString => redis.GetConnectionString();

    /// <summary>
    ///     A tenant on <see cref="ShardA" /> and a tenant on <see cref="ShardB" />, chosen by the
    ///     real placement function rather than pinned, so that the pair proves the hash spreads.
    /// </summary>
    public (Guid OnShardA, Guid OnShardB) SplitTenants { get; private set; }

    /// <summary>Two tenants that the placement function puts on the <i>same</i> shard.</summary>
    /// <remarks>
    ///     The harder isolation case, and the one a single-shard test would miss entirely: two
    ///     tenants in one database, where separation is the grain key and nothing else.
    /// </remarks>
    public (Guid First, Guid Second) CotenantsOnOneShard { get; private set; }

    /// <summary>Every configured durable shard id.</summary>
    public static IReadOnlyList<string> AllShards => [ShardA, ShardB];

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        await Task.WhenAll(
            redis.StartAsync(token),
            shardA.StartAsync(token),
            shardB.StartAsync(token)
        );

        var connections = new Dictionary<string, string>(StringComparer.Ordinal) {
            [ShardA] = Shard(shardA, ShardA), [ShardB] = Shard(shardB, ShardB)
        };

        foreach (var connectionString in connections.Values) {
            // The shipped applier, not a test-local copy of it. `--apply-durable-schema` runs this
            // exact call, so a defect in it fails here rather than in a cluster.
            await OrleansAdoNetSchema.ApplyAsync(connectionString, token);
        }

        SplitTenants = FindSplitPair(connections.Keys);
        CotenantsOnOneShard = FindCotenantPair(connections.Keys);

        silo = await StartSiloAsync(connections);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (silo is not null) {
            try {
                await silo.StopAsync();
            } catch (InvalidOperationException) {
                // Never started; nothing to stop.
            }

            await silo.DisposeAsync();
        }

        await Task.WhenAll(
            redis.DisposeAsync().AsTask(),
            shardA.DisposeAsync().AsTask(),
            shardB.DisposeAsync().AsTask()
        );
    }

    /// <summary>Opens a connection to one shard, for reading rows back with plain SQL.</summary>
    /// <param name="shard">The shard id.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<NpgsqlConnection> OpenShardAsync(string shard, CancellationToken cancellationToken) {
        // Pooling off for the same reason as OrleansAdoNetSchema: these connections are the test
        // looking at the database, and they must not show up in the silo's per-shard backend count.
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Connections.Durable(shard)) { Pooling = false }.ConnectionString
        );

        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>A GUID that is a pure function of its index, so tenant ids are stable across runs.</summary>
    public static Guid Tenant(int index) {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BitConverter.TryWriteBytes(bytes, index);
        bytes[15] = 0x5C;
        return new(bytes);
    }

    static PostgreSqlContainer NewShard() =>
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("cybercloud")
            .WithUsername("cybercloud")
            .WithPassword("cybercloud")
            .Build();

    static string Shard(PostgreSqlContainer container, string shard) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) {
            // So pg_stat_activity can be asked "how many backends does this silo hold open against
            // this shard" — the observable half of the MaxPoolSize claim.
            ApplicationName = ApplicationNamePrefix + shard
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
            $"--{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard={ShardA}"
        ];

        args.AddRange(
            connections.Select(x =>
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:{x.Key}={x.Value}"
            )
        );

        var builder = OrleansApplication.CreateSilo([.. args]);
        await builder.Services.AddApplicationAsync<StorageSiloModule>();

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    /// <summary>
    ///     Walks GUIDs until two of them hash to different shards. Deterministic — the same seed
    ///     produces the same pair on every machine and every run, so a failure is reproducible.
    /// </summary>
    static (Guid, Guid) FindSplitPair(IEnumerable<string> shards) {
        var map = MapOver(shards);
        var first = Tenant(0);

        for (var i = 1; i < 1000; i++) {
            var candidate = Tenant(i);
            if (!string.Equals(
                    map.DurableShardFor(candidate.ToString("D", CultureInfo.InvariantCulture)),
                    map.DurableShardFor(first.ToString("D", CultureInfo.InvariantCulture)),
                    StringComparison.Ordinal
                )) {
                return (first, candidate);
            }
        }

        throw new InvalidOperationException("1000 tenants all hashed to one shard — the placement function is broken.");
    }

    static (Guid, Guid) FindCotenantPair(IEnumerable<string> shards) {
        var map = MapOver(shards);
        var first = Tenant(0);

        for (var i = 1; i < 1000; i++) {
            var candidate = Tenant(i);
            if (string.Equals(
                    map.DurableShardFor(candidate.ToString("D", CultureInfo.InvariantCulture)),
                    map.DurableShardFor(first.ToString("D", CultureInfo.InvariantCulture)),
                    StringComparison.Ordinal
                )) {
                return (first, candidate);
            }
        }

        throw new InvalidOperationException(
            "1000 tenants and no two shared a shard — the placement function is broken."
        );
    }

    static StaticShardMapCache MapOver(IEnumerable<string> shards) {
        var options = new CyberCloudStorageOptions();
        foreach (var shard in shards) {
            options.Durable.Shards[shard] = "Host=unused";
        }

        return new(options);
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>Binds <see cref="StorageFixture" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class StorageSuite : ICollectionFixture<StorageFixture> {
    /// <summary>The collection name.</summary>
    public const string Name = "storage-containers";
}
