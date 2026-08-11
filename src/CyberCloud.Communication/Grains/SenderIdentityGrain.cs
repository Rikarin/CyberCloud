using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="ISenderIdentityGrain" /> — Entity, Durable, key <c>res/{senderId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Nothing here decides anything, and that is the whole point.</b> docs/plan/17 § The
///     channel abstraction: <i>"Sender-id registration, 10DLC campaign approval in the US, WhatsApp
///     template pre-approval, and per-country content rules are the tenant's compliance obligations
///     with our tooling, not obligations we assume."</i> This grain records what was submitted and
///     what the carrier answered, and refuses to send through a sender they have not approved.
/// </remarks>
public sealed class SenderIdentityGrain(
    [PersistentState("sender-identity", StorageTiers.Durable)] IPersistentState<SenderIdentityState> state,
    IClock clock
)
    : Grain, ISenderIdentityGrain {
    Guid senderId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = CommunicationGrainDecoder.TenantOf(this);
        senderId = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<SenderIdentity>> RegisterAsync(
        ChannelKind channel,
        string value,
        ImmutableArray<string> countries
    ) {
        if (channel == ChannelKind.Unknown) {
            return Result<SenderIdentity>.Failure(
                ErrorCode.InvalidRequestBody,
                "A sender sends on a channel. An alphanumeric sender id and a From address are not "
                + "interchangeable, and neither are their registrations."
            );
        }

        if (string.IsNullOrWhiteSpace(value)) {
            return Result<SenderIdentity>.Failure(
                ErrorCode.InvalidRequestBody,
                "A sender needs the value recipients will see."
            );
        }

        var sender = new SenderIdentity {
            SenderId = senderId,
            Channel = channel,
            Value = value.Trim(),
            Status = SenderRegistrationStatus.Draft,
            Countries = Normalize(countries),
            CarrierReference = string.Empty,
            UpdatedAt = clock.UtcNow,
            Note = string.Empty
        };

        state.State.Sender = sender;
        await state.WriteStateAsync();

        return Result<SenderIdentity>.Success(sender);
    }

    /// <inheritdoc />
    public async Task<Result<SenderIdentity>> MarkSubmittedAsync(string carrierReference) {
        if (state.State.Sender is not { } sender) {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(carrierReference)) {
            return Result<SenderIdentity>.Failure(
                ErrorCode.InvalidRequestBody,
                "A submission carries the carrier's reference — a 10DLC campaign id, a Meta WABA id. "
                + "It is what a support case quotes back at them, and without it the record says a "
                + "submission happened and cannot say which."
            );
        }

        state.State.Sender = sender with {
            Status = SenderRegistrationStatus.Pending,
            CarrierReference = carrierReference.Trim(),
            UpdatedAt = clock.UtcNow
        };

        await state.WriteStateAsync();

        return Result<SenderIdentity>.Success(state.State.Sender);
    }

    /// <inheritdoc />
    public async Task<Result<SenderIdentity>> RecordDecisionAsync(
        SenderRegistrationStatus status,
        ImmutableArray<string> countries,
        string note
    ) {
        if (state.State.Sender is not { } sender) {
            return NotFound();
        }

        // ⚠ Only the three that are carrier decisions. A caller who could write Draft or Pending
        // could walk a rejected sender back to looking un-submitted, which is how a sender that a
        // carrier refused ends up being tried again on a schedule.
        if (status is not (SenderRegistrationStatus.Approved
            or SenderRegistrationStatus.Rejected
            or SenderRegistrationStatus.Revoked)) {
            return Result<SenderIdentity>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{status} is not a carrier decision. Record Approved, Rejected or Revoked."
            );
        }

        if (status is SenderRegistrationStatus.Rejected or SenderRegistrationStatus.Revoked
            && string.IsNullOrWhiteSpace(note)) {
            return Result<SenderIdentity>.Failure(
                ErrorCode.InvalidRequestBody,
                "A rejection or a revocation carries the carrier's reason, verbatim. It is the only "
                + "thing the tenant can act on, and paraphrasing it loses the code their support "
                + "desk will ask for."
            );
        }

        state.State.Sender = sender with {
            Status = status,
            Countries = status == SenderRegistrationStatus.Approved ? Normalize(countries) : [],
            UpdatedAt = clock.UtcNow,
            Note = note
        };

        await state.WriteStateAsync();

        return Result<SenderIdentity>.Success(state.State.Sender);
    }

    /// <inheritdoc />
    public Task<Result<SenderIdentity>> GetAsync() =>
        Task.FromResult(state.State.Sender is { } sender ? Result<SenderIdentity>.Success(sender) : NotFound());

    /// <inheritdoc />
    public Task<Result<SenderIdentity>> CheckAsync(ChannelKind channel) {
        if (state.State.Sender is not { } sender) {
            return Task.FromResult(NotFound());
        }

        if (sender.Channel != channel) {
            return Task.FromResult(
                Result<SenderIdentity>.Failure(
                    ErrorCode.PolicyViolation,
                    $"Sender {senderId:D} is registered for {sender.Channel} and the send is on "
                    + $"{channel}. A registration is per channel because a carrier's approval is."
                )
            );
        }

        if (sender.Status != SenderRegistrationStatus.Approved) {
            return Task.FromResult(
                Result<SenderIdentity>.Failure(
                    ErrorCode.PolicyViolation,
                    $"Sender '{sender.Value}' is {sender.Status} with the carrier, not Approved, so "
                    + "nothing may be sent through it."
                    + (sender.Note.Length > 0 ? $" The carrier said: {sender.Note}" : string.Empty)
                    + " Moving it on is the tenant's obligation with our tooling — sender-id "
                    + "registration and 10DLC campaign approval belong to them, not to us "
                    + "(docs/plan/17 § The channel abstraction)."
                )
            );
        }

        // ⚠ An empty Countries is "no country is cleared", not "every country is". The other reading
        // is the one a reasonable person reaches for and is the one that puts an SMS into a
        // jurisdiction the sender id is illegal in — so it is refused here, loudly, rather than
        // being a default nobody notices.
        if (sender.Countries.IsDefaultOrEmpty) {
            return Task.FromResult(
                Result<SenderIdentity>.Failure(
                    ErrorCode.PolicyViolation,
                    $"Sender '{sender.Value}' is approved but is cleared for no country. An empty "
                    + "country list means nothing is cleared — it never means everywhere. Record the "
                    + "countries the carrier actually cleared on RecordDecisionAsync."
                )
            );
        }

        return Task.FromResult(Result<SenderIdentity>.Success(sender));
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>ISO 3166-1 alpha-2, upper case, deduplicated, blanks dropped.</summary>
    static ImmutableArray<string> Normalize(ImmutableArray<string> countries) =>
        countries.IsDefaultOrEmpty
            ? []
            : [
                .. countries
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
            ];

    Result<SenderIdentity> NotFound() =>
        Result<SenderIdentity>.Failure(ErrorCode.ResourceNotFound, $"There is no sender {senderId:D}.");
}

/// <summary>
///     <see cref="IProviderMessageIndexGrain" /> — Index, Hot, key
///     <c>res/{derived(serviceId, providerMessageId):N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>An unknown id activates this grain empty and <see cref="LookupAsync" /> deactivates it
///     on the way out.</b> Late, duplicate and orphaned webhooks are the normal case
///     (docs/plan/17 § The parts that are actually the work), so the activation this creates has to
///     cost as little as possible and must not sit in the collection cycle holding nothing.
/// </remarks>
public sealed class ProviderMessageIndexGrain(
    [PersistentState("provider-message-index", StorageTiers.Hot)]
    IPersistentState<ProviderMessageIndexState> state,
    IClock clock
)
    : Grain, IProviderMessageIndexGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = CommunicationGrainDecoder.TenantOf(this);
        _ = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> BindAsync(string idempotencyKey, DateTimeOffset expiresAt) {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "An index entry points at a message by its idempotency key; an empty one points at "
                + "nothing and would make every later receipt for this carrier id unroutable."
            );
        }

        state.State.IdempotencyKey = idempotencyKey;
        state.State.ExpiresAt = expiresAt;
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<string>> LookupAsync() {
        if (state.State.IdempotencyKey.Length == 0 || clock.UtcNow >= state.State.ExpiresAt) {
            DeactivateOnIdle();

            return Task.FromResult(
                Result<string>.Failure(
                    ErrorCode.ResourceNotFound,
                    "No message is bound to this provider message id, or the binding has aged past "
                    + "IMessageGrain.Retention. Drop the receipt — webhooks arrive late, twice, and "
                    + "for messages the platform has forgotten, and none of those is a fault."
                )
            );
        }

        return Task.FromResult(Result<string>.Success(state.State.IdempotencyKey));
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
