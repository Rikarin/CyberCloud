using CyberCloud.Providers.Compute.Contracts;
using CyberCloud.Providers.KeyVault.Contracts;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     A key vault's root is a path no tenant can hand the platform to resolve. docs/plan/18
///     § What landed, and what is owed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here because this is a project that references both families.</b> Rule 2 keeps
///         <c>CyberCloud.Providers.Compute.Contracts</c> and <c>CyberCloud.Providers.KeyVault.Contracts</c>
///         out of each other, so neither family's own tests can see the collision. The gateway
///         composes every provider, so its test project can.
///     </para>
///     <para>
///         ⚠ <b>The attack this closes.</b> <c>VirtualMachines.ParseCloudInitRef</c> accepts any
///         handle under the tenant's own <c>tenants/{tenantId}/</c>. <c>VirtualMachineReconciler</c>
///         resolves it with the platform's one broad OpenBao token and writes the value into the
///         cloud-init Secret the guest mounts. A root under that prefix would let a Contributor with
///         no data-plane role put the vault's AES-256 root into a machine they own. With the root,
///         every secret and private key the vault seals opens without the platform vault.
///     </para>
/// </remarks>
public sealed class KeyVaultRootIsOutsideTheTenantPrefixTests {
    static readonly Guid Tenant = Guid.Parse("30303030-0000-4000-8000-0000000000e1");
    static readonly Guid Vault = Guid.Parse("30303030-0000-4000-8000-0000000000e2");

    [Fact]
    public void ACloudInitHandleNamingAVaultsRootIsRefusedBeforeItIsResolved() {
        var root = KeyVaults.RootRef(Tenant, Vault);

        var parsed = VirtualMachines.ParseCloudInitRef(root.Path + "#" + root.Field, Tenant);

        parsed.IsFailure.ShouldBeTrue($"a tenant's VM could name its own vault's root '{root.Path}' as cloud-init user data");
        parsed.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        parsed.Error.Message.ShouldContain(VirtualMachines.TenantVaultPrefix(Tenant));
    }

    [Fact]
    public void EveryVaultsRootIsUnderThePlatformPrefixAndNoTenantsOwn() {
        // ⚠ Not only this tenant's prefix: the attacker names the path, so no tenant's may hold it.
        var path = KeyVaults.RootPath(Tenant, Vault);

        path.ShouldStartWith(KeyVaults.RootPrefix, Case.Sensitive);
        path.ShouldNotStartWith("tenants/", Case.Sensitive);
        path.ShouldNotStartWith(VirtualMachines.TenantVaultPrefix(Tenant), Case.Sensitive);
    }

    [Fact]
    public void TheSameHandleUnderTheTenantsOwnPrefixIsAccepted() {
        // The refusal is the prefix's, not a blanket one: without this the first test would pass
        // against a parse that refused everything.
        var parsed = VirtualMachines.ParseCloudInitRef(VirtualMachines.TenantVaultPrefix(Tenant) + "web#userdata", Tenant);

        parsed.IsSuccess.ShouldBeTrue(parsed.Error?.Message);
    }
}
