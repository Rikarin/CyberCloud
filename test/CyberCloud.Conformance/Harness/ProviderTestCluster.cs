using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts.Tunnel;
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
    public static string AncestorName(int level) => "ancestor-" + level.ToString(CultureInfo.InvariantCulture);
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
    ///     ⚠
    ///     <b>
    ///         Static for the same reason <see cref="Cluster" /> is, and it is the same defect if it
    ///         is not.
    ///     </b> The reconciler mints inside the SILO and a synchronous action resolves inside
    ///     the test process, so two instances would be a <c>listKeys</c> that cannot find the
    ///     credential its own create wrote — and the failure would read as a provider bug.
    /// </remarks>
    public static InMemorySecretVault Vault { get; } = new();

    /// <summary>
    ///     The one object store this provider's harness keeps artefacts in — what
    ///     <c>ReconcileContext.Objects</c> is inside the silo.
    /// </summary>
    /// <remarks>
    ///     Static for the reason <see cref="Vault" /> is. A type whose data plane is a platform host
    ///     rather than a cluster object — <c>CyberCloud.ContainerRegistry/feeds</c> — tears down by
    ///     deleting its prefix here, and the assertion that the teardown emptied it reads the same
    ///     instance from the test process.
    /// </remarks>
    public static InMemoryObjectStore Objects { get; } = new();

    /// <summary>
    ///     The buckets and keys a server with backups on is given — what <c>ReconcileContext.Grants</c>
    ///     is inside the silo.
    /// </summary>
    /// <remarks>
    ///     Static for the reason <see cref="Vault" /> is. Since #30 a PostgreSQL server's default body
    ///     archives to the platform's store, so the family converges only against something that
    ///     issues a key; <c>UnavailableObjectStoreGrants</c> would fail every create for a wiring reason.
    /// </remarks>
    public static InMemoryObjectStoreGrants Grants { get; } = new();

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

    /// <summary>
    ///     The driver's namespace ensurer, held here so that <see cref="Reset" /> can empty its memo.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Static for the same reason <see cref="Cluster" /> is, and for a sharper one.</b>
    ///     <c>NamespaceEnsurer</c> caches "the resource group's namespace exists on this cluster" and
    ///     re-applies only after <c>NamespaceEnsurer.RecheckAfter</c> — an hour, which is right for a
    ///     silo and useless here, because <see cref="Reset" /> empties the whole cluster between
    ///     tests in microseconds. Left to <c>AddCyberCloudResourceManager</c>'s own
    ///     <c>TryAddSingleton</c> the instance would be unreachable from a test, and the memo would
    ///     survive a reset that had just deleted the very namespace it remembers.
    ///     <para>
    ///         ⚠ <b>What that cost, before this existed.</b>
    ///         <c>EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations</c> asserts over
    ///         <c>World.Applied</c>. With a warm memo the namespace was never re-applied, so it never
    ///         entered <c>Applied</c>, so the assertion silently stopped covering it — and the whole
    ///         suite passed while the single-test run the <c>Labels</c> architecture gate makes
    ///         failed. An assertion whose subject depends on which test ran first is the failure class
    ///         this harness exists to prevent, not one for it to have.
    ///     </para>
    /// </remarks>
    public static NamespaceEnsurer Namespaces { get; } = new(Clock);

    /// <summary>
    ///     The agent-tunnel seam, for the one type whose product is an agent —
    ///     <see cref="FakeAgentTunnels" /> says what it does and does not prove.
    /// </summary>
    public static FakeAgentTunnels Agents { get; } = new(Clock);

    /// <summary>What the driver attached after a converging pass reported a connection.</summary>
    public static RecordingClusterConnectionRegistrar Registrar { get; } = new();

    /// <summary>Puts every piece back to its default.</summary>
    public static void Reset() {
        Cluster.Reset();
        Clock.Reset();
        Authorizer.Reset();
        Locks.Reset();
        Changes.Reset();
        Relations.Reset();
        Agents.Reset();
        Registrar.Reset();
        Objects.Reset();

        // ⚠ AFTER Cluster.Reset, and the order is the whole point: the memo describes the cluster,
        // and the cluster has just been emptied.
        Namespaces.Forget();

        // A clusterless family's world is its module, and the module is what empties it — see
        // IConvergedModule.Reset for why the suite's leftovers matter there and not in a cluster.
        TSource.ConvergedModule?.Reset();
    }
}

/// <summary>
///     An in-process Orleans cluster with one provider wired exactly as a silo wires it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             In-memory grain storage and in-memory reminders, which is a deviation from ADR-018 and
///             is owed back.
///         </b> The same deviation, for the same reason and with the same cost, as
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

    /// <summary>
    ///     The cross-resource seam of docs/plan/08 § What the resource manager deliberately does not
    ///     do, built the way <c>ReconcileDriver</c> builds it — over this harness's registry, grain
    ///     factory, authorizer and fake cluster — for the passes the suite drives by hand.
    /// </summary>
    /// <remarks>
    ///     ⚠ A context built by hand carries <c>RefusingResourceView</c>, and a reconciler that reads
    ///     another resource through it fails every hand-driven assertion for a wiring reason — the
    ///     drift repair, the hand edit and the four-clause check all construct their own
    ///     <c>ReconcileContext</c>. <c>Views.For(address)</c> is what the driver would have handed
    ///     that pass, bound to the same owner, so a vault repairing drift reads its protected server
    ///     exactly as it does inside the silo. Every other type never calls it and is unaffected.
    /// </remarks>
    public ResourceViews Views { get; private set; } = null!;

    /// <summary>The fake API server the reconciler applies into.</summary>
    public FakeKubeCluster World => ConformanceState<TSource>.Cluster;

    /// <summary>The test vault a minting reconciler writes into.</summary>
    public InMemorySecretVault Vault => ConformanceState<TSource>.Vault;

    /// <summary>The object store the silo's ReconcileContext.Objects is, read from the test side.</summary>
    public InMemoryObjectStore Objects => ConformanceState<TSource>.Objects;

    /// <summary>
    ///     A container holding every action handler the case's provider declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ Built from the registry rather than from a member on the case, because the provider
    ///     already says which handler serves which action and a second declaration on the case would
    ///     be one that can disagree with it. <c>Describe</c> is pure — <see cref="IResourceProvider" />
    ///     requires it — so building the registry twice is the same arrangement
    ///     <c>AddCyberCloudProvider</c> relies on.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE HARNESS'S OWN SEAMS ARE REGISTERED ALONGSIDE THE HANDLERS, AND THEY WERE NOT
    ///             UNTIL A HANDLER FIRST DECLARED A DEPENDENCY.
    ///         </b> This container held nothing but the
    ///         handler types, which worked for exactly as long as every handler had a parameterless
    ///         constructor — one did, for one provider. The first handler to take an
    ///         <c>IClock</c> failed to activate with
    ///         <i>
    ///             "Unable to resolve service for type 'CyberCloud.Core.Time.IClock' while attempting
    ///             to activate '…'"
    ///         </i>, thrown from <c>ActionDispatcher</c>'s <c>GetService</c> — which
    ///         names the handler and the harness and reads like a provider bug. ⚠
    ///         <b>
    ///             And it is a
    ///             harness bug: a real host has these.
    ///         </b> <c>AddCyberCloudResourceManager</c> registers
    ///         <c>IClock</c>, <c>IClusterConnectionFactory</c> and <c>ISecretResolver</c>, and
    ///         <c>AddCyberCloudProvider</c> adds the handler into that <i>same</i> container. Building
    ///         a separate one with strictly less in it made the suite ask less than the platform
    ///         provides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three registered are the harness's own doubles rather than new ones</b>, on
    ///         the same principle the shared vault follows: a handler that reads a secret the case's
    ///         reconciler minted must read it out of <see cref="Vault" />, and a handler that reaches
    ///         the cluster must see the objects <see cref="World" /> holds. A second set would make a
    ///         handler's answer disagree with the reconciler's world for a reason no assertion could
    ///         attribute. ⚠ Second harness change this family has needed, after
    ///         <c>ClusterConformanceHarness</c>'s hard-coded <c>Scope = "Namespaced"</c>, and the
    ///         shape is the same both times: the harness had quietly agreed with every provider so far
    ///         about something no provider had had to think about.
    ///     </para>
    /// </remarks>
    ServiceProvider Handlers() {
        var services = new ServiceCollection();

        services.AddSingleton<IClock>(ConformanceState<TSource>.Clock);
        services.AddSingleton<IClusterConnectionFactory>(new FakeClusterConnectionFactory(World));
        services.AddSingleton<ISecretResolver>(Vault);
        services.AddSingleton<ISecretWriter>(Vault);

        // ⚠ A clusterless family's handlers hold the module's client-side seams — the ones the
        // gateway registers in production — and the module is what knows how to build them over
        // this harness's client. See IConvergedModule.ConfigureHandlers.
        Module?.ConfigureHandlers(services, Grains);

        // ⚠ And what a cluster-backed case's handlers hold — IProviderCaseSource.ConfigureHandlers.
        TSource.ConfigureHandlers(services);

        foreach (var handler in Registry.Types
                     .SelectMany(static x => x.Actions)
                     .Select(static x => x.HandlerType)
                     .OfType<Type>()
                     .Distinct()) {
            services.AddSingleton(handler);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The module a clusterless case converges onto, or <see langword="null" /> for a case whose
    ///     world is <see cref="World" />. See <see cref="IProviderCaseSource.ConvergedModule" />.
    /// </summary>
    public static IConvergedModule? Module => TSource.ConvergedModule;

    /// <summary>
    ///     Whether this case's world is a module rather than the fake cluster.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not what the suite branches on. A clusterless type may register a
    ///     <c>ProviderConformanceCase.DataPlane</c> instead of a module, so the suite branches on
    ///     the registry's <c>RequiresCluster</c> — <c>ProviderConformanceTests.HasClusterDataPlane</c>
    ///     — and reads either registration through <c>ClusterlessWorld</c>.
    /// </remarks>
    public static bool Clusterless => TSource.ConvergedModule is not null;

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
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

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
    ///     ⚠
    ///     <b>
    ///         This is what makes <see cref="IProviderCaseSource.Ancestors" />' default safe, and it
    ///         is deliberately the FIRST thing anything touching an address goes through.
    ///     </b> Without it a
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
        string.Join('/', Ancestors.Select(static (_, level) => ConformanceIds.AncestorName(level)));

    /// <summary>Builds an address for the type under test.</summary>
    /// <param name="name">The resource name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="ConformanceIds.Tenant" />.</param>
    /// <param name="subscription">The subscription, defaulting to <see cref="ConformanceIds.Subscription" />.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The ancestor names come from the harness, not from the case, and that is why a child's
    ///         run is the same suite rather than a copy of it.
    ///     </b> Every assertion in
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

    /// <summary>
    ///     The sibling resources the source declares, checked against the type under test's own
    ///     provider and ancestor chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A sibling is from another provider, is nested deeper than the case's ancestor chain
    ///     reaches, sits under ancestors that are not the case's own, or is named like the harness's
    ///     ancestor at its level.
    /// </exception>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The first thing anything touching a sibling goes through, for the reason
    ///         <see cref="Ancestors" /> is.
    ///     </b> Without it a sibling from another provider fails inside
    ///     <c>ResourceManagerService</c> with the registry's message about an unknown type, on the
    ///     fixture's first create, naming neither the case nor the member; and a sibling nested past
    ///     the chain fails inside <c>ResourceId</c>'s constructor about parent-name counts. Each
    ///     refusal here names <c>Siblings</c> and the sibling, which is the difference between a
    ///     provider author reading it once and bisecting the fixture start.
    /// </remarks>
    public static ImmutableArray<SiblingResource> Siblings {
        get {
            var declared = TSource.Siblings;
            var ancestors = Ancestors;

            foreach (var sibling in declared) {
                var type = sibling.Case.Type;
                var member = typeof(TSource).Name + ".Siblings";

                if (!string.Equals(type.Namespace, Case.Type.Namespace, StringComparison.Ordinal)) {
                    throw new InvalidOperationException(
                        $"'{Case.DisplayName}' declares '{sibling.Name}' of type '{type}' as a sibling in "
                        + $"{member}, and that type belongs to another provider. The suite registers ONE "
                        + "provider, so a sibling from another would be a resource this run's registry "
                        + "cannot address, and its create would fail with the registry's message rather "
                        + "than this one. A sibling is a resource the type's body names within its own "
                        + "provider — see IProviderCaseSource.Siblings."
                    );
                }

                var levels = type.Depth - 1;

                if (levels > ancestors.Length) {
                    throw new InvalidOperationException(
                        $"'{Case.DisplayName}' declares '{sibling.Name}' of type '{type}' as a sibling in "
                        + $"{member}, which nests {type.Depth.ToString(CultureInfo.InvariantCulture)} level(s) "
                        + $"deep, and the case's ancestor chain is {ancestors.Length.ToString(CultureInfo.InvariantCulture)} "
                        + "long. A sibling lives under the harness's own ancestors, so it can nest no "
                        + "deeper than the type under test does — see IProviderCaseSource.Siblings."
                    );
                }

                for (var level = 0; level < levels; level++) {
                    if (AncestorTypeAt(type, level) != ancestors[level].Type) {
                        throw new InvalidOperationException(
                            $"'{Case.DisplayName}' declares '{sibling.Name}' of type '{type}' as a sibling in "
                            + $"{member}, and its ancestor at level {level.ToString(CultureInfo.InvariantCulture)} "
                            + $"is '{AncestorTypeAt(type, level)}' where the case's is '{ancestors[level].Type}'. "
                            + "A sibling is created under the harness's OWN ancestors, so its chain has to "
                            + "be a prefix of the case's — see IProviderCaseSource.Siblings."
                        );
                    }
                }

                if (levels < ancestors.Length
                    && string.Equals(sibling.Name, ConformanceIds.AncestorName(levels), StringComparison.Ordinal)) {
                    throw new InvalidOperationException(
                        $"'{Case.DisplayName}' declares a sibling named '{sibling.Name}' in {member}, which is "
                        + $"the harness's own ancestor name at level {levels.ToString(CultureInfo.InvariantCulture)}. "
                        + "The sibling would be the ancestor, created twice — pick another name."
                    );
                }
            }

            return declared;
        }
    }

    /// <summary>Builds the address of a declared sibling — under the harness's ancestors, at its own depth.</summary>
    /// <param name="sibling">The sibling, as <see cref="IProviderCaseSource.Siblings" /> declares it.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="ConformanceIds.Tenant" />.</param>
    /// <param name="subscription">The subscription, defaulting to <see cref="ConformanceIds.Subscription" />.</param>
    /// <remarks>
    ///     The one place the sibling arithmetic is done, as <see cref="Address" /> is for the type
    ///     under test: the sibling's ancestor path is the harness's ancestor names for the levels its
    ///     type nests below, which <see cref="Siblings" /> has already checked are the case's own.
    /// </remarks>
    public static ResourceId SiblingAddress(SiblingResource sibling, Guid? tenant = null, Guid? subscription = null) {
        ArgumentNullException.ThrowIfNull(sibling);

        var levels = sibling.Case.Type.Depth - 1;

        return new(
            tenant ?? ConformanceIds.Tenant,
            subscription ?? ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            sibling.Case.Type,
            sibling.Name,
            Guid.Empty,
            string.Join('/', Enumerable.Range(0, levels).Select(ConformanceIds.AncestorName))
        );
    }

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

        // ⚠ Before anything is written, so a case's direct-drive reconciler and the module's own
        // readings both have a client to reach the silo through. See IConvergedModule.Attach.
        Module?.Attach(cluster.GrainFactory);

        Registry = ProviderRegistry.Build(Providers());

        // ⚠ The subscriptions are created before anything is written into them. Step 1 of the write
        // path now reads ISubscriptionGrain and answers 404 for a subscription that does not exist,
        // so a harness that skipped this would fail every create with "does not exist" and the reason
        // would be the harness rather than the provider.
        await CreateSubscriptionAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);
        await CreateSubscriptionAsync(ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription);

        // ⚠ AND THE QUOTA LIMITS ARE LIFTED, WHICH CLOSES AN ITEM charts/managed/opensearch OWED AND
        // WHICH THE TENTH PROVIDER COULD NOT SHIP WITHOUT.
        //
        // Every assertion in this suite creates against ONE subscription and nothing releases the
        // committed amounts between them, so a provider's usable assertion count used to be
        // `QuotaGrain.Defaults[meter] / its own draw`. CyberCloud.Search met that as a tuning problem —
        // four assertions failed with "300 committed + 90 reserved + 30 requested > 400" and the case
        // was changed to use a smaller service. CyberCloud.ContainerService/managedClusters cannot be
        // tuned out of it: it draws QuotaMeter.Clusters, whose default limit is FIVE, and a cluster
        // cannot ask for less than one cluster. Twenty-eight assertions against a limit of five is a
        // suite that fails from the sixth test onwards, in every provider that is a cluster, forever.
        //
        // ⚠ NOTHING IN THIS SUITE ASSERTS A QUOTA LIMIT, so lifting them removes an accidental
        // coupling rather than an assertion. Quota's own behaviour — reserve, commit, release, refuse
        // over the limit — is CyberCloud.Tenancy.Tests' QuotaGrainTests, which sets its own limits and
        // is unaffected. What this restores is the property the suite was written to have: that the
        // twenty-eighth assertion is as independent of the first as the second is.
        await LiftQuotaAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);
        await LiftQuotaAsync(ConformanceIds.OtherTenant, ConformanceIds.OtherSubscription);

        Views = new(
            Registry,
            cluster.GrainFactory,
            Authorizer,
            new FakeClusterConnectionFactory(World),
            Clock,
            NullLogger<ResourceViews>.Instance
        );

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
                Vault,
                ConformanceState<TSource>.Agents
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

        // ⚠ AND THE SIBLINGS, THEN THE COMPANIONS — AFTER THE ANCESTORS, AND IN THE PRIMARY TENANT
        // ONLY. Both are resources the case's BODY names rather than ones its address interleaves: a
        // peering names a second network of its own provider (IProviderCaseSource.Siblings), a vault
        // names a PostgreSQL server of ANOTHER provider (IProviderCaseSource.Companions), and neither
        // can converge until what it names has. Siblings go first because they sit under the harness's
        // own ancestors; a companion is top-level and depends on nothing the harness made. The other
        // tenant gets neither: the ancestors are created there because the write path checks a
        // parent's binding before it compares tenants, so the 404 assertion needed a parent to get
        // past; nothing on the write path reads a sibling, and the other tenant's create is refused
        // at step 1 before any reconciler asks the view for a companion, so a copy of either over
        // there would keep no assertion honest.
        await CreateSiblingsAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);
        await CreateCompanionsAsync(ConformanceIds.Tenant, ConformanceIds.Subscription);

        // ⚠ AND THE WORLD IS REMEMBERED, ONCE, AFTER ALL OF THEM, SO THAT A RESET PUTS IT BACK RATHER
        // THAN EMPTYING IT. Every assertion begins with Reset, and until the peering and the vault —
        // merged the same day, each having hit this — that emptied the fake cluster and nothing minded:
        // every type re-created what it owns from its body. A co-writing type owns nothing and writes
        // onto the siblings' objects; a vault owns its schedule but reads the companion's Cluster
        // through the view first. Both need those objects there when a test starts. What is remembered
        // is exactly what the fixture created; a test's own objects are still gone at the next Reset.
        // See FakeKubeCluster.Baseline, and ReferenceSiblingProviderConformance
        // .TheSiblingSurvivesAResetAsAResourceAndAsObjects for the assertion that flipped.
        World.Baseline();
    }

    /// <summary>
    ///     The case's provider and every companion's, one instance per provider namespace.
    /// </summary>
    /// <remarks>
    ///     ⚠ Distinct by namespace: two companions from one family, or a companion from the case's own
    ///     family, must not register that provider twice — <c>ProviderRegistry.Build</c> refuses a
    ///     duplicate namespace by name, and the refusal would read as a provider bug.
    /// </remarks>
    public static List<IResourceProvider> Providers() {
        var providers = new List<IResourceProvider> { Case.CreateProvider() };

        foreach (var companion in Companions) {
            var provider = companion.ProviderCase.CreateProvider();
            if (providers.Exists(x => string.Equals(
                        x.ProviderNamespace,
                        provider.ProviderNamespace,
                        StringComparison.OrdinalIgnoreCase
                    )
                )) {
                continue;
            }

            providers.Add(provider);
        }

        return providers;
    }

    /// <summary>
    ///     The companion cases, checked to be top-level types of a provider this run can register.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     A companion nests under a parent, or two companions share a name.
    /// </exception>
    public static ImmutableArray<CompanionCase> Companions {
        get {
            var declared = TSource.Companions;

            foreach (var companion in declared) {
                if (companion.ProviderCase.Type.Depth != 1) {
                    throw new InvalidOperationException(
                        $"'{Case.DisplayName}' declares companion '{companion.ProviderCase.DisplayName}' of type "
                        + $"'{companion.ProviderCase.Type}', which nests under a parent. The harness creates a "
                        + "companion by name in the run's resource group and builds no ancestor chain for it; "
                        + "a top-level companion is the shape every case so far has needed. See "
                        + "IProviderCaseSource.Companions."
                    );
                }
            }

            var duplicate = declared.GroupBy(static x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault(static x => x.Count() > 1);
            if (duplicate is not null) {
                throw new InvalidOperationException(
                    $"'{Case.DisplayName}' declares two companions named '{duplicate.Key}'. Each is a resource in "
                    + "one resource group, so the second create would be an update of the first."
                );
            }

            return declared;
        }
    }

    /// <summary>Creates the companions, each driven to a confirmed binding before the next.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    async Task CreateCompanionsAsync(Guid tenant, Guid subscription) {
        foreach (var companion in Companions) {
            var address = companion.Address(tenant, subscription);

            var accepted = await Manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = companion.ProviderCase.ApiVersion,
                    Verb = WriteVerb.Put,
                    Body = companion.BodyFor(ConformanceIds.Cluster),
                    Caller = Caller(tenant)
                },
                CancellationToken.None
            );

            accepted.IsSuccess.ShouldBeTrue(
                $"the harness could not create companion '{address.Path}', which '{Case.Type}' protects: "
                + accepted.Error?.Message
            );

            var operation = Operation(tenant, accepted.GetValueOrThrow().OperationId);

            for (var drive = 0; drive < 8; drive++) {
                var status = await operation.DriveAsync();
                if (status.GetValueOrThrow().IsTerminal) {
                    break;
                }
            }

            var bound = await Index(address).GetAsync();

            bound.GetValueOrThrow()
                .State.ShouldBe(
                    IndexEntryState.Confirmed,
                    $"companion '{address.Path}' did not reach a confirmed binding, so every read of it "
                    + $"through the view answers 404 and '{Case.Type}' refuses it by name"
                );
        }
    }

    /// <summary>Puts every quota meter out of the way for one subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Every declared meter, rather than the ones the provider under test happens to
    ///         draw.
    ///     </b> A harness that lifted only what today's providers use would go back to being a
    ///     budget the day somebody declared <see cref="QuotaMeter.PublicIps" />, and the failure would
    ///     name quota rather than the harness — which is exactly what made this cost a provider author
    ///     an afternoon the first time.
    /// </remarks>
    async Task LiftQuotaAsync(Guid tenant, Guid subscription) {
        var quota = For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

        foreach (var meter in Enum.GetValues<QuotaMeter>()) {
            if (meter == QuotaMeter.Unknown) {
                continue;
            }

            var set = await quota.SetLimitAsync(meter, 1_000_000m);
            set.IsSuccess.ShouldBeTrue(set.Error?.Message);
        }
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

            bound.GetValueOrThrow()
                .State.ShouldBe(
                    IndexEntryState.Confirmed,
                    $"'{address.Path}' did not reach a confirmed binding, so every create under it will "
                    + "answer the parent-not-found 404 and the failure will name the CHILD's path"
                );
        }
    }

    /// <summary>Creates the siblings the source declares, each driven to <c>Succeeded</c>.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <remarks>
    ///     ⚠ <b>Driven to <c>Succeeded</c>, not merely to a confirmed binding, and asserted.</b> An
    ///     ancestor only has to <i>exist</i> for a child's create to pass the parent check. A sibling
    ///     exists so the type under test can relate to it — a peering onto a network that is still
    ///     <c>Creating</c> has no <c>Vpc</c> to write onto — so a sibling that did not converge is the
    ///     harness's failure, named here, rather than the case's failure a test later.
    /// </remarks>
    async Task CreateSiblingsAsync(Guid tenant, Guid subscription) {
        foreach (var sibling in Siblings) {
            var address = SiblingAddress(sibling, tenant, subscription);

            var accepted = await Manager.WriteAsync(
                new() {
                    Path = address.Path,
                    ApiVersion = sibling.Case.ApiVersion,
                    Verb = WriteVerb.Put,
                    Body = sibling.Case.Body(ConformanceIds.Cluster),
                    Caller = Caller(tenant)
                },
                CancellationToken.None
            );

            accepted.IsSuccess.ShouldBeTrue(
                $"the harness could not create the sibling '{address.Path}', which '{Case.Type}' relates "
                + $"to: {accepted.Error?.Message}"
            );

            var operation = Operation(tenant, accepted.GetValueOrThrow().OperationId);
            OperationStatus? last = null;

            for (var drive = 0; drive < 8; drive++) {
                last = (await operation.DriveAsync()).GetValueOrThrow();
                if (last.IsTerminal) {
                    break;
                }
            }

            last.ShouldNotBeNull();
            last.State.ShouldBe(
                OperationState.Succeeded,
                $"the sibling '{address.Path}' ended {last.State} rather than Succeeded, so a type that "
                + $"relates to it has nothing converged to relate to: {last.Error?.Message}"
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

            silo.ConfigureServices(static services => {
                    services.AddSingleton<IClock>(ConformanceState<TSource>.Clock);
                    services.AddSingleton<IResourceAuthorizer>(ConformanceState<TSource>.Authorizer);
                    services.AddSingleton<ILockResolver>(ConformanceState<TSource>.Locks);
                    services.AddSingleton<IResourceChangedSink>(ConformanceState<TSource>.Changes);
                    services.AddSingleton<IResourceRelationWriter>(ConformanceState<TSource>.Relations);
                    services.AddSingleton<IClusterConnectionFactory>(
                        new FakeClusterConnectionFactory(ConformanceState<TSource>.Cluster)
                    );

                    // ⚠ The instance, not the type, so that Reset can empty its memo — see the
                    // remarks on ConformanceState.Namespaces. AddCyberCloudResourceManager uses
                    // TryAddSingleton, so registering it first is what keeps this one.
                    services.AddSingleton(ConformanceState<TSource>.Namespaces);

                    // ⚠ BOTH SEAMS FROM ONE OBJECT, AND THE SUITE WOULD BE VACUOUS WITHOUT THEM.
                    // A provider whose reconciler mints a credential — CyberCloud.Storage/accounts is
                    // the first — cannot converge against UnavailableSecretWriter, so every
                    // convergence assertion would fail for a wiring reason. InMemorySecretVault
                    // implements mint-once for real, which is what keeps the idempotence assertion
                    // from measuring itself.
                    services.AddSingleton<ISecretResolver>(ConformanceState<TSource>.Vault);
                    services.AddSingleton<ISecretWriter>(ConformanceState<TSource>.Vault);

                    // ⚠ THE AGENT SEAM AND THE REGISTRAR, both before AddCyberCloudResourceManager's
                    // TryAdd defaults. CyberCloud.ContainerService/connectedClusters is the first type
                    // whose converging pass REPORTS a connection in a Docker-free run, and the
                    // refusing registrar would fail that pass for the harness's reason.
                    services.AddSingleton<IAgentTunnels>(ConformanceState<TSource>.Agents);
                    services.AddSingleton<IClusterConnectionRegistrar>(ConformanceState<TSource>.Registrar);
                    // ⚠ The object store, for the same reason the vault is here: a type whose data
                    // plane keeps bytes on the platform's storage cannot tear down against
                    // UnavailableObjectStore, and a refusal there would read as a provider bug.
                    services.AddSingleton<IObjectStore>(ConformanceState<TSource>.Objects);
                    services.AddSingleton<IObjectStoreGrants>(ConformanceState<TSource>.Grants);

                    // The provider, exactly as AddCyberCloudProvider<T> registers one: the provider
                    // itself, and the reconciler as a SINGLETON BY CONCRETE TYPE — clause 2 makes one
                    // instance per process correct, and the registry stores the concrete type because
                    // that is what ReconcileDriver resolves.
                    services.AddSingleton(static _ => TSource.ProviderCase.CreateProvider());
                    services.AddSingleton(TSource.ProviderCase.ReconcilerType);

                    // ⚠ AND EVERY COMPANION'S PROVIDER AND RECONCILER, because the silo's registry is
                    // built from the IResourceProvider registrations it holds, and the view resolves a
                    // protected item's type against THAT registry — a companion registered on the
                    // client side alone would be creatable and invisible. One provider per namespace,
                    // for the reason Providers() gives.
                    foreach (var provider in Providers().Skip(1)) {
                        services.AddSingleton(provider);
                    }

                    foreach (var reconciler in Companions
                                 .Select(static x => x.ProviderCase.ReconcilerType)
                                 .Where(static x => x != TSource.ProviderCase.ReconcilerType)
                                 .Distinct()) {
                        services.AddSingleton(reconciler);
                    }

                    // ⚠ AND EVERY ANCESTOR'S RECONCILER, because the harness creates the ancestors
                    // and ReconcileDriver resolves each type's reconciler FROM THIS CONTAINER by the
                    // concrete type the registry stores. One provider declares both a child and its
                    // parent, so registering only the case's own reconciler leaves the parent's
                    // create failing inside the silo — as a resolution error nothing on the request
                    // path can attribute to the harness.
                    //
                    // ⚠ AND EVERY SIBLING'S, for the same reason: the harness creates the siblings
                    // too, and a sibling of a different type than the case's — a network beside a
                    // peering — is driven by a reconciler nothing else here registers.
                    foreach (var reconciler in TSource.Ancestors
                                 .Select(static x => x.ReconcilerType)
                                 .Concat(TSource.Siblings.Select(static x => x.Case.ReconcilerType))
                                 .Where(static x => x != TSource.ProviderCase.ReconcilerType)
                                 .Distinct()) {
                        services.AddSingleton(reconciler);
                    }

                    // ⚠ AND EVERY ACTION HANDLER, for the reason the reconcilers are here: the
                    // registry stores a concrete Type and ActionDispatcher resolves it from a
                    // container. A LongRunning action driven inside the silo would otherwise refuse
                    // with a message about the container, naming the harness rather than the case.
                    foreach (var handler in ProviderRegistry.Build(Providers())
                                 .Types
                                     .SelectMany(static x => x.Actions)
                                     .Select(static x => x.HandlerType)
                                     .OfType<Type>()
                                     .Distinct()) {
                        services.AddSingleton(handler);
                    }

                    services.TryAddSingleton<ILoggerFactory>(static _ => NullLoggerFactory.Instance);
                }
            );

            // ⚠ A clusterless family's reconcilers converge onto a module's grains, and the module is
            // what hosts them here — the same call the silo host makes. Before the resource manager,
            // so the module's TryAdd registrations see the harness's clock rather than replacing it.
            TSource.ConvergedModule?.ConfigureSilo(silo);

            // ⚠ AND WHAT A CLUSTER-BACKED CASE'S SIBLING HANDLERS HOLD. Every handler above is
            // registered by concrete type and the host validates the container on build, so a
            // handler whose constructor takes a family seam needs that seam here even in a suite
            // that never posts to it. See IProviderCaseSource.ConfigureSilo for the case that found
            // this.
            TSource.ConfigureSilo(silo);

            silo.AddCyberCloudResourceManager();
        }
    }
}
