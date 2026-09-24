using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>
///     A scale set's reconciler and its two actions: the pool it renders, the replica count that lives
///     on the pool and survives a reconcile, the ceiling the capacity sets, and the race the
///     precondition closes.
/// </summary>
public sealed class VirtualMachineScaleSetTests {
    [Fact]
    public async Task ANewSetRendersAPoolAtItsCapacityWithTheMachinesSpecAsItsTemplate() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 3, size: "s1.large"));

        var outcome = await Reconcile(connection, address, body);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);

        var pool = JsonNode.Parse(connection.Objects[RecordingConnection.Key(VirtualMachineScaleSets.PoolRef(ns, "web"))])!;
        var spec = pool["spec"]!;

        spec["replicas"]!.GetValue<int>().ShouldBe(3, "a new set starts at its capacity");
        spec["selector"]!["matchLabels"]![VirtualMachineScaleSets.InstanceLabel]!.GetValue<string>().ShouldBe("web");
        spec["updateStrategy"]!["proactive"].ShouldNotBeNull("the default mode is Rolling, which is the pool's proactive");
        spec["maxUnavailable"]!.GetValue<int>().ShouldBe(1);

        // The machine every instance is: VirtualMachines' own render, whole.
        var machine = spec["virtualMachineTemplate"]!["spec"]!;
        machine["runStrategy"]!.GetValue<string>().ShouldBe(VirtualMachines.RunAlways);
        machine["template"]!["spec"]!["domain"]!["cpu"]!["cores"]!.GetValue<int>().ShouldBe(4);
        machine["dataVolumeTemplates"]![0]!["metadata"]!["name"]!.GetValue<string>()
            .ShouldBe("web-root", "the pool controller suffixes it per machine: web-root-0, web-root-1");
        spec["virtualMachineTemplate"]!["metadata"]!["labels"]![VirtualMachineScaleSets.NameLabel]!.GetValue<string>()
            .ShouldBe(VirtualMachineScaleSets.NameLabelValue);

        // ⚠ The platform's labels reach every level a machine or its disk is created from.
        var apply = connection.Applied.Single(static x => x.Target.Kind.Kind == "VirtualMachinePool");
        var applied = JsonNode.Parse(apply.Body)!["spec"]!["virtualMachineTemplate"]!;

        foreach (var labels in new[] {
                     applied["metadata"]!["labels"]!, applied["spec"]!["template"]!["metadata"]!["labels"]!,
                     applied["spec"]!["dataVolumeTemplates"]![0]!["metadata"]!["labels"]!
                 }) {
            labels[KubeLabels.ResourceId]!.GetValue<string>().ShouldBe(KubeLabels.GuidValue(address.Id));
        }
    }

    [Theory]
    [InlineData(VirtualMachineScaleSets.ManualUpgrade, "unmanaged")]
    [InlineData(VirtualMachineScaleSets.OnRestartUpgrade, "opportunistic")]
    [InlineData(VirtualMachineScaleSets.RollingUpgrade, "proactive")]
    public void TheUpgradeModeIsThePoolsOwnUpdateStrategy(string mode, string strategy) {
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, upgradeMode: mode, maxUnavailable: 2));
        var spec = JsonNode.Parse(VirtualMachineScaleSets.PoolJson("ns", "web", body.RootElement, 2))!["spec"]!;

        spec["updateStrategy"]!.AsObject().Select(static x => x.Key).ShouldBe([strategy]);

        if (mode == VirtualMachineScaleSets.RollingUpgrade) {
            spec["maxUnavailable"]!.GetValue<int>().ShouldBe(2);
        } else {
            spec["maxUnavailable"].ShouldBeNull("maxUnavailable bounds an automated update, and only Rolling has one");
        }
    }

    [Fact]
    public async Task AScaleIsKeptByTheNextReconcileAndAPutThatLowersTheCapacityClampsIt() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        var key = RecordingConnection.Key(VirtualMachineScaleSets.PoolRef(ns, "web"));
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 4));

        await Reconcile(connection, address, body);

        var scaled = await new VirtualMachineScaleSetActionHandler().InvokeAsync(
            Scale(connection, address, body, 1),
            TestContext.Current.CancellationToken
        );

        scaled.IsSuccess.ShouldBeTrue(scaled.Error?.Message);
        using (var answer = JsonDocument.Parse(scaled.GetValueOrThrow())) {
            VirtualMachineScaleSets.ScaleResponse.Validate(answer.RootElement).IsSuccess.ShouldBeTrue();
            answer.RootElement.GetProperty("replicasBefore").GetInt32().ShouldBe(4);
            answer.RootElement.GetProperty("replicas").GetInt32().ShouldBe(1);
            answer.RootElement.GetProperty("capacity").GetInt32().ShouldBe(4);
        }

        VirtualMachineScaleSets.ReplicasOf(connection.Objects[key]).ShouldBe(1);

        // ⚠ THE SEQUENCE THE DESIGN EXISTS FOR: a PUT with an unrelated change after a scale.
        using var resized = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 4, size: "s1.medium"));
        await Reconcile(connection, address, resized);

        VirtualMachineScaleSets.ReplicasOf(connection.Objects[key])
            .ShouldBe(1, "a reconcile pass scaled a set back out that its tenant had scaled in");

        // And a PUT that lowers the capacity below the live count brings the pool down to it.
        await new VirtualMachineScaleSetActionHandler().InvokeAsync(Scale(connection, address, resized, 4), TestContext.Current.CancellationToken);
        using var lowered = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 2));
        await Reconcile(connection, address, lowered);

        VirtualMachineScaleSets.ReplicasOf(connection.Objects[key]).ShouldBe(2, "the capacity is a ceiling on the live count");
    }

    [Fact]
    public async Task AScaleAboveTheCapacityIsRefusedAndNamesIt() {
        var connection = new RecordingConnection();
        var address = Set("web");
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 2));

        await Reconcile(connection, address, body);
        var applies = connection.Applied.Count;

        var refused = await new VirtualMachineScaleSetActionHandler().InvokeAsync(
            Scale(connection, address, body, 3),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("capacity of 2");
        connection.Applied.Count.ShouldBe(applies, "a refused scale applied something");
    }

    [Fact]
    public async Task AScaleThatLandsBetweenAPassReadAndItsApplyIsNotOverwritten() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var key = RecordingConnection.Key(VirtualMachineScaleSets.PoolRef(ReconcileDriver.NamespaceFor(address), "web"));
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 3));

        await Reconcile(connection, address, body);

        connection.BeforeNextApply = command => {
            if (command.Target.Kind.Kind != VirtualMachineScaleSets.PoolKind.Kind) {
                return;
            }

            var scaled = JsonNode.Parse(connection.Objects[key])!.AsObject();
            scaled["spec"]!["replicas"] = 0;
            connection.Store(key, scaled.ToJsonString());
        };

        var raced = await Reconcile(connection, address, body);

        raced.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Stale.ShouldHaveSingleItem();
        VirtualMachineScaleSets.ReplicasOf(connection.Objects[key]).ShouldBe(0, "the pass wrote back a count a scale had replaced");
    }

    [Fact]
    public async Task ListInstancesReadsTheMachinesThePoolMadeInIndexOrder() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 12));

        await Reconcile(connection, address, body);

        // What the pool controller would have made — labelled by the template, named {set}-{index}.
        foreach (var (index, printable) in new[] { (10, "Starting"), (2, "Running"), (0, "Running") }) {
            Machine(connection, ns, VirtualMachineScaleSets.InstanceName("web", index), "web", printable);
        }

        // A machine of another set with a prefix-sharing name is not this set's.
        Machine(connection, ns, "web-api-0", "web-api", "Running");

        var listed = await new VirtualMachineScaleSetActionHandler().InvokeAsync(
            Action(connection, address, body, VirtualMachineScaleSets.ListInstancesAction, "{}"),
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);
        using var answer = JsonDocument.Parse(listed.GetValueOrThrow());
        VirtualMachineScaleSets.InstancesResponse.Validate(answer.RootElement).IsSuccess.ShouldBeTrue();

        answer.RootElement.GetProperty("instances").EnumerateArray().Select(static x => x.GetString())
            .ShouldBe(["web-0", "web-2", "web-10"], "index order, not string order");
        answer.RootElement.GetProperty("states").EnumerateArray().Select(static x => x.GetString())
            .ShouldBe(["Running", "Running", "Starting"]);
        answer.RootElement.GetProperty("capacity").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task ConvergedFollowsThePoolsReadyAndCurrentCounts() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var pool = VirtualMachineScaleSets.PoolRef(ReconcileDriver.NamespaceFor(address), "web");
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, capacity: 2));

        await Reconcile(connection, address, body);

        Compute.Report(connection, pool, new() { ["replicas"] = 2, ["readyReplicas"] = 1 });
        var waiting = await Reconcile(connection, address, body);
        waiting.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        waiting.Reason.ShouldContain("1 of 2 machines are ready");

        // ⚠ Measured on the pinned pool: mid scale-in it reads ready == asked while the extra machine is
        // still shutting down, so `replicas` must equal the ask as well.
        Compute.Report(connection, pool, new() { ["replicas"] = 3, ["readyReplicas"] = 2 });
        (await Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);

        Compute.Report(connection, pool, new() { ["replicas"] = 2, ["readyReplicas"] = 2 });
        (await Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
    }

    [Fact]
    public async Task ANameNoInstanceCouldCarryIsRefusedBeforeAnythingIsRead() {
        var connection = new RecordingConnection();
        var address = Set(new string('w', VirtualMachineScaleSets.MaxNameLength + 1));
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId));

        var outcome = await Reconcile(connection, address, body);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        connection.Read.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACloudInitHandleOutsideTheTenantsPrefixIsRefusedAsAMachinesIs() {
        var connection = new RecordingConnection();
        var address = Set("web");
        var vault = new SeededSecrets((Compute.VaultPath("x", Compute.TenantB), "userdata", "#cloud-config"));
        using var body = JsonDocument.Parse(
            VirtualMachineScaleSets.Body(Compute.ClusterId, cloudInit: Compute.VaultPath("x", Compute.TenantB) + "#userdata")
        );

        var outcome = await new VirtualMachineScaleSetReconciler(new FixedClock()).ReconcileAsync(
            Compute.Context(connection, address, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        vault.Resolves.ShouldBe(0, "the foreign path was resolved before it was refused");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMachineAndASetOfOneNameKeepTheirOwnCloudInitAndTheSetsDeleteLeavesTheMachines() {
        // ⚠ #28's review: both types wrote `{name}-cloud-init` under the one Compute field manager, so the
        // two applies overwrote each other without a conflict, and the set's delete — which removes its
        // Secret whether or not its body names cloud-init — removed the machine's user data.
        var connection = new RecordingConnection();
        var machine = Compute.Machine("web");
        var set = Set("web");
        var ns = ReconcileDriver.NamespaceFor(set);
        ReconcileDriver.NamespaceFor(machine).ShouldBe(ns, "the two are in one resource group");
        var vault = new SeededSecrets(
            (Compute.VaultPath("vm"), "userdata", "#cloud-config machine"),
            (Compute.VaultPath("set"), "userdata", "#cloud-config set")
        );
        using var machineBody = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, cloudInit: Compute.VaultPath("vm") + "#userdata"));
        using var setBody = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId, cloudInit: Compute.VaultPath("set") + "#userdata"));
        using var bareSet = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId));

        await new VirtualMachineReconciler(new FixedClock()).ReconcileAsync(
            Compute.Context(connection, machine, machineBody.RootElement, vault),
            TestContext.Current.CancellationToken
        );
        var outcome = await new VirtualMachineScaleSetReconciler(new FixedClock()).ReconcileAsync(
            Compute.Context(connection, set, setBody.RootElement, vault),
            TestContext.Current.CancellationToken
        );
        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);

        UserData(connection, VirtualMachines.CloudInitSecretRef(ns, "web")).ShouldBe("#cloud-config machine");
        UserData(connection, VirtualMachineScaleSets.CloudInitSecretRef(ns, "web")).ShouldBe("#cloud-config set");
        VirtualMachineScaleSets.CloudInitSecretName("web").ShouldNotBe(VirtualMachines.CloudInitSecretName("web"));

        var pool = JsonNode.Parse(connection.Objects[RecordingConnection.Key(VirtualMachineScaleSets.PoolRef(ns, "web"))])!;
        pool["spec"]!["virtualMachineTemplate"]!["spec"]!["template"]!["spec"]!["volumes"]!.AsArray()
            .Select(static x => x!["cloudInitNoCloud"]?["secretRef"]?["name"]?.GetValue<string>())
            .OfType<string>()
            .ShouldBe(["web-cloud-init-set"], "every machine of the set mounts the set's Secret");

        // A set whose body names no cloud-init still deletes its own Secret name — and not the machine's.
        var deleted = await new VirtualMachineScaleSetReconciler(new FixedClock()).DeleteAsync(
            Compute.Context(connection, set, bareSet.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged, deleted.Error?.Message);
        connection.Deleted.ShouldNotContain(VirtualMachines.CloudInitSecretRef(ns, "web"));
        UserData(connection, VirtualMachines.CloudInitSecretRef(ns, "web")).ShouldBe("#cloud-config machine");
    }

    static string UserData(RecordingConnection connection, ObjectRef secret) {
        var read = connection.GetAsync(secret, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        read.IsSuccess.ShouldBeTrue($"'{secret}' is not there");
        return KubeSecret.Value(read.GetValueOrThrow(), VirtualMachines.CloudInitKey).GetValueOrThrow();
    }

    [Fact]
    public async Task TheDeleteIsForegroundSoTheMachinesGoBeforeThePoolDoes() {
        var connection = new RecordingConnection();
        var address = Set("web");
        using var body = JsonDocument.Parse(VirtualMachineScaleSets.Body(Compute.ClusterId));

        await Reconcile(connection, address, body);

        var deleted = await new VirtualMachineScaleSetReconciler(new FixedClock()).DeleteAsync(
            Compute.Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        connection.Deleted.ShouldContain(VirtualMachineScaleSets.PoolRef(ReconcileDriver.NamespaceFor(address), "web"));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static ResourceId Set(string name) =>
        new(Compute.TenantA, Compute.SubscriptionA, "prod", VirtualMachineScaleSets.Type, name, Guid.NewGuid());

    static Task<ReconcileOutcome> Reconcile(RecordingConnection connection, ResourceId address, JsonDocument body) =>
        new VirtualMachineScaleSetReconciler(new FixedClock()).ReconcileAsync(
            Compute.Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

    static ActionContext Scale(RecordingConnection connection, ResourceId address, JsonDocument body, int replicas) =>
        Action(connection, address, body, VirtualMachineScaleSets.ScaleAction, $$"""{"replicas":{{replicas}}}""");

    static ActionContext Action(
        RecordingConnection connection,
        ResourceId address,
        JsonDocument body,
        string action,
        string request
    ) =>
        Compute.Action(connection, address, body.RootElement, action) with {
            Body = JsonDocument.Parse(request).RootElement.Clone()
        };

    static void Machine(RecordingConnection connection, string ns, string name, string set, string printable) =>
        connection.Store(
            RecordingConnection.Key(VirtualMachines.VirtualMachineRef(ns, name)),
            new JsonObject {
                ["kind"] = "VirtualMachine",
                ["metadata"] = new JsonObject {
                    ["name"] = name,
                    ["labels"] = new JsonObject {
                        [VirtualMachineScaleSets.NameLabel] = VirtualMachineScaleSets.NameLabelValue,
                        [VirtualMachineScaleSets.InstanceLabel] = set
                    }
                },
                ["status"] = new JsonObject { ["printableStatus"] = printable }
            }.ToJsonString()
        );
}
