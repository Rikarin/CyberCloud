using CyberCloud.Communication.Providers;
using CyberCloud.Communication.Webhooks;
using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Communication;

/// <summary>Wires the sending domain into a silo.</summary>
/// <remarks>
///     ⚠ <b><c>TryAdd</c> throughout, so a host that registers a real carrier first keeps it.</b>
///     The five refusing seams are registered with <c>AddSingleton</c> rather than <c>TryAdd</c>
///     because they are a <i>collection</i> — <see cref="ChannelProviderRegistry" /> resolves by
///     name, and a real Twilio provider joins the collection beside the refusing one rather than
///     replacing it. That is what lets one silo serve a tenant on Twilio and another tenant whose
///     channel is unconfigured, and get an honest refusal for the second.
/// </remarks>
public static class CommunicationSiloBuilderExtensions {
    /// <summary>Registers the seams, the defaults and the client-side sender.</summary>
    /// <param name="silo">The silo being built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="silo" /> is null.</exception>
    public static ISiloBuilder AddCyberCloudCommunication(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => {
                services.TryAddSingleton<IClock, SystemClock>();

                // ⚠ One refusing seam per channel, and every one of them is registered. A channel
                // with NO provider fails a send with a wiring error; a channel with the refusing one
                // fails it with a sentence saying no carrier is configured and what a real one owes.
                // The second is the message an operator can act on at 03:00.
                services.AddSingleton<IChannelProvider, UnavailableSmsProvider>();
                services.AddSingleton<IChannelProvider, UnavailableWhatsAppProvider>();
                services.AddSingleton<IChannelProvider, UnavailableEmailProvider>();
                services.AddSingleton<IChannelProvider, UnavailablePushProvider>();
                services.AddSingleton<IChannelProvider, UnavailableVoiceProvider>();

                services.TryAddSingleton<IChannelProviderRegistry, ChannelProviderRegistry>();
                services.TryAddSingleton<IWebhookRouter, WebhookRouter>();

                services.AddCyberCloudCommunicationClient();
            }
        );
    }

    /// <summary>
    ///     Registers the two client-side seams — <see cref="IMessageSender" /> and
    ///     <see cref="ICommunicationControlPlane" /> — over whatever <c>IGrainFactory</c> the
    ///     container holds.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The gateway calls this and the silo gets it through
    ///         <see cref="AddCyberCloudCommunication" />, and the split is the whole reason it is a
    ///         separate method.</b> Both seams are constructed over an <c>IGrainFactory</c> and take
    ///         grain <i>references</i>, so they work identically behind a cluster client and inside
    ///         a silo — but the gateway is a client and hosts no grain, so the silo overload's carrier
    ///         seams, registry and webhook router would be dead weight there.
    ///     </para>
    ///     <para>
    ///         Why the gateway needs them at all: <c>CyberCloud.Providers.Communication</c>'s
    ///         synchronous actions — <c>send</c>, <c>status</c>, <c>checkSuppression</c>,
    ///         <c>listSuppressions</c> — run inside <c>ResourceManagerService</c> on the request
    ///         path, which is the gateway's process (docs/plan/08 § The write path, end to end), and
    ///         their handlers hold these two interfaces.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddCyberCloudCommunicationClient(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IMessageSender, GrainMessageSender>();
        services.TryAddSingleton<ICommunicationControlPlane, GrainCommunicationControlPlane>();

        return services;
    }
}
