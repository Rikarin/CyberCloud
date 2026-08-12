using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     The managed-cluster reconciler against a connection that misbehaves in the ways a real
///     management cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with any other provider's tests however identical they look.
/// </remarks>
public sealed class ManagedClusterReconcilerTests {
    // ── Failure class (a): a readonly mutable field on the reconciler ────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new ManagedClusterReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckStillMissesAReadonlyMutableCache() {
        // ⚠ THE BLIND SPOT, PINNED AGAINST A COUNTER-EXAMPLE THAT SHOULD FAIL AND DOES NOT.
        // ReconcilerConformance.CheckNoHiddenState skips a field that is `readonly`, and a `readonly`
        // Dictionary is mutable forever — which is exactly the shape a per-tenant cache takes when
        // somebody adds one for performance. FIVE provider families have confirmed that only the
        // cross-tenant test below catches it; this is the sixth, and pinning it is what stops the next
        // test looking redundant.
        //
        // If somebody ever closes the hole, THIS test goes red and says where to delete the
        // now-unnecessary belt.
        ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache()).ShouldBeEmpty(
            "the structural check now catches a readonly mutable collection. That is an improvement — "
            + "delete this test and say so in ReconcilerConformance's remarks, which currently promise "
            + "the opposite."
        );
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES THE CACHE ABOVE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process.
        var reconciler = new ManagedClusterReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS, and each brings its OWN subscription, because
        // ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}` and the tenant id is not
        // in it — two tenants sharing a subscription would share a namespace and this test would fail
        // for the harness's reason rather than the reconciler's.
        var alice = Address("prod", TenantA, SubscriptionA);
        var bob = Address("prod", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            ManagedClusters.Body(ClusterId, version: "1.32", controlPlaneReplicas: 1)
        );

        using var bobBody = JsonDocument.Parse(
            ManagedClusters.Body(ClusterId, version: "1.33", controlPlaneReplicas: 5)
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var controlPlanes = connection.Applied
            .Where(x => x.Target.Kind.Kind == "KamajiControlPlane")
            .ToList();

        controlPlanes.Count.ShouldBe(4);

        Spec(controlPlanes[0].Body)["replicas"]!.GetValue<int>().ShouldBe(1);
        Spec(controlPlanes[1].Body)["replicas"]!.GetValue<int>().ShouldBe(5);
        Spec(controlPlanes[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(1, "tenant A's replica count came back as tenant B's");

        Spec(controlPlanes[3].Body)["version"]!.GetValue<string>()
            .ShouldBe("v1.33.4", "tenant B's Kubernetes version came back as tenant A's");

        controlPlanes[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        controlPlanes[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        controlPlanes[0].Target.Namespace.ShouldNotBe(controlPlanes[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ THREE OBJECTS MAKE THIS THE ASSERTION THAT MATTERS MOST, AND THE INFRASTRUCTURE ONE IS THE
        // ONE MOST LIKELY TO BE MISSED. It carries no tenant-facing spec at all, so a reconciler that
        // read back only the two objects with fields in them would report Converged for a cluster whose
        // infrastructureRef points at nothing — which Cluster API accepts, stores and never provisions.
        // Set equality in BOTH directions is the only form that catches either half going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target)).ToHashSet(StringComparer.Ordinal);
        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.Count.ShouldBe(3);
        read.ShouldBe(applied, ignoreOrder: true);
    }

    [Fact]
    public async Task TheTwoTemplatesAreAppliedBeforeTheClusterThatNamesThem() {
        // Cluster API resolves controlPlaneRef and infrastructureRef on every pass and reports an
        // unresolvable one as a condition. Applying the Cluster first converges either way and spends
        // the gap writing errors nobody asked for.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        connection.Applied.Select(x => x.Target.Kind.Kind)
            .ShouldBe(["KubevirtCluster", "KamajiControlPlane", "Cluster"]);
    }

    [Fact]
    public async Task TheThreeObjectsAreAppliedIntoThreeDifferentApiGroups() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        connection.Applied.Select(x => x.Target.Kind.Group)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(3);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);
        var first = connection.Applied.Select(x => x.Body).ToList();

        await Reconcile(connection, body.RootElement);
        var second = connection.Applied.Skip(3).Select(x => x.Body).ToList();

        second.ShouldBe(first);
    }

    // ── The half no other provider has ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AControlPlaneThatReportsNotReadyIsInProgressRatherThanConverged() {
        // ⚠ THE WHOLE POINT OF THIS TYPE. `Matches` says the request is in the management cluster;
        // whether the tenant has an API server is a different question, and docs/plan/09 budgets six to
        // nine minutes between the two answers. A reconciler that stopped at `Matches` would report
        // Succeeded for a cluster nobody can use — and would have nothing to put in the step list
        // docs/plan/24's M1 story asks for.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var address = Address("observed", TenantA, SubscriptionA);
        var target = ManagedClusters.ClusterRef(ReconcileDriver.NamespaceFor(address), "observed");

        connection.Objects[RecordingConnection.Key(target)] = WithReadyCondition(
            connection.Objects[RecordingConnection.Key(target)],
            ready: false,
            "Waiting for the first worker to join"
        );

        var log = new CollectingLog();
        var outcome = await Reconcile(connection, body.RootElement, log);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);

        // ⚠ AND THE MESSAGE IS UPSTREAM'S, which is what turns a spinner into a story.
        log.Entries.ShouldContain(x => x.Detail.Contains("first worker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AControlPlaneThatReportsReadyConverges() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var address = Address("observed", TenantA, SubscriptionA);
        var target = ManagedClusters.ClusterRef(ReconcileDriver.NamespaceFor(address), "observed");

        connection.Objects[RecordingConnection.Key(target)] = WithReadyCondition(
            connection.Objects[RecordingConnection.Key(target)],
            ready: true,
            "Cluster is ready"
        );

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task AClusterWhoseStatusNobodyEverWroteConvergesAndThatIsTheHoleThisTypeHas() {
        // ⚠ THE COUNTER-EXAMPLE, PINNED IN THE IDIOM THIS REPOSITORY USES FOR A KNOWN HOLE. An object
        // with no `status` at all has never been seen by a controller. In a management cluster with
        // Cluster API installed that state lasts seconds; where the CRDs exist and the controller is
        // dead it lasts forever, and the platform cannot tell the two apart from the object.
        //
        // ⚠ THE ALTERNATIVE IS NOT AVAILABLE: FakeKubeCluster echoes an apply back with no status and
        // the k3s harness installs a schema-less CRD stub with no controller behind it, so a reconciler
        // that refused to converge without a status could never converge in EITHER conformance suite —
        // and "a provider is not registered until it passes" would make this row unshippable.
        //
        // ⚠ WHAT KEEPS THE HOLE SMALL is that a management cluster with no Cluster API at all fails at
        // the APPLY, by name — see AnApiServerRefusalFailsRatherThanConverges below. The hole is
        // "installed but not running", not "not installed". If somebody closes it — an ObjectMatchesDesired
        // overload that can see a status, or a case-supplied status hook — THIS test goes red and points
        // at charts/managed/kubernetes/conformance.yaml § owed, `converged-is-not-ready`.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).ShouldBe(
            ReconcileOutcome.Converged,
            "the reconciler now refuses to converge without a Cluster API status. That is the better "
            + "answer — delete this test and the § owed item it guards, and check that both conformance "
            + "suites can still reach Succeeded."
        );
    }

    [Fact]
    public async Task AnApiServerRefusalFailsRatherThanConverges() {
        // ⚠ THIS IS WHAT KEEPS ManagedClusters.Readiness' HOLE SMALL. A management cluster with no
        // Cluster API CRDs answers the apply with a refusal, and the code decides — a request the API
        // server refused will be refused identically for the next hour.
        var connection = new RecordingConnection { RefuseWith = ErrorCode.InvalidResourceType };
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ⚠ ON THIS TYPE THERE IS A SECOND PARTY WITH A LEGITIMATE CLAIM. The Kamaji control-plane
        // provider PATCHES spec.controlPlaneEndpoint onto the KubevirtCluster this reconciler applies;
        // forcing would take that field back every pass and hand it to nobody.
        var connection = new RecordingConnection { ConflictField = "spec.controlPlaneEndpoint" };
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceAllThreeObjectsAreGone() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);
        connection.Objects.Count.ShouldBe(3);

        var outcome = await new ManagedClusterReconciler(new FixedClock()).DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.ShouldBe(ReconcileOutcome.Converged);
        connection.Objects.ShouldBeEmpty();

        // ⚠ THE Cluster FIRST, which is the reverse of the apply order and is not cosmetic: Cluster API
        // owns the teardown of everything it created from it, and deleting the pieces out from under it
        // leaves the controller reconciling references to objects that are gone.
        connection.Deleted.Select(x => x.Kind.Kind)
            .ShouldBe(["Cluster", "KamajiControlPlane", "KubevirtCluster"]);
    }

    [Fact]
    public async Task TheRenderedObjectsCarryNoSecretValueAnywhere() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var applied in connection.Applied) {
            foreach (var forbidden in new[] { "password", "kubeconfig", "stringData", "token" }) {
                applied.Body.ShouldNotContain(forbidden, Case.Insensitive, forbidden);
            }
        }
    }

    [Fact]
    public async Task ObserveReportsBothTheRequestAndTheProduct() {
        // ⚠ TWO ANSWERS IN ONE SUMMARY, because on this type they are genuinely different questions and
        // a tenant reading one would infer the other.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var address = Address("observed", TenantA, SubscriptionA);

        var observed = await new ManagedClusterReconciler(new FixedClock()).ObserveAsync(
            new(address, ManagedClusters.V2026, body.RootElement, ReconcileDriver.NamespaceFor(address), connection),
            TestContext.Current.CancellationToken
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldContain("carry the desired spec");
        observed.Summary.ShouldContain("no controller has written a status yet");
    }

    [Fact]
    public async Task DeletingTheControlPlaneBehindTheReconcilersBackIsDriftRatherThanAbsence() {
        // A Cluster whose control plane was deleted out from under it still exists and still answers;
        // what it has lost is the API server the tenant was sold.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var address = Address("observed", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        connection.Objects.TryRemove(
            RecordingConnection.Key(ManagedClusters.ControlPlaneRef(ns, "observed")),
            out _
        );

        var observed = await new ManagedClusterReconciler(new FixedClock()).ObserveAsync(
            new(address, ManagedClusters.V2026, body.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldBe("the cluster has drifted");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    internal static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");
    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    internal static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(
        RecordingConnection connection,
        JsonElement desired,
        IReconcileLog? log = null
    ) =>
        await new ManagedClusterReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired, log), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        ManagedClusterReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                ManagedClusters.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        IReconcileLog? log = null
    ) {
        var address = Address("observed", TenantA, SubscriptionA);

        return new(
            address,
            ManagedClusters.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            log ?? new NullLog()
        );
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    internal static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            ManagedClusters.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    static string WithReadyCondition(string objectJson, bool ready, string message) {
        var node = JsonNode.Parse(objectJson)!.AsObject();

        node["status"] = new JsonObject {
            ["conditions"] = new JsonArray(
                new JsonObject {
                    ["type"] = "Ready",
                    ["status"] = ready ? "True" : "False",
                    ["message"] = message
                }
            )
        };

        return node.ToJsonString();
    }
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.
/// </summary>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => ManagedClusters.Type;

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

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    /// <summary>The code the API server refuses with, or <see langword="null" />.</summary>
    public ErrorCode? RefuseWith { get; init; }

    public Guid ClusterId => ManagedClusterReconcilerTests.ClusterId;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (RefuseWith is { } refusal) {
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(
                    refusal,
                    $"the API server refused to apply {command.Target}: no matches for kind "
                    + $"\"{command.Target.Kind.Kind}\" in version \"{command.Target.Kind.ApiVersion}\"."
                )
            );
        }

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
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kamaji" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = WithExistingStatus(Key(command.Target), command.Body);
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
    ///     Carries an existing object's <c>status</c> through an apply, as a real API server does.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>WITHOUT THIS THE FAKE IS WRONG IN THE ONE WAY THAT MATTERS TO THIS PROVIDER, AND IT
    ///     MADE A REAL TEST GO GREEN FOR THE WRONG REASON.</b> <c>status</c> is a subresource: a
    ///     server-side apply of the main resource does not touch it, so a controller's report survives
    ///     every pass this reconciler makes. A dictionary that replaced the whole document would erase
    ///     the status on the apply at the top of each pass — and this is the only type in the tree
    ///     whose <c>Converged</c> reads one, so no earlier provider's copy of this harness needed it.
    /// </remarks>
    /// <param name="key">The stored object's key.</param>
    /// <param name="body">What the apply carried.</param>
    string WithExistingStatus(string key, string body) {
        if (!Objects.TryGetValue(key, out var existing)) {
            return body;
        }

        var previous = System.Text.Json.Nodes.JsonNode.Parse(existing) as JsonObject;

        if (previous?["status"] is not { } status) {
            return body;
        }

        var applied = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        applied["status"] = status.DeepClone();

        return applied.ToJsonString();
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same resource name in two tenants; the KIND is in it because this type applies
    ///     objects that share a name across API groups, and a key without it would make the second
    ///     apply overwrite the first and every read-back return the wrong document.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. Most tests here assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}

/// <summary>A log that keeps what it was told — the step list is a feature on this type.</summary>
sealed class CollectingLog : IReconcileLog {
    public List<(string Phase, string Detail)> Entries { get; } = [];

    public void Report(string phase, string detail) => Entries.Add((phase, detail));

    public void Report(string phase, string detail, int percent) => Entries.Add((phase, detail));
}
