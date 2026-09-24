using CyberCloud.ResourceManager.Reconcile;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerInstance.Tests;

/// <summary>The pod a group renders, the vault handles it resolves, and when it converges.</summary>
public sealed class ContainerGroupReconcilerTests {
    [Fact]
    public async Task TheGroupIsOnePodWithASharedBudgetAndOnlyTheMainContainerTakesTheCommandAndPorts() {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = Groups.Body(
            ContainerGroups.Body(
                Groups.ClusterId,
                ["web=nginx:1.27", "log=busybox:1.37"],
                ["nginx", "-g", "daemon off;"],
                cpu: "1500m",
                memory: "1Gi",
                restartPolicy: "OnFailure",
                environment: ["LOG_LEVEL=info"],
                ports: ["80", "53/UDP"]
            )
        );

        var outcome = await Groups.Reconcile(connection, address, body);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);

        var pod = JsonNode.Parse(connection.Objects[GroupConnection.Key(ContainerGroups.PodRef(ns, "web"))])!;
        var spec = pod["spec"]!;

        spec["restartPolicy"]!.GetValue<string>().ShouldBe("OnFailure");
        spec["resources"]!["limits"]!["cpu"]!.GetValue<string>().ShouldBe("1500m", "one budget, on the pod");
        spec["resources"]!["requests"]!["memory"]!.GetValue<string>().ShouldBe("1Gi", "requests equal limits: Guaranteed");
        spec["automountServiceAccountToken"]!.GetValue<bool>().ShouldBeFalse("a tenant's workload holds no credential to the cluster");
        spec["enableServiceLinks"]!.GetValue<bool>().ShouldBeFalse();
        spec["imagePullSecrets"].ShouldBeNull("no registry credential was named");

        var containers = spec["containers"]!.AsArray();
        containers.Count.ShouldBe(2);
        containers[0]!["name"]!.GetValue<string>().ShouldBe("web");
        containers[0]!["command"]!.AsArray().Select(static x => x!.GetValue<string>()).ShouldBe(["nginx", "-g", "daemon off;"]);
        containers[0]!["ports"]!.AsArray().Select(static x => x!["protocol"]!.GetValue<string>()).ShouldBe(["TCP", "UDP"]);
        containers[1]!["command"].ShouldBeNull("a sidecar runs its image's own entrypoint");
        containers[1]!["ports"].ShouldBeNull();
        containers[1]!["env"]![0]!["name"]!.GetValue<string>().ShouldBe("LOG_LEVEL", "the environment is the group's");
        containers.ShouldAllBe(x => x!["resources"] == null, "the containers share the pod's budget and hold none of their own");

        // ⚠ The platform's seven labels are on the pod, which is what billing attributes a workload by.
        var labels = connection.Applied.Single(static x => x.Target.Kind.Kind == "Pod").Labels;
        labels[KubeLabels.ResourceId].ShouldBe(KubeLabels.GuidValue(address.Id));
    }

    [Fact]
    public async Task SecureEnvironmentAndThePullCredentialAreValuesInSecretsAndHandlesEverywhereElse() {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        var vault = new SeededSecrets(
            (Groups.VaultPath("db"), "password", "s3cr3t-value"),
            (Groups.VaultPath("CyberCloud.ContainerRegistry/registries/r1"), "adminPassword", "harbor-pass")
        );
        using var body = Groups.Body(
            ContainerGroups.Body(
                Groups.ClusterId,
                ["app=registry.example.com/team/app:2.1"],
                secureEnvironment: ["DB_PASSWORD=" + Groups.VaultPath("db") + "#password"],
                registryServer: "registry.example.com",
                registryUsername: "admin",
                registryPassword: Groups.VaultPath("CyberCloud.ContainerRegistry/registries/r1") + "#adminPassword"
            )
        );

        var outcome = await Groups.Reconcile(connection, address, body, vault);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Error?.Message);

        var env = KubeSecret.Value(
                (await connection.GetAsync(ContainerGroups.EnvironmentSecretRef(ns, "web"), TestContext.Current.CancellationToken))
                .GetValueOrThrow(),
                "DB_PASSWORD"
            )
            .GetValueOrThrow();
        env.ShouldBe("s3cr3t-value");

        var pull = JsonNode.Parse(connection.Objects[GroupConnection.Key(ContainerGroups.PullSecretRef(ns, "web"))])!;
        pull["type"]!.GetValue<string>().ShouldBe("kubernetes.io/dockerconfigjson");
        var config = JsonNode.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(pull["data"]![ContainerGroups.DockerConfigKey]!.GetValue<string>()))
        )!;
        config["auths"]!["registry.example.com"]!["auth"]!.GetValue<string>()
            .ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:harbor-pass")));

        // ⚠ THE VALUE IS IN THE TWO SECRETS AND IN NOTHING ELSE THE PLATFORM APPLIED.
        var pod = connection.Applied.Single(static x => x.Target.Kind.Kind == "Pod").Body;
        pod.ShouldNotContain("s3cr3t-value");
        pod.ShouldNotContain("harbor-pass");
        var container = JsonNode.Parse(pod)!["spec"]!["containers"]![0]!;
        container["env"]![0]!["valueFrom"]!["secretKeyRef"]!["name"]!.GetValue<string>().ShouldBe("web-env");
        JsonNode.Parse(pod)!["spec"]!["imagePullSecrets"]![0]!["name"]!.GetValue<string>().ShouldBe("web-pull");
    }

    [Theory]
    [InlineData("secure")]
    [InlineData("registry")]
    public async Task AHandleOutsideTheTenantsOwnVaultPrefixIsRefusedBeforeItIsResolved(string where) {
        // ⚠ The one-token resolver means the path is the whole discriminator — VirtualMachines'
        // TenantVaultPrefix argument, which this type inherits for two properties instead of one.
        var connection = new GroupConnection();
        var foreign = Groups.VaultPath("CyberCloud.ContainerRegistry/registries/x", Groups.TenantB);
        var vault = new SeededSecrets((foreign, "adminPassword", "tenant-b's"));
        using var body = Groups.Body(
            where == "secure"
                ? ContainerGroups.Body(Groups.ClusterId, secureEnvironment: ["STOLEN=" + foreign + "#adminPassword"])
                : ContainerGroups.Body(
                    Groups.ClusterId,
                    registryServer: "registry.example.com",
                    registryUsername: "admin",
                    registryPassword: foreign + "#adminPassword"
                )
        );

        var outcome = await Groups.Reconcile(connection, Groups.Address("web"), body, vault);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        outcome.Error.Message.ShouldContain(ContainerGroups.TenantVaultPrefix(Groups.TenantA));
        vault.Resolves.ShouldBe(0, "a foreign path was resolved before it was refused");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task APendingPodIsInProgressWithTheKubeletsReasonAndARunningOneConverges() {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var pod = ContainerGroups.PodRef(ReconcileDriver.NamespaceFor(address), "web");
        using var body = Groups.Body(ContainerGroups.Body(Groups.ClusterId, ["app=registry.invalid/nothing:1"]));

        await Groups.Reconcile(connection, address, body);

        Groups.Report(
            connection,
            pod,
            new() {
                ["phase"] = "Pending",
                ["containerStatuses"] = new JsonArray(
                    new JsonObject {
                        ["name"] = "app",
                        ["restartCount"] = 0,
                        ["state"] = new JsonObject {
                            ["waiting"] = new JsonObject {
                                ["reason"] = "ErrImagePull", ["message"] = "failed to resolve reference \"registry.invalid/nothing:1\""
                            }
                        }
                    }
                )
            }
        );

        var pending = await Groups.Reconcile(connection, address, body);

        pending.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        pending.Reason.ShouldContain("ErrImagePull");
        pending.Reason.ShouldContain("registry.invalid/nothing:1", Case.Sensitive, "the registry's own sentence reaches the tenant");

        Groups.Report(connection, pod, new() { ["phase"] = "Running" });
        (await Groups.Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        // A batch group that ran to its end is where it is meant to stop.
        Groups.Report(connection, pod, new() { ["phase"] = "Succeeded" });
        (await Groups.Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
    }

    [Fact]
    public async Task AChangedBodyReplacesThePodAndWaitsForTheOldOneToGo() {
        // ⚠ A pod's containers, environment and resources cannot be changed in place — the API server
        // refuses the apply — so a changed body is a deleted pod and a new one under the same name.
        var connection = new GroupConnection { Terminating = true };
        var address = Groups.Address("web");
        var pod = ContainerGroups.PodRef(ReconcileDriver.NamespaceFor(address), "web");
        using var first = Groups.Body(ContainerGroups.Body(Groups.ClusterId));
        using var second = Groups.Body(ContainerGroups.Body(Groups.ClusterId, cpu: "2"));

        await Groups.Reconcile(connection, address, first);
        var uid = ContainerGroups.UidOf(connection.Objects[GroupConnection.Key(pod)]);

        var replacing = await Groups.Reconcile(connection, address, second);

        replacing.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Deleted.ShouldBe([pod]);
        ContainerGroups.IsTerminating(connection.Objects[GroupConnection.Key(pod)]).ShouldBeTrue();

        // Still terminating: the pass waits rather than applying onto the pod being deleted.
        var applies = connection.Applied.Count(static x => x.Target.Kind.Kind == "Pod");
        (await Groups.Reconcile(connection, address, second)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Applied.Count(static x => x.Target.Kind.Kind == "Pod").ShouldBe(applies);

        connection.Finish(pod);

        (await Groups.Reconcile(connection, address, second)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        ContainerGroups.UidOf(connection.Objects[GroupConnection.Key(pod)]).ShouldNotBe(uid, "the group runs a new pod");
    }

    [Fact]
    public async Task ThePodMatchesWhenTheApiServerCanonicalisesAQuantity() {
        // ⚠ The API server stores `0.5` as `500m` and `1024Mi` as `1Gi`; a string comparison would call
        // every such body drifted and replace the pod on every pass, forever.
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var pod = ContainerGroups.PodRef(ReconcileDriver.NamespaceFor(address), "web");
        using var body = Groups.Body(ContainerGroups.Body(Groups.ClusterId, cpu: "0.5", memory: "1024Mi"));

        await Groups.Reconcile(connection, address, body);

        var stored = JsonNode.Parse(connection.Objects[GroupConnection.Key(pod)])!.AsObject();
        stored["spec"]!["resources"]!["limits"]!["cpu"] = "500m";
        stored["spec"]!["resources"]!["limits"]!["memory"] = "1Gi";

        ContainerGroups.Matches(stored.ToJsonString(), ReconcileDriver.NamespaceFor(address), body.RootElement).ShouldBeTrue();
    }

    [Fact]
    public async Task APublicAddressIsOneFloatingIpOntoThePodsOwnIpObject() {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = Groups.Body(
            ContainerGroups.Body(
                Groups.ClusterId,
                ports: ["80"],
                virtualNetwork: "vnet",
                subnet: "apps",
                ipAddress: "10.20.1.30",
                publicIpAddress: "front"
            )
        );

        (await Groups.Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var fip = connection.Applied.Single(static x => x.Target.Kind.Kind == "OvnFip");
        fip.Target.Namespace.ShouldBeEmpty("an OvnFip is cluster-scoped");
        fip.Target.Name.ShouldBe(ns + "-web", "the namespace folds into a cluster-scoped name");

        var spec = JsonNode.Parse(fip.Body)!["spec"]!;
        spec["ovnEip"]!.GetValue<string>().ShouldBe(ns + "-front");
        spec["ipName"]!.GetValue<string>().ShouldBe("web." + ns, "Kube-OVN names a pod's IP object {pod}.{namespace}");

        var annotations = JsonNode.Parse(connection.Applied.Single(static x => x.Target.Kind.Kind == "Pod").Body)!["metadata"]!["annotations"]!;
        annotations[ContainerGroups.LogicalSwitchAnnotation]!.GetValue<string>().ShouldBe(ns + "-vnet-apps");
        annotations[ContainerGroups.IpAddressAnnotation]!.GetValue<string>().ShouldBe("10.20.1.30");
    }

    [Fact]
    public async Task TheDeleteRemovesThePodForegroundAndBothSecrets() {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = Groups.Body(ContainerGroups.Body(Groups.ClusterId));

        await Groups.Reconcile(connection, address, body);

        var deleted = await new ContainerGroupReconciler(new FixedClock()).DeleteAsync(
            Groups.Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        connection.Objects.ShouldBeEmpty();
        connection.Deleted.ShouldBe([ContainerGroups.PodRef(ns, "web")], "only the pod was there to delete");
    }
}
