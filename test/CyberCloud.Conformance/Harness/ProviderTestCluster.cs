using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Immutable;
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

    /// <summary>
    ///     The name the harness gives the ancestor at <paramref name="level" />, outermost being 0.
    /// </summary>
    /// <param name="level">The nesting level, outermost first.</param>
    /// <remarks>
    ///     ⚠ Fixed rather than random, for the reason the GUIDs above are: a failure message names the
    ///     same path every time. It is also why every test in a child's run shares <i>one</i> parent —
    ///     the suite is about the child, and a parent per test would spend a create per assertion to
    ///     prove nothing the parent's own run does not already prove.
    /// </remarks>
    public static string AncestorName(int level) =>
        "ancestor-" + level.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>
    ///     The one test vault this provider's harness mints into and reads back from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Static for the same reason <see cref="Cluster" /> is, and it is the same defect if it
    ///     is not.</b> The reconciler mints inside the SILO and a synchronous action resolves inside
    ///     the test process, so two instances would be a <c>listKeys</c> that cannot find the
    ///     credential its own create wrote — and the failure would read as a provider bug.
    /// </remarks>
    public static InMemorySecretVault Vault { get; } = new();

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

    /// <summary>The test vault a minting reconciler writes into.</summary>
    public InMemorySecretVault Vault => ConformanceState<TSource>.Vault;

    /// <summary>
    ///     A container holding every action handler the case's provider declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ Built from the registry rather than from a member on the case, because the provider
    ///     already says which handler serves which action and a second declaration on the case would
    ///     be one that can disagree with it. <c>Describe</c> is pure — <see cref="IResourceProvider" />
    ///     requires it — so building the registry twice is the same arrangement
    ///     <c>AddCyberCloudProvider</c> relies on.
    /// </remarks>
    ServiceProvider Handlers() {
        var services = new ServiceCollection();

        foreach (var handler in Registry.Types
            .SelectMany(x => x.Actions)
            .Select(x => x.HandlerType)
            .OfType<Type>()
            .Distinct()) {
            services.AddSingleton(handler);
        }

        return services.BuildServiceProvider();
    }

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

    /// <summary>
    ///     The ancestor cases, checked against the depth the type under test declares.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The source describes a different number of ancestors than its type nests, or one of them is
    ///     not the type's own ancestor.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>This is what makes <see cref="IProviderCaseSource.Ancestors" />' default safe, and it
    ///     is deliberately the FIRST thing anything touching an address goes through.</b> Without it a
    ///     depth-2 source that left the member empty fails inside <c>ResourceId</c>'s constructor —
    ///     an <c>ArgumentException</c> about parent-name counts, raised from a static helper, on
    ///     <i>every</i> test in the class at once, naming neither the case nor the member that is
    ///     missing. That is the failure the whole suite was unrunnable behind; the message below is
    ///     the difference between a provider author reading it once and bisecting for an afternoon.
    /// </remarks>
    public static ImmutableArray<ProviderConformanceCase> Ancestors {
        get {
            var declared = TSource.Ancestors;
            var expected = Case.Type.Depth - 1;

            if (declared.Length != expected) {
                throw new InvalidOperationException(
                    $"'{Case.DisplayName}' is registered for '{Case.Type}', which nests "
                    + (expected + 1).ToString(CultureInfo.InvariantCulture)
                    + " level(s) deep, so the suite must create "
                    + expected.ToString(CultureInfo.InvariantCulture)
                    + " ancestor(s) before it can address one — a child cannot be created until its "
                    + "parent exists, and the create answers 404 when it does not. "
                    + typeof(TSource).Name
                    + ".Ancestors describes "
                    + declared.Length.ToString(CultureInfo.InvariantCulture)
                    + ". Set it to the parent type's own ProviderConformanceCase, outermost first — "
                    + "see IProviderCaseSource.Ancestors."
                );
            }

            for (var i = 0; i < declared.Length; i++) {
                var ancestor = declared[i];
                var expectedType = AncestorTypeAt(Case.Type, i);

                if (ancestor.Type != expectedType) {
                    throw new InvalidOperationException(
                        $"'{Case.DisplayName}' declares '{ancestor.Type}' as ancestor "
                        + i.ToString(CultureInfo.InvariantCulture)
                        + $" of '{Case.Type}', and that ancestor is '{expectedType}'. The suite "
                        + "registers ONE provider — a nested type and its parent are the same "
                        + "provider by construction — so a case naming somebody else's type would "
                        + "create a resource this run's registry cannot address."
                    );
                }
            }

            return declared;
        }
    }

    /// <summary>The <c>/</c>-separated ancestor names an address for the type under test carries.</summary>
    public static string AncestorPath =>
        string.Join('/', Ancestors.Select((_, level) => ConformanceIds.AncestorName(level)));

    /// <summary>Builds an address for the type under test.</summary>
    /// <param name="name">The resource name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="ConformanceIds.Tenant" />.</param>
    /// <param name="subscription">The subscription, defaulting to <see cref="ConformanceIds.Subscription" />.</param>
    /// <remarks>
    ///     ⚠ <b>The ancestor names come from the harness, not from the case, and that is why a child's
    ///     run is the same suite rather than a copy of it.</b> Every assertion in
    ///     <c>ProviderConformanceTests</c> addresses through this one method, so making it interleave
    ///     the ancestors it created is the whole of what a depth-2 type needed: the 27 assertions run
    ///     unchanged, against <c>…/probes/ancestor-0/samples/{name}</c> instead of
    ///     <c>…/probes/{name}</c>.
    /// </remarks>
    public static ResourceId Address(string name, Guid? tenant = null, Guid? subscription = null) =>
        new(
            tenant ?? ConformanceIds.Tenant,
            subscription ?? ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Case.Type,
            name,
            Guid.Empty,
            AncestorPath
        );

    /// <summary>The type of the ancestor at <paramref name="level" />, outermost being 0.</summary>
    /// <param name="type">The nested type.</param>
    /// <param name="level">The nesting level.</param>
    static ResourceTypeName AncestorTypeAt(ResourceTypeName type, int level) =>
        new(type.Namespace, string.Join('/', type.Type.Split('/').Take(level + 1)));

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
            // ⚠ THE ACTION PATH, OVER THE SAME FAKE CLUSTER AND THE SAME TEST VAULT THE SILO USES.
            // A synchronous action runs inside ResourceManagerService rather than on a silo, so this
            // instance — not the one in the silo's container — is what serves the suite's POST. Both
            // read Vault, so a credential the reconciler minted inside the silo is the one listKeys
            // hands back out here.
            new ActionDispatcher(
                Handlers(),
                new FakeClusterConnectionFactory(World),
                Vault
            ),
            NullLogger<ResourceManagerService>.Instance
        );

        // ⚠ AND THE ANCESTORS, WHICH IS THE OTHER THING A DEPTH-2 CASE CANNOT RUN WITHOUT. The create
        // path resolves the parent's index binding and refuses with the same 404 as "no such
        // resource" when it is absent, so a child's every assertion would fail as a 404 that named
        // the child's own path. Created through the Manager rather than by writing an index entry:
        // the parent is a real resource, and a harness that faked one would be asserting against a
        // binding the platform did not make.
        await CreateAncestorsAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);

        // ⚠ IN THE OTHER TENANT TOO, AND THAT ONE IS NOT SYMMETRY FOR ITS OWN SAKE.
        // CreatingWithAnotherTenantsIdsIs404AndNothingIsApplied writes at the other tenant's address
        // and asserts a 404. Without a parent over there the answer would still be 404 — from the
        // parent check, before the caller's tenant is ever compared — so the test would pass while
        // testing nothing. This is what keeps the assertion about the tenant boundary.
        await CreateAncestorsAsync(ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription);
    }

    /// <summary>Creates the ancestors the type under test hangs off, outermost first.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <remarks>
    ///     Each is driven to a terminal state before the next is written, because the next one's own
    ///     create reads its parent's <b>confirmed</b> binding — a name under an unexpired two-phase
    ///     claim resolves as absent, which is <c>IResourceIndexGrain.ResolveAsync</c> working.
    /// </remarks>
    async Task CreateAncestorsAsync(Guid tenant, Guid subscription) {
        for (var level = 0; level < Ancestors.Length; level++) {
            var ancestor = Ancestors[level];

            var address = new ResourceId(
                tenant,
                subscription,
                ConformanceIds.ResourceGroup,
                ancestor.Type,
                ConformanceIds.AncestorName(level),
                Guid.Empty,
                string.Join('/', Enumerable.Range(0, level).Select(ConformanceIds.AncestorName))
            );

            var accepted = await Manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = ancestor.ApiVersion,
                    Verb = WriteVerb.Put,
                    Body = ancestor.Body(ConformanceIds.Cluster),
                    Caller = Caller(tenant)
                },
                CancellationToken.None
            );

            accepted.IsSuccess.ShouldBeTrue(
                $"the harness could not create '{address.Path}', which is the parent every assertion "
                + $"about '{Case.Type}' hangs off: {accepted.Error?.Message}"
            );

            var operation = Operation(tenant, accepted.GetValueOrThrow().OperationId);

            for (var drive = 0; drive < 8; drive++) {
                var status = await operation.DriveAsync();
                if (status.GetValueOrThrow().IsTerminal) {
                    break;
                }
            }

            var bound = await Index(address).GetAsync();

            bound.GetValueOrThrow().State.ShouldBe(
                IndexEntryState.Confirmed,
                $"'{address.Path}' did not reach a confirmed binding, so every create under it will "
                + "answer the parent-not-found 404 and the failure will name the CHILD's path"
            );
        }
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

                    // ⚠ BOTH SEAMS FROM ONE OBJECT, AND THE SUITE WOULD BE VACUOUS WITHOUT THEM.
                    // A provider whose reconciler mints a credential — CyberCloud.Storage/accounts is
                    // the first — cannot converge against UnavailableSecretWriter, so every
                    // convergence assertion would fail for a wiring reason. InMemorySecretVault
                    // implements mint-once for real, which is what keeps the idempotence assertion
                    // from measuring itself.
                    services.AddSingleton<ISecretResolver>(ConformanceState<TSource>.Vault);
                    services.AddSingleton<ISecretWriter>(ConformanceState<TSource>.Vault);

                    // The provider, exactly as AddCyberCloudProvider<T> registers one: the provider
                    // itself, and the reconciler as a SINGLETON BY CONCRETE TYPE — clause 2 makes one
                    // instance per process correct, and the registry stores the concrete type because
                    // that is what ReconcileDriver resolves.
                    services.AddSingleton<IResourceProvider>(_ => TSource.ProviderCase.CreateProvider());
                    services.AddSingleton(TSource.ProviderCase.ReconcilerType);

                    // ⚠ AND EVERY ANCESTOR'S RECONCILER, because the harness creates the ancestors
                    // and ReconcileDriver resolves each type's reconciler FROM THIS CONTAINER by the
                    // concrete type the registry stores. One provider declares both a child and its
                    // parent, so registering only the case's own reconciler leaves the parent's
                    // create failing inside the silo — as a resolution error nothing on the request
                    // path can attribute to the harness.
                    foreach (var reconciler in TSource.Ancestors
                        .Select(x => x.ReconcilerType)
                        .Where(x => x != TSource.ProviderCase.ReconcilerType)
                        .Distinct()) {
                        services.AddSingleton(reconciler);
                    }

                    // ⚠ AND EVERY ACTION HANDLER, for the reason the reconcilers are here: the
                    // registry stores a concrete Type and ActionDispatcher resolves it from a
                    // container. A LongRunning action driven inside the silo would otherwise refuse
                    // with a message about the container, naming the harness rather than the case.
                    foreach (var handler in ProviderRegistry.Build([TSource.ProviderCase.CreateProvider()])
                        .Types
                        .SelectMany(x => x.Actions)
                        .Select(x => x.HandlerType)
                        .OfType<Type>()
                        .Distinct()) {
                        services.AddSingleton(handler);
                    }

                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudResourceManager();
        }
    }
}
