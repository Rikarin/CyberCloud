using CyberCloud.Providers.Compute.Contracts;
using CyberCloud.Providers.Network.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerInstance.Tests;

/// <summary>
///     The three rules this family spells a second time because rule 2 forbids the reference that would
///     spell them once — held to the families that own them.
/// </summary>
/// <remarks>
///     ⚠ docs/plan/03 § Assembly graph rules, rule 2, is over the SHIPPED graph and a test project is
///     outside it, which is what lets this class name <c>CyberCloud.Providers.Network.Contracts</c> and
///     <c>CyberCloud.Providers.Compute.Contracts</c> beside this family's own — the shape
///     <c>ComputeNetworkJoinTests</c> established. A drift in any of the three is a container group that
///     joins no subnet, translates no address, or reads another tenant's vault.
/// </remarks>
public sealed class ContainerGroupSpellingTests {
    [Fact]
    public void TheTenantVaultPrefixIsTheVirtualMachinesOwn() {
        foreach (var tenant in new[] { Groups.TenantA, Groups.TenantB, Guid.Empty }) {
            ContainerGroups.TenantVaultPrefix(tenant).ShouldBe(VirtualMachines.TenantVaultPrefix(tenant));
        }
    }

    [Fact]
    public void TheSubnetJoinIsTheSubnetsOwnObjectName() {
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = JsonDocument.Parse(ContainerGroups.Body(Groups.ClusterId, virtualNetwork: "vnet", subnet: "apps"));

        ContainerGroups.LogicalSwitchOf(ns, body.RootElement).ShouldBe(NetworkSubnets.ObjectNameOf(ns, "vnet", "apps"));
        ContainerGroups.LogicalSwitchAnnotation.ShouldBe(VirtualMachines.LogicalSwitchAnnotation);
    }

    [Fact]
    public void ThePublicAddressIsTheAddressTypesOwnOvnEipName() {
        var address = Groups.Address("web");
        var ns = ReconcileDriver.NamespaceFor(address);
        using var body = JsonDocument.Parse(
            ContainerGroups.Body(Groups.ClusterId, virtualNetwork: "vnet", subnet: "apps", publicIpAddress: "front")
        );

        ContainerGroups.OvnEipOf(ns, body.RootElement).ShouldBe(PublicIpAddresses.ObjectNameOf(ns, "front"));
        ContainerGroups.OvnFipKind.Group.ShouldBe(PublicIpAddresses.OvnEipKind.Group);
    }
}
