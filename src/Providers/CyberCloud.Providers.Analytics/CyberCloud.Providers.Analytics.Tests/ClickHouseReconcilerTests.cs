using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Analytics.Tests;

/// <summary>
///     The ClickHouse reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Storage.Tests</c> however identical they look. That duplication is
///     the price of the rule and is worth naming rather than apologising for: the alternative is a line
///     in <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class ClickHouseReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new ClickHouseClusterReconciler(new FixedClock()))
            .ShouldBeEmpty();
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
        // silo ONE instance serves every tenant in the process.
        var reconciler = new ClickHouseClusterReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a cluster `events` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("events", TenantA, SubscriptionA);
        var bob = Address("events", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            ClickHouseClusters.Body(ClusterId, shards: 1, replicas: 2, storageSize: "100Gi")
        );

        using var bobBody = JsonDocument.Parse(
            ClickHouseClusters.Body(ClusterId, shards: 4, replicas: 3, storageSize: "500Gi")
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        // Two objects per pass.
        var installations = connection.Applied
            .Where(x => x.Target.Kind.Kind == "ClickHouseInstallation")
            .ToList();

        installations.Count.ShouldBe(4);

        Layout(installations[0].Body)["shardsCount"]!.GetValue<int>().ShouldBe(1);
        Layout(installations[1].Body)["shardsCount"]!.GetValue<int>().ShouldBe(4);
        Layout(installations[2].Body)["shardsCount"]!.GetValue<int>()
            .ShouldBe(1, "tenant A's shard count came back as tenant B's");

        Claim(installations[3].Body)["storage"]!.GetValue<string>()
            .ShouldBe("500Gi", "tenant B's volume size came back as tenant A's");

        installations[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        installations[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's cluster rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        installations[0].Target.Namespace.ShouldNotBe(installations[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ TWO OBJECTS MAKE THIS THE ASSERTION THAT MATTERS MOST ON THIS TYPE. A reconciler that
        // applied both and read back only the installation would pass every other test in this file
        // — and would report Converged for a cluster whose Keeper apply was swallowed, which is a
        // ClickHouse that starts, answers SELECT 1, and cannot create a replicated table. Set equality
        // in BOTH directions is the only form that catches either half going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.Count.ShouldBe(2);

        read.ShouldBe(
            applied,
            "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
            + ". An object applied and not read back is one the loop reports Converged without ever "
            + "having observed."
        );
    }

    [Fact]
    public async Task TheKeeperIsAppliedBeforeTheInstallationThatPointsAtIt() {
        // ⚠ Not correctness — the installation is declarative and reconnects — but the gap is a
        // window in which every ClickHouse pod logs coordination failures the tenant did not cause.
        // Ordering is free; asserting it is what stops a refactor reordering the two applies for
        // tidiness.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        connection.Applied[0].Target.Kind.Kind.ShouldBe("ClickHouseKeeperInstallation");
        connection.Applied[1].Target.Kind.Kind.ShouldBe("ClickHouseInstallation");
    }

    [Fact]
    public async Task TheTwoObjectsAreAppliedIntoTwoDifferentApiGroups() {
        // ⚠ ONE OPERATOR BINARY, TWO CRDs, TWO GROUPS. A provider that put both kinds in
        // `clickhouse.altinity.com` would produce a Keeper apply the API server answers with a 404 on
        // every pass — and until 2026-08-12 that 404 escaped as an HttpOperationException with no
        // status code in it.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        connection.Applied.Select(x => x.Target.Kind.Group).Order(StringComparer.Ordinal).ShouldBe(
            ["clickhouse-keeper.altinity.com", "clickhouse.altinity.com"]
        );
    }

    [Fact]
    public async Task EachRenderedBodyNamesTheSameKindTheCommandTargets() {
        // ⚠ THE TWO-SPELLINGS CHECK, AND THIS TYPE IS THE FIRST THAT NEEDS IT. KubeCommandBuilder
        // injects `kind` into the body from the GroupVersionKind it is handed, and both renders write
        // `kind` themselves — because ONE Matches serves TWO kinds and a document with no kind would
        // have to be guessed at from its shape. The builder OVERWRITES, so a disagreement would be
        // resolved silently in its favour: the render would be judged as the wrong kind by Matches
        // and applied as the right one, and the resource would never converge with nothing saying why.
        //
        // Both values come from the same GroupVersionKind constant, so they cannot disagree — and this
        // is what says so rather than the comment above.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var applied in connection.Applied) {
            JsonNode.Parse(applied.Body)!["kind"]!.GetValue<string>()
                .ShouldBe(applied.Target.Kind.Kind);
        }
    }

    [Fact]
    public async Task TheInstallationNamesTheKeeperServiceOfThisResourceAndNotOfAnother() {
        // ⚠ THE ASSERTION ClickHouseClusters.Matches CANNOT MAKE, because ObjectMatchesDesired carries
        // no ADDRESS — the finding StorageBuckets records. Two tenants each with a cluster called
        // `events` is the case that catches a Keeper Service name built from anything but this
        // resource's own name.
        var reconciler = new ClickHouseClusterReconciler(new FixedClock());
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Pass(reconciler, connection, Address("events", TenantA, SubscriptionA), body.RootElement);
        await Pass(reconciler, connection, Address("orders", TenantB, SubscriptionB), body.RootElement);

        var hosts = connection.Applied
            .Where(x => x.Target.Kind.Kind == "ClickHouseInstallation")
            .Select(x => JsonNode.Parse(x.Body)!["spec"]!["configuration"]!["zookeeper"]!["nodes"]!
                .AsArray()[0]!["host"]!.GetValue<string>())
            .ToList();

        hosts.ShouldBe(["keeper-events", "keeper-orders"]);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. Nothing in either render counts, appends or timestamps, and this is what says so.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

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
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ On this CRD there is a second party
        // with a legitimate claim on the same subtree: spec.templating.policy `auto` lets a
        // cluster-scoped ClickHouseInstallationTemplate merge into spec.templates, which is exactly
        // where this provider writes. Forcing would silently undo whoever installed it, every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.templates.podTemplates" };
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("podTemplates");
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceBothObjectsAreGone() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new ClickHouseClusterReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);

        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(
            ["ClickHouseInstallation", "ClickHouseKeeperInstallation"],
            "the installation is deleted first, so servers do not outlive their coordination"
        );
    }

    // ── Failure class (f), at the object: no credential is ever rendered ─────────────────────────

    [Fact]
    public async Task TheRenderedObjectsCarryNoSecretValueAnywhere() {
        // docs/plan/05: credentials never in grain state, and never in a rendered object either.
        // ⚠ On this type nothing even NAMES a Secret, which is the shape charts/managed/seaweedfs
        // could not take: a CHI with no users section is not open — the operator's own hardening guide
        // says `default` gets an empty password behind a pod-IP allow-list covering this cluster's own
        // pods. So the honest rendering is nothing at all, and this is what says nothing crept in.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ClickHouseClusters.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var applied in connection.Applied) {
            foreach (var forbidden in new[] { "password", "secret", "stringData", "users" }) {
                applied.Body.ShouldNotContain(forbidden, Case.Insensitive, forbidden);
            }
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new ClickHouseClusterReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        ClickHouseClusterReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                ClickHouseClusters.V2026,
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
            ClickHouseClusters.V2026,
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
            ClickHouseClusters.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Layout(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["configuration"]!["clusters"]!.AsArray()[0]!["layout"]!
            .AsObject();

    static JsonObject Claim(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["templates"]!["volumeClaimTemplates"]!.AsArray()[0]!
            ["spec"]!["resources"]!["requests"]!.AsObject();
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> reports and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, which stops it being reassigned and stops nothing
///     about the dictionary it holds. That is the shape a per-tenant cache takes when somebody adds
///     one for performance. <c>CheckNoHiddenState</c> used to skip it for being
///     <see langword="readonly" /> and now reports it; the cross-tenant test in the sibling file is
///     what still catches the mixing a field's declared type cannot show.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => ClickHouseClusters.Type;

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
    ///     reconciler serving both can be caught mixing them. ⚠ The KIND is in it because this type
    ///     applies two objects that share a name — a key without it would make the second apply
    ///     overwrite the first and every read-back return the wrong document.
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
