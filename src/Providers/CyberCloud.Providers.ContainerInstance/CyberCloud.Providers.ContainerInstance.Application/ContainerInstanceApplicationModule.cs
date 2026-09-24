using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// Two hosts, for the reason NetworkApplicationModule gives: the gateway routes from the same
// registry the silo reconciles from, so both name this module in one reviewable line each.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.ContainerInstance.Application;

/// <summary>
///     The container-instance provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     docs/plan/03 § Providers: each provider is an ABP module that registers its resource types into
///     <c>CyberCloud.ResourceManager</c>. A host that <c>[DependsOn]</c> this module gets the provider,
///     its reconciler and its action handler, and there is no second call it can forget.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class ContainerInstanceApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new ContainerInstanceProvider());
    }
}
