using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND ON THIS FAMILY THE GATEWAY IS NOT ONLY ROUTING. Every other provider's actions
// that run on the request path read a cluster or a vault; four of this family's read a GRAIN, so the
// gateway does not just build the same registry the silo does — it also holds the module's two
// client-side seams. GatewayServiceCollectionExtensions registers them.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Communication.Application;

/// <summary>
///     The sending product's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     docs/plan/03 § Providers:
///     <i>
///         "Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."
///     </i> Both halves happen here — a host
///     that <c>[DependsOn]</c> this module gets the provider, its four reconcilers and its five
///     action handlers, and there is no second call it can forget. ⚠ What it cannot register is the
///     seams those handlers hold; see this project's <c>.csproj</c>.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class CommunicationApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new CommunicationProvider());
    }
}
