using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The RabbitMQ reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The file exists a third time for a third reconciler in one assembly, and that is not
///     duplication for its own sake.</b> Clause 2 is a property of an <i>instance</i>: a singleton
///     <c>KafkaClusterReconciler</c> and a singleton <c>NatsClusterReconciler</c> being stateless say
///     nothing about a singleton <c>RabbitmqClusterReconciler</c>, and the container registers all
///     three separately by concrete type. What IS shared is the harness —
///     <c>RecordingConnection</c>, <c>FixedClock</c>, <c>NullLog</c> and
///     <c>ReconcilerWithAReadonlyCache</c> live in <c>KafkaReconcilerTests.cs</c> and are reused here
///     rather than copied.
/// </remarks>
public sealed class RabbitmqReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new RabbitmqClusterReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES A READONLY CACHE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process. A `readonly` dictionary caching the
        // last rendered additionalConfig passes ReconcilerConformance.CheckNoHiddenState — its own
        // remarks say the check skips `IsInitOnly` — passes every single-tenant test, and hands
        // tenant B tenant A's default queue type in production. The structural check above has been
        // confirmed blind to this shape four times; this is the belt.
        var reconciler = new RabbitmqClusterReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a cluster `events` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("events", TenantA, SubscriptionA);
        var bob = Address("events", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            RabbitmqClusters.Body(ClusterId, nodes: 3, storageSize: "20Gi", defaultQueueType: "quorum")
        );

        // ⚠ Bob differs on the field a cache would most plausibly be added for: `additionalConfig` is
        // the only value on this type that the provider BUILDS a string for rather than copying out
        // of the body, so it is the one somebody memoises.
        using var bobBody = JsonDocument.Parse(
            RabbitmqClusters.Body(ClusterId, nodes: 5, storageSize: "80Gi", defaultQueueType: "classic")
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var aliceSpec = Spec(Stored(connection, alice));
        var bobSpec = Spec(Stored(connection, bob));

        aliceSpec["replicas"]!.GetValue<int>().ShouldBe(3, "tenant A's node count came back as tenant B's");
        bobSpec["replicas"]!.GetValue<int>().ShouldBe(5, "tenant B's node count came back as tenant A's");

        aliceSpec["persistence"]!["storage"]!.GetValue<string>().ShouldBe("20Gi");
        bobSpec["persistence"]!["storage"]!.GetValue<string>().ShouldBe("80Gi");

        // ⚠ AND THE CONFIG FRAGMENT, WHICH IS THE ONE THAT WOULD ACTUALLY HURT. A tenant who asked for
        // quorum queues and got a neighbour's `classic` has a cluster that replicates nothing, and
        // nothing anywhere reports an error — the broker starts, the queues declare, and one node
        // dying loses them.
        aliceSpec["rabbitmq"]!["additionalConfig"]!.GetValue<string>()
            .ShouldContain("default_queue_type = quorum", Case.Sensitive);

        aliceSpec["rabbitmq"]!["additionalConfig"]!.GetValue<string>()
            .ShouldNotContain("default_queue_type = classic", Case.Sensitive);

        bobSpec["rabbitmq"]!["additionalConfig"]!.GetValue<string>()
            .ShouldContain("default_queue_type = classic", Case.Sensitive);
    }

    // ── Clause 4 ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRenderedObjectIsAlsoReadBack() {
        // ⚠ ONE OBJECT, SO THIS IS CHEAPER THAN THE NATS VERSION AND STILL WORTH HAVING. The claim is
        // set equality between what was applied and what was read back — an object rendered and not
        // read back is one the loop reports Converged without ever having observed, which is what
        // clause 4 exists to forbid, and one read back and never rendered is a resource that never
        // converges. The count is asserted too, so a second object added to the renderer without a
        // matching read is caught rather than absorbed.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target)).ToHashSet(
            StringComparer.Ordinal
        );

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        read.ShouldBe(
            applied,
            "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
            + ". An object rendered and not read back is one the loop reports Converged without "
            + "ever having observed."
        );

        applied.Count.ShouldBe(1, "the object count changed and this test's expectation did not.");
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and on THIS operator that is not a hypothetical: it ships a
        // MutatingWebhookConfiguration and a ValidatingWebhookConfiguration, both with
        // failurePolicy: Fail, both in the released bundle. A reconciler that trusted the apply's own
        // result would report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. ⚠ The value that matters here is spec.rabbitmq.additionalConfig: it is the only
        // one on this type that the provider BUILDS a string for rather than copying out of the body,
        // so it is the only place a stray timestamp, counter or set iteration order could get in. The
        // plugin list is sorted for exactly that reason.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);
        var first = connection.Applied.Select(x => x.Body).ToArray();

        await Reconcile(connection, body.RootElement);
        var second = connection.Applied.Skip(first.Length).Select(x => x.Body).ToArray();

        second.ShouldBe(first);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections. A tenant whose cluster is down has a resource that is
        // still coming, not one that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.image` is the plausible one
        // on THIS type and it is not the tenant's doing: the operator's own mutating webhook writes
        // that field when it is unset, and the operator's controller writes metadata annotations. So
        // a conflict here is the likeliest of the three rows in this namespace to be a genuine
        // two-writer situation, and forcing would restart an argument with the operator every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.image" };
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("image");
    }

    [Fact]
    public async Task TheTeardownIssuesTheDeleteAndOnlyReportsGoneAfterAReadSaysSo() {
        // ⚠ THE FINALIZER IS WHY THIS IS NOT A FORMALITY. The operator adds
        // `deletion.finalizers.rabbitmqclusters.rabbitmq.com`, so the object survives the delete call
        // until the controller removes it. A teardown that reported Converged on the delete's own
        // result would tell the platform the cluster was gone while the StatefulSet, the PVCs and the
        // credential Secret were all still there — and the resource would stop being billed while
        // still consuming.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new RabbitmqClusterReconciler(new FixedClock());
        var gone = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        gone.ShouldBe(ReconcileOutcome.Converged);

        connection.Deleted.Select(x => x.Kind.Kind + "/" + x.Name)
            .ShouldContain("RabbitmqCluster/observed");
    }

    // ── The three ports docs/plan/12 cares about, and the one that must never be routable ────────

    [Fact]
    public async Task NothingThisTypeRendersAsksForAnExternalAddress() {
        // ⚠ FAILURE CLASS (c) AT THE OBJECT LEVEL, INVERTED. The sibling types assert that external
        // exposure is off unless asked for and that its allow-list is never absent. This type has no
        // such setting AT ALL, and the reason is that the operator's client Service carries AMQP
        // 5672, the management UI 15672 and Prometheus 15692 together while `spec.service.type` is
        // one enum over the whole Service — so asking for a public AMQP endpoint would publish a
        // management UI as a side effect. docs/plan/12's RabbitMQ row forbids exactly that.
        //
        // The assertion is that the rendered spec touches NEITHER of the two fields that could do it.
        // A future author adding `external.enabled` would have to delete this test, which is the
        // point: the decision is recorded where it would be undone.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(RabbitmqClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var spec = Spec(Stored(connection, Address("observed", TenantA, SubscriptionA)));

        spec.ContainsKey("service").ShouldBeFalse(
            "spec.service is written, and the only value of it this type could want is the CRD's own "
            + "default of {type: ClusterIP}. Writing it takes permanent ownership of the field under "
            + "server-side apply, and the only other value is a LoadBalancer this row declines."
        );

        spec.ContainsKey("override").ShouldBeFalse(
            "spec.override is written. That is the only route to loadBalancerSourceRanges on this "
            + "CRD, and it is a strategic-merge patch against a corev1.ServiceSpec — see "
            + "charts/managed/rabbitmq/conformance.yaml § owed, external-exposure-moves-three-ports."
        );
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000009");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new RabbitmqClusterReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        RabbitmqClusterReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                RabbitmqClusters.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(IKubeClusterConnection? connection, JsonElement desired) {
        var address = Address("observed", TenantA, SubscriptionA);

        return new(
            address,
            RabbitmqClusters.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            RabbitmqClusters.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static string Stored(RecordingConnection connection, ResourceId address) =>
        connection.Objects[
            RecordingConnection.Key(
                RabbitmqClusters.ClusterRef(ReconcileDriver.NamespaceFor(address), address.Name)
            )
        ];

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}
