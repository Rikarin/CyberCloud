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

namespace CyberCloud.Providers.Messaging.Application;

/// <summary>
///     The managed-Kafka provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> Both halves happen here — a host
///         that <c>[DependsOn]</c> this module gets the provider, its reconcilers and its action
///         handlers, and there is no second call it can forget.
///     </para>
///     <para>
///         ⚠ <b>The paragraph that used to be here said the second half could not be done, and it was
///         wrong by the time it was written.</b> It read: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;MessagingProvider&gt;()</c>, which runs before the
///         container is built, while an ABP module's <c>ConfigureServices</c> runs after — so a host
///         has to make two calls. The registry had already stopped being built at wiring time: it is a
///         factory over <c>GetServices&lt;IResourceProvider&gt;()</c>, resolved once, after everything
///         is registered. The cost of believing otherwise was not theoretical.
///         <c>AddCyberCloudProvider</c> ended up with <b>no caller anywhere in the repository</b>, so
///         no silo in <c>src/Hosts</c> served a single resource type and every conformance suite that
///         proved otherwise had built its own <c>TestCluster</c> and registered the provider itself.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MessagingApplicationModule : AbpModule {
    /// <inheritdoc />
    public override void ConfigureServices(ServiceConfigurationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Services.AddCyberCloudProvider(new MessagingProvider());
    }
}
