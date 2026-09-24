using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.ServiceDefaults;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Multitenant;
using Serilog.Core;
using Serilog.Events;
using StackExchange.Redis;
using System.Collections.Concurrent;
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
///     A clock a test can move — and move back. Registered in place of <see cref="SystemClock" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The only honest way to test an expiry.</b> A just-in-time grant that a test waited out
///     would be a test nobody runs; advancing an injected clock exercises the real filter in the
///     object grain, the real cache comparison and the real sweep. Moving it <i>back</i> is what
///     proves a sweep deleted a row rather than hiding it: a tuple still stored would reappear.
///     Every test that moves it puts it back where it found it, because the collection shares one.
/// </remarks>
public sealed class MovableClock : IClock {
    /// <summary>Where every test starts.</summary>
    public static readonly DateTimeOffset Start = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = Start;
}

/// <summary>Captures the silo's log events, structured, so an audit event can be asserted field by field.</summary>
/// <remarks>
///     ⚠ <b>A Serilog sink, not an <c>ILoggerProvider</c>.</b> <c>OrleansApplication</c>'s host
///     replaces <c>ILoggerFactory</c> with Serilog's, so a provider registered in the container is
///     never consumed — <c>LogEgressTests.EveryLoggerInTheProcessIsASerilogLogger</c> is the rule. What
///     the host does read from the container is sinks (<c>ReadFrom.Services</c>), which is the one
///     door a test has into the pipeline the audit events actually travel.
/// </remarks>
public sealed class AuditCapture : ILogEventSink {
    /// <summary>Every event: its <c>EventId</c>, its rendered message, and its named fields.</summary>
    public ConcurrentQueue<(int Id, string Message, IReadOnlyDictionary<string, object?> Fields)> Events { get; } = [];

    /// <inheritdoc />
    public void Emit(LogEvent logEvent) {
        ArgumentNullException.ThrowIfNull(logEvent);

        Dictionary<string, object?> fields = new(StringComparer.Ordinal);
        var id = 0;

        foreach (var (key, value) in logEvent.Properties) {
            if (string.Equals(key, "EventId", StringComparison.Ordinal) && value is StructureValue structure) {
                id = structure.Properties.FirstOrDefault(static p => p.Name == "Id")?.Value is ScalarValue { Value: int number }
                    ? number
                    : 0;

                continue;
            }

            fields[key] = value is ScalarValue scalar ? scalar.Value : value.ToString();
        }

        Events.Enqueue((id, logEvent.RenderMessage(CultureInfo.InvariantCulture), fields));
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

    /// <summary>The silo's clock — every grain's "now". See <see cref="MovableClock" />.</summary>
    public MovableClock Clock => (MovableClock)silo.Services.GetRequiredService<IClock>();

    /// <summary>The silo's structured log events, for the audit assertions.</summary>
    public AuditCapture Audit => silo.Services.GetRequiredService<AuditCapture>();

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

    /// <summary>A subject object's slice of the Leopard index.</summary>
    public IMembershipIndexGrain Index(Guid tenant, ObjectRef subjectObject) =>
        For(tenant).GetGrain<IMembershipIndexGrain>(GrainKeys.MembershipIndex(subjectObject.Type, subjectObject.Id));

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
            connections.Select(static x =>
                $"--{CyberCloudStorageOptions.SectionName}:Durable:Shards:{x.Key}={x.Value}"
            )
        );

        var reminders = redis.GetConnectionString();

        var builder = OrleansApplication.CreateSilo(
            [.. args],
            static cluster => cluster.ConfigureServices(static services => {
                    // Registered BEFORE AddCyberCloudAuthorization's TryAdd runs, so this wins.
                    services.AddSingleton<ArmableWriteInterceptor>();
                    services.AddSingleton<IRelationWriteInterceptor>(static sp =>
                        sp.GetRequiredService<ArmableWriteInterceptor>()
                    );

                    // The same, for the clock every expiry is compared with — issue #49.
                    services.AddSingleton<IClock, MovableClock>();

                    // The audit events are structured log events (docs/plan/11 § Auditing), so
                    // the way to assert one is to be a log sink.
                    services.AddSingleton<AuditCapture>();
                    services.AddSingleton<ILogEventSink>(static sp => sp.GetRequiredService<AuditCapture>());

                    // The tenancy refreshers are background loops this suite does not drive.
                    services.Configure<TenancyRefreshOptions>(static o => o.RunBackgroundRefresh = false);
                }
            ),
            (cluster, options) => {
                // ⚠ Redis reminders, in the same Redis as the hot tier — what SiloComposition wires,
                // and the tuple store's expiry sweep is a reminder. In-memory would pass as well and
                // would say nothing about a reminder row that has to survive the store's activation.
                cluster.UseRedisReminderService(r => r.ConfigurationOptions = ConfigurationOptions.Parse(reminders));
                cluster.AddCyberCloudTenancy(options).AddCyberCloudAuthorization();
            }
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
