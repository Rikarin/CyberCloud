using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconcilers this module
// registers; the gateway builds the SAME registry from the SAME providers, because RouteStage
// resolves a path against it and a gateway with its own list would route from a description of a
// platform the silo is not running. OwningHostAttribute allows multiple for exactly this — rule 5
// lets the gateway reference a provider's .Application assembly, and rule 4 wants each host that
// does named in one reviewable line.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.ContainerRegistry.Application;

/// <summary>
///     The managed-container-registry provider's ABP module — what a host <c>[DependsOn]</c> to load
///     it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> Both halves happen here — a host
///         that <c>[DependsOn]</c> this module gets the provider, its reconciler and its action
///         handler, and there is no second call it can forget.
///     </para>
///     <para>
///         ⚠ <b>A provider registered in one host and not the other is the failure
///         <c>HostCompositionTests</c> exists for.</b> <c>TheSiloAndTheGatewayAgreeAboutWhatExists</c>
///         fails on a difference and <c>TheSiloComposesEveryProviderModule</c> fails on an absence, so
///         the two <c>[DependsOn]</c> lists have to be edited together.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class ContainerRegistryApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new ContainerRegistryProvider());
    }
}
