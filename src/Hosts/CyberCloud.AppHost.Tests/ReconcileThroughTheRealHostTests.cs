using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     docs/plan/24 § Phase 1, exit criterion 1 — one resource, from <c>PUT</c> to
///     <c>Succeeded</c>, through the processes that ship.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT THIS PROVES THAT NO CONFORMANCE SUITE CAN.</b> Every suite that has ever
///         reconciled a widget builds its own <c>TestCluster</c>, calls
///         <c>AddCyberCloudResourceManager</c> and <c>AddCyberCloudProvider</c> on it, substitutes
///         the authorizer (<c>SwitchableAuthorizer</c>, <c>PermissiveAuthorizer</c>) and the
///         relation writer (<c>RecordingRelationWriter</c>), and then <b>pumps the operation by
///         hand</b> — <c>ClusterConformanceTests.ConvergeAsync</c> is a loop over
///         <c>IOperationGrain.DriveAsync</c>. So a green conformance run says the reconciler
///         is correct and says nothing at all about whether the platform reconciles. It stayed green
///         through the period in which <c>CyberCloud.Silo.Host</c> composed no provider, and it would
///         have stayed green through the two defects this file found:
///     </para>
///     <list type="number">
///         <item>
///             <b>No host referenced <c>CyberCloud.Authorization</c></b>, so
///             <c>ReBacResourceAuthorizer</c> — which the real hosts do compose — asked
///             <c>ICheckGrain</c> a question no silo held a grain to answer. The enforcement seam
///             turns a check that fails into the canonical <c>404</c>, so every create through the
///             real hosts was refused as a resource that does not exist.
///         </item>
///         <item>
///             <b>No host registered <c>KubeApiClientFactory.ResolveKubeconfig</c></b>, so the first
///             apply of the first reconcile of any type declaring <c>RequiresCluster</c> — which is
///             most of the catalogue — failed with an <c>InternalError</c> naming a registration
///             nobody had made.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>AND NOTHING HERE POKES THE OPERATION.</b> <see cref="ConvergeAsync" /> polls
///         <c>IResourceManager.GetOperationAsync</c> and never calls <c>DriveAsync</c>. The only thing
///         that can move this operation is the reminder <c>OperationGrain</c> registers, fired by the
///         Redis reminder table <c>SiloComposition.ConfigureStorage</c> wires, on whichever of the two
///         silo <i>processes</i> Orleans placed the grain on. A reminder that is registered and never
///         fires and a reconcile that is working look identical for the first minute and differ at the
///         deadline, so the failure message below reads the operation's own counters to say which one
///         happened.
///     </para>
///     <para>
///         ⚠ <b>AND THE SCOPES ARE THE PLATFORM'S NOW, WHICH IS A THIRD DEFECT OF THE SAME SHAPE.</b>
///         This file used to create its subscription and its resource group by reaching for
///         <c>ISubscriptionGrain</c> and <c>IResourceGroupGrain</c>, and to grant its caller
///         <c>owner</c> on the group with a tuple it wrote itself — with remarks calling that
///         <i>"the seeding a real deployment does when a subscription is created"</i>. It was not:
///         nothing in the platform could create either scope, and nothing wrote the
///         <c>resourceGroup#parent@subscription</c> or <c>subscription#parent@tenant</c> edges either,
///         so <i>every</i> harness in the repository stood on a scope tree it had built and a grant it
///         had placed one level too low. Both scopes now come from <c>IScopeManager</c> out of the
///         same real container, and the only tuple this file writes is on the <b>tenant</b> — which is
///         the one grant no rewrite can produce, because <c>tenant</c> has no <c>parent</c>.
///     </para>
///     <para>
///         ⚠ <b>What it still cannot prove.</b> The gateway's nine HTTP stages are not in this path:
///         <see cref="IResourceManager" /> is resolved from the real
///         <c>GatewayComposition.BuildAsync</c> container — the same object graph
///         <c>CyberCloud.Gateway.Host</c>'s own <c>Program.cs</c> builds — but the request is handed to
///         it directly rather than parsed off an <c>HttpContext</c>. That half is
///         <c>CyberCloud.Gateway.Host.Tests</c>, which runs the whole pipeline against a
///         <c>DefaultHttpContext</c> with a substituted manager; the two suites meet at
///         <c>IResourceManager</c> and neither covers the join. Driving it over HTTP would need a
///         bearer token, and the only resolver that issues one is <c>internal</c> to the gateway and
///         in-process by design.
///     </para>
///     <para>
///         ⚠ <b>The namespace seam used to be held open by hand here and is now an assertion.</b>
///         This class carried an <c>EnsureNamespaceAsync</c> that created
///         <c>{subscription:N}-{resourceGroup}</c> with the raw client, because
///         <c>ReconcileDriver.NamespaceFor</c> derived the name and nothing created it. It is gone:
///         <c>NamespaceEnsurer</c> applies the namespace, with ADR-013's seven labels, before the
///         first pass — and <see cref="AWidgetPutThroughTheRealHostsReachesSucceededAndItsConfigMapIsInK3s" />
///         now reads that namespace back through the raw client and checks the labels. A test that
///         creates the thing it is about to look for proves the API server accepts creates.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost — two silo processes, Redis, PostgreSQL, k3s.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class ReconcileThroughTheRealHostTests(LocalTopology topology) : IAsyncLifetime {
    /// <summary>
    ///     How long the operation is given to reach a terminal state before it is stuck.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Minutes, and the unit is not slack.</b> <c>OperationGrain.ReminderPeriod</c> is one
    ///     minute and <c>EnsureReminderAsync</c> registers the reminder with that as its due time too,
    ///     so the <i>first</i> pass of a create cannot happen sooner than about sixty seconds after
    ///     the <c>202</c>. A widget converges in one pass; four more minutes covers a retry ladder
    ///     that ticks at the same period.
    /// </remarks>
    static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for k3s, which nothing in the AppHost waits on.</summary>
    /// <remarks>
    ///     ADR-001 makes the Kubernetes API a data plane rather than a dependency of the control
    ///     plane booting, so the silos come up without it and this suite is the first thing that
    ///     needs it. Measured at about 20 s on a warm image cache.
    /// </remarks>
    static readonly TimeSpan ClusterBudget = TimeSpan.FromMinutes(3);

    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0010");
    static readonly Guid Subscription = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0011");

    /// <summary>The cluster id the widget's body names, and the connection grain's key.</summary>
    static readonly Guid Cluster = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0012");

    const string ResourceGroup = "phase-1";
    const string Widget = "first-widget";
    const string Subject = "phase-1-operator";

    /// <summary>The real gateway host, built the way <c>Program.cs</c> builds it.</summary>
    WebApplication gateway = null!;

    /// <summary>The raw Kubernetes client — the half of the assertion that is deliberately not us.</summary>
    IKubernetes raw = null!;

    /// <summary>The widget's address, before the index binds a GUID to it.</summary>
    static ResourceId Address { get; } =
        new(Tenant, Subscription, ResourceGroup, SampleWidgets.Type, Widget, Guid.Empty);

    /// <summary>The namespace <c>ReconcileDriver.NamespaceFor</c> derives for this address.</summary>
    static string Namespace { get; } =
        Subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + ResourceGroup;

    /// <summary>Who is asking. Its tenant selects every grain the write path touches.</summary>
    static CallerContext Caller { get; } =
        new() { TenantId = Tenant, SubjectType = SubjectTypes.User, SubjectId = Subject };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        raw = await ConnectToK3sAsync(token);

        // ⚠ THE GATEWAY IS BUILT BEFORE THE SEEDING NOW, BECAUSE THE SEEDING GOES THROUGH IT.
        // The subscription and the resource group used to be created by reaching for
        // ISubscriptionGrain and IResourceGroupGrain from this test, which is what every harness in
        // the repository does and is exactly why nobody noticed that nothing in the platform could
        // create either. They are created by IScopeManager now — resolved from the same real
        // container IResourceManager comes out of — so this file no longer stands on a scope tree
        // it built itself.
        gateway = await BuildGatewayAsync();
        await gateway.StartAsync(token);

        await BootstrapTenantAsync(token);
        await CreateScopesThroughTheRealManagerAsync(token);
        await AttachClusterAsync(token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (gateway is not null) {
            await gateway.StopAsync(CancellationToken.None);
            await gateway.DisposeAsync();
        }

        raw?.Dispose();
    }

    // ── The criterion ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWidgetPutThroughTheRealHostsReachesSucceededAndItsConfigMapIsInK3s() {
        var token = TestContext.Current.CancellationToken;
        var manager = gateway.Services.GetRequiredService<IResourceManager>();

        // ── The PUT. Twelve steps, none of them substituted. ────────────────────────────────────
        //
        // ⚠ Step 3 is ReBacResourceAuthorizer against the check grain in the silo, step 6 is the real
        // QuotaGrain on a real PostgreSQL shard, step 7 is the real index grain, step 8 writes a real
        // ReBAC parent edge, and step 9 writes the resource grain through the durable tier. There is
        // no double anywhere in that list, which is what makes this different from every other suite
        // that has ever created a widget.
        var accepted = await manager.WriteAsync(
            new() {
                Path = Address.Path,
                ApiVersion = SampleWidgets.V2026,
                Verb = WriteVerb.Put,
                Body = SampleWidgets.Body(Cluster),
                Caller = Caller
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue(
            $"the write path refused a create the caller is entitled to make: "
            + $"{accepted.Error?.Code} — {accepted.Error?.Message}. A ResourceNotFound here is the "
            + "enforcement seam answering for a check that could not be made, not for a resource "
            + "that does not exist — see the silo's CyberCloud.Authorization reference."
        );

        var write = accepted.GetValueOrThrow();

        write.NoOp.ShouldBeFalse("a create of a name nothing holds cannot be a no-op.");
        write.OperationId.ShouldNotBe(Guid.Empty);

        write.Resource.ProvisioningState.ShouldBe(
            ProvisioningState.Creating,
            "the resource grain accepted the desired state and did not enter Creating."
        );

        // ── Convergence, driven by nothing in this process ──────────────────────────────────────
        var status = await ConvergeAsync(manager, write.OperationId);

        status.State.ShouldBe(OperationState.Succeeded, Diagnose(status));

        // ── The namespace, which nothing in this process created ────────────────────────────────
        //
        // ⚠ THIS ASSERTION REPLACED A HELPER THAT CREATED THE NAMESPACE ITSELF. While
        // EnsureNamespaceAsync existed, every k3s-backed assertion below was standing on a namespace
        // the test had made, so the suite proved nothing about whether the platform can place a
        // resource on a cluster it has never written to. Nothing in this file touches the namespace
        // now: NamespaceEnsurer applies it on the driver's path, in a silo process, before the first
        // reconciler runs.
        var namespaces = await raw.CoreV1.ListNamespaceAsync(
            fieldSelector: "metadata.name=" + Namespace,
            cancellationToken: token
        );

        namespaces.Items.Count.ShouldBe(
            1,
            $"the operation succeeded and there is no namespace '{Namespace}' in the cluster. "
            + "ReconcileDriver.NamespaceFor derives it and NamespaceEnsurer is what creates it, "
            + "before the pass — a converged reconcile with no namespace means the ensure was "
            + "skipped and the reconciler applied somewhere else."
        );

        var created = namespaces.Items[0];

        // ⚠ THE LABELS ARE THE HALF THAT MATTERS, and they are why this is not just an existence
        // check. An unlabelled namespace is worse than an absent one: CloudConsoles' tenant-boundary
        // NetworkPolicy selects namespaces by cybercloud.io/tenant-id, so a namespace that exists
        // without the label degrades the isolation model silently instead of failing the reconcile.
        // The hand-rolled helper created exactly such a namespace, so this assertion is also what
        // makes its return impossible to miss.
        created.Metadata.Labels.ShouldNotBeNull(
            $"namespace '{Namespace}' carries no labels at all, so nothing in the cluster records "
            + "which tenant it belongs to."
        );

        foreach (var label in KubeLabels.Mandatory) {
            created.Metadata.Labels.ShouldContainKey(
                label,
                $"namespace '{Namespace}' is missing the mandatory label '{label}'. ADR-013's seven "
                + "are injected by KubeCommand on the same write that creates the namespace, so a "
                + "missing one here means the namespace was created by something other than "
                + "NamespaceEnsurer."
            );
        }

        created.Metadata.Labels[KubeLabels.TenantId].ShouldBe(
            KubeLabels.GuidValue(Tenant),
            "the namespace names a different tenant than the one that created the resource. The "
            + "cloud-shell NetworkPolicy's tenant-wide egress rule matches on exactly this value."
        );

        created.Metadata.Labels[KubeLabels.ResourceGroup].ShouldBe(ResourceGroup);

        // ── The object, read around every line of our own code ──────────────────────────────────
        //
        // ⚠ THE RAW KubernetesClient, ASKING THE API SERVER BY NAME. Reading it back through
        // IKubeClusterConnection would be asking the same code that wrote it, and a ConfigMap that
        // only our own client can see is one nobody has proved is there.
        // ⚠ Listed with a field selector rather than read by name, because a read of an absent object
        // throws an HttpOperationException whose message is a status line. The list answers with an
        // empty collection, so the assertion below is the one that reports the failure.
        var found = await raw.CoreV1.ListNamespacedConfigMapAsync(
            Namespace,
            fieldSelector: "metadata.name=" + Widget,
            cancellationToken: token
        );

        found.Items.Count.ShouldBe(
            1,
            $"the operation succeeded and there is no ConfigMap '{Widget}' in namespace "
            + $"'{Namespace}'. A converged reconcile that left nothing in the cluster means the "
            + "reconciler's read-back is answering from somewhere other than the API server."
        );

        var configMap = found.Items[0];

        configMap.Data.ShouldContainKeyAndValue("message", "hello");
        configMap.Data.ShouldContainKeyAndValue("enabled", "true");

        // ⚠ The seven labels are how a platform-owned object is told from a tenant's own — ADR-013,
        // docs/plan/09 § The command builder. They are applied by KubeCommandBuilder and survive
        // admission, or they do not, and only a real API server can say which.
        configMap.Metadata.Labels.ShouldNotBeNull(
            "the ConfigMap carries no labels, so nothing in the cluster records which resource owns it."
        );

        foreach (var label in KubeLabels.Mandatory) {
            configMap.Metadata.Labels.ShouldContainKey(
                label,
                $"the ConfigMap in the cluster is missing the mandatory label '{label}'. "
                + "KubeCommandBuilder applies all seven; a missing one here means admission stripped "
                + "it, which no dictionary standing in for an API server can show."
            );
        }

        configMap.Metadata.Labels[KubeLabels.ResourceId]
            .ShouldBe(
                KubeLabels.GuidValue(write.Resource.Id),
                "the object in the cluster names a different resource than the one the write path "
                + "created."
            );

        configMap.Metadata.Annotations.ShouldContainKey(KubeLabels.ResourcePathAnnotation);

        // ── And the resource, read back through the same front door ─────────────────────────────
        var read = await manager.ReadAsync(
            new() { Path = Address.Path, ApiVersion = SampleWidgets.V2026, Caller = Caller },
            token
        );

        read.IsSuccess.ShouldBeTrue($"the created resource is not readable: {read.Error?.Message}");

        read.GetValueOrThrow()
            .ProvisioningState
            .ShouldBe(
                ProvisioningState.Succeeded,
                "the operation reported Succeeded and the resource did not follow it. "
                + "OperationGrain.FinishResourceAsync is what moves the resource, and the two "
                + "disagreeing means a caller polling the operation and a caller reading the resource "
                + "get different answers."
            );
    }

    // ── Waiting ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Polls the operation until it is terminal. ⚠ It never drives it.
    /// </summary>
    /// <param name="manager">The gateway's write path.</param>
    /// <param name="operationId">The operation the <c>202</c> named.</param>
    /// <returns>The last status read, terminal or not.</returns>
    static async Task<OperationStatus> ConvergeAsync(IResourceManager manager, Guid operationId) {
        var token = TestContext.Current.CancellationToken;
        var clock = Stopwatch.StartNew();
        OperationStatus? last = null;

        while (clock.Elapsed < ConvergenceBudget) {
            var read = await manager.GetOperationAsync(operationId, Caller, token);

            read.IsSuccess.ShouldBeTrue(
                $"operation {operationId:D} became unreadable while it was running: "
                + $"{read.Error?.Code} — {read.Error?.Message}"
            );

            last = read.GetValueOrThrow();

            if (last.IsTerminal) {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"operation {operationId:D} reached {last.State} after "
                    + $"{clock.Elapsed.TotalSeconds:F0} s and {last.Attempts} reconcile "
                    + $"{(last.Attempts == 1 ? "pass" : "passes")}."
                );

                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        last.ShouldNotBeNull();
        return last;
    }

    /// <summary>
    ///     Says which link broke, because a stuck operation and a failed one need different repairs.
    /// </summary>
    /// <param name="status">The last status read.</param>
    /// <remarks>
    ///     ⚠ <b>The counters are the diagnosis and they are not available after the fact.</b>
    ///     <c>Attempts</c> is incremented by <c>DriveAsync</c> and by nothing else, so an operation
    ///     that sat at zero for five minutes is a reminder that never fired — a reminder table that is
    ///     not there, a silo that never re-registered, or a grain that could not activate. One that
    ///     tried and is still running is a reconciler that is not converging, and its own progress
    ///     entries say why.
    /// </remarks>
    static string Diagnose(OperationStatus status) =>
        status.State switch {
            OperationState.Failed or OperationState.Canceled =>
                $"the operation ended {status.State}: {status.Error?.Code} — {status.Error?.Message}. "
                + $"Its last progress was '{status.LastProgress?.Step}': "
                + $"{status.LastProgress?.Detail}",
            _ when status.Attempts == 0 =>
                $"the operation was still {status.State} after {ConvergenceBudget} "
                + "and has never been driven — Attempts is 0. THE RECONCILE LOOP NEVER "
                + "STARTED: OperationGrain registers a reminder named 'reconcile' at a one-minute "
                + "period and something fired it zero times. Look at the silo's reminder service "
                + "(SiloComposition.ConfigureStorage wires Redis from the hot tier's connection "
                + "string) before looking at the reconciler.",
            _ =>
                $"the operation was still {status.State} after {ConvergenceBudget}. "
                + $"IT STARTED AND DID NOT FINISH: {status.Attempts} passes ran and the last "
                + $"progress was '{status.LastProgress?.Step}': {status.LastProgress?.Detail}. "
                + "That is the reconciler or the cluster, not the loop."
        };

    // ── Seeding — everything the write path reads and does not create ────────────────────────────

    /// <summary>
    ///     The one thing that is still done by hand, and the one thing that has to be.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A tenant record and one direct <c>tenant:{t}#owner</c> tuple. Everything below
    ///         this line goes through the platform.</b> <c>CyberCloudSchema</c> gives <c>tenant</c>
    ///         no <c>parent</c> relation — nothing is above it — so no rewrite can produce a grant on
    ///         one and only a direct tuple can. That is not a gap in the platform; it is why
    ///         <c>IScopeManager.CreateTenantAsync</c> exists as a platform-operator seam off the
    ///         request pipeline, and why its request carries the owner rather than defaulting it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is <i>not</i> asserted here, said plainly.</b> This does not call
    ///         <c>CreateTenantAsync</c>: that method also assigns a durable shard and registers a
    ///         tenant-directory entry, and driving those needs a configured shard map this topology
    ///         does not seed. So the bootstrap's own ordering — the owner edge before the directory
    ///         entry, so that no request can reach a tenant nobody owns — is still owed a test. Its
    ///         two refusals are covered by <c>ScopeCreationTests</c>.
    ///     </para>
    /// </remarks>
    async Task BootstrapTenantAsync(CancellationToken cancellationToken) {
        var tenant = topology.Client.ForTenant(Tenancy(Tenant));

        var record = await tenant
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
            .CreateAsync("phase-1-tenant", "Phase 1", "eu-central");

        record.IsSuccess.ShouldBeTrue(record.Error?.Message);

        var subject = SubjectRef.Create(SubjectTypes.User, Subject).GetValueOrThrow();

        var tuple = RelationTuple.Create(
            AuthObjectRef
                .Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture))
                .GetValueOrThrow(),
            Relations.Owner,
            subject
        ).GetValueOrThrow();

        var written = await tenant
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant))
            .WriteAsync(tuple);

        written.IsSuccess.ShouldBeTrue(
            $"the ReBAC grant could not be written: {written.Error?.Code} — {written.Error?.Message}. "
            + "A failure naming a grain type means the silo holds no authorization grains, which is "
            + "the CyberCloud.Authorization reference in CyberCloud.Silo.Host.csproj."
        );

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     Creates the subscription and the resource group <b>through the real scope manager</b>.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THESE ARE THE FIRST TWO STEPS OF THE M1 EXIT STORY AND NOTHING HAD EVER RUN
    ///         THEM.</b> <c>ISubscriptionGrain.CreateAsync</c> and <c>CreateResourceGroupAsync</c> had
    ///         no non-test caller in the tree, so every harness — including the version of this file
    ///         that carried a <c>SeedSubscriptionAsync</c> — created its scopes by reaching for the
    ///         grains. The platform can do it now, and this is where that is proved against the
    ///         processes that ship.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here writes a role tuple, and the absence is the assertion.</b> The old
    ///         <c>GrantAsync</c> wrote <c>resourceGroup:{sub}-{rg}#owner@user:{subject}</c> directly,
    ///         with remarks calling it <i>"the seeding a real deployment does when a subscription is
    ///         created"</i>. It is not seeding any more: the only grant in this file is on the
    ///         <b>tenant</b>, and the two calls below are authorized through
    ///         <c>From("parent", "owner")</c> — the <c>subscription#parent@tenant</c> edge the first
    ///         call writes is what makes the second one legal. Step 3 of the widget's <c>PUT</c> then
    ///         checks <c>write</c> on the group and resolves four hops up to that same tuple.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="IScopeManager" /> is resolved from the gateway's container</b>, for the
    ///         reason <see cref="IResourceManager" /> is: it is the object graph
    ///         <c>GatewayComposition.BuildAsync</c> produces, with the real
    ///         <c>ReBacScopeAuthorizer</c> and the real <c>ReBacScopeRelationWriter</c> over the real
    ///         schema, against grains living in the two silo processes.
    ///     </para>
    /// </remarks>
    async Task CreateScopesThroughTheRealManagerAsync(CancellationToken cancellationToken) {
        var scopes = gateway.Services.GetRequiredService<IScopeManager>();

        var subscription = await scopes.CreateAsync(
            new() {
                Path = ScopeId.Subscription(Tenant, Subscription).Path,
                Body = """{"displayName":"phase 1"}""",
                Caller = Caller
            },
            cancellationToken
        );

        subscription.IsSuccess.ShouldBeTrue(
            "the scope path refused a subscription the tenant's owner is entitled to create: "
            + $"{subscription.Error?.Code} — {subscription.Error?.Message}. A ResourceNotFound here "
            + "is the enforcement seam answering for a check that could not be made — the tenant "
            + "owner tuple, or the silo's CyberCloud.Authorization reference."
        );

        subscription.GetValueOrThrow()
            .Created.ShouldBeTrue("a subscription nothing held cannot already exist.");

        var group = await scopes.CreateAsync(
            new() {
                Path = ScopeId.Group(Tenant, Subscription, ResourceGroup).Path,
                Body = """{"location":"eu-central"}""",
                Caller = Caller
            },
            cancellationToken
        );

        group.IsSuccess.ShouldBeTrue(
            "the scope path refused a resource group in a subscription the same caller had just "
            + $"created: {group.Error?.Code} — {group.Error?.Message}. The only grant in this test "
            + "is on the tenant, so this is the subscription's parent edge failing to carry it down."
        );

        group.GetValueOrThrow().Created.ShouldBeTrue();
    }

    /// <summary>
    ///     Attaches the local k3s as the cluster the widget's body names.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It attaches and does <i>not</i> ping, and the reason is a real property of the
    ///         grain rather than an omission.</b> <c>ClusterConnectionGrain</c> is null-tenant, so
    ///         <c>EnsureCallerMayReach</c> is the only thing standing between two tenants — and it
    ///         admits the owning tenant's grains, other null-tenant grains and a platform operator,
    ///         and nothing else. An Orleans <i>client</i> is <c>CallerKind.Client</c> and is refused
    ///         with <c>ResourceNotFound</c>, so a test that pinged from here would be asserting
    ///         against a check that is working. The first <c>AttachAsync</c> is the one call that is
    ///         open, because it is what establishes the ownership every later check reads.
    ///     </para>
    ///     <para>
    ///         The consequence is that a kubeconfig this silo cannot resolve does not surface here.
    ///         It surfaces as the operation's own failure a minute later, with the resolver's sentence
    ///         in <c>OperationStatus.Error</c> — which <see cref="Diagnose" /> prints.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The credential reference is a <c>file:</c> URI and the silo is what decides
    ///         whether to honour it.</b> <c>LocalKubeconfigFiles</c> refuses any path outside the root
    ///         the AppHost configured, so this test cannot reach a kubeconfig the topology did not
    ///         write.
    ///     </para>
    /// </remarks>
    async Task AttachClusterAsync(CancellationToken cancellationToken) {
        var connection = topology.Client
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(Cluster));

        var attached = await connection.AttachAsync(
            new() {
                ClusterId = Cluster,
                OwningTenantId = Tenant,
                Kind = ClusterConnectionKind.Kubeconfig,
                CredentialRef = new Uri(KubeconfigPath).AbsoluteUri,
                Endpoint = $"https://127.0.0.1:{CyberCloudResources.K3sApiPort}",
                DisplayName = "the AppHost's k3s"
            }
        );

        attached.IsSuccess.ShouldBeTrue(
            $"the cluster could not be attached: {attached.Error?.Code} — {attached.Error?.Message}"
        );

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ── The two hosts this suite has to reach ────────────────────────────────────────────────────

    /// <summary>
    ///     Builds the <b>real</b> gateway, pointed at the AppHost's cluster.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>GatewayComposition.BuildAsync</c> is the method <c>CyberCloud.Gateway.Host</c>'s own
    ///     <c>Program.cs</c> calls, so the <see cref="IResourceManager" /> resolved from it is the one
    ///     that ships: the same registry built from the same fourteen provider modules, the same
    ///     <c>ReBacResourceAuthorizer</c>, the same <c>ResourceScopeLockResolver</c>. A test that
    ///     constructed <c>ResourceManagerService</c> itself would be choosing its own seams, which is
    ///     what every other suite does and what this one exists not to do.
    /// </remarks>
    static Task<WebApplication> BuildGatewayAsync() =>
        GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture)
            ]
        );

    /// <summary>Where the AppHost's k3s wrote its kubeconfig.</summary>
    static string KubeconfigPath { get; } =
        Path.Combine(TestPaths.AppHostDirectory, ".k3s", "kubeconfig.yaml");

    /// <summary>
    ///     Waits for k3s and returns a raw client over it.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ Nothing in the AppHost waits on k3s — ADR-001 makes it a data plane rather than a
    ///     start-up dependency — so this suite waits for it itself, and says so when it never arrives
    ///     rather than failing later inside a reconcile.
    /// </remarks>
    static async Task<IKubernetes> ConnectToK3sAsync(CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();
        Exception? last = null;

        while (clock.Elapsed < ClusterBudget) {
            if (File.Exists(KubeconfigPath)) {
                try {
                    var client = new k8s.Kubernetes(
                        await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(
                            new FileInfo(KubeconfigPath)
                        )
                    );

                    _ = await client.CoreV1.ListNamespaceAsync(limit: 1, cancellationToken: cancellationToken);

                    return client;
                } catch (Exception notYet) when (notYet is not OperationCanceledException) {
                    last = notYet;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException(
            $"The AppHost's k3s did not answer within {ClusterBudget.TotalMinutes:F0} minutes. "
            + $"Kubeconfig: '{KubeconfigPath}' (exists: {File.Exists(KubeconfigPath)}). "
            + $"Last error: {last?.GetType().Name}: {last?.Message}",
            last
        );
    }

    /// <summary>The tenant id in the form <c>Orleans.Multitenant</c> keys on.</summary>
    static string Tenancy(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);
}
