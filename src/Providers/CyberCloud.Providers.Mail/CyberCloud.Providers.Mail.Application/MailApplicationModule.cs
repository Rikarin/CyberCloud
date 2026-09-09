using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconcilers this module
// registers; the gateway builds the SAME registry from the SAME providers, because RouteStage
// resolves a path against it and a gateway with its own list would route from a description of a
// platform the silo is not running.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Mail.Application;

/// <summary>
///     The managed-mail provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///     resource types into <c>CyberCloud.ResourceManager</c>."</i> Both halves happen here — a host
///     that <c>[DependsOn]</c> this module gets the provider and its reconciler, and there is no
///     second call it can forget.
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MailApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new MailProvider());
    }
}
