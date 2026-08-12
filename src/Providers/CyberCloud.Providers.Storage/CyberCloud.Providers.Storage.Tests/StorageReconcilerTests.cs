using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The object-storage reconciler against a connection that misbehaves in the ways a real cluster
///     does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Messaging.Tests</c> however identical they look. That duplication
///     is the price of the rule and is worth naming rather than apologising for: the alternative is a
///     line in <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class StorageReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new StorageAccountReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckStillMissesAReadonlyMutableCache() {
        // ⚠ THE BLIND SPOT, PINNED AGAINST A COUNTER-EXAMPLE THAT SHOULD FAIL AND DOES NOT.
        // ReconcilerConformance.CheckNoHiddenState skips a field that is `readonly`, and a `readonly`
        // Dictionary is mutable forever — which is exactly the shape a per-tenant cache takes when
        // somebody adds one for performance. Two providers have confirmed that only the cross-tenant
        // test below catches it; this is the third, and pinning it is what stops the next test looking
        // redundant.
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
        var reconciler = new StorageAccountReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming an account `assets` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("assets", TenantA, SubscriptionA);
        var bob = Address("assets", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            StorageAccounts.Body(ClusterId, volumeServers: 3, storageSize: "100Gi")
        );

        using var bobBody = JsonDocument.Parse(
            StorageAccounts.Body(ClusterId, volumeServers: 6, storageSize: "500Gi")
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        Volume(applied[0].Body)["replicas"]!.GetValue<int>().ShouldBe(3);
        Volume(applied[1].Body)["replicas"]!.GetValue<int>().ShouldBe(6);
        Volume(applied[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(3, "tenant A's volume-server count came back as tenant B's");

        Volume(applied[3].Body)["requests"]!["storage"]!.GetValue<string>()
            .ShouldBe("500Gi", "tenant B's volume size came back as tenant A's");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's account rather than Bob's. Without this the assertions above would hold for a
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
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ ONE OBJECT MAKES THIS LOOK TRIVIAL AND IT IS NOT. A reconciler that applied the Seaweed
        // and returned Converged without reading anything would pass every other test in this file:
        // the apply happened, the body was right, the labels were right. Clause 4 is the claim that
        // the platform OBSERVED what it applied, and set equality in both directions is the only form
        // that catches either half of it going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

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
        // Clause 1. Nothing in SeaweedJson counts, appends or timestamps, and this is what says so.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

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
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.volume.replicas` is the
        // plausible one on this type: it is the capacity axis, and a tenant running any autoscaler
        // over their own cluster can end up owning it. Forcing would silently undo them every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.volume.replicas" };
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("replicas");
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceTheObjectIsGone() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new StorageAccountReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Count.ShouldBe(1);
        connection.Deleted[0].Kind.Kind.ShouldBe("Seaweed");
    }

    // ── Failure class (f), at the object: the credential reference is never optional ─────────────

    [Fact]
    public async Task TheGatewaysConfigSecretReferenceIsRenderedOnEveryPass() {
        // ⚠ THE ONE FIELD WHOSE ABSENCE MAKES THE SERVICE WORK, WHICH IS WHY NOTHING ELSE WOULD EVER
        // REPORT IT. weed/s3api/auth_credentials.go sets `isAuthEnabled = len(identities) > 0` and
        // AuthenticateRequest returns an ADMIN identity when it is false — so a Seaweed rendered
        // without spec.s3.configSecret comes up, passes every readiness probe, converges, and serves
        // every anonymous request as an administrator. A "temporarily drop the reference so the
        // account can finish" change would look like a fix.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var s3 = JsonNode.Parse(connection.Applied[0].Body)!["spec"]!["s3"]!.AsObject();

        s3["configSecret"].ShouldNotBeNull(
            "the S3 gateway was rendered with no identities file. SeaweedFS treats that as "
            + "\"authentication disabled\" and grants ACTION_ADMIN to every anonymous caller."
        );

        s3["configSecret"]!["name"]!.GetValue<string>().ShouldBe("observed-s3-config");
        s3["configSecret"]!["key"]!.GetValue<string>().ShouldBe("s3.json");
    }

    [Fact]
    public async Task TheRenderedObjectCarriesNoSecretValueAnywhere() {
        // docs/plan/05: credentials never in grain state, and never in a rendered object either. The
        // CR names a Secret; it never carries one.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(StorageAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var forbidden in new[] { "accessKey", "secretKey", "secretAccessKey", "stringData" }) {
            connection.Applied[0].Body.ShouldNotContain(forbidden, Case.Sensitive, forbidden);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new StorageAccountReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        StorageAccountReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                StorageAccounts.V2026,
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
            StorageAccounts.V2026,
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
            StorageAccounts.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Volume(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["volume"]!.AsObject();
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

    public ResourceTypeName Type => StorageAccounts.Type;

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
