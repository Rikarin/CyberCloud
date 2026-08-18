using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager;
using Volo.Abp.Application;
using Volo.Abp.Modularity;

// ── Rule 4's declaration — docs/plan/03 § Assembly graph rules ────────────────────────────────
//
// ⚠ TWO HOSTS, AND THE SECOND ONE IS NOT A CONCESSION. The silo runs the reconciler this module
// registers; the gateway builds the SAME registry from the SAME providers, because RouteStage
// resolves a path against it and a gateway with its own list would route from a description of a
// platform the silo is not running. OwningHostAttribute allows multiple for exactly this — rule 5
// lets the gateway reference a provider's .Application assembly, and rule 4 wants each host that
// does named in one reviewable line.
//
// ⚠ ON THIS ROW THE GATEWAY'S COPY DOES MORE THAN ROUTE. `/hubs/terminal` is a gateway endpoint,
// and the connect action a client calls before opening it is dispatched from the gateway's own
// registry — so a Terminal provider present in the silo and absent from the gateway would be a
// console that converges and cannot be attached to, with a canonical 404 and nothing in the log.
// HostCompositionTests.TheSiloAndTheGatewayAgreeAboutWhatExists is what makes that unbuildable.
[assembly: OwningHost("CyberCloud.Silo.Host")]
[assembly: OwningHost("CyberCloud.Gateway.Host")]

namespace CyberCloud.Providers.Terminal.Application;

/// <summary>
///     The cloud-terminal provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT REGISTERS A PROVIDER AND NOTHING ELSE, AND THIS IS THE FIRST FAMILY WHOSE
///         <c>Describe</c> DECLARES BOTH A RECONCILER AND A HANDLER.</b>
///         <c>AddCyberCloudProvider</c> runs <c>Describe</c> against
///         <c>DiscoveringProviderBuilder</c> and registers each reconciler type and each handler type
///         as a singleton by concrete type. Eleven families exercised the first half of that walk;
///         this one exercises the second, twice, through one handler serving two actions.
///     </para>
///     <para>
///         ⚠ <b>NO APPLICATION SERVICE, INCLUDING FOR THE TERMINAL ITSELF.</b> The obvious place for
///         "carry bytes between a browser and a pod" is a bespoke controller here. It is not here:
///         docs/plan/10 § SignalR routes the terminal to <c>/hubs/terminal</c>, which the gateway
///         maps directly, and a hub is not routed from the provider registry. What this module
///         contributes to the terminal's data plane is the <c>connect</c> action that tells a client
///         which session to name on that hub, and nothing more.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class TerminalApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new TerminalProvider());
    }
}
