using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy;
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

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>The empty ABP module <c>CreateSilo</c> insists on.</summary>
sealed class AuthorizationSiloModule : AbpModule;

/// <summary>
///     An <see cref="IRelationWriteInterceptor" /> a test can arm to kill a tuple write
///     <b>
///         exactly
///         between the two grains
///     </b>
///     .
/// </summary>
/// <remarks>
///     See <see cref="IRelationWriteInterceptor" /> for why this seam exists at all: docs/plan/07
///     § Storage's central safety claim is about what happens when the write dies there, and the
///     only honest way to check it is to make the write die there.
/// </remarks>
public sealed class ArmableWriteInterceptor : IRelationWriteInterceptor {
    /// <summary>When true, the next write throws after its object half has landed.</summary>
    public bool Armed { get; set; }

    /// <summary>How many times it has fired.</summary>
    public int Fired { get; private set; }

    /// <inheritdoc />
    public ValueTask AfterObjectWriteAsync(RelationTuple tuple, bool isDelete) {
        if (!Armed) {
            return ValueTask.CompletedTask;
        }

        Armed = false;
        Fired++;

        throw new InvalidOperationException(
            "The silo died between the object write and the subject write. (Armed by "
            + nameof(ArmableWriteInterceptor)
            + " — see TwoGrainWriteTests.)"
        );
    }
}

/// <summary>
///     One real silo, one real Redis, three real PostgreSQL servers — the same shape as
///     <c>CyberCloud.Tenancy.Tests.Infrastructure.TenancyCluster</c>, and for the same reasons.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Three PostgreSQL servers, because "tuples are sharded by tenant" (docs/plan/07
///             § Storage) is a claim that can be false.
///         </b>
///         Two of them carry tenants and the third
///         carries every null-tenant grain. <c>CrossTenantAuthorizationTests</c> reads rows back out
///         of the shards with plain SQL, which is how "tenant A's tuples are in THAT database" gets
///         shown rather than asserted.
///     </para>
///     <para>
///         The silo runs the production <c>AddCyberCloudTenancy</c> wiring, so the cross-tenant
///         separation under test is the separation a deployed silo would have, and
///         <c>AddCyberCloudAuthorization</c> with the real <see cref="CyberCloudSchema" />.
///     </para>
/// </remarks>
public sealed class AuthorizationCluster : IAsyncLifetime {
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

    /// <summary>The silo's grain factory. Tenant-unaware — qualify with <see cref="For" />.</summary>
    public IGrainFactory Grains => silo.Services.GetRequiredService<IGrainFactory>();

    /// <summary>The write seam, for the interruption test.</summary>
    public ArmableWriteInterceptor Interceptor =>
        (ArmableWriteInterceptor)silo.Services.GetRequiredService<IRelationWriteInterceptor>();

    /// <summary>The connection table, for reading rows back with plain SQL.</summary>
    public IShardConnections Connections => silo.Services.GetRequiredService<IShardConnections>();

    /// <summary>Every configured durable shard id.</summary>
    public static IReadOnlyList<string> AllShards => [ShardA, ShardB, PlatformShard];

    /// <summary>The durable shard the storage layer is actually routing a tenant's grains to.</summary>
    /// <param name="tenant">The tenant.</param>
    public string DurableShardOf(Guid tenant) =>
        silo.Services.GetRequiredService<IShardMapCache>().DurableShardFor(Id(tenant));

    /// <summary>Two tenants the real placement function puts on <b>different</b> shards.</summary>
    /// <remarks>
    ///     Deterministic, so a failure is reproducible on every machine. "Tuples are sharded by
    ///     tenant" (docs/plan/07 § Storage) is only demonstrable across a shard boundary.
    /// </remarks>
    public (Guid First, Guid Second) SplitPair(int from) {
        var first = Tenant(from);
        var firstShard = DurableShardOf(first);

        for (var i = from + 1; i < from + 1_000; i++) {
            var candidate = Tenant(i);
            if (!string.Equals(DurableShardOf(candidate), firstShard, StringComparison.Ordinal)) {
                return (first, candidate);
            }
        }

        throw new InvalidOperationException(
            "1 000 tenants and no pair on different shards — the placement function is broken."
        );
    }

    /// <summary>A GUID that is a pure function of its index, so ids are stable across runs.</summary>
    public static Guid Tenant(int index) {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BitConverter.TryWriteBytes(bytes, index);
        bytes[15] = 0xA2;
        return new(bytes);
    }

    /// <summary>A tenant id in the form every call site uses to qualify a grain.</summary>
    public static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A tenant-qualified grain factory.</summary>
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(Id(tenant));

    /// <summary>The tenant's tuple store.</summary>
    public ITupleStoreGrain Store(Guid tenant) => For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant));

    /// <summary>An object's forward tuples.</summary>
    public IObjectRelationsGrain Objects(Guid tenant, ObjectRef target) =>
        For(tenant).GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(target.Type, target.Id));

    /// <summary>A subject's reverse index.</summary>
    public ISubjectRelationsGrain SubjectIndex(Guid tenant, SubjectRef subject) =>
        For(tenant).GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(subject.Type, subject.Id));

    /// <summary>An object's check grain.</summary>
    public ICheckGrain Check(Guid tenant, ObjectRef target) =>
        For(tenant).GetGrain<ICheckGrain>(GrainKeys.CheckCache(target.Type, target.Id));

    /// <summary>Writes a tuple through the store and returns the token.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tuple">The tuple, in the <c>object#relation@subject</c> grammar.</param>
    public async Task<ConsistencyToken> WriteAsync(Guid tenant, string tuple) {
        var written = await Store(tenant).WriteAsync(RelationTuple.Parse(tuple).GetValueOrThrow());
        return written.IsSuccess
            ? written.GetValueOrThrow()
            : throw new InvalidOperationException($"Writing '{tuple}' failed: {written.Error!.Message}");
    }

    /// <summary>Revokes a tuple through the store and returns the token.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tuple">The tuple.</param>
    public async Task<ConsistencyToken> RevokeAsync(Guid tenant, string tuple) {
        var deleted = await Store(tenant).DeleteAsync(RelationTuple.Parse(tuple).GetValueOrThrow());
        return deleted.IsSuccess
            ? deleted.GetValueOrThrow()
            : throw new InvalidOperationException($"Revoking '{tuple}' failed: {deleted.Error!.Message}");
    }

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

        silo = await StartSiloAsync(connections);
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

    /// <summary>Opens a connection to one shard, for reading grain rows with plain SQL.</summary>
    /// <param name="shard">The shard id.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<NpgsqlConnection> OpenShardAsync(
        string shard,
        CancellationToken cancellationToken
    ) {
        var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Connections.Durable(shard)) { Pooling = false }
                .ConnectionString
        );

        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    static PostgreSqlContainer NewShard() =>
        new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("cybercloud")
            .WithUsername("cybercloud")
            .WithPassword("cybercloud")
            .Build();

    static string Shard(PostgreSqlContainer container) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Timeout = 3, CommandTimeout = 5 }
            .ConnectionString;

    async Task<WebApplication> StartSiloAsync(Dictionary<string, string> connections) {
        List<string> args = [
            "--environment", "Development",
            "--urls", "http://127.0.0.1:0",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostSiloPort={FreePort()}",
            $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={FreePort()}",
            $"--{CyberCloudStorageOptions.SectionName}:Hot:ConnectionString={redis.GetConnectionString()}",
            $"--{CyberCloudStorageOptions.SectionName}:Durable:MaxPoolSize=5",
            $"--{CyberCloudStorageOptions.SectionName}:Durable:BootstrapShard={ShardA}",
            $"--{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard={PlatformShard}"
        ];

        args.AddRange(
            connections.Select(x =>
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:{x.Key}={x.Value}"
            )
        );

        var builder = OrleansApplication.CreateSilo(
            [.. args],
            cluster => cluster.ConfigureServices(services => {
                    // Registered BEFORE AddCyberCloudAuthorization's TryAdd runs, so this wins.
                    services.AddSingleton<ArmableWriteInterceptor>();
                    services.AddSingleton<IRelationWriteInterceptor>(sp =>
                        sp.GetRequiredService<ArmableWriteInterceptor>()
                    );

                    // The tenancy refreshers are background loops this suite does not drive.
                    services.Configure<TenancyRefreshOptions>(o => o.RunBackgroundRefresh = false);
                }
            ),
            (cluster, options) =>
                cluster.AddCyberCloudTenancy(options).AddCyberCloudAuthorization()
        );

        await builder.Services.AddApplicationAsync<AuthorizationSiloModule>();

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    static int FreePort() {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

/// <summary>Binds <see cref="AuthorizationCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class AuthorizationSuite : ICollectionFixture<AuthorizationCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "authorization-cluster";
}
