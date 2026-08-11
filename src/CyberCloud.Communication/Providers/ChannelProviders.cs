using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Communication.Providers;

/// <summary>
///     What every refusing seam says, and the shape they share.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>They refuse rather than logging and returning success, and for this module the
///         difference is the product.</b> The whole reason docs/plan/17 § The parts that are actually
///         the work insists on delivery receipts is that <i>"without them 'did it arrive' is
///         unanswerable"</i>. A seam that reported a dispatch and sent nothing makes that question
///         unanswerable in the worst way: the platform says yes. <c>IOtpDeliverySeam</c> in
///         <c>CyberCloud.Identity.Contracts</c> already states the consequence — <i>"an OTP factor
///         that reports delivery and sends nothing locks every user who enrols in it out of their
///         account"</i> — and this is the other end of that same seam.
///     </para>
///     <para>
///         Refusing is also <i>safe</i> here, which is what makes it the right default rather than
///         merely the loud one. <c>MessageGrain</c> reserves spend before dispatch and releases it on
///         a provider failure, so a refusal costs no budget; the message lands on
///         <see cref="MessageStatus.Failed" /> with the reason, and nothing is charged.
///     </para>
/// </remarks>
static class Refusal {
    /// <summary>The dispatch refusal, with what a real implementation would have to bring.</summary>
    /// <param name="channel">Which channel has no carrier.</param>
    /// <param name="owes">What the first real implementation has to do beyond calling an API.</param>
    public static Result<DispatchReceipt> Dispatch(ChannelKind channel, string owes) =>
        Result<DispatchReceipt>.Failure(
            ErrorCode.InternalError,
            $"No {channel} carrier is registered, so nothing was sent. docs/plan/17 § The channel "
            + $"abstraction names the implementations and this build ships none of them. {owes} "
            + "Register an IChannelProvider whose Kind is "
            + channel.ToString()
            + "; IChannelProvider's remarks list what one owes."
        );

    /// <summary>The status refusal. Same argument, and an unknown status is not a delivered one.</summary>
    /// <param name="channel">The channel.</param>
    public static Result<DeliveryStatus> Status(ChannelKind channel) =>
        Result<DeliveryStatus>.Failure(
            ErrorCode.InternalError,
            $"No {channel} carrier is registered, so where a message got to is unknown. ⚠ Reporting "
            + "MessageStatus.Unknown as a success would let a caller read it as \"not delivered yet\" "
            + "and keep waiting, which is indistinguishable from the carrier being slow."
        );

    /// <summary>
    ///     The webhook outcome. ⚠ <b>Success, and empty</b> — the one refusing path that does not
    ///     refuse.
    /// </summary>
    /// <remarks>
    ///     A callback arriving for a channel with no registered carrier is somebody's
    ///     misconfiguration or a stale carrier subscription, and the router's contract is that an
    ///     unrecognised payload is a success (see <see cref="IWebhookRouter.HandleAsync" />). Failing
    ///     would make the carrier retry it forever.
    /// </remarks>
    public static ValueTask<Result<WebhookOutcome>> Webhook() =>
        ValueTask.FromResult(Result<WebhookOutcome>.Success(WebhookOutcome.Empty));
}

/// <summary>The <see cref="IChannelProvider" /> a silo with no SMS carrier registers: it refuses.</summary>
/// <remarks>
///     ⚠ <b>A real implementation's hard part is not the HTTP call, it is US 10DLC.</b> Sending
///     application-to-person SMS to a US number requires a registered brand, a registered campaign
///     and a number associated with it; unregistered traffic is filtered by the carriers rather than
///     rejected by the API, so it fails <i>silently and partially</i>. That is the tenant's
///     obligation with our tooling (docs/plan/17 § The channel abstraction), which means a real
///     provider owes <see cref="ISenderIdentityGrain" /> an honest campaign status and owes the
///     delivery-receipt path the ability to distinguish "filtered" from "undelivered".
/// </remarks>
public sealed class UnavailableSmsProvider(ILogger<UnavailableSmsProvider> logger) : IChannelProvider {
    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Sms;

    /// <inheritdoc />
    public string Name => "unavailable";

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogWarning(
            "No SMS carrier is registered, so message {MessageId} was not sent. The message grain "
            + "records the failure and the spend reservation is released.",
            message.MessageId
        );

        return Task.FromResult(
            Refusal.Dispatch(
                ChannelKind.Sms,
                "A real one owes credential resolution from a CarrierSecretRef, a provider message "
                + "id on every accept, signature verification on every receipt, and — the part that "
                + "is not code — a 10DLC campaign status it reports rather than assumes."
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refusal.Status(ChannelKind.Sms));

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        Refusal.Webhook();
}

/// <summary>The <see cref="IChannelProvider" /> a silo with no WhatsApp carrier registers: it refuses.</summary>
/// <remarks>
///     ⚠ <b>A real implementation cannot send a body at all.</b> Meta's Cloud API accepts a
///     pre-approved template name plus arguments for any business-initiated message; free text is
///     only allowed inside a 24-hour window opened by the recipient. So a real provider owes
///     <see cref="MessageTemplateVersion.ProviderTemplateName" /> a real value, owes
///     <see cref="MessageTemplateVersion.Approval" /> the carrier's actual answer, and owes callers
///     a clear failure when neither exists — which is what <c>MessageGrain</c> already refuses on
///     before it gets here.
/// </remarks>
public sealed class UnavailableWhatsAppProvider(ILogger<UnavailableWhatsAppProvider> logger) : IChannelProvider {
    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.WhatsApp;

    /// <inheritdoc />
    public string Name => "unavailable";

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogWarning(
            "No WhatsApp carrier is registered, so message {MessageId} was not sent.",
            message.MessageId
        );

        return Task.FromResult(
            Refusal.Dispatch(
                ChannelKind.WhatsApp,
                "A real one owes template-by-reference sending — Meta accepts a pre-approved "
                + "template name and arguments, never a body — plus the approval status written back "
                + "onto the template version, which is a business task and not an engineering one."
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refusal.Status(ChannelKind.WhatsApp));

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        Refusal.Webhook();
}

/// <summary>The <see cref="IChannelProvider" /> a silo with no email sender registers: it refuses.</summary>
/// <remarks>
///     ⚠ <b>A real implementation's hard part is deliberately somebody else's.</b> docs/plan/17
///     § Deliverability puts SPF, DKIM, DMARC, PTR records, feedback loops and IP warm-up on
///     <c>CyberCloud.Mail</c>, and says <i>"the platform will not enable sending until the DNS
///     records verify"</i>. A provider here talks to whatever does that — SES, or our own Postfix —
///     and owes bounce and complaint classification into
///     <see cref="DeliveryReceipt.Suppresses" />, because that is what keeps the sending domain
///     alive.
/// </remarks>
public sealed class UnavailableEmailProvider(ILogger<UnavailableEmailProvider> logger) : IChannelProvider {
    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Email;

    /// <inheritdoc />
    public string Name => "unavailable";

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogWarning(
            "No email sender is registered, so message {MessageId} was not sent.",
            message.MessageId
        );

        return Task.FromResult(
            Refusal.Dispatch(
                ChannelKind.Email,
                "A real one owes a verified sending domain before its first send — docs/plan/17 "
                + "§ Deliverability — and owes bounce and complaint classification onto "
                + "DeliveryReceipt.Suppresses, which is what stops an ignored complaint from getting "
                + "the domain blocked."
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refusal.Status(ChannelKind.Email));

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        Refusal.Webhook();
}

/// <summary>The <see cref="IChannelProvider" /> a silo with no push service registers: it refuses.</summary>
/// <remarks>
///     ⚠ <b>A real implementation's trap is that a push token expires and the failure is
///     asynchronous.</b> APNs and FCM both report an unregistered device on a later feedback channel
///     rather than at send time, so a provider owes that path a
///     <see cref="SuppressionReason.HardBounce" /> — a device that has uninstalled the app is exactly
///     an address that no longer exists, and continuing to push at it is what gets a sender
///     throttled.
/// </remarks>
public sealed class UnavailablePushProvider(ILogger<UnavailablePushProvider> logger) : IChannelProvider {
    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Push;

    /// <inheritdoc />
    public string Name => "unavailable";

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogWarning("No push service is registered, so message {MessageId} was not sent.", message.MessageId);

        return Task.FromResult(
            Refusal.Dispatch(
                ChannelKind.Push,
                "A real one owes APNs or FCM credentials from a handle, and owes the asynchronous "
                + "unregistered-device feedback a route into the suppression list."
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refusal.Status(ChannelKind.Push));

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        Refusal.Webhook();
}

/// <summary>The <see cref="IChannelProvider" /> a silo with no voice carrier registers: it refuses.</summary>
/// <remarks>
///     ⚠ <b>Voice carries the strictest per-country rules of the five and the platform enforces
///     none of them.</b> Calling hours, recorded-call consent, and prerecorded-message restrictions
///     vary by jurisdiction and in several are criminal rather than civil matters. We are a broker,
///     not a carrier (docs/plan/17 § The channel abstraction): a real provider owes the tenant the
///     carrier's own answer about what a sender is cleared for, and owes it accurately, because it
///     is the only thing standing between the tenant and a regulator.
/// </remarks>
public sealed class UnavailableVoiceProvider(ILogger<UnavailableVoiceProvider> logger) : IChannelProvider {
    /// <inheritdoc />
    public ChannelKind Kind => ChannelKind.Voice;

    /// <inheritdoc />
    public string Name => "unavailable";

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogWarning("No voice carrier is registered, so message {MessageId} was not sent.", message.MessageId);

        return Task.FromResult(
            Refusal.Dispatch(
                ChannelKind.Voice,
                "A real one owes per-country sender clearance read back from the carrier, and owes "
                + "call-detail records the delivery-receipt path can settle a cost from."
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refusal.Status(ChannelKind.Voice));

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        Refusal.Webhook();
}

/// <summary>
///     An <see cref="IChannelProvider" /> that keeps everything in memory. For tests, and for a
///     single-silo development host.
/// </summary>
/// <remarks>
///     ⚠ <b>Not a carrier and not a stand-in for one.</b> It has none of the properties
///     <see cref="IChannelProvider" /> says a real implementation owes — no credential resolution, no
///     signature verification, no cost, no compliance status. What it is for is proving the things
///     this repository can actually prove: that a suppressed address never reaches a provider, that
///     one idempotency key produces one <see cref="Sent" /> entry and two produce two, and that a
///     spend limit stops a loop. <see cref="Calls" /> is the counter those assertions read.
/// </remarks>
public sealed class InMemoryChannelProvider(ChannelKind kind) : IChannelProvider {
    readonly ConcurrentQueue<OutboundMessage> sent = new();
    int calls;

    /// <inheritdoc />
    public ChannelKind Kind { get; } = kind;

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <summary>Every message handed to it, in dispatch order.</summary>
    public IReadOnlyCollection<OutboundMessage> Sent => sent;

    /// <summary>
    ///     How many times <see cref="SendAsync" /> was entered, including the calls that failed.
    /// </summary>
    /// <remarks>
    ///     ⚠ Counted separately from <see cref="Sent" /> on purpose. "The provider was never called"
    ///     is a different assertion from "nothing was sent", and for the suppression rule it is the
    ///     one that matters: a provider that was called and refused has already seen the address.
    /// </remarks>
    public int Calls => Volatile.Read(ref calls);

    /// <summary>When set, every dispatch fails — so the release-on-failure path can be exercised.</summary>
    public bool Fail { get; set; }

    /// <summary>What each dispatch reports as its cost.</summary>
    public decimal Cost { get; set; }

    /// <summary>The currency <see cref="Cost" /> is in.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>Forgets everything and stops failing.</summary>
    public void Reset() {
        sent.Clear();
        Volatile.Write(ref calls, 0);
        Fail = false;
        Cost = 0m;
        Currency = "EUR";
    }

    /// <inheritdoc />
    public Task<Result<DispatchReceipt>> SendAsync(
        OutboundMessage message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(message);
        Interlocked.Increment(ref calls);

        if (Fail) {
            return Task.FromResult(
                Result<DispatchReceipt>.Failure(ErrorCode.InternalError, "the carrier is down")
            );
        }

        sent.Enqueue(message);

        return Task.FromResult(
            Result<DispatchReceipt>.Success(
                new() {
                    ProviderMessageId = ProviderIdFor(message.MessageId),
                    Status = MessageStatus.Dispatched,
                    Cost = Cost,
                    Currency = Currency,
                    AcceptedAt = DateTimeOffset.UnixEpoch
                }
            )
        );
    }

    /// <summary>
    ///     The synthetic carrier id this provider mints for a message. ⚠ Deterministic so a test can
    ///     name it when it feeds a receipt back in, and prefixed so it cannot be mistaken for a real
    ///     carrier's.
    /// </summary>
    /// <param name="messageId">The message.</param>
    public static string ProviderIdFor(Guid messageId) =>
        "mem-" + messageId.ToString("N", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public Task<Result<DeliveryStatus>> GetStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<DeliveryStatus>.Success(
                new() {
                    ProviderMessageId = providerMessageId,
                    Status = MessageStatus.Dispatched,
                    ProviderStatus = "in-memory",
                    CheckedAt = DateTimeOffset.UnixEpoch
                }
            )
        );

    /// <inheritdoc />
    public ValueTask<Result<WebhookOutcome>> HandleWebhookAsync(
        WebhookEnvelope request,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(Result<WebhookOutcome>.Success(WebhookOutcome.Empty));
}

/// <summary>
///     Resolves a channel and a provider name to the registered <see cref="IChannelProvider" />.
/// </summary>
/// <remarks>
///     ⚠ <b>An unnamed provider resolves to the channel's <i>only</i> registration, and to nothing
///     when there are several.</b> Picking the first of several would make which carrier a tenant
///     sends through depend on service-registration order — a thing that changes when somebody
///     reorders a wiring method, and that nobody would look at when a tenant's messages started
///     arriving from a different sender id.
/// </remarks>
public sealed class ChannelProviderRegistry(IEnumerable<IChannelProvider> providers) : IChannelProviderRegistry {
    readonly ImmutableArray<IChannelProvider> registered = [.. providers];

    /// <inheritdoc />
    public Result<IChannelProvider> Resolve(ChannelKind channel, string name) {
        var forChannel = registered.Where(x => x.Kind == channel).ToImmutableArray();

        if (forChannel.Length == 0) {
            return Result<IChannelProvider>.Failure(
                ErrorCode.InternalError,
                $"No IChannelProvider serves {channel}. Every channel needs at least one, even if it "
                + "is the refusing seam — a channel with none means a send fails with a wiring error "
                + "instead of an honest \"there is no carrier\"."
            );
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return forChannel.Length == 1
                ? Result<IChannelProvider>.Success(forChannel[0])
                : Result<IChannelProvider>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"{channel} has {forChannel.Length.ToString(CultureInfo.InvariantCulture)} "
                    + "registered providers ("
                    + string.Join(", ", forChannel.Select(x => x.Name))
                    + ") and the channel configuration names none. Set "
                    + "ChannelConfiguration.Provider — which carrier a tenant sends through is not a "
                    + "thing to decide by registration order."
                );
        }

        foreach (var candidate in forChannel) {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) {
                return Result<IChannelProvider>.Success(candidate);
            }
        }

        return Result<IChannelProvider>.Failure(
            ErrorCode.ResourceNotFound,
            $"No IChannelProvider named '{name}' serves {channel}. Registered: "
            + string.Join(", ", forChannel.Select(x => x.Name))
            + "."
        );
    }
}
