using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, for every family's reason: the silo drives a deployment's operation and the gateway
// routes its PUT and its what-if from the same registry. A deployment type the silo knew and the
// gateway did not would be a what-if route that answers the canonical 404.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Resources.Application;

/// <summary>
///     The <c>CyberCloud.Resources</c> provider's ABP module — what a host <c>[DependsOn]</c> to serve
///     deployments.
/// </summary>
/// <remarks>
///     ⚠ <b>It registers the declaration and nothing that runs.</b> The deployment's driver, its body
///     validator and its what-if are the resource manager's and are registered by
///     <c>AddCyberCloudResourceManager</c> in every host that composes the manager; this module is what
///     puts the type in that host's registry, which is what makes them reachable.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class ResourcesApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new ResourcesProvider());
    }
}
