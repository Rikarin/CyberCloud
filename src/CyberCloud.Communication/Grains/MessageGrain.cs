using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="IMessageGrain" /> — Entity, Hot and TTL'd, key
///     <c>res/{derived(serviceId, idempotencyKey):N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="IMessageGrain" /> first: the grain key <i>is</i> the idempotency
///         check.</b> There is no "have I seen this key" lookup in this class because there does not
///         need to be — a retry carrying the same key arrives at this activation, and Orleans
///         serializes calls to it, so two concurrent retries cannot both find the state empty.
///     </para>
///     <para>
///         <b>The order of the checks in <see cref="DispatchAsync" /> is the contract</b>, and every
///         step in it is a thing that must not reach a carrier. Moving the suppression check below
///         the dispatch would still "work" in every test that asserts a refusal — and would call the
///         provider for an address somebody opted out of, which is the failure docs/plan/17 § The
///         parts that are actually the work says gets a sending domain blocked.
///     </para>
/// </remarks>
public sealed class MessageGrain(
    [PersistentState("message", StorageTiers.Hot)] IPersistentState<MessageState> state,
    IChannelProviderRegistry providers,
    IGrainFactory grains,
    IClock clock
)
    : Grain, IMessageGrain {
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = CommunicationGrainDecoder.TenantOf(this);
        _ = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<MessageSnapshot>> SendAsync(SendRequest request) {
        var prepared = Prepare(request);
        if (prepared.TryGetError(out var invalid)) {
            return Result<MessageSnapshot>.Failure(invalid);
        }

        var (destination, digest) = prepared.GetValueOrThrow();
        var now = clock.UtcNow;

        if (!state.State.IsExpired(now)) {
            var recorded = state.State.Snapshot!;

            // ⚠ Two different messages under one idempotency key is a bug in the caller's key
            // derivation, and answering with the first message's status would hide it until somebody
            // asked why the second never arrived.
            if (!string.Equals(state.State.RequestDigest, digest, StringComparison.Ordinal)) {
                return Result<MessageSnapshot>.Failure(
                    ErrorCode.Conflict,
                    $"Idempotency key '{request.IdempotencyKey}' already named a {recorded.Channel} "
                    + $"message to {recorded.Destination}, and this send differs from it. One key "
                    + "means one message — derive the key from the thing being notified about (the "
                    + "user and the purpose and the window), never from the attempt."
                );
            }

            // ⚠ A refusal is re-attemptable and a dispatch is not, and that asymmetry is the whole
            // idempotency guarantee. Nothing left the platform for a Refused message, so running the
            // checks again is free and lets a send succeed once the address is un-suppressed or the
            // window rolls over. Queued, Dispatched, Delivered and Failed all mean a carrier was or
            // may have been called, so they come straight back.
            if (recorded.Status != MessageStatus.Refused) {
                return Result<MessageSnapshot>.Success(recorded);
            }
        }

        return await DispatchAsync(request, destination, digest, now);
    }

    /// <inheritdoc />
    public Task<Result<MessageSnapshot>> GetAsync() {
        if (state.State.IsExpired(clock.UtcNow)) {
            return Task.FromResult(
                Result<MessageSnapshot>.Failure(
                    ErrorCode.ResourceNotFound,
                    "No message is recorded under this idempotency key. It was never sent, or it was "
                    + "sent more than IMessageGrain.Retention ago and the hot-tier record has aged "
                    + "out — and the two are deliberately indistinguishable, because a tier that "
                    + "could tell them apart would be the durable one."
                )
            );
        }

        return Task.FromResult(Result<MessageSnapshot>.Success(state.State.Snapshot!));
    }

    /// <inheritdoc />
    public async Task<Result<MessageSnapshot>> RecordReceiptAsync(DeliveryReceipt receipt) {
        ArgumentNullException.ThrowIfNull(receipt);

        if (state.State.IsExpired(clock.UtcNow)) {
            return Result<MessageSnapshot>.Failure(
                ErrorCode.ResourceNotFound,
                "No message is recorded under this idempotency key, so there is nothing to record a "
                + "receipt against."
            );
        }

        var snapshot = state.State.Snapshot!;

        // ⚠ Every receipt is kept, including one for a status the message has already passed and one
        // that is a duplicate of a receipt already seen. Carriers retry their webhooks and deliver
        // them out of order, so the receipt list is a log rather than a state machine, and only the
        // status is advanced — never rewound.
        var receipts = snapshot.Receipts.IsDefault ? [] : snapshot.Receipts;

        var status = Advance(snapshot.Status, receipt.Status);
        var settled = status is MessageStatus.Delivered or MessageStatus.Failed;

        state.State.Snapshot = snapshot with {
            Status = status,
            Receipts = receipts.Add(receipt),
            SettledAt = settled ? receipt.OccurredAt : snapshot.SettledAt,
            Cost = receipt.Cost > 0 ? receipt.Cost : snapshot.Cost,
            Currency = receipt.Currency.Length > 0 ? receipt.Currency : snapshot.Currency,
            Detail = receipt.Detail.Length > 0 ? receipt.Detail : snapshot.Detail
        };

        await state.WriteStateAsync();

        // The carrier priced it. Settle the reservation to the real figure so the day's spend is
        // accurate rather than an estimate — ISendLimitGrain.SettleAsync.
        if (receipt.Cost > 0 && state.State.ReservationId != Guid.Empty) {
            _ = await Limits(snapshot.ServiceId).SettleAsync(state.State.ReservationId, receipt.Cost);
            state.State.ReservationId = Guid.Empty;
            await state.WriteStateAsync();
        }

        return Result<MessageSnapshot>.Success(state.State.Snapshot);
    }

    /// <inheritdoc />
    public async Task<Result<MessageSnapshot>> RetryAsync(SendRequest request) {
        var prepared = Prepare(request);
        if (prepared.TryGetError(out var invalid)) {
            return Result<MessageSnapshot>.Failure(invalid);
        }

        var (destination, digest) = prepared.GetValueOrThrow();
        var now = clock.UtcNow;

        if (state.State.IsExpired(now)) {
            return Result<MessageSnapshot>.Failure(
                ErrorCode.ResourceNotFound,
                "No message is recorded under this idempotency key, so there is nothing to retry."
            );
        }

        if (state.State.Snapshot!.Status != MessageStatus.Queued) {
            return Result<MessageSnapshot>.Failure(
                ErrorCode.Conflict,
                $"This message is {state.State.Snapshot.Status} and only a Queued one can be "
                + "re-driven. Queued with no provider id is the ambiguous case — the silo died "
                + "between recording the send and hearing from the carrier, so nobody knows whether "
                + "it went out. Everything else has a known outcome, and re-driving it would send a "
                + "second copy."
            );
        }

        if (!string.Equals(state.State.RequestDigest, digest, StringComparison.Ordinal)) {
            return Result<MessageSnapshot>.Failure(
                ErrorCode.Conflict,
                "The retry does not match the message that is queued. A retry re-asserts the same "
                + "content; the grain keeps a digest rather than the body precisely so it can check "
                + "that without storing an OTP for a month."
            );
        }

        return await DispatchAsync(request, destination, digest, now);
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Validates the request against this grain's identity and canonicalises what the checks
    ///     need.
    /// </summary>
    /// <remarks>
    ///     ⚠ The key check is not ceremony. This grain is addressed by a guid derived from the
    ///     service and the idempotency key, so a caller that computed one key and sent a request
    ///     carrying another would get idempotency against a message nobody can find again.
    /// </remarks>
    Result<(string Destination, string Digest)> Prepare(SendRequest request) {
        if (request is null) {
            return Result<(string, string)>.Failure(ErrorCode.InvalidRequestBody, "A send needs a body.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) {
            return Result<(string, string)>.Failure(
                ErrorCode.InvalidRequestBody,
                "A send carries a client-supplied idempotency key — docs/plan/17 § The parts that are "
                + "actually the work. Without one, a retry after a timeout sends a second message, "
                + "and an invoice notice sent twice is a support call."
            );
        }

        if (request.ServiceId == Guid.Empty) {
            return Result<(string, string)>.Failure(
                ErrorCode.InvalidResourceId,
                "A send names the communication service it goes through."
            );
        }

        var expected = CommunicationGrainKeys.Message(request.ServiceId, request.IdempotencyKey);
        if (!string.Equals(expected, this.GetKeyWithinTenant(), StringComparison.Ordinal)) {
            return Result<(string, string)>.Failure(
                ErrorCode.InvalidGrainKey,
                "This message grain was addressed with a key derived from a different service and "
                + "idempotency key than the request carries. Address it with "
                + "CommunicationGrainKeys.Message(request.ServiceId, request.IdempotencyKey)."
            );
        }

        var normalized = Destinations.Normalize(request.Channel, request.Destination);
        if (normalized.TryGetError(out var error)) {
            return Result<(string, string)>.Failure(error);
        }

        var destination = normalized.GetValueOrThrow();

        return Result<(string, string)>.Success((destination, RequestDigests.Of(request, destination)));
    }

    /// <summary>
    ///     The send path. Every check here is a thing that must not reach a carrier, and they run in
    ///     this order for that reason.
    /// </summary>
    async Task<Result<MessageSnapshot>> DispatchAsync(
        SendRequest request,
        string destination,
        string digest,
        DateTimeOffset now
    ) {
        var messageId = state.State.Snapshot?.MessageId ?? Guid.NewGuid();

        var service = Tenant().GetGrain<ICommunicationServiceGrain>(
            CommunicationGrainKeys.Service(request.ServiceId)
        );

        var configured = await service.GetChannelAsync(request.Channel);
        if (configured.TryGetError(out var noChannel)) {
            return await RefuseAsync(messageId, request, destination, digest, now, noChannel);
        }

        var channel = configured.GetValueOrThrow();
        if (!channel.Enabled) {
            return await RefuseAsync(
                messageId,
                request,
                destination,
                digest,
                now,
                new(
                    ErrorCode.PolicyViolation,
                    $"The {request.Channel} channel is configured on service {request.ServiceId:D} "
                    + "and is turned off. Somebody disabled it — an abuse hold, a runaway loop, a "
                    + "rotated credential — and turning it back on is a deliberate act."
                )
            );
        }

        // ── The sender's compliance standing. We are a broker, not a carrier. ──────────────────
        if (channel.SenderId != Guid.Empty) {
            var sender = await Tenant()
                .GetGrain<ISenderIdentityGrain>(CommunicationGrainKeys.Sender(channel.SenderId))
                .CheckAsync(request.Channel);

            if (sender.TryGetError(out var notCleared)) {
                return await RefuseAsync(messageId, request, destination, digest, now, notCleared);
            }
        }

        // ── The suppression list, before dispatch. docs/plan/17 § The parts that are actually the
        //    work: "honoured before dispatch. Ignoring a complaint is how a sending domain gets
        //    blocked." Nothing below this line runs for a suppressed address, and no provider is
        //    constructed, resolved or called. ────────────────────────────────────────────────────
        var suppression = await Suppression(request.ServiceId).CheckAsync(request.Channel, destination);
        if (suppression.TryGetError(out var checkFailed)) {
            return await RefuseAsync(messageId, request, destination, digest, now, checkFailed);
        }

        if (suppression.GetValueOrThrow() is { IsSuppressed: true, Entry: { } entry }) {
            return await RefuseAsync(
                messageId,
                request,
                destination,
                digest,
                now,
                new(
                    ErrorCode.PolicyViolation,
                    $"{destination} is on this service's {request.Channel} suppression list "
                    + $"({entry.Reason}, recorded {entry.SuppressedAt:O}). Nothing was sent and no "
                    + "carrier was called."
                    + (entry.Note.Length > 0 ? $" Recorded reason: {entry.Note}" : string.Empty)
                )
            );
        }

        // ── Render, before dispatch. A missing required parameter fails here rather than at the
        //    carrier — see TemplateRenderer. ────────────────────────────────────────────────────
        var rendered = await RenderAsync(service, request);
        if (rendered.TryGetError(out var renderFailed)) {
            return await RefuseAsync(messageId, request, destination, digest, now, renderFailed);
        }

        var content = rendered.GetValueOrThrow();

        var provider = providers.Resolve(request.Channel, channel.Provider);
        if (provider.TryGetError(out var noProvider)) {
            return await RefuseAsync(messageId, request, destination, digest, now, noProvider);
        }

        // ── The spend limit, before dispatch. The only thing between a bug and a five-figure
        //    invoice — docs/plan/17 § The parts that are actually the work. ──────────────────────
        var reserved = await Limits(request.ServiceId).ReserveAsync(request.Channel, channel.Limits, channel.EstimatedUnitCost);
        if (reserved.TryGetError(out var overLimit)) {
            return await RefuseAsync(messageId, request, destination, digest, now, overLimit);
        }

        var reservation = reserved.GetValueOrThrow();

        // Queued is written BEFORE the carrier is called, so a silo that dies mid-dispatch leaves a
        // record saying "we may have sent this" rather than nothing at all. RetryAsync is what
        // resolves that ambiguity, deliberately, by a caller who knows it is safe to repeat.
        state.State.Snapshot = new() {
            MessageId = messageId,
            ServiceId = request.ServiceId,
            Channel = request.Channel,
            Destination = destination,
            Status = MessageStatus.Queued,
            Provider = provider.GetValueOrThrow().Name,
            QueuedAt = now,
            Currency = channel.Limits.Currency
        };

        state.State.RequestDigest = digest;
        state.State.ExpiresAt = now + IMessageGrain.Retention;
        state.State.ReservationId = reservation.ReservationId;
        await state.WriteStateAsync();

        var dispatched = await provider.GetValueOrThrow()
            .SendAsync(
                new() {
                    MessageId = messageId,
                    TenantId = tenantId,
                    Channel = request.Channel,
                    Destination = destination,
                    Sender = channel.SenderId == Guid.Empty ? string.Empty : channel.SenderId.ToString("D"),
                    Subject = content.Subject,
                    Body = content.Body,
                    ProviderTemplateName = content.ProviderTemplateName,
                    Arguments = request.Arguments.IsDefault ? [] : request.Arguments,
                    Locale = content.Locale,
                    Credentials = channel.Credentials
                }
            );

        if (dispatched.TryGetError(out var carrierRefused)) {
            // ⚠ The reservation goes back. A channel whose carrier is down would otherwise burn the
            // day's allowance on messages that never left, and the tenant's first working send would
            // be refused with a limit message that is true and useless.
            _ = await Limits(request.ServiceId).ReleaseAsync(reservation.ReservationId);
            state.State.ReservationId = Guid.Empty;

            state.State.Snapshot = state.State.Snapshot with {
                Status = MessageStatus.Failed,
                SettledAt = now,
                Detail = carrierRefused.Message
            };

            await state.WriteStateAsync();

            return Result<MessageSnapshot>.Success(state.State.Snapshot);
        }

        var accepted = dispatched.GetValueOrThrow();

        state.State.Snapshot = state.State.Snapshot with {
            Status = MessageStatus.Dispatched,
            ProviderMessageId = accepted.ProviderMessageId,
            DispatchedAt = now,
            Cost = accepted.Cost,
            Currency = accepted.Currency.Length > 0 ? accepted.Currency : state.State.Snapshot.Currency
        };

        await state.WriteStateAsync();

        // The index a delivery receipt arrives through. Bound after the dispatch, because before it
        // there is no carrier id to bind.
        if (accepted.ProviderMessageId.Length > 0) {
            _ = await Tenant()
                .GetGrain<IProviderMessageIndexGrain>(
                    CommunicationGrainKeys.ProviderMessage(request.ServiceId, accepted.ProviderMessageId)
                )
                .BindAsync(request.IdempotencyKey, state.State.ExpiresAt);
        }

        if (accepted.Cost > 0) {
            _ = await Limits(request.ServiceId).SettleAsync(reservation.ReservationId, accepted.Cost);
            state.State.ReservationId = Guid.Empty;
            await state.WriteStateAsync();
        }

        return Result<MessageSnapshot>.Success(state.State.Snapshot);
    }

    /// <summary>Renders the body, from a template or from free text.</summary>
    async Task<Result<RenderedMessage>> RenderAsync(ICommunicationServiceGrain service, SendRequest request) {
        if (string.IsNullOrWhiteSpace(request.TemplateName)) {
            // ⚠ WhatsApp does not accept a body from us for a business-initiated message; Meta
            // accepts a pre-approved template name and arguments. Refusing here names the reason;
            // letting it through produces a carrier error nobody outside Meta can decode.
            if (MessageTemplateGrain.RequiresApproval(request.Channel)) {
                return Result<RenderedMessage>.Failure(
                    ErrorCode.PolicyViolation,
                    $"{request.Channel} requires a pre-approved template for a business-initiated "
                    + "message and this send names none. docs/plan/17 § The parts that are actually "
                    + "the work: \"WhatsApp requires pre-approved templates\"."
                );
            }

            if (string.IsNullOrWhiteSpace(request.Body)) {
                return Result<RenderedMessage>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A send names a template or carries a body. An empty message costs the same as a "
                    + "real one and tells the recipient nothing."
                );
            }

            return Result<RenderedMessage>.Success(
                new() { Body = request.Body, Locale = request.Locale, Version = 0 }
            );
        }

        var resolved = await service.ResolveTemplateAsync(request.TemplateName);
        if (resolved.TryGetError(out var unknownTemplate)) {
            return Result<RenderedMessage>.Failure(unknownTemplate);
        }

        var version = await Tenant()
            .GetGrain<IMessageTemplateGrain>(CommunicationGrainKeys.Template(resolved.GetValueOrThrow()))
            .GetVersionAsync(request.TemplateVersion);

        if (version.TryGetError(out var noVersion)) {
            return Result<RenderedMessage>.Failure(noVersion);
        }

        return TemplateRenderer.Render(
            version.GetValueOrThrow(),
            request.Locale,
            request.Arguments.IsDefault ? [] : request.Arguments
        );
    }

    /// <summary>Records a refusal — nothing was sent and no carrier was called.</summary>
    async Task<Result<MessageSnapshot>> RefuseAsync(
        Guid messageId,
        SendRequest request,
        string destination,
        string digest,
        DateTimeOffset now,
        Error reason
    ) {
        state.State.Snapshot = new() {
            MessageId = messageId,
            ServiceId = request.ServiceId,
            Channel = request.Channel,
            Destination = destination,
            Status = MessageStatus.Refused,
            QueuedAt = now,
            SettledAt = now,
            Detail = reason.Message
        };

        state.State.RequestDigest = digest;
        state.State.ExpiresAt = now + IMessageGrain.Retention;
        state.State.ReservationId = Guid.Empty;
        await state.WriteStateAsync();

        // ⚠ The refusal is returned as a FAILURE, not as a successful snapshot carrying a Refused
        // status. A caller that had to inspect a status field to learn nothing was sent is a caller
        // who will forget to, and the whole point of Result (docs/plan/00 § Coding standards) is
        // that the failure path is the one you cannot walk past.
        return Result<MessageSnapshot>.Failure(reason);
    }

    /// <summary>
    ///     Where a receipt moves the status to.
    /// </summary>
    /// <remarks>
    ///     ⚠ Terminal states do not move. A carrier that delivers a "sent" webhook after its
    ///     "delivered" one — which happens, because they are produced by different systems — must
    ///     not walk a delivered message back to dispatched.
    /// </remarks>
    static MessageStatus Advance(MessageStatus current, MessageStatus reported) =>
        current is MessageStatus.Delivered or MessageStatus.Failed
            ? current
            : reported is MessageStatus.Delivered or MessageStatus.Failed
                ? reported
                : current;

    ISuppressionListGrain Suppression(Guid serviceId) =>
        Tenant().GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(serviceId));

    ISendLimitGrain Limits(Guid serviceId) =>
        Tenant().GetGrain<ISendLimitGrain>(CommunicationGrainKeys.Service(serviceId));

    /// <summary>
    ///     This grain's own tenant, as a qualified factory.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A grain-to-grain call is NOT automatically tenant-qualified, and assuming it was cost
    ///     an afternoon.</b> <c>Orleans.Multitenant</c> carries the tenant in the string key and
    ///     nowhere else (ADR-002, docs/plan/02 § ADR-002), so an unqualified
    ///     <c>GrainFactory.GetGrain</c> from inside a grain addresses the <i>null-tenant</i>
    ///     activation of the target — which then fails its own <c>TenantOf</c> check on activation.
    ///     Every cross-grain reference here goes through <c>ForTenant</c>, exactly as
    ///     <c>UsageRollupGrain</c> and <c>OperationGrain</c> do.
    ///     <para>
    ///         The failure is loud rather than silent only because every grain in this module asserts
    ///         its qualification in <c>OnActivateAsync</c>. Without that assertion the send would have
    ///         read an <i>empty</i> service configuration and refused with "no channel configured",
    ///         which is a plausible message for a real misconfiguration.
    ///     </para>
    /// </remarks>
    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
}
