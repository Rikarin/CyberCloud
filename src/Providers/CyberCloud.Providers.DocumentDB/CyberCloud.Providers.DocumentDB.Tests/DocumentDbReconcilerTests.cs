using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB.Tests;

/// <summary>
///     The document-database reconciler against a connection that misbehaves in the ways a real
///     cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Storage.Tests</c> however identical they look. That duplication is
///     the price of the rule and is worth naming rather than apologising for: the alternative is a
///     line in <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class DocumentDbReconcilerTests {
    // ── Failure class (b): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new DocumentDbAccountReconciler(new FixedClock()))
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
        var reconciler = new DocumentDbAccountReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming an account `orders` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("orders", TenantA, SubscriptionA);
        var bob = Address("orders", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            DocumentDbAccounts.Body(ClusterId, instances: 2, storageSize: "20Gi", gatewayReplicas: 2)
        );

        using var bobBody = JsonDocument.Parse(
            DocumentDbAccounts.Body(ClusterId, instances: 5, storageSize: "500Gi", gatewayReplicas: 7)
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        // Four passes × four objects.
        var clusters = connection.Applied.Where(x => x.Target.Kind.Kind == "Cluster").ToList();
        var deployments = connection.Applied.Where(x => x.Target.Kind.Kind == "Deployment").ToList();

        clusters.Count.ShouldBe(4);
        deployments.Count.ShouldBe(4);

        Spec(clusters[0].Body)["instances"]!.GetValue<int>().ShouldBe(2);
        Spec(clusters[1].Body)["instances"]!.GetValue<int>().ShouldBe(5);
        Spec(clusters[2].Body)["instances"]!.GetValue<int>()
            .ShouldBe(2, "tenant A's instance count came back as tenant B's");

        Spec(clusters[3].Body)["storage"]!["size"]!.GetValue<string>()
            .ShouldBe("500Gi", "tenant B's volume size came back as tenant A's");

        // ⚠ AND THE SECOND WORKLOAD SEPARATELY, because a cache keyed on the resource NAME would
        // return one tenant's whole render for the other and the Cluster assertions alone could not
        // tell a per-object cache from a per-resource one.
        Spec(deployments[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(2, "tenant A's gateway replica count came back as tenant B's");

        clusters[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        clusters[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's account rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        clusters[0].Target.Namespace.ShouldNotBe(clusters[1].Target.Namespace);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 3)]
    public async Task EveryRenderedObjectIsAlsoReadBack(bool monitoring, int expected) {
        // ⚠ THE PAIR NOTHING BUT THIS HOLDS TOGETHER. Rendered() and Targets() are two lists in one
        // file and the compiler does not compare them: an object rendered and not read back is one
        // the loop reports Converged without ever having observed, and one read back and never
        // rendered is a resource that never converges. Both settings of monitoring.enabled, because
        // the PodMonitor is the conditional object and a conditional in one list and not the other is
        // exactly how the two drift apart.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(
            monitoring
                ? DocumentDbAccounts.Body(ClusterId)
                : WithMonitoring(DocumentDbAccounts.Body(ClusterId), false)
        );

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.Count.ShouldBe(expected);

        read.ShouldBe(
            applied,
            "the reconciler applied " + applied.Count + " object(s) and read back " + read.Count
            + ". An object applied and not read back is one the loop reports Converged without ever "
            + "having observed."
        );
    }

    [Fact]
    public async Task TheClusterIsAppliedBeforeTheDeploymentThatMountsItsSecret() {
        // ⚠ ORDER, AND IT IS NOT COSMETIC. The gateway pods mount the Secret CloudNativePG generates
        // from the Cluster. Applying the Deployment first produces a minute of ContainerCreating that
        // looks identical to the state charts/managed/seaweedfs is PERMANENTLY stuck in — an operator
        // reading a dashboard cannot tell "waiting for its own database" from "waiting for a Secret
        // nobody will ever write".
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var kinds = connection.Applied.Select(x => x.Target.Kind.Kind).ToList();

        kinds.IndexOf("Cluster").ShouldBeLessThan(kinds.IndexOf("Deployment"));
    }

    [Fact]
    public async Task DeleteRemovesTheDeploymentBeforeTheClusterThatOwnsItsSecret() {
        // ⚠ The mirror of the apply order, and the reason DeleteAsync reverses the list. A pod that
        // is still terminating must never be a pod whose credential has already vanished.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new DocumentDbAccountReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);

        var kinds = connection.Deleted.Select(x => x.Kind.Kind).ToList();

        kinds.Count.ShouldBe(4);
        kinds.IndexOf("Deployment").ShouldBeLessThan(kinds.IndexOf("Cluster"));
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. Nothing in the four renderers counts, appends or timestamps — in particular there
        // is no configuration-digest annotation on the pod template, which is the usual Helm idiom
        // and which would make every apply non-idempotent.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

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
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.replicas` on the gateway
        // Deployment is the plausible one on this type: it is the throughput axis and it is exactly
        // what a tenant's own HorizontalPodAutoscaler owns. Forcing would silently undo them every
        // pass.
        var connection = new RecordingConnection { ConflictField = ".spec.replicas" };
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("replicas");
    }

    // ── The credential, at the object ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheGatewayReadsItsCredentialFromTheOPERATORSSecretAndNeverCarriesAValue() {
        // ⚠ THE SECRET NAME IS THE OPERATOR'S, DERIVED FROM THE CLUSTER'S NAME AND NOT FROM THE
        // RESOURCE'S. CloudNativePG names it `{cluster}-superuser`, and this provider's cluster is
        // `{resource}-pg` — so the Secret is `{resource}-pg-superuser`. A reference to
        // `{resource}-superuser` would name nothing, and the pods would sit in ContainerCreating with
        // a message about a missing Secret that does not say whose fault it is.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var deployment = connection.Applied.Single(x => x.Target.Kind.Kind == "Deployment");
        var env = JsonNode.Parse(deployment.Body)!["spec"]!["template"]!["spec"]!["containers"]!
            .AsArray()[0]!["env"]!.AsArray();

        var refs = env.OfType<JsonObject>()
            .Where(x => x["valueFrom"] is not null)
            .Select(x => x["valueFrom"]!["secretKeyRef"]!["name"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        refs.ShouldBe(["observed-pg-superuser"]);

        // ⚠ AND THE ENV LIST ORDER IS LOAD-BEARING. Kubernetes' $(VAR) expansion reads only entries
        // defined EARLIER in the same list, so a reorder that put the URL first would send the
        // literal "$(FERRETDB_PGUSER)" to PostgreSQL as a username — and the failure is an
        // authentication error in a pod log, not anything the control plane sees.
        var names = env.OfType<JsonObject>().Select(x => x["name"]!.GetValue<string>()).ToList();

        names.IndexOf("FERRETDB_PGUSER").ShouldBeLessThan(names.IndexOf("FERRETDB_POSTGRESQL_URL"));
        names.IndexOf("FERRETDB_PGPASSWORD").ShouldBeLessThan(names.IndexOf("FERRETDB_POSTGRESQL_URL"));
    }

    [Fact]
    public async Task TheDsnDoesNotUseTheSuperuserSecretsOwnUriKey() {
        // ⚠ THE SHORTCUT THAT LOOKS RIGHT AND PRODUCES A POD THAT NEVER CONNECTS. Every CloudNativePG
        // generated Secret carries a ready-made `uri` key — pkg/specs/secrets.go — and projecting it
        // is the obvious way to avoid assembling a DSN. It is correct for the APPLICATION secret and
        // wrong for the SUPERUSER one: internal/controller/cluster_create.go passes `"*"` as that
        // secret's dbname, so its uri is `postgresql://postgres:…@…:5432/*`, naming a database that
        // does not exist.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var deployment = connection.Applied.Single(x => x.Target.Kind.Kind == "Deployment");
        var env = JsonNode.Parse(deployment.Body)!["spec"]!["template"]!["spec"]!["containers"]!
            .AsArray()[0]!["env"]!.AsArray();

        env.OfType<JsonObject>()
            .Where(x => x["valueFrom"] is not null)
            .Select(x => x["valueFrom"]!["secretKeyRef"]!["key"]!.GetValue<string>())
            .ShouldNotContain(
                "uri",
                "the superuser Secret's uri key names the database `*`, which does not exist. The two "
                + "keys that are safe to read from that Secret are username and password."
            );

        var url = env.OfType<JsonObject>()
            .Single(x => x["name"]!.GetValue<string>() == "FERRETDB_POSTGRESQL_URL")["value"]!
            .GetValue<string>();

        url.ShouldBe(
            "postgres://$(FERRETDB_PGUSER):$(FERRETDB_PGPASSWORD)@observed-pg-rw:5432/postgres"
        );
    }

    [Fact]
    public async Task TheListenAndDebugAddressesAreWrittenRatherThanInheritedFromTheImage() {
        // ⚠ THE COUPLING THIS PROVIDER REFUSES TO DEPEND ON. cmd/ferretdb/main.go defaults
        // --listen-addr to 127.0.0.1:27017 and --debug-addr to 127.0.0.1:8088; only
        // build/ferretdb/production.Dockerfile's ENV lines make the process reachable from outside its
        // own pod. If that image ever stops setting them, a Deployment that inherited them binds to
        // loopback — the Service resolves to a port nothing answers, the kubelet's probes fail against
        // the pod IP, and the PodMonitor scrapes nothing. Same shape as charts/managed/seaweedfs'
        // WORKDIR/mountPath finding; worse consequence.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var deployment = connection.Applied.Single(x => x.Target.Kind.Kind == "Deployment");
        var env = JsonNode.Parse(deployment.Body)!["spec"]!["template"]!["spec"]!["containers"]!
            .AsArray()[0]!["env"]!.AsArray()
            .OfType<JsonObject>()
            .Where(x => x["value"] is not null)
            .ToDictionary(x => x["name"]!.GetValue<string>(), x => x["value"]!.GetValue<string>(), StringComparer.Ordinal);

        env["FERRETDB_LISTEN_ADDR"].ShouldBe(":27017");
        env["FERRETDB_DEBUG_ADDR"].ShouldBe(":8088");

        // ⚠ And neither may ever be written as a loopback address, which is what the binary would do
        // on its own. A literal here is the test — deriving it from the constant would compare the
        // constant to itself.
        env["FERRETDB_LISTEN_ADDR"].ShouldNotStartWith("127.0.0.1");
        env["FERRETDB_DEBUG_ADDR"].ShouldNotStartWith("127.0.0.1");
    }

    [Fact]
    public async Task TheRenderedObjectsCarryNoSecretValueAnywhere() {
        // docs/plan/05: credentials never in grain state, and never in a rendered object either. The
        // Deployment names a Secret and its keys; it never carries one.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var command in connection.Applied) {
            foreach (var forbidden in new[] { "stringData", "\"data\":", "PGPASSWORD=" }) {
                command.Body.ShouldNotContain(forbidden, Case.Sensitive, command.Target.Kind.Kind);
            }
        }
    }

    // ── The labels that are NOT the seven, and are the ones this provider can get wrong ──────────

    [Fact]
    public async Task TheSelectorTheDeploymentTheTemplateAndThePodMonitorAllAgree() {
        // ⚠ THE HALF OF ADR-013 THE LABELS GATE DOES NOT COVER, AND CHECKING WHICH HALF IS THE POINT.
        // The seven cybercloud.io/* labels are injected by KubeCommand and cannot be got wrong from
        // here — verified by SABOTAGE rather than assumed: rendering a metadata.labels block carrying
        // `cybercloud.io/tenant-id: not-a-tenant` leaves
        // EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations GREEN, because the
        // builder overwrites it. So that assertion is a gate over KubeCommand, and this provider's
        // sixth suite adds no coverage of it.
        //
        // ⚠ WHAT THIS PROVIDER CAN GET WRONG IS THE OTHER FOUR. app.kubernetes.io/* are written by
        // this file into THREE places that must agree — the Deployment's spec.selector, its pod
        // template, and the PodMonitor's selector — and none of them is injected by anything. If the
        // three drift apart the failure is silent in the worst way: the Deployment creates pods it
        // does not own (or the API server refuses it), the Service selects nothing and every client
        // gets a connection refused, and the PodMonitor scrapes nothing while reporting no error.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(DocumentDbAccounts.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var deployment = JsonNode.Parse(
            connection.Applied.Single(x => x.Target.Kind.Kind == "Deployment").Body
        )!;

        var selector = Labels(deployment["spec"]!["selector"]!["matchLabels"]!);
        var template = Labels(deployment["spec"]!["template"]!["metadata"]!["labels"]!);

        var service = JsonNode.Parse(
            connection.Applied.Single(x => x.Target.Kind.Kind == "Service").Body
        )!;

        var monitor = JsonNode.Parse(
            connection.Applied.Single(x => x.Target.Kind.Kind == "PodMonitor").Body
        )!;

        // ⚠ LITERALS, and they are the fourth independent copy after the two templates and
        // charts/managed/ferretdb/conformance.yaml's `additional:` block. Deriving them from
        // DocumentDbAccounts' own helper would compare the helper to itself, which is the shape that
        // let an earlier provider's casing sabotage stay green.
        selector.ShouldBe(
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["app.kubernetes.io/name"] = "ferretdb",
                ["app.kubernetes.io/instance"] = "observed",
                ["app.kubernetes.io/component"] = "gateway",
                ["app.kubernetes.io/managed-by"] = "cybercloud"
            }
        );

        template.ShouldBe(
            selector,
            "the pod template's labels differ from the Deployment's own selector. The API server "
            + "refuses that outright — and a selector is IMMUTABLE after create, so the fix is a new "
            + "resource rather than an update."
        );

        Labels(service["spec"]!["selector"]!).ShouldBe(
            selector,
            "the Service selects a different set of pods than the Deployment creates, so every client "
            + "gets a connection refused and nothing reports an error."
        );

        Labels(monitor["spec"]!["selector"]!["matchLabels"]!).ShouldBe(
            selector,
            "the PodMonitor selects a different set of pods than the Deployment creates, so it scrapes "
            + "nothing — silently, which is the exact failure docs/plan/12 § piece 6 exists to avoid."
        );

        // ⚠ AND NONE OF THE FOUR MAY EVER BE A cybercloud.io/* LABEL. A Deployment's spec.selector is
        // immutable, the pod labels have to match it, and cybercloud.io/api-version moves — so a
        // resource whose selector carried it could never be updated again. That is
        // charts/managed/nats' `pod-labels` finding, second sighting, on a Deployment rather than a
        // StatefulSet.
        selector.Keys.ShouldNotContain(x => x.StartsWith("cybercloud.io/", StringComparison.Ordinal));
    }

    static Dictionary<string, string> Labels(JsonNode node) =>
        node.AsObject().ToDictionary(x => x.Key, x => x.Value!.GetValue<string>(), StringComparer.Ordinal);

    // ── Backup: two properties, one answer ───────────────────────────────────────────────────────

    [Fact]
    public async Task BackupEnabledWithNoDestinationRendersNoBackupBlockAtAll() {
        // ⚠ THE THIRD SIGHTING OF THE CROSS-PROPERTY GAP, AND THE FAILURE IT PREVENTS IS THE WORST
        // KIND. An enabled backup with an empty destination would render
        // barmanObjectStore.destinationPath: "" — a cluster that comes up, archives nothing, and
        // reports itself as backed up. "Not backed up" is recoverable; "looks backed up" is not.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(
            WithBackup(DocumentDbAccounts.Body(ClusterId), enabled: true, destination: string.Empty)
        );

        await Reconcile(connection, body.RootElement);

        var cluster = connection.Applied.Single(x => x.Target.Kind.Kind == "Cluster");

        JsonNode.Parse(cluster.Body)!["spec"]!.AsObject().ContainsKey("backup").ShouldBeFalse(
            "a backup block was rendered with no destination. The archiver fails every WAL segment "
            + "and the cluster looks backed up."
        );
    }

    [Fact]
    public async Task BackupEnabledWithADestinationRendersTheWholeBlock() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(
            WithBackup(DocumentDbAccounts.Body(ClusterId), enabled: true, destination: "s3://t/docdb")
        );

        await Reconcile(connection, body.RootElement);

        var backup = JsonNode.Parse(
            connection.Applied.Single(x => x.Target.Kind.Kind == "Cluster").Body
        )!["spec"]!["backup"]!.AsObject();

        backup["retentionPolicy"]!.GetValue<string>().ShouldBe("14d");
        backup["barmanObjectStore"]!["destinationPath"]!.GetValue<string>().ShouldBe("s3://t/docdb");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("dddddddd-0000-4000-8000-000000000006");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new DocumentDbAccountReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        DocumentDbAccountReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                DocumentDbAccounts.V2026,
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
            DocumentDbAccounts.V2026,
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
            DocumentDbAccounts.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    static string WithMonitoring(string body, bool enabled) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["monitoring"] = new JsonObject { ["enabled"] = enabled };
        return node.ToJsonString();
    }

    static string WithBackup(string body, bool enabled, string destination) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["backup"] = new JsonObject {
            ["enabled"] = enabled, ["retentionDays"] = 14, ["destinationPath"] = destination
        };

        return node.ToJsonString();
    }
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

    public ResourceTypeName Type => DocumentDbAccounts.Type;

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

    public Guid ClusterId => Guid.Parse("dddddddd-0000-4000-8000-000000000006");

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
                ? Result<KubeObject>.Success(new() { Ref = target, Json = Kinded(json, target) })
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
    ///     ⚠ The API server returns <c>kind</c> on every object and a rendered document does not carry
    ///     one — <c>KubeCommand</c> supplies it out of band. <c>Matches</c> dispatches on <c>kind</c>
    ///     over FOUR kinds here, so a fake that echoed the stored body verbatim would send every read
    ///     down the unrecognised branch and no resource would ever converge.
    /// </summary>
    static string Kinded(string json, ObjectRef target) {
        var node = JsonNode.Parse(json)!.AsObject();
        node["kind"] = target.Kind.Kind;
        return node.ToJsonString();
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
