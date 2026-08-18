using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     The reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class KafkaReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new KafkaClusterReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckCatchesAReadonlyMutableCache() {
        // ⚠ CALIBRATION, AND IT NOW POINTS THE OTHER WAY. This test used to assert that
        // CheckNoHiddenState MISSED the counter-example below, because it skipped every
        // `field.IsInitOnly` — and `readonly` stops the FIELD being reassigned while stopping
        // nothing about the dictionary, so a per-tenant cache passed clause 2 while accumulating
        // state on a singleton every tenant shares. Seven families each pinned that blind spot
        // and it is now closed; this is what holds it closed.
        //
        // ⚠ THE CROSS-TENANT TEST BELOW STAYS, AND IS NOT MADE REDUNDANT BY THIS. This one reads
        // a field's declared TYPE. That one drives ONE reconciler instance through TWO tenants and
        // compares what each got, which is the only way to catch mixing no field type could show.
        var findings = ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache());

        findings.ShouldContain(
            x => x.Clause == ReconcilerClause.NoHiddenState,
            "a readonly field holding a mutable Dictionary is state on a shared singleton, and the "
            + "structural check is what catches it before the behavioural test has to"
        );

        findings.ShouldContain(x => x.Detail.Contains("lastRendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES THE CACHE ABOVE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process. A `readonly` dictionary caching the
        // last rendered spec passes the structural check, passes every single-tenant test, and hands
        // tenant B tenant A's retention window in production.
        //
        // So: one instance, two tenants, two different bodies, interleaved, and both worlds checked.
        var reconciler = new KafkaClusterReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a cluster `events` is the
        // ordinary case, not an edge one — the namespaces differ and nothing else does.
        // ⚠ Each tenant brings its OWN subscription, and it has to: ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT, so two tenants sharing
        // a subscription id would share a namespace and this test would fail for the harness's reason
        // rather than the reconciler's.
        var alice = Address("events", TenantA, SubscriptionA);
        var bob = Address("events", TenantB, SubscriptionB);

        var world = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(KafkaClusters.Body(ClusterId, nodes: 3, storageSize: "10Gi"));
        using var bobBody = JsonDocument.Parse(KafkaClusters.Body(ClusterId, nodes: 5, storageSize: "50Gi"));

        // Interleaved on purpose: A, B, A. A reconciler that remembered anything from its first pass
        // would answer the third pass with B's values.
        await Pass(reconciler, world, alice, aliceBody.RootElement);
        await Pass(reconciler, world, bob, bobBody.RootElement);
        var third = await Pass(reconciler, world, alice, aliceBody.RootElement);

        third.IsConverged.ShouldBeTrue(third.ToString());

        var pools = world.Applied.Where(x => x.Target.Kind.Kind == "KafkaNodePool").ToList();

        pools.Count.ShouldBe(3);

        Spec(pools[0].Body)["replicas"]!.GetValue<int>().ShouldBe(3);
        Spec(pools[1].Body)["replicas"]!.GetValue<int>().ShouldBe(5);
        Spec(pools[2].Body)["replicas"]!.GetValue<int>().ShouldBe(3);

        pools[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        pools[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        pools[2].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's Kafka rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        pools[0].Target.Namespace.ShouldNotBe(pools[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A Failed here would end the operation and strand a half-built broker cluster.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced
        // apply. On this type the other manager is plausibly an autoscaler writing the node pool's
        // `spec.replicas` through the `scale` subresource Strimzi's CRD declares over it.
        var connection = new RecordingConnection { ConflictField = ".spec.replicas" };
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".spec.replicas");
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. Both applies succeed and the reads find nothing — a reconciler that
        // believed its own applies would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. Both applies are server-side, so a second pass is an Unchanged — and unlike the
        // PostgreSQL provider there is no conditional teardown here, so the steady state is exactly
        // two applies and two reads however many times the reminder fires.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        var afterFirst = connection.Objects.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Objects.Count.ShouldBe(afterFirst.Count);
        foreach (var (key, body) in afterFirst) {
            connection.Objects[key].ShouldBe(body, $"the second pass rewrote '{key}'");
        }

        connection.Deleted.ShouldBeEmpty("a converged pass deleted something");
    }

    // ── Failure class (c): the seven labels, and the one that is not one of them ──────────────────

    [Fact]
    public async Task BothObjectsCarryTheSevenLabelsAndOnlyThePoolCarriesTheStrimziBinding() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied.Select(x => x.Target.Kind.Kind).ShouldBe(KafkaThenNodePool);

        foreach (var command in connection.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, command.Target.ToString());
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            command.Labels[KubeLabels.ResourceType].ShouldBe("cybercloud.messaging_kafkaclusters");
        }

        // ⚠ THE EIGHTH LABEL, ON EXACTLY ONE OBJECT. A KafkaNodePool whose `strimzi.io/cluster` does
        // not name an existing Kafka is silently ignored by the operator — no event, no status, no
        // pods — so its absence is the failure mode that looks most like "the cluster is still
        // coming". And it is NOT on the Kafka: a Kafka labelled as a member of itself would be
        // harmless and meaningless, and a provider that sprayed operator labels onto every object it
        // touched is one that would eventually spray one that is not harmless.
        var pool = connection.Applied.Single(x => x.Target.Kind.Kind == "KafkaNodePool");
        var kafka = connection.Applied.Single(x => x.Target.Kind.Kind == "Kafka");

        pool.Labels[KafkaClusters.ClusterLabel].ShouldBe("observed");
        kafka.Labels.ShouldNotContainKey(KafkaClusters.ClusterLabel);

        // ⚠ And the two KRaft annotations, on exactly the Kafka. Without them the operator builds a
        // ZooKeeper-based cluster from a manifest that never mentions ZooKeeper, and reads replica
        // count and storage off a `spec.kafka` that does not carry them.
        kafka.Annotations[KafkaClusters.KraftAnnotation].ShouldBe(KafkaClusters.Enabled);
        kafka.Annotations[KafkaClusters.NodePoolsAnnotation].ShouldBe(KafkaClusters.Enabled);
    }

    [Fact]
    public async Task TheKafkaIsAppliedBeforeTheNodePoolAndTornDownInTheOtherOrder() {
        // ⚠ Order, both ways, and it is load-bearing in a way the PostgreSQL provider's is not. A
        // pool applied first names a Kafka that does not exist, which the operator IGNORES rather
        // than reports; a Kafka deleted first leaves the operator reconciling a pool whose cluster is
        // gone.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        connection.Applied.Select(x => x.Target.Kind.Kind).ShouldBe(KafkaThenNodePool);

        var torn = await new KafkaClusterReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue(torn.ToString());
        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(NodePoolThenKafka);
    }

    [Fact]
    public async Task ExternalExposureIsOffUnlessAskedForAndItsAllowListIsNeverAbsent() {
        // docs/plan/12 § Cross-cutting decisions: "External exposure is never the default and the API
        // requires an explicit CIDR list — a managed database on a public IP with a weak password is
        // the single most common cloud breach".
        var connection = new RecordingConnection();
        using var plain = JsonDocument.Parse(KafkaClusters.Body(ClusterId));

        await Reconcile(connection, plain.RootElement);

        var listeners = Listeners(connection.Applied.Single(x => x.Target.Kind.Kind == "Kafka").Body);
        listeners.Count.ShouldBe(1, "a body that did not ask for external exposure got some");

        // And with it on but the allow-list empty: a load balancer that accepts NOTHING, never one
        // that accepts everything. An absent `loadBalancerSourceRanges` means "from anywhere".
        var open = new RecordingConnection();
        using var exposed = JsonDocument.Parse(WithExternal(KafkaClusters.Body(ClusterId), enabled: true));

        await Reconcile(open, exposed.RootElement);

        var second = Listeners(open.Applied.Single(x => x.Target.Kind.Kind == "Kafka").Body);
        second.Count.ShouldBe(2);

        var external = second[1]!.AsObject();
        external["tls"]!.GetValue<bool>().ShouldBeTrue("a public listener must be TLS whatever the body says");
        external["configuration"]!["loadBalancerSourceRanges"].ShouldNotBeNull(
            "the key is absent, which Kubernetes reads as 'from anywhere' — the exact opposite of an "
            + "unfinished allow-list's safe reading."
        );
        external["configuration"]!["loadBalancerSourceRanges"]!.AsArray().Count.ShouldBe(0);
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The apply order a pass must keep — the pool's label names the Kafka.</summary>
    static readonly string[] KafkaThenNodePool = ["Kafka", "KafkaNodePool"];

    /// <summary>The teardown order — the referrer before the referent.</summary>
    static readonly string[] NodePoolThenKafka = ["KafkaNodePool", "Kafka"];

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new KafkaClusterReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        KafkaClusterReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                KafkaClusters.V2026,
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
            KafkaClusters.V2026,
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
            KafkaClusters.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    static JsonArray Listeners(string kafkaJson) => Spec(kafkaJson)["kafka"]!["listeners"]!.AsArray();

    /// <summary>A body with external exposure turned on and no source ranges.</summary>
    static string WithExternal(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["external"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }
}

/// <summary>
///     ⚠ <b>Not a reconciler anybody ships — the counter-example the blind-spot test names.</b>
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, so <c>CheckNoHiddenState</c> skips it, and the
///     dictionary it holds is mutable forever. This is the shape a per-tenant cache takes when
///     somebody adds one for performance, and the only test in this file that would catch it is the
///     cross-tenant one.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => KafkaClusters.Type;

    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        lastRendered[context.Id.Name] = context.Desired.GetRawText();
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ReconcileOutcome.Converged);

    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ObservedState.Absent);
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>
    ///     Every object <i>read</i>, in order — clause 4's evidence.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Added for the provider whose rendered object set is a list rather than a pair.</b> A
    ///     reconciler that applies N objects and reads back fewer reports <c>Converged</c> for
    ///     objects it never observed, which is exactly what clause 4 forbids — and every other
    ///     assertion in this file goes green on it, because the applies all happened. Comparing this
    ///     against <see cref="Applied" /> is the only thing that catches it.
    /// </remarks>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = command.Body;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. This provider applies two objects, so a key without
    ///     the kind would let the pool overwrite the Kafka; and the namespace is in it because the
    ///     cross-tenant test puts the same resource name in two tenants, which is the only shape in
    ///     which one singleton reconciler serving both can be caught mixing them.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}
