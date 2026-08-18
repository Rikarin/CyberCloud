using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     The node-pool reconciler, and the assertions the shared conformance harness cannot make about a
///     child type.
/// </summary>
public sealed class AgentPoolReconcilerTests {
    // ── Failure class (a), on the child too ─────────────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new AgentPoolReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckCatchesAReadonlyMutableCacheOnAChildToo() {
        // ⚠ CALIBRATION, AND IT NOW POINTS THE OTHER WAY. This test used to assert that
        // CheckNoHiddenState MISSED the counter-example below, because it skipped every
        // `field.IsInitOnly` — and `readonly` stops the FIELD being reassigned while stopping
        // nothing about the dictionary, so a per-tenant cache passed clause 2 while accumulating
        // state on a singleton every tenant shares. Seven families each pinned that blind spot
        // and it is now closed; this is what holds it closed.
        //
        // ⚠ Pinned on the child as well, because a cache added to a CHILD's reconciler is a
        // different file from one added to its parent's.
        //
        // ⚠ THE CROSS-TENANT TEST BELOW STAYS, AND IS NOT MADE REDUNDANT BY THIS. This one reads
        // a field's declared TYPE. That one drives ONE reconciler instance through TWO tenants and
        // compares what each got, which is the only way to catch mixing no field type could show.
        var findings = ReconcilerConformance.CheckNoHiddenState(new PoolReconcilerWithAReadonlyCache());

        findings.ShouldContain(
            x => x.Clause == ReconcilerClause.NoHiddenState,
            "a readonly field holding a mutable Dictionary is state on a shared singleton, and the "
            + "structural check is what catches it before the behavioural test has to"
        );

        findings.ShouldContain(x => x.Detail.Contains("lastRendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        var reconciler = new AgentPoolReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Address("workers", "prod-cluster", TenantA, SubscriptionA);
        var bob = Address("workers", "prod-cluster", TenantB, SubscriptionB);

        using var aliceBody = JsonDocument.Parse(AgentPools.Body(ClusterId, count: 2, size: "s1.nano"));
        using var bobBody = JsonDocument.Parse(AgentPools.Body(ClusterId, count: 7, size: "s1.large"));

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var deployments = connection.Applied
            .Where(x => x.Target.Kind.Kind == "MachineDeployment")
            .ToList();

        deployments.Count.ShouldBe(4);

        Spec(deployments[0].Body)["replicas"]!.GetValue<int>().ShouldBe(2);
        Spec(deployments[1].Body)["replicas"]!.GetValue<int>().ShouldBe(7);
        Spec(deployments[2].Body)["replicas"]!.GetValue<int>()
            .ShouldBe(2, "tenant A's machine count came back as tenant B's");

        deployments[0].Target.Namespace.ShouldNotBe(deployments[1].Target.Namespace);
    }

    // ── What only a child can be wrong about ────────────────────────────────────────────────────

    [Fact]
    public async Task TwoClustersInOneResourceGroupHoldTwoDifferentPoolsOfTheSameName() {
        // ⚠ THE ASSERTION THE SHARED HARNESS CAN NEVER MAKE, AND THE ONE A CHILD TYPE EXISTS TO GET
        // RIGHT. ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}`, so a parent
        // RESOURCE lives inside a namespace rather than being one. A renderer that ignored
        // ResourceId.ParentNames would have the two pools fighting over one MachineDeployment — and on
        // this type that means every worker VM in the resource group moving from one cluster to the
        // other and back on each pass, with neither reporting an error anywhere.
        var reconciler = new AgentPoolReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var first = Address("workers", "alpha", TenantA, SubscriptionA);
        var second = Address("workers", "beta", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(reconciler, connection, first, body.RootElement);
        await Pass(reconciler, connection, second, body.RootElement);

        var deployments = connection.Applied
            .Where(x => x.Target.Kind.Kind == "MachineDeployment")
            .ToList();

        deployments[0].Target.Namespace.ShouldBe(deployments[1].Target.Namespace);

        deployments[0].Target.Name.ShouldBe("alpha-workers");
        deployments[1].Target.Name.ShouldBe("beta-workers");

        // ⚠ AND THE clusterName IS THE FIELD THAT DECIDES WHOSE MACHINES THEY ARE.
        Spec(deployments[0].Body)["clusterName"]!.GetValue<string>().ShouldBe("alpha");
        Spec(deployments[1].Body)["clusterName"]!.GetValue<string>().ShouldBe("beta");

        connection.Objects.Count.ShouldBe(6, "the two pools overwrote each other's objects");
    }

    [Fact]
    public async Task TheClusterNameComesFromTheAddressAndNotFromTheBody() {
        // Nothing in a pool's body names its cluster, and adding a property that did would be a second
        // spelling of a fact ResourceId.Parent already answers.
        var connection = new RecordingConnection();

        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        body.RootElement.GetProperty("properties").TryGetProperty("clusterName", out _)
            .ShouldBeFalse("the body now names the cluster, which is the fact the address carries");

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        foreach (var applied in connection.Applied.Where(x => x.Target.Kind.Kind == "MachineDeployment")) {
            Spec(applied.Body)["clusterName"]!.GetValue<string>().ShouldBe("prod-cluster");

            // ⚠ TWICE, and Cluster API requires both. A pool whose two disagreed would be adopted by
            // one cluster and counted by another.
            Spec(applied.Body)["template"]!["spec"]!["clusterName"]!.GetValue<string>()
                .ShouldBe("prod-cluster");
        }
    }

    [Fact]
    public async Task TheSelectorAndTheTemplateLabelsAreTheSameMap() {
        // ⚠ ADR-013's SEVEN LABELS COVER NEITHER OF THESE. KubeCommandBuilder injects the seven into
        // the OBJECT's metadata.labels non-overridably, which is why
        // EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations stays green for a provider
        // that gets this wrong. Cluster API's own validating webhook refuses a MachineDeployment whose
        // selector and template labels disagree — so upstream catches it, at admission, per object.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        var deployment = connection.Applied.Single(x => x.Target.Kind.Kind == "MachineDeployment");
        var spec = Spec(deployment.Body);

        var selector = spec["selector"]!["matchLabels"]!.ToJsonString();
        var template = spec["template"]!["metadata"]!["labels"]!.ToJsonString();

        selector.ShouldBe(template);
        spec["selector"]!["matchLabels"]![AgentPools.PoolLabel]!.GetValue<string>()
            .ShouldBe("prod-cluster-workers");
    }

    [Fact]
    public async Task NothingInAPassEverReadsTheClustersOwnObjects() {
        // ⚠ docs/plan/08 § Deleting a parent resource that has children: the platform "must not
        // re-check the parent on every write to a child". ⚠ And on this type there is a second reason
        // that runs the OPPOSITE way from StorageBucketReconciler's: a Cluster DOES report ready, so a
        // pool that waited for it could — and must not, because Cluster API adopts a MachineDeployment
        // the moment its control plane appears, and the first worker joining is a step in docs/plan/09's
        // own provisioning table rather than something that happens afterwards.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        connection.Read.ShouldAllBe(x => x.Kind.Kind != "Cluster" && x.Kind.Kind != "KamajiControlPlane");
        connection.Applied.ShouldAllBe(x => x.Target.Kind.Kind != "Cluster");
    }

    // ── The four clauses ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        var outcome = await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.Count.ShouldBe(3);
        read.ShouldBe(applied, ignoreOrder: true);
    }

    [Fact]
    public async Task TheTemplatesAreAppliedBeforeTheMachineDeploymentThatNamesThem() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        connection.Applied.Select(x => x.Target.Kind.Kind)
            .ShouldBe(["KubevirtMachineTemplate", "KubeadmConfigTemplate", "MachineDeployment"]);
    }

    [Fact]
    public async Task DeletingAPoolRemovesItsOwnObjectsAndNothingOfItsClusters() {
        // ⚠ The whole of what a pool owns is its three objects. A delete that also tidied up the
        // Cluster — or waited for it — would be this type reaching outside its own resource.
        var reconciler = new AgentPoolReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Address("workers", "prod-cluster", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        // A Cluster somebody else put there, which the delete must leave alone.
        var ns = ReconcileDriver.NamespaceFor(address);
        var foreign = ManagedClusters.ClusterRef(ns, "prod-cluster");
        connection.Objects[RecordingConnection.Key(foreign)] = "{\"kind\":\"Cluster\"}";

        var outcome = await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.ShouldBe(ReconcileOutcome.Converged);

        connection.Objects.Keys.ShouldBe([RecordingConnection.Key(foreign)]);

        // ⚠ The MachineDeployment first, which is the reverse of the apply order: Cluster API owns the
        // teardown of the machines it created from it.
        connection.Deleted.Select(x => x.Kind.Kind)
            .ShouldBe(["MachineDeployment", "KubeadmConfigTemplate", "KubevirtMachineTemplate"]);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        var reconciler = new AgentPoolReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Address("workers", "prod-cluster", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);
        var first = connection.Applied.Select(x => x.Body).ToList();

        await Pass(reconciler, connection, address, body.RootElement);

        connection.Applied.Skip(3).Select(x => x.Body).ShouldBe(first);
    }

    [Fact]
    public async Task AutoscaleBoundsAreRenderedAsAnnotationsAndOnlyWhenTheSwitchIsOn() {
        var reconciler = new AgentPoolReconciler(new FixedClock());
        var address = Address("workers", "prod-cluster", TenantA, SubscriptionA);

        var off = new RecordingConnection();
        using var withoutAutoscaler = JsonDocument.Parse(AgentPools.Body(ClusterId));
        await Pass(reconciler, off, address, withoutAutoscaler.RootElement);

        Metadata(off.Applied.Single(x => x.Target.Kind.Kind == "MachineDeployment").Body)
            .ShouldNotContainKey(AgentPools.AutoscaleMinAnnotation);

        var on = new RecordingConnection();
        using var withAutoscaler = JsonDocument.Parse(
            AgentPools.Body(ClusterId, autoscale: true, minCount: 2, maxCount: 9)
        );

        await Pass(reconciler, on, address, withAutoscaler.RootElement);

        var annotations = Metadata(
            on.Applied.Single(x => x.Target.Kind.Kind == "MachineDeployment").Body
        );

        annotations[AgentPools.AutoscaleMinAnnotation]!.GetValue<string>().ShouldBe("2");
        annotations[AgentPools.AutoscaleMaxAnnotation]!.GetValue<string>().ShouldBe("9");
    }

    [Fact]
    public async Task TheMachineTemplateRendersNoCloudInitVolumeAndNoSshKey() {
        // ⚠ REQUIRED RATHER THAN TIDY. The KubeVirt provider's machine controller APPENDS a
        // CloudInitConfigDrive volume and a matching disk to whatever this renders, writing the
        // bootstrap data and its own `capk` user into it. A template that supplied one would end up
        // with two, which is a VM that boots from the wrong config drive.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        await Pass(
            new AgentPoolReconciler(new FixedClock()),
            connection,
            Address("workers", "prod-cluster", TenantA, SubscriptionA),
            body.RootElement
        );

        var template = connection.Applied.Single(x => x.Target.Kind.Kind == "KubevirtMachineTemplate");

        foreach (var forbidden in new[] { "cloudInit", "cloudinit", "sshKeys", "userData" }) {
            template.Body.ShouldNotContain(forbidden, Case.Insensitive, forbidden);
        }
    }

    [Fact]
    public void RenderingAPoolWithNoParentThrowsRatherThanCollides() {
        // ⚠ Unreachable through the platform — ResourceId enforces `ParentNames.Count == Type.Depth - 1`
        // on construction — and the throw is what makes the impossible state loud if that ever slips.
        var flat = new ResourceId(
            TenantA,
            SubscriptionA,
            "prod",
            ManagedClusters.Type,
            "workers",
            Guid.NewGuid()
        );

        Should.Throw<ArgumentException>(() => AgentPools.ObjectNameOf(flat));
        Should.Throw<ArgumentException>(() => AgentPools.ClusterNameOf(flat));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = ManagedClusterReconcilerTests.ClusterId;
    static readonly Guid TenantA = ManagedClusterReconcilerTests.TenantA;
    static readonly Guid TenantB = ManagedClusterReconcilerTests.TenantB;
    static readonly Guid SubscriptionA = ManagedClusterReconcilerTests.SubscriptionA;
    static readonly Guid SubscriptionB = ManagedClusterReconcilerTests.SubscriptionB;

    static async Task<ReconcileOutcome> Pass(
        AgentPoolReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            Context(connection, address, desired),
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        new(
            address,
            AgentPools.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );

    static ResourceId Address(string name, string cluster, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            AgentPools.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            cluster
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    static JsonObject Metadata(string objectJson) =>
        JsonNode.Parse(objectJson)!["metadata"]!["annotations"]?.AsObject() ?? [];
}

/// <summary>A child reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.</summary>
sealed class PoolReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => AgentPools.Type;

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
