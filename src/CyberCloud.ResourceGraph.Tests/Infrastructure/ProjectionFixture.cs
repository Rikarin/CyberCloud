using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.ResourceGraph.Tests.Infrastructure;

/// <summary>
///     A real NATS with JetStream, a real ClickHouse, and one TestCluster silo running the projector
///     against real authorization grains — the shape a silo host has, collapsed for a test.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The projector runs INSIDE the silo, as the hosted service the host registers, and
///         not as an object the test drives.</b> <c>AddResourceGraphProjector</c> is the same call
///         <c>SiloComposition</c> makes; what the test process holds is the publisher's end (a
///         <see cref="NatsResourceChangedSink" />, which is what the gateway holds) and a reader over
///         ClickHouse. A message therefore travels sink → JetStream → the silo's consumer → the
///         silo's <c>ICheckGrain</c> → ClickHouse, and the test sees only what a portal would.
///     </para>
///     <para>
///         <c>nats:2.14</c> with <c>-js</c>, because JetStream is off by default and a stream
///         declaration against a plain NATS answers <c>no responders</c>, which reads like a
///         connection failure. <c>clickhouse/clickhouse-server:25.3-alpine</c>, an LTS, with a named
///         user so the credential headers are exercised rather than waved through.
///     </para>
///     <para>
///         ⚠ <b>The options reach the silo through a static, because <see cref="ISiloConfigurator" />
///         is instantiated by type.</b> The same shape <c>ResourceManagerCluster</c>'s doubles use.
///         They are written before <c>DeployAsync</c> and read once, in <c>Configure</c>.
///     </para>
/// </remarks>
public sealed class ProjectionFixture : IAsyncLifetime {
    public const string NatsImage = "nats:2.14";
    public const string ClickHouseImage = "clickhouse/clickhouse-server:25.3-alpine";
    const string ClickHouseUser = "cybercloud";
    const string ClickHousePassword = "cyber-cloud-test-password";

    /// <summary>The tenant every test writes into.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>The subscription.</summary>
    public static Guid Subscription { get; } = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>What the silo configurator reads. Set before deploy.</summary>
    internal static ResourceGraphOptions? SiloOptions { get; private set; }

    readonly IContainer nats = new ContainerBuilder(NatsImage)
        .WithPortBinding(4222, true)
        .WithCommand("--jetstream", "--store_dir", "/data")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
        .Build();

    /// <summary>
    ///     The server's time zone, deliberately not UTC. ⚠ ClickHouse parses a zoneless
    ///     <c>DateTime64(3)</c> parameter in the server's zone, and the first cut of the translator
    ///     bound its datetimes that way; every suite passed because the container ran in UTC, and
    ///     the two-hour error was found by a review (#54). Prague is two hours off UTC in September,
    ///     so a comparison that ignores the column's zone lands a row two hours away.
    /// </summary>
    public const string ClickHouseTimeZone = "Europe/Prague";

    readonly IContainer clickHouse = new ContainerBuilder(ClickHouseImage)
        .WithPortBinding(8123, true)
        .WithEnvironment("CLICKHOUSE_USER", ClickHouseUser)
        .WithEnvironment("CLICKHOUSE_PASSWORD", ClickHousePassword)
        .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
        .WithEnvironment("TZ", ClickHouseTimeZone)
        // ⚠ /ping, and not a query. The server reads CLICKHOUSE_USER from its environment before
        // it listens, so a 200 from /ping is a server whose user exists — and a path with a query
        // string in it does not work here: the wait strategy builds its URI from a path, so the
        // `?` is escaped and the server answers 404 forever. The first version of this fixture
        // waited on `/?query=SELECT 1` and never came back.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(x => x.ForPort(8123).ForPath("/ping")))
        .Build();

    TestCluster cluster = null!;
    NatsResourceChangedSink sink = null!;
    ClickHouseResourceGraphStore reader = null!;
    ClickHouseClient clickHouseClient = null!;

    /// <summary>The bound section both ends share.</summary>
    public ResourceGraphOptions Options { get; private set; } = null!;

    /// <summary>The publisher's end — what the gateway holds.</summary>
    public IResourceChangedSink Sink => sink;

    /// <summary>A reader over the same ClickHouse, in the test's process.</summary>
    public ClickHouseResourceGraphStore Reader => reader;

    /// <summary>The same ClickHouse, as the query API speaks to it — the client the gateway would hold.</summary>
    public ClickHouseClient ClickHouse => clickHouseClient;

    /// <summary>The cluster's grain factory, tenant not yet applied — what the gateway's query service is handed.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>The silo's grain factory, tenant applied.</summary>
    public TenantGrainFactory For(Guid tenant) => cluster.GrainFactory.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The silo's own projector, for the tests that drive one event without the stream.</summary>
    public ResourceGraphProjector SiloProjector =>
        ((InProcessSiloHandle)cluster.Primary).SiloHost.Services.GetRequiredService<ResourceGraphProjector>();

    /// <summary>A JetStream context on the test's own connection, for looking at the stream.</summary>
    public NatsJSContext JetStream { get; private set; } = null!;

    NatsConnection connection = null!;

    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        await Task.WhenAll(nats.StartAsync(token), clickHouse.StartAsync(token));

        Options = new() {
            NatsUrl = $"nats://{nats.Hostname}:{nats.GetMappedPublicPort(4222)}",
            ClickHouseEndpoint = $"http://{clickHouse.Hostname}:{clickHouse.GetMappedPublicPort(8123)}",
            ClickHouseUser = ClickHouseUser,
            ClickHousePassword = ClickHousePassword,
            AllowInsecureTransport = true,
            RequestTimeout = TimeSpan.FromSeconds(30)
        };

        SiloOptions = Options;

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        sink = new(Options, NullLogger<NatsResourceChangedSink>.Instance);
        clickHouseClient = new(new HttpClient { Timeout = Options.RequestTimeout }, Options);
        reader = new(clickHouseClient);

        connection = ResourceChangedLog.Connect(Options, "cybercloud-resource-graph-tests");
        JetStream = new(connection);
    }

    public async ValueTask DisposeAsync() {
        if (sink is not null) {
            await sink.DisposeAsync();
        }

        if (connection is not null) {
            await connection.DisposeAsync();
        }

        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }

        await Task.WhenAll(nats.DisposeAsync().AsTask(), clickHouse.DisposeAsync().AsTask());
    }

    /// <summary>Writes one tuple through the tenant's tuple store, the way the write path does.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tuple">The tuple, spelled <c>type:id#relation@subject</c>.</param>
    public async Task GrantAsync(Guid tenant, string tuple) {
        var store = For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant));
        var written = await store.WriteAsync(RelationTuple.Parse(tuple).GetValueOrThrow());
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    /// <summary>Deletes one tuple, the way a park's reparent and assignment drop do.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="tuple">The tuple, spelled <c>type:id#relation@subject</c>.</param>
    public async Task RevokeAsync(Guid tenant, string tuple) {
        var store = For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant));
        var deleted = await store.DeleteAsync(RelationTuple.Parse(tuple).GetValueOrThrow());
        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);
    }

    /// <summary>Polls the projection until the resource's row is at the version, or fails after a bound.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="resourceId">The resource.</param>
    /// <param name="version">The version to wait for.</param>
    public async Task<ResourceGraphRow> WaitForVersionAsync(Guid tenant, Guid resourceId, long version) {
        var token = TestContext.Current.CancellationToken;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        ResourceGraphRow? last = null;

        while (DateTimeOffset.UtcNow < deadline) {
            var read = await reader.ReadAsync(tenant, resourceId, token);
            read.IsSuccess.ShouldBeTrue(read.Error?.Message);
            last = read.GetValueOrThrow().Row;

            if (last is { } row && row.Version >= version) {
                return row;
            }

            await Task.Delay(200, token);
        }

        throw new TimeoutException(
            $"The projection did not reach version {version} for {resourceId:N} within 30s; the last row seen was "
            + (last is null ? "none" : $"version {last.Version} ({last.Change})")
        );
    }

    /// <summary>The event a test starts from: a Created at version 1 for a fresh resource.</summary>
    /// <param name="resourceId">The resource.</param>
    /// <param name="name">Its name.</param>
    public static ResourceChangedEvent Created(Guid resourceId, string name) =>
        new() {
            Change = ResourceChangeKind.Created,
            ResourceId = resourceId,
            TenantId = Tenant,
            SubscriptionId = Subscription,
            ResourceGroup = "prod",
            Provider = "CyberCloud.Testing",
            Type = "widgets",
            Name = name,
            ApiVersion = "2026-08-01",
            ProvisioningState = ProvisioningState.Creating,
            Location = "eu-central",
            Tags = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("env", "test"),
            CreatedAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            DesiredHash = "sha256:0",
            Version = 1
        };

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            // The schema CheckGrain and TupleStoreGrain evaluate against — the same line
            // SiloComposition writes — and then the same projector call it makes.
            silo.AddCyberCloudAuthorization();
            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock, SystemClock>();
                    services.AddResourceGraphProjector(
                        SiloOptions ?? throw new InvalidOperationException("ProjectionFixture.SiloOptions is set before the cluster deploys.")
                    );
                }
            );
        }
    }
}

/// <summary>One collection, so the two containers and the silo start once.</summary>
[CollectionDefinition(Name)]
public sealed class ProjectionSuite : ICollectionFixture<ProjectionFixture> {
    public const string Name = "resource-graph projection";
}
