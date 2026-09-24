using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IDeviceAuthorizationGrain" /> — Entity, <b>Hot</b>, key <c>device/{digest}</c>,
///     qualified by the platform tenant.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One turn per answer, and that is what makes the codes one-time.</b> The grain is
///         single-threaded, so two people typing the same user code cannot both approve it — the
///         first <see cref="DecideAsync" /> moves the status off <see cref="DeviceAuthorizationStatus.Pending" />
///         and the second finds it answered — and two polls that race the redemption cannot both
///         collect tokens: <see cref="RedeemAsync" /> records the token session in the same turn
///         that spends the device code, exactly as <see cref="AuthorizationCodeGrain" /> does for an
///         authorization code.
///     </para>
///     <para>
///         ⚠ <b>A guess changes nothing.</b> <see cref="PollAsync" /> with a secret that does not
///         match answers <see cref="DevicePollOutcome.Unknown" /> before it reads or writes the
///         interval, so somebody who learned a user code — it is on the person's screen — and built
///         device codes around it cannot slow the real device down, let alone collect its tokens.
///     </para>
///     <para>
///         The record clears itself through a reminder a minute past the codes' expiry, the way
///         <see cref="AuthorizationCodeGrain" /> clears a spent code; a stale record that outlived
///         its reminder reads as expired on the next call.
///     </para>
/// </remarks>
public sealed class DeviceAuthorizationGrain(
    [PersistentState("device", StorageTiers.Hot)]
    IPersistentState<DeviceAuthorizationGrainState> state,
    IClock clock
)
    : Grain, IDeviceAuthorizationGrain, IRemindable {
    /// <summary>The reminder that clears the record once the codes have expired.</summary>
    public const string ForgetReminder = "device-forget";

    /// <summary>What RFC 8628 § 3.5 adds to the interval on every <c>slow_down</c>.</summary>
    public static TimeSpan SlowDownIncrement { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long past expiry the record is kept — the reminder service's one-minute floor, and
    ///     long enough that a late poll hears <c>expired_token</c> rather than <c>invalid_grant</c>.
    /// </summary>
    public static TimeSpan Grace { get; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = IdentityGrainKeys.Decode(this, GrainKeyKind.DeviceAuthorization);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> BeginAsync(DeviceAuthorizationRequest request, string deviceSecret) {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(deviceSecret) || string.IsNullOrEmpty(request.ClientId)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "A device authorization names a client and carries a device secret; one of them is empty."
            );
        }

        var now = clock.UtcNow;

        if (request.Lifetime <= TimeSpan.Zero) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "A device authorization needs a lifetime; this one has none."
            );
        }

        if (Begun && now < state.State.ExpiresAt + Grace) {
            // ⚠ Live or recently expired, a user code is not reused: a person who typed it a
            // moment ago and a device that polls with it late must not meet somebody else's flow.
            // The host draws a fresh code.
            return Result.Failure(
                ErrorCode.Conflict,
                "This user code is held by another device authorization. Draw another."
            );
        }

        state.State = new() {
            SecretDigest = CredentialDigest.Sha256(deviceSecret),
            ClientId = request.ClientId,
            Scopes = [.. request.Scopes],
            ExpiresAt = now + request.Lifetime,
            Interval = request.Interval,
            Status = DeviceAuthorizationStatus.Pending
        };

        // ⚠ The reminder before the write, for the reason SignUpGrain gives.
        var due = state.State.ExpiresAt + Grace - now;
        await this.RegisterOrUpdateReminder(ForgetReminder, due, due);
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<DeviceAuthorizationDescriptor>> DescribeAsync() =>
        Task.FromResult(
            Live(clock.UtcNow)
                ? Result<DeviceAuthorizationDescriptor>.Success(Descriptor())
                : NotFound<DeviceAuthorizationDescriptor>()
        );

    /// <inheritdoc />
    public async Task<Result<DeviceAuthorizationDescriptor>> DecideAsync(DeviceApproval? approval) {
        if (!Live(clock.UtcNow)) {
            return NotFound<DeviceAuthorizationDescriptor>();
        }

        if (state.State.Status != DeviceAuthorizationStatus.Pending) {
            return Result<DeviceAuthorizationDescriptor>.Failure(
                ErrorCode.Conflict,
                "This code has already been used. Start the sign-in again on the device."
            );
        }

        if (approval is not null && (approval.TenantId == Guid.Empty || approval.UserId == Guid.Empty)) {
            return Result<DeviceAuthorizationDescriptor>.Failure(
                ErrorCode.InvalidRequestBody,
                "An approval names the tenant and the person who gave it; one of them is empty."
            );
        }

        state.State.Status = approval is null ? DeviceAuthorizationStatus.Denied : DeviceAuthorizationStatus.Approved;
        state.State.Approval = approval;

        await state.WriteStateAsync();

        return Result<DeviceAuthorizationDescriptor>.Success(Descriptor());
    }

    /// <inheritdoc />
    public async Task<Result<DevicePoll>> PollAsync(string deviceSecret) {
        if (!Matches(deviceSecret)) {
            return Result<DevicePoll>.Success(new(DevicePollOutcome.Unknown, TimeSpan.Zero, string.Empty, [], null));
        }

        var now = clock.UtcNow;

        if (now >= state.State.ExpiresAt) {
            return Answer(DevicePollOutcome.Expired);
        }

        switch (state.State.Status) {
            case DeviceAuthorizationStatus.Approved:
                return Answer(DevicePollOutcome.Approved, state.State.Approval);
            case DeviceAuthorizationStatus.Denied:
                return Answer(DevicePollOutcome.Denied);
            case DeviceAuthorizationStatus.Redeemed:
                // ⚠ With the approval, so the token endpoint carries a second presentation of a spent
                // device code through to RedeemAsync — which is where the replay revokes the session
                // the first redemption opened. A poll that stopped at "redeemed" would refuse the
                // replay and leave the leaked code's first session running.
                return Answer(DevicePollOutcome.Redeemed, state.State.Approval);
        }

        // ⚠ Pending: the only state RFC 8628 § 3.5 lets slow_down answer, since it is "a variant of
        // authorization_pending". The first poll is measured from nothing — a device that was
        // told five seconds and polled after four has been impolite, not abusive, and the RFC's
        // remedy is for its second poll — and every later one from the last.
        var tooSoon = state.State.LastPolledAt is { } last && now - last < state.State.Interval;

        state.State.LastPolledAt = now;

        if (tooSoon) {
            // ⚠ "The interval MUST be increased by 5 seconds for this and all subsequent requests"
            // — state, not a response header, so the device stays slowed whichever silo answers.
            state.State.Interval += SlowDownIncrement;
        }

        await state.WriteStateAsync();

        return Answer(tooSoon ? DevicePollOutcome.SlowDown : DevicePollOutcome.Pending);
    }

    /// <inheritdoc />
    public async Task<Result<DeviceRedemption>> RedeemAsync(string deviceSecret, Guid tokenSessionId) {
        if (tokenSessionId == Guid.Empty) {
            return Result<DeviceRedemption>.Failure(
                ErrorCode.InvalidRequestBody,
                "A device code cannot be redeemed by an empty token session id: the record exists to "
                + "name the session a second redemption must revoke."
            );
        }

        if (!Matches(deviceSecret) || !Live(clock.UtcNow) || state.State.Approval is not { } approval) {
            return Result<DeviceRedemption>.Failure(
                ErrorCode.AuthorizationFailed,
                "device-code-not-approved"
            );
        }

        if (state.State.Status == DeviceAuthorizationStatus.Redeemed) {
            return Result<DeviceRedemption>.Success(new(false, state.State.TokenSessionId, approval, state.State.Scopes));
        }

        if (state.State.Status != DeviceAuthorizationStatus.Approved) {
            return Result<DeviceRedemption>.Failure(ErrorCode.AuthorizationFailed, "device-code-not-approved");
        }

        state.State.Status = DeviceAuthorizationStatus.Redeemed;
        state.State.TokenSessionId = tokenSessionId;

        await state.WriteStateAsync();

        return Result<DeviceRedemption>.Success(new(true, tokenSessionId, approval, state.State.Scopes));
    }

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ForgetReminder, StringComparison.Ordinal)) {
            return;
        }

        if (!Begun || clock.UtcNow >= state.State.ExpiresAt + Grace) {
            await ForgetAsync();
            DeactivateOnIdle();
        }
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();

        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    bool Begun => state.State.SecretDigest.Length > 0;

    bool Live(DateTimeOffset now) => Begun && now < state.State.ExpiresAt;

    /// <summary>Whether <paramref name="deviceSecret" /> is this authorization's, in constant time.</summary>
    bool Matches(string? deviceSecret) =>
        Begun
        && !string.IsNullOrEmpty(deviceSecret)
        && CredentialDigest.FixedTimeEquals(CredentialDigest.Sha256(deviceSecret), state.State.SecretDigest);

    Result<DevicePoll> Answer(DevicePollOutcome outcome, DeviceApproval? approval = null) =>
        Result<DevicePoll>.Success(
            new(outcome, state.State.Interval, state.State.ClientId, [.. state.State.Scopes], approval)
        );

    DeviceAuthorizationDescriptor Descriptor() =>
        new() {
            ClientId = state.State.ClientId,
            Scopes = [.. state.State.Scopes],
            ExpiresAt = state.State.ExpiresAt,
            Status = state.State.Status
        };

    static Result<T> NotFound<T>() where T : notnull =>
        Result<T>.Failure(
            ErrorCode.ResourceNotFound,
            "No device sign-in is waiting for that code. Check it, or start again on the device."
        );

    async Task ForgetAsync() {
        await state.ClearStateAsync();
        state.State = new();

        var reminder = await this.GetReminder(ForgetReminder);
        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }
    }
}
