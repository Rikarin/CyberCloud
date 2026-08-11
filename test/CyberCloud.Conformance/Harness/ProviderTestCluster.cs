using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.Conformance;

/// <summary>The identities every conformance run uses.</summary>
/// <remarks>
///     Fixed rather than random so a failure message names the same GUIDs every time, and so the
///     cross-tenant cases read as two named tenants rather than as two opaque values.
/// </remarks>
public static class ConformanceIds {
    /// <summary>The tenant the provider under test lives in.</summary>
    public static Guid Tenant { get; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary>Somebody else's tenant. Nothing in a run may reach across this line.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>The subscription resources are created in.</summary>
    public static Guid Subscription { get; } = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    /// <summary>The other tenant's subscription.</summary>
    public static Guid OtherSubscription { get; } = Guid.Parse("dddddddd-0000-4000-8000-000000000004");

    /// <summary>The cluster the harness's fake API server answers for.</summary>
    public static Guid Cluster { get; } = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    /// <summary>The resource group everything lands in.</summary>
    public const string ResourceGroup = "prod";
}

/// <summary>
///     The mutable pieces one provider's harness owns, bound to a type rather than to a global.
/// </summary>
/// <typeparam name="TSource">The case source. One set of state per provider, for free.</typeparam>
/// <remarks>
///     ⚠ <b>Static, and it has to be.</b> The reconciler and the reconcile driver run inside the silo,
///     which resolves its own services from a container the test never touches; a test asserting "the
///     ConfigMap is gone" has to read the same cluster the reconciler wrote into. Keying the statics on
///     <typeparamref name="TSource" /> is what keeps that from being a shared global: two providers'
///     harnesses get two independent sets with no lock and no reset ordering between them.
/// </remarks>
public static class ConformanceState<TSource>
    where TSource : IProviderCaseSource {
    /// <summary>The one fake cluster this provider's harness applies into.</summary>
    public static FakeKubeCluster Cluster { get; } = new(ConformanceIds.Cluster);

    /// <summary>The clock the silo reads.</summary>
    public static ConformanceClock Clock { get; } = new();

    /// <summary>The enforcement-seam double.</summary>
    public static PermissiveAuthorizer Authorizer { get; } = new();

    /// <summary>The lock resolver.</summary>
    public static SettableLockResolver Locks { get; } = new();

    /// <summary>Step 11's recorded events.</summary>
    public static RecordingChanges Changes { get; } = new();

    /// <summary>Step 8's recorded ReBAC parent edges.</summary>
    public static RecordingRelationWriter Relations { get; } = new();

    /// <summary>Puts every piece back to its default.</summary>
    public static void Reset() {
        Cluster.Reset();
        Clock.Reset();
        Authorizer.Reset();
        Locks.Reset();
        Changes.Reset();
        Relations.Reset();
    }
}

/// <summary>
///     An in-process Orleans cluster with one provider wired exactly as a silo wires it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>In-memory grain storage and in-memory reminders, which is a deviation from ADR-018 and
///         is owed back.</b> The same deviation, for the same reason and with the same cost, as
///         <c>CyberCloud.ResourceManager.Tests</c>: no container could be started in the environment
///         this was written in. What it costs here specifically:
///     </para>
///     <list type="bullet">
///         <item>Nothing proves the grain state <b>serializes</b> — in-memory storage keeps the object graph.</item>
///         <item>
///             Nothing proves the durable tier survives a <b>silo</b> restart. Resumability is exercised
///             by deactivating a grain, which re-drives against the same in-process store.
///         </item>
///         <item>The API server is <see cref="FakeKubeCluster" />, which is a dictionary. See its remarks.</item>
///     </list>
///     <para>
///         Everything above that line — the twelve steps, the operation lifecycle, the verb grammar,
///         the reconciler's four clauses, drift correction, the labels on rendered output — is
///         behaviour of our own code and is fully exercised.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
public class ProviderTestCluster<TSource> : IAsyncLifetime
    where TSource : IProviderCaseSource {
    TestCluster cluster = null!;

    /// <summary>The provider under test.</summary>
    public static ProviderConformanceCase Case => TSource.ProviderCase;

    /// <summary>The write path, built on the <b>client</b> side, which is where the gateway holds it.</summary>
    /// <remarks>
    ///     ⚠ Built here rather than resolved from the silo, and that is the faithful shape.
    ///     <c>IResourceManager</c> is a service the gateway holds, and docs/plan/03 and docs/plan/10
    ///     make the gateway an Orleans <i>client</i> — so its grain factory is
    ///     <c>TestCluster.GrainFactory</c>. The silo still runs
    ///     <c>AddCyberCloudResourceManager</c>, because <c>OperationGrain</c> resolves
    ///     <c>ReconcileDriver</c> from the silo's container, so both halves are exercised on the side
    ///     each really lives on.
    /// </remarks>
    public IResourceManager Manager { get; private set; } = null!;

    /// <summary>The registry the write path validates against.</summary>
    public IProviderRegistry Registry { get; private set; } = null!;

    /// <summary>The fake API server the reconciler applies into.</summary>
    public FakeKubeCluster World => ConformanceState<TSource>.Cluster;

    /// <summary>The shared clock.</summary>
    public ConformanceClock Clock => ConformanceState<TSource>.Clock;

    /// <summary>The enforcement-seam double.</summary>
    public PermissiveAuthorizer Authorizer => ConformanceState<TSource>.Authorizer;

    /// <summary>The lock resolver.</summary>
    public SettableLockResolver Locks => ConformanceState<TSource>.Locks;

    /// <summary>Step 11's recorded events.</summary>
    public RecordingChanges Changes => ConformanceState<TSource>.Changes;

    /// <summary>
    ///     Step 8's recorded ReBAC parent edges — what a delete must leave empty.
    /// </summary>
    public RecordingRelationWriter Relations => ConformanceState<TSource>.Relations;

    /// <summary>The cluster the harness answers for.</summary>
    public static Guid ClusterId => ConformanceIds.Cluster;

    /// <summary>The client's grain factory. ⚠ Tenant-unaware, as a gateway's is.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>Puts every double back and empties the fake cluster.</summary>
    public static void Reset() => ConformanceState<TSource>.Reset();

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The resource grain.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    public IResourceGrain Resource(Guid tenant, Guid resourceId) =>
        For(tenant).GetGrain<IResourceGrain>(GrainKeys.Resource(resourceId));

    /// <summary>The operation grain.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="operationId">The operation.</param>
    public IOperationGrain Operation(Guid tenant, Guid operationId) =>
        For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

    /// <summary>The path index step 7 claims in.</summary>
    /// <param name="address">The resource's address.</param>
    public IResourceIndexGrain Index(ResourceId address) =>
        For(address.TenantId).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address));

    /// <summary>Builds an address for the type under test.</summary>
    /// <param name="name">The resource name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="ConformanceIds.Tenant" />.</param>
    /// <param name="subscription">The subscription, defaulting to <see cref="ConformanceIds.Subscription" />.</param>
    public static ResourceId Address(string name, Guid? tenant = null, Guid? subscription = null) =>
        new(
            tenant ?? ConformanceIds.Tenant,
            subscription ?? ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Case.Type,
            name,
            Guid.Empty
        );

    /// <summary>A caller.</summary>
    /// <param name="tenant">The tenant the request is for.</param>
    /// <param name="subject">The subject id.</param>
    public static CallerContext Caller(Guid? tenant = null, string subject = "alice") =>
        new() {
            TenantId = tenant ?? ConformanceIds.Tenant,
            SubjectType = "user",
            SubjectId = subject,
            CorrelationId = "conformance"
        };

    /// <summary>
    ///     Creates a subscription and its resource group, so step 1 of the write path can find them.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    async Task CreateSubscriptionAsync(Guid tenant, Guid subscription) {
        var created = await For(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
            .CreateAsync("conformance");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        // The group carries the lock the resolver walks. Created here so a run that sets one has
        // something to set it on.
        var group = await For(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, ConformanceIds.ResourceGroup))
            .CreateAsync(tenant, "eu-west-1");

        group.IsSuccess.ShouldBeTrue(group.Error?.Message);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        Registry = ProviderRegistry.Build([Case.CreateProvider()]);

        // ⚠ The subscriptions are created before anything is written into them. Step 1 of the write
        // path now reads ISubscriptionGrain and answers 404 for a subscription that does not exist,
        // so a harness that skipped this would fail every create with "does not exist" and the reason
        // would be the harness rather than the provider.
        await CreateSubscriptionAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);
        await CreateSubscriptionAsync(ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription);

        Manager = new ResourceManagerService(
            Registry,
            Authorizer,
            Relations,
            Locks,
            new NotSupportedPolicyEvaluator(),
            Changes,
            cluster.GrainFactory,
            NullLogger<ResourceManagerService>.Instance
        );
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     The silo, wired as <c>CyberCloud.Silo.Host</c> wires one, plus the harness's doubles.
    /// </summary>
    /// <remarks>
    ///     ⚠ The doubles go in <b>first</b> so the production wiring's <c>TryAdd</c> keeps them, which
    ///     is the same contract a real host relies on to swap an implementation in.
    /// </remarks>
    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(ConformanceState<TSource>.Clock);
                    services.AddSingleton<IResourceAuthorizer>(ConformanceState<TSource>.Authorizer);
                    services.AddSingleton<ILockResolver>(ConformanceState<TSource>.Locks);
                    services.AddSingleton<IResourceChangedSink>(ConformanceState<TSource>.Changes);
                    services.AddSingleton<IResourceRelationWriter>(ConformanceState<TSource>.Relations);
                    services.AddSingleton<IClusterConnectionFactory>(
                        new FakeClusterConnectionFactory(ConformanceState<TSource>.Cluster)
                    );

                    // The provider, exactly as AddCyberCloudProvider<T> registers one: the provider
                    // itself, and the reconciler as a SINGLETON BY CONCRETE TYPE — clause 2 makes one
                    // instance per process correct, and the registry stores the concrete type because
                    // that is what ReconcileDriver resolves.
                    services.AddSingleton<IResourceProvider>(_ => TSource.ProviderCase.CreateProvider());
                    services.AddSingleton(TSource.ProviderCase.ReconcilerType);

                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudResourceManager();
        }
    }
}
