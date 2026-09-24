using CyberCloud.Core.Contracts;
using CyberCloud.Providers.Monitor.Alerting;
using CyberCloud.Providers.Monitor.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
///         docs/plan/03 § Providers:
///         <i>
///             "Each is an ABP module (<c>[DependsOn]</c>), each registers
///             its resource types into <c>CyberCloud.ResourceManager</c>."
///         </i> Both halves happen here —
///         a host that <c>[DependsOn]</c> this module gets the provider, its reconciler and its
///         action handler, and there is no second call it can forget.
///     </para>
///     <para>
///         ⚠
///         <b>
///             This module is one of two in the tree whose registration actually carries a
///             handler
///         </b>, and that makes the <i>single</i> <c>AddCyberCloudProvider</c> call load
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

        // ⚠ A SECOND PROVIDER FROM THE SAME MODULE, AND IT IS THE ONLY MODULE THAT LOADS TWO.
        // CyberCloud.Dashboard/grafanas lives in this family's assemblies — docs/plan/03 § Providers
        // lists `grafanas` under CyberCloud.Providers.Monitor — under the namespace docs/plan/01
        // gives it. A host that loaded this module got one namespace until #32's third noun; it gets
        // two now, and HostCompositionTests names both. DashboardProvider's remarks carry the
        // argument for the shape.
        context.Services.AddCyberCloudProvider(new DashboardProvider());

        // ⚠ THE TWO SEAMS THE ALERT RULES HOLD, IN BOTH HOSTS. AddCyberCloudProvider registers the
        // reconciler and the handler by concrete type; it cannot register what their constructors
        // ask for. MonitorAlertRuleReconciler and MonitorAlertRuleListInstancesHandler both take
        // IAlertControlPlane, and the handler runs on the request path — the gateway's process — so
        // the gateway needs it as much as the silo does. The query seam is the refusing default
        // until a host registers a real one; TryAdd keeps a real one registered first.
        context.Services.AddCyberCloudMonitorAlerting();

        // ⚠ THE EXPLORERS' STORES, FROM CONFIGURATION, IN BOTH HOSTS (#41). queryMetrics,
        // listMetricLabels and searchLogs run inside ResourceManagerService — the gateway's process —
        // and their handlers hold IMonitorMetricsStore and IMonitorLogStore. A host whose
        // CyberCloud:Monitor:Query section is empty keeps the refusing store for that half, and a
        // malformed endpoint throws here, so the pod does not start rather than failing the first query.
        var query = new MonitorQueryOptions();
        context.Services.GetConfiguration().GetSection(MonitorQueryOptions.SectionName).Bind(query);
        context.Services.AddCyberCloudMonitorQuery(query);
    }
}
