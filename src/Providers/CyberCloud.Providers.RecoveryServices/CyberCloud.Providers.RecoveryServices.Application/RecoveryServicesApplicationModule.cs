using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconciler this module
// registers; the gateway builds the SAME registry from the SAME providers, because RouteStage
// resolves a path against it and a gateway with its own list would route from a description of a
// platform the silo is not running.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.RecoveryServices.Application;

/// <summary>
///     The backup-vault provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     docs/plan/03 § Providers:
///     <i>
///         "Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."
///     </i> Both halves happen here — a host
///     that <c>[DependsOn]</c> this module gets the provider, its reconciler and its two handlers,
///     and there is no second call it can forget.
///     <para>
///         ⚠ Nothing here registers the PostgreSQL provider the vault protects. The vault reaches a
///         server through the resource manager's view, which resolves the server's type from the
///         host's registry — a host that loads this module and not the PostgreSQL one has a vault
///         whose every item is refused as <c>ResourceNotFound</c>, which is the right answer for a
///         type that host does not serve.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class RecoveryServicesApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new RecoveryServicesProvider());
    }
}
