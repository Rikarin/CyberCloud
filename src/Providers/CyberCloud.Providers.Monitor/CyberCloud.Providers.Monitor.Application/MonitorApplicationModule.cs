using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconcilers and action
// handlers this module registers; the gateway builds the SAME registry from the SAME providers,
// because RouteStage resolves a path against it and a gateway with its own list would route from a
// description of a platform the silo is not running. OwningHostAttribute allows multiple for exactly
// this — rule 5 lets the gateway reference a provider's .Application assembly, and rule 4 wants each
// host that does named in one reviewable line.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Monitor.Application;

/// <summary>
///     The managed-observability provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> Both halves happen here —
///         a host that <c>[DependsOn]</c> this module gets the provider, its reconciler and its
///         action handler, and there is no second call it can forget.
///     </para>
///     <para>
///         ⚠ <b>This module is one of two in the tree whose registration actually carries a
///         handler</b>, and that makes the <i>single</i> <c>AddCyberCloudProvider</c> call load
///         bearing in a way the other ten families cannot demonstrate. <c>DiscoveringProviderBuilder</c>
///         walks what <c>Describe</c> declared and registers both the reconciler type and every
///         <c>ActionRegistration.HandlerType</c> as singletons by concrete type. A host that loaded
///         this module and then wired the handler itself would have two registrations of one type;
///         a host that loaded neither would serve a <c>listKeys</c> that answers <c>202</c> and
///         re-runs the reconciler, which is what every declared action in the tree did until the
///         handler seam existed.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MonitorApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new MonitorProvider());
    }
}
