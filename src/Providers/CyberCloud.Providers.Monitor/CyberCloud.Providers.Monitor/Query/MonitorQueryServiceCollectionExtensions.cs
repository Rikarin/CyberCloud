using CyberCloud.Providers.Monitor.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Providers.Monitor.Query;

/// <summary>Wires the two stores the query handlers hold, and the account ledger they check.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gateway is the host that needs them, and both hosts get them.</b> A synchronous
///         action runs inside <c>ResourceManagerService</c>, which in production is the gateway's
///         process — the same reason <c>AddCyberCloudMonitorAlerting</c> is called in both — so a
///         <c>queryMetrics</c> reaches vmselect from the gateway pod, and that pod is the one whose
///         configuration names the stores.
///     </para>
///     <para>
///         ⚠ <b>Each half is independent.</b> A region with VictoriaMetrics and no ClickHouse serves
///         the metrics explorer and refuses log search by name, and the reverse; the refusing store
///         stands in for whichever half is not configured.
///     </para>
/// </remarks>
public static class MonitorQueryServiceCollectionExtensions {
    /// <summary>Registers the configured stores, or the refusing one for a half with no endpoint.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The bound <see cref="MonitorQueryOptions.SectionName" />.</param>
    /// <exception cref="ArgumentException">A configured endpoint is malformed — thrown at composition, so the pod does not start.</exception>
    public static IServiceCollection AddCyberCloudMonitorQuery(this IServiceCollection services, MonitorQueryOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        if (options.IsMetricsConfigured) {
            services.TryAddSingleton<IMonitorMetricsStore>(provider => new VictoriaMetricsQueryStore(
                    new HttpClient { Timeout = options.QueryTimeout + TimeSpan.FromSeconds(5) },
                    options,
                    provider.GetRequiredService<ILogger<VictoriaMetricsQueryStore>>()
                )
            );
        }

        if (options.IsLogsConfigured) {
            services.TryAddSingleton<IMonitorLogStore>(provider => new ClickHouseLogStore(
                    new HttpClient { Timeout = options.QueryTimeout + TimeSpan.FromSeconds(5) },
                    options,
                    provider.GetRequiredService<ILogger<ClickHouseLogStore>>()
                )
            );
        }

        services.TryAddSingleton<IMonitorMetricsStore, UnavailableMonitorQueryStore>();
        services.TryAddSingleton<IMonitorLogStore, UnavailableMonitorQueryStore>();

        // ⚠ THE ACCOUNT LEDGER RIDES HERE, AND BOTH ITS CALLERS NEED IT. The workspace reconciler
        // claims the accountID in the silo and the metrics handlers check the claim in the gateway;
        // this is the one registration both hosts and every harness already make for this family.
        services.TryAddSingleton<IMonitorAccounts, GrainMonitorAccounts>();

        return services;
    }
}
