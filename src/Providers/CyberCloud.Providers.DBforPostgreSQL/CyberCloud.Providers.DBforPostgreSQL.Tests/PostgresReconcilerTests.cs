using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     The reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class PostgresReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new PostgresServerReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE. AddCyberCloudProvider registers a reconciler as a
        // SINGLETON BY CONCRETE TYPE — its own remarks say a transient registration "would hide a
        // field long enough for it to reach production" — so in a real silo ONE instance serves every
        // tenant in the process. A field caching, say, the last rendered spec would pass every test
        // that drives one tenant and would hand tenant B tenant A's database size in production.
        //
        // So: one instance, two tenants, two different bodies, interleaved, and both worlds checked.
        var reconciler = new PostgresServerReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a database `main` is the
        // ordinary case, not an edge one — the namespaces differ and nothing else does, so a
        // reconciler that keyed anything on the name alone would serve one of them the other's spec.
        // ⚠ Each tenant brings its OWN subscription, and it has to. ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` — the TENANT ID IS NOT IN IT — so two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason
        // rather than the reconciler's. That is not a defect: docs/plan/06 § The hierarchy puts a
        // subscription inside exactly one tenant, so the two can never really collide. It is worth
        // knowing that the isolation here rests on that invariant rather than on the label.
        var alice = Address("main", TenantA, SubscriptionA);
        var bob = Address("main", TenantB, SubscriptionB);

        var world = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(PostgresServers.Body(ClusterId, 2, "10Gi"));
        using var bobBody = JsonDocument.Parse(PostgresServers.Body(ClusterId, 5, "50Gi"));

        // Interleaved on purpose: A, B, A. A reconciler that remembered anything from its first pass
        // would answer the third pass with B's values.
        await Pass(reconciler, world, alice, aliceBody.RootElement);
        await Pass(reconciler, world, bob, bobBody.RootElement);
        var third = await Pass(reconciler, world, alice, aliceBody.RootElement);

        third.IsConverged.ShouldBeTrue(third.ToString());

        var applied = world.Applied
            .Where(static x => x.Target.Kind.Kind == "Cluster")
            .ToList();

        applied.Count.ShouldBe(3);

        Spec(applied[0].Body)["storage"]!["size"]!.GetValue<string>().ShouldBe("10Gi");
        Spec(applied[1].Body)["storage"]!["size"]!.GetValue<string>().ShouldBe("50Gi");
        Spec(applied[2].Body)["storage"]!["size"]!.GetValue<string>().ShouldBe("10Gi");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        applied[2].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's Cluster rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        applied[0].Target.Namespace.ShouldNotBe(applied[1].Target.Namespace);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A tenant whose cluster is down has a resource that is still coming, not one
        // that broke — a Failed here would end the operation and strand a half-built database.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced apply.
        // On this type the other manager is plausibly CloudNativePG editing a field it owns, and
        // forcing would make the platform and the operator fight over it once per reminder.
        var connection = new RecordingConnection { ConflictField = ".spec.instances" };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".spec.instances");
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    [Theory]
    [InlineData(nameof(ErrorCode.PolicyViolation))]
    [InlineData(nameof(ErrorCode.InvalidRequestBody))]
    [InlineData(nameof(ErrorCode.InvalidResourceType))]
    [InlineData(nameof(ErrorCode.AuthorizationFailed))]
    public async Task ARefusedApplyIsTerminalRatherThanRetriedForAnHour(string code) {
        // ⚠ The four codes KubeFailures.Classify reports when the API server ANSWERED and said no: an
        // admission webhook or Pod Security decision, a body it will not type-check, a kind it does not
        // serve — a cluster with no CloudNativePG is exactly that one — and the platform's own
        // credentials being refused. None is fixed by waiting, so a retryable outcome buys the tenant
        // sixty minutes of backoff and then an OperationTimeout in place of the message above.
        ErrorCode.TryFromValue(code, out var refusal).ShouldBeTrue();

        var connection = new RecordingConnection { RefuseWith = refusal };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse(outcome.ToString());
        outcome.IsTerminal.ShouldBeTrue("OperationGrain's terminal branch is `Failed when !Retryable`");
        outcome.Error!.Code.ShouldBe(refusal);
    }

    [Fact]
    public async Task AnApplyThatTheClusterNeverAnsweredIsStillRetryable() {
        // ⚠ The other half. ErrorCode.InternalError is what a transport fault comes back as, and it is
        // the commonest failure a reconciler sees — a terminal-by-default rule would end an operation
        // on a dropped connection, which is the mirror-image bug and the more expensive one.
        var connection = new RecordingConnection { RefuseWith = ErrorCode.InternalError };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeTrue(outcome.ToString());
        outcome.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. Both applies succeed and the reads find nothing — a reconciler that
        // believed its own applies would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task APoolerIsAppliedWithTheClusterAndBothCarryTheSevenLabels() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Rendered.Select(static x => x.Target.Kind.Kind).ShouldBe(ClusterThenPooler);

        // ⚠ ADR-013's seven, on a CUSTOM RESOURCE. The sample proved them on a core-group ConfigMap;
        // the rendered object here goes through the same KubeCommand injection, and that is the point
        // being pinned — a provider cannot opt out by rendering something exotic.
        foreach (var command in connection.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, command.Target.ToString());
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            command.Labels[KubeLabels.ResourceType].ShouldBe("cybercloud.dbforpostgresql_servers");

            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                command.Annotations.ShouldContainKey(annotation);
            }
        }
    }

    [Fact]
    public async Task TurningPoolingOffRemovesThePoolerAndThenStopsWriting() {
        // The pooler is the one object whose existence is a setting. A reconciler that applied it and
        // never removed it would leave PgBouncer serving a connection string the resource says is no
        // longer advertised — charts/managed/postgres/conformance.yaml's
        // `pooler-is-the-default-endpoint`.
        var connection = new RecordingConnection();

        using var pooled = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        using var unpooled = JsonDocument.Parse(PostgresServers.Body(ClusterId, pooling: false));

        await Reconcile(connection, pooled.RootElement);
        connection.Objects.Keys.ShouldContain(PoolerKey);

        var off = await Reconcile(connection, unpooled.RootElement);

        off.IsConverged.ShouldBeTrue(off.ToString());
        connection.Objects.Keys.ShouldNotContain(PoolerKey);

        // ⚠ And the steady state is silent. An unconditional delete would be correct and would issue
        // one request per reminder for the life of the resource; clause 1's "changes nothing" is not
        // "does nothing observable".
        var deletesSoFar = connection.Deleted.Count;
        (await Reconcile(connection, unpooled.RootElement)).IsConverged.ShouldBeTrue();
        connection.Deleted.Count.ShouldBe(deletesSoFar);
    }

    [Fact]
    public async Task ATeardownIsConvergedOnlyOnceBothObjectsAreUnreadable() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();

        // ⚠ THE BACKUP KEY'S SECRET STAYS, ON PURPOSE (#30). A server's recovery points outlive it —
        // the vault's schedules own them — and CloudNativePG restores from a Backup by reading the
        // credential Secret its status names, which is this one. Removing it with the Cluster would
        // make the restore a vault exists for, the one after the server is gone, impossible. What
        // reclaims it is owed: charts/managed/postgres/conformance.yaml § owed,
        // `the-backups-outlive-the-server-and-nothing-reclaims-them`.
        connection.Objects.Keys.ShouldBe(
            [RecordingConnection.Key(PostgresServers.BackupSecretRef(ReconcileDriver.NamespaceFor(Address("observed", TenantA, SubscriptionA)), "observed"))]
        );

        // ⚠ The Pooler goes first. It references the Cluster by name, so removing the referent first
        // leaves the operator reconciling a Pooler whose cluster is gone — noise in the tenant's own
        // event stream for as long as the two deletes are apart.
        connection.Deleted[0].Kind.Kind.ShouldBe("Pooler");
        connection.Deleted[1].Kind.Kind.ShouldBe("Cluster");
    }

    [Fact]
    public async Task ATeardownWithNoClusterIsConvergedRatherThanStuck() {
        // ⚠ The asymmetry with the create path, asserted so it is not read as an oversight. Failing
        // here would park the resource in Deleting — visible, billed and permanent — for a wiring
        // reason rather than a cluster one.
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(Context(null, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task ObservingReportsWhatIsThereAndNeverApplies() {
        // docs/plan/08 § The reconcile loop: ObserveAsync "must not apply anything — this runs on the
        // drift path too, where a write would turn a diff into a change."
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var reconciler = new PostgresServerReconciler(new FixedClock());
        var address = Address("observed", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        var absent = await reconciler.ObserveAsync(
            new(address, PostgresServers.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        absent.Exists.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("observing applied something");

        await Reconcile(connection, desired.RootElement);
        var appliedByReconciling = connection.Applied.Count;

        var present = await reconciler.ObserveAsync(
            new(address, PostgresServers.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        present.Exists.ShouldBeTrue();
        present.Summary.ShouldContain("desired");
        connection.Applied.Count.ShouldBe(appliedByReconciling, "observing applied something");
    }

    [Fact]
    public async Task TheRenderedClusterCarriesTheSizingPresetsQuantitiesAndTheExtensionsAllowList() {
        // The two places where "the body said it and the CR did not get it" would be invisible: a
        // preset that renders no `resources` block bills a tenant for a size they did not get, and an
        // extension that reaches neither the bootstrap SQL nor the preload list installs nothing.
        var connection = new RecordingConnection();

        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["preset"] = "s1.large" };
        body["properties"]!.AsObject()["extensions"] = new JsonArray("pgvector");

        using var desired = JsonDocument.Parse(body.ToJsonString());
        await Reconcile(connection, desired.RootElement);

        var spec = Spec(connection.Rendered[0].Body);

        spec["resources"]!["requests"]!["cpu"]!.GetValue<string>().ShouldBe("2");
        spec["resources"]!["limits"]!["memory"]!.GetValue<string>().ShouldBe("8Gi");
        // ⚠ `vector`, not `pgvector`. The allow-list value is the name of the DISTRIBUTION and
        // `CREATE EXTENSION pgvector` fails — see AnExtensionNameIsNotALibraryName.
        spec["bootstrap"]!["initdb"]!["postInitApplicationSQL"]!.AsArray()
            .Select(static x => x!.GetValue<string>())
            .ShouldBe(["CREATE EXTENSION IF NOT EXISTS vector;"]);

        // ⚠ AND NO PRELOAD LIST AT ALL. pgvector needs no `shared_preload_libraries` entry, and a
        // name with no library behind it fails the postmaster's startup rather than one feature.
        spec["postgresql"]!.AsObject()
            .ContainsKey("shared_preload_libraries")
            .ShouldBeFalse(
                "a body naming only pgvector rendered a shared_preload_libraries entry. There is no "
                + "pgvector library to preload, and an unresolvable entry there stops the instance from "
                + "starting at all."
            );
    }

    [Fact]
    public void AnExtensionNameIsNotALibraryName() {
        // ⚠ /properties/extensions IS ONE LIST READ AS TWO VOCABULARIES, and until 2026-08-18 both
        // renderings used the raw value. `CREATE EXTENSION pgvector` fails — pgvector's own README
        // says `CREATE EXTENSION vector` — and `shared_preload_libraries: [pgvector, postgis]` names
        // two libraries that do not exist, which the postmaster refuses at startup.
        //
        // The other two rows go the other way: pg_stat_statements' documentation says the module
        // "must be loaded by adding pg_stat_statements to shared_preload_libraries", and timescaledb's
        // extension_load_without_preload (src/extension_utils.c) raises "extension \"timescaledb\"
        // must be preloaded". Read upstream on 2026-08-18.
        //
        // ⚠ THIS ASSERTS EVERY ALLOWED VALUE IN ONE BODY, because the defect was per-value: a check
        // that named only the value somebody happened to think of is what let the first spelling ship.
        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["extensions"] =
            new JsonArray("pgvector", "postgis", "pg_stat_statements", "timescaledb");

        using var desired = JsonDocument.Parse(body.ToJsonString());
        var spec = JsonNode.Parse(PostgresServers.ClusterJson("orders", desired.RootElement))!["spec"]!;

        spec["bootstrap"]!["initdb"]!["postInitApplicationSQL"]!.AsArray()
            .Select(static x => x!.GetValue<string>())
            .ShouldBe(
                [
                    "CREATE EXTENSION IF NOT EXISTS vector;",
                    "CREATE EXTENSION IF NOT EXISTS postgis;",
                    "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;",
                    "CREATE EXTENSION IF NOT EXISTS timescaledb;"
                ]
            );

        spec["postgresql"]!["shared_preload_libraries"]!.AsArray()
            .Select(static x => x!.GetValue<string>())
            .ShouldBe(
                ["pg_stat_statements", "timescaledb"],
                "the preload list is the extensions that NEED preloading, under their library names. "
                + "pgvector and postgis need none; naming them there is a postmaster that never starts."
            );
    }

    [Fact]
    public void EveryValueTheAllowListPermitsHasACatalogueRow() {
        // The other direction, and the one the renderer's fallback makes silent: a value with no row
        // renders as itself in both vocabularies, which is exactly the defect this table closed. The
        // fallback stays — dropping the value would be worse — so this is what stops the allow-list
        // and the catalogue growing apart.
        foreach (var value in PostgresServers.Schema2026.Properties
                     .Single(static x => x.JsonPointer == "/properties/extensions")
                     .AllowedValues) {
            PostgresServers.ExtensionCatalogue.ShouldContainKey(value);
        }

        PostgresServers.ExtensionCatalogue.Count.ShouldBe(
            PostgresServers.Schema2026.Properties
                .Single(static x => x.JsonPointer == "/properties/extensions")
                .AllowedValues.Length,
            "PostgresServers.ExtensionCatalogue has a row for a value /properties/extensions does not "
            + "allow, so a row is either dead or the allow-list lost a value"
        );
    }

    [Fact]
    public void ThePreloadLibrariesAreASiblingOfParametersInBothSpellings() {
        // ⚠ THE DEFAULT IS `[]`, WHICH IS WHY THIS WENT UNNOTICED FOR MONTHS. Every test and every
        // manual run that used the default body rendered no `shared_preload_libraries` at all, so the
        // one placement that is refused was the one nothing exercised. This body names two extensions
        // on purpose.
        //
        // CloudNativePG declares the key as `AdditionalLibraries []string` on PostgresConfiguration —
        // a SIBLING of `parameters` (api/v1/cluster_types.go) — lists it in
        // FixedConfigurationParameters (pkg/postgres/configuration.go), and its validating webhook
        // answers any fixed key found under spec.postgresql.parameters with "Can't set fixed
        // configuration parameter" (internal/webhook/v1/cluster_webhook.go). The webhook builds its
        // ConfigurationInfo without IncludingSharedPreloadLibraries, so the sanitized value it would
        // compare against stays at the default settings' empty string and no non-empty list can equal
        // it. Every server created with pgvector, postgis, pg_stat_statements or timescaledb was
        // rejected at admission after the caller had been told 202.
        //
        // ⚠ NOTHING IN ./build.sh COMPARES THIS PAIR. ChartSurfaces filters `templates/` out of the
        // chart tree on purpose — build/Build.Charts.cs line-filters the same directory — so no
        // emitter has ever read a Helm template and the two spellings are held together by this test
        // and nothing else. Fixing one and not the other is exactly the drift ADR-012 exists to
        // prevent, so both halves are asserted here rather than in two places.
        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["extensions"] = new JsonArray("pgvector", "timescaledb");

        using var desired = JsonDocument.Parse(body.ToJsonString());

        var postgresql = JsonNode.Parse(PostgresServers.ClusterJson("orders", desired.RootElement))!
            ["spec"]!["postgresql"]!
            .AsObject();

        // ⚠ `timescaledb` alone: pgvector is in the body and needs no preload entry. Which values
        // reach this list is AnExtensionNameIsNotALibraryName's assertion; this one is about where.
        postgresql["shared_preload_libraries"]!.AsArray()
            .Select(static x => x!.GetValue<string>())
            .ShouldBe(["timescaledb"]);

        postgresql["parameters"]!.AsObject()
            .ContainsKey("shared_preload_libraries")
            .ShouldBeFalse(
                "shared_preload_libraries was written under spec.postgresql.parameters, where "
                + "CloudNativePG's validating webhook refuses it as a fixed configuration parameter. The "
                + "Cluster is rejected at admission and the caller has already been told 202."
            );

        // ⚠ The one key that IS a parameter stays one. Checked so that a fix which moved the whole
        // block out of `parameters` would fail here rather than silently drop max_connections.
        postgresql["parameters"]!["max_connections"]!.GetValue<string>().ShouldBe("200");
    }

    [Fact]
    public void NoOtherFixedConfigurationParameterIsWrittenUnderParameters() {
        // ⚠ FixedConfigurationParameters IS A LIST, NOT ONE ENTRY. Having found one key in the wrong
        // block, the question is whether the renderer writes any OTHER key the webhook refuses. It
        // writes exactly one parameter today and that one is free, but the check is cheap and the
        // failure it catches — a second key added later that is fixed or blocked upstream — has the
        // same signature: a 202 followed by an admission rejection nothing local reproduces.
        //
        // The list is pkg/postgres/configuration.go's FixedConfigurationParameters, read on
        // 2026-08-12. Only the entries a PostgreSQL provider might plausibly reach for are named
        // here; a full transcription would be a second copy of upstream's map that nothing updates.
        string[] refused = [
            "shared_preload_libraries", "archive_mode", "archive_command", "cluster_name", "port",
            "listen_addresses", "hot_standby", "restore_command", "temp_tablespaces", "ssl",
            "ssl_cert_file", "ssl_key_file", "synchronous_standby_names", "logging_collector",
            "log_destination", "log_directory", "log_filename", "data_directory", "hba_file",
            "primary_conninfo", "primary_slot_name", "restart_after_crash"
        ];

        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["extensions"] = new JsonArray("pgvector");
        body["properties"]!.AsObject()["synchronousReplication"] = true;

        using var desired = JsonDocument.Parse(body.ToJsonString());

        var parameters = JsonNode.Parse(PostgresServers.ClusterJson("orders", desired.RootElement))!
            ["spec"]!["postgresql"]!["parameters"]!
            .AsObject();

        parameters.ShouldNotBeEmpty("the renderer wrote no `parameters` block, so this checks nothing");

        foreach (var key in refused) {
            parameters.ContainsKey(key)
                .ShouldBeFalse(
                    $"'{key}' is in CloudNativePG's FixedConfigurationParameters and reached "
                    + "spec.postgresql.parameters, where its validating webhook refuses it."
                );
        }
    }

    [Fact]
    public async Task BackupsOnArchiveToTheServersOwnBucketWithAKeyTheVaultHolds() {
        // ⚠ #30, THE HALF #91 LEFT. The default body — backups on, no destination — used to be refused,
        // and a named destination rendered a store with no credentials, which CloudNativePG's webhook
        // refuses ("missing credentials"). Now the platform gives the server a bucket of its own and a
        // key to it, minted into the vault, rendered into a Secret the Cluster names.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var address = Address("observed", TenantA, SubscriptionA);
        var bucket = ObjectStoreCredentials.BucketFor(PostgresServers.BucketPrefix, address.Id);

        var outcome = await Reconcile(connection, desired.RootElement);
        outcome.IsConverged.ShouldBeTrue(outcome.ToString());

        Grants.Buckets.ShouldContain(bucket);

        var held = await Vault.ResolveAsync(
            new() { Path = ObjectStoreCredentials.VaultPathFor(address), Field = ObjectStoreCredentials.AccessKeyIdField },
            TestContext.Current.CancellationToken
        );
        var accessKeyId = held.GetValueOrThrow();

        // The Secret goes first — the Cluster names it — and carries the vault's key and nothing else.
        connection.Applied[0].Target.ShouldBe(PostgresServers.BackupSecretRef(ReconcileDriver.NamespaceFor(address), "observed"));
        var data = JsonNode.Parse(connection.Applied[0].Body)!["data"]!.AsObject();
        Encoding.UTF8.GetString(Convert.FromBase64String(data[PostgresServers.AccessKeyIdKey]!.GetValue<string>()))
            .ShouldBe(accessKeyId);

        var store = Spec(connection.Rendered[0].Body)["backup"]!["barmanObjectStore"]!.AsObject();
        store["destinationPath"]!.GetValue<string>().ShouldBe("s3://" + bucket + "/");
        store["endpointURL"]!.GetValue<string>().ShouldBe(InMemoryObjectStoreGrants.Endpoint);
        store["s3Credentials"]!["accessKeyId"]!["name"]!.GetValue<string>().ShouldBe("observed-backup-s3");
        store["s3Credentials"]!["secretAccessKey"]!["key"]!.GetValue<string>().ShouldBe(PostgresServers.SecretAccessKeyKey);

        // ⚠ The second pass reads the vault back and issues no second key — mint-once, end to end.
        var issued = Grants.Issued;
        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        Grants.Issued.ShouldBe(issued, "a converged server's every pass would otherwise leave a live key behind");

        // Nothing the tenant can read back holds the key — not the body, not the rendered Cluster.
        desired.RootElement.GetRawText().ShouldNotContain(accessKeyId);
        connection.Rendered[0].Body.ShouldNotContain(accessKeyId);
    }

    [Fact]
    public async Task ADestinationOfTheTenantsOwnIsRefusedBeforeAnythingIsAppliedAndNamesTheProperty() {
        // ⚠ #91 refused an EMPTY destination; #30 turns it round. A named destination has no
        // credentials this api-version can carry, and CloudNativePG's webhook refuses a store with
        // none, so the only honest place to say so is here, before the apply, naming the tenant's
        // property rather than the operator's field.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId, backupDestination: "s3://tenant-bucket/postgres"));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("the body says the same thing on every pass");
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe("/properties/backup/destinationPath");
        outcome.Error.Message.ShouldContain("/properties/backup/enabled to false");
        connection.Applied.ShouldBeEmpty("a refused server must leave no half-built Cluster behind");

        // Backups off is a body the definition admits whatever the destination says: no backup block,
        // no Secret, no key.
        var body = JsonNode.Parse(PostgresServers.Body(ClusterId, backupDestination: "s3://tenant-bucket/postgres"))!.AsObject();
        body["properties"]!["backup"]!["enabled"] = false;
        using var withoutBackups = JsonDocument.Parse(body.ToJsonString());

        await Reconcile(connection, withoutBackups.RootElement);

        connection.Applied.ShouldNotBeEmpty();
        connection.Applied.ShouldNotContain(static x => x.Target.Kind.Kind == "Secret");
        Spec(connection.Rendered[0].Body).ContainsKey("backup").ShouldBeFalse();
    }

    [Fact]
    public async Task AStoreThatRefusesIsRetriedAndAppliesNothing() {
        // The store is a network call on the pass. An unreachable store is a server still coming, and
        // a Cluster applied without its archive's credential would archive into nothing.
        var connection = new RecordingConnection();
        var refusing = new InMemoryObjectStoreGrants { Refuse = true };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var address = Address("refused", TenantA, SubscriptionA);

        var outcome = await new PostgresServerReconciler(new FixedClock()).ReconcileAsync(
            new ReconcileContext(address, PostgresServers.V2026, desired.RootElement, null, ReconcileDriver.NamespaceFor(address), connection, Vault, new NullLog()) {
                SecretWriter = Vault,
                Grants = refusing
            },
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeTrue("an unreachable store is transient");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADeploymentWithNoStoreFailsTheServerOnceAndNamesTheWayOut() {
        // ⚠ #30's review: the AppHost wires no grants, and the refusing default's InternalError is
        // retryable, so every default-bodied server there stayed Creating for the operation's full hour.
        // No store is not a store that is slow to answer.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var address = Address("unwired", TenantA, SubscriptionA);

        var outcome = await new PostgresServerReconciler(new FixedClock()).ReconcileAsync(
            new ReconcileContext(address, PostgresServers.V2026, desired.RootElement, null, ReconcileDriver.NamespaceFor(address), connection, Vault, new NullLog()) {
                SecretWriter = Vault,
                Grants = new RefusingObjectStoreGrants()
            },
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("waiting does not wire a store");
        outcome.Error!.Target.ShouldBe("/properties/backup/enabled");
        outcome.Error.Message.ShouldContain("backup.enabled to false");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void ARestoreBootstrapsFromTheRecoveryPointAndStillArchivesToItsOwnBucket() {
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId, recoveryPoint: "observed-20260924-0100"));
        var store = PostgresServers.BackupStore.For(Guid.NewGuid(), "restored", InMemoryObjectStoreGrants.Endpoint);

        var spec = Spec(PostgresServers.ClusterJson("restored", desired.RootElement, store));

        var bootstrap = spec["bootstrap"]!.AsObject();
        bootstrap.ContainsKey("initdb").ShouldBeFalse("a restore that also ran initdb would be two bootstraps");
        bootstrap["recovery"]!["backup"]!["name"]!.GetValue<string>().ShouldBe("observed-20260924-0100");
        bootstrap["recovery"]!["database"]!.GetValue<string>().ShouldBe("app");
        spec["backup"]!["barmanObjectStore"]!["destinationPath"]!.GetValue<string>().ShouldBe(store.DestinationPath);

        var rendered = JsonNode.Parse(PostgresServers.ClusterJson("restored", desired.RootElement, store))!.AsObject();
        rendered["kind"] = "Cluster";
        PostgresServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        using var fresh = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        PostgresServers.Matches(rendered.ToJsonString(), fresh.RootElement)
            .ShouldBeFalse("a Cluster restored from a point is not the Cluster a new server's body describes");
    }

    [Fact]
    public async Task StatementPoolingIsRefusedBeforeAnythingIsAppliedAndNamesTheProperty() {
        // ⚠ THE REVIEW OF #91's SECOND FINDING FOR THIS FAMILY, and the first the variant row found
        // on its own: the schema publishes PgBouncer's three modes and CloudNativePG's Pooler admits
        // two of them (spec.pgbouncer.poolMode: enum session, transaction), so `statement` was a
        // value the API accepted and no operator could honour — the Cluster applied, the Pooler was
        // refused naming the operator's field. The value cannot leave an immutable api-version, so
        // the reconciler refuses first, terminal, naming the tenant's property and both ways out.
        var connection = new RecordingConnection();
        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!["pooling"]!["mode"] = "statement";
        using var desired = JsonDocument.Parse(body.ToJsonString());

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("the body says the same thing on every pass");
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe("/properties/pooling/mode");
        outcome.Error.Message.ShouldContain("\"session\" and \"transaction\"");
        outcome.Error.Message.ShouldContain("/properties/pooling/enabled to false");
        connection.Applied.ShouldBeEmpty("a refused server must leave no Cluster without its Pooler behind");

        // Pooling off is a body the definition admits whatever the mode says: no Pooler is rendered.
        body["properties"]!["pooling"]!["enabled"] = false;
        using var withoutPooling = JsonDocument.Parse(body.ToJsonString());

        await Reconcile(connection, withoutPooling.RootElement);

        connection.Rendered.ShouldHaveSingleItem().Target.Kind.Kind.ShouldBe("Cluster");
    }

    [Fact]
    public async Task AsynchronousReplicationRendersNoSynchronousBlockAtAll() {
        // ⚠ The CRD has no "asynchronous" member — asynchronous IS the absence of the block. Writing
        // an empty one would put the field under this field manager's ownership forever under
        // server-side apply, which is a field the tenant's own controller could then never set.
        //
        // ⚠ Asserted at the place the definition declares the block AND at the place the renderer
        // used to put it. Until the review of #91 this test checked only that `postgresql_synchronous`
        // was absent — which a renderer that spelled the key wrong when the flag was ON satisfied
        // just as well. The presence half is SynchronousReplicationRendersTheBlockWhereTheDefinitionDeclaresIt.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        await Reconcile(connection, desired.RootElement);

        var spec = Spec(connection.Rendered[0].Body);
        spec["postgresql"]!.AsObject().ContainsKey("synchronous").ShouldBeFalse();
        spec.ContainsKey("postgresql_synchronous").ShouldBeFalse();
    }

    [Fact]
    public async Task SynchronousReplicationRendersTheBlockWhereTheDefinitionDeclaresIt() {
        // ⚠ THE REVIEW OF #91's FINDING FOR THIS FAMILY. With the flag on, ClusterJson wrote
        // `spec.postgresql_synchronous`, a sibling of `postgresql`; CloudNativePG declares the block
        // as `Synchronous` on PostgresConfiguration (api/v1/cluster_types.go), so the committed
        // definition knows `spec.postgresql.synchronous` and nothing under spec by the other name.
        // The apply patch refused it as a field not declared in schema — on every server created
        // with synchronous replication, and on none of the default bodies the suites converge, which
        // is how it outlived #91 itself. The shared suite's
        // EveryPropertyVariantTheSchemaAdmitsRendersAShapeTheDefinitionAdmits renders this flag's
        // other value against the definition; this pins the exact placement and the two members the
        // definition requires there.
        var connection = new RecordingConnection();
        var body = JsonNode.Parse(PostgresServers.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["synchronousReplication"] = true;
        using var desired = JsonDocument.Parse(body.ToJsonString());

        await Reconcile(connection, desired.RootElement);

        var spec = Spec(connection.Rendered[0].Body);
        spec.ContainsKey("postgresql_synchronous")
            .ShouldBeFalse(
                "`postgresql_synchronous` is not a field of CloudNativePG's Cluster spec; the apply patch refuses it"
            );

        var synchronous = spec["postgresql"]!["synchronous"].ShouldNotBeNull().AsObject();
        synchronous["method"]!.GetValue<string>().ShouldBe("any");
        synchronous["number"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void MatchesIsASubsetTestSoTheOperatorsOwnStatusIsNotDrift() {
        // CloudNativePG writes a large `status` and adds fields of its own to `spec`. Demanding an
        // exact match would turn the operator's bookkeeping into a permanent drift report and a
        // permanent re-apply loop.
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        var rendered = JsonNode.Parse(
            PostgresServers.ClusterJson(
                "subset",
                desired.RootElement,
                PostgresServers.BackupStore.For(Guid.NewGuid(), "subset", InMemoryObjectStoreGrants.Endpoint)
            )
        )!.AsObject();
        rendered["kind"] = "Cluster";
        rendered["status"] = new JsonObject { ["readyInstances"] = 2 };
        rendered["spec"]!.AsObject()["addedByTheOperator"] = "x";

        PostgresServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        rendered["spec"]!.AsObject()["instances"] = 4;
        PostgresServers.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeFalse();

        PostgresServers.Matches("not json at all", desired.RootElement).ShouldBeFalse();
    }

    // ── The claims' custody — issue #69 ────────────────────────────────────────────────────────
    //
    // ⚠ What these prove and what they cannot. CloudNativePG creates a server's claims itself and
    // stamps a controller reference on each, so the Cluster's deletion garbage-collects them; the
    // teardown has to take the reference off first, and a restore has to put a fresh one on before
    // the operator's first pass. The RecordingConnection below models ownership as the API server
    // holds it and nothing more: it does not garbage-collect (FakeKubeCluster does, and the shared
    // conformance case is where the survival across a soft delete is asserted), and it is not the
    // operator, so what the operator does with a re-owned claim is read from v1.30.0's source and
    // recorded on the reconciler rather than observed here.

    [Fact]
    public async Task ATeardownPausesTheOperatorAndDetachesEveryClaimBeforeItDeletesAnything() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        // ⚠ Serials 1 and 3, not 1 and 2: a failover replaced instance 2, which is the shape a name
        // predicted from the replica count would miss. And the operator's own labels, because the
        // selector is the operator's label and not one this platform wrote.
        var context = Context(connection, desired.RootElement);
        var owner = PostgresServers.ClusterOwner(
            "observed",
            connection.UidOf(PostgresServers.ClusterRef(context.Namespace, "observed"))
        );
        var claims = FailedOverClaims
            .Select(name => PlantClaim(connection, context.Namespace, name, "observed", owner))
            .ToList();

        connection.Events.Clear();

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(context, TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue(torn.ToString());

        // The order, as one sequence: the pause, then every detach, then the deletes.
        var firstDelete = connection.Events.FindIndex(static x => x.StartsWith("delete:", StringComparison.Ordinal));
        firstDelete.ShouldBeGreaterThan(0);
        connection.Events[0].ShouldBe("apply:Cluster/observed(paused)");
        connection.Events.Take(firstDelete)
            .Count(static x => x.StartsWith("detach:", StringComparison.Ordinal))
            .ShouldBe(4);
        connection.Events.Skip(firstDelete).ShouldBe(["delete:Pooler/observed-pooler", "delete:Cluster/observed"]);

        foreach (var claim in claims) {
            connection.Objects.ContainsKey(RecordingConnection.Key(claim)).ShouldBeTrue($"'{claim}' is gone");
            connection.ControllerOf(claim).ShouldBeNull($"'{claim}' still names the Cluster that was deleted");
        }
    }

    [Fact]
    public async Task ASecondTeardownPassOverAGoneClusterDetachesNothingAndConverges() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var context = Context(connection, desired.RootElement);
        PlantClaim(connection, context.Namespace, "observed-1", "observed", null);

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(context, TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue(torn.ToString());
        connection.OwnerChanges.ShouldBeEmpty();
        connection.Applied.ShouldBeEmpty("a teardown over an absent Cluster applied a pause to nothing");
    }

    [Fact]
    public async Task ATeardownThatCannotListItsClaimsDeletesNothingAndRetries() {
        // ⚠ The fail-closed half. "I could not find out which claims are mine" must never become
        // "there are none", because the delete that follows would take them all.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.RefuseLists = ErrorCode.InternalError;

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        torn.Retryable.ShouldBeTrue();
        connection.Deleted.ShouldBeEmpty("the Cluster was deleted with its claims unaccounted for");
    }

    [Fact]
    public async Task ATeardownThatCannotDetachAClaimDeletesNothingAndRetries() {
        var connection = new RecordingConnection { RefuseOwnerChanges = ErrorCode.Conflict };
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var context = Context(connection, desired.RootElement);
        var owner = PostgresServers.ClusterOwner(
            "observed",
            connection.UidOf(PostgresServers.ClusterRef(context.Namespace, "observed"))
        );
        var claim = PlantClaim(connection, context.Namespace, "observed-1", "observed", owner);

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(context, TestContext.Current.CancellationToken);

        torn.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        torn.Retryable.ShouldBeTrue("the API server not accepting a patch is not a reason to give the claim up");
        torn.Error!.Message.ShouldContain("observed-1");
        connection.Deleted.ShouldBeEmpty("the Cluster was deleted while a claim still named it");
        connection.ControllerOf(claim).ShouldNotBeNull();
    }

    [Fact]
    public async Task AConflictOnThePauseDoesNotBlockTheTeardown() {
        // ⚠ The pause is courtesy and the detach is correctness. A tenant's own controller holding a
        // field of the Cluster makes the pause conflict, and a delete the tenant cannot perform
        // because of their own edit would be worse than one status line in their event stream.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var context = Context(connection, desired.RootElement);
        var owner = PostgresServers.ClusterOwner(
            "observed",
            connection.UidOf(PostgresServers.ClusterRef(context.Namespace, "observed"))
        );
        var claim = PlantClaim(connection, context.Namespace, "observed-1", "observed", owner);

        connection.ConflictField = ".spec.instances";

        var torn = await new PostgresServerReconciler(new FixedClock())
            .DeleteAsync(context, TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue(torn.ToString());
        connection.ControllerOf(claim).ShouldBeNull();
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["Pooler", "Cluster"]);
    }

    [Fact]
    public async Task ARestoreCreatesTheClusterPausedHandsItTheClaimsAndThenReleasesTheOperator() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var context = Context(connection, desired.RootElement);

        // The world a soft delete leaves: the claims, labelled by the operator and owned by nobody.
        var claims = FailedOverClaims
            .Select(name => PlantClaim(connection, context.Namespace, name, "observed", null))
            .ToList();

        var back = await Reconcile(connection, desired.RootElement);

        back.IsConverged.ShouldBeTrue(back.ToString());

        var applies = connection.Events.Where(static x => x.StartsWith("apply:Cluster", StringComparison.Ordinal))
            .ToList();
        applies.First()
            .ShouldBe("apply:Cluster/observed(paused)", "the operator saw the Cluster before the claims were its");
        applies.Last().ShouldBe("apply:Cluster/observed", "the operator was never released");

        var adoptions = connection.Events.Where(static x => x.StartsWith("adopt:", StringComparison.Ordinal)).ToList();
        adoptions.Count.ShouldBe(4);
        connection.Events.IndexOf(adoptions[^1]).ShouldBeLessThan(connection.Events.IndexOf(applies.Last()));

        var uid = connection.UidOf(PostgresServers.ClusterRef(context.Namespace, "observed"));
        uid.ShouldNotBeEmpty();

        foreach (var claim in claims) {
            var controller = connection.ControllerOf(claim);
            controller.ShouldNotBeNull($"'{claim}' was not handed to the new Cluster");
            controller.Uid.ShouldBe(uid, "the owner reference names a uid the API server did not issue");
            controller.Kind.ShouldBe("Cluster");
        }

        PostgresServers.IsPaused(
            connection.Objects[RecordingConnection.Key(PostgresServers.ClusterRef(context.Namespace, "observed"))]
        )
            .ShouldBeFalse("a converged restore left the operator paused");
    }

    [Fact]
    public async Task AFreshCreateWithNoRetainedClaimsNeverPausesAndASteadyPassNeverLists() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Events.ShouldNotContain(x => x.EndsWith("(paused)", StringComparison.Ordinal));
        connection.OwnerChanges.ShouldBeEmpty();
        connection.Lists.Count.ShouldBe(1, "a create with no Cluster asks once whether a previous life left claims");

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Lists.Count.ShouldBe(1, "a pass over a running Cluster listed claims it has no reason to look for");
    }

    [Fact]
    public async Task RetainedVolumesAreTheClaimsTheOperatorLabelledAndNotACountOffTheBody() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(PostgresServers.Body(ClusterId));
        var context = Context(connection, desired.RootElement);

        PlantClaim(connection, context.Namespace, "observed-1", "observed", null);
        PlantClaim(connection, context.Namespace, "observed-3-wal", "observed", null);
        // Somebody else's server in the same namespace, wearing a name a pattern would match.
        PlantClaim(connection, context.Namespace, "observed-2", "other", null);

        var retained = await new PostgresServerReconciler(new FixedClock())
            .RetainedVolumesAsync(context, TestContext.Current.CancellationToken);

        retained.IsSuccess.ShouldBeTrue(retained.Error?.Message);
        retained.GetValueOrThrow().Select(static x => x.Claim.Name).Order().ShouldBe(["observed-1", "observed-3-wal"]);
        retained.GetValueOrThrow().ShouldAllBe(x => x.OwnedBy[PostgresServers.ClaimLabel] == "observed");
        connection.Lists.ShouldHaveSingleItem().Selector.ShouldBe("cnpg.io/cluster=observed");

        var blind = await new PostgresServerReconciler(new FixedClock())
            .RetainedVolumesAsync(Context(null, desired.RootElement), TestContext.Current.CancellationToken);

        blind.IsFailure.ShouldBeTrue(
            "with no cluster to ask, an empty answer would converge a purge over disks that are still there"
        );
    }

    /// <summary>
    ///     The claims of a two-instance server that has failed over once: serials 1 and 3, each with
    ///     its WAL volume. ⚠ Not 1 and 2 — the gap is the point, because a name predicted from the
    ///     replica count would miss serial 3.
    /// </summary>
    static readonly string[] FailedOverClaims = ["observed-1", "observed-1-wal", "observed-3", "observed-3-wal"];

    /// <summary>Plants a claim as CloudNativePG creates one: its labels, its serial, and its controller.</summary>
    static ObjectRef PlantClaim(
        RecordingConnection connection,
        string ns,
        string name,
        string cluster,
        OwnerRef? owner
    ) {
        var target = new ObjectRef { Kind = RetainedVolume.ClaimKind, Namespace = ns, Name = name };

        var metadata = new JsonObject {
            ["name"] = name,
            ["namespace"] = ns,
            ["labels"] = new JsonObject {
                [PostgresServers.ClaimLabel] = cluster,
                ["cnpg.io/instanceName"] = name.EndsWith("-wal", StringComparison.Ordinal) ? name[..^4] : name,
                ["cnpg.io/pvcRole"] = name.EndsWith("-wal", StringComparison.Ordinal) ? "PG_WAL" : "PG_DATA",
                ["app.kubernetes.io/managed-by"] = "cloudnative-pg"
            },
            ["annotations"] = new JsonObject { ["cnpg.io/pvcStatus"] = "ready" }
        };

        if (owner is not null) {
            metadata["ownerReferences"] = new JsonArray(KubeJson.OwnerReference(owner));
        }

        connection.Plant(
            target,
            new JsonObject {
                ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["metadata"] = metadata
            }.ToJsonString()
        );

        return target;
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The apply order a pass must keep — the Pooler references the Cluster by name.</summary>
    static readonly string[] ClusterThenPooler = ["Cluster", "Pooler"];

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new PostgresServerReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        PostgresServerReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                PostgresServers.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                Vault,
                new NullLog()
            ) {
                SecretWriter = Vault,
                Grants = Grants
            },
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(IKubeClusterConnection? connection, JsonElement desired) {
        var address = Address("observed", TenantA, SubscriptionA);

        return new(
            address,
            PostgresServers.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            Vault,
            new NullLog()
        ) {
            SecretWriter = Vault,
            Grants = Grants
        };
    }

    /// <summary>
    ///     The vault every hand-built context reads and mints through. ⚠ Shared across the class: the
    ///     paths are per resource GUID, and a server whose key was minted by an earlier test reads it
    ///     back, which is the second-pass behaviour the reconciler relies on anyway.
    /// </summary>
    static readonly InMemorySecretVault Vault = new();

    /// <summary>The store every hand-built context is given buckets and keys by.</summary>
    static readonly InMemoryObjectStoreGrants Grants = new();

    /// <summary>An address in a named tenant and its own subscription.</summary>
    static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            PostgresServers.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    /// <summary>The store key of the pooler the default context's address implies.</summary>
    static string PoolerKey =>
        RecordingConnection.Key(
            PostgresServers.PoolerRef(
                ReconcileDriver.NamespaceFor(Address("observed", TenantA, SubscriptionA)),
                "observed"
            )
        );
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>
    ///     The applies of CloudNativePG objects, without the backup key's Secret every pass with backups
    ///     on writes first since #30.
    /// </summary>
    public List<KubeCommand> Rendered => [.. Applied.Where(static x => x.Target.Kind.Kind != "Secret")];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; set; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    /// <summary>
    ///     The code every apply fails with, or <see langword="null" /> — the API server <i>refusing</i>,
    ///     as <c>KubeFailures.Classify</c> reports one, rather than being out of reach.
    /// </summary>
    public ErrorCode? RefuseWith { get; init; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (RefuseWith is { } refusal) {
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(
                    refusal,
                    $"Cluster {ClusterId:D} refused to apply {command.Target}. The object was not written."
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
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            // ⚠ A uid, minted on create and kept across updates, because the restore path reads the
            // Cluster's uid back to name it as the claims' owner — and a double that echoed the
            // applied body would hand it an empty one. The real API server issues one on every create.
            var body = JsonNode.Parse(command.Body)!.AsObject();
            var metadata = body["metadata"]!.AsObject();
            var existed = Objects.TryGetValue(Key(command.Target), out var previous);
            var uid = existed ? KubeJson.UidOf(JsonNode.Parse(previous!)) : string.Empty;
            metadata["uid"] = uid.Length > 0 ? uid : $"uid-{Interlocked.Increment(ref minted)}";
            Objects[Key(command.Target)] = body.ToJsonString();
            Events.Add(
                $"apply:{command.Target.Kind.Kind}/{command.Target.Name}"
                + (command.Annotations.ContainsKey(PostgresServers.PauseAnnotation) ? "(paused)" : string.Empty)
            );
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    int minted;

    /// <summary>Every write in the order it happened — applies, ownership changes and deletes together.</summary>
    /// <remarks>
    ///     ⚠ The order is the assertion for the teardown: a detach after a delete would lose the
    ///     race with the garbage collector, and three separate lists cannot say which came first.
    /// </remarks>
    public List<string> Events { get; } = [];

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
            Events.Add($"delete:{command.Target.Kind.Kind}/{command.Target.Name}");
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>Every ownership change, in order: the claim and the owner written, or <see langword="null" /> for a detach.</summary>
    public List<(ObjectRef Target, OwnerRef? Owner)> OwnerChanges { get; } = [];

    /// <summary>The kind and name of every object listed by selector, in order, with the selector asked for.</summary>
    public List<(GroupVersionKind Kind, string Selector)> Lists { get; } = [];

    /// <summary>
    ///     When set, every <see cref="ListAsync" /> fails with this code — the cluster refusing or
    ///     not answering the one question a teardown has to ask before it deletes.
    /// </summary>
    public ErrorCode? RefuseLists { get; set; }

    /// <summary>
    ///     Plants an object as an operator would — labels, annotations and owner reference as given,
    ///     never through the apply path.
    /// </summary>
    public void Plant(ObjectRef target, string json) => Objects[Key(target)] = json;

    /// <summary>The stored object's <c>metadata.uid</c>, or empty.</summary>
    public string UidOf(ObjectRef target) =>
        Objects.TryGetValue(Key(target), out var json) ? KubeJson.UidOf(JsonNode.Parse(json)) : string.Empty;

    /// <summary>The stored object's controller, or <see langword="null" />.</summary>
    public OwnerRef? ControllerOf(ObjectRef target) =>
        Objects.TryGetValue(Key(target), out var json) ? KubeJson.ControllerOf(JsonNode.Parse(json)) : null;

    /// <summary>
    ///     ⚠ Overrides the interface's fail-closed default, and the default is why the first run of
    ///     this suite after the teardown learned to detach went 14 red: a double that cannot list
    ///     answers "cannot list", and a teardown that cannot list its claims must not delete.
    /// </summary>
    public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(kind);
        Lists.Add((kind, labelSelector));

        if (RefuseLists is { } refusal) {
            return Task.FromResult(
                Result<IReadOnlyList<KubeObjectSummary>>.Failure(
                    refusal,
                    $"Cluster {ClusterId:D} did not answer the list."
                )
            );
        }

        var wanted = labelSelector.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static x => x[0], static x => x.Length > 1 ? x[1] : string.Empty, StringComparer.Ordinal);

        var found = new List<KubeObjectSummary>();

        foreach (var (key, json) in Objects) {
            var parts = key.Split('/', 3);

            if (parts[0] != kind.Kind || parts[1] != ns) {
                continue;
            }

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);

            if (JsonNode.Parse(json) is JsonObject root
                && root["metadata"] is JsonObject metadata
                && metadata["labels"] is JsonObject written) {
                foreach (var (name, value) in written) {
                    labels[name] = value?.GetValue<string>() ?? string.Empty;
                }
            }

            if (wanted.All(pair => labels.TryGetValue(pair.Key, out var value) && value == pair.Value)) {
                found.Add(new() { Kind = kind, Namespace = ns, Name = parts[2], Labels = labels });
            }
        }

        return Task.FromResult(Result<IReadOnlyList<KubeObjectSummary>>.Success(found));
    }

    /// <summary>When set, every ownership change fails with this code and changes nothing.</summary>
    public ErrorCode? RefuseOwnerChanges { get; init; }

    /// <summary>The merge patch, as the API server would hold it: the list replaced, or the key gone.</summary>
    public Task<Result> SetOwnerAsync(
        ObjectRef target,
        OwnerRef? owner,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);
        OwnerChanges.Add((target, owner));

        if (RefuseOwnerChanges is { } refusal) {
            return Task.FromResult(
                Result.Failure(refusal, $"Cluster {ClusterId:D} did not accept the change to '{target}'.")
            );
        }

        if (!Objects.TryGetValue(Key(target), out var json) || JsonNode.Parse(json) is not JsonObject root) {
            return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here."));
        }

        Events.Add($"{(owner is null ? "detach" : "adopt")}:{target.Name}");

        if (root["metadata"] is not JsonObject metadata) {
            metadata = [];
            root["metadata"] = metadata;
        }

        if (owner is null) {
            metadata.Remove("ownerReferences");
        } else {
            metadata["ownerReferences"] = new JsonArray(KubeJson.OwnerReference(owner));
        }

        Objects[Key(target)] = root.ToJsonString();
        return Task.FromResult(Result.Success);
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The sample's stub keys by namespace and name only,
    ///     which is enough for one object per resource; this provider applies two, so the Pooler would
    ///     overwrite the Cluster. The namespace is in the key because the cross-tenant test puts the
    ///     same resource name in two tenants, which is the only shape in which one singleton
    ///     reconciler serving both can be caught mixing them.
    /// </summary>
    internal static string Key(ObjectRef target) => target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
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
