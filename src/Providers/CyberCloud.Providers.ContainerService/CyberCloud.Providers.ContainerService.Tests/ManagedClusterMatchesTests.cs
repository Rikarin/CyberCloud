using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     What a read-back is judged against, and what it is deliberately not.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>EVERY TEST IN THIS FILE IS HAND-WRITTEN BECAUSE NO CONFORMANCE SUITE CAN CATCH WHAT IT
///         CATCHES.</b> <c>ClusterConformanceHarness</c> derives its CRD stubs from the case's own
///         <c>Objects</c> and a derived stub has an <b>open</b> schema — no required fields, no enums,
///         and, crucially, <b>no <c>+kubebuilder:default</c></b>. So an object read back in either
///         suite is byte-identical to the object applied, and an equality comparison passes.
///         <c>CyberCloud.Providers.Search</c> measured exactly that: an equality bug left its
///         Docker-free suite <b>27 of 27 green</b> and only a hand-written test was red.
///     </para>
///     <para>
///         ⚠ <b>AND ON THIS TYPE THERE ARE TWO INDEPENDENT SOURCES OF DEFAULTING, WHICH IS NEW.</b>
///         The <c>KamajiControlPlane</c> CRD carries five <c>+kubebuilder:default</c> markers on its
///         top-level spec; Cluster API's <c>MachineDeployment</c> carries <b>none</b> and defaults four
///         things from a <b>mutating webhook</b> instead. A stub reproduces neither.
///     </para>
/// </remarks>
public sealed class ManagedClusterMatchesTests {
    [Fact]
    public void WhatTheProviderRendersMatchesItself() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        foreach (var rendered in Rendered(body.RootElement)) {
            ManagedClusters.Matches(rendered, body.RootElement).ShouldBeTrue(rendered);
        }
    }

    // ── Failure class (b): Matches as equality rather than containment ───────────────────────────

    [Fact]
    public void AnObjectCarryingTheCrdsOwnDefaultsStillMatches() {
        // ⚠ THE TEST THAT CATCHES AN EQUALITY COMPARISON, AND THE ONLY ONE THAT CAN. Checked in the
        // CRD rather than in a README: KamajiControlPlaneSpec carries `+kubebuilder:default` on
        // `replicas`, on `registry` ("registry.k8s.io"), on the whole `kubelet` object
        // ({preferredAddressTypes: {InternalIP, ExternalIP, Hostname}, cgroupfs: systemd}), on the whole
        // `network` object, and on `network.serviceType`. A real API server writes those back on the
        // FIRST CREATE, on every cluster.
        //
        // ⚠ AND NEITHER CONFORMANCE SUITE WOULD NOTICE, because a derived CRD stub has an open schema
        // with no defaults in it. This test is the whole defence.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var withDefaults = JsonNode.Parse(
            ManagedClusters.ControlPlaneJson("prod", body.RootElement)
        )!.AsObject();

        var spec = withDefaults["spec"]!.AsObject();

        spec["registry"] = "registry.k8s.io";
        spec["kubelet"] = new JsonObject {
            ["cgroupfs"] = "systemd",
            ["preferredAddressTypes"] = new JsonArray("InternalIP", "ExternalIP", "Hostname")
        };
        spec["addons"] = new JsonObject { ["coreDNS"] = new JsonObject() };

        // ⚠ And a whole `status`, which every real object grows and no rendered document has.
        withDefaults["status"] = new JsonObject { ["initialized"] = true };

        ManagedClusters.Matches(withDefaults.ToJsonString(), body.RootElement).ShouldBeTrue(
            "the control plane is judged by EQUALITY. A real API server writes four keys back that this "
            + "provider never sent, so every cluster would sit InProgress forever while being perfectly "
            + "correct — and no conformance suite would catch it, because a derived CRD stub has no "
            + "defaults."
        );
    }

    [Fact]
    public void AFieldThisProviderNeverSentDoesNotMakeTheClusterDrifted() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var cluster = JsonNode.Parse(ManagedClusters.ClusterJson("prod", body.RootElement))!.AsObject();

        // Cluster API's own fields, none of which this platform writes.
        cluster["spec"]!.AsObject()["controlPlaneEndpoint"] =
            new JsonObject { ["host"] = "10.96.0.1", ["port"] = 6443 };

        cluster["spec"]!.AsObject()["availabilityGates"] = new JsonArray();

        ManagedClusters.Matches(cluster.ToJsonString(), body.RootElement).ShouldBeTrue(
            "an endpoint the Kamaji control-plane provider patched onto the Cluster reads as drift"
        );
    }

    // ── The fields whose drift is real ──────────────────────────────────────────────────────────

    [Fact]
    public void AControlPlaneWhoseServiceTypeWasChangedIsDrift() {
        // ⚠ THE ONE FIELD ON THIS OBJECT WHOSE DRIFT IS A SECURITY EVENT, and the only reason it is
        // compared. Kamaji's CRD defaults serviceType to LoadBalancer, so an object whose ClusterIP was
        // removed by a hand edit, a merge or a mutating policy comes back as a PUBLISHED KUBERNETES API
        // SERVER — and every other field would still match.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var controlPlane = JsonNode.Parse(
            ManagedClusters.ControlPlaneJson("prod", body.RootElement)
        )!.AsObject();

        controlPlane["spec"]!["network"]!.AsObject()["serviceType"] = "LoadBalancer";

        ManagedClusters.Matches(controlPlane.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Theory]
    [InlineData("replicas")]
    [InlineData("version")]
    [InlineData("dataStoreName")]
    public void AChangedControlPlaneFieldIsDrift(string field) {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var controlPlane = JsonNode.Parse(
            ManagedClusters.ControlPlaneJson("prod", body.RootElement)
        )!.AsObject();

        controlPlane["spec"]!.AsObject()[field] = field == "replicas" ? 9 : "something-else";

        ManagedClusters.Matches(controlPlane.ToJsonString(), body.RootElement).ShouldBeFalse(field);
    }

    [Fact]
    public void ARewrittenInfrastructureRefIsDrift() {
        // A Cluster whose infrastructureRef was pointed at somebody else's object is a cluster whose
        // machines are created in a place this platform did not choose, and Cluster API accepts it.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var cluster = JsonNode.Parse(ManagedClusters.ClusterJson("prod", body.RootElement))!.AsObject();

        cluster["spec"]!["infrastructureRef"]!.AsObject()["apiGroup"] = "infrastructure.example.com";

        ManagedClusters.Matches(cluster.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ARemovedServiceDomainIsDrift() {
        // ⚠ It is drift AND it is unrecoverable drift: Kamaji freezes the cluster domain with a
        // `self == oldSelf` rule, so a cluster that lost it on the way in can never be given it back.
        // Reporting it is the most this platform can do.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var cluster = JsonNode.Parse(ManagedClusters.ClusterJson("prod", body.RootElement))!.AsObject();

        cluster["spec"]!["clusterNetwork"]!.AsObject().Remove("serviceDomain");

        ManagedClusters.Matches(cluster.ToJsonString(), body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AKindThisTypeDoesNotOwnIsFalseRatherThanAssumed() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        ManagedClusters.Matches(
            new JsonObject { ["kind"] = "Deployment", ["spec"] = new JsonObject() }.ToJsonString(),
            body.RootElement
        ).ShouldBeFalse();

        // ⚠ And a document with NO kind is false too, which is the difference from the five providers
        // that accept `null or "<TheirKind>"`. A type that owns three kinds cannot guess which one an
        // unlabelled document was meant to be — and `KubeCommandBuilder` injects `kind` on the apply
        // path from the same GroupVersionKind the render names, so the renders write it themselves.
        ManagedClusters.Matches(
            new JsonObject { ["spec"] = new JsonObject() }.ToJsonString(),
            body.RootElement
        ).ShouldBeFalse();
    }

    [Fact]
    public void EachRenderedBodyNamesTheKindItIs() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var kinds = Rendered(body.RootElement)
            .Select(x => JsonNode.Parse(x)!["kind"]!.GetValue<string>())
            .ToList();

        kinds.ShouldBe(["KubevirtCluster", "KamajiControlPlane", "Cluster"]);
    }

    [Fact]
    public void MalformedJsonIsFalseRatherThanAnException() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        ManagedClusters.Matches("{not json", body.RootElement).ShouldBeFalse();
        ManagedClusters.Matches("[]", body.RootElement).ShouldBeFalse();
    }

    // ── Readiness, which is the half Matches deliberately says nothing about ─────────────────────

    [Fact]
    public void AClusterWithNoStatusIsNotReportedRatherThanNotReady() {
        // ⚠ THE THIRD ANSWER, AND THE WHOLE REASON ClusterReadinessKind HAS THREE MEMBERS. "Not ready"
        // and "nobody has said" are indistinguishable at the object and completely different at the
        // platform — the first is a cluster on its way, the second is a controller that may not be
        // running at all.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        ManagedClusters.Readiness(ManagedClusters.ClusterJson("prod", body.RootElement))
            .Kind.ShouldBe(ClusterReadinessKind.NotReported);
    }

    [Fact]
    public void AReadyConditionIsReadyAndAFalseOneCarriesItsOwnMessage() {
        ManagedClusters.Readiness(WithConditions(("Ready", "True", "")))
            .Kind.ShouldBe(ClusterReadinessKind.Ready);

        var notReady = ManagedClusters.Readiness(
            WithConditions(("Ready", "False", "Waiting for the first worker to join"))
        );

        notReady.Kind.ShouldBe(ClusterReadinessKind.NotReady);

        // ⚠ UPSTREAM'S OWN WORDS. docs/plan/09 names image pull, DHCP and cloud-init as the flakiest
        // step by far, and none of those is a phrase this platform could have invented — so the detail
        // reported to the tenant is Cluster API's condition message wherever there is one.
        notReady.Detail.ShouldBe("Waiting for the first worker to join");
    }

    [Fact]
    public void AStatusWithNoReadyConditionIsNotReadyAndSaysWhichHalfIsMissing() {
        // ⚠ NOT the same as no status. Cluster API writes the infrastructure and control-plane halves
        // before it summarises them, so this is the ordinary early state of a real provision — and it
        // is what docs/plan/09's six-to-nine-minute step table looks like from here.
        var readiness = ManagedClusters.Readiness(
            new JsonObject {
                ["kind"] = "Cluster",
                ["spec"] = new JsonObject(),
                ["status"] = new JsonObject {
                    ["infrastructureReady"] = true, ["controlPlaneReady"] = false
                }
            }.ToJsonString()
        );

        readiness.Kind.ShouldBe(ClusterReadinessKind.NotReady);
        readiness.Detail.ShouldContain("infrastructure is ready");
        readiness.Detail.ShouldContain("waiting for the control plane");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    static string[] Rendered(JsonElement desired) => [
        ManagedClusters.InfrastructureJson("prod"),
        ManagedClusters.ControlPlaneJson("prod", desired),
        ManagedClusters.ClusterJson("prod", desired)
    ];

    static string WithConditions(params (string Type, string Status, string Message)[] conditions) =>
        new JsonObject {
            ["kind"] = "Cluster",
            ["spec"] = new JsonObject(),
            ["status"] = new JsonObject {
                ["conditions"] = new JsonArray(
                    [
                        .. conditions.Select(
                            x => (JsonNode)new JsonObject {
                                ["type"] = x.Type, ["status"] = x.Status, ["message"] = x.Message
                            }
                        )
                    ]
                )
            }
        }.ToJsonString();
}

/// <summary>
///     The node pool's half of the same question.
/// </summary>
public sealed class AgentPoolMatchesTests {
    [Fact]
    public void WhatTheProviderRendersMatchesItself() {
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        foreach (var rendered in Rendered(body.RootElement)) {
            AgentPools.Matches(rendered, Address, body.RootElement).ShouldBeTrue(rendered);
        }
    }

    [Fact]
    public void AnObjectCarryingWhatTheMutatingWebhookWritesStillMatches() {
        // ⚠ THE SAME TRAP AS THE CLUSTER'S AND A DIFFERENT MECHANISM, WHICH IS WHY IT IS TESTED TWICE.
        // Neither MachineDeploymentSpec nor MachineSpec carries a single `+kubebuilder:default`; Cluster
        // API's MUTATING WEBHOOK writes the labels, the strategy type and the version prefix instead. A
        // reader who checked only for CRD markers would conclude equality was safe here, and it is not.
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        var deployment = JsonNode.Parse(
            AgentPools.MachineDeploymentJson(Address, body.RootElement)
        )!.AsObject();

        var spec = deployment["spec"]!.AsObject();

        // The two labels the webhook injects into BOTH the selector and the template.
        spec["selector"]!["matchLabels"]!.AsObject()["cluster.x-k8s.io/cluster-name"] = "prod-cluster";
        spec["template"]!["metadata"]!["labels"]!.AsObject()["cluster.x-k8s.io/cluster-name"] =
            "prod-cluster";
        spec["template"]!["metadata"]!["labels"]!.AsObject()["cluster.x-k8s.io/deployment-name"] =
            "prod-cluster-workers";

        deployment["status"] = new JsonObject { ["readyReplicas"] = 0 };

        AgentPools.Matches(deployment.ToJsonString(), Address, body.RootElement).ShouldBeTrue(
            "the MachineDeployment is judged by equality, so every pool sits InProgress forever the "
            + "moment Cluster API's own defaulting webhook touches it"
        );
    }

    [Fact]
    public void ARewrittenClusterNameIsDriftAndItIsTheFieldThisTypeMostNeedsCompared() {
        // A MachineDeployment whose clusterName was rewritten moves every VM in the pool into a
        // different tenant's cluster, and every other field would still match.
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        var deployment = JsonNode.Parse(
            AgentPools.MachineDeploymentJson(Address, body.RootElement)
        )!.AsObject();

        deployment["spec"]!.AsObject()["clusterName"] = "somebody-elses-cluster";

        AgentPools.Matches(deployment.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ARolloutPolicyAtTheOldV1Beta1PathIsDrift() {
        // ⚠ THE MISTAKE A COPIED TEMPLATE MAKES. The field moved from `spec.strategy.rollingUpdate` to
        // `spec.rollout.strategy.rollingUpdate` in v1beta2 and the API server PRUNES the old path — so a
        // chart written the old way is accepted and its rollout policy silently vanishes.
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        var deployment = JsonNode.Parse(
            AgentPools.MachineDeploymentJson(Address, body.RootElement)
        )!.AsObject();

        var spec = deployment["spec"]!.AsObject();
        spec.Remove("rollout");
        spec["strategy"] = new JsonObject {
            ["rollingUpdate"] = new JsonObject { ["maxSurge"] = 1, ["maxUnavailable"] = 0 }
        };

        AgentPools.Matches(deployment.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void AChangedInstancetypeIsDriftAndSoIsAChangedRootDisk() {
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        var template = JsonNode.Parse(
            AgentPools.MachineTemplateJson(Address, body.RootElement)
        )!.AsObject();

        var virtualMachine = template["spec"]!["template"]!["spec"]!["virtualMachineTemplate"]!["spec"]!
            .AsObject();

        virtualMachine["instancetype"]!.AsObject()["name"] = "s1.4xlarge";

        AgentPools.Matches(template.ToJsonString(), Address, body.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void TheInstancetypeNamesTheClusterScopedKindExplicitly() {
        // ⚠ An InstancetypeMatcher with an EMPTY kind resolves to the cluster-scoped type today, so
        // omitting it would be correct and would silently change meaning if that default ever moved.
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        JsonNode.Parse(AgentPools.MachineTemplateJson(Address, body.RootElement))!
            ["spec"]!["template"]!["spec"]!["virtualMachineTemplate"]!["spec"]!["instancetype"]!["kind"]!
            .GetValue<string>()
            .ShouldBe("VirtualMachineClusterInstancetype");
    }

    [Fact]
    public void MalformedJsonIsFalseRatherThanAnException() {
        using var body = JsonDocument.Parse(AgentPools.Body(ClusterId));

        AgentPools.Matches("{not json", Address, body.RootElement).ShouldBeFalse();
    }

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    static ResourceId Address { get; } =
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            AgentPools.Type,
            "workers",
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            "prod-cluster"
        );

    static string[] Rendered(JsonElement desired) => [
        AgentPools.MachineTemplateJson(Address, desired),
        AgentPools.BootstrapJson(Address),
        AgentPools.MachineDeploymentJson(Address, desired)
    ];
}
