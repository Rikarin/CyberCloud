using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Communication.Webhooks;

/// <summary>
///     The <see cref="IWebhookRouter" /> that talks to grains: receipts to their messages, inbound to
///     the suppression list and onwards.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The provider parses and this correlates, and the split is what makes a new carrier
///         cheap.</b> A provider knows one carrier's payload and nothing about grains; this knows
///         about grains and nothing about payloads. Every carrier-specific parser then gets the
///         late-receipt and duplicate-receipt cases right by construction, because it never handles
///         them.
///     </para>
///     <para>
///         ⚠ <b>Every <c>GetGrain</c> is qualified with <c>ForTenant</c> — CC1006.</b> A webhook
///         handler is not a grain, and a carrier callback is the least trusted input the platform
///         takes.
///     </para>
/// </remarks>
public sealed class WebhookRouter(
    IChannelProviderRegistry providers,
    IGrainFactory grains,
    ILogger<WebhookRouter> logger
)
    : IWebhookRouter {
    /// <inheritdoc />
    public async Task<Result<WebhookHandling>> HandleAsync(
        Guid tenantId,
        WebhookEnvelope envelope,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ServiceId == Guid.Empty) {
            return Result<WebhookHandling>.Failure(
                ErrorCode.InvalidResourceId,
                "A webhook envelope names the service whose callback path it arrived on. That path "
                + "is where the tenant and the service come from — a callback that does not know "
                + "which service it belongs to cannot be attributed to one safely."
            );
        }

        var channels = await For(tenantId)
            .GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(envelope.ServiceId))
            .ListChannelsAsync();

        if (channels.TryGetError(out var unknownService)) {
            return Result<WebhookHandling>.Failure(unknownService);
        }

        var applied = 0;
        var ignored = 0;
        var inbound = 0;
        var optOuts = 0;

        foreach (var channel in channels.GetValueOrThrow()) {
            var provider = providers.Resolve(channel.Channel, channel.Provider);
            if (provider.TryGetError(out _)) {
                continue;
            }

            var parsed = await provider.GetValueOrThrow().HandleWebhookAsync(envelope, cancellationToken);
            if (parsed.TryGetError(out var parseFailed)) {
                // A provider that cannot verify a signature says so, and that IS worth failing on:
                // an unverified receipt suppresses addresses on a stranger's say-so.
                return Result<WebhookHandling>.Failure(parseFailed);
            }

            var outcome = parsed.GetValueOrThrow();

            foreach (var receipt in outcome.Receipts.IsDefault ? [] : outcome.Receipts) {
                var handled = await HandleReceiptAsync(tenantId, envelope.ServiceId, receipt, cancellationToken);

                if (handled.TryGetValue(out var found) && found) {
                    applied++;
                } else {
                    ignored++;
                }
            }

            foreach (var message in outcome.Inbound.IsDefault ? [] : outcome.Inbound) {
                var handled = await HandleInboundAsync(tenantId, message, cancellationToken);
                inbound++;

                if (handled.TryGetValue(out var result) && result.Suppressed) {
                    optOuts++;
                }
            }
        }

        return Result<WebhookHandling>.Success(
            new() {
                ReceiptsApplied = applied,
                ReceiptsIgnored = ignored,
                InboundHandled = inbound,
                OptOuts = optOuts
            }
        );
    }

    /// <inheritdoc />
    public async Task<Result<bool>> HandleReceiptAsync(
        Guid tenantId,
        Guid serviceId,
        DeliveryReceipt receipt,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(receipt);

        // ⚠ THE UNKNOWN-ID RULE, AND IT IS A SUCCESS. docs/plan/17 § The parts that are actually the
        // work has receipts arriving as webhooks; in practice they arrive late, twice, and for
        // messages past IMessageGrain.Retention. Every one of those is the carrier working
        // correctly, so failing here would page somebody for someone else's retry logic. The count
        // is still reported, because a number that climbs means our retention horizon is shorter
        // than the carrier's receipt latency — which is a real finding, just not an incident.
        if (string.IsNullOrWhiteSpace(receipt.ProviderMessageId)) {
            logger.LogDebug("A delivery receipt carried no provider message id and was dropped.");
            return Result<bool>.Success(false);
        }

        var tenant = For(tenantId);

        var bound = await tenant
            .GetGrain<IProviderMessageIndexGrain>(
                CommunicationGrainKeys.ProviderMessage(serviceId, receipt.ProviderMessageId)
            )
            .LookupAsync();

        if (bound.TryGetError(out _)) {
            logger.LogDebug(
                "A delivery receipt for provider message {ProviderMessageId} matched no message and "
                + "was ignored. Late, duplicate and orphaned receipts are normal.",
                receipt.ProviderMessageId
            );

            return Result<bool>.Success(false);
        }

        var recorded = await tenant
            .GetGrain<IMessageGrain>(CommunicationGrainKeys.Message(serviceId, bound.GetValueOrThrow()))
            .RecordReceiptAsync(receipt);

        if (recorded.TryGetError(out _)) {
            return Result<bool>.Success(false);
        }

        // A carrier that says an address is permanently unreachable, or that its owner complained,
        // has told us something the suppression list must not lose — docs/plan/17 § The parts that
        // are actually the work.
        if (receipt.Suppresses != SuppressionReason.Unknown) {
            var snapshot = recorded.GetValueOrThrow();

            _ = await tenant
                .GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(serviceId))
                .SuppressAsync(
                    snapshot.Channel,
                    snapshot.Destination,
                    receipt.Suppresses,
                    receipt.Detail.Length > 0 ? receipt.Detail : receipt.ProviderStatus
                );
        }

        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<InboundOutcome>> HandleInboundAsync(
        Guid tenantId,
        InboundMessage inbound,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(inbound);

        if (inbound.ServiceId == Guid.Empty) {
            return Result<InboundOutcome>.Failure(
                ErrorCode.InvalidResourceId,
                "An inbound message names the service it arrived on."
            );
        }

        // ⚠ SUPPRESSION HAPPENS FIRST, BEFORE ANY FORWARDING. docs/plan/17 § The parts that are
        // actually the work: "STOP handling is legally required in most jurisdictions." Forwarding
        // first and suppressing after would make an opt-out depend on the tenant's webhook endpoint
        // being reachable — so a customer's downtime would become our regulatory finding.
        if (!StopKeywords.IsStop(inbound.Body, out var keyword)) {
            return Result<InboundOutcome>.Success(new() { WasStop = false });
        }

        var suppressed = await For(tenantId)
            .GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(inbound.ServiceId))
            .SuppressAsync(
                inbound.Channel,
                inbound.From,
                SuppressionReason.OptOut,
                $"Replied '{keyword}' on {inbound.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)}."
            );

        if (suppressed.TryGetError(out var failed)) {
            // ⚠ This one DOES fail. Everything else in the inbound path is best-effort; a stop word
            // that was recognised and not recorded is the one outcome that must reach the caller, so
            // the carrier retries its webhook and we get another chance.
            logger.LogError(
                "A stop keyword from an inbound message could not be recorded on the suppression "
                + "list for service {ServiceId}. The opt-out is NOT in force.",
                inbound.ServiceId
            );

            return Result<InboundOutcome>.Failure(failed);
        }

        return Result<InboundOutcome>.Success(new() { WasStop = true, Keyword = keyword, Suppressed = true });
    }

    TenantGrainFactory For(Guid tenantId) => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
}
