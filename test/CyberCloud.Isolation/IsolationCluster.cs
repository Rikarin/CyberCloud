using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Providers.Sample;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>One provider to attack, and the least this suite needs to know to attack it.</summary>
/// <param name="Name">What to call it in a test name.</param>
/// <param name="Type">The resource type.</param>
/// <param name="ApiVersion">An api-version it serves.</param>
/// <param name="Body">A body it accepts, for the harness's cluster.</param>
/// <param name="Action">An action it declares.</param>
/// <remarks>
///     ⚠ <b>Deliberately not <c>ProviderConformanceCase</c>.</b> An attacker's suite that reused the
///     provider's own conformance fixture would inherit whatever that fixture assumes, and the point
///     of docs/plan/03 § test/'s separate project is that this one assumes nothing. Everything here is
///     built from the provider's public contracts, the way an outside caller would build it.
/// </remarks>
public sealed record IsolationTarget(
    string Name,
    ResourceTypeName Type,
    string ApiVersion,
    Func<Guid, string> Body,
    string Action
) {
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Every provider in the platform, as this suite sees them.</summary>
/// <remarks>
///     ⚠ <b>"Every provider, every verb" is a claim about a list, so the list is here and the sweep
///     reads it.</b> A provider that is not in this list is a provider nobody tried to break into, and
///     that is worth being able to see at a glance rather than inferring from which files exist.
/// </remarks>
public static class IsolationCatalog {
    /// <summary>The providers under attack.</summary>
    public static ImmutableArray<IsolationTarget> Targets { get; } = [
        new(
            "CyberCloud.Sample/widgets",
            SampleWidgets.Type,
            SampleWidgets.V2026,
            cluster => SampleWidgets.Body(cluster),
            "ping"
        ),
        new(
            "CyberCloud.ConformanceReference/probes",
            Conformance.Reference.Probes.Type,
            Conformance.Reference.Probes.V2026,
            cluster => Conformance.Reference.Probes.Body(cluster),
            "ping"
        )
    ];

    /// <summary>The catalogue as xUnit theory data.</summary>
    public static TheoryData<IsolationTarget> All {
        get {
            var data = new TheoryData<IsolationTarget>();
            foreach (var target in Targets) {
                data.Add(target);
            }

            return data;
        }
    }
}

/// <summary>
///     Two tenants, every provider, and the <b>real</b> enforcement seam.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The real <c>ReBacResourceAuthorizer</c> over the real <c>CyberCloudSchema</c>, and
///         that choice is the reason this project earns its keep.</b> Every other suite in this
///         repository substitutes a double for the seam, which is right for testing step ordering and
///         useless for testing authorization: a double reproduces the rule its author believed. Two
///         defects lived in that gap until this harness was written, and both are named on the tests
///         that found them.
///     </para>
///     <para>
///         ⚠ <b>The harness writes the resource → group <c>parent</c> tuple itself, and it should not
///         have to.</b> <c>CyberCloudSchema</c> gives every resource
///         <c>Role(owner, This | From(parent, owner))</c>, so a resource is readable by its group's
///         owner <i>only if</i> a <c>resource:{id}#parent@resourceGroup:{group}</c> tuple exists.
///         Nothing in docs/plan/08 § The write path, end to end writes one — the eleven steps go
///         check, locks, policy, quota, index, desired state, operation, emit — and nothing else does
///         either. So after a successful create the resource is invisible to the person who created
///         it. <see cref="LinkToParentAsync" /> is this harness papering over that, out loud, because
///         an isolation suite that could not read anything back would be asserting 404 against 404.
///     </para>
///     <para>
///         ⚠ In-memory storage and in-memory reminders. The isolation properties are about which
///         grain key a call reaches and which answer the seam gives, and neither depends on where the
///         bytes land. The claim that tuples are physically sharded per tenant belongs to
///         <c>CyberCloud.Authorization.Tests</c>, which reads them back out of three real PostgreSQL
///         servers with plain SQL.
///     </para>
/// </remarks>
public sealed class IsolationCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant whose resources an attacker wants.</summary>
    public static Guid Victim { get; } = Guid.Parse("11111111-0000-4000-8000-000000000001");

    /// <summary>The attacker's own tenant.</summary>
    public static Guid Attacker { get; } = Guid.Parse("22222222-0000-4000-8000-000000000002");

    /// <summary>The victim's subscription.</summary>
    public static Guid VictimSubscription { get; } = Guid.Parse("33333333-0000-4000-8000-000000000003");

    /// <summary>The attacker's subscription.</summary>
    public static Guid AttackerSubscription { get; } = Guid.Parse("44444444-0000-4000-8000-000000000004");

    /// <summary>The cluster the fake API server answers for.</summary>
    public static Guid ClusterId { get; } = Guid.Parse("55555555-0000-4000-8000-000000000005");

    /// <summary>The resource group both tenants use.</summary>
    public const string Group = "prod";

    /// <summary>The victim's user.</summary>
    public const string VictimUser = "victor";

    /// <summary>The attacker's user.</summary>
    public const string AttackerUser = "mallory";

    /// <summary>The fake API server. Read to prove a refused request applied nothing.</summary>
    public FakeKubeCluster World { get; } = new(ClusterId);

    /// <summary>The write path, held the way a gateway holds it.</summary>
    public IResourceManager Manager { get; private set; } = null!;

    /// <summary>The registry, with every provider in it.</summary>
    public IProviderRegistry Registry { get; private set; } = null!;

    /// <summary>The client's grain factory.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>Builds an address.</summary>
    /// <param name="target">Which provider.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="tenant">Whose tenant.</param>
    /// <param name="subscription">Whose subscription.</param>
    public static ResourceId Address(IsolationTarget target, string name, Guid tenant, Guid subscription) {
        ArgumentNullException.ThrowIfNull(target);
        return new(tenant, subscription, Group, target.Type, name, Guid.Empty);
    }

    /// <summary>A caller.</summary>
    /// <param name="tenant">The tenant the request is for.</param>
    /// <param name="subject">The subject id.</param>
    public static CallerContext Caller(Guid tenant, string subject) =>
        new() { TenantId = tenant, SubjectType = "user", SubjectId = subject, CorrelationId = "isolation" };

    /// <summary>Grants a user <c>owner</c> on a subscription's resource group.</summary>
    /// <param name="tenant">The tenant the group is in.</param>
    /// <param name="subscription">The subscription the group belongs to.</param>
    /// <param name="user">Who gets it.</param>
    /// <remarks>
    ///     The object id is <c>ReBacResourceAuthorizer.GroupObjectId</c>'s
    ///     <c>{subscriptionId:N}-{name}</c> — a bare group name would merge the <c>prod</c> group of
    ///     every subscription into one authorization object, which is a cross-subscription hole rather
    ///     than a cross-tenant one and is just as real.
    /// </remarks>
    public Task GrantGroupOwnerAsync(Guid tenant, Guid subscription, string user) =>
        WriteTupleAsync(
            tenant,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.ResourceGroup,
                subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + Group
            ),
            Relations.Owner,
            SubjectRef.Of(ObjectTypes.User, user)
        );

    /// <summary>
    ///     Writes the <c>parent</c> tuple that makes a created resource reachable from its group.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription the group belongs to.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <remarks>
    ///     ⚠ <b>THE PLATFORM SHOULD BE DOING THIS AND DOES NOT.</b> See this class's remarks: the
    ///     eleven-step write path never writes a ReBAC tuple, so a resource created through it
    ///     inherits nothing from its resource group and is invisible to its own creator. This method
    ///     exists so the suite has something to attack; it is not a fixture convenience and it should
    ///     be deleted the day the write path grows the step.
    /// </remarks>
    public Task LinkToParentAsync(Guid tenant, Guid subscription, Guid resourceId) =>
        WriteTupleAsync(
            tenant,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.Resource,
                resourceId.ToString("N", CultureInfo.InvariantCulture)
            ),
            Relations.Parent,
            SubjectRef.Of(
                ObjectTypes.ResourceGroup,
                subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + Group
            )
        );

    /// <summary>Writes one tuple into a tenant's store.</summary>
    /// <param name="tenant">Whose store.</param>
    /// <param name="target">The object.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    public async Task WriteTupleAsync(
        Guid tenant,
        Authorization.Contracts.ObjectRef target,
        string relation,
        SubjectRef subject
    ) {
        var tuple = RelationTuple.Create(target, relation, subject);
        tuple.IsSuccess.ShouldBeTrue(tuple.Error?.Message);

        var written = await For(tenant)
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant))
            .WriteAsync(tuple.GetValueOrThrow());

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    /// <summary>Creates a resource and drives it to a terminal state.</summary>
    /// <param name="target">Which provider.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="tenant">Whose tenant.</param>
    /// <param name="subscription">Whose subscription.</param>
    /// <param name="user">Who is asking.</param>
    /// <returns>The created resource's GUID.</returns>
    public async Task<Guid> CreateAsync(
        IsolationTarget target,
        string name,
        Guid tenant,
        Guid subscription,
        string user
    ) {
        ArgumentNullException.ThrowIfNull(target);

        var address = Address(target, name, tenant, subscription);

        var accepted = await Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(ClusterId),
                Caller = Caller(tenant, user)
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(
            "the harness could not create the resource it needs to attack: " + accepted.Error?.Message
        );

        var resourceId = accepted.GetValueOrThrow().Resource.Id;
        await LinkToParentAsync(tenant, subscription, resourceId);

        var operation = For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(accepted.GetValueOrThrow().OperationId));

        for (var i = 0; i < 6; i++) {
            var status = await operation.DriveAsync();
            if (status.GetValueOrThrow().IsTerminal) {
                break;
            }
        }

        return resourceId;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        Instance = this;

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        Registry = ProviderRegistry.Build([new SampleProvider(), new Conformance.Reference.ReferenceProvider()]);

        Manager = new ResourceManagerService(
            Registry,
            // ⚠ THE REAL SEAM. Constructed on the client side, as the gateway constructs it, over the
            // client's grain factory — which is exactly the "permanently outside Orleans.Multitenant's
            // call filter" position IResourceManager's remarks describe, and therefore the position an
            // attacker's request actually arrives at.
            new ReBacResourceAuthorizer(cluster.GrainFactory, NullLogger<ReBacResourceAuthorizer>.Instance),
            new ResourceScopeLockResolver(cluster.GrainFactory),
            new NotSupportedPolicyEvaluator(),
            new LoggingResourceChangedSink(NullLogger<LoggingResourceChangedSink>.Instance),
            cluster.GrainFactory,
            NullLogger<ResourceManagerService>.Instance
        );

        await GrantGroupOwnerAsync(Victim, VictimSubscription, VictimUser);
        await GrantGroupOwnerAsync(Attacker, AttackerSubscription, AttackerUser);
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
    ///     The live harness, for the silo configurator to reach.
    /// </summary>
    /// <remarks>
    ///     ⚠ Static because <c>TestClusterBuilder.AddSiloBuilderConfigurator&lt;T&gt;</c> constructs
    ///     the configurator with <c>new()</c> and the silo resolves its own services. One harness
    ///     exists at a time — every class in this suite shares the collection fixture — and it is set
    ///     before <c>DeployAsync</c>, which is the only ordering that matters.
    /// </remarks>
    internal static IsolationCluster Instance { get; private set; } = null!;

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(new ConformanceClock());
                    services.AddSingleton<IClusterConnectionFactory>(
                        new FakeClusterConnectionFactory(Instance.World)
                    );

                    services.AddSingleton<IResourceProvider, SampleProvider>();
                    services.AddSingleton<IResourceProvider, Conformance.Reference.ReferenceProvider>();
                    services.AddSingleton<WidgetReconciler>();
                    services.AddSingleton<Conformance.Reference.ProbeReconciler>();

                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            // The real engine, with the real schema — this is what makes the suite worth running.
            silo.AddCyberCloudAuthorization();
            silo.AddCyberCloudResourceManager();
        }
    }
}

/// <summary>Binds every class in the suite to one harness.</summary>
[CollectionDefinition(Name)]
public sealed class IsolationSuite : ICollectionFixture<IsolationCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "isolation";
}
