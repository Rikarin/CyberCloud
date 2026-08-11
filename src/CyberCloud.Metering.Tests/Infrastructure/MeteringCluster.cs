using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Metering.Sinks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Metering.Tests.Infrastructure;

/// <summary>
///     The clock the silo reads, shared with the test so it can be advanced.
/// </summary>
/// <remarks>
///     ⚠ Static for the same reason <c>CyberCloud.ResourceManager.Tests</c>' is: the silo runs in
///     this process but resolves its own services, and every property under test here is caused by
///     <i>time passing</i> — a window closing, an hour elapsing, a dedup horizon expiring. There is
///     nothing to call to make that happen, and the alternative is a suite nobody runs. Advancing is
///     monotonic and forward only.
/// </remarks>
public sealed class TestClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static TestClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = Start;

    /// <summary>
    ///     ⚠ Deliberately <b>not</b> on a five-minute boundary. A start time that already sat on the
    ///     grid would make <c>UsageWindow.SampleAt</c> look like an identity function and would hide
    ///     the snap — which is the single arithmetic fact the "a sampler that runs twice produces one
    ///     record" property rests on.
    /// </summary>
    public static DateTimeOffset Start { get; } = new(2026, 8, 11, 12, 03, 17, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Puts time back to the start of a test.</summary>
    public void Reset() => UtcNow = Start;
}

/// <summary>
///     An <see cref="IMeteredResourceSource" /> a test drives — the resource graph, as a dictionary.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This stands in for the resource-graph projection of docs/plan/08 § The resource-graph
///         projection, and it is the <i>right</i> kind of stand-in.</b> What matters about the real
///         source is that it reports what <b>exists</b> — the quantities the write path reserved and
///         committed — rather than what is running. A double that returns a fixed list has exactly
///         that property, which is why the "stopped resource is still metered" test can be written
///         against it and mean something: the test sets <see cref="MeteredResource.RunState" /> to
///         every value there is and asserts the output does not move.
///     </para>
///     <para>
///         Static for the same reason <see cref="TestClock" /> is — the silo resolves its own
///         services and the test needs to reach the same instance.
///     </para>
/// </remarks>
public sealed class FakeResourceSource : IMeteredResourceSource {
    /// <summary>What every subscription holds, by subscription id.</summary>
    public static Dictionary<Guid, ImmutableArray<MeteredResource>> Holdings { get; } = [];

    /// <summary>When set, every read fails — so the "a failure is not an empty pass" rule can be checked.</summary>
    public static bool Fail { get; set; }

    /// <summary>How many times the sampler asked.</summary>
    public static int Reads { get; private set; }

    /// <summary>Empties every subscription and stops failing.</summary>
    public static void Reset() {
        Holdings.Clear();
        Fail = false;
        Reads = 0;
    }

    /// <summary>Puts a subscription's holdings in place.</summary>
    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="resources">What it holds.</param>
    public static void Hold(Guid subscriptionId, params MeteredResource[] resources) =>
        Holdings[subscriptionId] = [.. resources];

    /// <inheritdoc />
    public Task<Result<ImmutableArray<MeteredResource>>> ListAsync(
        Guid tenantId,
        Guid subscriptionId,
        CancellationToken cancellationToken = default
    ) {
        Reads++;

        if (Fail) {
            return Task.FromResult(
                Result<ImmutableArray<MeteredResource>>.Failure(
                    ErrorCode.InternalError,
                    "the resource-graph projection is down"
                )
            );
        }

        return Task.FromResult(
            Result<ImmutableArray<MeteredResource>>.Success(
                Holdings.TryGetValue(subscriptionId, out var held) ? held : []
            )
        );
    }
}

/// <summary>The <see cref="InMemoryUsageSink" /> the silo resolves, reachable from a test.</summary>
public static class TestSink {
    /// <summary>The one instance.</summary>
    public static InMemoryUsageSink Instance { get; } = new();
}

/// <summary>
///     An in-process Orleans cluster with metering wired as production wires it.
/// </summary>
/// <remarks>
///     ⚠ <b>In-memory storage and in-memory reminders, which is a deviation from ADR-018.</b> See
///     this project's <c>.csproj</c> for exactly what that costs and what is owed. Everything else —
///     the three grains, the call filter, the emitter, the catalogue — is the production wiring, and
///     <c>IQuotaGrain</c> is the real one from <c>CyberCloud.Tenancy</c>.
/// </remarks>
public sealed class MeteringCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every test meters into.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for the cross-tenant refusal.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The subscription every test meters into.</summary>
    public static Guid Subscription { get; } = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client, in the filter's terms.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>The emitter, constructed on the <b>client</b> side, as a gateway or a provider host holds it.</summary>
    public IUsageEmitter Emitter { get; private set; } = null!;

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The sampler for a subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    public IUsageSamplerGrain Sampler(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IUsageSamplerGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The rollup for a subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    public IUsageRollupGrain Rollup(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IUsageRollupGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The ledger for a subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    public IUsageLedgerGrain Ledger(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The <b>real</b> quota grain — docs/plan/06 § Quota's, from CyberCloud.Tenancy.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    public IQuotaGrain Quota(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

    /// <summary>Puts every double back to its default. Called at the top of every test.</summary>
    public static void ResetDoubles() {
        FakeResourceSource.Reset();
        TestSink.Instance.Reset();
        TestClock.Instance.Reset();
    }

    /// <summary>
    ///     A resource that holds one quota family — the shape almost every test needs.
    /// </summary>
    /// <param name="id">The resource's GUID.</param>
    /// <param name="family">The family it holds.</param>
    /// <param name="amount">How much.</param>
    /// <param name="runState">
    ///     ⚠ What the platform observed it doing. Defaults to <see cref="ObservedRunState.Running" />
    ///     so a test that flips it to <see cref="ObservedRunState.Stopped" /> is visibly changing
    ///     something — and is asserting that the change makes no difference.
    /// </param>
    /// <param name="name">The resource's name, for the path.</param>
    public static MeteredResource Resource(
        Guid id,
        QuotaMeter family,
        decimal amount,
        ObservedRunState runState = ObservedRunState.Running,
        string name = "widget"
    ) =>
        new() {
            ResourceId = id,
            ResourcePath =
                $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod"
                + $"/providers/CyberCloud.Sample/widgets/{name}",
            ResourceType = "CyberCloud.Sample/widgets",
            Region = "eu-central",
            Quantities = [new() { Family = family, Amount = amount }],
            RunState = runState
        };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        // ⚠ Built on the CLIENT side, which is the faithful shape. IUsageEmitter is held by a
        // provider host or the sampler's own silo; the client half is what a non-grain caller looks
        // like, and it is the half CC1006 is about — GrainUsageEmitter must qualify with ForTenant
        // because Orleans.Multitenant's call filter never sees a caller that is not a grain.
        Emitter = new GrainUsageEmitter(cluster.GrainFactory);
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

            // ⚠ Both metering grains are IRemindable and RegisterOrUpdateReminder throws without a
            // reminder service. In-memory rather than Redis (docs/plan/04 § Reminders) — see the
            // .csproj on what that does and does not prove.
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    // The doubles go in FIRST so the production wiring's TryAdd keeps them.
                    services.AddSingleton<IClock>(TestClock.Instance);
                    services.AddSingleton<IUsageSink>(TestSink.Instance);
                    services.AddSingleton<IMeteredResourceSource, FakeResourceSource>();
                }
            );

            silo.AddCyberCloudMetering();
        }
    }
}

/// <summary>
///     One cluster for the whole assembly. Deploying a silo per class costs seconds each and none of
///     these tests share state that survives <see cref="MeteringCluster.ResetDoubles" /> — grains are
///     kept apart by using a fresh subscription GUID per test where it matters.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MeteringClusterFixture : ICollectionFixture<MeteringCluster> {
    /// <summary>The collection's name.</summary>
    public const string Name = "metering";
}
