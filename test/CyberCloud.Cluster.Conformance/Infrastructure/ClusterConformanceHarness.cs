using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.ServiceDefaults.Storage;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Multitenant;
using Orleans.TestingHost;
using StackExchange.Redis;
using System.Globalization;
using System.Text;

namespace CyberCloud.Cluster.Conformance.Infrastructure;

/// <summary>
///     The pieces one provider's cluster-backed harness owns, bound to a type rather than to a global.
/// </summary>
/// <typeparam name="TSource">The case source. One set of state per provider, for free.</typeparam>
/// <remarks>
///     ⚠ <b>Static, and it has to be, for the reason <c>ConformanceState&lt;T&gt;</c> gives:</b>
///     Orleans' <c>TestClusterBuilder.AddSiloBuilderConfigurator&lt;T&gt;</c> constructs the
///     configurator with <c>new()</c>, so nothing can be handed to it. Keying on
///     <typeparamref name="TSource" /> is what keeps that from being a shared global.
/// </remarks>
public static class ClusterConformanceState<TSource>
    where TSource : IProviderCaseSource {
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

    /// <summary>The connection to the real API server. Set before the silos start.</summary>
    public static RealClusterConnection Connection { get; set; } = null!;

    /// <summary>The PostgreSQL shard the durable tier writes to.</summary>
    public static string DurableConnectionString { get; set; } = string.Empty;

    /// <summary>The Redis the reminder table lives in.</summary>
    public static string RedisConnectionString { get; set; } = string.Empty;
}

/// <summary>
///     An Orleans cluster wired exactly as a silo wires one, over a <b>real</b> API server and a
///     <b>real</b> PostgreSQL durable tier.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this is for, stated against what it replaces.</b> <c>ProviderTestCluster</c> runs
///         the same provider over <c>AddMemoryGrainStorage</c> and a <c>FakeKubeCluster</c>, and its
///         own remarks list the three things that costs: nothing proves the grain state serializes,
///         nothing proves the durable tier survives a silo loss, and the API server is a dictionary.
///         This harness exists to pay all three, and nothing else about the wiring differs — the
///         doubles, the provider registration and <c>AddCyberCloudResourceManager</c> are identical,
///         so a difference in result is a difference in <i>infrastructure</i> rather than in setup.
///     </para>
///     <para>
///         ⚠ <b>The hot tier is still memory, and that is deliberate rather than owed.</b> ADR-003
///         makes the hot tier's loss tolerance non-zero by definition — every grain in
///         <c>durable-grains.txt</c> binds <c>StorageTiers.Durable</c>, and every grain these five
///         tests touch is on that list. A Redis hot tier would add a container and a failure mode to
///         a path no assertion here reads.
///     </para>
///     <para>
///         ⚠ <b>Reminders are Redis, and that one is not optional.</b> <c>UseInMemoryReminderService</c>
///         puts the reminder table in a grain, so killing the silo that holds it loses every
///         registration — which would make the silo-kill test's "re-registers its reminder and
///         continues" a claim about a table that had itself just been recreated.
///         <c>ClusterConnectionGrain</c> records that no silo in this repository calls
///         <c>UseRedisReminderService</c>; this harness is the first thing that does.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
public sealed class ClusterConformanceHarness<TSource> : IAsyncDisposable
    where TSource : IProviderCaseSource {
    /// <summary>The provider under test.</summary>
    public static ProviderConformanceCase Case => TSource.ProviderCase;

    /// <summary>The cluster the harness answers for.</summary>
    public static Guid ClusterId => ConformanceIds.Cluster;

    TestCluster cluster = null!;

    /// <summary>The write path, built on the <b>client</b> side, which is where the gateway holds it.</summary>
    public IResourceManager Manager { get; private set; } = null!;

    /// <summary>The registry the write path validates against.</summary>
    public IProviderRegistry Registry { get; private set; } = null!;

    /// <summary>The raw Kubernetes client — the half of every assertion that is deliberately not us.</summary>
    public IKubernetes Raw { get; private set; } = null!;

    /// <summary>The fabric's client over the same cluster.</summary>
    public IKubeApiClient Api { get; private set; } = null!;

    /// <summary>
    ///     The recording connection <b>this</b> harness's silos were handed.
    /// </summary>
    /// <remarks>
    ///     ⚠ An instance field and not a read of
    ///     <c>ClusterConformanceState&lt;TSource&gt;.Connection</c>, even though the configurator has
    ///     to read the static one. Two harnesses for one provider exist in a run — the lifecycle
    ///     fixture's and the silo-kill test's pair — and a test that asserted on "the static
    ///     connection" would be asserting on whichever harness started last. That is exactly the
    ///     shared-mutable-global failure <c>ProviderConformanceCase</c>'s remarks argue against, and
    ///     it cost a run: the silo-kill harness disposed a client the lifecycle silo was still using.
    /// </remarks>
    public RealClusterConnection Connection { get; private set; } = null!;

    /// <summary>The PostgreSQL shard the durable tier wrote into.</summary>
    public string DurableConnectionString => ClusterConformanceState<TSource>.DurableConnectionString;

    /// <summary>The Redis the reminder table lives in.</summary>
    public string RedisConnectionString => ClusterConformanceState<TSource>.RedisConnectionString;

    /// <summary>The shared clock.</summary>
    public ConformanceClock Clock => ClusterConformanceState<TSource>.Clock;

    /// <summary>The lock resolver.</summary>
    public SettableLockResolver Locks => ClusterConformanceState<TSource>.Locks;

    /// <summary>Step 8's recorded ReBAC parent edges.</summary>
    public RecordingRelationWriter Relations => ClusterConformanceState<TSource>.Relations;

    /// <summary>The client's grain factory. ⚠ Tenant-unaware, as a gateway's is.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>The silo handles, for a test that kills one.</summary>
    public TestCluster Cluster => cluster;

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The operation grain.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="operationId">The operation.</param>
    public IOperationGrain Operation(Guid tenant, Guid operationId) =>
        For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

    /// <summary>Builds an address for the type under test.</summary>
    /// <param name="name">The resource name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    public static ResourceId Address(string name) =>
        new(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            Case.Type,
            name,
            Guid.Empty
        );

    /// <summary>A caller.</summary>
    /// <param name="tenant">The tenant the request is for.</param>
    public static CallerContext Caller(Guid? tenant = null) =>
        new() {
            TenantId = tenant ?? ConformanceIds.Tenant,
            SubjectType = "user",
            SubjectId = "alice",
            CorrelationId = "cluster-conformance"
        };

    /// <summary>The namespace the driver derives for everything this harness creates.</summary>
    public static string Namespace => ReconcileDriver.NamespaceFor(Address("any"));

    /// <summary>
    ///     Starts the containers if they are not up, then an Orleans cluster over them.
    /// </summary>
    /// <param name="silos">How many silos. Two is what makes a kill observable.</param>
    /// <param name="serviceId">
    ///     The Orleans service id. ⚠ Passed in rather than generated, because the durable rows in
    ///     <c>OrleansStorage</c> are keyed by it — a second cluster that means to read the first
    ///     one's state must ask under the same id.
    /// </param>
    /// <param name="basePort">The silo port to start allocating from, so two clusters can coexist.</param>
    /// <param name="endpoints">The already-started containers.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public static async Task<ClusterConformanceHarness<TSource>> StartAsync(
        int silos,
        string serviceId,
        int basePort,
        ClusterEndpoints endpoints,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(endpoints);

        ClusterConformanceState<TSource>.DurableConnectionString = endpoints.DurableConnectionString;
        ClusterConformanceState<TSource>.RedisConnectionString = endpoints.RedisConnectionString;

        var harness = new ClusterConformanceHarness<TSource>();

        using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(endpoints.Kubeconfig));
        var config = await KubernetesClientConfiguration
            .BuildConfigFromConfigFileAsync(yaml)
            .ConfigureAwait(false);

        harness.Raw = new k8s.Kubernetes(config);
        harness.Api = new KubeApiClient(
            harness.Raw,
            ClusterId,
            ClusterConformanceState<TSource>.Clock,
            false
        );

        harness.Connection = new(harness.Api, ClusterId);

        // ⚠ The static is what the silo configurator can reach — Orleans constructs it with `new()`.
        // It is only correct because this assembly runs its classes one at a time; see
        // AssemblyInfo.cs, which disables parallelisation and says why.
        ClusterConformanceState<TSource>.Connection = harness.Connection;

        await EnsureNamespaceAsync(harness.Raw, Namespace, cancellationToken).ConfigureAwait(false);
        await EnsureCustomResourceDefinitionsAsync(harness.Raw, cancellationToken).ConfigureAwait(false);

        var builder = new TestClusterBuilder((short)silos);
        builder.Options.ServiceId = serviceId;
        builder.Options.ClusterId = serviceId;
        builder.Options.BaseSiloPort = (short)basePort;
        builder.Options.BaseGatewayPort = (short)(basePort + 100);
        builder.AddSiloBuilderConfigurator<Configurator>();

        harness.cluster = builder.Build();
        await harness.cluster.DeployAsync().ConfigureAwait(false);

        harness.Registry = ProviderRegistry.Build([Case.CreateProvider()]);

        await harness.CreateSubscriptionAsync(ConformanceIds.Tenant, ConformanceIds.Subscription)
            .ConfigureAwait(false);

        harness.Manager = new ResourceManagerService(
            harness.Registry,
            ClusterConformanceState<TSource>.Authorizer,
            ClusterConformanceState<TSource>.Relations,
            ClusterConformanceState<TSource>.Locks,
            new NotSupportedPolicyEvaluator(),
            ClusterConformanceState<TSource>.Changes,
            harness.cluster.GrainFactory,
            NullLogger<ResourceManagerService>.Instance
        );

        return harness;
    }

    /// <summary>
    ///     Kills every silo without letting any of them shut down cleanly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Killed rather than stopped, and the distinction is the test.</b> A graceful stop
    ///     deactivates grains, which writes their state — so a resource that converged afterwards
    ///     would prove only that a clean shutdown flushes. <c>KillSiloAsync</c> is Orleans' ungraceful
    ///     path: the process's grains are gone with no <c>OnDeactivateAsync</c>, and anything not
    ///     already in PostgreSQL is lost.
    /// </remarks>
    public async Task KillEverySiloAsync() {
        foreach (var silo in cluster.SecondarySilos.ToList()) {
            await cluster.KillSiloAsync(silo).ConfigureAwait(false);
        }

        await cluster.KillSiloAsync(cluster.Primary).ConfigureAwait(false);
    }

    /// <summary>
    ///     The reminder rows a grain has, read out of the silo's own <see cref="IReminderTable" />.
    /// </summary>
    /// <param name="grain">The grain whose reminders to read.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The table, not the Redis keyspace.</b> The first version of this counted keys in
    ///         Redis, which was wrong in a way worth recording: it conflated "the reminder is stored
    ///         out of process" with "this particular Redis is where it went", and it could not tell a
    ///         missing registration from a key layout the package changed. Reading
    ///         <c>IReminderTable</c> asks the question directly — is there a row for this grain — and
    ///         <see cref="ReminderTableTypeName" /> answers the other half, which store it is.
    ///     </para>
    ///     <para>
    ///         Resolved from the <b>silo's</b> container rather than the client's, because a reminder
    ///         table is a silo service and the client has none.
    ///     </para>
    /// </remarks>
    public async Task<int> ReminderRowsAsync(IAddressable grain) {
        ArgumentNullException.ThrowIfNull(grain);

        var table = cluster.GetSiloServiceProvider().GetRequiredService<IReminderTable>();
        var rows = await table.ReadRows(grain.GetGrainId()).ConfigureAwait(false);

        return rows.Reminders.Count;
    }

    /// <summary>Every reminder row in the table, whoever owns it — for a failure message that helps.</summary>
    /// <remarks>
    ///     ⚠ Zero here and zero for a grain mean different things: the first says nothing registered
    ///     a reminder at all, the second says the row is filed under a grain id the test did not
    ///     expect. Both look identical without this.
    /// </remarks>
    public async Task<string> AllRemindersAsync() {
        var table = cluster.GetSiloServiceProvider().GetRequiredService<IReminderTable>();

        // ⚠ begin == end is Orleans' spelling of "the whole ring" — see RangeFactory.CreateFullRange.
        var rows = await table.ReadRows(0, 0).ConfigureAwait(false);

        return rows.Reminders.Count == 0
            ? "the whole table is empty"
            : string.Join(", ", rows.Reminders.Select(x => x.GrainId + "/" + x.ReminderName));
    }

    /// <summary>The concrete <see cref="IReminderTable" /> the silos are really using.</summary>
    /// <remarks>
    ///     ⚠ Asserted rather than assumed. <c>UseInMemoryReminderService</c> and
    ///     <c>UseRedisReminderService</c> are one line apart and both compile; the difference only
    ///     shows up when a silo dies, which is the one moment this suite cares about.
    /// </remarks>
    public string ReminderTableTypeName =>
        cluster.GetSiloServiceProvider().GetRequiredService<IReminderTable>().GetType().Name;

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            try {
                await cluster.StopAllSilosAsync().ConfigureAwait(false);
            } catch (Exception) {
                // Already killed by a test. Disposing what is gone is not a result.
            }

            await cluster.DisposeAsync().ConfigureAwait(false);
        }

        Raw?.Dispose();
        GC.SuppressFinalize(this);
    }

    async Task CreateSubscriptionAsync(Guid tenant, Guid subscription) {
        var created = await For(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
            .CreateAsync("cluster-conformance");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var group = await For(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, ConformanceIds.ResourceGroup))
            .CreateAsync(tenant, "eu-west-1");

        group.IsSuccess.ShouldBeTrue(group.Error?.Message);
    }

    static async Task EnsureNamespaceAsync(IKubernetes raw, string ns, CancellationToken cancellationToken) {
        var existing = await raw.CoreV1
            .ListNamespaceAsync(fieldSelector: $"metadata.name={ns}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (existing.Items.Count > 0) {
            return;
        }

        try {
            await raw.CoreV1
                .CreateNamespaceAsync(new() { Metadata = new() { Name = ns } }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        } catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict) {
            // ⚠ Two harnesses in one process race here — the lifecycle fixture and the silo-kill
            // test share one k3s and both want the same namespace. A 409 is the other one having
            // won, which is the desired end state; the list above is the cheap path, not the check.
        }
    }

    /// <summary>
    ///     Installs a minimal <c>CustomResourceDefinition</c> for every custom kind the case's
    ///     resource owns, so that the API server serves a REST path for it.
    /// </summary>
    /// <param name="raw">The raw client.</param>
    /// <param name="cancellationToken">The harness's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this, a provider whose objects are custom resources fails every
    ///         cluster-backed assertion, and fails them namelessly.</b> A bare k3s serves no REST
    ///         path for <c>kafka.strimzi.io/v1beta2</c>, so the apply comes back as a
    ///         <c>k8s.Autorest.HttpOperationException</c> <b>carrying no status code</b> — the client
    ///         cannot even discover the group to report a 404 against — and the failure names neither
    ///         the missing CRD nor the provider. The first provider in this suite rendered a
    ///         core-group <c>ConfigMap</c>, so the gap could not appear; the first one that renders a
    ///         custom resource is where it does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The list is DERIVED from <c>ProviderConformanceCase.Objects</c> rather than
    ///         declared as a member of the case, and the difference matters.</b> A declared list is a
    ///         claim a provider author can get wrong by omission — and the failure of an omitted CRD
    ///         is the nameless one above, so the claim would be checked by the worst possible error
    ///         message. Every fact a minimal CRD needs is already in the <see cref="ObjectRef" /> the
    ///         case yields: group, version, kind and the plural that addresses the REST path. Deriving
    ///         it means a provider cannot under-declare, cannot drift, and costs the twentieth
    ///         provider nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this is NOT: the operator's real CRD, and not the operator.</b> The schema
    ///         is <c>x-kubernetes-preserve-unknown-fields</c>, so it validates nothing — this suite's
    ///         four criteria are about REST addressability, server-side apply under our field
    ///         manager, admission of the seven labels, and conflict parsing, none of which need the
    ///         upstream schema. It deliberately does not vendor Strimzi's 8 000-line CRD: a copy of
    ///         an upstream file that nothing checks is drift with a version number on it, and the
    ///         facts that ARE relied on are pinned in <c>charts/managed/kafka/SOURCE</c> where a
    ///         human reads them. What no CRD can supply is the controller: nothing in this suite
    ///         writes <c>status.conditions</c>, which is why every provider's readiness assertion is
    ///         owed in its <c>conformance.yaml</c> rather than made here.
    ///     </para>
    /// </remarks>
    static async Task EnsureCustomResourceDefinitionsAsync(
        IKubernetes raw,
        CancellationToken cancellationToken
    ) {
        var kinds = Case
            .Objects(Address("crd-discovery").WithId(Guid.NewGuid()), Namespace)
            .Select(x => x.Kind)
            // The core group has no CRD and needs none — its REST paths are built in.
            .Where(x => !string.IsNullOrEmpty(x.Group))
            .DistinctBy(x => x.Group + "/" + x.Plural, StringComparer.Ordinal);

        foreach (var kind in kinds) {
            var definition = new V1CustomResourceDefinition {
                ApiVersion = "apiextensions.k8s.io/v1",
                Kind = "CustomResourceDefinition",
                Metadata = new() { Name = kind.Plural + "." + kind.Group },
                Spec = new() {
                    Group = kind.Group,
                    Scope = "Namespaced",
                    Names = new() {
                        Kind = kind.Kind,
                        ListKind = kind.Kind + "List",
                        Plural = kind.Plural,
                        Singular = kind.Kind.ToLowerInvariant()
                    },
                    Versions = [
                        new() {
                            Name = kind.Version,
                            Served = true,
                            Storage = true,
                            // The status subresource is not decoration: an operator writes status
                            // there, and a suite that later asserts on drift needs the spec and the
                            // status to be separately writable exactly as they are upstream.
                            Subresources = new() { Status = new() },
                            Schema = new() {
                                OpenAPIV3Schema = new() {
                                    Type = "object", XKubernetesPreserveUnknownFields = true
                                }
                            }
                        }
                    ]
                }
            };

            try {
                await raw.ApiextensionsV1
                    .CreateCustomResourceDefinitionAsync(definition, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            } catch (k8s.Autorest.HttpOperationException ex)
                when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict) {
                // Two harnesses share one k3s — the lifecycle fixture and the silo-kill test — and
                // both install the same definitions. A 409 is the other one having won, which is the
                // desired end state.
            }
        }

        // ⚠ A CRD is served by the API server ASYNCHRONOUSLY: `Established` is a condition the
        // apiextensions controller sets after the create returns, and an apply issued before it is
        // the same nameless 404 this whole method exists to remove. Polling the condition is what
        // makes the wait a fact rather than a sleep.
        foreach (var kind in kinds) {
            await WaitUntilEstablishedAsync(raw, kind.Plural + "." + kind.Group, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the apiextensions controller to mark a definition <c>Established</c>.</summary>
    /// <param name="raw">The raw client.</param>
    /// <param name="name">The definition's name, <c>{plural}.{group}</c>.</param>
    /// <param name="cancellationToken">The harness's token.</param>
    /// <exception cref="InvalidOperationException">The definition never became established.</exception>
    static async Task WaitUntilEstablishedAsync(
        IKubernetes raw,
        string name,
        CancellationToken cancellationToken
    ) {
        for (var attempt = 0; attempt < 60; attempt++) {
            var definition = await raw.ApiextensionsV1
                .ReadCustomResourceDefinitionAsync(name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (definition.Status?.Conditions?.Any(
                    x => string.Equals(x.Type, "Established", StringComparison.Ordinal)
                        && string.Equals(x.Status, "True", StringComparison.Ordinal)
                ) == true) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        // ⚠ Throws rather than returning, and the fixture turns it into a skip naming the provider.
        // Carrying on would produce the nameless HttpOperationException this method exists to
        // remove, which is strictly worse than saying what did not happen.
        throw new InvalidOperationException(
            $"the CustomResourceDefinition '{name}' was created and never became Established, so the "
            + "API server serves no REST path for it. Every cluster-backed assertion about an object "
            + "of this kind would fail as an HttpOperationException with no status code."
        );
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
            ArgumentNullException.ThrowIfNull(silo);

            // ── The hot tier. Memory, on purpose — see the class remarks. ───────────────────────
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            // ── The durable tier. PostgreSQL through Microsoft.Orleans.Persistence.AdoNet, which
            //    is the tier ADR-003 specifies, with the serializer the shipped wiring uses. ─────
            silo.AddAdoNetGrainStorage(
                StorageTiers.Durable,
                options => {
                    options.Invariant = DurableTierConfigurator.NpgsqlInvariant;
                    options.ConnectionString = ClusterConformanceState<TSource>.DurableConnectionString;

                    // ⚠ THE ONE LINE THAT MAKES THE ROUND-TRIP ASSERTION MEAN ANYTHING. In-memory
                    // storage keeps the object graph; this turns the state into UTF-8 JSON and back,
                    // which is where "System.Text.Json does not populate a get-only collection"
                    // bites. It is the same serializer DurableTierConfigurator installs, so a defect
                    // in it fails here rather than in a cluster.
                    options.GrainStorageSerializer = new SystemTextJsonGrainStorageSerializer();
                }
            );

            // ── Reminders. Redis, so the table outlives a silo. ─────────────────────────────────
            silo.UseRedisReminderService(options =>
                options.ConfigurationOptions =
                    ConfigurationOptions.Parse(ClusterConformanceState<TSource>.RedisConnectionString)
            );

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(ClusterConformanceState<TSource>.Clock);
                    services.AddSingleton<IResourceAuthorizer>(ClusterConformanceState<TSource>.Authorizer);
                    services.AddSingleton<ILockResolver>(ClusterConformanceState<TSource>.Locks);
                    services.AddSingleton<IResourceChangedSink>(ClusterConformanceState<TSource>.Changes);
                    services.AddSingleton<IResourceRelationWriter>(ClusterConformanceState<TSource>.Relations);

                    // ⚠ THE DIFFERENCE FROM ProviderTestCluster, IN ONE LINE: the reconciler is handed
                    // a connection to a real API server rather than to a dictionary.
                    services.AddSingleton<IClusterConnectionFactory>(
                        new RealClusterConnectionFactory(ClusterConformanceState<TSource>.Connection)
                    );

                    services.AddSingleton<IResourceProvider>(_ => TSource.ProviderCase.CreateProvider());
                    services.AddSingleton(TSource.ProviderCase.ReconcilerType);

                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudResourceManager();
        }
    }
}

/// <summary>Reads the durable tier back with plain SQL, around Orleans.</summary>
/// <remarks>
///     ⚠ <b>Plain SQL rather than a second grain read, and that is the whole assertion.</b> Asking
///     Orleans for the state again would be answered from the activation's memory. The row in
///     <c>OrleansStorage</c> is the only evidence the state left the process, and its
///     <c>payloadjson</c> column is the only evidence it survived <i>serialization</i> rather than
///     merely being handed to a provider.
/// </remarks>
public static class DurableRows {
    /// <summary>Every stored payload whose grain id contains <paramref name="fragment" />.</summary>
    /// <param name="connectionString">The shard.</param>
    /// <param name="fragment">A substring of the Orleans grain id, e.g. the operation's GUID.</param>
    /// <param name="cancellationToken">The test's token.</param>
    public static async Task<List<(string GrainId, string GrainType, string Payload)>> ReadAsync(
        string connectionString,
        string fragment,
        CancellationToken cancellationToken
    ) {
        await using var connection = new Npgsql.NpgsqlConnection(
            new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString
        );

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // ⚠ `payloadbinary` and no `payloadjson`, checked against the DDL rather than assumed —
        // PostgreSQL-Persistence.sql declares eleven columns and a JSON one is not among them
        // (Orleans' `UseJsonFormat` column exists in some of the other dialects' scripts, not this
        // one). The bytes ARE JSON here because the durable tier's GrainStorageSerializer is
        // SystemTextJsonGrainStorageSerializer, so decoding them as UTF-8 is reading exactly what
        // was serialized.
        command.CommandText =
            """
            SELECT COALESCE(grainidextensionstring, ''), graintypestring,
                   COALESCE(convert_from(payloadbinary, 'UTF8'), '')
            FROM orleansstorage
            WHERE grainidextensionstring LIKE @fragment
            """;

        command.Parameters.AddWithValue("fragment", "%" + fragment + "%");

        List<(string, string, string)> rows = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }
}
