using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The collector reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     The doubles are <c>MonitorReconcilerTests</c>' — <c>RecordingConnection</c>, <c>FixedClock</c>,
///     <c>NullLog</c> — shared within this assembly because both reconcilers are this family's.
/// </remarks>
public sealed class CollectorReconcilerTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionA = Guid.Parse("33333333-3333-4333-8333-333333333333");
    static readonly Guid SubscriptionB = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public void TheReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new MonitorCollectorReconciler(new FixedClock())).ShouldBeEmpty();

    [Fact]
    public async Task APassAppliesTheThreeObjectsInOrderAndReadsAllThreeBack() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Reason);

        // ⚠ Configuration, collector, address — the order is about the message a tenant gets when
        // the second lands before the first: CreateContainerConfigError reads as a broken image.
        connection.Applied.Select(static x => x.Target.Kind.Kind).ShouldBe(["ConfigMap", "Deployment", "Service"]);

        // Clause 4: every object read back, none assumed from its apply.
        connection.Read.Select(static x => x.Kind.Kind).ShouldBe(["ConfigMap", "Deployment", "Service"]);

        // And the seven labels on every command — ADR-013, asserted on what was sent.
        foreach (var command in connection.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ContainsKey(label).ShouldBeTrue($"{command.Target} lacks {label}");
            }
        }
    }

    [Fact]
    public async Task ASwallowedApplyIsInProgressAndNeverConverged() {
        // ⚠ THE CLAUSE-4 TRAP. An apply that reports success and stores nothing is what a silently
        // refused admission looks like; a reconciler judging on the apply's own result would report a
        // collector that does not exist as converged.
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("not readable back yet");
    }

    [Fact]
    public async Task BothReceiversOffIsRefusedBeforeAnythingIsApplied() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, false, false));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        // ⚠ Failed and not InProgress: nothing about a body with both receivers off becomes true by
        // waiting, and a retry every ten seconds for an hour would be the default's answer.
        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldContain("Both receivers are off");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASuspendedClusterIsInProgressAndNotAFailure() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ANullClusterIsARefusalOnReconcileAndConvergedOnDelete() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));

        (await reconciler.ReconcileAsync(Context(null, body.RootElement), TestContext.Current.CancellationToken))
            .Kind.ShouldBe(ReconcileOutcomeKind.Failed);

        // ⚠ Converged, not Failed — a teardown with no cluster to reach has nothing left to remove.
        (await reconciler.DeleteAsync(Context(null, body.RootElement), TestContext.Current.CancellationToken))
            .Kind.ShouldBe(ReconcileOutcomeKind.Converged);
    }

    [Fact]
    public async Task ADeleteRemovesTheAddressFirstAndConvergesWhenAllThreeAreGone() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var context = Context(connection, body.RootElement);

        (await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken)).Kind.ShouldBe(
            ReconcileOutcomeKind.Converged
        );
        connection.Objects.Count.ShouldBe(3);

        var outcome = await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Reason);
        connection.Objects.ShouldBeEmpty();

        // The reverse of the apply order: a workload stops resolving the collector before the pods
        // behind it disappear.
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["Service", "Deployment", "ConfigMap"]);
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ The test a single-tenant test cannot be. One singleton serves every tenant in a silo.
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("gateway", "prod", TenantA, SubscriptionA);
        var bob = Address("gateway", "prod", TenantB, SubscriptionB);

        using var aliceBody = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, replicas: 1));
        using var bobBody = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, replicas: 3, otlpGrpc: false));

        foreach (var (address, desired) in new[] {
                     (alice, aliceBody), (bob, bobBody), (alice, aliceBody), (bob, bobBody)
                 }) {
            (await reconciler.ReconcileAsync(
                    Context(connection, desired.RootElement, address),
                    TestContext.Current.CancellationToken
                ))
                .Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        }

        var deployments = connection.Applied.Where(static x => x.Target.Kind.Kind == "Deployment").ToList();
        deployments.Count.ShouldBe(4);

        Replicas(deployments[0].Body).ShouldBe(1);
        Replicas(deployments[1].Body).ShouldBe(3);
        Replicas(deployments[2].Body).ShouldBe(1, "tenant A's collector came back with tenant B's replica count");
        Replicas(deployments[3].Body).ShouldBe(3, "tenant B's collector came back with tenant A's replica count");

        deployments[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        deployments[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        deployments[0].Target.Namespace.ShouldNotBe(deployments[1].Target.Namespace);
    }

    [Fact]
    public async Task AConfigurationChangeRollsTheCollectorThroughTheHash() {
        // ⚠ A ConfigMap edit does not restart a pod; the hash on the pod template is what makes a
        // receiver change a rollout, and it is the field an obvious implementation leaves out.
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();

        using var both = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        using var httpOnly = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, false));

        (await reconciler.ReconcileAsync(
                Context(connection, both.RootElement),
                TestContext.Current.CancellationToken
            )).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        (await reconciler.ReconcileAsync(
                Context(connection, httpOnly.RootElement),
                TestContext.Current.CancellationToken
            )).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var hashes = connection.Applied
            .Where(static x => x.Target.Kind.Kind == "Deployment")
            .Select(static x => JsonNode.Parse(
                    x.Body
                )!["spec"]!["template"]!["metadata"]!["annotations"]![MonitorCollectors.ConfigChecksumAnnotation]!
                    .GetValue<string>()
            )
            .ToList();

        hashes.Count.ShouldBe(2);
        hashes[0].ShouldNotBe(hashes[1]);
    }

    [Fact]
    public async Task ObserveReportsDriftWhenTheServiceIsGoneAndAbsenceWhenTheDeploymentIs() {
        var reconciler = new MonitorCollectorReconciler(new FixedClock());
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var context = Context(connection, body.RootElement);

        (await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken)).Kind.ShouldBe(
            ReconcileOutcomeKind.Converged
        );

        var observe = new ObserveContext(
            context.Id,
            context.ApiVersion,
            context.Desired,
            context.Namespace,
            connection
        );

        (await reconciler.ObserveAsync(observe, TestContext.Current.CancellationToken)).Summary.ShouldContain(
            "as declared"
        );

        // ⚠ A collector whose Service was deleted is running and unreachable — drift, not absence.
        connection.Objects.TryRemove(
            RecordingConnection.Key(MonitorCollectors.ServiceRef(context.Namespace, context.Id)),
            out _
        )
            .ShouldBeTrue();
        var drifted = await reconciler.ObserveAsync(observe, TestContext.Current.CancellationToken);
        drifted.Exists.ShouldBeTrue();
        drifted.Summary.ShouldContain("drifted");

        connection.Objects.TryRemove(
            RecordingConnection.Key(MonitorCollectors.DeploymentRef(context.Namespace, context.Id)),
            out _
        )
            .ShouldBeTrue();
        (await reconciler.ObserveAsync(observe, TestContext.Current.CancellationToken)).Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task ListEndpointsIsAPureFunctionOfTheAddressAndReachesNothing() {
        var handler = new MonitorCollectorListEndpointsHandler();
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, false));
        var address = Address("gateway", "prod", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        var context = new ActionContext(
            address,
            MonitorWorkspaces.V2026,
            MonitorCollectors.ListEndpointsAction,
            body.RootElement,
            body.RootElement,
            ns,
            null,
            new InMemorySecretVault()
        );

        var response = JsonNode.Parse(
            (await handler.InvokeAsync(context, TestContext.Current.CancellationToken)).GetValueOrThrow()
        )!;

        response["otlpGrpcEndpoint"]!.GetValue<string>().ShouldBe(string.Empty);
        response["otlpHttpEndpoint"]!.GetValue<string>().ShouldBe($"http://collector-prod-gateway.{ns}.svc:4318");
        response["service"]!.GetValue<string>().ShouldBe($"collector-prod-gateway.{ns}.svc");
        response["workspace"]!.GetValue<string>().ShouldBe("prod");

        // And the response is what the declared response schema says leaves the platform.
        MonitorCollectors.ListEndpointsResponse.Validate(response.Deserialize<JsonElement>(), allowTags: false)
            .IsSuccess.ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        ResourceId? address = null
    ) {
        var id = address ?? Address("gateway", "prod", TenantA, SubscriptionA);
        var vault = new InMemorySecretVault();

        return new(
            id,
            MonitorWorkspaces.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(id),
            connection,
            vault,
            new NullLog()
        ) { SecretWriter = vault };
    }

    static ResourceId Address(string name, string workspace, Guid tenant, Guid subscription) =>
        new(tenant, subscription, "prod", MonitorCollectors.Type, name, Guid.NewGuid(), workspace);

    static int Replicas(string deploymentJson) => JsonNode.Parse(deploymentJson)!["spec"]!["replicas"]!.GetValue<int>();
}
