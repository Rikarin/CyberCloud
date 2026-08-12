using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     <c>Matches</c> is containment, and on this family the reason is the Kube-OVN <b>controller</b>
///     rather than a CRD default or a mutating webhook.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>EVERY OBJECT IN THIS FILE IS HAND-WRITTEN, AND THAT IS FORCED RATHER THAN LAZY.</b>
///         <c>ClusterConformanceHarness</c> derives a CRD stub from the case's own <c>Objects</c> with
///         an <c>x-kubernetes-preserve-unknown-fields</c> schema — <b>no defaults</b> — and the k3s it
///         starts runs <b>no Kube-OVN controller</b>. So the read-back in every cluster-backed
///         assertion is a faithful echo of the apply, and an equality comparison passes there. That is
///         the exact hole <c>CyberCloud.Providers.Search</c> measured: the equality mistake was run
///         against both halves and the conformance suite was <b>27 of 27 green</b> while one
///         hand-written unit test was the only red thing in the tree. This file is that test for this
///         family, and it is bigger because this substrate rewrites more.
///     </para>
///     <para>
///         ⚠ <b>THE USUAL ARGUMENT FOR CONTAINMENT IS FALSE HERE AND THE OBJECTS BELOW ARE WHY IT IS
///         STILL MANDATORY.</b> Checked in <c>charts/kube-ovn/templates/kube-ovn-crd.yaml</c> and the
///         Go types rather than in a README: across <c>Vpc</c>, <c>Subnet</c>, <c>SecurityGroup</c>,
///         <c>IptablesEIP</c> and <c>OvnEip</c> there is exactly <b>one</b>
///         <c>+kubebuilder:default</c> — <c>Vpc.spec.bfdPort.enabled=false</c> — and no
///         <c>MutatingWebhookConfiguration</c> anywhere in the project. What rewrites the spec is
///         <c>pkg/controller/subnet.go</c>'s <c>formatSubnet</c> and <c>pkg/controller/vpc.go</c>'s
///         <c>formatVpc</c>, both of which issue a full <c>Update</c> against an object this provider
///         applied.
///     </para>
/// </remarks>
public sealed class NetworkMatchesTests {
    static readonly Guid Cluster = Guid.Parse("cccccccc-0000-4000-8000-000000000001");

    // ── The Vpc ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVpcCarryingTheCrdsOwnDefaultStillMatches() {
        // ⚠ THE ONE +kubebuilder:default IN THE WHOLE KUBE-OVN CRD SET, AND THE HARNESS WILL NEVER
        // PRODUCE IT. `Vpc.spec.bfdPort.enabled` is `+kubebuilder:default=false`, so a real API server
        // writes a `bfdPort` block onto an object this provider never sent one for, on the FIRST
        // create. The derived stub's schema has no defaults, so the cluster-backed suite echoes the
        // apply and an equality comparison passes there and fails in production.
        using var desired = JsonDocument.Parse(VirtualNetworks.Body(Cluster));

        var applied = VirtualNetworks.VpcJson("ns", "net", desired.RootElement);
        var readBack = WithSpec(applied, spec => spec["bfdPort"] = new JsonObject { ["enabled"] = false });

        VirtualNetworks.Matches(readBack, desired.RootElement).ShouldBeTrue(
            "the CRD's own default came back on the object and Matches read it as drift, so the "
            + "reconciler will answer InProgress on every pass for the life of the resource"
        );

        // And the equality mistake, made explicit, so the assertion above cannot be mistaken for a
        // tautology: the two documents are genuinely different strings.
        readBack.ShouldNotBe(applied);
    }

    [Fact]
    public void AVpcCarryingTheControllersOwnWriteBackStillMatches() {
        // ⚠ formatVpc adds a FINALIZER and fills staticRoutes[].policy, then issues a full
        // Vpcs().Update(...). handleDeleteVpcStaticRoute removes entries from spec.staticRoutes
        // outright. None of that is this provider's, and all of it lands on this provider's object.
        using var desired = JsonDocument.Parse(VirtualNetworks.Body(Cluster));

        var applied = VirtualNetworks.VpcJson("ns", "net", desired.RootElement);

        var readBack = WithSpec(
            applied,
            spec => {
                spec["namespaces"] = new JsonArray();
                spec["staticRoutes"] = new JsonArray(
                    new JsonObject {
                        ["cidr"] = "0.0.0.0/0", ["nextHopIP"] = "10.0.0.1", ["policy"] = "policyDst"
                    }
                );
            }
        );

        VirtualNetworks.Matches(readBack, desired.RootElement).ShouldBeTrue(
            "the Kube-OVN controller's own spec write-back was read as drift"
        );
    }

    [Fact]
    public void AVpcWhoseExternalFlagWasChangedDoesNotMatch() {
        // ⚠ CONTAINMENT MUST STILL REFUSE A REAL CHANGE. A Matches that returned true for everything
        // would pass every test above and detect no drift at all, which is the failure mode
        // "containment" is one careless edit away from.
        using var desired = JsonDocument.Parse(VirtualNetworks.Body(Cluster));

        var applied = VirtualNetworks.VpcJson("ns", "net", desired.RootElement);
        var tampered = WithSpec(applied, spec => spec["enableExternal"] = true);

        VirtualNetworks.Matches(tampered, desired.RootElement).ShouldBeFalse(
            "somebody attached this tenant's router to the external network and the reconciler did "
            + "not notice"
        );
    }

    [Fact]
    public void ADocumentOfAnotherKindNeverMatches() {
        using var desired = JsonDocument.Parse(VirtualNetworks.Body(Cluster));

        VirtualNetworks.Matches(
            """{"kind":"Subnet","spec":{"enableExternal":false}}""",
            desired.RootElement
        ).ShouldBeFalse();

        VirtualNetworks.Matches("not json at all", desired.RootElement).ShouldBeFalse();
        VirtualNetworks.Matches("{}", desired.RootElement).ShouldBeFalse();
    }

    // ── The Subnet, where the controller does the most ───────────────────────────────────────────

    [Fact]
    public void ASubnetWhoseCidrTheControllerCanonicalizedStillMatches() {
        // ⚠ THE SINGLE MOST LIKELY BUG IN THIS FAMILY, AND IT IS INVISIBLE TO EVERY SUITE WE HAVE.
        // pkg/controller/subnet.go's formatCIDR splits spec.cidrBlock on ',', runs each element
        // through net.ParseCIDR and writes back ipNet.String(). So a tenant who asks for
        // 10.20.5.7/24 has 10.20.5.0/24 stored ON THE OBJECT, BY THE CONTROLLER, one pass later.
        //
        // A string comparison here reports drift on a perfectly converged subnet FOREVER: the
        // reconciler answers InProgress every pass, the resource never reaches Succeeded, and the
        // message says "does not yet carry the desired spec" while the cluster is exactly right.
        using var desired = JsonDocument.Parse(
            NetworkSubnets.Body(Cluster, prefixV4: "10.20.5.7/24")
        );

        var applied = NetworkSubnets.SubnetJson("ns", Address("web", "net"), desired.RootElement);

        // What this provider sent, verbatim — host bits and all.
        Spec(applied)["cidrBlock"]!.GetValue<string>().ShouldBe("10.20.5.7/24");

        var readBack = WithSpec(applied, spec => spec["cidrBlock"] = "10.20.5.0/24");

        NetworkSubnets.MatchesBody(readBack, desired.RootElement).ShouldBeTrue(
            "the controller canonicalized the CIDR and Matches compared strings, so this subnet will "
            + "never report Succeeded"
        );
    }

    [Fact]
    public void ASubnetCarryingEverythingElseTheControllerWritesStillMatches() {
        // ⚠ formatSubnet fills provider, vpc, gatewayType and enableLb when empty; formatGateway
        // derives spec.gateway from the CIDR; formatExcludeIPs APPENDS every gateway IP and
        // sort.Strings() the result; and spec.protocol is recomputed from the CIDR unconditionally.
        // This provider sends none of them — see NetworkSubnets.SubnetJson — and all of them come
        // back.
        using var desired = JsonDocument.Parse(NetworkSubnets.Body(Cluster));

        var applied = NetworkSubnets.SubnetJson("ns", Address("web", "net"), desired.RootElement);

        var readBack = WithSpec(
            applied,
            spec => {
                spec["protocol"] = "IPv4";
                spec["provider"] = "ovn";
                spec["gateway"] = "10.20.1.1";
                spec["gatewayType"] = "distributed";
                spec["enableLb"] = true;
                spec["excludeIps"] = new JsonArray("10.20.1.1");
                spec["default"] = false;
            }
        );

        NetworkSubnets.MatchesBody(readBack, desired.RootElement).ShouldBeTrue(
            "the controller's own fields were read as drift"
        );
    }

    [Fact]
    public void ADualStackSubnetSurvivesCanonicalizationOfBothFamilies() {
        using var desired = JsonDocument.Parse(
            NetworkSubnets.Body(Cluster, prefixV4: "10.20.1.0/24", prefixV6: "fd00:20:1::/64")
        );

        var applied = NetworkSubnets.SubnetJson("ns", Address("web", "net"), desired.RootElement);

        // ⚠ IPv4 FIRST, which util.CheckProtocol depends on: it returns Dual only when the two
        // elements parse as one v4 and one v6, and every other dual-stack field in Kube-OVN follows
        // the same order.
        Spec(applied)["cidrBlock"]!.GetValue<string>().ShouldBe("10.20.1.0/24,fd00:20:1::/64");

        var readBack = WithSpec(applied, spec => spec["cidrBlock"] = "10.20.1.0/24,fd00:20:1:0::/64");

        NetworkSubnets.MatchesBody(readBack, desired.RootElement).ShouldBeTrue(
            "the v6 half was re-spelled by the controller and the comparison did not survive it"
        );
    }

    [Fact]
    public void ASubnetWhoseCidrIsGenuinelyDifferentDoesNotMatch() {
        using var desired = JsonDocument.Parse(NetworkSubnets.Body(Cluster));

        var applied = NetworkSubnets.SubnetJson("ns", Address("web", "net"), desired.RootElement);
        var tampered = WithSpec(applied, spec => spec["cidrBlock"] = "10.99.9.0/24");

        NetworkSubnets.MatchesBody(tampered, desired.RootElement).ShouldBeFalse(
            "the parsed-network comparison has become an accept-everything comparison"
        );
    }

    [Fact]
    public void ASubnetThatLostItsSecondFamilyDoesNotMatch() {
        // ⚠ The comparison is element-wise over a comma-separated list, so a dual-stack subnet that
        // came back single-stack is drift rather than a shorter spelling of the same thing.
        using var desired = JsonDocument.Parse(
            NetworkSubnets.Body(Cluster, prefixV6: "fd00:20:1::/64")
        );

        var applied = NetworkSubnets.SubnetJson("ns", Address("web", "net"), desired.RootElement);
        var tampered = WithSpec(applied, spec => spec["cidrBlock"] = "10.20.1.0/24");

        NetworkSubnets.MatchesBody(tampered, desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void ASubnetBoundToAnotherNetworksVpcDoesNotMatch() {
        // ⚠ THE FIELD THIS TYPE MOST NEEDS COMPARED, AND THE ONE THE SHARED SUITE CANNOT SEE.
        // `spec.vpc` is derived from the ADDRESS, so this comparison cannot be satisfied by a body
        // that agrees with itself. A Subnet whose vpc was rewritten is a range being handed out
        // inside a different tenant's routing domain, under this tenant's resource id.
        using var desired = JsonDocument.Parse(NetworkSubnets.Body(Cluster));

        var id = Address("web", "net");
        var applied = NetworkSubnets.SubnetJson("ns", id, desired.RootElement);
        var tampered = WithSpec(applied, spec => spec["vpc"] = "ns-someone-elses-network");

        NetworkSubnets.MatchesBody(tampered, desired.RootElement).ShouldBeTrue(
            "the body half cannot see the address — that is the documented limit of MatchesBody"
        );

        NetworkSubnets.Matches(tampered, "ns", id, desired.RootElement).ShouldBeFalse(
            "the full Matches CAN see the address and must refuse a rebound subnet"
        );
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static ResourceId Address(string name, string network) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            NetworkSubnets.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            network
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();

    /// <summary>The applied document with its <c>spec</c> edited, as an API server would return it.</summary>
    static string WithSpec(string objectJson, Action<JsonObject> edit) {
        var document = JsonNode.Parse(objectJson)!.AsObject();
        edit(document["spec"]!.AsObject());
        return document.ToJsonString();
    }
}
