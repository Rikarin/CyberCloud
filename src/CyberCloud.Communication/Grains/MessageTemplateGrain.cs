using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Communication.Grains;

/// <summary>
///     <see cref="IMessageTemplateGrain" /> — Entity, Durable, key <c>res/{templateId:N}</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>There is no method here that reaches an existing version's body, and that absence is the
///     design.</b> A carrier approved a specific body; editing it in place invalidates the approval
///     without changing anything a reviewer would notice. See <see cref="IMessageTemplateGrain" />.
/// </remarks>
public sealed class MessageTemplateGrain(
    [PersistentState("message-template", StorageTiers.Durable)] IPersistentState<MessageTemplateState> state,
    IClock clock
)
    : Grain, IMessageTemplateGrain {
    Guid templateId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = CommunicationGrainDecoder.TenantOf(this);
        templateId = CommunicationGrainDecoder.ResourceOf(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<MessageTemplateVersion>> CreateAsync(Guid serviceId, string name, ChannelKind channel) {
        if (state.State.Created) {
            return Result<MessageTemplateVersion>.Success(
                state.State.Versions.Count > 0 ? state.State.Versions[^1] : Empty(channel)
            );
        }

        if (serviceId == Guid.Empty) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidResourceId,
                "A template belongs to a communication service."
            );
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidResourceName,
                "A template needs a name — it is what a send references."
            );
        }

        if (channel == ChannelKind.Unknown) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                "A template is written for a channel. A WhatsApp body is not an email body, and the "
                + "channel decides whether pre-approval is consulted at all."
            );
        }

        state.State.Created = true;
        state.State.ServiceId = serviceId;
        state.State.Name = name;
        state.State.Channel = channel;
        await state.WriteStateAsync();

        return Result<MessageTemplateVersion>.Success(Empty(channel));
    }

    /// <inheritdoc />
    public async Task<Result<MessageTemplateVersion>> AddVersionAsync(
        ImmutableArray<TemplateParameter> parameters,
        ImmutableArray<LocalizedBody> bodies
    ) {
        if (!state.State.Created) {
            return NotFound();
        }

        if (bodies.IsDefaultOrEmpty) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                "A template version needs at least one localized body. A version with none renders "
                + "to nothing, and the failure would appear at a carrier rather than here."
            );
        }

        foreach (var body in bodies) {
            if (string.IsNullOrWhiteSpace(body.Locale)) {
                return Result<MessageTemplateVersion>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "Every body names its locale — a BCP 47 tag such as 'en-US'. An unlabelled body "
                    + "cannot be chosen for a recipient and cannot be replaced by a better one."
                );
            }

            if (string.IsNullOrWhiteSpace(body.Body)) {
                return Result<MessageTemplateVersion>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"The '{body.Locale}' body is empty. Sending an empty message costs the same as "
                    + "sending a real one and tells the recipient nothing."
                );
            }
        }

        var declared = parameters.IsDefault ? [] : parameters;
        foreach (var parameter in declared) {
            if (string.IsNullOrWhiteSpace(parameter.Name)) {
                return Result<MessageTemplateVersion>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "Every template parameter needs a name — it is what a send's argument matches "
                    + "and what a missing-parameter refusal names."
                );
            }
        }

        var version = new MessageTemplateVersion {
            Version = state.State.Versions.Count + 1,
            Channel = state.State.Channel,
            Parameters = declared,
            Bodies = bodies,
            ProviderTemplateName = string.Empty,
            Approval = SenderRegistrationStatus.Draft,
            CreatedAt = clock.UtcNow
        };

        state.State.Versions.Add(version);
        await state.WriteStateAsync();

        return Result<MessageTemplateVersion>.Success(version);
    }

    /// <inheritdoc />
    public async Task<Result<MessageTemplateVersion>> RecordApprovalAsync(
        int version,
        SenderRegistrationStatus status,
        string providerTemplateName
    ) {
        if (!state.State.Created) {
            return NotFound();
        }

        // ⚠ Only the three that are carrier decisions. A caller who could write Draft or Pending
        // could walk a template back to a state that hides a rejection; a caller who could write
        // Unknown could erase one.
        if (status is not (SenderRegistrationStatus.Approved
            or SenderRegistrationStatus.Rejected
            or SenderRegistrationStatus.Revoked)) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{status} is not a carrier decision. Record Approved, Rejected or Revoked — the "
                + "platform reports what the carrier answered and does not answer for them "
                + "(docs/plan/17 § The channel abstraction)."
            );
        }

        if (status == SenderRegistrationStatus.Approved && string.IsNullOrWhiteSpace(providerTemplateName)) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.InvalidRequestBody,
                "An approval carries the carrier's own name for the template. Without it a "
                + "template-by-reference send — which is every business-initiated WhatsApp message — "
                + "has nothing to quote, and fails at the carrier with an error nobody can act on."
            );
        }

        var index = state.State.Versions.FindIndex(x => x.Version == version);
        if (index < 0) {
            return Result<MessageTemplateVersion>.Failure(
                ErrorCode.ResourceNotFound,
                $"Template {templateId:D} has no version {version}."
            );
        }

        state.State.Versions[index] = state.State.Versions[index] with {
            Approval = status,
            ProviderTemplateName = providerTemplateName
        };

        await state.WriteStateAsync();

        return Result<MessageTemplateVersion>.Success(state.State.Versions[index]);
    }

    /// <inheritdoc />
    public Task<Result<MessageTemplateVersion>> GetVersionAsync(int version) {
        if (!state.State.Created) {
            return Task.FromResult(NotFound());
        }

        if (state.State.Versions.Count == 0) {
            return Task.FromResult(
                Result<MessageTemplateVersion>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"Template '{state.State.Name}' has no versions yet, so there is nothing to send."
                )
            );
        }

        if (version > 0) {
            var named = state.State.Versions.FirstOrDefault(x => x.Version == version);

            return Task.FromResult(
                named is null
                    ? Result<MessageTemplateVersion>.Failure(
                        ErrorCode.ResourceNotFound,
                        $"Template '{state.State.Name}' has no version {version}."
                    )
                    : Result<MessageTemplateVersion>.Success(named)
            );
        }

        // ⚠ On a channel that pre-approves, "the newest" means the newest APPROVED one. Otherwise
        // adding a draft to a WhatsApp template would take production sending down between the
        // moment a tenant saved it and the moment Meta answered — which can be days.
        if (RequiresApproval(state.State.Channel)) {
            for (var i = state.State.Versions.Count - 1; i >= 0; i--) {
                if (state.State.Versions[i].Approval == SenderRegistrationStatus.Approved) {
                    return Task.FromResult(Result<MessageTemplateVersion>.Success(state.State.Versions[i]));
                }
            }

            return Task.FromResult(
                Result<MessageTemplateVersion>.Failure(
                    ErrorCode.PolicyViolation,
                    $"Template '{state.State.Name}' has no carrier-approved version, and "
                    + $"{state.State.Channel} requires pre-approved templates for business-initiated "
                    + "messages. Submitting it and getting a decision is the tenant's obligation "
                    + "with our tooling — docs/plan/17 § The channel abstraction — and the platform "
                    + "refuses here rather than letting the carrier reject it."
                )
            );
        }

        return Task.FromResult(Result<MessageTemplateVersion>.Success(state.State.Versions[^1]));
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<MessageTemplateVersion>>> ListVersionsAsync() =>
        Task.FromResult(Result<ImmutableArray<MessageTemplateVersion>>.Success([.. state.State.Versions]));

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Which channels will not accept a body from us at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ WhatsApp only, today. It is a method rather than a property on
    ///     <see cref="ChannelKind" /> because the answer is a carrier's policy rather than a fact
    ///     about the channel, and carriers change theirs.
    /// </remarks>
    internal static bool RequiresApproval(ChannelKind channel) => channel == ChannelKind.WhatsApp;

    MessageTemplateVersion Empty(ChannelKind channel) => new() { Version = 0, Channel = channel };

    Result<MessageTemplateVersion> NotFound() =>
        Result<MessageTemplateVersion>.Failure(
            ErrorCode.ResourceNotFound,
            $"There is no template {templateId:D}."
        );
}
