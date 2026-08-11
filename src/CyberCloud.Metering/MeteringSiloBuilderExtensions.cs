using CyberCloud.Core.Time;
using CyberCloud.Metering.Sinks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Metering;

/// <summary>
///     The silo wiring for metering. docs/plan/04 § Silo composition.
/// </summary>
public static class MeteringSiloBuilderExtensions {
    /// <summary>
    ///     Registers the emitter and the defaults for every seam that has no implementation yet.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every seam gets a default and both defaults refuse.</b>
    ///         <see cref="UnavailableUsageSink" /> refuses rather than logging and succeeding;
    ///         <see cref="UnavailableMeteredResourceSource" /> refuses rather than reporting an empty
    ///         subscription. Each type's remarks carry the argument, and both come down to the same
    ///         thing: for metering the plausible default silently destroys the one artefact that
    ///         cannot be reconstructed. <c>TryAdd</c> throughout, so a host with the real thing
    ///         registers it first and keeps it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reminders must be configured by the host.</b> The sampler and the rollup are both
    ///         <c>IRemindable</c>, and a silo with no reminder service throws on
    ///         <c>RegisterOrUpdateReminder</c>. This method does not choose one — production is Redis
    ///         (docs/plan/04 § Reminders) and a test's is <c>UseInMemoryReminderService</c>, and
    ///         picking either here would take the decision away from the host that knows which it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing here starts a sampler.</b> Sampling begins when something calls
    ///         <c>IUsageSamplerGrain.StartAsync</c> for a subscription, and the intended caller is
    ///         subscription creation, which lives in <c>CyberCloud.Tenancy</c> and is not this
    ///         assembly's to change. Until that call exists, a subscription produces no state-based
    ///         usage and does so silently — which is why <c>IUsageSamplerGrain.IsRunningAsync</c>
    ///         exists for a health check to fail on, and why this is the first thing to wire when
    ///         metering is turned on for real.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudMetering(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => {
                services.TryAddSingleton<IClock, SystemClock>();
                services.TryAddSingleton<IUsageSink, UnavailableUsageSink>();
                services.TryAddSingleton<IMeteredResourceSource, UnavailableMeteredResourceSource>();
                services.TryAddSingleton<IUsageEmitter, GrainUsageEmitter>();
            }
        );
    }
}
