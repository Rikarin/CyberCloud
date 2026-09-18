using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>Wires the alert evaluator's two seams into a container.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Called by <c>MonitorApplicationModule</c>, which both hosts load, and by the
///             conformance module, which the harness loads — and by nothing else.
///         </b> A synchronous
///         action runs inside <c>ResourceManagerService</c>, which in production is the gateway's
///         process, so the <c>listInstances</c> handler needs <see cref="IAlertControlPlane" /> in the
///         gateway as well as in the silo; the query seam is registered in both because
///         <c>TryAdd</c> makes the second registration free and the alternative is two methods that
///         drift.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>TryAdd</c> throughout, so a host that registers a real query seam first keeps
///             it.
///         </b> The refusing default is the contract every seam in this tree follows: a silo that
///         wired nothing gets a sentence naming what it did not wire, never a stub that answers.
///     </para>
///     <para>
///         What is <i>not</i> here: <c>IMessageSender</c>, which the evaluator grain also holds.
///         That is the sending module's to register (<c>AddCyberCloudCommunication</c> in a silo),
///         and the silo host already does — a second registration here would be this family
///         choosing the sending module's implementation, which is the coupling
///         module-layering.txt's fifth line was reviewed to exclude.
///     </para>
/// </remarks>
public static class MonitorAlertingServiceCollectionExtensions {
    /// <summary>Registers <see cref="IAlertControlPlane" /> and the refusing <see cref="IAlertQuerySeam" />.</summary>
    /// <param name="services">The container being built. Must already hold an <c>IGrainFactory</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static IServiceCollection AddCyberCloudMonitorAlerting(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAlertControlPlane, GrainAlertControlPlane>();
        services.TryAddSingleton<IAlertQuerySeam, UnavailableAlertQuerySeam>();

        return services;
    }
}
