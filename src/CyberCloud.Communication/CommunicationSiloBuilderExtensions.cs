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
                services.TryAddSingleton<IMessageSender, GrainMessageSender>();
                services.TryAddSingleton<IWebhookRouter, WebhookRouter>();
            }
        );
    }
}
