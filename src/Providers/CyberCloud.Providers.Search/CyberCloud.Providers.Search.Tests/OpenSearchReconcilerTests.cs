using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search.Tests;

/// <summary>
///     The search reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Storage.Tests</c> however identical they look. That duplication is
///     the price of the rule and is worth naming rather than apologising for: the alternative is a
///     line in <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class OpenSearchReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new OpenSearchServiceReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckStillMissesAReadonlyMutableCache() {
        // ⚠ THE BLIND SPOT, PINNED AGAINST A COUNTER-EXAMPLE THAT SHOULD FAIL AND DOES NOT.
        // ReconcilerConformance.CheckNoHiddenState skips a field that is `readonly` — `field.IsInitOnly`
        // is the first thing it `continue`s on — and a `readonly` Dictionary is mutable forever, which
        // is exactly the shape a per-tenant cache takes when somebody adds one for performance. Three
        // providers have confirmed that only the cross-tenant test below catches it; this is the
        // fourth, and pinning it is what stops the next test looking redundant.
        //
        // ⚠ AND THE TEMPTATION IS LARGER ON THIS TYPE THAN ON ANY BEFORE IT. ClusterJson renders a
        // three-entry array by re-reading the body and re-resolving the preset table on every pass,
        // which is precisely the shape somebody memoises.
        //
        // If somebody ever closes the hole, THIS test goes red and says where to delete the
        // now-unnecessary belt — a better outcome than a comment nobody re-reads.
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
        var reconciler = new OpenSearchServiceReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a search service `logs` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("logs", TenantA, SubscriptionA);
        var bob = Address("logs", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, dataNodes: 3, storageSize: "100Gi")
        );

        // ⚠ Bob's body has a DIFFERENT NUMBER OF POOLS, which is the shape only this type has. A cache
        // keyed on anything but the full address would give Alice a coordinating pool she never asked
        // for — three StatefulSets where she is billed for two.
        using var bobBody = JsonDocument.Parse(
            OpenSearchServices.Body(
                ClusterId,
                dataNodes: 6,
                storageSize: "500Gi",
                coordinatingNodes: 2
            )
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        Pools(applied[0].Body).Count.ShouldBe(2);
        Pools(applied[1].Body).Count.ShouldBe(3);

        Pools(applied[2].Body).Count.ShouldBe(
            2,
            "tenant A's node pools came back as tenant B's — a coordinating pool she never asked for"
        );

        DataPool(applied[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(3, "tenant A's data-node count came back as tenant B's");

        DataPool(applied[3].Body)["diskSize"]!.GetValue<string>()
            .ShouldBe("500Gi", "tenant B's disk size came back as tenant A's");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's service rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        applied[0].Target.Namespace.ShouldNotBe(applied[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ ONE OBJECT MAKES THIS LOOK TRIVIAL AND IT IS NOT. A reconciler that applied the
        // OpenSearchCluster and returned Converged without reading anything would pass every other
        // test in this file: the apply happened, the pools were right, the labels were right. Clause 4
        // is the claim that the platform OBSERVED what it applied, and set equality in both directions
        // is the only form that catches either half of it going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        read.ShouldBe(
            applied,
            "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
            + ". An object applied and not read back is one the loop reports Converged without ever "
            + "having observed."
        );
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. ⚠ AND ON THIS TYPE IT IS A CLAIM ABOUT AN ARRAY, WHICH IS WHY IT IS NOT THE
        // formality it is elsewhere. spec.nodePools has no listMapKey in the operator's CRD, so
        // server-side apply owns the whole array atomically — a renderer that emitted the same pools
        // in a different order on two passes would make every reconcile a write, and every write a
        // rolling restart of three StatefulSets.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(
            OpenSearchServices.Body(ClusterId, coordinatingNodes: 2)
        );

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
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.confMgmt.smartScaler` is the
        // plausible one on this type, and it is not a tenant doing anything odd: the CRD carries
        // +kubebuilder:default=true AND +kubebuilder:validation:Required on that field, so the API
        // server itself owns it. Forcing would be this platform fighting the API server on every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.confMgmt.smartScaler" };
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("smartScaler");
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceTheObjectIsGone() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new OpenSearchServiceReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Count.ShouldBe(1);
        connection.Deleted[0].Kind.Kind.ShouldBe("OpenSearchCluster");
    }

    // ── The credential, at the object ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRenderedObjectCarriesNoSecretValueAnywhere() {
        // docs/plan/05: credentials never in grain state, and never in a rendered object either. This
        // type names no Secret at all — the operator generates its own — so the assertion is that
        // nothing resembling a credential appears, rather than that a reference does.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(OpenSearchServices.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var forbidden in new[] { "password", "adminSecret", "stringData", "OPENSEARCH_INITIAL" }) {
            connection.Applied[0].Body.ShouldNotContain(forbidden, Case.Sensitive, forbidden);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new OpenSearchServiceReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        OpenSearchServiceReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                OpenSearchServices.V2026,
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
            OpenSearchServices.V2026,
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
            OpenSearchServices.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonArray Pools(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["nodePools"]!.AsArray();

    static JsonObject DataPool(string objectJson) =>
        Pools(objectJson).Single(x => x!["component"]!.GetValue<string>() == "data")!.AsObject();
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, so <c>CheckNoHiddenState</c> skips it, and the
///     dictionary it holds is mutable forever. This is the shape a per-tenant cache takes when
///     somebody adds one for performance, and the only test in the sibling file that would catch it is
///     the cross-tenant one.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => OpenSearchServices.Type;

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

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

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
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kube-apiserver" }]
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
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same resource name in two tenants, which is the only shape in which one singleton
    ///     reconciler serving both can be caught mixing them.
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
