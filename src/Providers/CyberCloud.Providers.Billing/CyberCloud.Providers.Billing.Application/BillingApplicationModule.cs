using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// Both hosts, for the reason every family gives: the silo reconciles, and the gateway routes from a
// registry built from the same providers.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Billing.Application;

/// <summary>The cost-management provider's ABP module — what a host <c>[DependsOn]</c> to load it.</summary>
/// <remarks>
///     ⚠ The budget reconciler takes <c>IBudgetControlPlane</c>, which this module cannot register —
///     see this project's <c>.csproj</c>.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class BillingApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new BillingProvider());
    }
}
