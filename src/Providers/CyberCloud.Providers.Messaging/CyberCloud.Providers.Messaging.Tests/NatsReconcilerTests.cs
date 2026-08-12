using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The NATS reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The whole file exists a second time for a second reconciler in one assembly, and that is
///     not duplication for its own sake.</b> Clause 2 is a property of an <i>instance</i>: a
///     singleton <c>KafkaClusterReconciler</c> being stateless says nothing about a singleton
///     <c>NatsClusterReconciler</c>, and the container registers the two separately by concrete type.
///     What IS shared is the harness — <c>RecordingConnection</c>, <c>FixedClock</c>,
///     <c>NullLog</c> and <c>ReconcilerWithAReadonlyCache</c> live in <c>KafkaReconcilerTests.cs</c>
///     and are reused here rather than copied.
/// </remarks>
public sealed class NatsReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new NatsClusterReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES A READONLY CACHE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process. A `readonly` dictionary caching the
        // last rendered config passes ReconcilerConformance.CheckNoHiddenState — its own remarks say
        // the check skips `IsInitOnly` — passes every single-tenant test, and hands tenant B tenant
        // A's route list in production.
        //
        // KafkaReconcilerTests pins that blind spot against a counter-example; this is the belt.
        var reconciler = new NatsClusterReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a cluster `events` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("events", TenantA, SubscriptionA);
        var bob = Address("events", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 3, storageSize: "10Gi"));
        using var bobBody = JsonDocument.Parse(NatsClusters.Body(ClusterId, servers: 5, storageSize: "50Gi"));

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var aliceSet = Spec(Stored(connection, alice, NatsClusters.StatefulSetRef));
        var bobSet = Spec(Stored(connection, bob, NatsClusters.StatefulSetRef));

        aliceSet["replicas"]!.GetValue<int>().ShouldBe(3, "tenant A's server count came back as tenant B's");
        bobSet["replicas"]!.GetValue<int>().ShouldBe(5, "tenant B's server count came back as tenant A's");

        // ⚠ And the ConfigMap, which is the object a cache would most plausibly be added for — it is
        // the one this provider BUILDS a string for rather than assembling from the body directly.
        Config(connection, alice).ShouldContain("events-2." + NatsClusters.HeadlessServiceName("events"));
        Config(connection, alice).ShouldNotContain("events-3.");
        Config(connection, bob).ShouldContain("events-4." + NatsClusters.HeadlessServiceName("events"));
    }

    // ── Clause 4, and the pairing that makes it mean anything ────────────────────────────────────

    [Fact]
    public async Task EveryRenderedObjectIsAlsoReadBack() {
        // ⚠ THE TWO LISTS IN THE RECONCILER ARE HAND-MAINTAINED AND NOTHING BUT THIS HOLDS THEM
        // TOGETHER. `Rendered` says what to apply and `Targets` says what to read back. An object in
        // the first and not the second is one the loop reports Converged without ever having observed
        // — which is precisely what clause 4 exists to forbid — and one in the second and not the
        // first is a resource that never converges at all.
        //
        // ⚠ BOTH SETTINGS OF monitoring.enabled, because the PodMonitor is the conditional object and
        // a pairing test on the default body alone would prove nothing about the branch.
        foreach (var monitoring in new[] { true, false }) {
            var connection = new RecordingConnection();
            using var body = JsonDocument.Parse(WithMonitoring(NatsClusters.Body(ClusterId), monitoring));

            var outcome = await Reconcile(connection, body.RootElement);

            outcome.ShouldBe(ReconcileOutcome.Converged, $"monitoring.enabled = {monitoring}");

            var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target)).ToHashSet(
                StringComparer.Ordinal
            );

            var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

            // ⚠ SET EQUALITY IN BOTH DIRECTIONS, WHICH IS THE ONLY FORM THAT CATCHES EITHER MISTAKE.
            // Asserting a COUNT would have gone green on a Targets list missing the PodMonitor — the
            // applies all still happen, the outcome is still Converged, and nothing else in this file
            // would notice.
            read.ShouldBe(
                applied,
                "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
                + ". An object rendered and not read back is one the loop reports Converged without "
                + "ever having observed."
            );

            applied.Count.ShouldBe(
                monitoring ? 5 : 4,
                "the object count changed and this test's expectation did not."
            );
        }
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. ⚠ The object that matters here is the ConfigMap: it is the only one whose body is
        // a STRING this provider builds rather than a document it assembles, so it is the only one a
        // stray timestamp, counter or digest could get into. A config-digest annotation on the pod
        // template — the usual Helm idiom, deliberately not written — would break exactly this.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

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
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.replicas` is the plausible
        // one on this type: a StatefulSet declares a `scale` subresource, so any autoscaler the
        // tenant runs can own that field. Forcing would silently undo it every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.replicas" };
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("replicas");
    }

    // ── The order, which is load-bearing at RUNTIME rather than at admission ──────────────────────

    [Fact]
    public async Task TheConfigAndTheHeadlessServiceAreAppliedBeforeTheStatefulSetAndTornDownAfterIt() {
        // ⚠ KUBERNETES REFUSES NONE OF THESE ORDERS, WHICH IS WHY IT NEEDS A TEST. A StatefulSet
        // applied first is accepted and creates pods that crash-loop on a missing ConfigMap, or — far
        // worse — come up as N standalone servers because no peer's DNS record exists. A cluster of
        // three standalone servers reports three healthy servers.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(NatsClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var applied = connection.Applied.Select(x => x.Target.Kind.Kind + "/" + x.Target.Name).ToArray();

        applied[0].ShouldBe("ConfigMap/" + NatsClusters.ConfigMapName("observed"));
        applied[1].ShouldBe("Service/" + NatsClusters.HeadlessServiceName("observed"));
        applied[2].ShouldBe("StatefulSet/observed");

        var reconciler = new NatsClusterReconciler(new FixedClock());
        await reconciler.DeleteAsync(Context(connection, body.RootElement), TestContext.Current.CancellationToken);

        var deleted = connection.Deleted.Select(x => x.Kind.Kind + "/" + x.Name).ToArray();

        Array.IndexOf(deleted, "StatefulSet/observed")
            .ShouldBeLessThan(
                Array.IndexOf(deleted, "ConfigMap/" + NatsClusters.ConfigMapName("observed")),
                "the ConfigMap was removed before the StatefulSet that mounts it, so a pod that was "
                + "still terminating lost the configuration it was running on."
            );
    }

    // ── Failure class (c) at the object level: exposure is never accidental ──────────────────────

    [Fact]
    public async Task ExternalExposureIsOffUnlessAskedForAndItsAllowListIsNeverAbsent() {
        var off = new RecordingConnection();
        using var plain = JsonDocument.Parse(NatsClusters.Body(ClusterId));
        await Reconcile(off, plain.RootElement);

        var closed = Spec(Stored(off, Address("observed", TenantA, SubscriptionA), NatsClusters.ClientServiceRef));

        closed["type"]!.GetValue<string>().ShouldBe("ClusterIP");
        closed["loadBalancerSourceRanges"].ShouldBeNull();

        var on = new RecordingConnection();
        using var exposed = JsonDocument.Parse(WithExternal(NatsClusters.Body(ClusterId), true));
        await Reconcile(on, exposed.RootElement);

        var open = Spec(Stored(on, Address("observed", TenantA, SubscriptionA), NatsClusters.ClientServiceRef));

        open["type"]!.GetValue<string>().ShouldBe("LoadBalancer");

        // ⚠ PRESENT AND EMPTY, NOT ABSENT. An absent loadBalancerSourceRanges means "from anywhere".
        // An unfinished allow-list must not be the configuration that opens a broker to the internet
        // — docs/plan/12 § Cross-cutting decisions says so in as many words.
        open["loadBalancerSourceRanges"].ShouldNotBeNull(
            "external exposure with an empty allow-list rendered no source ranges at all, which is a "
            + "load balancer that accepts every address on the internet."
        );

        open["loadBalancerSourceRanges"]!.AsArray().Count.ShouldBe(0);

        // ⚠ AND THE ROUTE PORT IS ON NEITHER. 6222 behind a public address is an unauthenticated
        // route, which is a second server joining the cluster rather than a client connecting to it.
        foreach (var spec in new[] { closed, open }) {
            spec["ports"]!.AsArray()
                .Select(x => x!["port"]!.GetValue<int>())
                .ShouldNotContain(NatsClusters.ClusterPort);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new NatsClusterReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        NatsClusterReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                NatsClusters.V2026,
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
            NatsClusters.V2026,
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
            NatsClusters.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static string Stored(
        RecordingConnection connection,
        ResourceId address,
        Func<string, string, ObjectRef> reference
    ) =>
        connection.Objects[
            RecordingConnection.Key(reference(ReconcileDriver.NamespaceFor(address), address.Name))
        ];

    static string Config(RecordingConnection connection, ResourceId address) =>
        JsonNode.Parse(Stored(connection, address, NatsClusters.ConfigMapRef))!["data"]![
            NatsClusters.ConfigKey
        ]!.GetValue<string>();

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    /// <summary>A body with external exposure turned on and no source ranges.</summary>
    static string WithExternal(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["external"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }

    static string WithMonitoring(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["monitoring"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }
}
