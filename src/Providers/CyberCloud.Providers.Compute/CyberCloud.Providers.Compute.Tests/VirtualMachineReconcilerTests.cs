using CyberCloud.ResourceManager.Conformance;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>
///     The virtual-machine reconciler, and the assertions the shared conformance harness cannot make
///     about a type with a power state.
/// </summary>
public sealed class VirtualMachineReconcilerTests {
    // ── The two clauses a shared suite driving one tenant cannot see ────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        ReconcilerConformance.CheckNoHiddenState(new VirtualMachineReconciler(new FixedClock())).ShouldBeEmpty();
        ReconcilerConformance.CheckNoHiddenState(new DiskReconciler(new FixedClock())).ShouldBeEmpty();
        ReconcilerConformance.CheckNoHiddenState(new ImageReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var alice = Compute.Machine("web", Compute.TenantA, Compute.SubscriptionA);
        var bob = Compute.Machine("web", Compute.TenantB, Compute.SubscriptionB);

        using var aliceBody = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, size: "s1.small"));
        using var bobBody = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, size: "s1.large"));

        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);

        var machines = connection.Applied.Where(static x => x.Target.Kind.Kind == "VirtualMachine").ToList();

        machines.Count.ShouldBe(3);
        Cores(machines[0].Body).ShouldBe(1);
        Cores(machines[1].Body).ShouldBe(4);
        Cores(machines[2].Body).ShouldBe(1, "tenant A's size came back as tenant B's");
        machines[0].Target.Namespace.ShouldNotBe(machines[1].Target.Namespace);
    }

    // ── The power state, which is the whole design ──────────────────────────────────────────────

    [Fact]
    public async Task AFreshMachineIsRenderedAlwaysAndAHaltedOneStaysHalted() {
        // ⚠ THE ASSERTION THE DESIGN EXISTS FOR. The reconciler renders `runStrategy` from the object
        // it reads, not from the body — so a machine somebody stopped is not booted by a pass that
        // changed nothing about power. VirtualMachines' class remarks carry the argument.
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var first = connection.Applied.Single(static x => x.Target.Kind.Kind == "VirtualMachine");
        VirtualMachines.RunStrategyOf(first.Body).ShouldBe(VirtualMachines.RunAlways);

        // An operator — or the power handler — halts the machine on the object.
        var target = VirtualMachines.VirtualMachineRef(ReconcileDriver(address), "web");
        var stored = JsonNode.Parse(connection.Objects[RecordingConnection.Key(target)])!.AsObject();
        stored["spec"]!["runStrategy"] = VirtualMachines.RunHalted;
        connection.Objects[RecordingConnection.Key(target)] = stored.ToJsonString();

        await Pass(reconciler, connection, address, body.RootElement);

        var second = connection.Applied.Last(static x => x.Target.Kind.Kind == "VirtualMachine");
        VirtualMachines.RunStrategyOf(second.Body)
            .ShouldBe(
                VirtualMachines.RunHalted,
                "a reconcile pass turned a stopped machine back on, which is the drift correction tenants complain about"
            );
    }

    [Fact]
    public void TheSabotagedRenderWouldFailThePreviousTest() {
        // ⚠ CALIBRATION: the obvious implementation renders Always unconditionally, and Matches
        // ignores the run strategy on purpose — so nothing but the assertion above would catch it.
        // This pins that Matches is blind to the field, which is what makes that assertion load-bearing.
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        var halted = VirtualMachines.VirtualMachineJson("ns", "web", body.RootElement, VirtualMachines.RunHalted);
        var always = VirtualMachines.VirtualMachineJson("ns", "web", body.RootElement, VirtualMachines.RunAlways);

        VirtualMachines.Matches(halted, "ns", body.RootElement).ShouldBeTrue();
        VirtualMachines.Matches(always, "ns", body.RootElement).ShouldBeTrue();
        halted.ShouldNotBe(always);
    }

    // ── Readiness, over KubeVirt's own words ────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsThePrintableStatus() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));
        var target = VirtualMachines.VirtualMachineRef(ReconcileDriver(address), "web");

        // No status at all: the derived-stub branch, converged and said out loud.
        (await Pass(reconciler, connection, address, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        // The scheduler could not place the instance — a node with no KVM device, which the
        // k3s-in-Docker lane was believed to be until KubeVirtOnAnEmptyCluster measured it.
        Compute.Report(
            connection,
            target,
            new JsonObject {
                ["printableStatus"] = "ErrorUnschedulable",
                ["conditions"] = new JsonArray(
                    new JsonObject {
                        ["type"] = "PodScheduled",
                        ["status"] = "False",
                        ["reason"] = "Unschedulable",
                        ["message"] = "0/1 nodes are available: 1 Insufficient devices.kubevirt.io/kvm."
                    }
                )
            }
        );

        var stuck = await Pass(reconciler, connection, address, body.RootElement);
        stuck.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        stuck.Reason.ShouldContain("ErrorUnschedulable");
        stuck.Reason.ShouldContain(
            "devices.kubevirt.io/kvm",
            Case.Sensitive,
            "the scheduler's own sentence is the diagnosis a tenant needs"
        );

        // A clone CDI refused: KubeVirt's Failure condition is the reason when nothing is scheduled.
        Compute.Report(
            connection,
            target,
            new JsonObject {
                ["printableStatus"] = "Starting",
                ["conditions"] = new JsonArray(
                    new JsonObject {
                        ["type"] = "Failure", ["status"] = "True", ["message"] = "Source PVC prod/ubuntu not found"
                    }
                )
            }
        );

        (await Pass(reconciler, connection, address, body.RootElement)).Reason.ShouldContain("Source PVC");

        // Running: the verdict.
        Compute.Report(connection, target, new JsonObject { ["printableStatus"] = "Running" });
        (await Pass(reconciler, connection, address, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        // Stopped is converged only on a machine that was asked to stop.
        Compute.Report(connection, target, new JsonObject { ["printableStatus"] = "Stopped" });
        (await Pass(reconciler, connection, address, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);

        var stored = JsonNode.Parse(connection.Objects[RecordingConnection.Key(target)])!.AsObject();
        stored["spec"]!["runStrategy"] = VirtualMachines.RunHalted;
        connection.Objects[RecordingConnection.Key(target)] = stored.ToJsonString();

        (await Pass(reconciler, connection, address, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);
    }

    // ── The image ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnImportingImageIsWaitedForAndAnAbsentOneIsLeftToCdi() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var ns = ReconcileDriver(address);
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, "ubuntu"));

        var image = Images.DataVolumeRef(ns, "ubuntu");
        connection.Objects[RecordingConnection.Key(image)] = Compute.ImportedImage("ubuntu", "ImportInProgress");

        var waiting = await Pass(reconciler, connection, address, body.RootElement);
        waiting.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        waiting.Reason.ShouldContain("ubuntu");
        waiting.Reason.ShouldContain("ImportInProgress");
        connection.Applied.ShouldBeEmpty("a machine was applied before its image had imported");

        connection.Objects[RecordingConnection.Key(image)] = Compute.ImportedImage("ubuntu", Cdi.Failed);
        var failed = await Pass(reconciler, connection, address, body.RootElement);
        failed.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        failed.Retryable.ShouldBeFalse(
            "the image is immutable on the machine, so a retry would spin on a body the tenant cannot mend"
        );

        connection.Objects.TryRemove(RecordingConnection.Key(image), out _);
        (await Pass(reconciler, connection, address, body.RootElement)).ShouldBe(
            ReconcileOutcome.Converged,
            "an absent image is CDI's to refuse — the machine is applied and recovers when the image lands"
        );

        var rendered = connection.Applied.Single(static x => x.Target.Kind.Kind == "VirtualMachine").Body;
        var root = Compute.Spec(rendered)["dataVolumeTemplates"]![0]!["spec"]!["source"]!["pvc"]!;
        root["name"]!.GetValue<string>().ShouldBe("ubuntu");
        root["namespace"]!.GetValue<string>().ShouldBe(ns, "a clone across namespaces is not offered");
    }

    // ── Cloud-init ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CloudInitIsResolvedOncePerPassAndReachesASecretAndNothingElse() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var secrets = new SeededSecrets(
            (Compute.VaultPath("web"), "userdata", "#cloud-config\nusers:\n  - name: ops\n")
        );
        var address = Compute.Machine("web");
        _ = ReconcileDriver(address);
        using var body = JsonDocument.Parse(
            VirtualMachines.Body(Compute.ClusterId, cloudInit: Compute.VaultPath("web") + "#userdata")
        );

        var outcome = await reconciler.ReconcileAsync(
            Compute.Context(connection, address, body.RootElement, secrets),
            TestContext.Current.CancellationToken
        );

        outcome.ShouldBe(ReconcileOutcome.Converged);
        secrets.Resolves.ShouldBe(1);

        connection.Applied.Select(static x => x.Target.Kind.Kind)
            .ShouldBe(
                ["Secret", "VirtualMachine"],
                "the Secret goes first: a machine that names a Secret not yet there cannot start"
            );

        var secret = connection.Applied[0];
        secret.Target.Name.ShouldBe("web-cloud-init");
        var data = JsonNode.Parse(secret.Body)!["data"]![VirtualMachines.CloudInitKey]!.GetValue<string>();
        Encoding.UTF8.GetString(Convert.FromBase64String(data)).ShouldStartWith("#cloud-config");

        // ⚠ THE VALUE IS IN THE SECRET AND NOWHERE ELSE — docs/plan/13's non-negotiable for this type.
        var machine = connection.Applied[1].Body;
        machine.ShouldNotContain("#cloud-config");
        machine.ShouldNotContain(Compute.VaultPath("web"));
        machine.ShouldContain("web-cloud-init");
        Compute.Spec(machine)["template"]!["spec"]!["volumes"]!.AsArray().Count.ShouldBe(2);

        // And a body with no handle renders no Secret and no cloud-init volume at all.
        var plain = new RecordingConnection();
        using var plainBody = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));
        await Pass(reconciler, plain, address, plainBody.RootElement);
        plain.Applied.Select(static x => x.Target.Kind.Kind).ShouldBe(["VirtualMachine"]);
        plain.Applied[0].Body.ShouldNotContain("cloudInitNoCloud");
    }

    [Fact]
    public async Task AHandleTheVaultDoesNotHoldEndsThePassWithoutApplyingTheMachine() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        using var body = JsonDocument.Parse(
            VirtualMachines.Body(Compute.ClusterId, cloudInit: Compute.VaultPath("missing") + "#userdata")
        );

        var outcome = await reconciler.ReconcileAsync(
            Compute.Context(connection, address, body.RootElement, new SeededSecrets()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        connection.Applied.ShouldBeEmpty("a machine was applied that would mount a Secret nothing can fill");
    }

    [Fact]
    public async Task AHandleOutsideTheTenantsOwnVaultPrefixIsRefusedBeforeItIsResolved() {
        // ⚠ THE EXFILTRATION THE #28 REVIEW FOUND, CLOSED. The vault is shared and its token is the
        // platform's, so a path is the only thing that scopes a read; tenant A's body names tenant B's
        // registry password, which the seeded vault HOLDS — and the pass ends with the resolver never
        // asked, nothing applied, and a refusal that names A's own prefix rather than B's path.
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var foreign = Compute.VaultPath(
            "CyberCloud.ContainerRegistry/registries/11111111-1111-4111-8111-111111111111",
            Compute.TenantB
        );
        var secrets = new SeededSecrets((foreign, "password", "bobs-registry-password"));
        var address = Compute.Machine("web", Compute.TenantA, Compute.SubscriptionA);
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, cloudInit: foreign + "#password"));

        var outcome = await reconciler.ReconcileAsync(
            Compute.Context(connection, address, body.RootElement, secrets),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        outcome.Error.Message.ShouldContain(VirtualMachines.TenantVaultPrefix(Compute.TenantA));
        outcome.Error.Message.ShouldNotContain("bobs-registry-password");
        secrets.Resolves.ShouldBe(0, "the resolver was asked for another tenant's path; the check has to come first");
        connection.Applied.ShouldBeEmpty();

        // And the same shape under A's own prefix resolves, so the refusal is about the prefix and not the path's depth.
        var own = Compute.VaultPath("CyberCloud.ContainerRegistry/registries/11111111-1111-4111-8111-111111111111");
        using var ownBody = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, cloudInit: own + "#password"));
        var ownSecrets = new SeededSecrets((own, "password", "alices-own"));

        (await reconciler.ReconcileAsync(
                Compute.Context(connection, address, ownBody.RootElement, ownSecrets),
                TestContext.Current.CancellationToken
            ))
            .ShouldBe(ReconcileOutcome.Converged);
        ownSecrets.Resolves.ShouldBe(1);
    }

    // ── Disks and the network join ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DataDisksBecomeClaimsByNameAndTheJoinIsAnAnnotation() {
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var ns = ReconcileDriver(address);
        using var body = JsonDocument.Parse(
            VirtualMachines.Body(Compute.ClusterId, dataDisks: ["data", "logs"], virtualNetwork: "vnet", subnet: "web")
        );

        await Pass(new VirtualMachineReconciler(new FixedClock()), connection, address, body.RootElement);

        var spec = Compute.Spec(connection.Applied.Single().Body);
        var volumes = spec["template"]!["spec"]!["volumes"]!.AsArray();

        volumes.Select(static x => x!["persistentVolumeClaim"]?["claimName"]?.GetValue<string>())
            .Where(static x => x is not null)
            .ShouldBe(["data", "logs"]);
        volumes[0]!["dataVolume"]!["name"]!.GetValue<string>().ShouldBe("web-root");

        spec["template"]!["spec"]!["domain"]!["devices"]!["disks"]!.AsArray()[0]!["bootOrder"]!
            .GetValue<int>()
            .ShouldBe(1, "the root disk boots first");

        spec["template"]!["metadata"]!["annotations"]![VirtualMachines.LogicalSwitchAnnotation]!.GetValue<string>()
            .ShouldBe(ns + "-vnet-web");

        // ⚠ ADR-013 on the objects KubeVirt derives from ours: the launcher pod carries the pod
        // template's labels and the root claim carries the DataVolume template's, so both templates
        // are stamped with the lifetime-stable set — which is how billing attributes the pod.
        spec["template"]!["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldBe(KubeLabels.GuidValue(address.Id));
        spec["dataVolumeTemplates"]![0]!["metadata"]!["labels"]![KubeLabels.ResourceId]!.GetValue<string>()
            .ShouldBe(KubeLabels.GuidValue(address.Id));
    }

    [Fact]
    public async Task ADiskTheBodyCannotRenderIsRefusedBeforeAnythingIsRead() {
        // ⚠ Two kinds of name the schema admits and a real cluster would not: the machine's own volume
        // names, which KubeVirt's webhook refuses as duplicates, and strings that are not resource
        // names at all, which the API server refuses as a claim name — neither refuser is in either
        // conformance suite. VirtualMachines.DataDiskProblem.
        foreach (var bad in new[] {
                     VirtualMachines.RootVolume, VirtualMachines.CloudInitVolume, "Not A Name", "../../etc"
                 }) {
            var connection = new RecordingConnection();
            using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, dataDisks: ["data", bad]));

            var outcome = await Pass(
                new VirtualMachineReconciler(new FixedClock()),
                connection,
                Compute.Machine("web"),
                body.RootElement
            );

            outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed, bad);
            outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
            outcome.Error.Target.ShouldBe("/properties/dataDisks");
            outcome.Error.Message.ShouldContain(bad);
            connection.Applied.ShouldBeEmpty();
            connection.Read.ShouldBeEmpty("a body that cannot render should cost the cluster nothing");
        }
    }

    [Fact]
    public async Task ObserveReportsTheInstancePhaseAndTheRunStrategy() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var ns = ReconcileDriver(address);
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);

        var noInstance = await reconciler.ObserveAsync(
            Compute.Observe(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );
        noInstance.Exists.ShouldBeTrue();
        noInstance.Summary.ShouldContain("run strategy Always");
        noInstance.Summary.ShouldContain("no instance");

        connection.Objects[RecordingConnection.Key(VirtualMachines.InstanceRef(ns, "web"))] =
            new JsonObject {
                ["kind"] = "VirtualMachineInstance", ["status"] = new JsonObject { ["phase"] = "Scheduling" }
            }.ToJsonString();

        var scheduling = await reconciler.ObserveAsync(
            Compute.Observe(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );
        scheduling.Summary.ShouldContain("instance Scheduling");
    }

    [Fact]
    public async Task DeleteRemovesTheMachineThenTheSecretAndLeavesDisksAndImagesAlone() {
        var reconciler = new VirtualMachineReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var address = Compute.Machine("web");
        var ns = ReconcileDriver(address);
        var secrets = new SeededSecrets((Compute.VaultPath("web"), "userdata", "#cloud-config"));
        using var body = JsonDocument.Parse(
            VirtualMachines.Body(
                Compute.ClusterId,
                dataDisks: ["data"],
                cloudInit: Compute.VaultPath("web") + "#userdata"
            )
        );

        await reconciler.ReconcileAsync(
            Compute.Context(connection, address, body.RootElement, secrets),
            TestContext.Current.CancellationToken
        );

        var disk = Disks.DataVolumeRef(ns, "data");
        var image = Images.DataVolumeRef(ns, "ubuntu");
        connection.Objects[RecordingConnection.Key(disk)] = """{"kind":"DataVolume"}""";
        connection.Objects[RecordingConnection.Key(image)] = Compute.ImportedImage("ubuntu");

        var outcome = await reconciler.DeleteAsync(
            Compute.Context(connection, address, body.RootElement, secrets),
            TestContext.Current.CancellationToken
        );

        outcome.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["VirtualMachine", "Secret"]);
        connection.Objects.Keys.OrderBy(static x => x, StringComparer.Ordinal)
            .ShouldBe(
                [RecordingConnection.Key(disk), RecordingConnection.Key(image)],
                Case.Sensitive,
                "a machine's delete reached a disk or an image, which are other resources' objects"
            );
    }

    [Fact]
    public async Task ASuspendedClusterIsInProgressAndARefusalIsAFailure() {
        var address = Compute.Machine("web");
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        var suspended = await Pass(
            new VirtualMachineReconciler(new FixedClock()),
            new RecordingConnection { Suspend = true },
            address,
            body.RootElement
        );
        suspended.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);

        var refused = await Pass(
            new VirtualMachineReconciler(new FixedClock()),
            new RecordingConnection { RefuseWith = ErrorCode.PolicyViolation },
            address,
            body.RootElement
        );
        refused.Kind.ShouldBe(ReconcileOutcomeKind.Failed);

        var swallowed = await Pass(
            new VirtualMachineReconciler(new FixedClock()),
            new RecordingConnection { SwallowApplies = true },
            address,
            body.RootElement
        );
        swallowed.Kind.ShouldBe(
            ReconcileOutcomeKind.InProgress,
            "clause 4: Converged follows a read, never an apply's own result"
        );
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static async Task<ReconcileOutcome> Pass(
        VirtualMachineReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            Compute.Context(connection, address, desired),
            TestContext.Current.CancellationToken
        );

    static string ReconcileDriver(ResourceId address) =>
        CyberCloud.ResourceManager.Reconcile.ReconcileDriver.NamespaceFor(address);

    static int Cores(string objectJson) =>
        Compute.Spec(objectJson)["template"]!["spec"]!["domain"]!["cpu"]!["cores"]!.GetValue<int>();
}
