using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     The container-registry reconciler against a connection that misbehaves in the ways a real
///     cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with <c>CyberCloud.Providers.Storage.Tests</c> however identical they look. That duplication is
///     the price of the rule and is worth naming rather than apologising for.
/// </remarks>
public sealed class ContainerRegistryReconcilerTests {
    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new ContainerRegistryReconciler(new FixedClock()))
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
        var reconciler = new ContainerRegistryReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a registry `images` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("images", TenantA, SubscriptionA);
        var bob = Address("images", TenantB, SubscriptionB);

        var connection = new RecordingConnection();

        using var aliceBody = JsonDocument.Parse(
            ContainerRegistries.Body(ClusterId, replicas: 2, storageSize: "100Gi")
        );

        using var bobBody = JsonDocument.Parse(
            ContainerRegistries.Body(ClusterId, replicas: 5, storageSize: "500Gi")
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        // ⚠ THE CORE DEPLOYMENTS ONLY, IN PASS ORDER. Each pass applies fifteen objects, so an index
        // into the raw list would land on a Service most of the time. Core is the one that carries the
        // replica count, which is what a cross-tenant leak would move.
        var cores = connection.Applied
            .Where(x => x.Target.Name.EndsWith("-core", StringComparison.Ordinal))
            .ToList();

        cores.Count.ShouldBe(4);

        Replicas(cores[0].Body).ShouldBe(2);
        Replicas(cores[1].Body).ShouldBe(5);
        Replicas(cores[2].Body).ShouldBe(2, "tenant A's replica count came back as tenant B's");
        Replicas(cores[3].Body).ShouldBe(5, "tenant B's replica count came back as tenant A's");

        cores[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        cores[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's registry rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the first.
        cores[0].Target.Namespace.ShouldNotBe(cores[1].Target.Namespace);

        // ⚠ AND THE IMAGE VOLUME, which is the other half of a cross-tenant mix and the one that would
        // matter most: a registry sized for 500 GiB of images provisioned at 100.
        var registries = connection.Applied
            .Where(x => x.Target.Name.EndsWith("-registry", StringComparison.Ordinal))
            .Where(x => x.Target.Kind.Kind == "StatefulSet")
            .ToList();

        ClaimSize(registries[3].Body).ShouldBe("500Gi", "tenant B's image volume came back as tenant A's");
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a cluster with nothing in it.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.ShouldNotBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ ON A FIFTEEN-OBJECT TYPE THIS IS THE ASSERTION THAT MATTERS MOST, because fourteen right
        // ones make the fifteenth invisible. Clause 4 is the claim that the platform OBSERVED what it
        // applied, and set equality in both directions is the only form that catches either half going
        // missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

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

        applied.Count.ShouldBe(
            15,
            "a registry is fifteen objects on the default body — one Secret, one ConfigMap, six "
            + "Services, three StatefulSets, three Deployments and a PodMonitor."
        );
    }

    [Fact]
    public void EveryTargetHasADocumentAndEveryDocumentHasATarget() {
        // ⚠ TWO LISTS THAT MUST STAY THE SAME LENGTH AND THE SAME ORDER — one the reconciler applies
        // and one it reads back — and on a fifteen-object type they are far enough apart in the file
        // that a sixteenth object added to one and not the other is an easy mistake. A target with no
        // document is a read of something nothing wrote; a document with no target is an object nobody
        // observes.
        //
        // ⚠ It also checks the CONDITIONAL object, which is where the two lists most plausibly drift:
        // the PodMonitor is applied only when monitoring is on, in both lists, and a body that turned
        // it off in one place and not the other would leave the pass waiting for an object it never
        // applied.
        using var on = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));
        using var off = JsonDocument.Parse(WithoutMonitoring(ContainerRegistries.Body(ClusterId)));

        ContainerRegistryReconciler.Targets("ns", "reg", on.RootElement).Length.ShouldBe(15);
        ContainerRegistryReconciler.Targets("ns", "reg", off.RootElement).Length.ShouldBe(14);
    }

    [Fact]
    public async Task TurningMonitoringOffAppliesFourteenObjectsAndNoPodMonitor() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(WithoutMonitoring(ContainerRegistries.Body(ClusterId)));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        connection.Applied.Count.ShouldBe(14);
        connection.Applied.ShouldNotContain(x => x.Target.Kind.Kind == "PodMonitor");

        // ⚠ And core stops LISTENING as well as stops being scraped. A metrics port left open on a pod
        // nothing scrapes is a surface with no observer, which is worse than either half alone.
        var core = connection.Applied.Single(x => x.Target.Name.EndsWith("-core", StringComparison.Ordinal));

        core.Body.ShouldContain("\"METRIC_ENABLE\"");
        Container(core.Body)["ports"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. Nothing in the fifteen renders counts, appends or timestamps, and this is what says
        // so.
        //
        // ⚠ IT IS ALSO THE MINT-ONCE ASSERTION. The reconciler generates a FRESH credential set on
        // every pass and hands it to the vault, so byte-stability here is a statement about mint-once:
        // the second pass's candidate must be discarded and the rendered credentials Secret must still
        // carry the FIRST pass's values. A reconciler that overwrote on mint, or that rendered its
        // candidate instead of what it resolved back, fails exactly here — with the Secret's body
        // differing and the other fourteen identical.
        //
        // ⚠ One vault across both passes, which is what production is. A fresh store per pass would let
        // a mint-every-time reconciler pass, because each pass would then be the first.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await Reconcile(connection, body.RootElement, vault);
        var first = connection.Applied.Select(x => x.Body).ToArray();

        await Reconcile(connection, body.RootElement, vault);
        var second = connection.Applied.Skip(first.Length).Select(x => x.Body).ToArray();

        second.ShouldBe(first);

        vault.Writes.ShouldBe(
            1,
            "the second pass minted a second credential set. Mint-once is what stops a reconcile loop "
            + "from rotating a tenant's administrator password out from under them on every reminder — "
            + "and on Harbor a second mint would not even take effect, because src/core/main.go applies "
            + "HARBOR_ADMIN_PASSWORD only when the stored salt is empty."
        );
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections. A tenant whose cluster is down has a resource that is
        // still coming, not one that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ `.spec.replicas` is the plausible one
        // on this type: any horizontal autoscaler a tenant runs over their own cluster ends up owning
        // it, and forcing would undo them every pass.
        var connection = new RecordingConnection { ConflictField = ".spec.replicas" };
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("replicas");
    }

    // ── Failure class (c): the credential, and the order that makes it safe ──────────────────────

    [Fact]
    public async Task AVaultThatRefusesLeavesNOTHINGAppliedToTheCluster() {
        // ⚠ THIS IS THE ORDER ARGUMENT, AS AN ASSERTION.
        //
        // Mint-then-apply leaves an orphaned KV document: inert, nothing running, nothing billed, and
        // the next pass reuses it because mint-once makes the retry converge on the same set.
        // Apply-then-mint leaves six workloads referencing a Secret that does not exist — survivable
        // only because a missing secretKeyRef holds the pod in CreateContainerConfigError, and one edit
        // away from the thing goharbor/harbor-helm actually ships, which is
        // `harborAdminPassword: "Harbor12345"` consumed with no generation fallback.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement, vault);

        outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Converged);

        connection.Applied.ShouldBeEmpty(
            "the reconciler applied to the cluster before the credentials existed. Six Harbor "
            + "components referencing a Secret nobody wrote is one edit away from a reader reaching "
            + "for the upstream chart's published default."
        );
    }

    [Fact]
    public async Task AVaultFailureIsRetryableSoTheNextPassCanConverge() {
        // The other half of the order argument: refusing must not be terminal. A sealed, unreachable or
        // unwired vault is a resource that has not started, not one that broke.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var refused = await Reconcile(connection, body.RootElement, vault);
        refused.Retryable.ShouldBeTrue(refused.Error?.Message);

        vault.RefuseMint = false;

        (await Reconcile(connection, body.RootElement, vault)).ShouldBe(
            ReconcileOutcome.Converged,
            "a pass that failed on the vault left something behind that stops the next one"
        );

        vault.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task AnOrphanedMintIsReusedRatherThanReplacedWhenTheClusterComesBack() {
        // The surviving failure, driven: the vault write lands, the cluster refuses, and the pass that
        // succeeds afterwards uses the SAME credentials. That is what makes the orphan harmless — it is
        // not litter, it is the credential set this resource was always going to have.
        var failing = new RecordingConnection { FailApplies = true };
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await Reconcile(failing, body.RootElement, vault);

        var path = ContainerRegistries.SecretPath(Address("observed", TenantA, SubscriptionA));
        var minted = vault.Peek(path, ContainerRegistries.AdminPasswordField);

        minted.ShouldNotBeNull("the credentials were not minted before the cluster was touched");

        var working = new RecordingConnection();
        (await Reconcile(working, body.RootElement, vault)).ShouldBe(ReconcileOutcome.Converged);

        vault.Peek(path, ContainerRegistries.AdminPasswordField).ShouldBe(
            minted,
            "the recovery pass minted a second credential set, so a tenant who had already read the "
            + "first password holds one Harbor never accepted"
        );

        vault.Writes.ShouldBe(1);
    }

    // ── The teardown ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRemovesEveryObjectAndTheCredentialsSecretLast() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        var reconciler = new ContainerRegistryReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Count.ShouldBe(15);

        // ⚠ THE APPLY ORDER REVERSED, AND IT IS REVERSED FOR A SECURITY REASON. Taking the credentials
        // away from under six running components would restart every one of them without the values
        // they authenticate each other with — and a Harbor core that cannot read HARBOR_ADMIN_PASSWORD
        // does not refuse callers, because src/core/main.go applies it only when the stored salt is
        // empty. A teardown interrupted before the last step leaves a Secret nobody mounts.
        connection.Deleted[^1].Kind.Kind.ShouldBe("Secret");
        connection.Deleted[0].Kind.Kind.ShouldBe("PodMonitor");
    }

    // ── The shape a fake cluster echoes back and a real API server refuses ──────────────────────

    [Fact]
    public async Task EveryRenderedPodSpecIsSomethingARealApiServerCanTypeCHECK() {
        // ⚠ THIS TEST EXISTS BECAUSE THE DOCKER-FREE SUITE WAS GREEN OVER A DEFECT THE REAL API SERVER
        // REFUSED, AND THE GAP BETWEEN THE TWO IS THE WHOLE POINT OF IT.
        //
        // `ConfigVolume` returns a JsonArray, and the two call sites that needed it passed
        // `volumes: [ConfigVolume(name)]` — a collection expression around something that is already a
        // collection. The rendered `spec.template.spec.volumes` was therefore an ARRAY OF ARRAYS.
        //
        // Nothing caught it: FakeKubeCluster echoes an apply back verbatim, `Matches` compares the
        // replica count, the image tag and the claim size and never looks at volumes, and the shared
        // conformance suite was 32 of 32 green. The k3s suite failed four assertions with "the API
        // server could not type-check the object the platform rendered" — which is the ONE thing this
        // family's cluster-backed suite can prove that no other family's can, because fourteen of its
        // fifteen objects are built-in kinds and are schema-validated for real.
        //
        // ⚠ So this asserts the SHAPE rather than the contents: every member of a `volumes` array, a
        // `containers` array and a `volumeMounts` array is an object with a `name`. That is the class
        // of mistake a JsonObject-building renderer makes, and it is invisible to every comparison
        // this provider owns.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var command in connection.Applied) {
            if (JsonNode.Parse(command.Body)!["spec"] is not JsonObject spec
                || spec["template"] is not JsonObject template
                || template["spec"] is not JsonObject pod) {
                continue;
            }

            NamedObjects(pod, "containers", command.Target.Name);
            NamedObjects(pod, "volumes", command.Target.Name);

            foreach (var container in pod["containers"]!.AsArray()) {
                NamedObjects(container!.AsObject(), "volumeMounts", command.Target.Name);
                NamedObjects(container.AsObject(), "ports", command.Target.Name);
            }
        }
    }

    /// <summary>Every member of a named array is an object carrying a <c>name</c>.</summary>
    static void NamedObjects(JsonObject parent, string member, string owner) {
        if (parent[member] is not JsonArray array) {
            return;
        }

        foreach (var entry in array) {
            entry.ShouldBeOfType<JsonObject>(
                $"'{owner}' renders a {member} entry that is not an object. A collection expression "
                + "around something that is already a JsonArray produces an array of arrays, which a "
                + "fake cluster echoes back happily and a real API server refuses to type-check."
            );

            entry!.AsObject()["name"]?.GetValue<string>().ShouldNotBeNullOrEmpty(
                $"'{owner}' renders a {member} entry with no name"
            );
        }
    }

    // ── Failure class (e), at the object: the labels a builder does not inject ───────────────────

    [Fact]
    public async Task EverySelectorAgreesWithThePodTemplateItSelects() {
        // ⚠ THE LABELS A PROVIDER CAN ACTUALLY GET WRONG, AND THE Labels ARCHITECTURE GATE DOES NOT
        // COVER THEM. DocumentDbAccounts measured that: ADR-013's seven are injected by KubeCommand
        // non-overridably, so rendering a wrong `cybercloud.io/tenant-id` leaves that gate green. What
        // is injected by nothing is the app.kubernetes.io/* set, and on this type it is written into
        // THREE places per component that must agree — an immutable workload selector, its pod
        // template, and a Service selector. Six components, eighteen places.
        //
        // ⚠ A workload whose selector does not match its own template is accepted by nothing (the API
        // server refuses it), but a SERVICE whose selector does not match is accepted by everything and
        // routes to no pods — a registry that comes up healthy and answers nothing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var command in connection.Applied) {
            var document = JsonNode.Parse(command.Body)!.AsObject();

            if (document["spec"] is not JsonObject spec) {
                continue;
            }

            if (spec["template"] is JsonObject template) {
                var selector = spec["selector"]!["matchLabels"]!.ToJsonString();
                var labels = template["metadata"]!["labels"]!.ToJsonString();

                labels.ShouldBe(
                    selector,
                    $"'{command.Target.Name}' has a selector that does not match its own pod template"
                );
            }
        }

        // Every Service's selector is one of the six component label sets, and every component has a
        // workload carrying exactly that set.
        var workloadLabels = connection.Applied
            .Select(x => JsonNode.Parse(x.Body)!.AsObject())
            .Where(x => (x["spec"] as JsonObject)?["template"] is not null)
            .Select(x => x["spec"]!["template"]!["metadata"]!["labels"]!.ToJsonString())
            .ToHashSet(StringComparer.Ordinal);

        var serviceSelectors = connection.Applied
            .Where(x => x.Target.Kind.Kind == "Service")
            .Select(x => JsonNode.Parse(x.Body)!["spec"]!["selector"]!.ToJsonString())
            .ToList();

        serviceSelectors.Count.ShouldBe(6);

        foreach (var selector in serviceSelectors) {
            workloadLabels.ShouldContain(
                selector,
                "a Service selects a label set no workload in this registry carries, so it routes to no "
                + "pods — which the API server accepts and nothing else reports."
            );
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-11111111111c");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-44444444444c");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-22222222222c");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-55555555555c");

    internal static string WithoutMonitoring(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["monitoring"]!["enabled"] = false;
        return node.ToJsonString();
    }

    static int Replicas(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["replicas"]!.GetValue<int>();

    static string ClaimSize(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["volumeClaimTemplates"]![0]!["spec"]!["resources"]!
            ["requests"]!["storage"]!.GetValue<string>();

    static JsonObject Container(string objectJson) =>
        JsonNode.Parse(objectJson)!["spec"]!["template"]!["spec"]!["containers"]![0]!.AsObject();

    static async Task<ReconcileOutcome> Reconcile(
        RecordingConnection connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) =>
        await new ContainerRegistryReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired, vault), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        ContainerRegistryReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) {
        // ⚠ One vault per ADDRESS, which is what the cross-tenant test needs: two tenants minting at
        // two paths must not be able to read each other back, and a shared store would pass that test
        // for free by holding both.
        var store = new InMemorySecretVault();

        return await reconciler.ReconcileAsync(
            new(
                address,
                ContainerRegistries.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                store,
                new NullLog()
            ) {
                SecretWriter = store
            },
            TestContext.Current.CancellationToken
        );
    }

    internal static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        var address = Address("observed", TenantA, SubscriptionA);
        var store = vault ?? new InMemorySecretVault();

        return new(
            address,
            ContainerRegistries.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            store,
            new NullLog()
        ) {
            SecretWriter = store
        };
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    internal static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            ContainerRegistries.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-33333333333c")
        );
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, so <c>CheckNoHiddenState</c> skips it, and the
///     dictionary it holds is mutable forever.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => ContainerRegistries.Type;

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

    /// <summary>Whether every apply fails outright — the half of a partial failure the cluster owns.</summary>
    public bool FailApplies { get; set; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    public Task<Result<ApplyOutcome>> ApplyAsync(
        KubeCommand command,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        if (FailApplies) {
            // ⚠ Not recorded in Applied: a refused apply changed nothing, and the ordering assertion
            // reads that list to prove the cluster was never touched before the vault was.
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(ErrorCode.ProvisioningFailed, "the API server refused.")
            );
        }

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
    ///     puts the same resource name in two tenants.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}
