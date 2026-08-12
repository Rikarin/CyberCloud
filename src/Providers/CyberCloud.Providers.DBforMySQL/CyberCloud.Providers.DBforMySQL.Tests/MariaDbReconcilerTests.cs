using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     The reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class MariaDbReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        //
        // ⚠ AND IT IS NOT ENOUGH ON ITS OWN. ReconcilerConformance.CheckNoHiddenState skips `readonly`
        // fields, because an injected dependency assigned once in a constructor is the normal shape —
        // so a `readonly Dictionary<,>` used as a per-pass cache passes this check and is a
        // cross-tenant bug. The test below is the one that catches that, and neither replaces the
        // other: this one runs in microseconds and names the field, that one needs two tenants and a
        // world.
        ReconcilerConformance.CheckNoHiddenState(new MariaDbServerReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONE THAT COVERS THE STRUCTURAL CHECK'S
        // BLIND SPOT. AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE —
        // its own remarks say a transient registration "would hide a field long enough for it to reach
        // production" — so in a real silo ONE instance serves every tenant in the process. A field
        // caching, say, the last rendered spec would pass every test that drives one tenant and would
        // hand tenant B tenant A's database name in production.
        //
        // So: one instance, two tenants, two different bodies, interleaved, and both worlds checked.
        var reconciler = new MariaDbServerReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a server `orders` is the
        // ordinary case, not an edge one — the namespaces differ and nothing else does, so a
        // reconciler that keyed anything on the name alone would serve one of them the other's spec.
        // ⚠ Each tenant brings its OWN subscription, and it has to. ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` — the TENANT ID IS NOT IN IT — so two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason
        // rather than the reconciler's.
        var alice = Address("orders", TenantA, SubscriptionA);
        var bob = Address("orders", TenantB, SubscriptionB);

        var world = new RecordingConnection();

        // ⚠ The two bodies differ in THREE independently rendered fields — the topology (which changes
        // `spec.replicas` AND whether a `galera` block exists), the volume size, and the database
        // name. A reconciler that kept one of them would be caught; one that kept a whole rendered
        // document would be caught three times over.
        using var aliceBody = JsonDocument.Parse(
            MariaDbServers.Body(ClusterId, highAvailability: "Galera", storageSize: "20Gi", database: "alice")
        );

        using var bobBody = JsonDocument.Parse(
            MariaDbServers.Body(ClusterId, highAvailability: "None", storageSize: "64Gi", database: "bob")
        );

        // Interleaved on purpose: A, B, A. A reconciler that remembered anything from its first pass
        // would answer the third pass with B's values.
        await Pass(reconciler, world, alice, aliceBody.RootElement);
        await Pass(reconciler, world, bob, bobBody.RootElement);
        var third = await Pass(reconciler, world, alice, aliceBody.RootElement);

        third.IsConverged.ShouldBeTrue(third.ToString());

        var applied = world.Applied;
        applied.Count.ShouldBe(3);

        Spec(applied[0].Body)["replicas"]!.GetValue<int>().ShouldBe(3);
        Spec(applied[1].Body)["replicas"]!.GetValue<int>().ShouldBe(1);
        Spec(applied[2].Body)["replicas"]!.GetValue<int>().ShouldBe(3);

        Spec(applied[0].Body)["database"]!.GetValue<string>().ShouldBe("alice");
        Spec(applied[1].Body)["database"]!.GetValue<string>().ShouldBe("bob");
        Spec(applied[2].Body)["database"]!.GetValue<string>().ShouldBe("alice");

        Spec(applied[0].Body)["storage"]!["size"]!.GetValue<string>().ShouldBe("20Gi");
        Spec(applied[1].Body)["storage"]!["size"]!.GetValue<string>().ShouldBe("64Gi");

        Spec(applied[0].Body).AsObject().ContainsKey("galera").ShouldBeTrue();
        Spec(applied[1].Body).AsObject().ContainsKey("galera").ShouldBeFalse(
            "tenant B asked for no high availability and got tenant A's Galera block"
        );

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        applied[2].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's MariaDB rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the first.
        applied[0].Target.Namespace.ShouldNotBe(applied[1].Target.Namespace);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A tenant whose cluster is down has a resource that is still coming, not one
        // that broke — a Failed here would end the operation and strand a half-built database.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced apply.
        // ⚠ On this type the other manager is very plausibly the operator itself: MariaDB.SetDefaults
        // writes eleven fields into the spec it is handed, including a whole
        // storage.volumeClaimTemplate and a `tls` block this provider never asked for. Forcing would
        // take those back from the controller that maintains them, once per reminder, forever.
        var connection = new RecordingConnection { ConflictField = ".spec.storage.volumeClaimTemplate" };
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".spec.storage.volumeClaimTemplate");
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. The apply succeeds and the read finds nothing — a reconciler that
        // believed its own apply would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task TheMariaDbCarriesTheSevenLabelsAndBothAnnotations() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied.Count.ShouldBe(1);
        connection.Applied[0].Target.Kind.Kind.ShouldBe("MariaDB");

        // ⚠ ADR-013's seven, on a CUSTOM RESOURCE at v1alpha1 whose CRD nothing in this repository
        // installs. The rendered object goes through the same KubeCommand injection as a core-group
        // ConfigMap, and that is the point being pinned — a provider cannot opt out by rendering
        // something exotic, and an alpha CRD is the most exotic thing in the tree.
        foreach (var command in connection.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, command.Target.ToString());
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            // ⚠ A LITERAL, matching charts/managed/mariadb/conformance.yaml § labels.values.
            command.Labels[KubeLabels.ResourceType].ShouldBe("cybercloud.dbformysql_servers");

            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                command.Annotations.ShouldContainKey(annotation);
            }
        }
    }

    [Fact]
    public async Task ASecondPassOnAConvergedServerWritesNothingNew() {
        // Clause 1, in the shape that matters for this type: the apply is server-side, so a repeat is
        // an Unchanged, and nothing here counts, appends or timestamps. A rendering that embedded a
        // clock or a counter would produce a different body on every pass and re-apply forever.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        var first = connection.Applied[0].Body;

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied[1].Body.ShouldBe(first, "the rendered object changed between two identical passes");
    }

    [Fact]
    public async Task ATeardownIsConvergedOnlyOnceTheObjectIsUnreadableAndItCascadesInForeground() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var torn = await new MariaDbServerReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
        connection.Objects.ShouldBeEmpty();

        // ⚠ FOREGROUND, and it is a choice invisible in the resulting object. A MariaDB expands into a
        // StatefulSet, four Services and ConfigMaps — its OWNED children. A background cascade returns
        // as soon as the CR is gone, so the read-back would report "not found" while three database
        // pods were still accepting writes, and a resource that stops being billed while it is still
        // serving traffic is the failure the read-back exists to prevent. docs/plan/06 § Two-phase
        // create: "never silently gone while its pods still run and its meter still ticks".
        connection.Cascades.ShouldBe([CascadePolicy.Foreground]);
    }

    [Fact]
    public async Task ATeardownWithNoClusterIsConvergedRatherThanStuck() {
        // ⚠ The asymmetry with the create path, asserted so it is not read as an oversight. Failing
        // here would park the resource in Deleting — visible, billed and permanent — for a wiring
        // reason rather than a cluster one.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var torn = await new MariaDbServerReconciler(new FixedClock())
            .DeleteAsync(Context(null, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task ObservingReportsWhatIsThereAndNeverApplies() {
        // docs/plan/08 § The reconcile loop: ObserveAsync "must not apply anything — this runs on the
        // drift path too, where a write would turn a diff into a change."
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var reconciler = new MariaDbServerReconciler(new FixedClock());
        var address = Address("observed", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        var absent = await reconciler.ObserveAsync(
            new(address, MariaDbServers.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        absent.Exists.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("observing applied something");

        await Reconcile(connection, desired.RootElement);
        var appliedByReconciling = connection.Applied.Count;

        var present = await reconciler.ObserveAsync(
            new(address, MariaDbServers.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        present.Exists.ShouldBeTrue();
        present.Summary.ShouldContain("desired");
        connection.Applied.Count.ShouldBe(appliedByReconciling, "observing applied something");
    }

    [Fact]
    public async Task TheRenderedServerCarriesThePresetsQuantitiesAndTheVersionsImage() {
        // ⚠ THE TWO PLACES WHERE "THE BODY SAID IT AND THE CR DID NOT GET IT" WOULD BE INVISIBLE. A
        // preset that renders no `resources` block bills a tenant for a size they did not get; and an
        // unset `spec.image` is not an error at all — MariaDB.GetImage falls back to the operator's
        // RelatedMariadbImage, so the tenant gets whatever MariaDB the cluster's operator was shipped
        // with while the API says 11.4.
        var connection = new RecordingConnection();

        var body = JsonNode.Parse(MariaDbServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["preset"] = "s1.large" };

        using var desired = JsonDocument.Parse(body.ToJsonString());
        await Reconcile(connection, desired.RootElement);

        var spec = Spec(connection.Applied[0].Body);

        spec["resources"]!["requests"]!["cpu"]!.GetValue<string>().ShouldBe("2");
        spec["resources"]!["limits"]!["memory"]!.GetValue<string>().ShouldBe("8Gi");
        spec["image"]!.GetValue<string>().ShouldBe("mariadb:11.4");
        spec["port"]!.GetValue<int>().ShouldBe(3306);
    }

    [Fact]
    public void MatchesIsAContainmentTestSoTheOperatorsOwnDefaultingIsNotDrift() {
        // ⚠ THIS TYPE'S OPERATOR EDITS THE SPEC IT IS GIVEN, AND THE CRD IS THE EVIDENCE RATHER THAN
        // THE README. api/v1alpha1/mariadb_types.go § SetDefaults writes image, rootEmptyPassword,
        // rootPasswordSecretKeyRef, port, myCnfConfigMapKeyRef, passwordSecretKeyRef, the metrics
        // exporter's image/port/username/password ref, `tls: {enabled: true}` and updateStrategy; and
        // Storage.SetDefaults() adds ephemeral, resizeInUseVolumes, waitForVolumeResize and a whole
        // volumeClaimTemplate. An equality test would report drift on the pass immediately after the
        // operator first saw the object, and on every pass after that, forever.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId));

        var rendered = JsonNode.Parse(MariaDbServers.ServerJson("subset", desired.RootElement))!.AsObject();
        rendered["kind"] = "MariaDB";
        rendered["status"] = new JsonObject { ["currentPrimary"] = "subset-0" };

        var spec = rendered["spec"]!.AsObject();
        spec["tls"] = new JsonObject { ["enabled"] = true };
        spec["myCnfConfigMapKeyRef"] = new JsonObject { ["name"] = "subset-config", ["key"] = "my.cnf" };
        spec["updateStrategy"] = new JsonObject { ["type"] = "ReplicasFirstPrimaryLast" };
        spec["storage"]!.AsObject()["ephemeral"] = false;
        spec["storage"]!.AsObject()["resizeInUseVolumes"] = true;
        spec["storage"]!.AsObject()["volumeClaimTemplate"] = new JsonObject {
            ["accessModes"] = new JsonArray("ReadWriteOnce")
        };

        MariaDbServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        // And the halves that make containment worth more than no test at all.
        var galeraDropped = JsonNode.Parse(rendered.ToJsonString())!.AsObject();
        galeraDropped["spec"]!.AsObject().Remove("galera");
        MariaDbServers.Matches(galeraDropped.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a Galera server whose block the operator dropped is a single instance with a "
            + "three-instance quota reservation, and Matches called it converged"
        );

        var credentialDropped = JsonNode.Parse(rendered.ToJsonString())!.AsObject();
        credentialDropped["spec"]!.AsObject().Remove("rootPasswordSecretKeyRef");
        MariaDbServers.Matches(credentialDropped.ToJsonString(), desired.RootElement).ShouldBeFalse(
            "a server whose root credential reference is gone has a password nothing recorded"
        );

        var resized = JsonNode.Parse(rendered.ToJsonString())!.AsObject();
        resized["spec"]!["storage"]!.AsObject()["size"] = "1Gi";
        MariaDbServers.Matches(resized.ToJsonString(), desired.RootElement).ShouldBeFalse();

        MariaDbServers.Matches("not json at all", desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void MatchesToleratesAnAbsentGaleraBlockOnlyWhenTheBodyAskedForNoHighAvailability() {
        // The other direction of the same test: with `highAvailability: None` the operator may leave
        // `spec.galera` absent or write `enabled: false`, and both mean the same thing to it. A
        // Matches that demanded the key would report drift forever on every single-instance server.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(ClusterId, highAvailability: "None"));

        var rendered = JsonNode.Parse(MariaDbServers.ServerJson("single", desired.RootElement))!.AsObject();
        MariaDbServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        rendered["spec"]!.AsObject()["galera"] = new JsonObject { ["enabled"] = false };
        MariaDbServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        // But an operator that turned Galera ON under a body that asked for one instance is drift, and
        // it is the expensive direction: three pods and three PVCs against a one-instance reservation.
        rendered["spec"]!["galera"]!.AsObject()["enabled"] = true;
        MariaDbServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeFalse();
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new MariaDbServerReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        MariaDbServerReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                MariaDbServers.V2026,
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
            MariaDbServers.V2026,
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
            MariaDbServers.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
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
    ///     The cascade policy of every delete, in order.
    /// </summary>
    /// <remarks>
    ///     ⚠ Recorded because it is a CHOICE this provider makes and the first relational provider
    ///     makes differently, and because it is invisible in the resulting object. A delete that used
    ///     the default would look identical in <see cref="Deleted" />.
    /// </remarks>
    public List<CascadePolicy> Cascades { get; } = [];

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
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "mariadb-operator" }]
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
            Cascades.Add(policy);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in the key because the cross-tenant
    ///     test puts the same resource name in two tenants, which is the only shape in which one
    ///     singleton reconciler serving both can be caught mixing them.
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

    public void Report(string phase, string detail, int percentComplete) { }
}
