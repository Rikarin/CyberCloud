using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="ISignUpGrain" /> — Entity, <b>Hot</b>, key <c>signup/{signupId:N}</c>, qualified by
///     the platform tenant.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This is <c>UserGrain.IssueOtpAsync</c> and <c>UserGrain.RedeemOtpAsync</c> for a user
///             who does not exist yet
///         </b>, and every rule those two methods carry is carried here for the
///         same reason: the challenge is written before the send, a retry inside
///         <see cref="OtpPolicy.ResendCooldown" /> redelivers the same code, the per-window issue cap
///         is charged for a resend and not for a retry, the comparison is constant-time over keyed
///         digests, and a correct answer is accepted at most once because the activation is
///         single-threaded. <see cref="OtpPolicy" /> is where those four properties are argued;
///         <c>SignUpGrainTests</c> asserts them against this grain.
///     </para>
///     <para>
///         ⚠ <b>Nothing is created here.</b> The tenant, the user, the credential, the subscription
///         and the resource group are created by the identity host's orchestrator, which records
///         each through <see cref="RecordStepAsync" /> once it is done. This grain is the memory a
///         retried <c>complete</c> resumes from, and the second gate in front of every create: a
///         step cannot be recorded for an address nobody has proven.
///     </para>
///     <para>
///         ⚠ <b>The reminder is the expiry, and the expiry is a clear rather than a flag.</b>
///         <see cref="SignUpPolicy.Lifetime" /> after <see cref="BeginAsync" /> the reminder fires,
///         the state is cleared and the activation is dropped, so an abandoned sign-up costs the hot
///         tier nothing after fifteen minutes. Before then every method also checks the clock, so a
///         sign-up whose reminder is late still answers as expired.
///     </para>
/// </remarks>
public sealed class SignUpGrain(
    [PersistentState("signup", StorageTiers.Hot)]
    IPersistentState<SignUpGrainState> state,
    OtpCodeProtector otpCodes,
    IOtpDeliverySeam otpDelivery,
    IClock clock,
    ILogger<SignUpGrain> logger
)
    : Grain, ISignUpGrain, IRemindable {
    /// <summary>The reminder that clears an abandoned sign-up.</summary>
    public const string ExpiryReminder = "signup-expiry";

    /// <summary>
    ///     The plaintext of the outstanding code, for <see cref="OtpPolicy.ResendCooldown" /> only.
    ///     ⚠ A field and not state — <c>UserGrain</c> carries the argument.
    /// </summary>
    string? liveCode;

    Guid signupId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        signupId = IdentityGrainKeys.Decode(this, GrainKeyKind.SignUp).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result> BeginAsync(string email) {
        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out var invalid)) {
            return Result.Failure(invalid);
        }

        var address = normalized.GetValueOrThrow();
        var now = clock.UtcNow;

        if (Expired(now)) {
            await ForgetAsync();
        }

        if (state.State.CompletedSteps.Contains(SignUpStep.Completed)) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Sign-up {signupId:D} has completed. Its tenant exists; sign in to it instead."
            );
        }

        if (state.State.Email.Length == 0) {
            // ⚠ The three ids are minted ONCE, here, and never again for this sign-up. A retried
            // completion re-drives the same tenant, user and subscription — TenantCreateRequest's
            // remarks say a platform-minted id would make every retry a new tenant.
            state.State.Email = address;
            state.State.TenantId = Guid.NewGuid();
            state.State.UserId = Guid.NewGuid();
            state.State.SubscriptionId = Guid.NewGuid();
            state.State.StartedAt = now;
            state.State.ExpiresAt = now + SignUpPolicy.Lifetime;

            // The reminder is registered before the first write, so a crash between the two leaves
            // a reminder for a sign-up that never began — which fires, finds nothing, and unregisters
            // itself — rather than a sign-up nothing will ever clear.
            await this.RegisterOrUpdateReminder(ExpiryReminder, SignUpPolicy.Lifetime, SignUpPolicy.Lifetime);

            IdentityLog.SignUpBegun(logger, signupId, state.State.TenantId, state.State.UserId);
        } else if (!CredentialDigest.FixedTimeEquals(state.State.Email, address)) {
            if (state.State.Verified) {
                return Result.Failure(
                    ErrorCode.Conflict,
                    $"Sign-up {signupId:D} has already proven a different address. Changing the "
                    + "address after verification would enrol an address nobody has proven; start a "
                    + "new sign-up instead."
                );
            }

            // A different address before verification is a person correcting a typo. Nothing has
            // been created, so the ids stay and only the challenge restarts — with the same ids, so
            // a completion still re-drives one tenant.
            state.State.Email = address;
            state.State.Digest = string.Empty;
            state.State.Attempts = 0;
            liveCode = null;
        }

        // ⚠ Pruned before it is counted, not after — UserGrain.IssueOtpAsync.
        state.State.OtpIssuedAt.RemoveAll(x => x <= now - OtpPolicy.IssueWindow);

        var retried = state.State.Digest.Length > 0
            && state.State.ChallengeExpiresAt > now
            && state.State.ChallengeIssuedAt > now - OtpPolicy.ResendCooldown
            && liveCode is not null;

        if (!retried && state.State.OtpIssuedAt.Count >= OtpPolicy.MaxIssuesPerWindow) {
            await state.WriteStateAsync();

            return Result.Failure(
                ErrorCode.QuotaExceeded,
                $"Sign-up {signupId:D} has been sent {OtpPolicy.MaxIssuesPerWindow} codes inside "
                + $"{OtpPolicy.IssueWindow.TotalMinutes:F0} minutes, which is the per-sign-up cap — "
                + "OtpPolicy, property 3."
            );
        }

        var code = retried ? liveCode! : OtpCodeProtector.Generate();

        if (!retried) {
            state.State.Digest = Digest(code);
            state.State.ChallengeIssuedAt = now;
            state.State.ChallengeExpiresAt = now + OtpPolicy.Lifetime;
            state.State.Attempts = 0;
            state.State.OtpIssuedAt.Add(now);
            liveCode = code;
        }

        // ⚠ Before the send, always — UserGrain.IssueOtpAsync says which failure each order leaves.
        await state.WriteStateAsync();

        var delivered = await otpDelivery.DeliverAsync(
            new() {
                TenantId = tenantId,
                UserId = state.State.UserId,
                Purpose = OtpPurpose.Enrolment,
                Kind = CredentialKind.EmailOtp,
                Destination = address,
                Code = code
            }
        );

        if (delivered.TryGetError(out var undelivered)) {
            IdentityLog.OtpNotSent(logger, tenantId, state.State.UserId, undelivered.Message);
            return Result.Failure(undelivered);
        }

        IdentityLog.OtpIssued(logger, tenantId, state.State.UserId, OtpPurpose.Enrolment, CredentialKind.EmailOtp);
        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> VerifyAsync(string candidate) {
        // ⚠ Computed before anything branches — UserGrain.RedeemOtpAsync.
        var offered = Digest(candidate ?? string.Empty);
        var now = clock.UtcNow;

        if (Expired(now)) {
            await ForgetAsync();
            return Result<bool>.Success(false);
        }

        if (state.State.Email.Length == 0 || state.State.Digest.Length == 0) {
            return Result<bool>.Success(false);
        }

        if (state.State.ChallengeExpiresAt <= now || state.State.Attempts >= OtpPolicy.MaxAttempts) {
            // Removed rather than left to rot, so the next code is the only answerable one.
            state.State.Digest = string.Empty;
            liveCode = null;
            await state.WriteStateAsync();
            return Result<bool>.Success(false);
        }

        state.State.Attempts++;

        // ⚠ CONSTANT-TIME, over two fixed-length keyed digests — UserGrain.RedeemOtpAsync.
        var matched = CredentialDigest.FixedTimeEquals(state.State.Digest, offered);

        if (matched) {
            // ⚠ THE SINGLE-USE GUARANTEE: the challenge is gone before this write returns, and the
            // grain is single-threaded, so a second presentation of the same code finds nothing.
            state.State.Digest = string.Empty;
            state.State.Verified = true;
            liveCode = null;
        }

        await state.WriteStateAsync();

        return Result<bool>.Success(matched);
    }

    /// <inheritdoc />
    public async Task<Result<SignUpDescriptor>> GetAsync() {
        if (Expired(clock.UtcNow)) {
            await ForgetAsync();
        }

        return state.State.Email.Length == 0
            ? NotFound<SignUpDescriptor>()
            : Result<SignUpDescriptor>.Success(Descriptor());
    }

    /// <inheritdoc />
    public async Task<Result> RecordStepAsync(SignUpStep completed) {
        if (!Enum.IsDefined(completed)) {
            return Result.Failure(ErrorCode.InvalidRequestBody, $"'{completed}' is not a sign-up step.");
        }

        var gate = await GateAsync();
        if (gate.TryGetError(out var refused)) {
            return Result.Failure(refused);
        }

        if (state.State.CompletedSteps.Contains(completed)) {
            // Idempotent: the orchestrator may record a step it already skipped, and a second
            // record of one step must not be a second entry.
            return Result.Success;
        }

        state.State.CompletedSteps.Add(completed);
        await state.WriteStateAsync();

        IdentityLog.SignUpStepCompleted(logger, signupId, state.State.TenantId, completed);
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result> CompleteAsync() => RecordStepAsync(SignUpStep.Completed);

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ExpiryReminder, StringComparison.Ordinal)) {
            return;
        }

        if (state.State.Email.Length == 0 || Expired(clock.UtcNow)) {
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

    /// <summary>
    ///     The checks every create step shares: the sign-up exists, has not expired, has proven its
    ///     address and has not completed.
    /// </summary>
    async Task<Result> GateAsync() {
        if (Expired(clock.UtcNow)) {
            await ForgetAsync();
        }

        if (state.State.Email.Length == 0) {
            return NotFound<SignUpDescriptor>().ToResult();
        }

        if (!state.State.Verified) {
            // ⚠ The second gate in front of every create. The host checks the same flag; this one is
            // the one a host bug cannot get past.
            return Result.Failure(
                ErrorCode.AuthorizationFailed,
                $"Sign-up {signupId:D} has not proven its address, so nothing may be created for it."
            );
        }

        if (state.State.CompletedSteps.Contains(SignUpStep.Completed)) {
            return Result.Failure(
                ErrorCode.Conflict,
                $"Sign-up {signupId:D} has completed and records no further steps."
            );
        }

        return Result.Success;
    }

    bool Expired(DateTimeOffset now) => state.State.Email.Length > 0 && now >= state.State.ExpiresAt;

    /// <summary>Clears the sign-up entirely and drops its reminder. What expiry means.</summary>
    async Task ForgetAsync() {
        liveCode = null;
        await state.ClearStateAsync();
        state.State = new();

        var reminder = await this.GetReminder(ExpiryReminder);
        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }
    }

    // ⚠ Bound to the sign-up id rather than to the pre-allocated user id: the user does not exist,
    // and a challenge is a fact about THIS sign-up. OtpCodeProtector.Digest says why the binding
    // is inside the MAC at all.
    string Digest(string code) => otpCodes.Digest(tenantId, signupId, OtpPurpose.Enrolment, code);

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"Sign-up {signupId:D} does not exist or has expired.");

    SignUpDescriptor Descriptor() =>
        new() {
            SignupId = signupId,
            Email = state.State.Email,
            TenantId = state.State.TenantId,
            UserId = state.State.UserId,
            SubscriptionId = state.State.SubscriptionId,
            Verified = state.State.Verified,
            CompletedSteps = [.. state.State.CompletedSteps],
            ExpiresAt = state.State.ExpiresAt
        };
}
