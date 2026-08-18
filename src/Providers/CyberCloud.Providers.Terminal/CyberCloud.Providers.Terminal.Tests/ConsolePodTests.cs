using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>
///     What the shell pod and the network policy actually say — this row's answer to "what can the pod
///     reach when the tenant asks for nothing".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>EVERY ASSERTION HERE IS ABOUT RENDERED YAML AND NOT ABOUT BEHAVIOUR, AND THAT IS THE
///         HONEST LIMIT OF WHAT THIS PROJECT CAN CLAIM.</b> Failure class (c) — an unsafe default when
///         the tenant asks for nothing — has three sightings in this tree (SeaweedFS' anonymous admin,
///         Qdrant's unset api_key, MariaDB's root password), and every one of them was a field that
///         was absent rather than wrong. So the test that catches the fourth has to read what is
///         written, from a body that asks for nothing, and name each default it is closing.
///     </para>
///     <para>
///         ⚠ <b>What it cannot do is prove the constraint holds.</b> A NetworkPolicy applies and reads
///         back identically in a cluster that enforces it and in one that does not.
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>a-networkpolicy-that-nothing-enforces-still-reads-back</c>.
///     </para>
/// </remarks>
public sealed class ConsolePodTests {
    // ── Failure class (c): the unsafe default ─────────────────────────────────────────────────

    [Fact]
    public void TheShellMountsNoKubernetesServiceAccountTokenInEitherPlaceItCouldBeMounted() {
        // ⚠ THE UNSAFE DEFAULT THIS ROW EXISTS TO CLOSE, AND IT IS THE WORST OF THE FOUR SIGHTINGS
        // BECAUSE THE IMAGE CONTAINS kubectl. A pod with no serviceAccountName runs as the namespace's
        // `default` account with its token mounted at
        // /var/run/secrets/kubernetes.io/serviceaccount, and whatever RBAC that account has becomes
        // the tenant's — which, in a cluster where somebody once bound cluster-admin to it, is
        // everything.
        //
        // Both places, because the pod-level field is the one that binds and the account-level one is
        // the one an auditor reads.
        var pod = Pod();
        pod["spec"]!["serviceAccountName"]!.GetValue<string>().ShouldBe("plain-shell");
        pod["spec"]!["automountServiceAccountToken"]!.GetValue<bool>().ShouldBeFalse();

        var account = JsonNode.Parse(CloudConsoles.ServiceAccountJson("plain", Desired))!.AsObject();
        account["automountServiceAccountToken"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void EveryFieldOfTheSecurityContextIsWrittenBecauseEveryDefaultIsTheWrongOne() {
        // docs/plan/19 § The pod: "Non-root, read-only root filesystem except $HOME and /tmp, no
        // privilege escalation, seccomp RuntimeDefault, dropped capabilities."
        //
        // ⚠ EACH LINE BELOW CLOSES A DEFAULT RATHER THAN RESTATING AN INTENTION:
        //   * an omitted runAsNonRoot runs as whatever the image's USER says, which for most base
        //     images is root;
        //   * allowPrivilegeEscalation DEFAULTS TO TRUE;
        //   * an omitted readOnlyRootFilesystem is a writable root filesystem;
        //   * an omitted capabilities.drop is the container runtime's default set, which includes
        //     CHOWN, SETUID, SETGID and NET_RAW;
        //   * an omitted seccompProfile is Unconfined unless the kubelet was started with a default
        //     profile, which is not something this platform controls.
        var pod = Pod();
        var security = pod["spec"]!["securityContext"]!;

        security["runAsNonRoot"]!.GetValue<bool>().ShouldBeTrue();
        security["runAsUser"]!.GetValue<int>().ShouldBe(CloudConsoles.ShellUid);
        security["runAsUser"]!.GetValue<int>().ShouldNotBe(0);
        security["seccompProfile"]!["type"]!.GetValue<string>().ShouldBe("RuntimeDefault");

        // ⚠ fsGroup, which is not in doc 19's list and has to be here anyway: without it a dynamically
        // provisioned volume comes up root-owned and $HOME is unwritable, which presents as a shell
        // that starts and cannot save anything.
        security["fsGroup"]!.GetValue<int>().ShouldBe(CloudConsoles.ShellUid);

        var container = pod["spec"]!["containers"]!.AsArray()[0]!;
        var inner = container["securityContext"]!;

        inner["allowPrivilegeEscalation"]!.GetValue<bool>().ShouldBeFalse();
        inner["readOnlyRootFilesystem"]!.GetValue<bool>().ShouldBeTrue();
        inner["capabilities"]!["drop"]!.AsArray().Select(x => x!.GetValue<string>()).ShouldBe(["ALL"]);
    }

    [Fact]
    public void DroppingAllCapabilitiesIsWhatMakesTcpdumpPresentAndBroken() {
        // ⚠ docs/plan/19 § The image's second footnote, honoured rather than quietly contradicted:
        // tcpdump "requires NET_RAW, which the pod does not have by default. It is present and it will
        // fail without an elevated session — documented rather than silently absent."
        //
        // This test exists so that the day somebody "fixes" tcpdump by adding NET_RAW back, they have
        // to delete an assertion that says why it is not there. NET_RAW is packet capture on the pod's
        // network namespace, which on a shared node is other tenants' traffic if the CNI ever gets it
        // wrong.
        var drop = Pod()["spec"]!["containers"]!.AsArray()[0]!["securityContext"]!["capabilities"]!["drop"]!
            .AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        drop.ShouldBe(["ALL"]);

        Pod()["spec"]!["containers"]!.AsArray()[0]!["securityContext"]!["capabilities"]!
            .AsObject()
            .ContainsKey("add")
            .ShouldBeFalse("a capability was added back; NET_RAW is packet capture and is the one to justify");
    }

    [Fact]
    public void NothingMayOpenAConnectionToAShell() {
        // ⚠ AN EMPTY LIST, WHICH IS NOT THE SAME AS NO KEY. With Ingress in policyTypes and an empty
        // rules array, nothing may connect to the pod. Omitting `ingress` entirely with Ingress still
        // in policyTypes means the same thing to the API — but it reads as an oversight, and the next
        // person to edit this would add a rule to a key that was not there rather than to one that
        // deliberately was.
        //
        // Every byte a person types reaches this pod through the API server's exec stream, which is
        // not pod-network traffic and is unaffected.
        var policy = Policy();

        policy["spec"]!["policyTypes"]!.AsArray().Select(x => x!.GetValue<string>())
            .ShouldBe(["Ingress", "Egress"]);

        policy["spec"]!["ingress"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void WhatTheShellReachesByDefaultIsExactlyFourThingsAndTheCloudMetadataAddressIsNotOneOfThem() {
        // ⚠ THIS IS THE ANSWER TO FAILURE CLASS (c) FOR THIS ROW, READ OFF THE RENDERED POLICY RATHER
        // THAN OFF ANYBODY'S INTENTION. A body that asks for nothing gets:
        //   1. its own namespace — the tenant's resources in this resource group;
        //   2. any namespace labelled with this tenant's id — see the owed item; matches nothing yet;
        //   3. kube-dns, on UDP and TCP;
        //   4. public addresses, minus every private range.
        // And nothing else. In particular: not the platform's namespaces, not another tenant's
        // workloads, and not 169.254.169.254.
        var egress = Policy()["spec"]!["egress"]!.AsArray();
        egress.Count.ShouldBe(4);

        egress[0]!["to"]![0]!["namespaceSelector"]!["matchLabels"]!["kubernetes.io/metadata.name"]!
            .GetValue<string>()
            .ShouldBe("plain-ns");

        egress[1]!["to"]![0]!["namespaceSelector"]!["matchLabels"]![KubeLabels.TenantId]!
            .GetValue<string>()
            .ShouldBe(Tenant.ToString("D"));

        // ⚠ BOTH PROTOCOLS. A UDP-only rule works until a response exceeds 512 bytes and the resolver
        // retries over TCP, which presents as "curl works and dig doesn't, sometimes".
        egress[2]!["ports"]!.AsArray().Select(x => x!["protocol"]!.GetValue<string>())
            .ShouldBe(["UDP", "TCP"]);

        var except = egress[3]!["to"]![0]!["ipBlock"]!["except"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        egress[3]!["to"]![0]!["ipBlock"]!["cidr"]!.GetValue<string>().ShouldBe("0.0.0.0/0");

        // ⚠ THE ONE TO READ TWICE. 169.254.0.0/16 is the cloud metadata address, and a shell that can
        // reach it can usually read a node's own instance credentials — the single most-used
        // escalation from a container anywhere.
        except.ShouldContain("169.254.0.0/16");
        except.ShouldContain("10.0.0.0/8");
        except.ShouldContain("172.16.0.0/12");
        except.ShouldContain("192.168.0.0/16");
        except.ShouldContain("100.64.0.0/10");
    }

    [Fact]
    public void APolicyThatComesBackWithNoIngressKeyAtAllStillMatches() {
        // ⚠ THE BUG A REAL API SERVER FOUND AND BOTH FAKE HARNESSES HID, PINNED SO IT CANNOT COME
        // BACK. `NetworkPolicySpec.Ingress` carries `omitempty`, so the empty rule list this provider
        // applies comes back from a real API server as NO `ingress` KEY. `Matches` compared
        // `is JsonArray { Count: 0 }` — true for what was applied, false for what was returned — so
        // the console converged in the Docker-free harness (which echoes the apply back) and in every
        // unit test here, and sat in InProgress forever against k3s. Four cluster-conformance
        // assertions went red on the first run and nothing else in the tree would have said why.
        //
        // ⚠ It is the exact hazard `Matches`' own remarks predicted for built-in types — "the API
        // server defaults them itself, in the same request" — met from the other direction: not a
        // field added, a field REMOVED.
        var served = JsonNode.Parse(CloudConsoles.NetworkPolicyJson("plain", Tenant, "plain-ns", Desired))!
            .AsObject();

        served["kind"] = "NetworkPolicy";
        served["spec"]!.AsObject().Remove("ingress").ShouldBeTrue();

        CloudConsoles.Matches(served.ToJsonString(), Desired).ShouldBeTrue(
            "an ingress list the API server omitted read as a policy that had lost its ingress rules"
        );

        // And a policy that grew an actual ingress rule IS drift, which is the half that keeps the
        // relaxation from being a hole: somebody opening a shell to inbound traffic must not read as
        // converged.
        served["spec"]!["ingress"] = new JsonArray { new JsonObject() };

        CloudConsoles.Matches(served.ToJsonString(), Desired).ShouldBeFalse(
            "a shell that something may now connect to reported no drift"
        );
    }

    [Fact]
    public void TenantOnlyRemovesThePublicRuleAndLeavesTheOtherThree() {
        // The other posture, so that "four rules" above is a fact about Internet rather than about the
        // renderer.
        using var tenantOnly = JsonDocument.Parse(CloudConsoles.Body(Cluster, egress: "TenantOnly"));

        var egress = JsonNode.Parse(
            CloudConsoles.NetworkPolicyJson("plain", Tenant, "plain-ns", tenantOnly.RootElement)
        )!["spec"]!["egress"]!.AsArray();

        egress.Count.ShouldBe(3);
        egress.ShouldAllBe(x => x!["to"]![0]!.AsObject().ContainsKey("ipBlock") == false);
    }

    [Fact]
    public void ThePolicySelectsTheConsolesOwnPodAndNotEveryPodInTheNamespace() {
        // ⚠ A resource group's namespace holds the tenant's OTHER workloads — their Postgres, their
        // Valkey — and a policy whose podSelector was empty would apply a shell's egress rules to all
        // of them, which is a tenant's database silently losing its network.
        Policy()["spec"]!["podSelector"]!["matchLabels"]![KubeLabels.ResourceType]!
            .GetValue<string>()
            .ShouldBe("cybercloud.terminal_consoles");
    }

    [Fact]
    public async Task ThePodCarriesTheLabelItsOwnNetworkPolicySelectsOn() {
        // ⚠ THE JOIN BETWEEN THE TWO OBJECTS, AND IT IS THE ONE THING THAT MAKES THE POLICY APPLY TO
        // ANYTHING. The pod is applied by the connect handler rather than by the reconciler, so it is
        // outside every conformance suite in the tree — and a pod applied by any route that skipped
        // KubeCommand would carry no cybercloud.io/resource-type and would be a shell no policy
        // governs.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        (await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        (await ConsoleReconcilerTests.Connect(connection, desired.RootElement)).IsSuccess.ShouldBeTrue();

        var pod = connection.Applied.Single(x => x.Target.Kind.Kind == "Pod");

        foreach (var label in KubeLabels.Mandatory) {
            pod.Labels.ShouldContainKey(label, pod.Target.ToString());
            pod.Labels[label].ShouldNotBeNullOrEmpty();
        }

        pod.Labels[KubeLabels.ResourceType].ShouldBe("cybercloud.terminal_consoles");

        var policy = connection.Applied.Single(x => x.Target.Kind.Kind == "NetworkPolicy");

        JsonNode.Parse(policy.Body)!["spec"]!["podSelector"]!["matchLabels"]![KubeLabels.ResourceType]!
            .GetValue<string>()
            .ShouldBe(
                pod.Labels[KubeLabels.ResourceType],
                "the policy selects a label the pod does not carry, so it governs nothing"
            );
    }

    // ── Failure class (b): an idle session that never goes away ───────────────────────────────

    [Fact]
    public void TheHardCapIsOnThePodSoItHoldsWithNothingOfThisPlatformRunning() {
        // ⚠ THE ONLY HALF OF docs/plan/19's SESSION POLICY THAT ANYTHING ENFORCES TODAY, AND IT IS
        // ENFORCED BY THE KUBELET. activeDeadlineSeconds is checked by the node, so a console whose
        // session grain died, whose silo moved, or whose reaper was never written still stops burning
        // CPU at the cap.
        Pod()["spec"]!["activeDeadlineSeconds"]!.GetValue<int>().ShouldBe(8 * 3600);

        // ⚠ restartPolicy: Never, which is unusual and is the point. A shell that restarted after its
        // own deadline expired would defeat the cap; one that restarted after the user typed `exit`
        // would be a terminal that will not close.
        Pod()["spec"]!["restartPolicy"]!.GetValue<string>().ShouldBe("Never");
    }

    [Fact]
    public void TheIdleTimeoutIsCarriedOnTheObjectRatherThanLookedUp() {
        // ⚠ THE HALF NOTHING ENFORCES, AND THE ANNOTATION IS WHAT MAKES THAT SURVIVABLE. A sweeper
        // that had to resolve every pod back to a resource body to learn its timeout would be a
        // sweeper that cannot run without the resource manager; carrying the number on the object
        // makes the reclaim decision readable by kubectl and by whatever eventually sweeps.
        // conformance.yaml § owed, `no-idle-reaper`.
        Pod()["metadata"]!["annotations"]![CloudConsoles.IdleTimeoutAnnotation]!
            .GetValue<string>()
            .ShouldBe("1200");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(120)]
    public void TheIdleTimeoutHasBothABottomAndACeilingSoThereIsNoValueMeaningNever(int minutes) {
        // ⚠ THE CEILING IS THE POINT AND IT IS UNUSUAL — most numeric properties in this tree have a
        // maximum to stop a tenant asking for something the cluster cannot give. This one has a
        // maximum so that "never idle out" is UNREPRESENTABLE. A tenant who could set it to zero, or
        // to a year, would have a terminal they closed a week ago that is still costing them, which is
        // the exact failure docs/plan/19 § The pod calls the design constraint.
        var property = CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == CloudConsoles.IdleTimeoutMinutesPointer);

        property.Minimum.ShouldBe(5);
        property.Maximum.ShouldBe(120);
        minutes.ShouldBeInRange(5, 120);

        var cap = CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == CloudConsoles.MaxDurationHoursPointer);

        cap.Minimum.ShouldBe(1);
        cap.Maximum.ShouldBe(24);
    }

    [Fact]
    public void ALoweredIdleTimeoutAndCapReachThePodRatherThanOnlyTheBody() {
        // The half a schema test cannot make: a number the tenant lowered has to arrive on the object,
        // or the API accepted a setting nothing acts on.
        var body = JsonNode.Parse(CloudConsoles.Body(Cluster))!.AsObject();
        body["properties"]!["session"]!["idleTimeoutMinutes"] = 5;
        body["properties"]!["session"]!["maxDurationHours"] = 1;

        using var desired = JsonDocument.Parse(body.ToJsonString());
        var pod = JsonNode.Parse(CloudConsoles.PodJson("plain", desired.RootElement))!.AsObject();

        pod["metadata"]!["annotations"]![CloudConsoles.IdleTimeoutAnnotation]!.GetValue<string>().ShouldBe("300");
        pod["spec"]!["activeDeadlineSeconds"]!.GetValue<int>().ShouldBe(3600);
    }

    // ── Auditing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecordingIsOffByDefaultAndIsVisibleFromInsideTheCluster() {
        // docs/plan/19 § Auditing: command content "is not recorded by default — it is a shell, it
        // contains secrets, and a keystroke log is a liability".
        Pod()["metadata"]!["annotations"]![CloudConsoles.RecordingAnnotation]!.GetValue<string>()
            .ShouldBe("false");

        using var recorded = JsonDocument.Parse(CloudConsoles.Body(Cluster, recording: true));

        JsonNode.Parse(CloudConsoles.PodJson("plain", recorded.RootElement))!
            ["metadata"]!["annotations"]![CloudConsoles.RecordingAnnotation]!.GetValue<string>()
            .ShouldBe("true");
    }

    [Fact]
    public void RecordingIsImmutableSoARecordedConsoleCannotBeQuietlyUnrecorded() {
        // ⚠ THE STRONGEST THING EXPRESSIBLE TODAY, AND IT IS NOT WHAT docs/plan/19 ASKS FOR. That
        // document makes the opt-in per SUBSCRIPTION; the resource model has no per-subscription
        // policy surface, so it is per console — and an audit control the audited party can decline is
        // not an audit control. What immutability buys is that a console created under a compliance
        // requirement stays recorded for its whole life.
        // conformance.yaml § owed, `recording-is-per-console`.
        CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/audit/sessionRecording")
            .Immutable
            .ShouldBeTrue();

        // And the identity is immutable for the matching reason: an audit trail naming a principal the
        // shell no longer runs as is worse than none.
        CloudConsoles.Schema2026.Properties
            .Single(x => x.JsonPointer == CloudConsoles.PrincipalIdPointer)
            .Immutable
            .ShouldBeTrue();
    }

    [Fact]
    public void TheServiceAccountSaysWhoseShellThisIs() {
        // The in-cluster half of docs/plan/19 § Auditing's "who": an operator holding kubectl and
        // nothing else can tell whose shell a pod is without resolving a resource id through an API
        // they may not have.
        JsonNode.Parse(CloudConsoles.ServiceAccountJson("plain", Desired))!
            ["metadata"]!["annotations"]![CloudConsoles.PrincipalAnnotation]!.GetValue<string>()
            .ShouldBe(Principal.ToString("D"));
    }

    // ── The rest of the pod ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOnlyWritablePlacesAreHomeAndTmpAndBothAreBounded() {
        // ⚠ readOnlyRootFilesystem makes /tmp unwritable without a volume, which breaks almost every
        // tool in the image; and an unbounded emptyDir there is the node-filling failure the ephemeral
        // limit exists to stop. Both halves, or neither works.
        var pod = Pod();

        var mounts = pod["spec"]!["containers"]!.AsArray()[0]!["volumeMounts"]!.AsArray()
            .Select(x => x!["mountPath"]!.GetValue<string>())
            .ToList();

        mounts.ShouldBe([CloudConsoles.HomePath, "/tmp"]);

        var limits = pod["spec"]!["containers"]!.AsArray()[0]!["resources"]!["limits"]!;
        limits["ephemeral-storage"]!.GetValue<string>().ShouldBe(CloudConsoles.EphemeralStorageLimit);

        pod["spec"]!["volumes"]!.AsArray()[0]!["persistentVolumeClaim"]!["claimName"]!.GetValue<string>()
            .ShouldBe(CloudConsoles.HomeClaimName("plain"));
    }

    [Fact]
    public void TheImageIsPinnedByDigestAndIsNeverAName() {
        // docs/plan/18 § Platform security: "A pinned digest, never a tag." A shell image resolved by
        // tag would let a registry change what every tenant's terminal is, silently, between two
        // attaches of one session.
        var image = Pod()["spec"]!["containers"]!.AsArray()[0]!["image"]!.GetValue<string>();

        image.ShouldContain("@sha256:");
        image.ShouldNotContain(":latest");
        image.Split('@')[0].ShouldBe(CloudConsoles.ImageRepository);
    }

    [Fact]
    public void ThereIsNoCommandProperty() {
        // ⚠ AN ABSENCE ASSERTED, BECAUSE IT IS THE ONE A REVIEWER WOULD ASK FOR AND IT MUST NOT BE
        // ADDED. A `command` property on a resource whose pod holds a managed identity turns a
        // terminal into an unattended job runner with that identity — which is a different product,
        // with a different authorization story, and nothing in this row's design would cover it.
        CloudConsoles.Schema2026.Declares("/properties/command").ShouldBeFalse();
        CloudConsoles.Schema2026.Declares("/properties/image/repository").ShouldBeFalse();
        CloudConsoles.Schema2026.Declares("/properties/image/digest").ShouldBeFalse();

        Pod()["spec"]!["containers"]!.AsArray()[0]!["command"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ShouldBe(["/bin/bash", "-l"]);
    }

    [Fact]
    public void TheShellGetsAStdinAndATtyOrItIsNotATerminal() {
        // Without both, `kubectl exec -it` into this pod attaches to a process with no controlling
        // terminal: no line editing, no job control, no `vim`. It is the one pair on this object that
        // is about the product rather than about safety.
        var container = Pod()["spec"]!["containers"]!.AsArray()[0]!;

        container["stdin"]!.GetValue<bool>().ShouldBeTrue();
        container["tty"]!.GetValue<bool>().ShouldBeTrue();
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid Cluster = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Principal = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");

    static readonly JsonDocument Plain = JsonDocument.Parse(CloudConsoles.Body(Cluster, principalId: Principal));

    static JsonElement Desired => Plain.RootElement;

    /// <summary>The pod a body that asks for nothing renders.</summary>
    static JsonObject Pod() => JsonNode.Parse(CloudConsoles.PodJson("plain", Desired))!.AsObject();

    /// <summary>The policy a body that asks for nothing renders.</summary>
    static JsonObject Policy() =>
        JsonNode.Parse(CloudConsoles.NetworkPolicyJson("plain", Tenant, "plain-ns", Desired))!.AsObject();
}
