using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Silo.Host.Tests;

/// <summary>
///     What a fresh cluster is given at start so the first tenant can exist — the shard map and the
///     sign-up operator's grant — asserted against the real <c>ShardMapGrain</c> and
///     <c>TupleStoreGrain</c> rather than against doubles.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real grains, because the claims are about their idempotency.</b> "A second run
///         changes nothing" is a fact about <c>ShardMapGrain.ConfigureShardsAsync</c> bumping its
///         version only when a shard is new and about a tuple write being a success when the tuple
///         exists; a double that answered success to everything would prove the task called it
///         twice and nothing else. The cluster is in-memory storage under the production grain
///         types, the same shape <c>CyberCloud.Identity.Tests</c> uses.
///     </para>
///     <para>
///         ⚠ <b>The task is driven directly, not through <c>SiloComposition</c>.</b> Starting the
///         real silo needs PostgreSQL and Redis; what is under test is what the task does with a
///         grain factory and a configuration, and both can be handed to it. That it is registered
///         as a startup task at all is <c>SiloComposition</c>'s and is read there.
///     </para>
/// </remarks>
[Collection(BootstrapSuite.Name)]
public sealed class PlatformBootstrapTaskTests(BootstrapCluster cluster) {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConfiguresTheShardMapFromTheDurableShardsMinusThePlatformShard() {
        await cluster.Task(selfServe: false).ExecuteAsync(Ct);

        var snapshot = (await cluster.ShardMap.GetSnapshotAsync(0)).GetValueOrThrow();

        // ⚠ The platform shard carries the directory and the map and nothing else; a tenant placed
        // on it would put a customer's rows beside the platform's. So it is configured as a durable
        // shard — it is one — and excluded from the map.
        snapshot.DurableShards.ShouldBe([BootstrapCluster.ShardA, BootstrapCluster.ShardB]);

        // And a tenant can now be placed, which is the whole reason the task exists.
        (await cluster.ShardMap.AssignAsync(Guid.NewGuid(), "local")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondRunChangesNothing() {
        await cluster.Task(selfServe: true).ExecuteAsync(Ct);
        var first = (await cluster.ShardMap.GetSnapshotAsync(0)).GetValueOrThrow();

        // ⚠ Both silos run the task, and a restart runs it again. The second run must find the
        // first's work done: the map's version does not move (ConfigureShardsAsync bumps it only for
        // a shard it has not seen), the shard list is the same list, and the platform's operator
        // relation still names the sign-up operator exactly once — IObjectRelationsGrain.WriteAsync
        // is idempotent, so re-writing the tuple adds nothing. (The tuple store's own version does
        // move on the re-write; that is a cache invalidation, not a change to who may do what.)
        await cluster.Task(selfServe: true).ExecuteAsync(Ct);

        var second = (await cluster.ShardMap.GetSnapshotAsync(0)).GetValueOrThrow();
        second.Version.ShouldBe(first.Version);
        second.DurableShards.ShouldBe(first.DurableShards);
        (await cluster.OperatorGrantCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task WritesTheSignUpOperatorTupleOnlyWhenSelfServeIsOn() {
        var operatorGrant = await cluster.HoldsOperatorGrantAsync();

        if (!operatorGrant) {
            // A silo with sign-up closed has no reason to hold a standing operator grant for it.
            await cluster.Task(selfServe: false).ExecuteAsync(Ct);
            (await cluster.HoldsOperatorGrantAsync()).ShouldBeFalse("sign-up is closed, so no grant");
        }

        await cluster.Task(selfServe: true).ExecuteAsync(Ct);

        (await cluster.HoldsOperatorGrantAsync())
            .ShouldBeTrue("platform:root#operator@servicePrincipal:{SignUpOperator} is what CreateTenantAsync checks");
    }

    [Fact]
    public async Task ASiloWithNoDurableShardSkipsItself() {
        // ⚠ The shape HostCompositionTests starts: no CyberCloud:Storage at all, so no grain storage
        // and no map to configure. The task must not touch a grain on such a silo — the activation
        // would fail and take the start with it.
        var task = new PlatformBootstrapTask(
            new RefusingGrainFactory(),
            new ConfigurationBuilder().Build(),
            NullLogger<PlatformBootstrapTask>.Instance
        );

        await Should.NotThrowAsync(() => task.ExecuteAsync(Ct));
    }

    [Fact]
    public void TheTenantShardsAreTheConfiguredShardsMinusTheNullTenantShard() {
        var storage = new CyberCloudStorageOptions();
        storage.Durable.Shards["durable01"] = "Host=b";
        storage.Durable.Shards["platform00"] = "Host=p";
        storage.Durable.Shards["durable00"] = "Host=a";
        storage.Durable.NullTenantShard = "platform00";

        PlatformBootstrapTask.TenantShards(storage).ShouldBe(["durable00", "durable01"]);

        // With no platform shard named, every configured shard carries tenants.
        storage.Durable.NullTenantShard = null;
        PlatformBootstrapTask.TenantShards(storage).ShouldBe(["durable00", "durable01", "platform00"]);
    }
}

/// <summary>Binds <see cref="BootstrapCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class BootstrapSuite : ICollectionFixture<BootstrapCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "bootstrap-cluster";
}

/// <summary>
///     An in-process silo with the shard map and the authorization engine on in-memory storage.
/// </summary>
public sealed class BootstrapCluster : IAsyncLifetime {
    /// <summary>The first tenant-carrying shard, as the AppHost names it.</summary>
    public const string ShardA = "durable00";

    /// <summary>The second.</summary>
    public const string ShardB = "durable01";

    /// <summary>The platform shard, which the map must not carry.</summary>
    public const string PlatformShard = "platform00";

    TestCluster cluster = null!;

    /// <summary>The real shard map.</summary>
    public IShardMapGrain ShardMap => cluster.GrainFactory.GetGrain<IShardMapGrain>(GrainKeys.ShardMap());

    /// <summary>The task, over the configuration the AppHost would give a silo.</summary>
    /// <param name="selfServe">Whether <c>CyberCloud:Identity:SelfServeSignUp</c> is on.</param>
    public PlatformBootstrapTask Task(bool selfServe) {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal) {
                    [$"{CyberCloudStorageOptions.SectionName}:Durable:Shards:{ShardA}"] = "Host=a",
                    [$"{CyberCloudStorageOptions.SectionName}:Durable:Shards:{ShardB}"] = "Host=b",
                    [$"{CyberCloudStorageOptions.SectionName}:Durable:Shards:{PlatformShard}"] = "Host=p",
                    [$"{CyberCloudStorageOptions.SectionName}:Durable:NullTenantShard"] = PlatformShard,
                    [PlatformBootstrapTask.SelfServeSignUpKey] = selfServe ? "true" : "false"
                }
            )
            .Build();

        return new(cluster.GrainFactory, configuration, NullLogger<PlatformBootstrapTask>.Instance);
    }

    /// <summary>Whether the platform tenant's store holds the sign-up operator's grant.</summary>
    public async Task<bool> HoldsOperatorGrantAsync() => await OperatorGrantCountAsync() > 0;

    /// <summary>How many times <c>platform:root#operator</c> names the sign-up operator. One, or none.</summary>
    public async Task<int> OperatorGrantCountAsync() {
        var relations = cluster.GrainFactory
            .ForTenant(Guid.Empty.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(ObjectTypes.Platform, "root"));

        var snapshot = await relations.ReadDurableAsync();
        if (snapshot.IsFailure || !snapshot.GetValueOrThrow().ByRelation.TryGetValue(Relations.Operator, out var operators)) {
            return 0;
        }

        var expected = IdentityBootstrap.SignUpOperator.ToString("N", CultureInfo.InvariantCulture);

        return operators.Count(x => string.Equals(x.Type, SubjectTypes.ServicePrincipal, StringComparison.Ordinal)
            && string.Equals(x.Id, expected, StringComparison.Ordinal)
        );
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => services.AddSingleton<IClock, SystemClock>());

            // The schema CheckGrain and TupleStoreGrain evaluate against — the same line
            // SiloComposition writes.
            silo.AddCyberCloudAuthorization();
        }
    }
}

/// <summary>An <see cref="IGrainFactory" /> that refuses to hand out a reference.</summary>
/// <remarks>
///     Reaching any member means the task touched a grain on a silo with no storage — the failure
///     that would take the silo's start with it.
/// </remarks>
sealed class RefusingGrainFactory : IGrainFactory {
    static InvalidOperationException Refuse() =>
        new("A silo with no durable shard configured has no grain storage, and the bootstrap task must not touch a grain on it.");

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidKey => throw Refuse();

    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerKey => throw Refuse();

    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey => throw Refuse();

    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidCompoundKey => throw Refuse();

    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerCompoundKey => throw Refuse();

    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw Refuse();

    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw Refuse();

    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw Refuse();

    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw Refuse();

    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw Refuse();

    public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
        where TGrainInterface : IAddressable => throw Refuse();

    public IAddressable GetGrain(GrainId grainId) => throw Refuse();

    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw Refuse();

    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw Refuse();

    public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw Refuse();

    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver => throw Refuse();

    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver => throw Refuse();
}
