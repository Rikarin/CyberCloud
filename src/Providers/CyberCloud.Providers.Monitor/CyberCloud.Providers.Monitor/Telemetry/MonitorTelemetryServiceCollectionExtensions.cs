using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Providers.Monitor.Telemetry;

/// <summary>Wires the component views' store into a container.</summary>
/// <remarks>
///     ⚠ <b>Both hosts, and the gateway is the one that queries.</b> A view is a synchronous action,
///     so it runs inside <c>ResourceManagerService</c> in the gateway's process and the gateway is
///     where ClickHouse is reached from. The silo registers it too because <c>MonitorApplicationModule</c>
///     is one list for both, which is the argument <c>AddCyberCloudMonitorAlerting</c> makes.
/// </remarks>
public static class MonitorTelemetryServiceCollectionExtensions {
    /// <summary>
    ///     Registers <see cref="ITelemetryStore" />: over ClickHouse when <paramref name="configuration" />
    ///     sets <see cref="MonitorTelemetryOptions.SectionName" />'s endpoint, the refusing default
    ///     otherwise.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">The host's configuration, or <see langword="null" /> for none.</param>
    /// <exception cref="ArgumentException">The section names an endpoint that is not usable — see <see cref="ClickHouseTelemetryStore.ValidatedEndpoint" />.</exception>
    /// <remarks>
    ///     ⚠ <c>TryAdd</c>, so a host — or a test — that registered a store first keeps it; and the
    ///     endpoint is validated here, at start, so a plain-HTTP endpoint in a region is a host that
    ///     does not start rather than a view that fails at the first request.
    /// </remarks>
    public static IServiceCollection AddCyberCloudMonitorTelemetry(
        this IServiceCollection services,
        IConfiguration? configuration
    ) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MonitorTelemetryOptions();
        configuration?.GetSection(MonitorTelemetryOptions.SectionName).Bind(options);

        if (!options.IsConfigured) {
            services.TryAddSingleton<ITelemetryStore, UnavailableTelemetryStore>();
            return services;
        }

        _ = ClickHouseTelemetryStore.ValidatedEndpoint(options);

        services.TryAddSingleton<ITelemetryStore>(provider => new ClickHouseTelemetryStore(
                // The client's own timeout sits past the server's, so a statement ClickHouse cancels
                // at its budget comes back as ClickHouse's refusal rather than as this side giving up.
                new HttpClient { Timeout = options.QueryTimeout + TimeSpan.FromSeconds(5) },
                options,
                provider.GetService<ILogger<ClickHouseTelemetryStore>>()
            )
        );

        return services;
    }
}
