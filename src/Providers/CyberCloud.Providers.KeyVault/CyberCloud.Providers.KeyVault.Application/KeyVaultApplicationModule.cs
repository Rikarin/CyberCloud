using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND FOR THIS FAMILY THE GATEWAY IS WHERE THE DATA PLANE RUNS. The silo runs the
// reconciler and hosts KeyVaultGrain; the gateway builds the same registry to route, and — because
// ResourceManagerService runs a synchronous action's handler in the process that received the
// request — it is the gateway that runs KeyVaultActionHandler and calls the grain across the
// process boundary.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.KeyVault.Application;

/// <summary>
///     The key-vault provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     ⚠ <c>HostCompositionTests.TheSiloAndTheGatewayAgreeAboutWhatExists</c> fails when one host
///     loads this and the other does not, so the two <c>[DependsOn]</c> lists change together.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class KeyVaultApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new KeyVaultProvider());
    }
}
