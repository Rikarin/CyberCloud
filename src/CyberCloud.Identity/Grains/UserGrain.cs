using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IUserGrain" /> — Entity, Durable, key <c>user/{userId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every credential check happens inside this grain and nothing hands a hash out.</b>
///         The verify methods take a candidate and answer a Boolean, so the constant-time comparison
///         and the Argon2id parameters live in one place. A <c>GetPasswordHashAsync</c> would put a
///         credential on the wire between silos and would make the comparison the caller's problem.
///     </para>
///     <para>
///         ⚠ <b>This grain is Durable and is on <c>durable-grains.txt</c>.</b> A user's credentials
///         cannot be rebuilt from anything — losing them means every affected person is locked out of
///         an account they still own, with no recovery that does not go through support.
///     </para>
/// </remarks>
public sealed class UserGrain(
    [PersistentState("user", StorageTiers.Durable)] IPersistentState<UserGrainState> state,
    IPasswordHasher hasher,
    IGrainFactory grains,
    IClock clock
)
    : Grain, IUserGrain {
    Guid userId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        userId = IdentityGrainKeys.Decode(this, GrainKeyKind.User).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<UserProfile>> CreateAsync(string email, string displayName, UserStatus status) {
        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out var invalid)) {
            return Result<UserProfile>.Failure(invalid);
        }

        var address = normalized.GetValueOrThrow();

        if (state.State.CreatedAt != default) {
            // ⚠ Idempotent for the same address, which is what makes sign-up re-drivable after a silo
            // loss between the email-index claim and this call. A different address is a conflict
            // rather than a rename: renaming has to move the index claim, and doing that silently
            // here would leave the old claim held forever.
            return CredentialDigest.FixedTimeEquals(state.State.Email, address)
                ? Result<UserProfile>.Success(Profile())
                : Result<UserProfile>.Failure(
                    ErrorCode.Conflict,
                    $"User {userId:D} already exists with a different address. Changing it is "
                    + "ChangeEmailAsync, which the caller must pair with moving the email index claim."
                );
        }

        state.State.Email = address;
        state.State.DisplayName = displayName ?? string.Empty;
        state.State.Status = status;
        state.State.CreatedAt = clock.UtcNow;

        await state.WriteStateAsync();
        return Result<UserProfile>.Success(Profile());
    }

    /// <inheritdoc />
    public Task<Result<UserProfile>> GetAsync() =>
        Task.FromResult(Exists() ? Result<UserProfile>.Success(Profile()) : NotFound<UserProfile>());

    /// <inheritdoc />
    public async Task<Result<UserProfile>> SetStatusAsync(UserStatus status) {
        if (!Exists()) {
            return NotFound<UserProfile>();
        }

        state.State.Status = status;

        if (status is UserStatus.Suspended or UserStatus.Deprovisioned) {
            // ⚠ Before the write returns, not after. An administrator who suspends an account and is
            // told it worked has been told the account cannot act; a session that outlived the call
            // would make that false for up to the refresh lifetime.
            await RevokeEverySessionAsync(RevocationReason.AdminAction);
        }

        if (status == UserStatus.Deprovisioned) {
            state.State.PasswordHash = string.Empty;
            state.State.Passkeys.Clear();
            state.State.RecoveryCodeHashes.Clear();
            state.State.Totp = null;
            state.State.SpentTotpCounters.Clear();
        }

        await state.WriteStateAsync();
        return Result<UserProfile>.Success(Profile());
    }

    /// <inheritdoc />
    public async Task<Result<UserProfile>> ChangeEmailAsync(string email) {
        if (!Exists()) {
            return NotFound<UserProfile>();
        }

        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out var invalid)) {
            return Result<UserProfile>.Failure(invalid);
        }

        state.State.Email = normalized.GetValueOrThrow();
        await state.WriteStateAsync();

        return Result<UserProfile>.Success(Profile());
    }

    // ── Credentials ────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> SetPasswordAsync(string candidate) {
        if (!Exists()) {
            return NotFound();
        }

        if (string.IsNullOrEmpty(candidate)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "A password is required. To remove a password, deprovision the credential rather "
                + "than setting an empty one — an empty stored hash is indistinguishable from 'no "
                + "password enrolled' and would make the two paths behave differently."
            );
        }

        state.State.PasswordHash = hasher.Hash(candidate);

        // docs/plan/11 § Sessions and revocation lists "password change" among the things that
        // invalidate a refresh chain. A password change that left a stolen session alive would be
        // the one recovery action a user takes after a compromise, doing nothing about it.
        await RevokeEverySessionAsync(RevocationReason.CredentialChange);
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<bool>> VerifyPasswordAsync(string candidate) {
        // ⚠ NO EARLY RETURN FOR "NO PASSWORD ENROLLED". docs/plan/11 § Credentials wants sign-in to
        // take the same time whether or not the account can sign in at all, and a user with only a
        // passkey is exactly the case a probe would use to tell accounts apart. Verifying against the
        // hasher's dummy costs what verifying a real hash costs.
        var stored = state.State.PasswordHash.Length > 0 ? state.State.PasswordHash : hasher.DummyHash;
        var verified = hasher.Verify(candidate ?? string.Empty, stored);

        return Task.FromResult(
            Result<bool>.Success(verified && state.State.PasswordHash.Length > 0 && CanAuthenticate())
        );
    }

    /// <inheritdoc />
    public async Task<Result<UserProfile>> AddPasskeyAsync(PasskeyCredential credential) {
        ArgumentNullException.ThrowIfNull(credential);

        if (!Exists()) {
            return NotFound<UserProfile>();
        }

        if (state.State.Passkeys.Exists(x =>
                string.Equals(x.CredentialId, credential.CredentialId, StringComparison.Ordinal))) {
            return Result<UserProfile>.Failure(
                ErrorCode.Conflict,
                "That credential is already enrolled on this account."
            );
        }

        state.State.Passkeys.Add(credential with { CreatedAt = clock.UtcNow, LastUsedAt = clock.UtcNow });
        await state.WriteStateAsync();

        return Result<UserProfile>.Success(Profile());
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<PasskeyCredential>>> ListPasskeysAsync() =>
        Task.FromResult(Result<IReadOnlyList<PasskeyCredential>>.Success([.. state.State.Passkeys]));

    /// <inheritdoc />
    public async Task<Result<bool>> RecordPasskeyAssertionAsync(string credentialId, uint signCount) {
        var index = state.State.Passkeys.FindIndex(x =>
            string.Equals(x.CredentialId, credentialId, StringComparison.Ordinal));

        if (index < 0 || !CanAuthenticate()) {
            return Result<bool>.Success(false);
        }

        var existing = state.State.Passkeys[index];

        // ⚠ "Reject a DECREASE from a non-zero counter", not "require an increase". A large number of
        // authenticators — including every one that stores no per-credential state — report a
        // constant zero, which is legal in the WebAuthn spec. Requiring an increase would reject
        // those on the second sign-in and look like a broken key rather than a policy choice.
        if (existing.SignCount != 0 && signCount != 0 && signCount <= existing.SignCount) {
            return Result<bool>.Success(false);
        }

        state.State.Passkeys[index] = existing with { SignCount = signCount, LastUsedAt = clock.UtcNow };
        await state.WriteStateAsync();

        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<UserProfile>> RemovePasskeyAsync(string credentialId) {
        if (!Exists()) {
            return NotFound<UserProfile>();
        }

        state.State.Passkeys.RemoveAll(x => string.Equals(x.CredentialId, credentialId, StringComparison.Ordinal));
        await state.WriteStateAsync();

        return Result<UserProfile>.Success(Profile());
    }

    /// <inheritdoc />
    public async Task<Result<UserProfile>> EnrollTotpAsync(TotpEnrollment enrollment) {
        ArgumentNullException.ThrowIfNull(enrollment);

        if (!Exists()) {
            return NotFound<UserProfile>();
        }

        if (enrollment.SecretRef.IsEmpty) {
            return Result<UserProfile>.Failure(
                ErrorCode.InvalidRequestBody,
                "A TOTP enrolment must carry a vault handle for the shared secret. docs/plan/11 "
                + "§ Credentials: the secret is stored as a SecretRef, never in grain state."
            );
        }

        state.State.Totp = enrollment with { ConfirmedAt = clock.UtcNow };
        state.State.SpentTotpCounters.Clear();

        await state.WriteStateAsync();
        return Result<UserProfile>.Success(Profile());
    }

    /// <inheritdoc />
    public Task<Result<TotpEnrollment>> GetTotpAsync() =>
        Task.FromResult(
            state.State.Totp is { } totp
                ? Result<TotpEnrollment>.Success(totp)
                : Result<TotpEnrollment>.Failure(
                    ErrorCode.ResourceNotFound,
                    "No authenticator app is enrolled on this account."
                )
        );

    /// <inheritdoc />
    public async Task<Result<bool>> ClaimTotpCounterAsync(long counter) {
        if (state.State.Totp is null || !CanAuthenticate()) {
            return Result<bool>.Success(false);
        }

        if (state.State.SpentTotpCounters.Contains(counter)) {
            // The code was valid and is already spent. That is a replay — docs/plan/11 § Credentials.
            return Result<bool>.Success(false);
        }

        state.State.SpentTotpCounters.Add(counter);

        // ⚠ Pruned, because an unbounded list on a durable grain is a row that grows until the write
        // fails. Only counters within the drift window of *now* can ever be presented again, so
        // anything further back can never be replayed and keeping it buys nothing. The window is
        // widened by one step beyond the verifier's so a claim that races the step boundary is still
        // remembered.
        var floor = TotpAuthenticator.CounterFor(clock.UtcNow) - (TotpParameters.DriftSteps + 1);
        state.State.SpentTotpCounters.RemoveAll(x => x < floor);

        await state.WriteStateAsync();
        return Result<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<Result<RecoveryCodeBatch>> GenerateRecoveryCodesAsync() {
        if (!Exists()) {
            return NotFound<RecoveryCodeBatch>();
        }

        var codes = RecoveryCodes.Generate();

        // ⚠ Replaces the previous batch rather than adding to it. docs/plan/11 § Credentials shows
        // them once; a user who regenerates because they think the old sheet leaked has to end up
        // with the old sheet not working, or the regeneration is theatre.
        state.State.RecoveryCodeHashes = [.. codes.Select(RecoveryCodes.Hash)];
        await state.WriteStateAsync();

        return Result<RecoveryCodeBatch>.Success(new() { Codes = [.. codes], CreatedAt = clock.UtcNow });
    }

    /// <inheritdoc />
    public async Task<Result<bool>> RedeemRecoveryCodeAsync(string candidate) {
        if (!CanAuthenticate()) {
            return Result<bool>.Success(false);
        }

        var digest = RecoveryCodes.Hash(candidate ?? string.Empty);

        // ⚠ Every stored hash is compared even after a match, and each comparison is constant-time.
        // A `FindIndex` that stopped early would make the response time a function of the code's
        // position in the list, and the list is in generation order — so a fast rejection would say
        // "your first code is still unburnt".
        var matched = -1;
        for (var i = 0; i < state.State.RecoveryCodeHashes.Count; i++) {
            if (CredentialDigest.FixedTimeEquals(state.State.RecoveryCodeHashes[i], digest) && matched < 0) {
                matched = i;
            }
        }

        if (matched < 0) {
            return Result<bool>.Success(false);
        }

        state.State.RecoveryCodeHashes.RemoveAt(matched);
        await state.WriteStateAsync();

        return Result<bool>.Success(true);
    }

    // ── Sessions ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     How many session ids the user remembers.
    /// </summary>
    /// <remarks>
    ///     ⚠ Bounded because the session grain deliberately does not call back to prune this list —
    ///     see <c>SessionGrain.RevokeInternalAsync</c>, where that callback is a grain cycle and a
    ///     30-second timeout. Without a cap, a user who signs in from a script every minute would
    ///     grow a durable row until the write failed. The oldest go first; a dropped id costs a
    ///     session that "sign out everywhere" will not reach, which is why the cap is generous
    ///     relative to the number of devices a person actually uses.
    /// </remarks>
    public const int MaxTrackedSessions = 200;

    /// <inheritdoc />
    public async Task<Result> TrackSessionAsync(Guid sessionId) {
        if (!state.State.Sessions.Contains(sessionId)) {
            state.State.Sessions.Add(sessionId);

            if (state.State.Sessions.Count > MaxTrackedSessions) {
                state.State.Sessions.RemoveRange(0, state.State.Sessions.Count - MaxTrackedSessions);
            }

            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Guid>>> ListSessionsAsync() =>
        Task.FromResult(Result<IReadOnlyList<Guid>>.Success([.. state.State.Sessions]));

    /// <inheritdoc />
    public async Task<Result> ForgetSessionAsync(Guid sessionId) {
        if (state.State.Sessions.Remove(sessionId)) {
            await state.WriteStateAsync();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    async Task RevokeEverySessionAsync(RevocationReason reason) {
        var tenant = grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

        foreach (var sessionId in state.State.Sessions) {
            await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId)).RevokeAsync(reason);
        }

        state.State.Sessions.Clear();
    }

    bool Exists() => state.State.CreatedAt != default;

    /// <summary>Whether the account is in a state where any credential may succeed.</summary>
    bool CanAuthenticate() => state.State.Status is UserStatus.Active;

    UserProfile Profile() =>
        new() {
            UserId = userId,
            TenantId = tenantId,
            Email = state.State.Email,
            DisplayName = state.State.DisplayName,
            Status = state.State.Status,
            CreatedAt = state.State.CreatedAt,
            EnrolledCredentials = [.. Enrolled()],
            RemainingRecoveryCodes = state.State.RecoveryCodeHashes.Count
        };

    IEnumerable<CredentialKind> Enrolled() {
        // Ordered by CredentialKind, which is ordered by preference — docs/plan/11 § Credentials
        // makes passkeys the default offered credential rather than an upsell, and the enrolment
        // page reads this order.
        if (state.State.Passkeys.Count > 0) {
            yield return CredentialKind.Passkey;
        }

        if (state.State.PasswordHash.Length > 0) {
            yield return CredentialKind.Password;
        }

        if (state.State.Totp is not null) {
            yield return CredentialKind.Totp;
        }

        if (state.State.RecoveryCodeHashes.Count > 0) {
            yield return CredentialKind.RecoveryCode;
        }
    }

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"User {userId:D} does not exist.");

    Result NotFound() =>
        Result.Failure(ErrorCode.ResourceNotFound, $"User {userId:D} does not exist.");
}
