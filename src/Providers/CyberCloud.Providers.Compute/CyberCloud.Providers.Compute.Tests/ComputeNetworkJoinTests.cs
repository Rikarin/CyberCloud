using CyberCloud.Providers.Network.Contracts;
using System.Text.Json;

namespace CyberCloud.Providers.Compute.Tests;

/// <summary>
///     The one fact this family spells a second time — a subnet's object name — held to the Network
///     family's own spelling of it.
/// </summary>
/// <remarks>
///     ⚠ <b>The first test in the tree to cross a provider family boundary, and the reason is the
///     rule that forbids the shipped assemblies from crossing it.</b> docs/plan/03 § Assembly graph
///     rules, rule 2, keeps <c>CyberCloud.Providers.Compute</c> off
///     <c>CyberCloud.Providers.Network.Contracts</c>, so <see cref="VirtualMachines.LogicalSwitchOf" />
///     has to spell <c>{namespace}-{network}-{subnet}</c> itself. A test project is outside the rule
///     — <c>Build.Architecture</c> reads shipping assemblies — and is the only place the two spellings
///     can be put side by side. <c>charts/managed/kube-ovn-vpc/conformance.yaml § owed</c>,
///     <c>nothing-can-join-a-network-yet</c>, predicted this consumer and this route.
/// </remarks>
public sealed class ComputeNetworkJoinTests {
    [Fact]
    public void TheMachinesLogicalSwitchIsTheSubnetsOwnObjectName() {
        var ns = "11111111111141118111111111111111-prod";
        using var body = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, virtualNetwork: "vnet", subnet: "web"));

        VirtualMachines.LogicalSwitchOf(ns, body.RootElement).ShouldBe(
            NetworkSubnets.ObjectNameOf(ns, "vnet", "web"),
            "the machine would put its interface on a switch the Network family does not render"
        );

        // And through the subnet's own address, which is the rule the Network family applies to itself.
        var subnet = new ResourceId(Compute.TenantA, Compute.SubscriptionA, "prod", NetworkSubnets.Type, "web", Guid.NewGuid(), "vnet");
        VirtualMachines.LogicalSwitchOf(ns, body.RootElement).ShouldBe(NetworkSubnets.ObjectNameOf(ns, subnet));

        VirtualMachines.LogicalSwitchAnnotation.ShouldBe(LoadBalancers.LogicalSwitchAnnotation, "the fabric reads one annotation key, and a proxy pod and a machine must use the same one");
    }

    [Fact]
    public void HalfAJoinIsThePodNetwork() {
        using var networkOnly = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, virtualNetwork: "vnet"));
        using var subnetOnly = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId, subnet: "web"));
        using var neither = JsonDocument.Parse(VirtualMachines.Body(Compute.ClusterId));

        VirtualMachines.LogicalSwitchOf("ns", networkOnly.RootElement).ShouldBeEmpty();
        VirtualMachines.LogicalSwitchOf("ns", subnetOnly.RootElement).ShouldBeEmpty();
        VirtualMachines.LogicalSwitchOf("ns", neither.RootElement).ShouldBeEmpty();

        VirtualMachines.VirtualMachineJson("ns", "web", neither.RootElement, VirtualMachines.RunAlways)
            .ShouldNotContain(VirtualMachines.LogicalSwitchAnnotation);
    }
}
