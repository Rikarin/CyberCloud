using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Providers.Sample;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.Providers.Storage;
using CyberCloud.Providers.Storage.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
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
/// <param name="Ancestors">
///     The targets this one nests inside, <b>outermost first</b>. Empty for a top-level type.
///     <para>
///         ⚠ <b>A child cannot be created until its parent exists</b> — the write path resolves the
///         parent's index binding on every create and answers the same 404 as "no such resource" when
///         it is absent. So a suite that attacks a child has to have built one first, in the tenant
///         it is attacking <i>and</i> in the attacker's own. This member is what
///         <see cref="IsolationCluster.Address" /> interleaves into the path and what
///         <c>IsolationCluster.CreateAncestorsAsync</c> creates.
///     </para>
///     <para>
///         ⚠ <b>Positional and therefore unskippable, unlike its conformance counterpart.</b> This
///         record is the suite's own and has exactly one construction site — the catalogue below — so
///         a default would buy nothing and would let the next child target be added without anybody
///         deciding what its ancestors are.
///     </para>
/// </param>
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
    string Action,
    ImmutableArray<IsolationTarget> Ancestors
) {
    /// <summary>The name this suite gives the ancestor at <paramref name="level" />, outermost 0.</summary>
    /// <param name="level">The nesting level.</param>
    public static string AncestorName(int level) =>
        "ancestor-" + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>The <c>/</c>-separated ancestor names an address for this target carries.</summary>
    public string ParentNames =>
        string.Join('/', Ancestors.Select((_, level) => AncestorName(level)));

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
    /// <summary>The reference provider's top-level type, which the child below nests inside.</summary>
    static IsolationTarget Probes { get; } =
        new(
            "CyberCloud.ConformanceReference/probes",
            Conformance.Reference.Probes.Type,
            Conformance.Reference.Probes.V2026,
            cluster => Conformance.Reference.Probes.Body(cluster),
            "ping",
            []
        );

    /// <summary>
    ///     The shipping object-storage account, which the bucket in the list below nests inside.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Declared BEFORE <see cref="Targets" />, as <see cref="Probes" /> is, and the compiler
    ///     is what enforces it</b> — a static initializer that read this after <c>Targets</c> would
    ///     put a <see langword="null" /> ancestor in the list, which is <c>CS8601</c> here and would
    ///     have been an ancestorless child at run time.
    ///     <para>
    ///         ⚠ <b>Built from the provider's public contracts, exactly as an outside caller would.</b>
    ///         This suite deliberately does not reuse <c>StorageCase</c> — an attacker's suite that
    ///         borrowed the provider's own conformance fixture would inherit whatever that fixture
    ///         assumes, and the point of docs/plan/03 § test/'s separate project is that this one
    ///         assumes nothing.
    ///     </para>
    /// </remarks>
    static IsolationTarget StorageAccount { get; } =
        new(
            "CyberCloud.Storage/accounts",
            StorageAccounts.Type,
            StorageAccounts.V2026,
            cluster => StorageAccounts.Body(cluster),
            StorageAccounts.ListKeysAction,
            []
        );

    /// <summary>The providers under attack.</summary>
    public static ImmutableArray<IsolationTarget> Targets { get; } = [
        new(
            "CyberCloud.Sample/widgets",
            SampleWidgets.Type,
            SampleWidgets.V2026,
            cluster => SampleWidgets.Body(cluster),
            "ping",
            []
        ),
        Probes,
        // ⚠ A CHILD, AND IT IS IN THE SWEEP RATHER THAN IN A TEST OF ITS OWN.
        //
        // Every theory in this project reads this list, so one entry puts a nested type through the
        // whole attack surface — every verb, the oracle sweep, and the below-the-manager reads —
        // rather than through whichever handful of them somebody thought to write a child version
        // of. That is the same argument ProviderConformanceTests makes for a shared suite, and it is
        // load-bearing for the same reason: a child-specific test file is free to assert less and
        // nothing says which assertions it dropped.
        //
        // ⚠ AND THE PARENT HOP IS WHY IT CANNOT BE SKIPPED. Until 2026-08-12 a resource's ReBAC
        // `parent` edge always named its resource GROUP; a child's now names its PARENT RESOURCE. So
        // a child's visibility is decided by a rewrite through an object in the tenant's own tuple
        // store rather than by the group every isolation assertion here was written against, and
        // "the boundary holds one level down" stopped being implied by "the boundary holds".
        new(
            "CyberCloud.ConformanceReference/probes/samples",
            Conformance.Reference.Probes.ChildType,
            Conformance.Reference.Probes.V2026,
            cluster => Conformance.Reference.Probes.ChildBody(cluster),
            "ping",
            [Probes]
        ),
        // ⚠ THE FIRST SHIPPING PROVIDER IN THIS LIST, AND ITS CHILD, AND BOTH HALVES ARE THE POINT.
        //
        // Until now every target here was a fixture: CyberCloud.Sample/widgets is docs/plan/24's
        // deliberately-trivial instrument and the reference provider exists only so the shared suite
        // has something to run against. So "every provider, every verb, wrong tenant → 404" was a
        // claim about two types that nobody ships. This is the first row an outside caller could
        // actually address, and — because it is object storage — the first whose 404 protects
        // something a tenant would notice losing.
        //
        // ⚠ AND THE CHILD IS WHY THE ACCOUNT IS HERE RATHER THAN ONLY THE BUCKET. A child cannot be
        // created until its parent exists, in the ATTACKER'S tenant as well as the victim's — see
        // CreateAncestorsAsync — so the account has to be a target in its own right for the bucket to
        // be attackable at all. That the account then gets the whole sweep for free is the shape this
        // list is for.
        StorageAccount,
        new(
            "CyberCloud.Storage/accounts/buckets",
            StorageBuckets.Type,
            StorageBuckets.V2026,
            cluster => StorageBuckets.Body(cluster),
            StorageBuckets.StatsAction,
            [StorageAccount]
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
///         ⚠ <b>The harness no longer writes the resource → group <c>parent</c> tuple, and the
///         deletion of the method that did is this suite's second finding.</b>
///         <c>CyberCloudSchema</c> gives every resource
///         <c>Role(owner, This | From(parent, owner))</c>, so a resource is readable by its group's
///         owner <i>only if</i> a <c>resource:{id}#parent@resourceGroup:{group}</c> tuple exists —
///         and the write path never wrote one, so a create succeeded and its own creator then got
///         <c>404</c>. <c>LinkToParentAsync</c> used to sit here papering over that, with remarks
///         saying in capitals that the platform should be doing it. docs/plan/08 § The write path,
///         end to end has the step now (step 8, between the index claim and the durable write), the
///         helper is gone, and <see cref="ParentsOfAsync" /> reads the tuple back instead of writing
///         it. A harness that has to fix the system under test before it can attack it is itself the
///         bug report.
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

    /// <summary>
    ///     The scope path, held the way a gateway holds it, over the real authorization engine.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Real seams on both sides, for the reason <see cref="Manager" /> has them.</b> The
    ///     scope path's whole job is to write two <c>parent</c> tuples and check a permission through
    ///     the rewrites they enable, so a doubled authorizer or a doubled relation writer would leave
    ///     it asserting that its author's beliefs are self-consistent. This is the only suite in the
    ///     repository that drives <c>ScopeManagerService</c> against <c>CyberCloudSchema</c>.
    /// </remarks>
    public IScopeManager Scopes { get; private set; } = null!;

    /// <summary>
    ///     The test vault both halves share — the silo mints into it, the client reads out of it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Static because the silo resolves its own container and this suite asserts across the
    ///     boundary, the same reason ConformanceState keys its cluster on a type.
    /// </remarks>
    public static InMemorySecretVault Vault { get; } = new();

    /// <summary>The action handlers the client-side dispatcher resolves.</summary>
    static ServiceProvider Handlers { get; } = BuildHandlers();

    static ServiceProvider BuildHandlers() {
        var services = new ServiceCollection();
        services.AddSingleton<StorageAccountListKeysHandler>();

        return services.BuildServiceProvider();
    }

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
        return new(tenant, subscription, Group, target.Type, name, Guid.Empty, target.ParentNames);
    }

    /// <summary>A caller.</summary>
    /// <param name="tenant">The tenant the request is for.</param>
    /// <param name="subject">The subject id.</param>
    public static CallerContext Caller(Guid tenant, string subject) =>
        new() { TenantId = tenant, SubjectType = "user", SubjectId = subject, CorrelationId = "isolation" };

    /// <summary>Grants a user <c>owner</c> directly on a tenant.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="user">Who gets it.</param>
    /// <remarks>
    ///     ⚠ <b>Direct, because there is no other kind on a tenant.</b> <c>CyberCloudSchema</c> gives
    ///     <c>tenant</c> no <c>parent</c> relation — nothing is above it — so no rewrite can produce
    ///     <c>owner</c> here and a tenant with no direct tuple is one nobody can act on. That is the
    ///     fact <c>IScopeManager.CreateTenantAsync</c> is built around.
    /// </remarks>
    public Task GrantTenantOwnerAsync(Guid tenant, string user) =>
        WriteTupleAsync(
            tenant,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.Tenant,
                tenant.ToString("N", CultureInfo.InvariantCulture)
            ),
            Relations.Owner,
            SubjectRef.Of(ObjectTypes.User, user)
        );

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
    ///     Reads a resource's <c>parent</c> tuples straight out of the tenant's forward index.
    /// </summary>
    /// <param name="tenant">Whose store.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <returns>The subjects of every <c>parent</c> tuple on that resource. Empty when there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>This replaced a method that WROTE the tuple, and the replacement is the finding.</b>
    ///     <c>LinkToParentAsync</c> used to live here, with remarks saying in capitals that the
    ///     platform should be doing it and did not. docs/plan/08 § The write path, end to end now has
    ///     the step — step 8, between the index claim and the durable write — so the harness's job
    ///     turned from papering over a defect into checking that the platform does it, and this reads
    ///     the tuple rather than writing it.
    ///     <para>
    ///         It goes through <c>IObjectRelationsGrain</c>, which is the <i>forward</i> index and the
    ///         authority (docs/plan/07 § Storage, row 1) — the same direction <c>Check</c> walks. A
    ///         test that read the reverse index would be asserting against the half that is allowed to
    ///         be stale.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<SubjectRef>> ParentsOfAsync(Guid tenant, Guid resourceId) {
        var snapshot = await For(tenant)
            .GetGrain<IObjectRelationsGrain>(
                GrainKeys.ObjectRelations(
                    ObjectTypes.Resource,
                    resourceId.ToString("N", CultureInfo.InvariantCulture)
                )
            )
            .ReadDurableAsync();

        return snapshot.IsSuccess
            && snapshot.GetValueOrThrow().ByRelation.TryGetValue(Relations.Parent, out var parents)
                ? parents
                : [];
    }

    /// <summary>Resolves an address to the resource's GUID through the tenant's path index.</summary>
    /// <param name="tenant">Whose index.</param>
    /// <param name="address">The address.</param>
    /// <remarks>
    ///     ⚠ Only a <b>confirmed</b> binding resolves, which is what makes this the same question the
    ///     write path's parent check asks. A test that read the claim instead would call a name under
    ///     an unexpired two-phase lease a parent.
    /// </remarks>
    public async Task<Guid> ResolveAsync(Guid tenant, ResourceId address) {
        var bound = await For(tenant)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address))
            .ResolveAsync();

        bound.IsSuccess.ShouldBeTrue($"'{address.Path}' is not bound: {bound.Error?.Message}");
        return bound.GetValueOrThrow();
    }

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

        // ⚠ Nothing is linked here. The write path's step 8 did it, and OracleTests asserts that it
        // did — see this class's remarks on the helper that used to be on this line.
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

        // ⚠ THE SAME THREE PROVIDERS THE SILO'S CONFIGURATOR REGISTERS, AND THE TWO LISTS ARE TWO
        // COPIES OF ONE FACT. The silo builds its own registry from `GetServices<IResourceProvider>()`
        // — ResourceManagerSiloBuilderExtensions — and this one is the CLIENT'S, constructed the way
        // the gateway constructs it. A provider added to one and not the other produces "'…' is not a
        // resource type this platform serves" from whichever half was forgotten, which is a clear
        // enough message that a third copy to diff them would cost more than it catches.
        Registry = ProviderRegistry.Build(
            [new SampleProvider(), new Conformance.Reference.ReferenceProvider(), new StorageProvider()]
        );

        Manager = new ResourceManagerService(
            Registry,
            // ⚠ THE REAL SEAM. Constructed on the client side, as the gateway constructs it, over the
            // client's grain factory — which is exactly the "permanently outside Orleans.Multitenant's
            // call filter" position IResourceManager's remarks describe, and therefore the position an
            // attacker's request actually arrives at.
            new ReBacResourceAuthorizer(cluster.GrainFactory, NullLogger<ReBacResourceAuthorizer>.Instance),
            // ⚠ AND THE REAL WRITE HALF OF IT. Step 8's tuple goes into the same store the check
            // walks, through the real ReBacResourceRelationWriter over the real CyberCloudSchema. A
            // double here would let the suite pass with a tuple the schema cannot follow — which is
            // precisely the class of defect this project exists to catch, and precisely how the
            // resourcegroup/resourceGroup casing bug survived everywhere else.
            new ReBacResourceRelationWriter(
                cluster.GrainFactory,
                NullLogger<ReBacResourceRelationWriter>.Instance
            ),
            new ResourceScopeLockResolver(cluster.GrainFactory),
            new NotSupportedPolicyEvaluator(),
            new LoggingResourceChangedSink(NullLogger<LoggingResourceChangedSink>.Instance),
            cluster.GrainFactory,
            // ⚠ THE ACTION PATH, AND IT IS ON THE SEAM SIDE THIS SUITE EXISTS TO ATTACK. A
            // synchronous action runs HERE, on the client, outside Orleans.Multitenant's call
            // filter — so  against another tenant's account is refused by
            // IResourceAuthorizer or by nothing at all. The vault is shared with the silo so that a
            // credential the victim's create minted is really there to be stolen.
            new ActionDispatcher(Handlers, new NoClusterConnectionFactory(), Vault),
            NullLogger<ResourceManagerService>.Instance
        );

        Scopes = new ScopeManagerService(
            new ReBacScopeAuthorizer(cluster.GrainFactory, NullLogger<ReBacScopeAuthorizer>.Instance),
            new ReBacScopeRelationWriter(cluster.GrainFactory, NullLogger<ReBacScopeRelationWriter>.Instance),
            cluster.GrainFactory,
            // ⚠ Wired with NO cluster connections, which is what this suite wants: a group delete
            // that reached a namespace would be reading a k3s that is not here. The reclaim refuses
            // instead — ConnectionNamespaceInventory answers ResourceNotFound for a cluster with no
            // connection — and what this suite attacks is everything ABOVE that: whether the tenant
            // check and the permission check hold on a delete the way they do on a create.
            new ResourceGroupReclaimer(
                cluster.GrainFactory,
                new NoClusterConnectionFactory(),
                new ConnectionNamespaceInventory(new NoClusterConnectionFactory()),
                new NamespaceEnsurer(new ConformanceClock()),
                NullLogger<ResourceGroupReclaimer>.Instance
            ),
            NullLogger<ScopeManagerService>.Instance
        );

        // ⚠ The subscriptions and their groups are real records now, because step 1 of the write path
        // reads ISubscriptionGrain and answers 404 for a subscription the caller's tenant does not
        // have. That check is what closes the /tenants/{mine}/subscriptions/{theirs}/… hole
        // BelowTheManagerTests used to document from the safe side.
        await CreateScopesAsync(Victim, VictimSubscription);
        await CreateScopesAsync(Attacker, AttackerSubscription);

        await GrantGroupOwnerAsync(Victim, VictimSubscription, VictimUser);
        await GrantGroupOwnerAsync(Attacker, AttackerSubscription, AttackerUser);

        // ⚠ THE ANCESTORS, IN BOTH TENANTS, AND THE SECOND ONE IS THE POINT. A child's create resolves
        // its parent's index binding before the enforcement seam runs, so an attacker's request
        // against a child whose parent does not exist in the ATTACKER's tenant would be refused by
        // the parent check — and every cross-tenant assertion in this suite would pass without the
        // seam being consulted at all. Building the same parent on both sides is what keeps the 404s
        // below about the tenant boundary rather than about a missing fixture.
        await CreateAncestorsAsync(Victim, VictimSubscription, VictimUser);
        await CreateAncestorsAsync(Attacker, AttackerSubscription, AttackerUser);
    }

    /// <summary>Creates every ancestor every target in the catalogue nests inside.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">Its subscription.</param>
    /// <param name="user">Who creates them.</param>
    /// <remarks>
    ///     ⚠ One shared parent per catalogue level, named by <see cref="IsolationTarget.AncestorName" />
    ///     and created once. A parent per test would spend a create per assertion to prove what the
    ///     parent's own row in the sweep already proves.
    /// </remarks>
    async Task CreateAncestorsAsync(Guid tenant, Guid subscription, string user) {
        foreach (var target in IsolationCatalog.Targets.Where(x => !x.Ancestors.IsEmpty)) {
            for (var level = 0; level < target.Ancestors.Length; level++) {
                var ancestor = target.Ancestors[level];
                var name = IsolationTarget.AncestorName(level);

                var address = new ResourceId(
                    tenant,
                    subscription,
                    Group,
                    ancestor.Type,
                    name,
                    Guid.Empty,
                    string.Join('/', Enumerable.Range(0, level).Select(IsolationTarget.AncestorName))
                );

                var bound = await For(tenant)
                    .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address))
                    .GetAsync();

                if (bound.IsSuccess && bound.GetValueOrThrow().State == IndexEntryState.Confirmed) {
                    // Two targets may share an ancestor; the first one built it.
                    continue;
                }

                await CreateAsync(ancestor, name, tenant, subscription, user);
            }
        }
    }

    /// <summary>Creates a tenant's subscription and its <c>prod</c> group.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    async Task CreateScopesAsync(Guid tenant, Guid subscription) {
        var created = await For(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
            .CreateAsync("isolation");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var group = await For(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, Group))
            .CreateAsync(tenant, "eu-west-1");

        group.IsSuccess.ShouldBeTrue(group.Error?.Message);
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

                    // ⚠ The CHILD type's reconciler. ReconcileDriver resolves each type's reconciler
                    // from this container by the concrete type the registry stores, so a child whose
                    // reconciler is not registered fails inside the silo — where nothing on the
                    // request path can attribute it to the harness.
                    services.AddSingleton<Conformance.Reference.SampleReconciler>();

                    // ⚠ The first SHIPPING provider in this suite, and both of its types. One
                    // IResourceProvider registration carries both — StorageProvider.Describe chains
                    // the child onto the parent — and each type still needs its own reconciler
                    // singleton, because ProviderRegistry stores them by CONCRETE TYPE.
                    services.AddSingleton<IResourceProvider, StorageProvider>();
                    services.AddSingleton<StorageAccountReconciler>();
                    services.AddSingleton<StorageBucketReconciler>();
                    services.AddSingleton<StorageAccountListKeysHandler>();

                    // ⚠ The SAME vault instance the client-side manager reads. The mint happens in
                    // the silo and listKeys resolves on the client, so two instances would make every
                    // listKeys assertion below fail as "not found" rather than as a refusal — which
                    // is a suite that passes for the wrong reason.
                    services.AddSingleton<ISecretResolver>(Vault);
                    services.AddSingleton<ISecretWriter>(Vault);

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
